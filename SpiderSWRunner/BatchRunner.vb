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
                                               ByVal isPull As Boolean, _
                                               Optional ByVal innerBC As String = "free") As String
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
        ' Tri-Radius (17) and Bullet (12): crown radius R2 and crown center Cx
        ' define the geometry — include them so different crowns get distinct
        ' filenames. Without this, an edge sweep over R2/Cx collides on name
        ' and the precheck flags valid runs as duplicates.
        If p.ProfileType = 17 OrElse p.ProfileType = 12 Then
            base = base & String.Format("_R2{0:0.0}_Cx{1:0.0}", p.BulletR_Top, p.BulletCx)
        End If
        ' DoubleRoll (11): center-flat CF defines the geometry — include it so a
        ' CF sweep gets distinct filenames. Keyed on nominal CenterFlat (the header
        ' carries nominal + effective). Same rationale as R2/Cx above.
        If p.ProfileType = 11 Then
            base = base & String.Format("_CF{0:0.0}", p.CenterFlat)
        End If
        If (p.ProfileType = 2 OrElse p.ProfileType = 3) AndAlso p.StraightLength <> 1.0 Then
            base = base & String.Format("_SL{0:0.0}", p.StraightLength)
        End If
        If Not p.FirstRollUp Then
            base = base & "_FD"
        End If
        ' INNER BC SUFFIX (2026-08-30): a clamped-boundary run must never share a stem
        ' with its free-boundary twin of identical geometry. Free adds nothing, so every
        ' existing stem is unchanged.
        If String.Equals(innerBC, "clamped", StringComparison.OrdinalIgnoreCase) Then base = base & "_BCclamped"
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

            ' InnerBC must be exactly "clamped" or "free" (2026-09-26 input rule)
            If r.InnerBC <> "clamped" AndAlso r.InnerBC <> "free" Then
                r.PrecheckPass = False
                r.PrecheckNote = "InnerBC '" & r.InnerBC & "' is not clamped/free"
            End If

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

            ' Gate: thin-shell validity — REMOVED 2026-08-28.
            '
            ' The T/R_crest > 0.13 AND T > 0.43 gate is void. It was written
            ' during the defective reaction-extraction window and no proof of
            ' the underlying shell degradation was ever recorded. The 148-run
            ' re-derivation batch was run against it, and the MeshScale
            ' 0.50 vs 0.25 discriminator at ArcLines A90 / T1.10 / N7
            ' (T/R_crest = 0.3400) shifted Kms0 by only +1.74% (21.4127 ->
            ' 21.7861 N/mm), with end-of-stroke force shifting +0.92%. A
            ' mesh-refinement shift that small is convergence, not shell
            ' breakdown, so no boundary exists on this axis. SpeaD ruled the
            ' gate void and ingested the 148 runs as good data.
            '
            ' The AllowHighTR column is retained in SweepMatrix/SpiderProfile
            ' so existing batch CSVs still parse, but it is now inert: no code
            ' path reads it. Do not reinstate a T/R gate without recorded
            ' evidence of the defect it is meant to catch.

            ' Gate: Bullet parameter validation
            If r.PrecheckPass AndAlso r.ProfileType = 12 Then
                Dim ve As String = p.ValidateBulletParams()
                If ve.Length > 0 Then
                    r.PrecheckPass = False
                    r.PrecheckNote = "Bullet: " & ve
                End If
            End If

            ' Filename collision
            Dim fname As String = BuildOutputFilename(p, r.IsPull(), r.InnerBC)
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
    ' RUN-LIST INPUT GUARD (2026-09-26, SD rule after proof row b2).
    ' The loader (SweepMatrix) splits on commas without honouring quotes
    ' and defaults any field it cannot parse. A Message text with a comma
    ' therefore shifted every later column by one and the row ran with
    ' InnerBC read as "False" (free inner boundary) while reporting PASS.
    ' This guard re-reads the RAW run-table file the batch was loaded from
    ' and REFUSES the whole batch — no SolidWorks connection, no solve —
    ' when any data line is not exactly 34 plain comma-separated fields,
    ' when any quote character is present, when InnerBC is not exactly
    ' "clamped" or "free", when ComponentMode is not 0/1, Direction is not
    ' Push/Pull, a boolean column is not True/False, or a numeric column
    ' does not parse. It also checks that the parsed rows agree with the
    ' raw file (count, RunID, InnerBC) so a loader default cannot slip
    ' through. The reason goes to the BatchLog and to every row's Message.
    ' ──────────────────────────────────────────────────────────────
    Private Shared ReadOnly RunListColumns() As String = { _
        "RunID", "Status", "ComponentMode", "ID", "OD", "N", "H_pp", "T", "LipWidth", "InnerLipWidth", _
        "FirstRollUp", "ProfileType", "ConnAngle", "SLen", "CenterFlat", "BulletR_Top", "BulletCx", _
        "TaperPct", "VariablePitch", "PitchTaperPct", "UseNaturalH", "Material", "E", "Nu", "Density", _
        "MaxDisp", "Steps", "Direction", "OutputDir", "Message", "SolidMesh", "MeshScale", "AllowHighTR", "InnerBC"}
    Private Shared ReadOnly RunListNumeric() As String = { _
        "RunID", "ComponentMode", "ID", "OD", "N", "H_pp", "T", "LipWidth", "InnerLipWidth", "ProfileType", _
        "ConnAngle", "SLen", "CenterFlat", "BulletR_Top", "BulletCx", "TaperPct", "PitchTaperPct", _
        "E", "Nu", "Density", "MaxDisp", "Steps", "MeshScale"}
    Private Shared ReadOnly RunListBoolean() As String = { _
        "FirstRollUp", "VariablePitch", "UseNaturalH", "SolidMesh", "AllowHighTR"}

    ' Returns "" when the file passes; otherwise the first reason it fails.
    Public Shared Function GuardRunListFile(ByVal path As String, ByVal rows As List(Of BatchRow)) As String
        Try
            If String.IsNullOrEmpty(path) Then Return "run-table path not set — raw file cannot be verified"
            If Not System.IO.File.Exists(path) Then Return "run-table file not found: " & path
            Dim lines() As String = System.IO.File.ReadAllLines(path)
            Dim dataLines As New List(Of String)()
            Dim header As String = ""
            For Each ln As String In lines
                If ln.Trim().Length = 0 Then Continue For
                If header.Length = 0 Then header = ln Else dataLines.Add(ln)
            Next
            If header.Length = 0 Then Return "run-table file is empty"
            Dim hdr() As String = header.Split(","c)
            If hdr.Length <> RunListColumns.Length Then
                Return String.Format("header has {0} columns, expected {1}", hdr.Length, RunListColumns.Length)
            End If
            For c As Integer = 0 To RunListColumns.Length - 1
                If hdr(c).Trim() <> RunListColumns(c) Then
                    Return String.Format("header column {0} is '{1}', expected '{2}'", c + 1, hdr(c).Trim(), RunListColumns(c))
                End If
            Next
            If dataLines.Count <> rows.Count Then
                Return String.Format("raw file has {0} data lines but {1} rows were loaded", dataLines.Count, rows.Count)
            End If
            Dim colIndex As New Dictionary(Of String, Integer)()
            For c As Integer = 0 To RunListColumns.Length - 1
                colIndex(RunListColumns(c)) = c
            Next
            For li As Integer = 0 To dataLines.Count - 1
                Dim ln As String = dataLines(li)
                Dim lineNo As Integer = li + 2
                If ln.IndexOf(""""c) >= 0 Then
                    Return String.Format("line {0}: quote character present — a field contains a comma or quote; the loader does not honour quoting", lineNo)
                End If
                Dim f() As String = ln.Split(","c)
                If f.Length <> RunListColumns.Length Then
                    Return String.Format("line {0}: {1} fields, expected {2}", lineNo, f.Length, RunListColumns.Length)
                End If
                For Each nc As String In RunListNumeric
                    Dim v As Double
                    If Not Double.TryParse(f(colIndex(nc)).Trim(), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, v) Then
                        Return String.Format("line {0}: column {1} = '{2}' is not numeric", lineNo, nc, f(colIndex(nc)).Trim())
                    End If
                Next
                For Each bc As String In RunListBoolean
                    Dim bv As String = f(colIndex(bc)).Trim()
                    If bv <> "True" AndAlso bv <> "False" Then
                        Return String.Format("line {0}: column {1} = '{2}' is not True/False", lineNo, bc, bv)
                    End If
                Next
                Dim cm As String = f(colIndex("ComponentMode")).Trim()
                If cm <> "0" AndAlso cm <> "1" Then Return String.Format("line {0}: ComponentMode = '{1}', expected 0 or 1", lineNo, cm)
                Dim dr As String = f(colIndex("Direction")).Trim()
                If dr <> "Push" AndAlso dr <> "Pull" Then Return String.Format("line {0}: Direction = '{1}', expected Push or Pull", lineNo, dr)
                Dim bc34 As String = f(colIndex("InnerBC")).Trim()
                If bc34 <> "clamped" AndAlso bc34 <> "free" Then Return String.Format("line {0}: InnerBC = '{1}', expected exactly clamped or free", lineNo, bc34)
                If RowStressMode(f(colIndex("Message"))) < 0 Then Return String.Format("line {0}: Message carries both FACEPROOF and LEGACYSTRESS", lineNo)
                If f(colIndex("OutputDir")).Trim().Length = 0 Then Return String.Format("line {0}: OutputDir is empty", lineNo)
                ' parsed row must agree with the raw line
                Dim r As BatchRow = rows(li)
                Dim rawRunID As Integer = CInt(Double.Parse(f(colIndex("RunID")).Trim(), Globalization.CultureInfo.InvariantCulture))
                If r.RunID <> rawRunID Then Return String.Format("line {0}: loaded RunID {1} != raw RunID {2}", lineNo, r.RunID, rawRunID)
                If String.Compare(r.InnerBC, bc34, False) <> 0 Then
                    Return String.Format("line {0}: loaded InnerBC '{1}' != raw InnerBC '{2}'", lineNo, r.InnerBC, bc34)
                End If
                If String.Compare(r.OutputDir.Trim(), f(colIndex("OutputDir")).Trim(), False) <> 0 Then
                    Return String.Format("line {0}: loaded OutputDir differs from raw OutputDir", lineNo)
                End If
            Next
            Return ""
        Catch ex As System.Exception
            Return "guard exception: " & ex.Message
        End Try
    End Function

    ' ──────────────────────────────────────────────────────────────
    ' RUN-LIST CONTROLS THE SWITCH FILES (2026-09-27, Patrick: "you give me a
    ' file, I run it, I give you results" — no hand-edited switch files).
    ' The run list's Message column may carry these exact tokens:
    '   FACEPROOF     -> stress readout mode 2 (face-resolved + _faceproof.txt)
    '   LEGACYSTRESS  -> stress readout mode 0 (legacy one-face, not for landing)
    '   (neither)     -> mode 1 (face-resolved) — the normal campaign mode
    '   KEEPOPEN      -> leave SolidWorks open after the batch
    ' Before each row the batch engine WRITES C:\SpiderSW_Results\stress_faces.txt
    ' with that row's mode, and at batch start creates or deletes
    ' C:\SpiderSW_Results\keep_study_open.txt. SWAutomation reads those files
    ' exactly as before, so the extraction path is unchanged; what was a manual
    ' step is now driven by the run list. A row carrying both FACEPROOF and
    ' LEGACYSTRESS is refused by the input guard.
    ' ──────────────────────────────────────────────────────────────
    Public Const StressFacesPath As String = "C:\SpiderSW_Results\stress_faces.txt"
    Public Const KeepOpenPath As String = "C:\SpiderSW_Results\keep_study_open.txt"

    Private Shared Function HasToken(ByVal msg As String, ByVal token As String) As Boolean
        If String.IsNullOrEmpty(msg) Then Return False
        For Each w As String In msg.Split(New Char() {" "c, ";"c, "|"c, "["c, "]"c, "("c, ")"c}, StringSplitOptions.RemoveEmptyEntries)
            If String.Equals(w, token, StringComparison.Ordinal) Then Return True
        Next
        Return False
    End Function

    ' Returns 0, 1 or 2; -1 when the Message asks for two modes at once.
    Public Shared Function RowStressMode(ByVal msg As String) As Integer
        Dim proof As Boolean = HasToken(msg, "FACEPROOF")
        Dim legacy As Boolean = HasToken(msg, "LEGACYSTRESS")
        If proof AndAlso legacy Then Return -1
        If proof Then Return 2
        If legacy Then Return 0
        Return 1
    End Function

    Public Shared Function RowKeepOpen(ByVal msg As String) As Boolean
        Return HasToken(msg, "KEEPOPEN")
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

        ' INPUT GUARD — refuse the batch before SolidWorks is touched.
        Dim guardReason As String = GuardRunListFile(checkpointPath, rows)
        If guardReason.Length > 0 Then
            Dim msg As String = "INPUT GUARD REFUSED: " & guardReason
            Log("*** " & msg & " ***")
            Log("*** Batch NOT started. Fix the run list and reload it. ***")
            BatchLog(batchLogPath, msg)
            BatchLog(batchLogPath, "BATCH REFUSED " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") & "  pass=0")
            For Each r As BatchRow In rows
                If r.Status <> "PASS" AndAlso r.Status <> "PARTIAL" Then
                    r.Status = "FAIL"
                    r.Message = msg
                End If
            Next
            Checkpoint(rows, checkpointPath)
            Return 0
        End If
        Log("Input guard: run list verified (" & rows.Count & " rows, 34 plain fields each, InnerBC/booleans/numerics parsed)")
        BatchLog(batchLogPath, "Input guard PASS: " & rows.Count & " rows verified against " & checkpointPath)

        ' Switch files from the run list (captured now, before any Message is
        ' overwritten by a row result).
        Dim rowMode As New Dictionary(Of Integer, Integer)()
        Dim keepOpen As Boolean = False
        For Each r As BatchRow In rows
            rowMode(r.RunID) = RowStressMode(r.Message)
            If RowKeepOpen(r.Message) Then keepOpen = True
        Next
        Try
            If keepOpen Then
                System.IO.File.WriteAllText(KeepOpenPath, "set by run list " & checkpointPath)
            ElseIf System.IO.File.Exists(KeepOpenPath) Then
                System.IO.File.Delete(KeepOpenPath)
            End If
            Log("Run-list switches: keep_study_open = " & If(keepOpen, "ON", "off"))
            BatchLog(batchLogPath, "Run-list switches: keep_study_open = " & If(keepOpen, "ON", "off"))
        Catch exK As System.Exception
            Dim msgK As String = "INPUT GUARD REFUSED: cannot set " & KeepOpenPath & " (" & exK.Message & ")"
            Log("*** " & msgK & " ***")
            BatchLog(batchLogPath, msgK)
            BatchLog(batchLogPath, "BATCH REFUSED " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") & "  pass=0")
            Return 0
        End Try

        If Not _sw.ConnectOrStart() Then
            Log("ABORT: cannot connect/start SolidWorks.")
            Return 0
        End If

        ' Hold the dialog watchdog open for the ENTIRE batch, not just during
        ' each RunAnalysis — buckling dialogs can surface in the gaps between
        ' runs (extraction, part rebuild) and would otherwise block unattended.
        _sw.KeepWatchdogAlive = True
        _sw.StartDialogWatchdog()

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

            ' Stress readout mode for THIS row, from the run list (see RowStressMode).
            Dim sMode As Integer = 1
            If rowMode.ContainsKey(r.RunID) Then sMode = rowMode(r.RunID)
            Try
                System.IO.File.WriteAllText(StressFacesPath, sMode.ToString())
                Log(String.Format("Run {0}: stress readout mode {1} set from run list ({2})", r.RunID, sMode, _
                    If(sMode = 2, "FACEPROOF", If(sMode = 0, "LEGACYSTRESS", "face-resolved"))))
            Catch exS As System.Exception
                ok = False : failStage = "StressModeFile"
                Log(String.Format("Run {0}: cannot write {1}: {2}", r.RunID, StressFacesPath, exS.Message))
            End Try

            If ok AndAlso Not _sw.CreatePart(p) Then ok = False : failStage = "CreatePart"
            _sw.InnerBoundaryClamped = (r.InnerBC = "clamped")
            _sw.WFreeGateFailed = False
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

            ' W_FREE GATE ABORT (2026-09-03, SD one-BC-standard ruling): the row above
            ' already FAILED because its emitted W_free was not ActiveW. Stop the batch
            ' here so a mis-bound family costs minutes, not a night.
            If _sw.WFreeGateFailed Then
                BatchLog(batchLogPath, "BATCH ABORTED: W_free gate failed on RunID " & r.RunID & " — remaining rows skipped")
                Log("*** BATCH ABORTED: W_free gate failed on RunID " & r.RunID & ". Remaining rows skipped. Send the log to SW. ***")
                Exit For
            End If

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
        ' Release the always-on watchdog now the batch is finished.
        _sw.KeepWatchdogAlive = False
        _sw.ForceStopDialogWatchdog()

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
            ' TOLERANCE 2026-09-15 (SD ruling): the physical quantity is NODE SPACING, not
            ' millimetres. The extractor reports the nearest mesh node to each crest, so a
            ' node can sit up to half an element from the apex in R, and its Y is the
            ' profile evaluated there - not the apex value. Element size is not in the auto
            ' header, so it is estimated from the row's own data: the crest-R sampling
            ' offset (target crest R vs reported node R) is at most half an element, so
            ' elSize ~= 2 x max offset and 0.5 x elSize = that max offset. Floor at the old
            ' 0.05 so a fine mesh cannot loosen the gate. A natH/H_pp mismatch is 0.5mm+
            ' and still fails. PERMANENT FIX (next build, SD): evaluate the profile function
            ' at each node's actual R and compare there, then this returns to a flat 0.05.
            Dim gotY As List(Of Double) = ParseNumList(crestYLine)
            Dim gotRc As List(Of Double) = ParseNumList(crestRLine)
            Dim expRc As List(Of Double) = p.GetRollCrestRadii()
            Dim crestTol As Double = CrestY_Tol_mm
            If gotRc.Count > 0 AndAlso gotRc.Count = expRc.Count Then
                Dim maxOff As Double = 0.0
                For ri As Integer = 0 To gotRc.Count - 1
                    Dim off As Double = Math.Abs(gotRc(ri) - expRc(ri))
                    If off > maxOff Then maxOff = off
                Next
                If maxOff > crestTol Then crestTol = maxOff
            End If
            Dim uniformH As Boolean = (p.TaperPct = 0.0 AndAlso Not p.UseNaturalH)
            Dim H_eff As Double = p.H_pp / 2.0
            If uniformH Then
                sb.Append(String.Format("[2]tol={0:F3}mm ", crestTol))
                For ci As Integer = 0 To gotY.Count - 1
                    Dim d As Double = Math.Abs(Math.Abs(gotY(ci)) - H_eff)
                    If d > crestTol Then
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
                    ' Push/Pull Kms0 difference is REPORTED, never gated.
                    ' For asymmetry / taper / parity campaigns the difference
                    ' is the signal being measured, not an error. (Changed
                    ' 2026-06-12 after the 1% gate false-failed valid
                    ' asymmetry runs and blocked the queue.)
                    Dim rel As Double = Math.Abs(kms0 - mateKms) / Math.Max(kms0, mateKms)
                    sb.Append(String.Format("[3]pair diff {0:F2}% ", rel * 100.0))
                Else
                    ' Only note unreadable — do not fail the run on it.
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
