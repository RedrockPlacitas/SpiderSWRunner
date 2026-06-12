Imports System
Imports System.Collections.Generic
Imports System.Text
Imports Microsoft.VisualBasic

' ══════════════════════════════════════════════════════════════════
'  BatchRunner.vb — unattended batch execution engine
'
'  Consumes a run table (List(Of BatchRow)), executes each row via
'  SWAutomation (Create Part -> Setup Study -> Finest Mesh + Run ->
'  Extract), validates output CSVs, and checkpoints progress so an
'  interrupted batch resumes by skipping PASS rows.
'
'  Standing rules implemented here:
'   - Whole-batch geometry precheck + filename collision check BEFORE
'     run 1 (no 3AM surprises)
'   - Mandatory 4-point CSV validation per run
'   - Pause after first run for user verification of DB/CSV writes
'   - SolidWorks restart every N runs (SW2014 COM memory hygiene)
'   - Failure isolation: a failed row never halts the queue
' ══════════════════════════════════════════════════════════════════

Public Class BatchRunner

    ' Crest R gate guards against gross geometry errors (wrong T shifts crests
    ' by ~half a pitch, mm-scale). Nearest-node quantization with the finest
    ' standard mesh (~3.3mm elements) produces offsets up to ~0.7mm on valid
    ' runs (verified 2026-06-11), so the tolerance is 1.0mm.
    Public Const CrestR_Tol_mm As Double = 1.0     ' check 1 tolerance (nearest-node)
    Public Const CrestY_Tol_mm As Double = 0.05    ' check 2 tolerance (memory rule)
    Public Const KmsPair_Tol As Double = 0.01      ' check 3: 1%

    Private _sw As SWAutomation
    Private _log As Action(Of String)
    Public AbortRequested As Boolean = False

    ' Raised after run 1 completes; return False to abort the batch.
    Public VerifyFirstRun As Func(Of BatchRow, Boolean) = Nothing
    ' Progress callback: (currentIndex0Based, total, row)
    Public Progress As Action(Of Integer, Integer, BatchRow) = Nothing

    Public Sub New(ByVal sw As SWAutomation, ByVal logger As Action(Of String))
        _sw = sw
        _log = logger
    End Sub

    Private Sub Log(ByVal msg As String)
        If _log IsNot Nothing Then _log(msg)
    End Sub

    ' ──────────────────────────────────────────────────────────────
    ' Output filename — single source of truth for batch AND single
    ' runs (Form1 delegates here). Mirrors the historical convention.
    ' ──────────────────────────────────────────────────────────────
    Public Shared Function ProfileTag(ByVal p As SpiderProfile) As String
        Select Case p.ProfileType
            Case 1 : Return "Arc"
            Case 2 : Return "ArcLines"
            Case 3 : Return "SineLines"
            Case 10
                If p.FirstRollUp Then Return "HalfRoll" Else Return "InvRoll"
            Case 11 : Return "DoubleRoll"
            Case 12 : Return "Bullet"
            Case 13 : Return "Flat"
            Case 14 : Return "Triangle"
            Case 16 : Return "LipRoll"
            Case 17 : Return "TriRadius"
            Case 99 : Return "COMSOL"
            Case Else : Return "Sin"
        End Select
    End Function

    Public Shared Function BuildOutputFilename(ByVal p As SpiderProfile, _
                                               ByVal isPull As Boolean) As String
        Dim modeStr As String = If(p.ComponentMode = 1, "Edge", "Spider")
        Dim tag As String = ProfileTag(p)
        Dim matTag As String = p.MaterialName.Replace(" ", "_")
        Dim base As String = String.Format("{0}_N{1}_ID{2}_OD{3}_{4}_Hpp{5:F1}_T{6:F2}_{7}", _
            modeStr, p.N, CInt(p.ID), CInt(p.OD), tag, p.H_pp, p.T, matTag)
        If p.ProfileType = 2 OrElse p.ProfileType = 3 Then
            base = base & String.Format("_A{0}", CInt(p.ConnectorAngle))
        End If
        If p.TaperPct <> 0 Then
            base = base & String.Format("_Tp{0}", CInt(p.TaperPct))
        End If
        If p.VariablePitch AndAlso p.PitchTaperPct <> 0 Then
            base = base & String.Format("_Pt{0}", CInt(p.PitchTaperPct))
        End If
        If p.UseNaturalH Then
            base = base & "_natH"
        End If
        If (p.ProfileType = 2 OrElse p.ProfileType = 3) AndAlso p.StraightLength <> 1.0 Then
            base = base & String.Format("_SL{0:0.0}", p.StraightLength)
        End If
        If Not p.FirstRollUp Then
            base = base & "_FD"
        End If
        If isPull Then
            base = base & "_Pull"
        Else
            base = base & "_Push"
        End If
        Return base & "_auto.csv"
    End Function

    ' ──────────────────────────────────────────────────────────────
    ' WHOLE-BATCH PRECHECK — run before run 1.
    '  a) Geometry gate: H_pp <= MaxH_pp (spider mode); Bullet param
    '     validation for ProfileType 12. EffPitch/MaxH_pp/margin are
    '     recorded for the SpiderDesigner precheck table.
    '  b) Filename collision: existing file on disk OR duplicate
    '     within the batch.
    ' Returns True if every PENDING row passed.
    ' ──────────────────────────────────────────────────────────────
    Public Function PrecheckAll(ByVal rows As List(Of BatchRow)) As Boolean
        Dim allPass As Boolean = True
        Dim namesSeen As New Dictionary(Of String, Integer)()

        Log("══ BATCH PRECHECK ══")
        For Each r As BatchRow In rows
            r.PrecheckPass = True
            r.PrecheckNote = ""
            Dim p As SpiderProfile = r.ToProfile()

            ' Geometry values for the precheck table
            Try
                r.EffPitch = p.EffPitchPerRoll
                r.MaxHpp = p.MaxH_pp
                r.Margin = r.MaxHpp - p.H_pp
            Catch ex As System.Exception
                r.EffPitch = 0 : r.MaxHpp = 0 : r.Margin = 0
                r.PrecheckPass = False
                r.PrecheckNote = "geom calc: " & ex.Message
            End Try

            ' Gate: spider mode H_pp ceiling
            If r.PrecheckPass AndAlso r.ComponentMode = 0 Then
                If p.H_pp > r.MaxHpp + 0.0001 Then
                    r.PrecheckPass = False
                    r.PrecheckNote = String.Format("H_pp {0:F2} > MaxH_pp {1:F2}", p.H_pp, r.MaxHpp)
                End If
            End If

            ' Gate: Bullet parameter validation
            If r.PrecheckPass AndAlso r.ProfileType = 12 Then
                Dim ve As String = p.ValidateBulletParams()
                If ve.Length > 0 Then
                    r.PrecheckPass = False
                    r.PrecheckNote = "Bullet: " & ve
                End If
            End If

            ' Filename collision
            Dim fname As String = BuildOutputFilename(p, r.IsPull())
            r.OutputFile = System.IO.Path.Combine(r.OutputDir, fname)
            If r.PrecheckPass Then
                If namesSeen.ContainsKey(r.OutputFile) Then
                    r.PrecheckPass = False
                    r.PrecheckNote = "DUPLICATE of RunID " & namesSeen(r.OutputFile)
                Else
                    namesSeen(r.OutputFile) = r.RunID
                    If r.Status <> "PASS" AndAlso r.Status <> "PARTIAL" AndAlso _
                       System.IO.File.Exists(r.OutputFile) Then
                        r.PrecheckPass = False
                        r.PrecheckNote = "FILE EXISTS: " & r.OutputFile
                    End If
                End If
            End If

            If Not r.PrecheckPass AndAlso r.Status <> "PASS" Then allPass = False
            Log(String.Format("  Run {0,3}: EffPitch={1,7:F3}  MaxHpp={2,6:F2}  margin={3,6:F2}  {4}  {5}", _
                r.RunID, r.EffPitch, r.MaxHpp, r.Margin, _
                If(r.PrecheckPass, "PASS", "FAIL"), r.PrecheckNote))
        Next
        Log(String.Format("══ PRECHECK {0} ══", If(allPass, "PASS — batch may run", "FAILED — fix flagged rows first")))
        Return allPass
    End Function

    ' ──────────────────────────────────────────────────────────────
    ' EXECUTE the batch.
    '  checkpointPath — run-table CSV rewritten after every row
    '  restartEvery   — SolidWorks restart cadence (0 = never)
    '  pauseAfterFirst— invoke VerifyFirstRun after run 1
    ' Returns count of PASS rows this session.
    ' ──────────────────────────────────────────────────────────────
    Public Function Execute(ByVal rows As List(Of BatchRow), _
                            ByVal checkpointPath As String, _
                            ByVal restartEvery As Integer, _
                            ByVal pauseAfterFirst As Boolean) As Integer
        AbortRequested = False
        Dim passCount As Integer = 0
        Dim runsSinceRestart As Integer = 0
        Dim firstVerified As Boolean = Not pauseAfterFirst
        Dim batchLogPath As String = ""
        Try
            Dim dir0 As String = rows(0).OutputDir
            If Not System.IO.Directory.Exists(dir0) Then System.IO.Directory.CreateDirectory(dir0)
            batchLogPath = System.IO.Path.Combine(dir0, _
                "BatchLog_" & DateTime.Now.ToString("yyyyMMdd_HHmmss") & ".log")
        Catch : End Try

        BatchLog(batchLogPath, "BATCH START " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") & _
                 "  rows=" & rows.Count)

        If Not _sw.ConnectOrStart() Then
            Log("ABORT: cannot connect/start SolidWorks.")
            Return 0
        End If

        For i As Integer = 0 To rows.Count - 1
            System.Windows.Forms.Application.DoEvents()
            If AbortRequested Then
                Log("── ABORT requested — stopping after row " & i & " ──")
                BatchLog(batchLogPath, "ABORTED before RunID " & rows(i).RunID)
                Exit For
            End If

            Dim r As BatchRow = rows(i)
            If Progress IsNot Nothing Then Progress(i, rows.Count, r)

            If r.Status = "PASS" OrElse r.Status = "PARTIAL" Then
                Log(String.Format("Run {0}: already {1} — skipping (resume)", r.RunID, r.Status))
                Continue For
            End If
            If Not r.PrecheckPass Then
                r.Status = "SKIP"
                r.Message = "precheck: " & r.PrecheckNote
                Checkpoint(rows, checkpointPath)
                Continue For
            End If

            ' SW restart cadence
            If restartEvery > 0 AndAlso runsSinceRestart >= restartEvery Then
                If Not _sw.RestartSolidWorks() Then
                    Log("ABORT: SolidWorks restart failed.")
                    BatchLog(batchLogPath, "ABORT: SW restart failed before RunID " & r.RunID)
                    Exit For
                End If
                runsSinceRestart = 0
            End If

            Log("")
            Log(String.Format("════ RUN {0}/{1}  (RunID {2})  {3} ════", _
                i + 1, rows.Count, r.RunID, System.IO.Path.GetFileName(r.OutputFile)))
            Dim t0 As DateTime = DateTime.Now
            Dim p As SpiderProfile = r.ToProfile()
            Dim ok As Boolean = True
            Dim failStage As String = ""

            If ok AndAlso Not _sw.CreatePart(p) Then ok = False : failStage = "CreatePart"
            If ok AndAlso Not _sw.SetupStudy(p, r.MaxDisp, r.Steps, r.IsPull()) Then ok = False : failStage = "SetupStudy"
            If ok AndAlso Not _sw.MeshAndRun() Then ok = False : failStage = "MeshAndRun"
            If ok Then
                If Not System.IO.Directory.Exists(r.OutputDir) Then
                    System.IO.Directory.CreateDirectory(r.OutputDir)
                End If
                If Not _sw.ExtractResultsAuto(p, r.MaxDisp, r.Steps, r.OutputFile) Then
                    ok = False : failStage = "Extract"
                End If
            End If

            ' (View JPG captures removed by request 2026-06-11 — the CSV pair
            ' per direction carries profiles and stress; images added no value.)

            ' 4-point validation (+ stepping/completion)
            Dim vNotes As String = ""
            Dim isPartial As Boolean = False
            If ok Then
                ok = ValidateAutoCsv(r, p, rows, vNotes, isPartial)
                If Not ok Then failStage = "Validation"
            End If

            Dim dt As TimeSpan = DateTime.Now - t0
            If ok AndAlso isPartial Then
                ' Gates passed but the solver terminated before full stroke.
                ' Data up to the stop is valid; the row is terminal (rerunning
                ' reproduces the same ceiling) and does not block the batch.
                r.Status = "PARTIAL"
                r.Message = String.Format("{0:F1} min. {1}", dt.TotalMinutes, vNotes)
                passCount += 1
                Log(String.Format("Run {0}: PARTIAL ({1:F1} min)  {2}", r.RunID, dt.TotalMinutes, vNotes))
            ElseIf ok Then
                r.Status = "PASS"
                r.Message = String.Format("{0:F1} min. {1}", dt.TotalMinutes, vNotes)
                passCount += 1
                Log(String.Format("Run {0}: PASS ({1:F1} min)  {2}", r.RunID, dt.TotalMinutes, vNotes))
            Else
                r.Status = "FAIL"
                r.Message = failStage & ". " & vNotes
                Log(String.Format("Run {0}: FAIL at {1}  {2}", r.RunID, failStage, vNotes))
            End If
            BatchLog(batchLogPath, String.Format("RunID {0}  {1}  {2:F1}min  {3}  {4}", _
                r.RunID, r.Status, dt.TotalMinutes, failStage, _
                System.IO.Path.GetFileName(r.OutputFile)))
            runsSinceRestart += 1
            Checkpoint(rows, checkpointPath)

            ' First-run verification gate
            If Not firstVerified Then
                firstVerified = True
                If VerifyFirstRun IsNot Nothing Then
                    If Not VerifyFirstRun(r) Then
                        Log("── First-run verification declined — batch stopped ──")
                        BatchLog(batchLogPath, "STOPPED at first-run verification")
                        Exit For
                    End If
                    Log("── First-run verified — releasing remaining rows ──")
                    BatchLog(batchLogPath, "First-run verified; continuing")
                End If
            End If
        Next

        BatchLog(batchLogPath, "BATCH END " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") & _
                 "  pass=" & passCount)
        Log("")
        Log(String.Format("══ BATCH COMPLETE: {0} PASS of {1} rows ══", passCount, rows.Count))
        Log("Batch log: " & batchLogPath)
        Return passCount
    End Function

    Private Sub Checkpoint(ByVal rows As List(Of BatchRow), ByVal path As String)
        Try
            If Not String.IsNullOrEmpty(path) Then SweepMatrix.SaveCsv(rows, path)
        Catch ex As System.Exception
            Log("  Checkpoint write failed: " & ex.Message)
        End Try
    End Sub

    Private Sub BatchLog(ByVal path As String, ByVal line As String)
        Try
            If Not String.IsNullOrEmpty(path) Then
                System.IO.File.AppendAllText(path, line & System.Environment.NewLine)
            End If
        Catch : End Try
    End Sub

    ' ──────────────────────────────────────────────────────────────
    ' MANDATORY 4-POINT CSV VALIDATION
    '  1. Crest R positions match expected geometry (±0.5 mm, nearest node)
    '  2. Crest |Y| within ±0.05 mm of nominal H_eff (uniform-H runs only;
    '     taper/naturalH runs are reported informationally, not gated)
    '  3. Push/Pull Kms0 match within 1% (evaluated when both of a pair exist)
    '  4. Header E/Nu/Material match the run row
    ' ──────────────────────────────────────────────────────────────
    Public Function ValidateAutoCsv(ByVal r As BatchRow, ByVal p As SpiderProfile, _
                                    ByVal allRows As List(Of BatchRow), _
                                    ByRef notes As String, _
                                    ByRef isPartial As Boolean) As Boolean
        Dim pass As Boolean = True
        isPartial = False
        Dim sb As New StringBuilder()
        Try
            If Not System.IO.File.Exists(r.OutputFile) Then
                notes = "CSV missing: " & r.OutputFile
                Return False
            End If
            Dim lines() As String = System.IO.File.ReadAllLines(r.OutputFile)

            Dim hdrGeom As String = ""
            Dim crestRLine As String = ""
            Dim crestYLine As String = ""
            Dim firstData As String = ""
            Dim dataRowCount As Integer = 0
            Dim timeFracs As New List(Of Double)()
            For Each ln As String In lines
                If ln.StartsWith("# ID=") Then hdrGeom = ln
                If ln.StartsWith("# Crest node R") Then crestRLine = ln
                If ln.StartsWith("# Crest node Y") Then crestYLine = ln
                If ln.Length > 0 AndAlso Not ln.StartsWith("#") AndAlso Not ln.StartsWith("Step,") Then
                    dataRowCount += 1
                    If firstData.Length = 0 Then firstData = ln
                    Dim ff() As String = ln.Split(","c)
                    If ff.Length > 1 Then
                        Dim tfv As Double
                        If Double.TryParse(ff(1).Trim(), tfv) Then timeFracs.Add(tfv)
                    End If
                End If
            Next

            ' ── Check 5: stepping uniformity + run completion (two distinct causes) ──
            ' 5a: step-size uniformity proves FIXED stepping (autostepping detector)
            ' 5b: last time_frac < 1.0 means the solver TERMINATED EARLY — data up
            '     to the stop is valid; this is reported as PARTIAL, not stepping
            '     failure (cf. SineLines A60 geometry-physical solver ceiling).
            Dim expectedStep As Double = 1.0 / CDbl(r.Steps)
            If timeFracs.Count >= 2 Then
                Dim maxDev As Double = 0.0
                For i As Integer = 1 To timeFracs.Count - 1
                    Dim dv As Double = Math.Abs((timeFracs(i) - timeFracs(i - 1)) - expectedStep)
                    If dv > maxDev Then maxDev = dv
                Next
                If maxDev > 0.000001 Then
                    pass = False
                    sb.Append(String.Format("[5a]non-uniform steps (max dev {0:G3}, autostepping?) ", maxDev))
                End If
            End If
            If timeFracs.Count > 0 AndAlso timeFracs(timeFracs.Count - 1) < 0.999 Then
                isPartial = True
                Dim xStop As Double = timeFracs(timeFracs.Count - 1) * r.MaxDisp
                sb.Append(String.Format("[5b]SOLVER STOPPED at step {0}/{1}, X={2:F2}mm of {3}mm (possible geometry-physical ceiling) ", _
                    dataRowCount, r.Steps, xStop, r.MaxDisp))
            ElseIf dataRowCount <> r.Steps Then
                pass = False
                sb.Append(String.Format("[5]rows {0} <> steps {1} ", dataRowCount, r.Steps))
            End If

            ' ── Check 1: Crest R vs expected geometry ──
            Dim expCrests As List(Of Double) = p.GetRollCrestRadii()
            Dim gotR As List(Of Double) = ParseNumList(crestRLine)
            If gotR.Count <> expCrests.Count Then
                pass = False
                sb.Append(String.Format("[1]crestR count {0}<>{1} ", gotR.Count, expCrests.Count))
            Else
                For ci As Integer = 0 To expCrests.Count - 1
                    Dim d As Double = Math.Abs(gotR(ci) - expCrests(ci))
                    If d > CrestR_Tol_mm Then
                        pass = False
                        sb.Append(String.Format("[1]roll{0} R off {1:F2}mm ", ci + 1, d))
                    End If
                Next
            End If

            ' ── Check 2: Crest |Y| vs nominal H_eff ──
            Dim gotY As List(Of Double) = ParseNumList(crestYLine)
            Dim uniformH As Boolean = (p.TaperPct = 0.0 AndAlso Not p.UseNaturalH)
            Dim H_eff As Double = p.H_pp / 2.0
            If uniformH Then
                For ci As Integer = 0 To gotY.Count - 1
                    Dim d As Double = Math.Abs(Math.Abs(gotY(ci)) - H_eff)
                    If d > CrestY_Tol_mm Then
                        pass = False
                        sb.Append(String.Format("[2]roll{0} |Y|={1:F3} vs Heff={2:F3} ", _
                            ci + 1, Math.Abs(gotY(ci)), H_eff))
                    End If
                Next
            Else
                sb.Append("[2]taper/natH: Y not gated ")
            End If

            ' ── Check 4: header E/Nu/Material vs row ──
            Dim hdrE As Double = ParseHeaderVal(hdrGeom, "E")
            Dim hdrNu As Double = ParseHeaderVal(hdrGeom, "Nu")
            Dim hdrMat As String = ParseHeaderMat(hdrGeom)
            If Math.Abs(hdrE - r.E) > 0.000001 Then
                pass = False
                sb.Append(String.Format("[4]E hdr={0} row={1} ", hdrE, r.E))
            End If
            If Math.Abs(hdrNu - r.Nu) > 0.000001 Then
                pass = False
                sb.Append(String.Format("[4]Nu hdr={0} row={1} ", hdrNu, r.Nu))
            End If
            If hdrMat.Length > 0 AndAlso hdrMat <> r.MaterialName Then
                pass = False
                sb.Append(String.Format("[4]Mat hdr='{0}' row='{1}' ", hdrMat, r.MaterialName))
            End If

            ' ── Check 3: Push/Pull Kms0 pair match within 1% ──
            Dim kms0 As Double = ParseKms0(firstData)
            sb.Append(String.Format("Kms0={0:F4} ", kms0))
            Dim mate As BatchRow = Nothing
            For Each o As BatchRow In allRows
                If Not Object.ReferenceEquals(o, r) AndAlso _
                   o.GeomKey() = r.GeomKey() AndAlso o.IsPull() <> r.IsPull() Then
                    mate = o
                    Exit For
                End If
            Next
            If mate IsNot Nothing AndAlso _
               (mate.Status = "PASS" OrElse mate.Status = "PARTIAL") AndAlso _
               System.IO.File.Exists(mate.OutputFile) Then
                Dim mateKms As Double = ParseKms0FromFile(mate.OutputFile)
                If kms0 > 0 AndAlso mateKms > 0 Then
                    Dim rel As Double = Math.Abs(kms0 - mateKms) / Math.Max(kms0, mateKms)
                    If rel > KmsPair_Tol Then
                        pass = False
                        sb.Append(String.Format("[3]pair Kms0 {0:F4}/{1:F4} diff {2:F2}% ", _
                            kms0, mateKms, rel * 100.0))
                    Else
                        sb.Append(String.Format("[3]pair OK {0:F2}% ", rel * 100.0))
                    End If
                Else
                    pass = False
                    sb.Append("[3]pair Kms0 unreadable ")
                End If
            End If

        Catch ex As System.Exception
            pass = False
            sb.Append("validate exception: " & ex.Message)
        End Try
        notes = sb.ToString().Trim()
        Return pass
    End Function

    Private Function ParseNumList(ByVal line As String) As List(Of Double)
        Dim result As New List(Of Double)()
        Try
            Dim ix As Integer = line.IndexOf(":")
            If ix < 0 Then Return result
            For Each s As String In line.Substring(ix + 1).Split(","c)
                Dim v As Double
                If Double.TryParse(s.Trim(), v) Then result.Add(v)
            Next
        Catch : End Try
        Return result
    End Function

    Private Function ParseHeaderVal(ByVal hdr As String, ByVal key As String) As Double
        Try
            For Each tok As String In hdr.Split(" "c)
                If tok.StartsWith(key & "=") Then
                    Dim v As Double
                    If Double.TryParse(tok.Substring(key.Length + 1), v) Then Return v
                End If
            Next
        Catch : End Try
        Return Double.NaN
    End Function

    Private Function ParseHeaderMat(ByVal hdr As String) As String
        Try
            Dim marker As String = "Material="
            Dim ix As Integer = hdr.IndexOf(marker)
            If ix >= 0 Then Return hdr.Substring(ix + marker.Length).Trim()
        Catch : End Try
        Return ""
    End Function

    Private Function ParseKms0(ByVal firstDataLine As String) As Double
        Try
            ' Columns: Step,Time_frac,X_applied_mm,F_reaction_N,Kms_N_per_mm,...
            Dim f() As String = firstDataLine.Split(","c)
            If f.Length > 4 Then
                Dim v As Double
                If Double.TryParse(f(4).Trim(), v) Then Return v
            End If
        Catch : End Try
        Return Double.NaN
    End Function

    Private Function ParseKms0FromFile(ByVal path As String) As Double
        Try
            For Each ln As String In System.IO.File.ReadAllLines(path)
                If ln.Length > 0 AndAlso Not ln.StartsWith("#") AndAlso _
                   Not ln.StartsWith("Step,") Then
                    Return ParseKms0(ln)
                End If
            Next
        Catch : End Try
        Return Double.NaN
    End Function
End Class
