Imports System
Imports System.Collections.Generic
Imports System.Reflection
Imports System.Text
Imports Microsoft.VisualBasic
Imports Microsoft.VisualBasic.CompilerServices
Imports SolidWorks.Interop.sldworks
Imports SolidWorks.Interop.swconst
Imports SolidWorks.Interop.cosworks

    Public Class SWAutomation
        Private _swApp As SldWorks
        Private _model As ModelDoc2
        Private _log As Action(Of String)

        ' Persist across steps
        Private _cw As CosmosWorks
        Private _cwDoc As CWModelDoc
        Private _study As CWStudy

        ' Persist edges from SetupStudy for use in ExtractResultsAuto
        Private _idEdge As Edge = Nothing
        Private _odEdge As Edge = Nothing

        Public Sub New(ByVal logger As Action(Of String))
            _log = logger
        End Sub

        Private Sub Log(ByVal msg As String)
            If _log IsNot Nothing Then _log(msg)
        End Sub

        ' ──────────────────────────────────────────────────────────────
        ' STEP 1 — Connect
        ' ──────────────────────────────────────────────────────────────
        Public Function Connect() As Boolean
            Try
                Log("Connecting to SolidWorks...")
                _swApp = DirectCast(GetObject("", "SldWorks.Application"), SldWorks)
                If _swApp Is Nothing Then
                    Log("ERROR: Could not connect. Is SolidWorks running?")
                    Return False
                End If
                _swApp.Visible = True
                Log("Connected to SolidWorks " & _swApp.RevisionNumber())
                Return True
            Catch ex As System.Exception
                Log("ERROR: " & ex.Message)
                Return False
            End Try
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' STEP 2 — Create Part
        ' ──────────────────────────────────────────────────────────────
        Public Function CreatePart(ByVal profile As SpiderProfile) As Boolean
            If _swApp Is Nothing Then
                Log("ERROR: Not connected.")
                Return False
            End If
            ' Release all COM references before creating new part
            ' Prevents SW crash when user closes previous part manually
            Try
                _study = Nothing
                _cwDoc = Nothing
                _cw = Nothing
                _idEdge = Nothing
                _odEdge = Nothing
                ' Close any open docs except the SW window itself
                If _model IsNot Nothing Then
                    Try
                        _swApp.CloseDoc(_model.GetPathName())
                    Catch : End Try
                    _model = Nothing
                End If
                System.Runtime.InteropServices.Marshal.CleanupUnusedObjectsInCurrentContext()
                System.Threading.Thread.Sleep(500)  ' give SW time to fully close
            Catch : End Try
            Try
                Dim templatePath As String = _swApp.GetUserPreferenceStringValue( _
                    CInt(swUserPreferenceStringValue_e.swDefaultTemplatePart))
                If String.IsNullOrEmpty(templatePath) Then
                    templatePath = "C:\ProgramData\SolidWorks\SolidWorks 2014\templates\Part.prtdot"
                End If

                _model = DirectCast(_swApp.NewDocument(templatePath, 0, 0, 0), ModelDoc2)
                If _model Is Nothing Then
                    Log("ERROR: Failed to create part.")
                    Return False
                End If

                ' Select Front Plane, start sketch
                _model.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, False, 0, Nothing, 0)
                _model.SketchManager.InsertSketch(True)

                ' Centerline (Y-axis), meters
                Dim ax As Object = _model.SketchManager.CreateCenterLine(0, -0.1, 0, 0, 0.1, 0)
                If ax Is Nothing Then
                    Dim sl As SketchSegment = DirectCast( _
                        _model.SketchManager.CreateLine(0, -0.1, 0, 0, 0.1, 0), SketchSegment)
                    If sl IsNot Nothing Then sl.ConstructionGeometry = True
                End If

                Dim pts As List(Of ProfilePoint) = profile.GeneratePoints(30)
                Log(String.Format("Profile: {0} points", pts.Count))

                ' ── Bullet profile (ProfileType=12): three-arc bullet via sketch relations ──
                ' Construction recipe (authored from a successful manual GUI build):
                '   1. Rectangle (construction): (0,0)-(width, H_pp), H/V + fixed corners
                '   2. Arc 2: 3-point arc, seeds at (w/3, 2H/3), (2w/3, 2H/3), apex (w/2, ~H)
                '   3. Arc 1: 3-point arc, endpoints EXACTLY at (0,0) and Arc 2's start
                '   4. Arc 3: 3-point arc, endpoints EXACTLY at Arc 2's end and (w,0)
                '   5. Merge coincident sketch points (via ISketch.GetSketchPoints2 sweep).
                '      Connects Arc1.start⇌corner(0,0), Arc1.end⇌Arc2.start,
                '      Arc3.start⇌Arc2.end, Arc3.end⇌corner(w,0). ISketchSegment.
                '      GetStartPoint2() returns Double[] not SketchPoint so we can't get
                '      per-endpoint refs; GetSketchPoints2() returns typed refs for all
                '      points, which we then match pairwise by coordinate.
                '   6. Tangent relations: Arc2↔top, Arc1↔left, Arc1↔Arc2, Arc3↔right,
                '      Arc3↔Arc2. All via direct Select4 on SketchSegment refs.
                '   7. Dimensions: R2 on Arc 2, Cx from left line to Arc 2 center.
                '   8. Inner + outer lip lines.
                If profile.ProfileType = 12 Then
                    Log("── Bullet geometry (3-arc via sketch relations) ──")

                    Dim valErr As String = profile.ValidateBulletParams()
                    If valErr.Length > 0 Then
                        Log("ERROR: " & valErr)
                        Return False
                    End If

                    Dim wMM As Double = profile.BulletWidth
                    Dim hMM As Double = profile.H_pp
                    Dim r2MM As Double = profile.BulletR_Top
                    Dim cxMM As Double = profile.GetBulletCx()
                    Dim innerLipMM As Double = profile.InnerLipWidth
                    Dim outerLipMM As Double = profile.LipWidth + 5.0

                    Dim rOff As Double = profile.R_inner / 1000.0
                    Dim w As Double = wMM / 1000.0
                    Dim h As Double = hMM / 1000.0
                    Dim r2 As Double = r2MM / 1000.0
                    Dim cx As Double = cxMM / 1000.0
                    Dim innerLip As Double = innerLipMM / 1000.0
                    Dim outerLip As Double = outerLipMM / 1000.0

                    Log(String.Format("  width={0:F2}mm  H_pp={1:F2}mm  R2={2:F2}mm  Cx={3:F2}mm  InnerLip={4:F2}mm", _
                        wMM, hMM, r2MM, cxMM, innerLipMM))

                    ' ---- Construction rectangle ----
                    ' AddToDB left at default (False) so SW inference / auto-snap is
                    ' active during creation (mirrors GUI drawing behaviour).
                    Dim botLine As SketchSegment = Nothing
                    Dim rightLine As SketchSegment = Nothing
                    Dim topLine As SketchSegment = Nothing
                    Dim leftLine As SketchSegment = Nothing

                    Dim rectObj As Object
                    rectObj = _model.SketchManager.CreateLine(rOff, 0, 0, rOff + w, 0, 0)
                    If TypeOf rectObj Is SketchSegment Then botLine = DirectCast(rectObj, SketchSegment)
                    rectObj = _model.SketchManager.CreateLine(rOff + w, 0, 0, rOff + w, h, 0)
                    If TypeOf rectObj Is SketchSegment Then rightLine = DirectCast(rectObj, SketchSegment)
                    rectObj = _model.SketchManager.CreateLine(rOff + w, h, 0, rOff, h, 0)
                    If TypeOf rectObj Is SketchSegment Then topLine = DirectCast(rectObj, SketchSegment)
                    rectObj = _model.SketchManager.CreateLine(rOff, h, 0, rOff, 0, 0)
                    If TypeOf rectObj Is SketchSegment Then leftLine = DirectCast(rectObj, SketchSegment)

                    If botLine Is Nothing OrElse rightLine Is Nothing _
                       OrElse topLine Is Nothing OrElse leftLine Is Nothing Then
                        Log("  ERROR: rectangle line creation failed")
                        Return False
                    End If

                    For Each seg As SketchSegment In New SketchSegment() {botLine, rightLine, topLine, leftLine}
                        Try : seg.ConstructionGeometry = True : Catch : End Try
                    Next

                    ' H/V constraints via direct Select4 on segment refs
                    Try
                        _model.ClearSelection2(True)
                        botLine.Select4(False, Nothing)
                        _model.SketchAddConstraints("sgHORIZONTAL2D")
                        _model.ClearSelection2(True)
                        topLine.Select4(False, Nothing)
                        _model.SketchAddConstraints("sgHORIZONTAL2D")
                        _model.ClearSelection2(True)
                        leftLine.Select4(False, Nothing)
                        _model.SketchAddConstraints("sgVERTICAL2D")
                        _model.ClearSelection2(True)
                        rightLine.Select4(False, Nothing)
                        _model.SketchAddConstraints("sgVERTICAL2D")
                        _model.ClearSelection2(True)
                        Log("  rect H/V constraints applied")
                    Catch ex As Exception
                        Log("  rect H/V: " & ex.Message)
                    End Try

                    ' Fix bottom corners by coord-based SKETCHPOINT select (OK for
                    ' rectangle corners — the prior failure mode was arc endpoints).
                    Try
                        _model.ClearSelection2(True)
                        _model.Extension.SelectByID2("", "SKETCHPOINT", rOff, 0, 0, False, 0, Nothing, 0)
                        _model.SketchAddConstraints("sgFIXED")
                        _model.ClearSelection2(True)
                        _model.Extension.SelectByID2("", "SKETCHPOINT", rOff + w, 0, 0, False, 0, Nothing, 0)
                        _model.SketchAddConstraints("sgFIXED")
                        _model.ClearSelection2(True)
                        Log("  bottom corners fixed")
                    Catch ex As Exception
                        Log("  fix corners: " & ex.Message)
                    End Try

                    ' Rectangle width & height dimensions
                    Try
                        _model.ClearSelection2(True)
                        leftLine.Select4(False, Nothing)
                        Dim hDimObj As Object = _model.AddDimension2(rOff - 0.01, h / 2, 0)
                        If hDimObj IsNot Nothing Then
                            Try
                                Dim getDim As Object = hDimObj.GetType().InvokeMember("GetDimension2", _
                                    Reflection.BindingFlags.InvokeMethod, Nothing, hDimObj, New Object() {0})
                                If getDim IsNot Nothing Then
                                    getDim.GetType().InvokeMember("SetSystemValue3", _
                                        Reflection.BindingFlags.InvokeMethod, Nothing, getDim, _
                                        New Object() {h, 1, Nothing})
                                End If
                            Catch ex As Exception
                                Log("  height dim value: " & ex.Message)
                            End Try
                            Log(String.Format("  Rectangle height dim = {0:F2}mm", hMM))
                        End If
                        _model.ClearSelection2(True)

                        _model.ClearSelection2(True)
                        botLine.Select4(False, Nothing)
                        Dim wDimObj As Object = _model.AddDimension2(rOff + w / 2, -0.015, 0)
                        If wDimObj IsNot Nothing Then
                            Try
                                Dim getDim As Object = wDimObj.GetType().InvokeMember("GetDimension2", _
                                    Reflection.BindingFlags.InvokeMethod, Nothing, wDimObj, New Object() {0})
                                If getDim IsNot Nothing Then
                                    getDim.GetType().InvokeMember("SetSystemValue3", _
                                        Reflection.BindingFlags.InvokeMethod, Nothing, getDim, _
                                        New Object() {w, 1, Nothing})
                                End If
                            Catch ex As Exception
                                Log("  width dim value: " & ex.Message)
                            End Try
                            Log(String.Format("  Rectangle width dim = {0:F2}mm", wMM))
                        End If
                        _model.ClearSelection2(True)
                    Catch ex As Exception
                        Log("  rect dimensions: " & ex.Message)
                    End Try
                    Log("  Rectangle built (H/V + fixed + dimensioned)")

                    ' ---- Arc 2: 3-point arc, seed points per user recipe ----
                    ' Apex z = 0.98*h so apex sketch point is distinct from top line.
                    Dim seedT1x As Double = w / 3.0
                    Dim seedT1z As Double = 2.0 * h / 3.0
                    Dim seedT2x As Double = 2.0 * w / 3.0
                    Dim seedT2z As Double = 2.0 * h / 3.0
                    Dim seedMidX As Double = w / 2.0
                    Dim seedMidZ As Double = h * 0.98

                    Dim arc2Obj As Object = Nothing
                    Try
                        arc2Obj = _model.SketchManager.Create3PointArc( _
                            rOff + seedT1x, seedT1z, 0, _
                            rOff + seedT2x, seedT2z, 0, _
                            rOff + seedMidX, seedMidZ, 0)
                    Catch ex As Exception
                        Log("  Create3PointArc (Arc 2): " & ex.Message)
                    End Try
                    Dim arc2 As SketchSegment = Nothing
                    If arc2Obj IsNot Nothing AndAlso TypeOf arc2Obj Is SketchSegment Then
                        arc2 = DirectCast(arc2Obj, SketchSegment)
                    End If
                    If arc2 Is Nothing Then
                        Log("  ERROR: Arc 2 creation failed")
                        Return False
                    End If
                    Log("  Arc 2 created")

                    ' ---- Arc 1: 3-point arc from (0,0) to Arc 2 start ----
                    Dim arc1MidX As Double = seedT1x * 0.45
                    Dim arc1MidZ As Double = seedT1z * 0.55
                    Dim arc1Obj As Object = Nothing
                    Try
                        arc1Obj = _model.SketchManager.Create3PointArc( _
                            rOff, 0, 0, _
                            rOff + seedT1x, seedT1z, 0, _
                            rOff + arc1MidX, arc1MidZ, 0)
                    Catch ex As Exception
                        Log("  Create3PointArc (Arc 1): " & ex.Message)
                    End Try
                    Dim arc1 As SketchSegment = Nothing
                    If arc1Obj IsNot Nothing AndAlso TypeOf arc1Obj Is SketchSegment Then
                        arc1 = DirectCast(arc1Obj, SketchSegment)
                    End If
                    If arc1 Is Nothing Then
                        Log("  ERROR: Arc 1 creation failed")
                        Return False
                    End If
                    Log("  Arc 1 created")

                    ' ---- Arc 3: 3-point arc from Arc 2 end to (w,0) ----
                    Dim arc3MidX As Double = seedT2x + (w - seedT2x) * 0.55
                    Dim arc3MidZ As Double = seedT2z * 0.55
                    Dim arc3Obj As Object = Nothing
                    Try
                        arc3Obj = _model.SketchManager.Create3PointArc( _
                            rOff + seedT2x, seedT2z, 0, _
                            rOff + w, 0, 0, _
                            rOff + arc3MidX, arc3MidZ, 0)
                    Catch ex As Exception
                        Log("  Create3PointArc (Arc 3): " & ex.Message)
                    End Try
                    Dim arc3 As SketchSegment = Nothing
                    If arc3Obj IsNot Nothing AndAlso TypeOf arc3Obj Is SketchSegment Then
                        arc3 = DirectCast(arc3Obj, SketchSegment)
                    End If
                    If arc3 Is Nothing Then
                        Log("  ERROR: Arc 3 creation failed")
                        Return False
                    End If
                    Log("  Arc 3 created")

                    ' ---- Merge coincident sketch points ----
                    ' ISketchSegment.GetStartPoint2() returns Double[] (coords) not a
                    ' SketchPoint object. Instead enumerate ALL sketch points via
                    ' ISketch.GetSketchPoints2() (which DOES return typed SketchPoint
                    ' objects), then merge any pair within 0.02mm. This connects
                    ' Arc1.start⇌corner(0,0), Arc1.end⇌Arc2.start, Arc3.start⇌Arc2.end,
                    ' Arc3.end⇌corner(w,0) in one sweep.
                    Try
                        Dim activeSketch As Sketch = DirectCast(_model.SketchManager.ActiveSketch, Sketch)
                        Dim ptsObj As Object = activeSketch.GetSketchPoints2()
                        Dim arr As Array = TryCast(ptsObj, Array)
                        If arr Is Nothing Then
                            Log("  sketch points: GetSketchPoints2 did not return an array")
                        Else
                            Dim skPts As New List(Of SketchPoint)()
                            For i As Integer = 0 To arr.Length - 1
                                Dim sp As SketchPoint = TryCast(arr.GetValue(i), SketchPoint)
                                If sp IsNot Nothing Then skPts.Add(sp)
                            Next
                            Log(String.Format("  sketch points enumerated: {0}", skPts.Count))

                            ' Debug: log all point coordinates (mm) so we can see
                            ' which ones are actually coincident.
                            For i As Integer = 0 To skPts.Count - 1
                                Try
                                    Log(String.Format("    pt[{0}]: ({1:F4}, {2:F4}) mm", _
                                        i, (skPts(i).X - rOff) * 1000.0, skPts(i).Y * 1000.0))
                                Catch
                                End Try
                            Next

                            Dim tolM As Double = 0.0002  ' 0.2mm coincidence tolerance
                            Dim mergedCount As Integer = 0
                            Dim consumed(skPts.Count - 1) As Boolean
                            For i As Integer = 0 To skPts.Count - 1
                                If consumed(i) Then Continue For
                                Dim ix As Double = 0.0
                                Dim iy As Double = 0.0
                                Dim iOk As Boolean = False
                                Try
                                    ix = skPts(i).X
                                    iy = skPts(i).Y
                                    iOk = True
                                Catch
                                    consumed(i) = True
                                End Try
                                If Not iOk Then Continue For
                                For j As Integer = i + 1 To skPts.Count - 1
                                    If consumed(j) Then Continue For
                                    Dim jx As Double = 0.0
                                    Dim jy As Double = 0.0
                                    Dim jOk As Boolean = False
                                    Try
                                        jx = skPts(j).X
                                        jy = skPts(j).Y
                                        jOk = True
                                    Catch
                                        consumed(j) = True
                                    End Try
                                    If Not jOk Then Continue For
                                    Dim dx As Double = ix - jx
                                    Dim dy As Double = iy - jy
                                    Dim d As Double = Math.Sqrt(dx * dx + dy * dy)
                                    If d <= tolM Then
                                        Try
                                            _model.ClearSelection2(True)
                                            skPts(i).Select4(False, Nothing)
                                            skPts(j).Select4(True, Nothing)
                                            _model.SketchAddConstraints("sgMERGEPOINTS")
                                            _model.ClearSelection2(True)
                                            consumed(j) = True
                                            mergedCount += 1
                                            Log(String.Format("    merge pt[{0}]-pt[{1}] d={2:F4}mm", _
                                                i, j, d * 1000.0))
                                        Catch mex As Exception
                                            Log(String.Format("    merge pt[{0}]-pt[{1}] FAILED: {2}", _
                                                i, j, mex.Message))
                                        End Try
                                    ElseIf d <= 0.001 Then
                                        ' Near-miss (within 1mm) — useful for diagnosing
                                        Log(String.Format("    near pt[{0}]-pt[{1}] d={2:F4}mm (>tol)", _
                                            i, j, d * 1000.0))
                                    End If
                                Next
                            Next
                            Log(String.Format("  merged {0} coincident point pairs", mergedCount))
                        End If
                    Catch ex As Exception
                        Log("  merge coincident: " & ex.Message)
                    End Try

                    ' ══════════════════════════════════════════════════════════════
                    ' Topology connected: (0,0) — Arc1 — Arc2 — Arc3 — (w,0)
                    ' Now apply tangent relations and dimensions via direct Select4.
                    ' ══════════════════════════════════════════════════════════════

                    ' Tangent: Arc 2 ↔ top line
                    Try
                        _model.ClearSelection2(True)
                        arc2.Select4(False, Nothing)
                        topLine.Select4(True, Nothing)
                        _model.SketchAddConstraints("sgTANGENT")
                        _model.ClearSelection2(True)
                        Log("  tangent: Arc2 ↔ top")
                    Catch ex As Exception
                        Log("  tangent Arc2/top: " & ex.Message)
                    End Try

                    ' Tangent: Arc 1 ↔ left line
                    Try
                        _model.ClearSelection2(True)
                        arc1.Select4(False, Nothing)
                        leftLine.Select4(True, Nothing)
                        _model.SketchAddConstraints("sgTANGENT")
                        _model.ClearSelection2(True)
                        Log("  tangent: Arc1 ↔ left")
                    Catch ex As Exception
                        Log("  tangent Arc1/left: " & ex.Message)
                    End Try

                    ' Tangent: Arc 1 ↔ Arc 2
                    Try
                        _model.ClearSelection2(True)
                        arc1.Select4(False, Nothing)
                        arc2.Select4(True, Nothing)
                        _model.SketchAddConstraints("sgTANGENT")
                        _model.ClearSelection2(True)
                        Log("  tangent: Arc1 ↔ Arc2")
                    Catch ex As Exception
                        Log("  tangent Arc1/Arc2: " & ex.Message)
                    End Try

                    ' Tangent: Arc 3 ↔ right line
                    Try
                        _model.ClearSelection2(True)
                        arc3.Select4(False, Nothing)
                        rightLine.Select4(True, Nothing)
                        _model.SketchAddConstraints("sgTANGENT")
                        _model.ClearSelection2(True)
                        Log("  tangent: Arc3 ↔ right")
                    Catch ex As Exception
                        Log("  tangent Arc3/right: " & ex.Message)
                    End Try

                    ' Tangent: Arc 3 ↔ Arc 2
                    Try
                        _model.ClearSelection2(True)
                        arc3.Select4(False, Nothing)
                        arc2.Select4(True, Nothing)
                        _model.SketchAddConstraints("sgTANGENT")
                        _model.ClearSelection2(True)
                        Log("  tangent: Arc3 ↔ Arc2")
                    Catch ex As Exception
                        Log("  tangent Arc3/Arc2: " & ex.Message)
                    End Try

                    ' ---- R2 dimension on Arc 2 ----
                    Try
                        _model.ClearSelection2(True)
                        arc2.Select4(False, Nothing)
                        Dim r2DimObj As Object = _model.AddDimension2(rOff + seedMidX + 0.005, h + 0.005, 0)
                        If r2DimObj IsNot Nothing Then
                            Try
                                Dim getDim As Object = r2DimObj.GetType().InvokeMember("GetDimension2", _
                                    Reflection.BindingFlags.InvokeMethod, Nothing, r2DimObj, New Object() {0})
                                If getDim IsNot Nothing Then
                                    getDim.GetType().InvokeMember("SetSystemValue3", _
                                        Reflection.BindingFlags.InvokeMethod, Nothing, getDim, _
                                        New Object() {r2, 1, Nothing})
                                End If
                            Catch ex As Exception
                                Log("  R2 dim value set: " & ex.Message)
                            End Try
                            Log(String.Format("  R2 dimension set to {0:F2}mm", r2MM))
                        Else
                            Log("  R2 dimension: AddDimension2 returned Nothing")
                        End If
                        _model.ClearSelection2(True)
                    Catch ex As Exception
                        Log("  R2 dimension: " & ex.Message)
                    End Try

                    ' ---- Cx dimension: left vertical line to Arc 2's CENTER point ----
                    ' arc2.GetType() resolves to ISketchSegment.GetType() (returns enum),
                    ' not System.Object.GetType(). Box to Object first.
                    Dim arc2CenterPt As SketchPoint = Nothing
                    Try
                        Dim arc2Boxed As Object = arc2
                        Dim centerObj As Object = arc2Boxed.GetType().InvokeMember("GetCenterPoint2", _
                            Reflection.BindingFlags.InvokeMethod, Nothing, arc2Boxed, New Object() {})
                        If centerObj IsNot Nothing AndAlso TypeOf centerObj Is SketchPoint Then
                            arc2CenterPt = DirectCast(centerObj, SketchPoint)
                        End If
                    Catch ex As Exception
                        Log("  GetCenterPoint2: " & ex.Message)
                    End Try

                    Try
                        _model.ClearSelection2(True)
                        leftLine.Select4(False, Nothing)
                        If arc2CenterPt IsNot Nothing Then
                            arc2CenterPt.Select4(True, Nothing)
                        Else
                            arc2.Select4(True, Nothing)
                        End If
                        Dim cxDimObj As Object = _model.AddDimension2(rOff + cx * 0.5, -0.008, 0)
                        If cxDimObj IsNot Nothing Then
                            Try
                                Dim getDim As Object = cxDimObj.GetType().InvokeMember("GetDimension2", _
                                    Reflection.BindingFlags.InvokeMethod, Nothing, cxDimObj, New Object() {0})
                                If getDim IsNot Nothing Then
                                    getDim.GetType().InvokeMember("SetSystemValue3", _
                                        Reflection.BindingFlags.InvokeMethod, Nothing, getDim, _
                                        New Object() {cx, 1, Nothing})
                                End If
                            Catch ex As Exception
                                Log("  Cx dim value set: " & ex.Message)
                            End Try
                            Log(String.Format("  Cx dimension set to {0:F2}mm", cxMM))
                        Else
                            Log("  Cx dimension: AddDimension2 returned Nothing")
                        End If
                        _model.ClearSelection2(True)
                    Catch ex As Exception
                        Log("  Cx dimension: " & ex.Message)
                    End Try

                    ' ---- Lip lines ----
                    _model.SketchManager.CreateLine(rOff - innerLip, 0, 0, rOff, 0, 0)
                    _model.SketchManager.CreateLine(rOff + w, 0, 0, rOff + w + outerLip, 0, 0)
                    Log("  Inner + outer lip lines drawn")
                    Log("  Bullet sketch complete")


                ' ── Arc-entity profiles (CircularArc=1, HalfRoll=10, DoubleRoll=11) ──
                ' Draw real CreateArc sketch entities. SW handles >180° arcs natively.
                ' For DoubleRoll, a connecting line is drawn between non-adjacent arcs
                ' (the center flat section).
                ElseIf profile.UsesArcEntities Then
                    Dim rolls As List(Of CircularArcRoll) = profile.GetArcRolls()
                    If rolls Is Nothing OrElse rolls.Count = 0 Then
                        Log("ERROR: GetArcRolls returned no rolls")
                        Return False
                    End If

                    ' Lead-in line: R_inner → first arc start, at z=0
                    _model.SketchManager.CreateLine( _
                        profile.R_inner / 1000.0, 0, 0, _
                        rolls(0).R_start / 1000.0, 0, 0)

                    ' Draw each arc, with connecting lines between non-adjacent arcs
                    For ri As Integer = 0 To rolls.Count - 1
                        Dim rl As CircularArcRoll = rolls(ri)
                        ' SW direction convention on Front Plane sketch (CCW=+1, CW=-1):
                        '   UP dome traverses via +Z top of circle → CW → -1
                        '   DOWN valley traverses via -Z bottom    → CCW → +1
                        Dim dirArc As Integer = If(rl.IsUp, -1, 1)
                        Dim arc As Object = _model.SketchManager.CreateArc( _
                            rl.Rc      / 1000.0, rl.Zc / 1000.0, 0, _
                            rl.R_start / 1000.0, 0, 0, _
                            rl.R_end   / 1000.0, 0, 0, _
                            dirArc)
                        If arc Is Nothing Then
                            Log(String.Format("ERROR: CreateArc failed on roll {0}", ri + 1))
                            Return False
                        End If

                        ' If next arc doesn't start where this one ends, draw a flat line
                        If ri < rolls.Count - 1 Then
                            Dim gap As Double = rolls(ri + 1).R_start - rl.R_end
                            If gap > 0.01 Then
                                _model.SketchManager.CreateLine( _
                                    rl.R_end / 1000.0, 0, 0, _
                                    rolls(ri + 1).R_start / 1000.0, 0, 0)
                            End If
                        End If
                    Next
                    Log(String.Format("  Arcs: {0} drawn (h={1:F2}, pitch={2:F2})", _
                        rolls.Count, profile.H_eff, profile.EffPitchPerRoll))

                    ' Outer lip line: last arc end → outer lip
                    _model.SketchManager.CreateLine( _
                        rolls(rolls.Count - 1).R_end / 1000.0, 0, 0, _
                        (profile.R_rolls_outer + profile.LipWidth + 5.0) / 1000.0, 0, 0)
                Else
                    ' ── All other profile types: existing single-spline path ──
                    ' Deduplicate consecutive points (ArcLines/SineLines have Z=0 duplicates at junctions)
                    Dim cleanPts As New List(Of ProfilePoint)
                    cleanPts.Add(pts(0))
                    For i As Integer = 1 To pts.Count - 2
                        Dim prev As ProfilePoint = cleanPts(cleanPts.Count - 1)
                        If Math.Abs(pts(i).R - prev.R) > 0.0005 OrElse Math.Abs(pts(i).Z - prev.Z) > 0.0005 Then
                            cleanPts.Add(pts(i))
                        End If
                    Next

                    Dim nSp As Integer = cleanPts.Count
                    Dim spArr(nSp * 3 - 1) As Double
                    For i As Integer = 0 To nSp - 1
                        spArr(i * 3)     = cleanPts(i).R / 1000.0
                        spArr(i * 3 + 1) = cleanPts(i).Z / 1000.0
                        spArr(i * 3 + 2) = 0
                    Next
                    Dim spline As Object = _model.SketchManager.CreateSpline2(spArr, False)
                    If spline Is Nothing Then spline = _model.SketchManager.CreateSpline(CObj(spArr))
                    Log(String.Format("  Spline: {0} pts (from {1} raw)", nSp, pts.Count))

                    ' Outer lip line
                    _model.SketchManager.CreateLine( _
                        cleanPts(cleanPts.Count - 1).R / 1000.0, 0, 0, _
                        pts(pts.Count - 1).R / 1000.0, 0, 0)
                End If

                ' Close sketch, revolve
                _model.SketchManager.InsertSketch(True)
                _model.ClearSelection2(True)
                _model.Extension.SelectByID2("Sketch1", "SKETCH", 0, 0, 0, False, 0, Nothing, 0)

                Dim feat As Object = _model.FeatureManager.FeatureRevolve2( _
                    True, False, False, False, False, False, _
                    CInt(swEndConditions_e.swEndCondBlind), _
                    CInt(swEndConditions_e.swEndCondBlind), _
                    2.0 * Math.PI, 0, False, False, 0, 0, _
                    CInt(swThinWallType_e.swThinWallOneDirection), _
                    0, 0, True, True, True)

                If feat Is Nothing Then
                    Log("WARNING: FeatureRevolve2 returned Nothing")
                Else
                    Log("Surface revolve created!")
                End If

                _model.ViewZoomtofit2()
                Log("Part complete.")
                Log(profile.Summary())
                Return True
            Catch ex As System.Exception
                Log("ERROR in CreatePart: " & ex.Message)
                Return False
            End Try
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Helper — connect to CosmosWorks
        ' ──────────────────────────────────────────────────────────────
        Private Function GetCosmos() As Boolean
            Try
                Dim cb As CwAddincallback = DirectCast( _
                    _swApp.GetAddInObject("CosmosWorks.CosmosWorks"), CwAddincallback)
                _cw = cb.CosmosWorks
                Log("CosmosWorks connected")
                Return True
            Catch ex As System.Exception
                Log("ERROR getting CosmosWorks: " & ex.Message)
                Return False
            End Try
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Helper — reconnect to active study (for steps 5a/5b)
        ' ──────────────────────────────────────────────────────────────
        Private Function ReconnectStudy() As Boolean
            If _study IsNot Nothing Then Return True
            Try
                If _swApp Is Nothing Then
                    Log("ERROR: Not connected to SolidWorks.")
                    Return False
                End If
                If Not GetCosmos() Then Return False
                _cwDoc = _cw.ActiveDoc
                If _cwDoc Is Nothing Then
                    Log("ERROR: No active Simulation doc.")
                    Return False
                End If
                Dim sm As CWStudyManager = _cwDoc.StudyManager
                _study = sm.GetStudy(sm.ActiveStudy)
                If _study Is Nothing Then
                    Log("ERROR: Could not get active study.")
                    Return False
                End If
                Return True
            Catch ex As System.Exception
                Log("ERROR reconnecting study: " & ex.Message)
                Return False
            End Try
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Helper — find circular edges by scanning body geometry
        ' ──────────────────────────────────────────────────────────────
        Private Sub FindCircularEdges(ByVal profile As SpiderProfile, _
                                       ByRef idEdge As Edge, ByRef odEdge As Edge)
            idEdge = Nothing
            odEdge = Nothing
            Try
                Dim part As PartDoc = DirectCast(_model, PartDoc)
                Dim bodies As Object = part.GetBodies2(CInt(swBodyType_e.swSheetBody), True)
                If bodies Is Nothing Then bodies = part.GetBodies2(CInt(swBodyType_e.swSolidBody), True)
                If bodies Is Nothing Then Log("  No bodies found") : Return

                Dim bodyArr() As Object = DirectCast(bodies, Object())
                If bodyArr.Length = 0 Then Return

                Dim rID_m As Double = profile.R_inner / 1000.0   ' target ID radius in meters
                Dim rOD_m As Double = profile.R_outer / 1000.0   ' target OD radius in meters
                Dim bestIdDiff As Double = Double.MaxValue
                Dim bestOdDiff As Double = Double.MaxValue
                Dim bestIdR As Double = 0, bestOdR As Double = 0

                Dim body As Body2 = DirectCast(bodyArr(0), Body2)
                Dim edges As Object = body.GetEdges()
                If edges Is Nothing Then Return
                Dim edgeArr() As Object = DirectCast(edges, Object())
                Log(String.Format("  Scanning {0} edges...", edgeArr.Length))

                For Each eObj As Object In edgeArr
                    Dim e As Edge = DirectCast(eObj, Edge)
                    Dim rMean As Double = -1

                    ' Fast path: IsCircle with CircleParams
                    Try
                        Dim curve As Curve = e.GetCurve()
                        If curve IsNot Nothing AndAlso curve.IsCircle() Then
                            Dim cp() As Double = DirectCast(curve.CircleParams, Double())
                            If cp IsNot Nothing AndAlso cp.Length > 6 Then rMean = cp(6)
                        End If
                    Catch : End Try

                    ' Fallback: sample start vertex position
                    If rMean < 0 Then
                        Try
                            Dim sv As Vertex = e.GetStartVertex()
                            If sv IsNot Nothing Then
                                Dim pt() As Double = DirectCast(sv.GetPoint(), Double())
                                If pt IsNot Nothing AndAlso pt.Length >= 3 Then
                                    Dim r1 As Double = Math.Sqrt(pt(0)*pt(0) + pt(2)*pt(2))
                                    ' Verify with end vertex — if both have same R it's circular
                                    Dim ev As Vertex = e.GetEndVertex()
                                    If ev IsNot Nothing Then
                                        Dim pt2() As Double = DirectCast(ev.GetPoint(), Double())
                                        If pt2 IsNot Nothing AndAlso pt2.Length >= 3 Then
                                            Dim r2 As Double = Math.Sqrt(pt2(0)*pt2(0) + pt2(2)*pt2(2))
                                            If Math.Abs(r1 - r2) < 0.001 Then rMean = (r1+r2)/2  ' circular
                                        End If
                                    Else
                                        rMean = r1  ' closed loop: start=end, definitely circular
                                    End If
                                End If
                            End If
                        Catch : End Try
                    End If

                    If rMean < 0 Then Continue For

                    Dim diffId As Double = Math.Abs(rMean - rID_m)
                    If diffId < bestIdDiff Then bestIdDiff = diffId : idEdge = e : bestIdR = rMean
                    If rMean > bestOdR Then odEdge = e : bestOdR = rMean
                Next

                If idEdge IsNot Nothing Then
                    Log(String.Format("  ID edge: R={0:F2}mm (target={1:F2}mm)", bestIdR * 1000.0, profile.R_inner))
                End If
                If odEdge IsNot Nothing Then
                    Log(String.Format("  OD edge: R={0:F2}mm (target={1:F2}mm)", bestOdR * 1000.0, profile.R_outer))
                End If
            Catch ex As System.Exception
                Log("  Edge search failed: " & ex.Message)
            End Try
        End Sub

        ' ──────────────────────────────────────────────────────────────
        ' STEP 3 — Setup Study (create + material + shell + BCs + props)
        ' ──────────────────────────────────────────────────────────────
        Public Function SetupStudy(ByVal profile As SpiderProfile, _
                                    ByVal maxDisp_mm As Double, _
                                    ByVal nSteps As Integer) As Boolean
            If _swApp Is Nothing OrElse _model Is Nothing Then
                Log("ERROR: No active model.")
                Return False
            End If
            Try
                ' Always re-fetch CosmosWorks — stale reference causes RPC_E_SERVERFAULT
                ' when a new document has been opened since last SetupStudy call.
                _cw = Nothing
                _cwDoc = Nothing
                _study = Nothing
                If Not GetCosmos() Then Return False
                _cwDoc = _cw.ActiveDoc
                If _cwDoc Is Nothing Then
                    Log("ERROR: Simulation has no active doc.")
                    Return False
                End If

                ' ─── 3.1 Create study ───
                Log("Creating nonlinear study...")
                Dim studyMgr As CWStudyManager = _cwDoc.StudyManager
                Dim errCode As Integer = 0
                studyMgr.CreateNewStudy3("SpiderNL", _
                    CInt(swsAnalysisStudyType_e.swsAnalysisStudyTypeNonlinear), _
                    0, errCode)
                Dim cnt As Integer = studyMgr.StudyCount
                If cnt > 0 Then
                    studyMgr.ActiveStudy = cnt - 1
                    _study = studyMgr.GetStudy(cnt - 1)
                End If
                If _study Is Nothing Then
                    Log(String.Format("ERROR: Study creation failed (err={0})", errCode))
                    Return False
                End If
                Log("Study 'SpiderNL' created")

                ' ─── 3.2 Apply material ───
                Log("")
                Log("Applying material: " & profile.MaterialName)

                ' Step A: Apply to part document
                Try
                    Dim part As PartDoc = DirectCast(_model, PartDoc)
                    Dim configName As String = ""
                    Try
                        configName = _model.ConfigurationManager.ActiveConfiguration.Name
                    Catch
                        configName = "Default"
                    End Try
                    Log(String.Format("  Config: '{0}'", configName))

                    ' Try multiple database paths
                    Dim applied As Boolean = False
                    For Each db As String In New String() { _
                        "Spider Materials", _
                        "", _
                        "Custom Materials", _
                        "solidworks materials"}
                        Try
                            part.SetMaterialPropertyName2(configName, db, profile.MaterialName)
                            Dim check As String = part.GetMaterialPropertyName2(configName, db)
                            If Not String.IsNullOrEmpty(check) Then
                                Log(String.Format("  Part material OK: db='{0}' -> '{1}'", db, check))
                                applied = True
                                Exit For
                            End If
                        Catch
                        End Try
                    Next
                    If Not applied Then
                        Log("  Part material: could not verify")
                    End If
                Catch ex As System.Exception
                    Log("  Part material failed: " & ex.Message)
                End Try

                ' Step B: Apply to Simulation study via SolidManager
                Try
                    Dim solidMgr As CWSolidManager = _study.SolidManager
                    If solidMgr IsNot Nothing Then
                        Dim compCount As Integer = solidMgr.ComponentCount
                        Log(String.Format("  SolidManager components: {0}", compCount))
                        If compCount > 0 Then
                            Dim smErr As Integer = 0
                            Dim comp As CWSolidComponent = solidMgr.GetComponentAt(0, smErr)
                            If comp IsNot Nothing Then
                                Dim compObj As Object = DirectCast(comp, Object)
                                Log("  Got SolidComponent")

                                ' Try SetLibraryMaterial on the COMPONENT (not body)
                                Try
                                    CallByName(compObj, "SetLibraryMaterial", CallType.Method, _
                                        "Spider Materials", profile.MaterialName)
                                    Log("  Component SetLibraryMaterial: OK")
                                Catch ex2 As System.Exception
                                    Log("  Component SetLibraryMaterial: " & ex2.Message)
                                End Try

                                ' Also try on body index 0 with error handling
                                Try
                                    Dim body As CWSolidBody = comp.GetSolidBodyAt(0, smErr)
                                    If body IsNot Nothing Then
                                        CallByName(DirectCast(body, Object), "SetLibraryMaterial", _
                                            CallType.Method, "Spider Materials", profile.MaterialName)
                                        Log("  Body SetLibraryMaterial: OK")
                                    Else
                                        Log(String.Format("  GetSolidBodyAt: err={0} (expected for shells)", smErr))
                                    End If
                                Catch
                                End Try

                                ' Enumerate component members for material-related methods
                                Log("  Component members (material/set/lib):")
                                For Each m As MemberInfo In compObj.GetType().GetMembers()
                                    If m.MemberType = MemberTypes.Method OrElse _
                                       m.MemberType = MemberTypes.Property Then
                                        Dim nm As String = m.Name.ToLower()
                                        If nm.Contains("material") OrElse nm.Contains("lib") OrElse _
                                           nm.Contains("shell") OrElse nm.Contains("body") Then
                                            Log("    " & m.MemberType.ToString() & ": " & m.Name)
                                        End If
                                    End If
                                Next
                            End If
                        End If
                    End If
                Catch ex As System.Exception
                    Log("  Simulation material failed: " & ex.Message)
                End Try

                ' ─── 3.3 Shell thickness ───
                Log("")
                Log("Shell thickness...")
                Try
                    Dim shellMgr As CWShellManager = _study.ShellManager
                    If shellMgr IsNot Nothing AndAlso shellMgr.ShellCount > 0 Then
                        Dim shErr As Integer = 0
                        Dim sh As CWShell = shellMgr.GetShellAt(0, shErr)
                        If sh IsNot Nothing Then
                            sh.ShellBeginEdit()
                            Try
                                sh.ShellUnit = CInt(swsLinearUnit_e.swsLinearUnitMillimeters)
                            Catch
                            End Try
                            Try
                                CallByName(DirectCast(sh, Object), "ShellThickness", CallType.Set, CDbl(profile.T))
                            Catch
                            End Try
                            Try
                                CallByName(DirectCast(sh, Object), "SetLibraryMaterial", CallType.Method, _
                                    "Spider Materials", profile.MaterialName)
                            Catch
                            End Try
                            sh.ShellEndEdit()
                        End If
                    End If
                Catch
                End Try
                Log(String.Format("  *** Right-click Part in study -> Edit Definition -> accept {0}mm ***", profile.T))
                Log("  *** Then right-click Mesh -> Create Mesh ***")
                Log("  *** Then click 'Mesh + Run' ***")

                ' ─── 3.4 Fixtures and loads ───
                Log("")
                Log("Applying fixtures and loads...")
                Try
                    ' Find ID edge for prescribed displacement
                    Dim idEdge As Edge = Nothing
                    Dim odEdge As Edge = Nothing
                    FindCircularEdges(profile, idEdge, odEdge)
                    _idEdge = idEdge
                    _odEdge = odEdge

                    ' Find lip top face (OD) AND inner flange face (ID) —
                    ' flat annular faces with axial normal. Score each by the maximum
                    ' radius of its circular edges. Outermost = lip (fixed), innermost
                    ' = cone/VC attach (displaced).
                    Dim lipFace As Face2 = Nothing
                    Dim innerFace As Face2 = Nothing
                    Try
                        Dim part As PartDoc = DirectCast(_model, PartDoc)
                        Dim bodies As Object = part.GetBodies2(CInt(swBodyType_e.swSheetBody), True)
                        If bodies Is Nothing Then
                            bodies = part.GetBodies2(CInt(swBodyType_e.swSolidBody), True)
                        End If
                        If bodies IsNot Nothing Then
                            Dim bodyArr() As Object = DirectCast(bodies, Object())
                            If bodyArr.Length > 0 Then
                                Dim body As Body2 = DirectCast(bodyArr(0), Body2)
                                Dim faces As Object = body.GetFaces()
                                If faces IsNot Nothing Then
                                    Dim faceArr() As Object = DirectCast(faces, Object())
                                    Dim bestLipMaxR As Double = 0
                                    Dim bestInnerMaxR As Double = Double.MaxValue
                                    For Each fObj As Object In faceArr
                                        Dim f As Face2 = DirectCast(fObj, Face2)
                                        Try
                                            Dim sf As Surface = f.GetSurface()
                                            If sf IsNot Nothing AndAlso sf.IsPlane() Then
                                                Dim fp() As Double = DirectCast(sf.PlaneParams, Double())
                                                ' Normal should be axial (near Y axis)
                                                If Math.Abs(fp(1)) > 0.9 Then
                                                    ' Get max edge radius for this face
                                                    Dim faceMaxR As Double = 0
                                                    Dim fEdges As Object = f.GetEdges()
                                                    If fEdges IsNot Nothing Then
                                                        Dim fEdgeArr() As Object = DirectCast(fEdges, Object())
                                                        For Each feObj As Object In fEdgeArr
                                                            Try
                                                                Dim fe As Edge = DirectCast(feObj, Edge)
                                                                Dim curve As Curve = fe.GetCurve()
                                                                If curve IsNot Nothing AndAlso curve.IsCircle() Then
                                                                    Dim cp() As Double = DirectCast(curve.CircleParams, Double())
                                                                    If cp IsNot Nothing AndAlso cp.Length > 6 Then
                                                                        If cp(6) > faceMaxR Then faceMaxR = cp(6)
                                                                    End If
                                                                End If
                                                            Catch : End Try
                                                        Next
                                                    End If
                                                    ' Track outermost face (lip) and innermost face (flange)
                                                    If faceMaxR > bestLipMaxR Then
                                                        bestLipMaxR = faceMaxR
                                                        lipFace = f
                                                    End If
                                                    If faceMaxR > 0 AndAlso faceMaxR < bestInnerMaxR Then
                                                        bestInnerMaxR = faceMaxR
                                                        innerFace = f
                                                    End If
                                                End If
                                            End If
                                        Catch
                                        End Try
                                    Next
                                    If lipFace IsNot Nothing Then
                                        Log(String.Format("  OD lip face found: max edge R={0:F2}mm", bestLipMaxR * 1000.0))
                                    End If
                                    If innerFace IsNot Nothing Then
                                        Log(String.Format("  ID flange face found: max edge R={0:F2}mm", bestInnerMaxR * 1000.0))
                                    End If
                                    ' Safety: if only one flat face found, don't use same face for both
                                    If innerFace IsNot Nothing AndAlso lipFace IsNot Nothing AndAlso _
                                       Object.ReferenceEquals(innerFace, lipFace) Then
                                        innerFace = Nothing
                                        Log("  WARNING: only one flat face found, ID face = OD face — using ID edge fallback")
                                    End If
                                End If
                            End If
                        End If
                    Catch ex As System.Exception
                        Log("  Face search failed: " & ex.Message)
                    End Try

                    ' Find Axis1 (revolve axis) for ID prescribed displacement reference
                    Dim refGeom As Object = Nothing
                    Dim refName As String = ""
                    Try
                        Dim feat As Feature = DirectCast( _
                            DirectCast(_model, PartDoc).FeatureByName("Axis1"), Feature)
                        If feat IsNot Nothing Then
                            refGeom = DirectCast(feat, Object)
                            refName = "Axis1"
                            Log("  Got Axis1 (revolve axis)")
                        End If
                    Catch
                    End Try
                    ' Fallback to Front Plane — this is what worked manually in previous session
                    If refGeom Is Nothing Then
                        Try
                            Dim feat As Feature = DirectCast( _
                                DirectCast(_model, PartDoc).FeatureByName("Front Plane"), Feature)
                            If feat IsNot Nothing Then
                                refGeom = DirectCast(feat, Object)
                                refName = "Front Plane"
                                Log("  Using Front Plane as reference (Axis1 not found)")
                            End If
                        Catch
                        End Try
                    End If

                    Dim lrMgr As CWLoadsAndRestraintsManager = _study.LoadsAndRestraintsManager

                    ' --- Fixed restraint on lip top face ---
                    If lrMgr IsNot Nothing Then
                        Log("  Applying fixed restraint on lip top face...")
                        _model.ClearSelection2(True)
                        Dim fixErr As Integer = 0
                        Dim fixR As CWRestraint = Nothing

                        If lipFace IsNot Nothing Then
                            Dim faceArr() As Object = New Object() {DirectCast(lipFace, Object)}
                            fixR = lrMgr.AddRestraint( _
                                CInt(swsRestraintType_e.swsRestraintTypeFixed), _
                                faceArr, Nothing, fixErr)
                            If fixR IsNot Nothing Then
                                Log(String.Format("  Lip face Fixed: OK (err={0})", fixErr))
                            Else
                                Log(String.Format("  Lip face Fixed: FAILED (err={0}) — trying OD edge", fixErr))
                            End If
                        End If

                        ' Fallback to OD edge if face not found or failed
                        If fixR Is Nothing AndAlso odEdge IsNot Nothing Then
                            Dim odArr() As Object = New Object() {DirectCast(odEdge, Object)}
                            fixR = lrMgr.AddRestraint( _
                                CInt(swsRestraintType_e.swsRestraintTypeFixed), _
                                odArr, Nothing, fixErr)
                            If fixR IsNot Nothing Then
                                Log(String.Format("  OD edge Fixed (fallback): OK (err={0})", fixErr))
                            Else
                                Log(String.Format("  Fixed FAILED (err={0})", fixErr))
                                Log("  *** SET MANUALLY: Fixtures -> Fixed -> select lip top face ***")
                            End If
                        End If
                    End If

                    ' --- Prescribed axial displacement on ID flange face (preferred) or ID edge (fallback) ---
                    Dim dispEntity As Object = Nothing
                    Dim dispLabel As String = ""
                    If innerFace IsNot Nothing Then
                        dispEntity = DirectCast(innerFace, Object)
                        dispLabel = "ID flange face"
                    ElseIf idEdge IsNot Nothing Then
                        dispEntity = DirectCast(idEdge, Object)
                        dispLabel = "ID edge (fallback)"
                    End If

                    If dispEntity IsNot Nothing AndAlso lrMgr IsNot Nothing Then
                        Log(String.Format("  Applying prescribed axial displacement on {0}...", dispLabel))
                        _model.ClearSelection2(True)
                        Dim disp_m As Double = maxDisp_mm   ' SW expects mm for shell studies
                        Dim idArr() As Object = New Object() {dispEntity}
                        Dim dispErr As Integer = 0
                        Dim dispR As CWRestraint = Nothing

                        For Each rType As Integer In New Integer() {5, 7, 2, 8}
                            Try
                                dispR = lrMgr.AddRestraint(rType, idArr, refGeom, dispErr)
                                If dispR IsNot Nothing Then
                                    Log(String.Format("  Restraint type {0}: OK", rType))
                                    Exit For
                                End If
                            Catch
                            End Try
                        Next

                        If dispR Is Nothing Then
                            ' Selection-based fallback using ID edge
                            If idEdge IsNot Nothing Then
                                idEdge.Select4(False, Nothing)
                                If refName = "Axis1" Then
                                    _model.Extension.SelectByID2("Axis1", "AXIS", 0, 0, 0, True, 0, Nothing, 0)
                                Else
                                    _model.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, True, 0, Nothing, 0)
                                End If
                                For Each rType As Integer In New Integer() {5, 7, 2, 8}
                                    Try
                                        dispR = lrMgr.AddRestraint(rType, Nothing, Nothing, dispErr)
                                        If dispR IsNot Nothing Then
                                            Log(String.Format("  Restraint type {0} via selection: OK", rType))
                                            Exit For
                                        End If
                                    Catch
                                    End Try
                                Next
                            End If
                        End If

                        If dispR IsNot Nothing Then
                            Try
                                dispR.RestraintBeginEdit()
                                ' Dir2 = Y = axial (Front Plane reference, Y is up)
                                ' This matches the manual setup that worked in the previous session
                                dispR.SetTranslationComponentsValues(0, 1, 0, 0.0, disp_m, 0.0)
                                Dim endErr As Integer = dispR.RestraintEndEdit()
                                Log(String.Format("  Prescribed disp: {0}mm Dir2=Y axial (err={1})", maxDisp_mm, endErr))
                            Catch ex2 As System.Exception
                                Log("  SetTranslation failed: " & ex2.Message)
                            End Try
                        Else
                            Log(String.Format("  Prescribed disp FAILED (err={0})", dispErr))
                            Log("  *** SET MANUALLY (this worked in previous session): ***")
                            Log("  Right-click Fixtures -> Use Reference Geometry")
                            Log("  Select: ID inner edge")
                            Log("  Click pink reference box -> select Front Plane from tree")
                            Log("  Translations: tick middle row (Y/up arrow) = " & maxDisp_mm & "mm")
                            Log("  Leave other two rows = 0, click green checkmark")
                        End If
                    End If
                Catch ex As System.Exception
                    Log("  Fixtures/loads failed: " & ex.Message)
                End Try

                ' ─── 3.5 Study properties ───
                Log("")
                Log("Setting study properties...")
                Try
                    Dim opts As Object = Nothing
                    For Each pName As String In New String() { _
                        "NonLinearStudyOptions", "NonlinearStudyOptions", _
                        "StudyOptions", "StaticStudyOptions"}
                        Try
                            opts = CallByName(_study, pName, CallType.Get)
                            If opts IsNot Nothing Then
                                Log(String.Format("  Got options via: {0}", pName))
                                Exit For
                            End If
                        Catch
                        End Try
                    Next

                    If opts IsNot Nothing Then
                        For Each pName As String In New String() {"LargeDisplacement", "UseLargeDisplacement"}
                            Try
                                CallByName(opts, pName, CallType.Set, 1)
                                Log(String.Format("  {0} = ON", pName))
                                Exit For
                            Catch
                            End Try
                        Next

                        Try
                            CallByName(opts, "IncrementEndTimeValue", CallType.Set, 1.0)
                            Log("  EndTime = 1.0")
                        Catch
                        End Try

                        Dim stepSet As Boolean = False
                        For Each pName As String In New String() { _
                            "AutoTimeStep", "AutoStepping", "TimeStepOption", _
                            "SolutionControl", "UseAutoStepping"}
                            Try
                                CallByName(opts, pName, CallType.Set, 0)
                                Log(String.Format("  {0} = 0 (fixed stepping)", pName))
                                stepSet = True
                                Exit For
                            Catch
                            End Try
                        Next

                        For Each pName As String In New String() { _
                            "FixedIncrementTimeIncrement", "TimeIncrement", _
                            "InitialTimeIncrement", "FixedTimeStep"}
                            Try
                                CallByName(opts, pName, CallType.Set, 1.0 / CDbl(nSteps))
                                Log(String.Format("  {0} = {1:F4}", pName, 1.0 / CDbl(nSteps)))
                                stepSet = True
                                Exit For
                            Catch
                            End Try
                        Next

                        If Not stepSet Then
                            Log("  Could not set time stepping. NL options members:")
                            Dim oType As Type = opts.GetType()
                            For Each m As MemberInfo In oType.GetMembers()
                                If m.MemberType = MemberTypes.Property Then
                                    Log("    " & m.Name)
                                End If
                            Next
                        End If
                    Else
                        Log("  Could not find options. Enumerating study members...")
                        Dim stType As Type = _study.GetType()
                        For Each m As MemberInfo In stType.GetMembers()
                            If m.MemberType = MemberTypes.Property Then
                                Dim nm As String = m.Name.ToLower()
                                If nm.Contains("option") OrElse nm.Contains("solver") OrElse _
                                   nm.Contains("large") OrElse nm.Contains("step") OrElse _
                                   nm.Contains("nonlin") Then
                                    Log("    Prop: " & m.Name)
                                End If
                            End If
                        Next
                        Log("  *** SET MANUALLY: Study -> Properties ***")
                        Log(String.Format("  Large displacement ON, {0} steps", nSteps))
                    End If
                Catch ex As System.Exception
                    Log("  Properties failed: " & ex.Message)
                End Try

                Log("")
                Log("=== Setup complete ===")
                Log("Check study tree. Yellow items need manual attention.")
                Return True

            Catch ex As System.Exception
                Log("ERROR in SetupStudy: " & ex.Message)
                Return False
            End Try
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' STEP 4 — Mesh and Run
        ' ──────────────────────────────────────────────────────────────
        Public Function MeshAndRun() As Boolean
            If Not ReconnectStudy() Then Return False

            Try
                Log("Running study (will auto-mesh if needed)...")
                Dim mesh As CWMesh = _study.Mesh
                If mesh IsNot Nothing Then
                    Try
                        If mesh.NodeCount > 0 Then
                            Log(String.Format("  Existing mesh: {0} nodes", mesh.NodeCount))
                        Else
                            Log("  No mesh yet — RunAnalysis will create one")
                        End If
                    Catch
                        Log("  Mesh state unknown — proceeding to run")
                    End Try
                End If

                Log("Running study (auto-meshes then solves)...")
                Try
                    Dim runErr As Integer = _study.RunAnalysis()
                    Log(String.Format("RunAnalysis complete (err={0})", runErr))
                    If runErr <> 0 Then
                        Log("WARNING: err<>0. Check SolidWorks.")
                        Log("If mesh failed: right-click Mesh -> Create Mesh, then re-run.")
                    End If
                    Return (runErr = 0)
                Catch ex As System.Exception
                    Log("RunAnalysis failed: " & ex.Message)
                    Return False
                End Try
            Catch ex As System.Exception
                Log("ERROR in MeshAndRun: " & ex.Message)
                Return False
            End Try
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' STEP 5 — Extract Results (manual guide, kept as fallback)
        ' ──────────────────────────────────────────────────────────────
        Public Function ExtractResults(ByVal profile As SpiderProfile, _
                                        ByVal outputPath As String) As Boolean
            If Not ReconnectStudy() Then Return False

            Try
                Log("Extracting results (guide mode)...")
                Dim results As CWResults = _study.Results
                If results Is Nothing Then
                    Log("ERROR: No results. Solve first.")
                    Return False
                End If

                Dim crests As List(Of Double) = profile.GetRollCrestRadii()
                Dim sb As New StringBuilder()
                sb.AppendLine("Spider SolidWorks FEA Results")
                sb.AppendLine("============================")
                sb.AppendLine(profile.Summary())
                sb.AppendLine("")
                sb.AppendLine("Roll crests to probe:")
                For i As Integer = 0 To crests.Count - 1
                    Dim dir As String = If(i Mod 2 = 0, _
                        If(profile.FirstRollUp, "UP", "DOWN"), _
                        If(profile.FirstRollUp, "DOWN", "UP"))
                    sb.AppendLine(String.Format("  Roll {0}: R={1:F2}mm ({2})", _
                        i + 1, crests(i), dir))
                Next
                sb.AppendLine("")
                sb.AppendLine("Extract F(x):")
                sb.AppendLine("  Right-click OD restraint -> List Result Force")
                sb.AppendLine("")
                sb.AppendLine("Extract per-roll Z displacement:")
                sb.AppendLine("  Displacement Plot -> Probe -> click roll crests")

                System.IO.File.WriteAllText(outputPath, sb.ToString())
                Log("Guide written: " & outputPath)
                Return True
            Catch ex As System.Exception
                Log("ERROR in ExtractResults: " & ex.Message)
                Return False
            End Try
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' STEP 5a — Enumerate Results (SW2014-specific probing)
        ' ──────────────────────────────────────────────────────────────
        Public Sub EnumerateResults(ByVal profile As SpiderProfile)
            Try
                If Not ReconnectStudy() Then Return
                If _idEdge Is Nothing OrElse _odEdge Is Nothing Then
                    If _model Is Nothing Then
                        Try : _model = DirectCast(_swApp.ActiveDoc, ModelDoc2) : Catch : End Try
                    End If
                    If _model IsNot Nothing Then FindCircularEdges(profile, _idEdge, _odEdge)
                End If
                Dim results As CWResults = _study.Results
                If results Is Nothing Then Log("no results") : Return

                ' 1. Get ID edge node IDs from GetDisplacementForEntities(comp=2, step=13)
                ' Then probe DispAllSteps for node 1482 to find which slot = axial UY
                Dim idNode As Integer = -1
                Try
                    Dim st As Integer = 0
                    Dim eArr() As Object = New Object() {DirectCast(_idEdge, Object)}
                    Dim oa() As Object = DirectCast(results.GetDisplacementForEntities(2, 13, Nothing, eArr, 0, st), Object())
                    idNode = CInt(oa(0))
                    Log(String.Format("ID edge node0: id={0}  comp2_val={1:F6}mm at step13", idNode, CDbl(oa(1))))
                Catch ex As Exception : Log("IDedge err: " & ex.Message) : End Try

                ' 2. GetDisplacementForEntities comp=3 (try UY or UZ) for ID edge at step 13
                For comp As Integer = 1 To 4
                    Try
                        Dim st As Integer = 0
                        Dim eArr() As Object = New Object() {DirectCast(_idEdge, Object)}
                        Dim oa() As Object = DirectCast(results.GetDisplacementForEntities(comp, 13, Nothing, eArr, 0, st), Object())
                        Log(String.Format("IDedge comp={0} step13: [0]={1:E4} [1]={2:F6}", comp, CDbl(oa(0)), CDbl(oa(1))))
                    Catch ex As Exception : Log("IDedge comp=" & comp & " err: " & ex.Message) : End Try
                Next

                ' 3. DispAllSteps for ID edge node -- which slot is UY=10mm at step 13?
                If idNode > 0 Then
                    Try
                        Dim st As Integer = 0
                        Dim oa() As Object = DirectCast( _
                            results.GetDisplacementComponentForAllStepsAtNode(2, idNode, Nothing, 0, st), Object())
                        Dim last As Integer = (oa.Length \ 3) - 1
                        Log(String.Format("DispAllSteps(idNode={0}) step13: [base+0]={1:F6} [base+1]={2:F6} [base+2]={3:F6}", _
                            idNode, CDbl(oa(last*3)), CDbl(oa(last*3+1)), CDbl(oa(last*3+2))))
                        Log("  (one of these should be close to 10mm = prescribed UY at step13)")
                        ' Also check step 1
                        Log(String.Format("  step1: [base+0]={0:F6} [base+1]={1:F6} [base+2]={2:F6}", _
                            CDbl(oa(0)), CDbl(oa(1)), CDbl(oa(2))))
                    Catch ex As Exception : Log("DispAllSteps idNode err: " & ex.Message) : End Try
                End If

                ' 4. React Fy for OD edge nodes (compare to ID edge sum)
                If _odEdge IsNot Nothing Then
                    Try
                        Dim st As Integer = 0
                        Dim eArr() As Object = New Object() {DirectCast(_odEdge, Object)}
                        Dim dispOa() As Object = DirectCast(results.GetDisplacementForEntities(2, 13, Nothing, eArr, 0, st), Object())
                        Dim odNodeIDs As New List(Of Integer)()
                        For i As Integer = 0 To dispOa.Length - 1 Step 2
                            odNodeIDs.Add(CInt(dispOa(i)))
                        Next
                        Log(String.Format("OD edge nodes: {0}", odNodeIDs.Count))
                        ' Build react index map from step 1
                        Dim reactOa() As Object = DirectCast(results.GetReactionForcesAndMoments(13, Nothing, 0, st), Object())
                        Dim idxMap As New Dictionary(Of Integer, Integer)()
                        Dim stride As Integer = 9
                        For i As Integer = 0 To (reactOa.Length \ stride) - 1
                            idxMap(CInt(reactOa(i*stride))) = i
                            If i Mod 500 = 0 Then System.Windows.Forms.Application.DoEvents()
                        Next
                        ' Sum Fy (idx+2) for OD nodes
                        Dim sumOD As Double = 0
                        For Each nid As Integer In odNodeIDs
                            If idxMap.ContainsKey(nid) Then
                                sumOD += CDbl(reactOa(idxMap(nid)*stride + 2))
                            End If
                        Next
                        Log(String.Format("OD Fy sum at step13: {0:F4}N  |sum|={1:F4}N", sumOD, Math.Abs(sumOD)))
                        Log("  (compare to ID Fy sum=0.8663N from last run)")
                    Catch ex As Exception : Log("OD react err: " & ex.Message) : End Try
                End If

                Log("Done")
            Catch ex As System.Exception
                Log("ERROR: " & ex.Message)
            End Try
        End Sub


        ' ──────────────────────────────────────────────────────────────
        ' STEP 5b — Extract Results Auto
        '
        ' Reads UY displacement at each roll crest node per step and
        ' reaction force at OD per step. Writes calibration CSV for
        ' SpiderDesigner: Kms(x), per-roll Z(x), ratio curves.
        '
        ' Call EnumerateResults first to confirm component indices and
        ' unit conventions before running this.
        ' ──────────────────────────────────────────────────────────────
        Public Function ExtractResultsAuto(ByVal profile As SpiderProfile, _
                                            ByVal maxDisp_mm As Double, _
                                            ByVal nSteps As Integer, _
                                            ByVal outputPath As String) As Boolean
            If Not ReconnectStudy() Then Return False
            Try
                Log("=== ExtractResultsAuto ===")
                Dim results As CWResults = _study.Results
                If results Is Nothing Then Log("ERROR: no results") : Return False
                Dim mesh As CWMesh = _study.Mesh
                If mesh Is Nothing Then Log("ERROR: no mesh") : Return False
                Dim mo As Object = DirectCast(mesh, Object)

                ' Reconnect edges if needed
                If _idEdge Is Nothing OrElse _odEdge Is Nothing Then
                    If _model Is Nothing Then
                        Try : _model = DirectCast(_swApp.ActiveDoc, ModelDoc2) : Catch : End Try
                    End If
                    If _model IsNot Nothing Then FindCircularEdges(profile, _idEdge, _odEdge)
                End If

                ' Step count
                Dim stepCount As Integer = nSteps
                Try
                    Dim v As Object = CallByName(DirectCast(results,Object), "GetMaximumAvailableSteps", CallType.Method)
                    If v IsNot Nothing Then stepCount = CInt(v)
                Catch : End Try
                Log(String.Format("  stepCount={0}  maxDisp={1}mm  nodes={2}", stepCount, maxDisp_mm, mesh.NodeCount))

                ' ─── Phase 1: Build nodeID->(R_mm, Y_mm) map from GetNodes ───
                ' GetNodes: Object[] stride=4 [nodeID, x_m, y_m, z_m, ...]  Y=axial, R=sqrt(x2+z2)
                Log("  Building node map...")
                Dim nodeR As New Dictionary(Of Integer, Double)()
                Dim nodeY As New Dictionary(Of Integer, Double)()
                Dim coordScale As Double = 1.0
                Try
                    Dim oa() As Object = DirectCast(CallByName(mo, "GetNodes", CallType.Method), Object())
                    Dim nc As Integer = oa.Length \ 4
                    ' Auto-detect coord units from first node
                    Dim r0 As Double = Math.Sqrt(CDbl(oa(1))^2 + CDbl(oa(3))^2)
                    coordScale = If(r0 < 1.0, 1000.0, 1.0)
                    Log(String.Format("  Coord units: {0} (R0={1:F5})", If(coordScale=1000, "meters", "mm"), r0))
                    For i As Integer = 0 To nc - 1
                        Dim b As Integer = i * 4
                        Dim nid As Integer = CInt(oa(b))
                        Dim nx As Double = CDbl(oa(b+1)), ny As Double = CDbl(oa(b+2)), nz As Double = CDbl(oa(b+3))
                        nodeR(nid) = Math.Sqrt(nx*nx + nz*nz) * coordScale
                        nodeY(nid) = ny * coordScale
                        If i Mod 500 = 0 Then System.Windows.Forms.Application.DoEvents()
                    Next
                    Log(String.Format("  Node map: {0} entries", nodeR.Count))
                Catch ex As Exception
                    Log("  GetNodes failed: " & ex.Message)
                    Return False
                End Try

                ' ─── Phase 2: Match crest nodes by (R, Y) ───
                ' Y_crest = ±H_eff depending on roll direction (FirstRollUp and roll index)
                Dim crests As List(Of Double) = profile.GetRollCrestRadii()
                Dim H_eff As Double = profile.H_pp / 2.0
                Dim crestNodeID(crests.Count - 1) As Integer
                Dim crestNodeR(crests.Count - 1) As Double
                Dim crestNodeY(crests.Count - 1) As Double
                For ci As Integer = 0 To crests.Count - 1
                    Dim targetR As Double = crests(ci)
                    ' Roll direction: even index same as FirstRollUp, odd index opposite
                    Dim rollUp As Boolean = If(ci Mod 2 = 0, profile.FirstRollUp, Not profile.FirstRollUp)
                    Dim targetY As Double = If(rollUp, H_eff, -H_eff)
                    ' Find node minimizing combined R and Y distance
                    Dim bestScore As Double = Double.MaxValue
                    Dim bestID As Integer = -1
                    Dim bestR As Double = 0, bestY As Double = 0
                    For Each kvp As KeyValuePair(Of Integer, Double) In nodeR
                        Dim nid As Integer = kvp.Key
                        Dim diffR As Double = Math.Abs(kvp.Value - targetR)
                        Dim diffY As Double = Math.Abs(nodeY(nid) - targetY)
                        ' Weight R match more heavily (crests are spread radially, H_eff is small)
                        Dim score As Double = diffR + diffY * 0.5
                        If score < bestScore Then
                            bestScore = score
                            bestID = nid
                            bestR = kvp.Value
                            bestY = nodeY(nid)
                        End If
                    Next
                    crestNodeID(ci) = bestID
                    crestNodeR(ci) = bestR
                    crestNodeY(ci) = bestY
                    Log(String.Format("  Roll {0} ({1}): target R={2:F2}mm Y={3:F2}mm  node={4} R={5:F2}mm Y={6:F2}mm", _
                        ci+1, If(rollUp,"up","dn"), targetR, targetY, bestID, bestR, bestY))
                Next

                ' ─── Phase 3: Get AXIAL UY at each crest node for all steps ───
                ' GetDisplacementComponentForAllStepsAtNode(comp=1=UY, nodeID, ...)
                ' Returns Object[stepCount*3]: [step_num, time_frac, UY_mm]
                Log("  Getting crest UY (comp=1=axial)...")
                Dim crestUY(crests.Count-1, stepCount-1) As Double
                Dim timeFrac(stepCount-1) As Double
                Dim timeFracLoaded As Boolean = False
                For ci As Integer = 0 To crests.Count - 1
                    Try
                        Dim st As Integer = 0
                        Dim oa() As Object = DirectCast( _
                            results.GetDisplacementComponentForAllStepsAtNode(1, crestNodeID(ci), Nothing, 0, st), Object())
                        For iStep As Integer = 0 To stepCount - 1
                            Dim b As Integer = iStep * 3
                            If b + 2 < oa.Length Then
                                If Not timeFracLoaded Then timeFrac(iStep) = CDbl(oa(b+1))
                                crestUY(ci, iStep) = CDbl(oa(b+2))  ' comp=1=UY already in mm
                            End If
                        Next
                        If Not timeFracLoaded Then timeFracLoaded = True
                        Log(String.Format("  Roll {0}: step1={1:F4}mm step13={2:F4}mm", _
                            ci+1, crestUY(ci,0), crestUY(ci, stepCount-1)))
                    Catch ex As Exception
                        Log("  Roll " & (ci+1) & " failed: " & ex.Message)
                    End Try
                Next

                ' ─── Phase 4: Get reaction force from ID edge nodes ───
                ' Sum Fy (stride+2) for ID edge nodes = total axial spring force
                Log("  Getting ID edge node IDs...")
                Dim idEdgeNodeIDs As New List(Of Integer)()
                Try
                    Dim st As Integer = 0
                    Dim eArr() As Object = New Object() {DirectCast(_idEdge, Object)}
                    Dim oa() As Object = DirectCast(results.GetDisplacementForEntities(1, 1, Nothing, eArr, 0, st), Object())
                    For i As Integer = 0 To oa.Length - 1 Step 2
                        idEdgeNodeIDs.Add(CInt(oa(i)))
                    Next
                    Log(String.Format("  ID edge nodes: {0}", idEdgeNodeIDs.Count))
                Catch ex As Exception
                    Log("  ID edge nodeIDs failed: " & ex.Message)
                End Try

                ' Build nodeID->array-index map from step 1 reaction array
                Dim reactNodeIdx As New Dictionary(Of Integer, Integer)()
                Dim reactStride As Integer = 9
                Try
                    Dim st As Integer = 0
                    Dim oa() As Object = DirectCast(results.GetReactionForcesAndMoments(1, Nothing, 0, st), Object())
                    For i As Integer = 0 To (oa.Length \ reactStride) - 1
                        reactNodeIdx(CInt(oa(i*reactStride))) = i
                        If i Mod 500 = 0 Then System.Windows.Forms.Application.DoEvents()
                    Next
                    Log(String.Format("  React index map: {0} entries", reactNodeIdx.Count))
                Catch ex As Exception
                    Log("  React index map failed: " & ex.Message)
                End Try

                Dim fReact(stepCount-1) As Double
                For iStep As Integer = 0 To stepCount - 1
                    Try
                        Dim st As Integer = 0
                        Dim oa() As Object = DirectCast( _
                            results.GetReactionForcesAndMoments(iStep+1, Nothing, 0, st), Object())
                        Dim sumFy As Double = 0
                        For Each nid As Integer In idEdgeNodeIDs
                            If reactNodeIdx.ContainsKey(nid) Then
                                sumFy += CDbl(oa(reactNodeIdx(nid)*reactStride + 2))
                            End If
                        Next
                        fReact(iStep) = Math.Abs(sumFy)
                    Catch ex As Exception
                        fReact(iStep) = Double.NaN
                    End Try
                Next
                Log(String.Format("  F_reaction: step1={0:F4}N step13={1:F4}N  Kms_secant={2:F4}N/mm", _
                    fReact(0), fReact(stepCount-1), fReact(stepCount-1)/maxDisp_mm))

                ' ─── Phase 5: Write CSV ───
                Dim sb As New System.Text.StringBuilder()
                sb.Append("Step,Time_frac,X_applied_mm,F_reaction_N,Kms_N_per_mm")
                For ci As Integer = 0 To crests.Count - 1
                    sb.Append(String.Format(",Z_roll{0}_mm", ci+1))
                Next
                For ci As Integer = 0 To crests.Count - 1
                    sb.Append(String.Format(",Ratio_roll{0}", ci+1))
                Next
                sb.AppendLine()
                sb.AppendLine(String.Format("# ID={0} OD={1} N={2} H_pp={3} T={4} E={5} Nu={6}", _
                    profile.ID, profile.OD, profile.N, profile.H_pp, profile.T, profile.E, profile.Nu))
                sb.AppendLine(String.Format("# Roll crests R (mm): " & String.Join(", ", _
                    crests.ConvertAll(Function(r) r.ToString("F2")).ToArray())))
                sb.AppendLine(String.Format("# Crest node IDs: " & String.Join(", ", _
                    Array.ConvertAll(crestNodeID, Function(id) id.ToString()))))
                sb.AppendLine(String.Format("# Crest node R (mm): " & String.Join(", ", _
                    Array.ConvertAll(crestNodeR, Function(r) r.ToString("F2")))))
                sb.AppendLine(String.Format("# Crest node Y (mm): " & String.Join(", ", _
                    Array.ConvertAll(crestNodeY, Function(y) y.ToString("F2")))))

                For iStep As Integer = 0 To stepCount - 1
                    Dim tf As Double = If(timeFracLoaded, timeFrac(iStep), CDbl(iStep+1)/CDbl(stepCount))
                    Dim xApp As Double = tf * maxDisp_mm
                    Dim fStr As String = If(Double.IsNaN(fReact(iStep)), "", fReact(iStep).ToString("F6"))
                    Dim kStr As String = If(Double.IsNaN(fReact(iStep)) OrElse Math.Abs(xApp) < 0.001, "", _
                        (fReact(iStep) / xApp).ToString("F6"))
                    sb.Append(String.Format("{0},{1:F6},{2:F6},{3},{4}", iStep+1, tf, xApp, fStr, kStr))
                    For ci As Integer = 0 To crests.Count - 1
                        sb.Append(String.Format(",{0:F6}", crestUY(ci, iStep)))
                    Next
                    For ci As Integer = 0 To crests.Count - 1
                        Dim ratio As Double = If(Math.Abs(xApp) > 0.001, crestUY(ci, iStep) / xApp, 0.0)
                        sb.Append(String.Format(",{0:F6}", ratio))
                    Next
                    sb.AppendLine()
                Next

                System.IO.File.WriteAllText(outputPath, sb.ToString())
                Log("  CSV: " & outputPath)
                Log("=== ExtractResultsAuto complete ===")
                Return True
            Catch ex As System.Exception
                Log("ERROR in ExtractResultsAuto: " & ex.Message)
                Return False
            End Try
        End Function       ' ──────────────────────────────────────────────────────────────
        ' Utility
        ' ──────────────────────────────────────────────────────────────
        Public Sub ExportProfileCSV(ByVal profile As SpiderProfile, ByVal path As String)
            Dim pts As List(Of ProfilePoint) = profile.GeneratePoints(30)
            Dim sb As New StringBuilder()
            sb.AppendLine("R_mm,Z_mm")
            For i As Integer = 0 To pts.Count - 1
                sb.AppendLine(String.Format("{0:F4},{1:F4}", pts(i).R, pts(i).Z))
            Next
            System.IO.File.WriteAllText(path, sb.ToString())
            Log("Profile CSV exported: " & path)
        End Sub

    End Class
