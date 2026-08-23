Imports System
Imports System.Collections.Generic
Imports System.Reflection
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading
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

        ' Persist solid-mesh state for CreateFinestMesh (which takes no profile).
        ' A thin-wall solid needs element size small vs the wall or it meshes zero.
        Private _solidMesh As Boolean = False
        Private _meshScale As Double = 0.5
        Private Shared _lastBindShare As Double = 0.0
        Private _wallT As Double = 0.0
        ' Set True inside CreateFinestMesh when a solid row's estimated element
        ' count exceeds the ceiling (thin wall over a large ring). MeshAndRun
        ' checks it and returns without running, so SW is never handed an
        ' unmeshable thin solid to lock on.
        Private _solidMeshSkip As Boolean = False
        Private _solidElemCeiling As Double = 500000.0

        Public Sub New(ByVal logger As Action(Of String))
            _log = logger
        End Sub

        Private Sub Log(ByVal msg As String)
            If _log IsNot Nothing Then _log(msg)
            ' Persist every line to disk. The textbox log is lost when SW locks
            ' and the process is killed; this file survives a Ctrl-Alt-Del so the
            ' exact last line before a lock (and every extraction step) is
            ' recoverable. Wrapped so logging can never throw.
            Try
                System.IO.File.AppendAllText("C:\SpiderSW_Results\SpiderSW_debug.log", _
                    DateTime.Now.ToString("HH:mm:ss.fff") & "  " & msg & System.Environment.NewLine)
            Catch
            End Try
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

            ' ── TriRadius (ProfileType=17): open template and drive dimensions ──
            If profile.ProfileType = 17 Then
                Return CreatePartFromTriRadiusTemplate(profile)
            End If

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
                    ' ── All other profile types: use GetSketchSegments for line+spline ──
                    ' GetSketchSegments returns alternating line (IsLine=True) and spline
                    ' (IsLine=False) segments. Drawing them separately prevents spline
                    ' oscillation at flat-to-corrugation transitions.
                    Dim segs As List(Of ProfileSegment) = profile.GetSketchSegments(30)
                    For si As Integer = 0 To segs.Count - 1
                        Dim seg As ProfileSegment = segs(si)
                        If seg.IsLine Then
                            ' Draw as a line from first to last point
                            If seg.Points.Count >= 2 Then
                                _model.SketchManager.CreateLine( _
                                    seg.Points(0).R / 1000.0, seg.Points(0).Z / 1000.0, 0, _
                                    seg.Points(seg.Points.Count - 1).R / 1000.0, _
                                    seg.Points(seg.Points.Count - 1).Z / 1000.0, 0)
                            End If
                        Else
                            ' Draw as a spline, deduplicating consecutive points
                            Dim cleanPts As New List(Of ProfilePoint)
                            cleanPts.Add(seg.Points(0))
                            For i As Integer = 1 To seg.Points.Count - 1
                                Dim prev As ProfilePoint = cleanPts(cleanPts.Count - 1)
                                If Math.Abs(seg.Points(i).R - prev.R) > 0.0005 OrElse _
                                   Math.Abs(seg.Points(i).Z - prev.Z) > 0.0005 Then
                                    cleanPts.Add(seg.Points(i))
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
                            Log(String.Format("  Spline: {0} pts (from {1} raw)", nSp, seg.Points.Count))
                        End If
                    Next
                    Log(String.Format("  Segments: {0} drawn", segs.Count))
                End If

                ' Close sketch, revolve
                _model.SketchManager.InsertSketch(True)
                _model.ClearSelection2(True)
                _model.Extension.SelectByID2("Sketch1", "SKETCH", 0, 0, 0, False, 0, Nothing, 0)

                Dim feat As Object
                _solidMesh = profile.SolidMesh
                _meshScale = profile.MeshScale
                If _meshScale <= 0.0 Then _meshScale = 0.5
                _wallT = profile.T
                If profile.SolidMesh Then
                    ' Thin-wall SOLID of wall thickness T (SW revolve thickness is
                    ' in METERS). Recovers valid stiffness where the shell degrades
                    ' on the corrugation apex (high T/R_crest). IsSolid + IsThin true.
                    Dim tMeters As Double = profile.T / 1000.0
                    feat = _model.FeatureManager.FeatureRevolve2( _
                        True, True, True, False, False, False, _
                        CInt(swEndConditions_e.swEndCondBlind), _
                        CInt(swEndConditions_e.swEndCondBlind), _
                        2.0 * Math.PI, 0, False, False, 0, 0, _
                        CInt(swThinWallType_e.swThinWallOneDirection), _
                        tMeters, 0, True, True, True)
                    If feat Is Nothing Then
                        Log("WARNING: FeatureRevolve2 (thin-wall solid) returned Nothing")
                    Else
                        Log(String.Format("Thin-wall SOLID revolve created! wall={0:F3} mm", profile.T))
                    End If
                Else
                    feat = _model.FeatureManager.FeatureRevolve2( _
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
        ' TriRadius — open template part and drive dimensions
        ' ──────────────────────────────────────────────────────────────
        Private Function CreatePartFromTriRadiusTemplate(ByVal profile As SpiderProfile) As Boolean
            Try
                Dim templatePath As String = "C:\Projects\SpiderSWRunner\TriRadius.SLDPRT"
                Log("── TriRadius: opening template ──")
                Log("  Template: " & templatePath)

                If Not System.IO.File.Exists(templatePath) Then
                    Log("ERROR: Template not found: " & templatePath)
                    Return False
                End If

                ' Open (or activate if already open)
                Dim docErr As Integer = 0
                Dim docWarn As Integer = 0
                _model = DirectCast(_swApp.OpenDoc6(templatePath, _
                    CInt(swDocumentTypes_e.swDocPART), 0, "", docErr, docWarn), ModelDoc2)
                If _model Is Nothing Then
                    ' Might already be open — try to activate
                    _model = DirectCast(_swApp.ActivateDoc3(templatePath, True, 0, docErr), ModelDoc2)
                End If
                If _model Is Nothing Then
                    Log(String.Format("ERROR: Could not open template (err={0})", docErr))
                    Return False
                End If
                Log("  Template opened OK")

                ' Drive dimensions (SW stores in meters, input is mm)
                SetDimensionMM("Cone ID@Sketch2", profile.R_inner)
                SetDimensionMM("Basket ID@Sketch2", profile.R_outer)
                SetDimensionMM("Height@Sketch2", profile.H_pp)
                ' FIX (2026-06-14): R2 Radius and R2 Center were swapped, which
                ' fed the crown-radius dim a center-offset value (and vice versa),
                ' producing a zero-length line — the sketch failed to update and
                ' the part meshed at the template's stale default geometry.
                '   R2 Radius@Sketch2 = crown arc radius           = BulletR_Top
                '   R2 Center@Sketch2 = absolute crown center pos  = R_inner + Cx
                ' (template frame: R2 Center is in the same axis frame as Cone ID,
                '  e.g. default Cone ID 50 / R2 Center 55 = centered on a 10 span.)
                SetDimensionMM("R2 Radius@Sketch2", profile.BulletR_Top)
                SetDimensionMM("R2 Center@Sketch2", profile.R_inner + profile.GetBulletCx())
                SetDimensionMM("InnerLip@Sketch2", profile.InnerLipWidth)
                SetDimensionMM("OuterLip@Sketch2", profile.LipWidth)

                ' Rebuild to update driven dimensions
                Log("  Rebuilding...")
                _model.EditRebuild3()

                ' Read driven (tangency-solved) dimensions
                Dim r1 As Double = GetDimensionMM("R1 Radius@Sketch2")
                Dim r3 As Double = GetDimensionMM("R3 Radius@Sketch2")
                Log(String.Format("  R1 Radius (driven) = {0:F3}mm", r1))
                Log(String.Format("  R3 Radius (driven) = {0:F3}mm", r3))

                _model.ViewZoomtofit2()
                Log("TriRadius part complete.")
                Log(profile.Summary())
                Return True
            Catch ex As System.Exception
                Log("ERROR in TriRadius template: " & ex.Message)
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Set a named dimension in the active model (value in mm).
        ''' </summary>
        Private Sub SetDimensionMM(ByVal dimName As String, ByVal value_mm As Double)
            Try
                Dim swDim As Object = _model.Parameter(dimName)
                If swDim IsNot Nothing Then
                    swDim.SystemValue = value_mm / 1000.0
                    Log(String.Format("  Set {0} = {1:F2}mm", dimName, value_mm))
                Else
                    Log(String.Format("  WARNING: Dimension '{0}' not found", dimName))
                End If
            Catch ex As System.Exception
                Log(String.Format("  ERROR setting '{0}': {1}", dimName, ex.Message))
            End Try
        End Sub

        ''' <summary>
        ''' Read a named dimension from the active model (returns mm).
        ''' </summary>
        Private Function GetDimensionMM(ByVal dimName As String) As Double
            Try
                Dim swDim As Object = _model.Parameter(dimName)
                If swDim IsNot Nothing Then
                    Return swDim.SystemValue * 1000.0
                Else
                    Log(String.Format("  WARNING: Dimension '{0}' not found for read", dimName))
                    Return 0.0
                End If
            Catch ex As System.Exception
                Log(String.Format("  ERROR reading '{0}': {1}", dimName, ex.Message))
                Return 0.0
            End Try
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Helper — connect to CosmosWorks
        ' ──────────────────────────────────────────────────────────────
        Private Function GetCosmos() As Boolean
            Try
                Dim cb As CwAddincallback = DirectCast( _
                    _swApp.GetAddInObject("CosmosWorks.CosmosWorks"), CwAddincallback)
                If cb Is Nothing Then Return False
                _cw = cb.CosmosWorks
                If _cw Is Nothing Then Return False
                Log("CosmosWorks connected")
                Return True
            Catch ex As System.Exception
                Return False
            End Try
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Simulation add-in loads LAZILY after a fresh SolidWorks start —
        ' GetAddInObject fails for tens of seconds on a new instance.
        ' Retry with patience instead of failing the run (the cause of the
        ' first restart-test SetupStudy failure, 2026-06-11).
        ' ──────────────────────────────────────────────────────────────
        Private Function GetCosmosRetry(ByVal timeoutSec As Integer) As Boolean
            Dim t0 As DateTime = DateTime.Now
            Dim attempt As Integer = 0
            Do
                attempt += 1
                If GetCosmos() Then
                    If attempt > 1 Then
                        Log(String.Format("  (Simulation add-in ready after {0:F0}s, attempt {1})", _
                            (DateTime.Now - t0).TotalSeconds, attempt))
                    End If
                    Return True
                End If
                If (DateTime.Now - t0).TotalSeconds >= timeoutSec Then
                    Log(String.Format("ERROR: Simulation add-in not available after {0}s. " & _
                        "Check Tools -> Add-Ins -> SolidWorks Simulation (set 'Start Up' too).", timeoutSec))
                    Return False
                End If
                If attempt = 1 Then Log("  Waiting for Simulation add-in to load...")
                System.Threading.Thread.Sleep(5000)
                System.Windows.Forms.Application.DoEvents()
            Loop
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
                If Not GetCosmosRetry(120) Then Return False
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
                                    ByVal nSteps As Integer, _
                                    Optional ByVal isPull As Boolean = False) As Boolean
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
                If Not GetCosmosRetry(120) Then Return False
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
                        "Edge_Materials", _
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

                ' Step B: Apply to Simulation study via SolidManager (solid bodies only;
                ' for shell studies the real assignment happens in 3.3 on the CWShell).
                Try
                    Dim solidMgr As CWSolidManager = _study.SolidManager
                    If solidMgr IsNot Nothing Then
                        Dim compCount As Integer = solidMgr.ComponentCount
                        Log(String.Format("  SolidManager components: {0}", compCount))
                        If compCount > 0 Then
                            Dim smErr As Integer = 0
                            Dim comp As CWSolidComponent = solidMgr.GetComponentAt(0, smErr)
                            If comp IsNot Nothing Then
                                If ApplyLibraryMaterialTo(DirectCast(comp, Object), profile, "SolidComponent") Then
                                    Log("  SolidComponent material: OK")
                                End If
                                Try
                                    Dim body As CWSolidBody = comp.GetSolidBodyAt(0, smErr)
                                    If body IsNot Nothing Then
                                        If ApplyLibraryMaterialTo(DirectCast(body, Object), profile, "SolidBody") Then
                                            Log("  SolidBody material: OK")
                                        End If
                                    Else
                                        Log(String.Format("  GetSolidBodyAt: err={0} (expected for shells)", smErr))
                                    End If
                                Catch
                                End Try
                            End If
                        End If
                    End If
                Catch ex As System.Exception
                    Log("  Simulation material (solid path): " & ex.Message)
                End Try

                ' ─── 3.3 Shell thickness + shell material ───
                Log("")
                Log("Shell thickness + material...")
                Dim shellOK As Boolean = False
                Dim shellMatOK As Boolean = False
                If profile.SolidMesh Then
                    ' Solid body: wall thickness is geometric, material applied via
                    ' the SolidManager path in 3.2. No shell definition to set.
                    Log("  SOLID mode: wall is geometric; shell thickness skipped, material from solid path")
                    shellOK = True
                    shellMatOK = True
                Else
                Try
                    Dim shellMgr As CWShellManager = _study.ShellManager
                    If shellMgr IsNot Nothing AndAlso shellMgr.ShellCount > 0 Then
                        Dim nShells As Integer = shellMgr.ShellCount
                        Log(String.Format("  ShellManager reports {0} shell(s) — applying thickness/material to all", nShells))
                        shellOK = True
                        shellMatOK = True
                        For si As Integer = 0 To nShells - 1
                            Dim shErr As Integer = 0
                            Dim sh As CWShell = shellMgr.GetShellAt(si, shErr)
                            If sh Is Nothing Then
                                Log(String.Format("  Shell {0}: GetShellAt returned Nothing", si))
                                shellOK = False
                                Continue For
                            End If
                            ' --- ONE edit bracket for material + thickness. ShellEndEdit IS
                            '     the dialog's OK button: changes made outside the bracket are
                            '     visible to read-backs but NEVER COMMITTED to the solve. That is
                            '     why ModelType read back 5 while the study solved linear (2 GPa).
                            '     The manual fix worked because clicking OK commits; the code was
                            '     changing the material outside the bracket and losing it. ---
                            sh.ShellBeginEdit()
                            Dim matThis As Boolean = ApplyShellLibraryMaterial(sh, profile)
                            If Not matThis Then
                                ' The custom fallback builds a LINEAR material. For _MR
                                ' (hyperelastic) cards it must NEVER run — it silently
                                ' replaces the Mooney card with linear (root cause of the
                                ' 12:18-13:20 linear solves in the debug log).
                                If profile.MaterialName.EndsWith("_MR", StringComparison.OrdinalIgnoreCase) Then
                                    Log("  *** _MR library assignment FAILED — linear fallback SKIPPED (would overwrite hyperelastic card) ***")
                                Else
                                    matThis = ApplyCustomShellMaterial(sh, profile)
                                End If
                            End If
                            If Not matThis Then shellMatOK = False

                            ' --- Thickness (same bracket, committed together) ---
                            Try
                                sh.ShellUnit = CInt(swsLinearUnit_e.swsLinearUnitMillimeters)
                            Catch
                            End Try
                            Try
                                sh.ShellThickness = CDbl(profile.T)
                            Catch ex2 As System.Exception
                                Log("  Shell " & si & " ShellThickness set failed: " & ex2.Message)
                            End Try
                            Dim endRet As Object = Nothing
                            Try
                                endRet = CallByName(DirectCast(sh, Object), "ShellEndEdit", CallType.Method)
                            Catch
                                Try : sh.ShellEndEdit() : Catch : End Try
                            End Try

                            ' Read-back verification
                            Try
                                Dim tBack As Double = CDbl(sh.ShellThickness)
                                Log(String.Format("  Shell {0} thickness applied: {1:F3} mm (read-back {2:F3}, EndEdit={3})", _
                                    si, profile.T, tBack, If(endRet Is Nothing, "?", endRet.ToString())))
                                If Math.Abs(tBack - profile.T) >= 0.001 Then shellOK = False
                            Catch
                                Log(String.Format("  Shell {0} thickness set to {1:F3} mm (read-back unavailable)", si, profile.T))
                            End Try
                        Next
                    Else
                        Log("  No shells found in ShellManager")
                    End If
                Catch ex As System.Exception
                    Log("  Shell setup failed: " & ex.Message)
                End Try
                End If
                If Not shellOK Then
                    Log(String.Format("  *** MANUAL: Right-click Part -> Edit Definition -> accept {0}mm ***", profile.T))
                End If
                If Not shellMatOK Then
                    Log("  *** MANUAL: Right-click Part -> Apply/Edit Material -> " & profile.MaterialName & " ***")
                End If

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
                                        ' Flat single-face ring (Type 13): inner flange and outer lip
                                        ' resolve to the SAME planar disk face. Fixing that whole face
                                        ' clamps the entire plate, then prescribing the ID edge fights it
                                        ' (over-constrained -> solver throws limit-point dialogs, no results).
                                        ' Null BOTH faces so the BCs route to edges: OD edge fixed,
                                        ' ID edge displaced — the correct clamped-outer / pushed-inner plate.
                                        innerFace = Nothing
                                        lipFace = Nothing
                                        Log("  Flat single-face ring: OD edge (fixed) + ID edge (displaced), not whole-disk face")
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
                                ' This matches the manual setup that worked in the previous session.
                                ' Pull (-Y) is applied as a NEGATIVE value — equivalent to the
                                ' manual 'Reverse direction' checkbox. Verify on first batch Pull
                                ' run that Kms0 matches a prior manually-reversed Pull run.
                                Dim dispSigned As Double = If(isPull, -disp_m, disp_m)
                                dispR.SetTranslationComponentsValues(0, 1, 0, 0.0, dispSigned, 0.0)
                                Dim endErr As Integer = dispR.RestraintEndEdit()
                                Log(String.Format("  Prescribed disp: {0}mm Dir2=Y axial {1} (err={2})", _
                                    maxDisp_mm, If(isPull, "PULL(-Y)", "PUSH(+Y)"), endErr))
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
                ' All names below are VERIFIED against the SW2014 interop dump
                ' (SimAPI_Members.txt): NonLinearStudyOptions, UseLargeDisplacement,
                ' EndTime, FixedTimeIncrement (Double = step size), and
                ' TimeIncrement (Int32 = stepping-method selector).
                Log("")
                Log("Setting study properties...")
                Try
                    Dim opts As CWNonLinearStudyOptions = _study.NonLinearStudyOptions

                    If opts IsNot Nothing Then
                        Try
                            opts.UseLargeDisplacement = 1
                            Log("  UseLargeDisplacement = ON")
                        Catch ex2 As System.Exception
                            Log("  UseLargeDisplacement failed: " & ex2.Message)
                        End Try

                        Try
                            opts.EndTime = 1.0
                            Log("  EndTime = 1.0")
                        Catch ex2 As System.Exception
                            Log("  EndTime failed: " & ex2.Message)
                        End Try

                        ' Step size (pre-loads the 'Fixed' increment value)
                        Dim stepSize As Double = 1.0 / CDbl(nSteps)
                        Try
                            opts.FixedTimeIncrement = stepSize
                            Log(String.Format("  FixedTimeIncrement = {0:F6} ({1} steps)", stepSize, nSteps))
                        Catch ex2 As System.Exception
                            Log("  FixedTimeIncrement failed: " & ex2.Message)
                        End Try

                        ' Stepping-method selector (Int32). Earlier code flipped
                        ' to the OPPOSITE of the study default, assuming default =
                        ' Automatic. That is unreliable: if the study already
                        ' defaulted to the Fixed value, flipping set it to
                        ' Automatic, and the solver ran adaptive (symptom: CSV
                        ' row count << requested steps, non-uniform increments).
                        ' DETERMINISTIC FIX (2026-06-14): try BOTH candidate
                        ' values explicitly and keep the one whose read-back
                        ' sticks to a Fixed setting. We treat the value that
                        ' coexists with our FixedTimeIncrement as Fixed. We set
                        ' the value, re-assert FixedTimeIncrement, and log both.
                        ' On SW2014 the Fixed selector is conventionally 1, so we
                        ' try 1 first, then 0 as fallback.
                        Try
                            Dim tiBefore As Integer = opts.TimeIncrement
                            Dim applied As Integer = -1
                            For Each candidate As Integer In New Integer() {1, 0}
                                opts.TimeIncrement = candidate
                                ' Re-assert fixed step size after switching mode
                                Try : opts.FixedTimeIncrement = stepSize : Catch : End Try
                                Dim rb As Integer = opts.TimeIncrement
                                Log(String.Format("  TimeIncrement try {0} -> read-back {1}", candidate, rb))
                                If rb = candidate Then
                                    applied = candidate
                                    ' candidate 1 is the conventional Fixed value;
                                    ' accept it and stop. If only 0 sticks, accept
                                    ' that (some builds invert), but keep going to
                                    ' prefer 1 when both stick.
                                    If candidate = 1 Then Exit For
                                End If
                            Next
                            ' Ensure we end on the Fixed-intended value (prefer 1)
                            opts.TimeIncrement = 1
                            Try : opts.FixedTimeIncrement = stepSize : Catch : End Try
                            Dim finalRb As Integer = opts.TimeIncrement
                            Log(String.Format("  TimeIncrement FINAL = {0} (was default {1}); FixedTimeIncrement = {2:F6} ({3} steps)", _
                                finalRb, tiBefore, stepSize, nSteps))
                            If finalRb <> 1 Then
                                Log("  WARNING: could not confirm Fixed(=1); validator will flag if stepping is not fixed.")
                                Log("  *** If run 1 shows non-uniform steps: Study Properties -> Stepping -> Fixed -> OK ***")
                            Else
                                Log("  Stepping = FIXED confirmed")
                            End If
                        Catch ex2 As System.Exception
                            Log("  TimeIncrement failed: " & ex2.Message)
                            Log("  *** MANUAL: Study Properties -> click 'Fixed' radio -> OK ***")
                        End Try
                    Else
                        Log("  NonLinearStudyOptions returned Nothing")
                        Log("  *** SET MANUALLY: Study -> Properties ***")
                        Log(String.Format("  Large displacement ON, Fixed, {0} steps", nSteps))
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
        ' Helper — resolve material database FULL PATHS.
        ' SetLibraryMaterial requires the full path to the .sldmat file,
        ' NOT the display name. (Display names were the prior failure mode.)
        ' Custom DBs containing 'spider' or 'edge' are tried first.
        ' ──────────────────────────────────────────────────────────────
        Private _matDbPaths As List(Of String) = Nothing

        Private Function ResolveMaterialDbPaths() As List(Of String)
            If _matDbPaths IsNot Nothing Then Return _matDbPaths
            Dim result As New List(Of String)()
            Dim others As New List(Of String)()
            Try
                Dim dbs As Object = _swApp.GetMaterialDatabases()
                If dbs IsNot Nothing Then
                    For Each o As Object In DirectCast(dbs, Object())
                        Dim p As String = TryCast(o, String)
                        If String.IsNullOrEmpty(p) Then Continue For
                        Dim low As String = p.ToLower()
                        If low.Contains("spider") OrElse low.Contains("edge") Then
                            result.Add(p)
                        Else
                            others.Add(p)
                        End If
                    Next
                End If
            Catch ex As System.Exception
                Log("  GetMaterialDatabases failed: " & ex.Message)
            End Try
            result.AddRange(others)
            ' Last-resort known default location
            result.Add("C:\Program Files\SolidWorks Corp\SolidWorks\lang\english\sldmaterials\solidworks materials.sldmat")
            Log(String.Format("  Material DBs resolved: {0}", result.Count))
            For Each p As String In result
                Log("    " & p)
            Next
            _matDbPaths = result
            Return result
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Helper — SetLibraryMaterial on any cosworks target object,
        ' iterating resolved full .sldmat paths. Returns True on success.
        ' ──────────────────────────────────────────────────────────────
        Private Function ApplyLibraryMaterialTo(ByVal target As Object, _
                                                ByVal profile As SpiderProfile, _
                                                ByVal label As String) As Boolean
            For Each dbPath As String In ResolveMaterialDbPaths()
                Try
                    Dim ret As Object = CallByName(target, "SetLibraryMaterial", CallType.Method, _
                        dbPath, profile.MaterialName)
                    Dim ok As Boolean = True
                    Try
                        If TypeOf ret Is Boolean Then ok = CBool(ret)
                    Catch : End Try
                    If ok Then
                        Log(String.Format("  {0} SetLibraryMaterial OK: '{1}' from", label, profile.MaterialName))
                        Log("    " & dbPath)
                        ' HYPERELASTIC (_MR) cards: this object (SolidComponent/SolidBody) is
                        ' what the STUDY TREE material node maps to — the object the manual
                        ' "choose Mooney" click edits. SetLibraryMaterial loads the card but
                        ' SW defaults the model to Linear Elastic (E=2e9 stand-in). Select the
                        ' Mooney model HERE, on the study-tree object, via late binding
                        ' (these types are not in the reflected dump). Try the getter names,
                        ' set ModelType=5 + 2 constants, write back if a setter exists.
                        If profile.MaterialName.EndsWith("_MR", StringComparison.OrdinalIgnoreCase) Then
                            Dim matObj As Object = Nothing
                            For Each getter As String In New String() {"GetSolidMaterial", "GetMaterial", "GetDefaultMaterial"}
                                Try
                                    matObj = CallByName(target, getter, CallType.Method)
                                    If matObj IsNot Nothing Then
                                        Log(String.Format("    {0}.{1}: material object obtained", label, getter))
                                        Exit For
                                    End If
                                Catch : End Try
                            Next
                            If matObj IsNot Nothing Then
                                Dim mtBack As Integer = -1
                                Try
                                    CallByName(matObj, "ModelType", CallType.Set, 7)   ' 7=Mooney-Rivlin (probe-verified 2026-07-08)
                                    Try : CallByName(matObj, "MooneyRivlinConstants", CallType.Set, 2) : Catch : End Try
                                    Try : mtBack = CInt(CallByName(matObj, "ModelType", CallType.Get)) : Catch : End Try
                                    ' write back if a setter exists on this object
                                    For Each setter As String In New String() {"SetSolidMaterial", "SetMaterial"}
                                        Try
                                            CallByName(target, setter, CallType.Method, matObj)
                                            Log(String.Format("    {0}.{1}: material written back", label, setter))
                                            Exit For
                                        Catch : End Try
                                    Next
                                    Log(String.Format("    {0} HYPERELASTIC: ModelType set=5 read-back={1}", label, mtBack))
                                Catch exM As System.Exception
                                    Log(String.Format("    {0} HYPERELASTIC ModelType failed: {1}", label, exM.Message))
                                End Try
                            Else
                                Log(String.Format("    {0}: no material getter found (tried GetSolidMaterial/GetMaterial/GetDefaultMaterial)", label))
                            End If
                        End If
                        Return True
                    End If
                Catch
                End Try
            Next
            Log(String.Format("  {0} SetLibraryMaterial: no DB accepted '{1}'", label, profile.MaterialName))
            Return False
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Shell material via library DB (typed, SW2014-verified signature:
        ' SetLibraryMaterial(SLibraryPathName, SMaterialName) As Int32).
        ' Success is judged by READ-BACK, not by the return code.
        ' ──────────────────────────────────────────────────────────────
        Private Function ApplyShellLibraryMaterial(ByVal sh As CWShell, _
                                                   ByVal profile As SpiderProfile) As Boolean
            ' Hyperelastic cards (name ends "_MR") need the model TYPE selected after
            ' the library material is loaded — SetLibraryMaterial loads the card but SW
            ' defaults the model to Linear Elastic (the 2 GPa fallback seen in the log).
            ' This is the code equivalent of choosing "Hyperelastic - Mooney Rivlin" in
            ' the study material dialog. Verified API (SimAPI dump): CWShell.GetShellMaterial
            ' -> CWMaterial.ModelType (set) / .MooneyRivlinConstants. C10/C01/Nu/Density
            ' come from the card; nothing is entered here.
            Dim isHyper As Boolean = profile.MaterialName.EndsWith("_MR", StringComparison.OrdinalIgnoreCase)
            For Each dbPath As String In ResolveMaterialDbPaths()
                Try
                    Dim ret As Integer = sh.SetLibraryMaterial(dbPath, profile.MaterialName)
                    If isHyper Then
                        Try
                            Dim mat As CWMaterial = sh.GetShellMaterial()
                            If mat IsNot Nothing Then
                                LogMooneyProps("    card state after SetLibraryMaterial", mat)
                                ' ModelType=7 = Hyperelastic Mooney-Rivlin. VERIFIED by probe
                                ' (2026-07-08): manual dialog selection of Mooney on this study
                                ' read back ModelType=7, MooneyRivlinConstants=2. The 5 used
                                ' previously was wrong (unverified guess) — root cause of the
                                ' linear solves and the 19:07 solver hang.
                                mat.ModelType = 7
                                Try : mat.MooneyRivlinConstants = 2 : Catch : End Try  ' 2-constant (C10+C01)
                                ' CRITICAL: GetShellMaterial returns a detached copy. Mutating it
                                ' does NOT change what the shell solves with — the change only
                                ' reaches the solve if written back via SetShellMaterial. Without
                                ' this the tree shows the right NAME but solves LINEAR (the 2 GPa /
                                ' 1333 MPa result), even though ModelType read-back on the copy = 5.
                                Dim setRet As Integer = -999
                                Try
                                    setRet = sh.SetShellMaterial(DirectCast(mat, Object))
                                Catch exSet As System.Exception
                                    Log("    SetShellMaterial threw: " & exSet.Message)
                                End Try
                                ' Verify against the shell's ACTUAL material after write-back
                                Dim mtBack As Integer = -1
                                Dim nmrBack As Integer = -1
                                Try
                                    Dim matChk As CWMaterial = sh.GetShellMaterial()
                                    mtBack = matChk.ModelType
                                    Try : nmrBack = matChk.MooneyRivlinConstants : Catch : End Try
                                    LogMooneyProps("    committed material", matChk)
                                Catch : End Try
                                Log(String.Format("  Shell HYPERELASTIC '{0}': SetShellMaterial ret={1}, shell ModelType now={2} (7=Mooney-Rivlin, probe-verified), constants={3}", _
                                    profile.MaterialName, setRet, mtBack, nmrBack))
                                ' Strict gate: the manual known-good state reads ModelType=7
                                ' AND MooneyRivlinConstants=2. Anything else is not the good
                                ' state — fail loudly rather than solve wrong physics.
                                If mtBack = 7 AndAlso nmrBack = 2 Then
                                    Log("    from " & dbPath)
                                    Return True
                                End If
                            End If
                        Catch exH As System.Exception
                            Log("    hyperelastic ModelType set failed: " & exH.Message)
                        End Try
                    Else
                        If VerifyShellMaterial(sh, profile) Then
                            Log(String.Format("  Shell SetLibraryMaterial '{0}' VERIFIED (ret={1}) from", _
                                profile.MaterialName, ret))
                            Log("    " & dbPath)
                            Return True
                        End If
                    End If
                Catch
                End Try
            Next
            Log(String.Format("  Shell SetLibraryMaterial: no DB verified '{0}'", profile.MaterialName))
            Return False
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Read back the shell material and verify it UNIT-INDEPENDENTLY:
        '   - NUXY is dimensionless: must match profile.Nu
        '   - GXY/EX must equal 1/(2(1+Nu)) regardless of unit system
        '     (this is the stale-G hazard gate)
        ' EX is logged raw for the human sanity check.
        ' ──────────────────────────────────────────────────────────────
        Private Function VerifyShellMaterial(ByVal sh As CWShell, _
                                             ByVal profile As SpiderProfile) As Boolean
            Try
                Dim mat As CWMaterial = sh.GetShellMaterial()
                If mat Is Nothing Then
                    Log("    read-back: GetShellMaterial returned Nothing")
                    Return False
                End If
                Dim matName As String = ""
                Try : matName = mat.MaterialName : Catch : End Try

                Dim tdep As Integer = 0
                Dim exV As Double = mat.GetPropertyByName(0, "EX", tdep)
                Dim nuV As Double = mat.GetPropertyByName(0, "NUXY", tdep)
                Dim gxV As Double = mat.GetPropertyByName(0, "GXY", tdep)
                Log(String.Format("    read-back: '{0}'  EX={1:G6}  NUXY={2:F4}  GXY={3:G6}", _
                    matName, exV, nuV, gxV))

                If Math.Abs(nuV - profile.Nu) > 0.005 Then
                    Log(String.Format("    Nu mismatch: read {0:F4} vs profile {1:F4}", nuV, profile.Nu))
                    Return False
                End If
                ' ABSOLUTE E check (2026-08-18): Nu and G/E ratio alone cannot catch a
                ' card whose E differs from the row (both scale together). Compare EX
                ' against the row's E (MPa -> Pa) within 1%.
                If exV > 0.0 Then
                    Dim expE_Pa As Double = profile.E * 1000000.0
                    If Math.Abs(exV - expE_Pa) / expE_Pa > 0.01 Then
                        Log(String.Format("    E mismatch: read {0:G6} Pa vs row {1:G6} Pa", exV, expE_Pa))
                        Return False
                    End If
                End If
                If exV > 0.0 AndAlso gxV > 0.0 Then
                    Dim ratio As Double = gxV / exV
                    Dim expected As Double = 1.0 / (2.0 * (1.0 + nuV))
                    Dim rel As Double = Math.Abs(ratio - expected) / expected
                    If rel > 0.02 Then
                        Log(String.Format("    STALE-G: G/E={0:F4} expected {1:F4} ({2:F1}% off)", _
                            ratio, expected, rel * 100.0))
                        Return False
                    End If
                    Log(String.Format("    G/E ratio OK: {0:F4} (expected {1:F4})", ratio, expected))
                Else
                    Log("    EX/GXY read-back non-positive — cannot verify G")
                    Return False
                End If
                Return True
            Catch ex As System.Exception
                Log("    read-back failed: " & ex.Message)
                Return False
            End Try
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Helper — log the Mooney/linear property state of a material
        ' object via GetPropertyByName. Property names MOONEY_A/MOONEY_B
        ' verified verbatim from Custom Materials.sldmat (the _MR card's
        ' XML property elements). NaN = property not readable.
        ' ──────────────────────────────────────────────────────────────
        Private Sub LogMooneyProps(ByVal prefix As String, ByVal mat As CWMaterial)
            If mat Is Nothing Then Return
            Try
                Dim tdep As Integer = 0
                Dim aV As Double, bV As Double, exV As Double, nuV As Double
                Try : aV = mat.GetPropertyByName(0, "MOONEY_A", tdep) : Catch : aV = Double.NaN : End Try
                Try : bV = mat.GetPropertyByName(0, "MOONEY_B", tdep) : Catch : bV = Double.NaN : End Try
                Try : exV = mat.GetPropertyByName(0, "EX", tdep) : Catch : exV = Double.NaN : End Try
                Try : nuV = mat.GetPropertyByName(0, "NUXY", tdep) : Catch : nuV = Double.NaN : End Try
                Log(String.Format("{0}: MOONEY_A={1:G6}  MOONEY_B={2:G6}  EX={3:G6}  NUXY={4:F4}", _
                    prefix, aV, bV, exV, nuV))
            Catch : End Try
        End Sub

        ' ──────────────────────────────────────────────────────────────
        ' DIAGNOSTIC — Probe the ACTIVE study's shell material state.
        ' Read-only; changes nothing. Intended use: run Setup, manually
        ' select Hyperelastic - Mooney Rivlin in the study material
        ' dialog (the known-good manual state), click Probe. The output
        ' gives ground truth for:
        '   (a) the real ModelType enum value for Mooney-Rivlin
        '       (the 5 used in ApplyShellLibraryMaterial is UNVERIFIED)
        '   (b) whether MOONEY_A/MOONEY_B and MooneyRivlinConstants are
        '       visible on the CWShell material object at all when the
        '       manual selection is in effect. If the good state shows
        '       nothing different here, CWShell is the wrong target.
        ' ──────────────────────────────────────────────────────────────
        Public Sub ProbeShellMaterial()
            Try
                If Not ReconnectStudy() Then Return
                Log("========================================")
                Log("  SHELL MATERIAL STATE PROBE (read-only)")
                Log("========================================")
                Try : Log("  Study: '" & _study.Name & "'") : Catch : End Try
                Dim shellMgr As CWShellManager = _study.ShellManager
                If shellMgr Is Nothing OrElse shellMgr.ShellCount < 1 Then
                    Log("  No shells in ShellManager — nothing to probe")
                    Return
                End If
                For si As Integer = 0 To shellMgr.ShellCount - 1
                    Dim shErr As Integer = 0
                    Dim sh As CWShell = shellMgr.GetShellAt(si, shErr)
                    If sh Is Nothing Then
                        Log(String.Format("  Shell {0}: GetShellAt returned Nothing (err={1})", si, shErr))
                        Continue For
                    End If
                    Dim mat As CWMaterial = Nothing
                    Try
                        mat = sh.GetShellMaterial()
                    Catch ex As System.Exception
                        Log(String.Format("  Shell {0}: GetShellMaterial threw: {1}", si, ex.Message))
                    End Try
                    If mat Is Nothing Then
                        Log(String.Format("  Shell {0}: GetShellMaterial returned Nothing", si))
                        Continue For
                    End If
                    Log(String.Format("  Shell {0}:", si))
                    Try
                        Log("    MaterialName          = '" & mat.MaterialName & "'")
                    Catch ex As System.Exception
                        Log("    MaterialName          read failed: " & ex.Message)
                    End Try
                    Try
                        Log("    ModelType             = " & mat.ModelType.ToString())
                    Catch ex As System.Exception
                        Log("    ModelType             read failed: " & ex.Message)
                    End Try
                    Try
                        Log("    MooneyRivlinConstants = " & mat.MooneyRivlinConstants.ToString())
                    Catch ex As System.Exception
                        Log("    MooneyRivlinConstants read failed: " & ex.Message)
                    End Try
                    For Each pname As String In New String() {"MOONEY_A", "MOONEY_B", "EX", "NUXY", "GXY", "DENS"}
                        Try
                            Dim tdep As Integer = 0
                            Dim v As Double = mat.GetPropertyByName(0, pname, tdep)
                            Log(String.Format("    {0,-9} = {1:G6}", pname, v))
                        Catch ex As System.Exception
                            Log(String.Format("    {0,-9} read failed: {1}", pname, ex.Message))
                        End Try
                    Next
                Next
                Log("========================================")
                Log("  MATERIAL PROBE COMPLETE")
                Log("========================================")
            Catch ex As System.Exception
                Log("MATERIAL PROBE ERROR: " & ex.Message)
            End Try
        End Sub

        ' ──────────────────────────────────────────────────────────────
        ' Fallback — custom in-code shell material with EXPLICIT E/Nu/G/Density.
        ' Sets G = E/(2(1+Nu)) directly — immune to the stale-G card hazard.
        ' Typed calls verified against the SW2014 interop dump:
        '   GetDefaultMaterial() As CWMaterial
        '   SetPropertyByName(SName, DValue, BValue As Int32)
        '   SetShellMaterial(MaterialDisp) As Int32
        ' UNIT ASSUMPTION (flagged): profile.E is MPa -> EX passed as N/m^2
        ' (E*1e6) with MaterialUnits=0. Verified on run 1 via the Kms0 gate;
        ' a wrong unit system is off by orders of magnitude and unmissable.
        ' ──────────────────────────────────────────────────────────────
        Private Function ApplyCustomShellMaterial(ByVal sh As CWShell, _
                                                  ByVal profile As SpiderProfile) As Boolean
            Try
                Log("  Trying custom in-code material (explicit E/Nu/G/Density)...")
                Dim mat As CWMaterial = sh.GetDefaultMaterial()
                If mat Is Nothing Then
                    Log("    GetDefaultMaterial returned Nothing")
                    Return False
                End If

                Dim E_SI As Double = profile.E * 1000000.0          ' MPa -> N/m^2
                Dim G_SI As Double = E_SI / (2.0 * (1.0 + profile.Nu))

                Try : mat.MaterialName = profile.MaterialName & "_auto" : Catch : End Try
                Try : mat.MaterialUnits = 0 : Catch : End Try

                Try
                    mat.SetPropertyByName("EX", E_SI, 0)
                    mat.SetPropertyByName("NUXY", profile.Nu, 0)
                    mat.SetPropertyByName("GXY", G_SI, 0)
                    mat.SetPropertyByName("DENS", profile.Density, 0)
                    Log(String.Format("    set EX={0:G6} NUXY={1:F4} GXY={2:G6} DENS={3}", _
                        E_SI, profile.Nu, G_SI, profile.Density))
                Catch ex As System.Exception
                    Log("    SetPropertyByName failed: " & ex.Message)
                    Return False
                End Try

                sh.ShellBeginEdit()
                Dim ret As Integer = -999
                Try
                    ret = sh.SetShellMaterial(DirectCast(mat, Object))
                Catch ex As System.Exception
                    Log("    SetShellMaterial failed: " & ex.Message)
                End Try
                Try : sh.ShellEndEdit() : Catch : End Try

                Dim verified As Boolean = VerifyShellMaterial(sh, profile)
                Log(String.Format("    custom material: SetShellMaterial ret={0}, verified={1}", ret, verified))
                Return verified
            Catch ex As System.Exception
                Log("  Custom material failed: " & ex.Message)
                Return False
            End Try
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Helper — IDispatch invoke with ByRef out-args (CallByName can't
        ' return ByRef values; InvokeMember on __ComObject can).
        ' ──────────────────────────────────────────────────────────────
        Private Function InvokeByRef(ByVal target As Object, ByVal name As String, _
                                     ByRef args() As Object) As Object
            Return target.GetType().InvokeMember(name, _
                BindingFlags.InvokeMethod, Nothing, target, args)
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Create FINEST standard mesh, high-quality (quadratic) elements.
        ' Standard mesher is the permanent campaign standard (curvature-based
        ' caused ~10x stiffness reduction on shells). Finest = 0.5x default
        ' global element size. Falls back to RunAnalysis auto-mesh on failure.
        ' ──────────────────────────────────────────────────────────────
        Public Function CreateFinestMesh() As Boolean
            _solidMeshSkip = False
            Log("  CreateFinestMesh: entered")
            If Not ReconnectStudy() Then Return False
            Try
                Dim mesh As CWMesh = _study.Mesh
                If mesh Is Nothing Then
                    Log("  No CWMesh object — auto-mesh fallback")
                    Return False
                End If

                ' Typed, SW2014-verified: MesherType / Quality / 
                ' GetDefaultElementSizeAndTolerance / Study.CreateMesh
                Try
                    mesh.MesherType = 0          ' standard mesher (campaign standard)
                    Log("  mesh.MesherType = 0 (standard)")
                Catch ex As System.Exception
                    Log("  MesherType set failed: " & ex.Message)
                End Try
                Try
                    mesh.Quality = 1             ' high quality (quadratic)
                    Log("  mesh.Quality = 1 (high/quadratic)")
                Catch ex As System.Exception
                    Log("  Quality set failed: " & ex.Message)
                End Try

                Dim unit As Integer = CInt(swsLinearUnit_e.swsLinearUnitMillimeters)
                Dim fineEl As Double = 0.0
                Dim fineTol As Double = 0.0

                If _solidMesh AndAlso _wallT > 0.0 Then
                    ' VERIFIED (SpiderSW_debug.log, RunID 27/28, T=1.2): _study.CreateMesh
                    ' HANGS at el=wall for this thin corrugated solid — at the THICKEST
                    ' wall in the batch, so it is not a thickness problem. One quadratic
                    ' element through a thin wall fails the mesher's element checks;
                    ' el=wall/2 (two elements through the wall) re-triggers the in-plane
                    ' tet explosion over the OD ring. Draft/linear meshes but is too
                    ' stiff for Kms0 (ruled out). There is no solid mesh for this
                    ' geometry in SW Simulation's mesher. Skip ALL solid rows so the
                    ' batch never locks SW; high-T/R stiffness recovery moves to SD
                    ' (analytical), not solid FEA.
                    _solidMeshSkip = True
                    Log(String.Format("  SOLID: wall={0:F3}mm — SW mesher hangs on this thin solid at any T; skipping row (recovery moves to SD analytical).", _wallT))
                    Return False
                End If
                If Not _solidMesh Then
                    ' ── SHELL path (unchanged; solids skipped and returned above) ─
                    Dim elSize As Double = 0.0
                    Dim tol As Double = 0.0
                    Try
                        mesh.GetDefaultElementSizeAndTolerance(unit, elSize, tol)
                        Log(String.Format("  Default mesh: el={0:F4}mm tol={1:F5}mm", elSize, tol))
                    Catch ex As System.Exception
                        Log("  GetDefaultElementSizeAndTolerance failed: " & ex.Message)
                        Return False
                    End Try
                    If elSize <= 0.0 Then
                        Log("  Default element size non-positive — auto-mesh fallback")
                        Return False
                    End If
                    fineEl = elSize * _meshScale
                    fineTol = tol * _meshScale
                    Log(String.Format("  MeshScale = {0:F3} (0.5 = campaign standard)", _meshScale))
                End If

                Log(String.Format("  Creating FINEST mesh: el={0:F4}mm tol={1:F5}mm ...", fineEl, fineTol))
                Try
                    Dim meshErr As Integer = _study.CreateMesh(unit, fineEl, fineTol)
                    Log(String.Format("  CreateMesh returned {0}", meshErr))
                    Try
                        Log(String.Format("  Mesh: {0} nodes, {1} elements", _
                            mesh.NodeCount, mesh.ElementCount))
                    Catch : End Try
                    Return (meshErr = 0)
                Catch ex As System.Exception
                    Log("  CreateMesh failed: " & ex.Message)
                    Return False
                End Try
            Catch ex As System.Exception
                Log("  CreateFinestMesh: " & ex.Message)
                Return False
            End Try
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Diagnostic — dump ALL Simulation interop type members to a file.
        ' Reflects the cosworks interop assembly METADATA (not the COM RCW),
        ' so this gives the exact SW2014 property/method names for shell
        ' material assignment, nonlinear stepping flags, mesh control, etc.
        ' ──────────────────────────────────────────────────────────────
        Public Sub DumpSimApiMembers(ByVal outPath As String)
            Try
                Dim asmCos As System.Reflection.Assembly = GetType(CWStudy).Assembly
                Dim keys() As String = New String() { _
                    "shell", "material", "mesh", "nonlinear", "studyoptions", _
                    "restraint", "results", "study"}
                Dim sb As New StringBuilder()
                sb.AppendLine("SolidWorks Simulation interop member dump")
                sb.AppendLine("Assembly: " & asmCos.FullName)
                sb.AppendLine("Generated: " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                sb.AppendLine(New String("="c, 70))
                For Each t As Type In asmCos.GetTypes()
                    Dim tn As String = t.Name.ToLower()
                    Dim match As Boolean = False
                    For Each k As String In keys
                        If tn.Contains(k) Then match = True : Exit For
                    Next
                    If Not match Then Continue For
                    If Not t.IsInterface Then Continue For
                    sb.AppendLine()
                    sb.AppendLine("TYPE: " & t.Name)
                    sb.AppendLine(New String("-"c, 50))
                    For Each m As MemberInfo In t.GetMembers()
                        Select Case m.MemberType
                            Case MemberTypes.Property
                                Dim pi As PropertyInfo = DirectCast(m, PropertyInfo)
                                sb.AppendLine(String.Format("  PROP   {0} : {1}{2}{3}", _
                                    pi.Name, pi.PropertyType.Name, _
                                    If(pi.CanRead, " get", ""), If(pi.CanWrite, " set", "")))
                            Case MemberTypes.Method
                                Dim mi As MethodInfo = DirectCast(m, MethodInfo)
                                If mi.IsSpecialName Then Continue For
                                Dim ps As New StringBuilder()
                                For Each p As ParameterInfo In mi.GetParameters()
                                    If ps.Length > 0 Then ps.Append(", ")
                                    ps.Append(If(p.ParameterType.IsByRef, "ByRef ", ""))
                                    ps.Append(p.Name & " As " & p.ParameterType.Name.Replace("&", ""))
                                Next
                                sb.AppendLine(String.Format("  METHOD {0}({1}) : {2}", _
                                    mi.Name, ps.ToString(), mi.ReturnType.Name))
                        End Select
                    Next
                Next
                System.IO.File.WriteAllText(outPath, sb.ToString())
                Log("Sim API member dump written: " & outPath)
            Catch ex As System.Exception
                Log("DumpSimApiMembers failed: " & ex.Message)
            End Try
        End Sub

        ' ──────────────────────────────────────────────────────────────
        ' SolidWorks process lifecycle — for unattended batch operation.
        ' SW2014 accumulates memory over long COM sessions; the batch
        ' engine restarts it every N runs.
        ' ──────────────────────────────────────────────────────────────
        Public Function ConnectOrStart() As Boolean
            Dim connected As Boolean = Connect()
            If Not connected Then
                ' No healthy registered instance answered. Any SLDWORKS process
                ' still present is a zombie (e.g. from a hard kill) and will
                ' block COM activation of a new instance — sweep it first.
                Try
                    For Each pr As System.Diagnostics.Process In _
                        System.Diagnostics.Process.GetProcessesByName("SLDWORKS")
                        Try
                            Log("  Killing stale SLDWORKS pid " & pr.Id)
                            pr.Kill()
                            pr.WaitForExit(10000)
                        Catch : End Try
                    Next
                Catch : End Try
                System.Threading.Thread.Sleep(2000)

                Log("Starting new SolidWorks instance...")
                Try
                    _swApp = DirectCast(CreateObject("SldWorks.Application"), SldWorks)
                    If _swApp Is Nothing Then
                        Log("ERROR: CreateObject SldWorks failed.")
                        Return False
                    End If
                    _swApp.Visible = True
                    Log("SolidWorks started: " & _swApp.RevisionNumber())
                    connected = True
                Catch ex As System.Exception
                    Log("ERROR starting SolidWorks: " & ex.Message)
                    Return False
                End Try
            End If
            ' Do not declare readiness until the Simulation add-in answers —
            ' a fresh instance loads it lazily, long after the window appears.
            Return GetCosmosRetry(120)
        End Function

        Public Sub ShutdownSolidWorks()
            ' Release COM references first.
            Try
                _study = Nothing : _cwDoc = Nothing : _cw = Nothing
                _idEdge = Nothing : _odEdge = Nothing
                If _model IsNot Nothing Then
                    Try : _swApp.CloseDoc(_model.GetPathName()) : Catch : End Try
                    _model = Nothing
                End If
                If _swApp IsNot Nothing Then
                    Try : _swApp.CloseAllDocuments(True) : Catch : End Try
                    Try : _swApp.ExitApp() : Catch : End Try
                End If
            Catch : End Try
            _swApp = Nothing
            Try
                System.Runtime.InteropServices.Marshal.CleanupUnusedObjectsInCurrentContext()
                GC.Collect()
                GC.WaitForPendingFinalizers()
            Catch : End Try

            ' ExitApp can raise a modal "save changes?" / rebuild dialog that
            ' hangs the process forever — and the per-run watchdog is not
            ' covering this window. Run a short local dismiss loop while we
            ' wait for graceful exit, THEN hard-kill anything still alive.
            ' Verified failure mode 2026-06-12: restart died at the run-12
            ' boundary because a wedged SLDWORKS held the COM/journal lock.
            Dim deadline As DateTime = DateTime.Now.AddSeconds(15)
            While DateTime.Now < deadline
                Try
                    EnumWindows(AddressOf WatchdogEnum, IntPtr.Zero)
                Catch
                End Try
                If System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length = 0 Then Exit While
                Thread.Sleep(500)
            End While

            ' Decisive kill — do not wait politely, the process already had its
            ' chance to exit gracefully above.
            KillAllSolidWorks()
            Log("SolidWorks shut down.")
        End Sub

        ' Kill every SLDWORKS process and confirm they are gone. Returns True
        ' when the process table is clear.
        Public Function KillAllSolidWorks() As Boolean
            Try
                For Each pr As System.Diagnostics.Process In _
                    System.Diagnostics.Process.GetProcessesByName("SLDWORKS")
                    Try
                        Log("  Killing SLDWORKS pid " & pr.Id)
                        pr.Kill()
                    Catch : End Try
                Next
            Catch : End Try
            ' Verify clear — wait up to 15s for the OS to reap them.
            Dim deadline As DateTime = DateTime.Now.AddSeconds(15)
            While DateTime.Now < deadline
                If System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length = 0 Then
                    Return True
                End If
                Thread.Sleep(500)
            End While
            Dim remaining As Integer = System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length
            If remaining > 0 Then
                Log(String.Format("  WARNING: {0} SLDWORKS process(es) still present after kill", remaining))
            End If
            Return (remaining = 0)
        End Function

        Public Function RestartSolidWorks() As Boolean
            Log("── Restarting SolidWorks (memory hygiene) ──")
            ShutdownSolidWorks()

            ' Guarantee a clean process table before relaunching — a lingering
            ' wedged instance holds the COM registration and journal lock and
            ' is the documented cause of relaunch failure.
            If Not KillAllSolidWorks() Then
                Log("  Clean-slate not reached; waiting longer...")
                Thread.Sleep(10000)
                KillAllSolidWorks()
            End If
            Thread.Sleep(5000)

            ' Relaunch with up to 3 attempts — cold starts occasionally fail
            ' to register COM on the first try.
            For attempt As Integer = 1 To 3
                Log(String.Format("  Relaunch attempt {0}/3...", attempt))
                If ConnectOrStart() Then
                    Log("  SolidWorks restarted OK.")
                    Return True
                End If
                Log("  Attempt failed; clearing and retrying...")
                KillAllSolidWorks()
                Thread.Sleep(8000)
            Next
            Log("  ERROR: SolidWorks restart failed after 3 attempts.")
            Return False
        End Function

        ' ──────────────────────────────────────────────────────────────
        ' Best-effort view captures for batch runs (.jpg). Captures the
        ' CURRENT display state (post-run this is typically the active
        ' result plot). Side (*Front) and Top (*Top) views.
        ' ──────────────────────────────────────────────────────────────
        Public Sub SaveViewImages(ByVal basePathNoSuffix As String)
            If _model Is Nothing Then Return
            Try
                SaveOneView("*Front", basePathNoSuffix & "_side_stepLast.jpg")
                SaveOneView("*Top", basePathNoSuffix & "_top_stepLast.jpg")
                SaveOneView("*Front", "")   ' restore side view
            Catch ex As System.Exception
                Log("  View capture failed: " & ex.Message)
            End Try
        End Sub

        Private Sub SaveOneView(ByVal viewName As String, ByVal jpgPath As String)
            Try
                _model.ShowNamedView2(viewName, -1)
                _model.ViewZoomtofit2()
                System.Windows.Forms.Application.DoEvents()
                If jpgPath.Length = 0 Then Return
                Dim bmpPath As String = jpgPath & ".tmp.bmp"
                If _model.SaveBMP(bmpPath, 0, 0) Then
                    Try
                        Using bmp As System.Drawing.Bitmap = New System.Drawing.Bitmap(bmpPath)
                            bmp.Save(jpgPath, System.Drawing.Imaging.ImageFormat.Jpeg)
                        End Using
                        Log("  JPG: " & jpgPath)
                    Catch ex As System.Exception
                        Log("  BMP->JPG convert failed: " & ex.Message)
                    End Try
                    Try : System.IO.File.Delete(bmpPath) : Catch : End Try
                End If
            Catch ex As System.Exception
                Log("  SaveOneView(" & viewName & "): " & ex.Message)
            End Try
        End Sub

        ' ──────────────────────────────────────────────────────────────
        ' DIALOG WATCHDOG — auto-dismisses modal solver popups during
        ' RunAnalysis (e.g. "Solution failure in a Step>1" at a buckling /
        ' limit point) so unattended batches don't freeze. Enumerates
        ' top-level dialog windows and posts Enter (default = OK), keeping
        ' whatever steps converged. Pure Win32, no SW enum dependency.
        ' ──────────────────────────────────────────────────────────────
        ' Win32 (Declare syntax — robust on VS2010/.NET4)
        Private Declare Auto Function EnumWindows Lib "user32.dll" ( _
            ByVal cb As EnumWindowsProc, ByVal l As IntPtr) As Boolean
        Private Declare Auto Function GetWindowThreadProcessId Lib "user32.dll" ( _
            ByVal hWnd As IntPtr, ByRef pid As Integer) As Integer
        Private Declare Auto Function GetClassName Lib "user32.dll" ( _
            ByVal hWnd As IntPtr, ByVal sb As System.Text.StringBuilder, ByVal max As Integer) As Integer
        Private Declare Auto Function IsWindowVisible Lib "user32.dll" ( _
            ByVal hWnd As IntPtr) As Boolean
        Private Declare Auto Function PostMessage Lib "user32.dll" ( _
            ByVal hWnd As IntPtr, ByVal msg As Integer, _
            ByVal wParam As IntPtr, ByVal lParam As IntPtr) As Boolean
        Private Declare Auto Function SendMessage Lib "user32.dll" ( _
            ByVal hWnd As IntPtr, ByVal msg As Integer, _
            ByVal wParam As IntPtr, ByVal lParam As IntPtr) As IntPtr
        Private Declare Auto Function FindWindowEx Lib "user32.dll" ( _
            ByVal parent As IntPtr, ByVal childAfter As IntPtr, _
            ByVal cls As String, ByVal title As String) As IntPtr
        Private Declare Auto Function SetForegroundWindow Lib "user32.dll" ( _
            ByVal hWnd As IntPtr) As Boolean
        Private Declare Auto Function EnumChildWindows Lib "user32.dll" ( _
            ByVal parent As IntPtr, ByVal cb As EnumWindowsProc, ByVal l As IntPtr) As Boolean
        Private Declare Auto Function GetWindow Lib "user32.dll" ( _
            ByVal hWnd As IntPtr, ByVal cmd As Integer) As IntPtr
        Private Declare Auto Function GetDesktopWindow Lib "user32.dll" () As IntPtr
        Private Declare Auto Function GetWindowText Lib "user32.dll" Alias "GetWindowTextW" ( _
            ByVal hWnd As IntPtr, ByVal sb As System.Text.StringBuilder, ByVal max As Integer) As Integer
        Private Delegate Function EnumWindowsProc(ByVal hWnd As IntPtr, ByVal l As IntPtr) As Boolean

        Private Const WM_KEYDOWN As Integer = &H100
        Private Const WM_KEYUP As Integer = &H101
        Private Const WM_CLOSE As Integer = &H10
        Private Const WM_COMMAND As Integer = &H111
        Private Const BM_CLICK As Integer = &HF5
        Private Const VK_RETURN As Integer = &HD
        Private Const IDOK As Integer = 1

        Private _watchdog As Thread = Nothing
        Private _watchdogRun As Boolean = False
        Private _watchdogDismissals As Integer = 0
        Private _swProcessId As Integer = 0

        ' When True, the watchdog stays up across the whole batch (set by
        ' BatchRunner around Execute) instead of only during each RunAnalysis.
        ' This closes the gap where a buckling dialog appears between the
        ' per-run start/stop and blocks the batch.
        Public Property KeepWatchdogAlive As Boolean = False

        Public Sub StartDialogWatchdog()
            ' Idempotent — don't spawn a second thread if one is already up.
            If _watchdog IsNot Nothing AndAlso _watchdog.IsAlive Then
                RefreshSwProcessId()
                Return
            End If
            _swProcessId = 0
            RefreshSwProcessId()
            _watchdogRun = True
            _watchdog = New Thread(AddressOf WatchdogLoop)
            _watchdog.IsBackground = True
            _watchdog.Start()
        End Sub

        Public Sub StopDialogWatchdog()
            ' Honor the always-on flag: per-run stop calls become no-ops while
            ' a batch holds the watchdog open.
            If KeepWatchdogAlive Then Return
            ForceStopDialogWatchdog()
        End Sub

        Public Sub ForceStopDialogWatchdog()
            _watchdogRun = False
            Try
                If _watchdog IsNot Nothing Then _watchdog.Join(3000)
            Catch
            End Try
            _watchdog = Nothing
        End Sub

        Private Sub RefreshSwProcessId()
            Try
                ' Re-resolve each time — the pid changes after a mid-batch
                ' SolidWorks restart, and a stale pid would make the watchdog
                ' ignore the new instance's dialogs.
                For Each pr As System.Diagnostics.Process In _
                    System.Diagnostics.Process.GetProcessesByName("SLDWORKS")
                    _swProcessId = pr.Id
                    Exit For
                Next
            Catch
            End Try
        End Sub

        Private _watchdogLogPath As String = ""

        Private Sub WatchdogLoop()
            ' Poll ~0.4s continuously. Scan EVERY top-level window on the
            ' desktop; for any window belonging to the SLDWORKS process, also
            ' recurse its children. A target is any #32770 dialog OR any window
            ' whose title matches the known solver-failure captions. This makes
            ' no assumption about the dialog's parentage — it cannot be missed.
            Try
                _watchdogLogPath = "C:\SpiderSW_Results\watchdog.log"
                RefreshSwPidSet()
                WdLog("──── watchdog start ──── sw pids=" & If(_swPidSet IsNot Nothing, _swPidSet.Count, 0))
            Catch
            End Try
            Dim cycles As Integer = 0
            While _watchdogRun
                Try
                    EnumWindows(AddressOf WatchdogEnum, IntPtr.Zero)
                Catch
                End Try
                cycles += 1
                If cycles >= 12 Then
                    RefreshSwProcessId()
                    RefreshSwPidSet()
                    cycles = 0
                End If
                Dim waited As Integer = 0
                While _watchdogRun AndAlso waited < 400
                    Thread.Sleep(100)
                    waited += 100
                End While
            End While
            WdLog("──── watchdog stop ──── dismissals=" & _watchdogDismissals)
        End Sub

        Private Sub WdLog(ByVal msg As String)
            Try
                If _watchdogLogPath.Length > 0 Then
                    System.IO.File.AppendAllText(_watchdogLogPath, _
                        DateTime.Now.ToString("HH:mm:ss.fff") & "  " & msg & System.Environment.NewLine)
                End If
            Catch
            End Try
        End Sub

        Private Function WindowText(ByVal hWnd As IntPtr) As String
            Try
                Dim sb As New System.Text.StringBuilder(512)
                GetWindowText(hWnd, sb, 512)
                Return sb.ToString()
            Catch
                Return ""
            End Try
        End Function

        Private Function WindowClass(ByVal hWnd As IntPtr) As String
            Try
                Dim cn As New System.Text.StringBuilder(256)
                GetClassName(hWnd, cn, 256)
                Return cn.ToString()
            Catch
                Return ""
            End Try
        End Function

        ' Top-level enumeration callback. For SLDWORKS-owned windows, recurse
        ' into children too, then test this window as a possible dialog.
        Private Function WatchdogEnum(ByVal hWnd As IntPtr, ByVal l As IntPtr) As Boolean
            Try
                Dim pid As Integer = 0
                GetWindowThreadProcessId(hWnd, pid)

                ' Recurse children of any SolidWorks-owned top-level window,
                ' resolved by LIVE process name (not a cached PID, which is
                ' wrong when SW was opened manually). This is what makes the
                ' watchdog work regardless of who started SolidWorks.
                If IsSolidWorksPid(pid) Then
                    Try
                        EnumChildWindows(hWnd, AddressOf WatchdogChildEnum, IntPtr.Zero)
                    Catch
                    End Try
                End If

                ' Test this top-level window itself (process-independent test)
                TryDismissIfDialog(hWnd, pid)
            Catch
            End Try
            Return True
        End Function

        ' Live check: is this pid a SLDWORKS process? Cached set, refreshed
        ' periodically by the loop, so we don't spawn process queries per window.
        Private _swPidSet As System.Collections.Generic.HashSet(Of Integer) = Nothing
        Private Function IsSolidWorksPid(ByVal pid As Integer) As Boolean
            Try
                If _swPidSet Is Nothing Then Return False
                Return _swPidSet.Contains(pid)
            Catch
            End Try
            Return False
        End Function

        Private Sub RefreshSwPidSet()
            Try
                Dim s As New System.Collections.Generic.HashSet(Of Integer)()
                For Each pr As System.Diagnostics.Process In _
                    System.Diagnostics.Process.GetProcessesByName("SLDWORKS")
                    s.Add(pr.Id)
                Next
                _swPidSet = s
            Catch
            End Try
        End Sub

        Private Function WatchdogChildEnum(ByVal hWnd As IntPtr, ByVal l As IntPtr) As Boolean
            Try
                Dim pid As Integer = 0
                GetWindowThreadProcessId(hWnd, pid)
                TryDismissIfDialog(hWnd, pid)
            Catch
            End Try
            Return True
        End Function

        Private Sub TryDismissIfDialog(ByVal hWnd As IntPtr, ByVal pid As Integer)
            If Not IsWindowVisible(hWnd) Then Return
            Dim cls As String = WindowClass(hWnd)
            If cls <> "#32770" Then Return

            ' A real solver message box ALWAYS has a clickable button child.
            ' A blank #32770 with no button is an internal SW shell — ignore.
            Dim hBtn As IntPtr = FindWindowEx(hWnd, IntPtr.Zero, "Button", Nothing)
            If hBtn = IntPtr.Zero Then Return

            ' PROCESS-INDEPENDENT (fixed 2026-06-13): never gate on the SW
            ' process id. When SW is opened MANUALLY the latched _swProcessId
            ' is wrong/stale and every real dialog was being rejected — the
            ' overnight failure. The ONLY hard exclusion is our OWN app's
            ' process (so the pause/verify MessageBox is never auto-clicked).
            ' Everything else that is a #32770-with-a-button is a solver dialog
            ' we want gone, regardless of which process or who started SW.
            Try
                If pid = System.Diagnostics.Process.GetCurrentProcess().Id Then Return
            Catch
            End Try

            ' Caption sanity: dismiss SolidWorks/solver dialogs. Match the known
            ' captions, OR a blank/SolidWorks-titled dialog (the solver boxes
            ' sometimes report an empty or generic title). This keeps us off
            ' unrelated third-party app dialogs that happen to be #32770.
            Dim title As String = WindowText(hWnd)
            Dim tl As String = title.ToLower()
            Dim isSolverDialog As Boolean = _
                tl.Contains("nonlinear") OrElse tl.Contains("solution failure") OrElse _
                tl.Contains("program aborted") OrElse tl.Contains("rotation") OrElse _
                tl.Contains("step size") OrElse tl.Contains("singularity") OrElse _
                tl.Contains("solidworks") OrElse tl.Contains("simulation") OrElse _
                tl.Contains("cosmos") OrElse tl.Length = 0
            If Not isSolverDialog Then Return

            WdLog(String.Format("dialog hWnd={0} pid={1} cls='{2}' title='{3}' -> clicking", _
                hWnd.ToInt64(), pid, cls, title))
            SendMessage(hBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero)
            _watchdogDismissals += 1
        End Sub

        ' ──────────────────────────────────────────────────────────────
        ' STEP 4 — Mesh and Run
        ' ──────────────────────────────────────────────────────────────
        Public Function MeshAndRun() As Boolean
            If Not ReconnectStudy() Then Return False

            Try
                ' Explicit FINEST standard high-quality mesh (campaign standard).
                ' On failure, fall back to RunAnalysis auto-mesh (legacy behavior).
                Dim meshed As Boolean = CreateFinestMesh()
                If _solidMeshSkip Then
                    Log("SKIP: solid mesh intractable for this row — not running.")
                    Log("  No auto-mesh fallback (that path crashed SW on thin solids). SW left idle.")
                    Log("  Route this configuration as a shell run, or run the thick-T solid rows.")
                    Return False
                End If
                If Not meshed Then
                    Log("  Finest-mesh creation unavailable — RunAnalysis will auto-mesh")
                    Log("  (run 'Dump Sim API' to lock the SW2014 mesh call names)")
                End If

                Dim mesh As CWMesh = _study.Mesh
                If mesh IsNot Nothing Then
                    Try
                        If mesh.NodeCount > 0 Then
                            Log(String.Format("  Mesh ready: {0} nodes", mesh.NodeCount))
                        End If
                    Catch
                        Log("  Mesh state unknown — proceeding to run")
                    End Try
                End If

                Log("Running study (solves; auto-meshes if no mesh)...")
                Try
                    ' Buckling/limit points raise a modal "Solution failure in a
                    ' Step>1" dialog that blocks RunAnalysis until dismissed —
                    ' fatal to unattended batches. A watchdog thread auto-clicks
                    ' it (Enter = default OK). Per the chosen policy we DISMISS
                    ' and keep whatever steps converged (PARTIAL), rather than
                    ' pushing through the instability. Partial results are saved
                    ' by the solver and the validator flags the early stop.
                    StartDialogWatchdog()
                    Dim runErr As Integer = 0
                    Try
                        runErr = _study.RunAnalysis()
                    Finally
                        ' Keep dismissing for a few seconds after the call
                        ' unwinds — the "Program aborted. Press ENTER to go
                        ' back to preprocessor" dialog can land right as
                        ' RunAnalysis returns.
                        Dim tEnd As DateTime = DateTime.Now.AddSeconds(5)
                        While DateTime.Now < tEnd
                            System.Windows.Forms.Application.DoEvents()
                            Thread.Sleep(250)
                        End While
                        StopDialogWatchdog()
                    End Try
                    Log(String.Format("RunAnalysis complete (err={0}){1}", _
                        runErr, If(_watchdogDismissals > 0, _
                        String.Format("  [watchdog dismissed {0} solver dialog(s) — likely buckling/limit point]", _watchdogDismissals), "")))
                    If runErr <> 0 Then
                        Log("WARNING: err<>0 (often the buckling stop itself — check the response).")
                    End If
                    ' err<>0 with watchdog dismissals is the buckling case: the
                    ' run is still 'successful' for extraction (partial data
                    ' exists). Return True so the pipeline extracts; the CSV
                    ' validator decides PASS vs PARTIAL from the step count.
                    Return (runErr = 0 OrElse _watchdogDismissals > 0)
                Catch ex As System.Exception
                    StopDialogWatchdog()
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
            ' Stress API diagnostic probe (kept for future API changes / debugging)
            Try
                If Not ReconnectStudy() Then Return
                Dim results As CWResults = _study.Results
                Dim mesh As CWMesh = _study.Mesh
                If results Is Nothing Then Log("PROBE: no results — solve first") : Return
                If mesh Is Nothing Then Log("PROBE: no mesh") : Return

                Dim stepNum As Integer = 1
                Try
                    Dim v As Object = CallByName(DirectCast(results, Object), "GetMaximumAvailableSteps", CallType.Method)
                    If v IsNot Nothing Then stepNum = CInt(v)
                Catch : End Try

                Log("========================================")
                Log("  STRESS API DIAGNOSTIC PROBE")
                Log("========================================")
                Log(String.Format("  stepNum={0}  mesh.NodeCount={1}", stepNum, mesh.NodeCount))

                Log("[GetMinMaxStress]  (Tier 3 fast API)")
                Try
                    Dim st As Integer = 0
                    Dim oa() As Object = DirectCast( _
                        results.GetMinMaxStress(9, -1, stepNum, Nothing, 0, st), Object())
                    Log(String.Format("    SUCCESS  len={0}  maxVal={1:F4} MPa  maxNode={2}", _
                        oa.Length, CDbl(oa(3)) / 1000000.0, CInt(oa(2))))
                Catch ex As Exception
                    Log("    FAILED: " & ex.Message)
                End Try

                Log("[GetStressComponentForAllStepsAtNode]  (per-node API)")
                Try
                    Dim st As Integer = 0
                    Dim oa() As Object = DirectCast( _
                        results.GetStressComponentForAllStepsAtNode(9, 1, Nothing, 0, st), Object())
                    If oa.Length >= 3 * stepNum Then
                        Log(String.Format("    SUCCESS  nodeID=1 step{0} VM={1:E4} Pa = {2:F4} MPa", _
                            stepNum, CDbl(oa((stepNum-1)*3+2)), CDbl(oa((stepNum-1)*3+2)) / 1000000.0))
                    End If
                Catch ex As Exception
                    Log("    FAILED: " & ex.Message)
                End Try

                Log("========================================")
                Log("  PROBE COMPLETE")
                Log("========================================")
            Catch ex As System.Exception
                Log("PROBE ERROR: " & ex.Message)
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
                Dim nodeX As New Dictionary(Of Integer, Double)()
                Dim nodeZ As New Dictionary(Of Integer, Double)()
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
                        nodeX(nid) = nx * coordScale
                        nodeZ(nid) = nz * coordScale
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
                ' Crown apex height differs by mode:
                '  - Spider rolls: apex at ±H_pp/2 (H_pp is peak-to-peak)
                '  - Edge (ComponentMode=1): H_pp IS the crown height used directly
                '    (ED contract §3: H_pp used as H_eff, no second halving), so the
                '    apex sits at ±H_pp. Using H_pp/2 here made the node-finder miss
                '    the crown and grab an attach-line node (R off, Y=0, Kms0=0).
                Dim H_eff As Double = If(profile.ComponentMode = 1, profile.H_pp, profile.H_pp / 2.0)
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

                ' HARD GATE (2026-08-18): an empty ID-edge node list means the
                ' reaction sum below is a sum over nothing — every F would be 0
                ' and the CSV would record a fake zero-stiffness run (root cause
                ' of the L1 / BF-B all-zero rows in the tau ladder). Fail the
                ' run loudly instead of writing zeros.
                If idEdgeNodeIDs.Count = 0 Then
                    Log("  *** ID edge node list EMPTY - GetDisplacementForEntities failed;")
                    Log("  *** reaction extraction impossible. FAILING run (no CSV written). ***")
                    Return False
                End If
                If idEdgeNodeIDs.Count < 60 Then
                    Log(String.Format("  *** WARNING: only {0} ID edge nodes (typical ~124+) - reaction sum may undercount ***", idEdgeNodeIDs.Count))
                End If

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
                ' FIX (2026-08-18): F_reaction now sums Fy over the ENTIRE driven
                ' ID region (all reacting nodes with R <= R_roll_start + 0.25mm),
                ' not just the ID-edge ring. The ring captures only a geometry-
                ' dependent share of the face reaction (measured 0.44 at A1/T0.42,
                ' 0.067 at T0.55) — root cause of the store-wide Kms scale defect.
                ' Verification per step: the fixed-lip OD-region sum must balance
                ' the ID sum (equilibrium). Mismatch > 0.5% fails the run.
                Dim rIDlim_F As Double = profile.R_roll_start + 0.25
                Dim rODlim_F As Double = profile.R_rolls_outer - 0.25
                Dim eqWorst As Double = 0.0
                For iStep As Integer = 0 To stepCount - 1
                    Try
                        Dim st As Integer = 0
                        Dim oa() As Object = DirectCast( _
                            results.GetReactionForcesAndMoments(iStep+1, Nothing, 0, st), Object())
                        Dim sumID As Double = 0, sumOD As Double = 0
                        For ri As Integer = 0 To (oa.Length \ reactStride) - 1
                            Dim nid3 As Integer = CInt(oa(ri * reactStride))
                            Dim fy As Double = CDbl(oa(ri * reactStride + 2))
                            If Math.Abs(fy) < 1.0E-12 Then Continue For
                            Dim rr As Double = -1
                            If nodeR.ContainsKey(nid3) Then rr = nodeR(nid3)
                            If rr >= 0 AndAlso rr <= rIDlim_F Then
                                sumID += fy
                            ElseIf rr >= rODlim_F Then
                                sumOD += fy
                            End If
                        Next
                        fReact(iStep) = Math.Abs(sumID)
                        If fReact(iStep) > 1.0E-9 Then
                            Dim eq As Double = Math.Abs(Math.Abs(sumID) - Math.Abs(sumOD)) / Math.Abs(sumID)
                            If eq > eqWorst Then eqWorst = eq
                        End If
                    Catch ex As Exception
                        fReact(iStep) = Double.NaN
                    End Try
                Next
                Log(String.Format("  F_reaction (ID-region): step1={0:F4}N step13={1:F4}N  Kms_secant={2:F4}N/mm", _
                    fReact(0), fReact(stepCount-1), fReact(stepCount-1)/maxDisp_mm))
                Log(String.Format("  Equilibrium check: worst |ID|-|OD| mismatch = {0:F3}%", eqWorst * 100.0))
                If eqWorst > 0.005 Then
                    Log("  *** EQUILIBRIUM FAIL: ID/OD reaction sums do not balance - force data untrustworthy. FAILING run. ***")
                    Return False
                End If
                ' Zero-force gate: a displacement-driven run cannot produce zero
                ' reaction at full stroke. All-zero means the prescribed displacement
                ' never bound (L1/BF-B failure class). Fail instead of writing zeros.
                If fReact(stepCount-1) < 1.0E-9 Then
                    Log("  *** ZERO-FORCE: no reaction at final step - prescribed displacement did not bind. FAILING run. ***")
                    Return False
                End If

                ' ─── Phase 4.1 DIAGNOSTIC (2026-08-18): reaction accounting audit ───
                ' The edge-node sum above captures only the share of the driven-face
                ' reaction carried by the inner ring. This block measures, at step 1
                ' and the last step, three sums from the SAME reaction array:
                '   sumEdge   = existing edge-node sum (what the CSV reports)
                '   sumIDreg  = ALL reacting nodes with R <= R_roll_start + 0.25mm
                '               (the entire driven flange region = true ID reaction)
                '   sumODreg  = ALL reacting nodes with R >= R_rolls_outer - 0.25mm
                '               (fixed lip; equilibrium partner, sign-opposite)
                ' It also dumps every reacting node (id, R, Y, Fy) at step 1 to
                ' <OutputDir>\<stem>_reactions.csv so the capture share is measured,
                ' not inferred. CSV columns and F_reaction remain UNCHANGED.
                Try
                    Dim rIDlim As Double = profile.R_roll_start + 0.25
                    Dim rODlim As Double = profile.R_rolls_outer - 0.25
                    For Each dStep As Integer In New Integer() {1, stepCount}
                        Dim st2 As Integer = 0
                        Dim oa2() As Object = DirectCast( _
                            results.GetReactionForcesAndMoments(dStep, Nothing, 0, st2), Object())
                        Dim sumEdge As Double = 0, sumIDreg As Double = 0, sumODreg As Double = 0, sumAll As Double = 0
                        Dim rIDmin As Double = 1.0E9, rIDmax As Double = -1.0E9
                        Dim rODmin As Double = 1.0E9, rODmax As Double = -1.0E9
                        Dim nID As Integer = 0, nOD As Integer = 0, nOther As Integer = 0
                        Dim dumpSb As System.Text.StringBuilder = Nothing
                        If dStep = 1 Then
                            dumpSb = New System.Text.StringBuilder()
                            dumpSb.AppendLine("NodeID,R_mm,Y_mm,Fy_N,Region")
                        End If
                        For ri As Integer = 0 To (oa2.Length \ reactStride) - 1
                            Dim nid2 As Integer = CInt(oa2(ri * reactStride))
                            Dim fy As Double = CDbl(oa2(ri * reactStride + 2))
                            If Math.Abs(fy) < 1.0E-12 Then Continue For
                            sumAll += fy
                            Dim rr As Double = -1, yy As Double = 0
                            If nodeR.ContainsKey(nid2) Then rr = nodeR(nid2)
                            If nodeY.ContainsKey(nid2) Then yy = nodeY(nid2)
                            Dim region As String = "OTHER"
                            If rr >= 0 AndAlso rr <= rIDlim Then
                                sumIDreg += fy : nID += 1 : region = "ID"
                            ElseIf rr >= rODlim Then
                                sumODreg += fy : nOD += 1 : region = "OD"
                            Else
                                nOther += 1
                            End If
                            If idEdgeNodeIDs.Contains(nid2) Then sumEdge += fy : region = region & "+EDGE"
                            If rr >= 0 Then
                                If region.StartsWith("ID") AndAlso rr < rIDmin Then rIDmin = rr
                                If region.StartsWith("ID") AndAlso rr > rIDmax Then rIDmax = rr
                                If region.StartsWith("OD") AndAlso rr < rODmin Then rODmin = rr
                                If region.StartsWith("OD") AndAlso rr > rODmax Then rODmax = rr
                            End If
                            If dumpSb IsNot Nothing Then
                                dumpSb.AppendLine(String.Format("{0},{1:F3},{2:F3},{3:E6},{4}", nid2, rr, yy, fy, region))
                            End If
                        Next
                        Log(String.Format("  REACTION AUDIT step {0}: edgeSum={1:F5}N  IDregion={2:F5}N  ODregion={3:F5}N  ALL={4:F5}N  (nID={5} nOD={6} nOther={7})", _
                            dStep, Math.Abs(sumEdge), Math.Abs(sumIDreg), Math.Abs(sumODreg), sumAll, nID, nOD, nOther))
                        If dStep = 1 AndAlso Math.Abs(sumIDreg) > 1.0E-12 Then
                            Dim capShare As Double = Math.Abs(sumEdge) / Math.Abs(sumIDreg)
                            Log(String.Format("  REACTION AUDIT: edge/IDregion capture share = {0:F4}", capShare))
                            ' W_free (2026-08-20): clamped-to-clamped free radial span, measured
                            ' from the entities that actually reacted. Self-documents the
                            ' MaxDisp = 0.55 * W_free rule instead of leaving it in prose.
                            If rIDmin < 1.0E8 AndAlso rODmax > -1.0E8 Then
                                Dim wFree As Double = rODmin - rIDmax
                                Log(String.Format("  W_FREE: ID bound R {0:F3}..{1:F3} mm | OD bound R {2:F3}..{3:F3} mm | W_free = {4:F3} mm | 0.55*W_free = {5:F3} mm", _
                                    rIDmin, rIDmax, rODmin, rODmax, wFree, 0.55 * wFree))
                            End If
                            ' Binding classification, recorded per run rather than inferred
                            ' from family. InvRoll cloth at H_pp 18 bound to the EDGE where
                            ' the rest of its family bound to the FACE - so this must be data.
                            If capShare > 0.98 AndAlso capShare < 1.02 Then
                                Log("  BINDING: EDGE-bound (capture share ~1.0) - legacy extraction was CORRECT for this run")
                            Else
                                Log(String.Format("  BINDING: FACE-bound (capture share {0:F4}) - legacy extraction UNDERCOUNTED this run", capShare))
                            End If
                            If _lastBindShare > 0.0 Then
                                Dim wasEdge As Boolean = (_lastBindShare > 0.98 AndAlso _lastBindShare < 1.02)
                                Dim nowEdge As Boolean = (capShare > 0.98 AndAlso capShare < 1.02)
                                If wasEdge <> nowEdge Then
                                    Log("  *** BINDING FLIP: this run bound differently than the previous run in this batch ***")
                                    Log(String.Format("  *** previous share = {0:F4}, this share = {1:F4} - anchor set needs a note ***", _lastBindShare, capShare))
                                End If
                            End If
                            _lastBindShare = capShare
                        End If
                        If dumpSb IsNot Nothing Then
                            Try
                                Dim dumpPath As String = outputPath.Replace("_auto.csv", "_reactions.csv")
                                System.IO.File.WriteAllText(dumpPath, dumpSb.ToString())
                                Log("  Reaction dump: " & dumpPath)
                            Catch exD As Exception
                                Log("  Reaction dump write failed: " & exD.Message)
                            End Try
                        End If
                    Next
                Catch exA As Exception
                    Log("  REACTION AUDIT failed: " & exA.Message)
                End Try

                ' ─── Phase 4.5: Tier 3 via GetMinMaxStress (one call per step) ───────
                ' Fast path: ~50 calls total. <1 second on a 10k-node mesh.
                Log("  Phase 4.5: GetMinMaxStress per step (Tier 3)...")
                Dim stressOK As Boolean = False
                Dim vmMax(stepCount - 1) As Double
                Dim vmMaxR(stepCount - 1) As Double
                Dim vmMaxZ(stepCount - 1) As Double
                Dim vmMaxNodeID(stepCount - 1) As Integer

                ' One-time slot-order detection: the [0]/[2] slots hold node IDs,
                ' and we need to know which is the MAX node. Cross-check the slot-[2]
                ' candidate against the per-node API.
                Dim maxNodeSlot As Integer = 2  ' default guess: [minNid, minVal, maxNid, maxVal]
                Try
                    Dim sx As Integer = 0
                    Dim oa() As Object = DirectCast( _
                        results.GetMinMaxStress(9, -1, 1, Nothing, 0, sx), Object())
                    If oa.Length >= 4 Then
                        Dim maxValAtStep1 As Double = CDbl(oa(3))
                        Dim cand2 As Integer = CInt(oa(2))
                        ' Look up stress at cand2 via per-node API at step 1
                        Dim sx2 As Integer = 0
                        Dim sv() As Object = DirectCast( _
                            results.GetStressComponentForAllStepsAtNode(9, cand2, Nothing, 0, sx2), Object())
                        Dim valAtCand2 As Double = CDbl(sv(2))  ' step 1 value
                        Dim rel As Double = Math.Abs(valAtCand2 - maxValAtStep1) / Math.Max(1.0e-9, Math.Abs(maxValAtStep1))
                        maxNodeSlot = If(rel < 0.05, 2, 0)
                        Log(String.Format("    Slot detect: cand2 stress={0:E3}  maxVal={1:E3}  rel={2:F4}  -> slot=[{3}]", _
                            valAtCand2, maxValAtStep1, rel, maxNodeSlot))
                        stressOK = True
                    Else
                        Log(String.Format("    Slot detect: unexpected GetMinMaxStress length {0}", oa.Length))
                    End If
                Catch ex As Exception
                    Log("    Slot detect FAILED: " & ex.Message)
                End Try

                If stressOK Then
                    Dim apiFailCount As Integer = 0
                    For iStep As Integer = 0 To stepCount - 1
                        Try
                            Dim sx As Integer = 0
                            Dim oa() As Object = DirectCast( _
                                results.GetMinMaxStress(9, -1, iStep + 1, Nothing, 0, sx), Object())
                            If oa.Length >= 4 Then
                                vmMax(iStep) = CDbl(oa(3)) / 1000000.0  ' Pa -> MPa
                                Dim maxNid As Integer = CInt(If(maxNodeSlot = 0, oa(0), oa(2)))
                                vmMaxNodeID(iStep) = maxNid
                                If maxNid > 0 AndAlso nodeR.ContainsKey(maxNid) Then
                                    vmMaxR(iStep) = nodeR(maxNid)
                                    vmMaxZ(iStep) = nodeY(maxNid)
                                End If
                            Else
                                apiFailCount += 1
                                vmMax(iStep) = Double.NaN
                            End If
                        Catch ex As Exception
                            apiFailCount += 1
                            vmMax(iStep) = Double.NaN
                        End Try
                    Next
                    Log(String.Format("    Tier 3: step1={0:F3} MPa  midstep={1:F3} MPa  laststep={2:F3} MPa  (api fails: {3})", _
                        vmMax(0), vmMax(stepCount \ 2), vmMax(stepCount - 1), apiFailCount))
                    Log(String.Format("    Last-step max @ node {0} R={1:F2}mm Z={2:F2}mm", _
                        vmMaxNodeID(stepCount - 1), vmMaxR(stepCount - 1), vmMaxZ(stepCount - 1)))
                End If

                ' ─── Phase 5: Write _auto.csv ───────────────────────────────────────
                Dim sb As New System.Text.StringBuilder()
                sb.Append("Step,Time_frac,X_applied_mm,F_reaction_N,Kms_N_per_mm")
                For ci As Integer = 0 To crests.Count - 1
                    sb.Append(String.Format(",Z_roll{0}_mm", ci + 1))
                Next
                For ci As Integer = 0 To crests.Count - 1
                    sb.Append(String.Format(",Ratio_roll{0}", ci + 1))
                Next
                If stressOK Then
                    sb.Append(",vonMises_max_MPa,R_at_max_mm,Z_at_max_mm")
                End If
                sb.AppendLine()
                sb.AppendLine(String.Format("# ID={0} OD={1} N={2} H_pp={3} T={4} E={5} Nu={6} Density={7} Material={8}", _
                    profile.ID, profile.OD, profile.N, profile.H_pp, profile.T, profile.E, profile.Nu, profile.Density, profile.MaterialName))
                ' Self-describing geometry labels (2026-08-21). EffPitch carries the
                ' 2T fillet term so it varies with thickness; any angle label derived
                ' from a fixed pitch drifts with T. TrueConnAngle is the inverse
                ' natural-H angle at THIS run's pitch, which is what "angle" means.
                Try
                    Dim epNow As Double = profile.EffPitchPerRoll
                    Dim angTrue As Double = Double.NaN
                    If epNow > 0.0 Then
                        angTrue = Math.Atan(Math.PI * profile.H_pp / (2.0 * epNow)) * 180.0 / Math.PI
                    End If
                    Dim rcNow As Double = profile.CrestRadius
                    Dim rcTxt As String = "n/a"
                    Dim trTxt As String = "n/a"
                    If rcNow < 1.0E9 AndAlso rcNow > 0.0 Then
                        rcTxt = String.Format("{0:F4}", rcNow)
                        trTxt = String.Format("{0:F4}", profile.T / rcNow)
                    End If
                    sb.AppendLine(String.Format("# EffPitch={0:F4} TrueConnAngle={1:F3} HppOverEffPitch={2:F5} InputConnAngle={3:F2} R_crest={4} T_over_Rcrest={5}", _
                        epNow, angTrue, profile.H_pp / epNow, profile.ConnectorAngle, rcTxt, trTxt))
                Catch exLbl As Exception
                    sb.AppendLine("# EffPitch=n/a TrueConnAngle=n/a (label computation failed: " & exLbl.Message & ")")
                End Try
                If profile.ProfileType = 11 Then
                    ' Self-describing DoubleRoll CenterFlat. effective = value actually
                    ' built after the span*0.8 clamp; distiller keys on effective.
                    sb.AppendLine(String.Format("# DoubleRoll CenterFlat (mm): nominal={0:F2} effective={1:F2}", _
                        profile.CenterFlat, profile.EffectiveCenterFlat))
                End If
                sb.AppendLine(String.Format("# Roll crests R (mm): " & String.Join(", ", _
                    crests.ConvertAll(Function(r) r.ToString("F2")).ToArray())))
                sb.AppendLine(String.Format("# Crest node IDs: " & String.Join(", ", _
                    Array.ConvertAll(crestNodeID, Function(id) id.ToString()))))
                sb.AppendLine(String.Format("# Crest node R (mm): " & String.Join(", ", _
                    Array.ConvertAll(crestNodeR, Function(r) r.ToString("F2")))))
                sb.AppendLine(String.Format("# Crest node Y (mm): " & String.Join(", ", _
                    Array.ConvertAll(crestNodeY, Function(y) y.ToString("F2")))))
                If stressOK Then
                    sb.AppendLine("# Stress component: von Mises (comp=9)  Units: MPa  (Tier 3 = per-step global max via GetMinMaxStress)")
                End If

                For iStep As Integer = 0 To stepCount - 1
                    Dim tf As Double = If(timeFracLoaded, timeFrac(iStep), CDbl(iStep + 1) / CDbl(stepCount))
                    Dim xApp As Double = tf * maxDisp_mm
                    Dim fStr As String = If(Double.IsNaN(fReact(iStep)), "", fReact(iStep).ToString("F6"))
                    Dim kStr As String = If(Double.IsNaN(fReact(iStep)) OrElse Math.Abs(xApp) < 0.001, "", _
                        (fReact(iStep) / xApp).ToString("F6"))
                    sb.Append(String.Format("{0},{1:F6},{2:F6},{3},{4}", iStep + 1, tf, xApp, fStr, kStr))
                    For ci As Integer = 0 To crests.Count - 1
                        sb.Append(String.Format(",{0:F6}", crestUY(ci, iStep)))
                    Next
                    For ci As Integer = 0 To crests.Count - 1
                        Dim ratio As Double = If(Math.Abs(xApp) > 0.001, crestUY(ci, iStep) / xApp, 0.0)
                        sb.Append(String.Format(",{0:F6}", ratio))
                    Next
                    If stressOK Then
                        Dim vmStr As String = If(Double.IsNaN(vmMax(iStep)), "", vmMax(iStep).ToString("F4"))
                        sb.Append(String.Format(",{0},{1:F4},{2:F4}", vmStr, vmMaxR(iStep), vmMaxZ(iStep)))
                    End If
                    sb.AppendLine()
                Next

                System.IO.File.WriteAllText(outputPath, sb.ToString())
                Log("  CSV: " & outputPath)
                ' Read-back: confirm the auto.csv actually landed. It has been seen
                ' absent while profile.csv/DONE.txt (written after) persisted — a
                ' write that returned without error but left no file. Retry once,
                ' then fail the run rather than let it stamp PASS on a missing file.
                If Not System.IO.File.Exists(outputPath) Then
                    Log("  WARNING: auto.csv not on disk after write — retrying once")
                    Try
                        System.IO.File.WriteAllText(outputPath, sb.ToString())
                    Catch exRetry As Exception
                        Log("  retry write threw: " & exRetry.Message)
                    End Try
                    If Not System.IO.File.Exists(outputPath) Then
                        Log("  ERROR: auto.csv did not persist: " & outputPath)
                        Return False
                    End If
                    Log("  auto.csv landed on retry")
                End If

                ' ─── Phase 6: _profile.csv (Tier 2 = 10 deciles, Tier 1 = stress at θ=0 slice) ───
                Log("  Extracting profile cross-section...")
                Try
                    Dim angleTol As Double = 0.06
                    Dim profileNodes As New List(Of Integer)()
                    For Each kvp As KeyValuePair(Of Integer, Double) In nodeX
                        Dim nid As Integer = kvp.Key
                        Dim nx As Double = kvp.Value
                        Dim nz As Double = nodeZ(nid)
                        If nx > 0.1 Then
                            Dim theta As Double = Math.Abs(Math.Atan2(nz, nx))
                            If theta < angleTol Then profileNodes.Add(nid)
                        End If
                    Next
                    If profileNodes.Count < 10 Then
                        angleTol = 0.15
                        profileNodes.Clear()
                        For Each kvp As KeyValuePair(Of Integer, Double) In nodeX
                            Dim nid As Integer = kvp.Key
                            Dim nx As Double = kvp.Value
                            Dim nz As Double = nodeZ(nid)
                            If nx > 0.1 Then
                                Dim theta As Double = Math.Abs(Math.Atan2(nz, nx))
                                If theta < angleTol Then profileNodes.Add(nid)
                            End If
                        Next
                    End If
                    profileNodes.Sort(Function(a, b) nodeR(a).CompareTo(nodeR(b)))
                    Log(String.Format("  Profile nodes: {0} (angle tol={1:F3} rad)", profileNodes.Count, angleTol))

                    If profileNodes.Count >= 5 Then
                        ' 10 decile excursion levels
                        Dim targetFracs() As Double = {0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0}
                        Dim profileSteps(10) As Integer
                        Dim profileDisp(10) As Double
                        For ti As Integer = 0 To 10
                            Dim targetStep As Integer = CInt(Math.Round(targetFracs(ti) * (stepCount - 1)))
                            If targetStep < 0 Then targetStep = 0
                            If targetStep >= stepCount Then targetStep = stepCount - 1
                            profileSteps(ti) = targetStep
                            Dim tf As Double = If(timeFracLoaded, timeFrac(targetStep), CDbl(targetStep + 1) / CDbl(stepCount))
                            profileDisp(ti) = tf * maxDisp_mm
                        Next
                        profileSteps(0) = -1
                        profileDisp(0) = 0.0

                        Dim dispLevelsStr As String = profileDisp(1).ToString("F1")
                        For ti As Integer = 2 To 10
                            dispLevelsStr &= ", " & profileDisp(ti).ToString("F1")
                        Next
                        Log("  Excursion levels (mm): " & dispLevelsStr)

                        ' Displacement extraction for profile nodes
                        Dim profUX(profileNodes.Count - 1, stepCount - 1) As Double
                        Dim profUY(profileNodes.Count - 1, stepCount - 1) As Double
                        For pi As Integer = 0 To profileNodes.Count - 1
                            Dim nid As Integer = profileNodes(pi)
                            Try
                                Dim stx As Integer = 0
                                Dim oax() As Object = DirectCast( _
                                    results.GetDisplacementComponentForAllStepsAtNode(0, nid, Nothing, 0, stx), Object())
                                For iStep As Integer = 0 To stepCount - 1
                                    Dim bx As Integer = iStep * 3
                                    If bx + 2 < oax.Length Then profUX(pi, iStep) = CDbl(oax(bx + 2))
                                Next
                                Dim sty As Integer = 0
                                Dim oay() As Object = DirectCast( _
                                    results.GetDisplacementComponentForAllStepsAtNode(1, nid, Nothing, 0, sty), Object())
                                For iStep As Integer = 0 To stepCount - 1
                                    Dim by As Integer = iStep * 3
                                    If by + 2 < oay.Length Then profUY(pi, iStep) = CDbl(oay(by + 2))
                                Next
                            Catch ex As Exception
                            End Try
                            If pi Mod 20 = 0 Then System.Windows.Forms.Application.DoEvents()
                        Next
                        Log("  Profile displacement extraction complete")

                        ' Tier 1 stress for profile nodes only (per-node API, ~200 calls)
                        Dim profStress(profileNodes.Count - 1, stepCount - 1) As Double  ' MPa
                        Dim profStressOK As Boolean = False
                        If stressOK Then
                            Log(String.Format("  Tier 1: extracting stress for {0} profile nodes...", profileNodes.Count))
                            System.Windows.Forms.Application.DoEvents()  ' flush start message
                            Dim t0 As DateTime = DateTime.Now
                            Dim sFails As Integer = 0
                            Dim reportEvery As Integer = Math.Max(1, profileNodes.Count \ 10)
                            For pi As Integer = 0 To profileNodes.Count - 1
                                Dim nid As Integer = profileNodes(pi)
                                Try
                                    Dim sts As Integer = 0
                                    Dim oas() As Object = DirectCast( _
                                        results.GetStressComponentForAllStepsAtNode(9, nid, Nothing, 0, sts), Object())
                                    For iStep As Integer = 0 To stepCount - 1
                                        Dim bs As Integer = iStep * 3
                                        If bs + 2 < oas.Length Then
                                            profStress(pi, iStep) = CDbl(oas(bs + 2)) / 1000000.0  ' Pa -> MPa
                                        End If
                                    Next
                                Catch ex As Exception
                                    sFails += 1
                                End Try
                                ' Progress every ~10% with elapsed and ETA - so the user sees the
                                ' run is alive during the ~60-second per-node-API phase
                                If ((pi + 1) Mod reportEvery = 0) OrElse (pi = profileNodes.Count - 1) Then
                                    Dim el As Double = (DateTime.Now - t0).TotalSeconds
                                    Dim pct As Double = (pi + 1) * 100.0 / profileNodes.Count
                                    Dim eta As Double = If(pi + 1 > 0, el * (profileNodes.Count - pi - 1) / (pi + 1), 0.0)
                                    Log(String.Format("    Tier 1: {0}/{1} ({2:F0}%)  elapsed {3:F1}s  ETA {4:F1}s", _
                                        pi + 1, profileNodes.Count, pct, el, eta))
                                    System.Windows.Forms.Application.DoEvents()
                                End If
                                System.Windows.Forms.Application.DoEvents()
                            Next
                            profStressOK = (sFails < profileNodes.Count)
                            Log(String.Format("  Tier 1 done in {0:F1}s ({1} failures)", _
                                (DateTime.Now - t0).TotalSeconds, sFails))
                            System.Windows.Forms.Application.DoEvents()  ' flush "done" message
                        End If

                        ' Build _profile.csv
                        Dim sbProf As New System.Text.StringBuilder()
                        sbProf.Append("NodeID,R_undef_mm,Z_undef_mm")
                        For ti As Integer = 1 To 10
                            Dim dLabel As String = profileDisp(ti).ToString("F1").Replace(".", "_")
                            sbProf.Append(String.Format(",R_at_{0}mm,Z_at_{0}mm", dLabel))
                        Next
                        If profStressOK Then
                            sbProf.Append(",Stress_undef_MPa")
                            For ti As Integer = 1 To 10
                                Dim dLabel As String = profileDisp(ti).ToString("F1").Replace(".", "_")
                                sbProf.Append(String.Format(",Stress_at_{0}_MPa", dLabel))
                            Next
                        End If
                        sbProf.AppendLine()

                        sbProf.AppendLine(String.Format("# ID={0} OD={1} N={2} H_pp={3} T={4} E={5} Nu={6} Density={7} Material={8}", _
                            profile.ID, profile.OD, profile.N, profile.H_pp, profile.T, profile.E, profile.Nu, profile.Density, profile.MaterialName))
                        sbProf.AppendLine(String.Format("# Profile nodes: {0}  Angle tolerance: {1:F3} rad", profileNodes.Count, angleTol))
                        Dim excursionStr As String = profileDisp(1).ToString("F1")
                        For ti As Integer = 2 To 10
                            excursionStr &= ", " & profileDisp(ti).ToString("F1")
                        Next
                        sbProf.AppendLine("# Excursion levels (mm): " & excursionStr)
                        Dim stepStr As String = (profileSteps(1) + 1).ToString()
                        For ti As Integer = 2 To 10
                            stepStr &= ", " & (profileSteps(ti) + 1).ToString()
                        Next
                        sbProf.AppendLine("# Steps used: " & stepStr)
                        If profStressOK Then
                            sbProf.AppendLine("# Stress component: von Mises (comp=9)  Units: MPa  (Tier 1)")
                        End If

                        For pi As Integer = 0 To profileNodes.Count - 1
                            Dim nid As Integer = profileNodes(pi)
                            Dim rUndef As Double = nodeR(nid)
                            Dim zUndef As Double = nodeY(nid)
                            sbProf.Append(String.Format("{0},{1:F4},{2:F4}", nid, rUndef, zUndef))
                            For ti As Integer = 1 To 10
                                Dim si As Integer = profileSteps(ti)
                                Dim rDef As Double = rUndef + profUX(pi, si)
                                Dim zDef As Double = zUndef + profUY(pi, si)
                                sbProf.Append(String.Format(",{0:F4},{1:F4}", rDef, zDef))
                            Next
                            If profStressOK Then
                                sbProf.Append(",0.0000")
                                For ti As Integer = 1 To 10
                                    Dim si As Integer = profileSteps(ti)
                                    sbProf.Append(String.Format(",{0:F4}", profStress(pi, si)))
                                Next
                            End If
                            sbProf.AppendLine()
                        Next

                        Dim profPath As String = outputPath.Replace("_auto.csv", "_profile.csv")
                        System.IO.File.WriteAllText(profPath, sbProf.ToString())
                        Log("  Profile CSV: " & profPath)
                        System.Windows.Forms.Application.DoEvents()

                        ' Disk-based completion sentinel — independent of UI repaint.
                        ' If this file appears next to the CSVs, the extraction is complete
                        ' and the runner may be closed safely (even if the log textbox has
                        ' not yet refreshed to show the "complete" message).
                        Try
                            Dim donePath As String = outputPath.Replace("_auto.csv", "_DONE.txt")
                            Dim doneText As String = _
                                "ExtractResultsAuto completed at " & _
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") & System.Environment.NewLine & _
                                "auto:    " & outputPath & System.Environment.NewLine & _
                                "profile: " & profPath & System.Environment.NewLine
                            System.IO.File.WriteAllText(donePath, doneText)
                            Log("  Sentinel: " & donePath)
                            System.Windows.Forms.Application.DoEvents()
                        Catch sentEx As Exception
                            Log("  Sentinel write failed: " & sentEx.Message)
                        End Try
                    Else
                        Log("  WARNING: Too few profile nodes found — skipping profile extraction")
                    End If
                Catch ex As Exception
                    Log("  Profile extraction failed: " & ex.Message)
                End Try

                Log("=== ExtractResultsAuto complete ===")
                System.Windows.Forms.Application.DoEvents()

                ' Explicit COM release before function exit. Without this, the heavy
                ' CWResults reference goes out of scope and the .NET runtime invokes
                ' Marshal.Release() implicitly, which can stall the UI thread for many
                ' seconds while SW finishes any pending operations on the results object.
                ' The user perceives this as a "phantom hang" — the CSVs and the sentinel
                ' are on disk but the UI never repaints to show the completion message.
                Try
                    If results IsNot Nothing Then
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(results)
                    End If
                    System.Runtime.InteropServices.Marshal.CleanupUnusedObjectsInCurrentContext()
                Catch : End Try
                System.Windows.Forms.Application.DoEvents()

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
