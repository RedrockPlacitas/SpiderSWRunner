Imports System
Imports System.Collections.Generic
Imports System.Windows.Forms
Imports System.Drawing
Imports Microsoft.VisualBasic

' ══════════════════════════════════════════════════════════════════
'  BatchForm.vb — sweep definition + batch execution UI
'
'  Workflow:  define swept variables -> Generate Matrix (expands,
'  prechecks, previews) -> Run Batch (unattended, checkpointed).
'  The generated matrix serializes to the same CSV run table the
'  engine consumes — exportable directly into SpiderDesigner /
'  EdgeDesigner handoff documents.
' ══════════════════════════════════════════════════════════════════

Public Class BatchForm
    Inherits Form

    Private Const SweepSlots As Integer = 5

    Private _baseProvider As Func(Of BatchRow)
    Private _base As BatchRow
    Private _sw As SWAutomation
    Private _runner As BatchRunner
    Private _rows As List(Of BatchRow) = Nothing
    Private _running As Boolean = False

    Private cbVar(SweepSlots - 1) As ComboBox
    Private tbFrom(SweepSlots - 1) As TextBox
    Private tbTo(SweepSlots - 1) As TextBox
    Private tbCount(SweepSlots - 1) As TextBox
    Private tbList(SweepSlots - 1) As TextBox
    Private cbSweepMode As ComboBox
    Private chkBothDir As CheckBox
    Private chkPauseFirst As CheckBox
    Private nudRestartEvery As NumericUpDown
    Private tbMinPerRun As TextBox
    Private tbCheckpoint As TextBox
    Private btnGenerate As Button
    Private btnDeleteRows As Button
    Private btnLoadCsv As Button
    Private btnSaveCsv As Button
    Private btnRun As Button
    Private btnAbort As Button
    Private grid As DataGridView
    Private lblSummary As Label
    Private txtLog As TextBox

    Public Sub New(ByVal baseProvider As Func(Of BatchRow), ByVal sw As SWAutomation)
        _baseProvider = baseProvider
        _base = baseProvider()
        _sw = sw
        _runner = New BatchRunner(_sw, AddressOf LogMsg)
        _runner.VerifyFirstRun = AddressOf VerifyFirstRunDialog
        _runner.Progress = AddressOf OnProgress
        BuildUi()
    End Sub

    Private Sub BuildUi()
        Me.Text = "Batch Sweep — SpiderSWRunner"
        Me.ClientSize = New Size(940, 720)
        Me.StartPosition = FormStartPosition.CenterParent

        Dim y As Integer = 8
        Dim lbl As Label

        lbl = New Label()
        lbl.Text = "Swept variables (others held at the values currently in the main form):"
        lbl.Location = New Point(8, y)
        lbl.AutoSize = True
        Me.Controls.Add(lbl)
        y += 20

        ' Column headers for the sweep matrix
        Dim hdrFrom As Label = New Label()
        hdrFrom.Text = "From" : hdrFrom.Location = New Point(134, y) : hdrFrom.AutoSize = True
        Me.Controls.Add(hdrFrom)
        Dim hdrTo As Label = New Label()
        hdrTo.Text = "To" : hdrTo.Location = New Point(204, y) : hdrTo.AutoSize = True
        Me.Controls.Add(hdrTo)
        Dim hdrN As Label = New Label()
        hdrN.Text = "# Values" : hdrN.Location = New Point(274, y) : hdrN.AutoSize = True
        Me.Controls.Add(hdrN)
        Dim hdrL As Label = New Label()
        hdrL.Text = "or explicit list (overrides From/To): 0.25,0.30,0.38"
        hdrL.Location = New Point(344, y) : hdrL.AutoSize = True : hdrL.ForeColor = Color.DimGray
        Me.Controls.Add(hdrL)
        y += 18

        For i As Integer = 0 To SweepSlots - 1
            cbVar(i) = New ComboBox()
            cbVar(i).Location = New Point(8, y)
            cbVar(i).Size = New Size(120, 22)
            cbVar(i).DropDownStyle = ComboBoxStyle.DropDownList
            cbVar(i).Items.Add("(none)")
            For Each n As String In SweepMatrix.VarNames
                cbVar(i).Items.Add(n)
            Next
            cbVar(i).SelectedIndex = 0
            Me.Controls.Add(cbVar(i))

            tbFrom(i) = New TextBox()
            tbFrom(i).Location = New Point(134, y)
            tbFrom(i).Size = New Size(62, 22)
            Me.Controls.Add(tbFrom(i))

            tbTo(i) = New TextBox()
            tbTo(i).Location = New Point(204, y)
            tbTo(i).Size = New Size(62, 22)
            Me.Controls.Add(tbTo(i))

            tbCount(i) = New TextBox()
            tbCount(i).Location = New Point(274, y)
            tbCount(i).Size = New Size(62, 22)
            Me.Controls.Add(tbCount(i))

            tbList(i) = New TextBox()
            tbList(i).Location = New Point(344, y)
            tbList(i).Size = New Size(300, 22)
            Me.Controls.Add(tbList(i))
            y += 26
        Next

        y += 4
        lbl = New Label() : lbl.Text = "Mode:" : lbl.Location = New Point(8, y + 3) : lbl.AutoSize = True
        Me.Controls.Add(lbl)
        cbSweepMode = New ComboBox()
        cbSweepMode.Location = New Point(50, y)
        cbSweepMode.Size = New Size(140, 22)
        cbSweepMode.DropDownStyle = ComboBoxStyle.DropDownList
        cbSweepMode.Items.AddRange(New Object() {"One-at-a-time", "Full factorial"})
        cbSweepMode.SelectedIndex = 0
        Me.Controls.Add(cbSweepMode)

        chkBothDir = New CheckBox()
        chkBothDir.Text = "Push + Pull pairs"
        chkBothDir.Location = New Point(200, y)
        chkBothDir.Size = New Size(130, 22)
        chkBothDir.Checked = True
        Me.Controls.Add(chkBothDir)

        chkPauseFirst = New CheckBox()
        chkPauseFirst.Text = "Pause after run 1 for verification"
        chkPauseFirst.Location = New Point(335, y)
        chkPauseFirst.Size = New Size(210, 22)
        chkPauseFirst.Checked = True
        Me.Controls.Add(chkPauseFirst)

        lbl = New Label() : lbl.Text = "Restart SW every:" : lbl.Location = New Point(550, y + 3) : lbl.AutoSize = True
        Me.Controls.Add(lbl)
        nudRestartEvery = New NumericUpDown()
        nudRestartEvery.Location = New Point(650, y)
        nudRestartEvery.Size = New Size(50, 22)
        nudRestartEvery.Minimum = 0
        nudRestartEvery.Maximum = 200
        nudRestartEvery.Value = 12
        Me.Controls.Add(nudRestartEvery)
        lbl = New Label() : lbl.Text = "runs (0=never)" : lbl.Location = New Point(704, y + 3) : lbl.AutoSize = True
        Me.Controls.Add(lbl)

        lbl = New Label() : lbl.Text = "Min/run est:" : lbl.Location = New Point(800, y + 3) : lbl.AutoSize = True
        Me.Controls.Add(lbl)
        tbMinPerRun = New TextBox()
        tbMinPerRun.Location = New Point(872, y)
        tbMinPerRun.Size = New Size(40, 22)
        tbMinPerRun.Text = "8"
        Me.Controls.Add(tbMinPerRun)
        y += 30

        lbl = New Label() : lbl.Text = "Checkpoint run-table CSV:" : lbl.Location = New Point(8, y + 3) : lbl.AutoSize = True
        Me.Controls.Add(lbl)
        tbCheckpoint = New TextBox()
        tbCheckpoint.Location = New Point(155, y)
        tbCheckpoint.Size = New Size(540, 22)
        tbCheckpoint.Text = System.IO.Path.Combine(_base.OutputDir, "BatchRunTable.csv")
        Me.Controls.Add(tbCheckpoint)
        y += 30

        btnGenerate = MakeBtn("Generate Matrix", 8, y, 120)
        AddHandler btnGenerate.Click, AddressOf btnGenerate_Click
        btnLoadCsv = MakeBtn("Load CSV...", 132, y, 90)
        AddHandler btnLoadCsv.Click, AddressOf btnLoadCsv_Click
        btnSaveCsv = MakeBtn("Save CSV...", 226, y, 90)
        AddHandler btnSaveCsv.Click, AddressOf btnSaveCsv_Click
        btnDeleteRows = MakeBtn("Delete Selected Rows", 320, y, 140)
        AddHandler btnDeleteRows.Click, AddressOf btnDeleteRows_Click
        btnRun = MakeBtn("RUN BATCH", 468, y, 130)
        btnRun.BackColor = Color.FromArgb(200, 240, 200)
        btnRun.Enabled = False
        AddHandler btnRun.Click, AddressOf btnRun_Click
        btnAbort = MakeBtn("ABORT", 602, y, 80)
        btnAbort.BackColor = Color.FromArgb(250, 200, 200)
        btnAbort.Enabled = False
        AddHandler btnAbort.Click, AddressOf btnAbort_Click

        lblSummary = New Label()
        lblSummary.Text = "No matrix generated."
        lblSummary.Location = New Point(690, y + 8)
        lblSummary.AutoSize = True
        lblSummary.Font = New Font(Me.Font, FontStyle.Bold)
        Me.Controls.Add(lblSummary)
        y += 40

        grid = New DataGridView()
        grid.Location = New Point(8, y)
        grid.Size = New Size(924, 240)
        grid.ReadOnly = True
        grid.AllowUserToAddRows = False
        grid.AllowUserToDeleteRows = False
        grid.RowHeadersVisible = False
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        grid.MultiSelect = True
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells
        grid.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        Me.Controls.Add(grid)
        y += 248

        txtLog = New TextBox()
        txtLog.Multiline = True
        txtLog.ScrollBars = ScrollBars.Both
        txtLog.WordWrap = False
        txtLog.ReadOnly = True
        txtLog.BackColor = Color.FromArgb(20, 20, 30)
        txtLog.ForeColor = Color.FromArgb(180, 220, 180)
        txtLog.Font = New Font("Consolas", 9.0F)
        txtLog.Location = New Point(8, y)
        txtLog.Size = New Size(924, Me.ClientSize.Height - y - 8)
        txtLog.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
        Me.Controls.Add(txtLog)
    End Sub

    Private Function MakeBtn(ByVal text As String, ByVal x As Integer, _
                             ByVal y As Integer, ByVal w As Integer) As Button
        Dim b As New Button()
        b.Text = text
        b.Location = New Point(x, y)
        b.Size = New Size(w, 30)
        Me.Controls.Add(b)
        Return b
    End Function

    Private Sub LogMsg(ByVal msg As String)
        Try
            txtLog.AppendText(msg & System.Environment.NewLine)
            Application.DoEvents()
        Catch : End Try
    End Sub

    ' ── Generate matrix from sweep definition ──
    Private Sub btnGenerate_Click(ByVal sender As Object, ByVal e As EventArgs)
        Try
            ' Re-read the base configuration from the main form so edits
            ' made after this window opened are always honored.
            _base = _baseProvider()
            LogMsg(String.Format("Base re-read from main form: SLen={0} Angle={1} T={2} H_pp={3} OD={4} Dir={5} Out={6}", _
                _base.StraightLength, _base.ConnectorAngle, _base.T, _base.H_pp, _base.OD, _base.Direction, _base.OutputDir))
            Dim vars As New List(Of SweepVar)()
            For i As Integer = 0 To SweepSlots - 1
                If cbVar(i).SelectedIndex = 0 Then Continue For
                Dim sv As New SweepVar()
                sv.Name = cbVar(i).SelectedItem.ToString()

                If tbList(i).Text.Trim().Length > 0 Then
                    ' Explicit list wins (commas; start:step:stop also accepted)
                    sv.Values = SweepVar.ParseValues(tbList(i).Text)
                ElseIf tbFrom(i).Text.Trim().Length > 0 Then
                    Dim vFrom As Double = Double.Parse(tbFrom(i).Text.Trim())
                    Dim vTo As Double = vFrom
                    If tbTo(i).Text.Trim().Length > 0 Then vTo = Double.Parse(tbTo(i).Text.Trim())
                    Dim nVals As Integer = 1
                    If tbCount(i).Text.Trim().Length > 0 Then nVals = Integer.Parse(tbCount(i).Text.Trim())
                    If nVals < 1 Then nVals = 1
                    Dim vals As New List(Of Double)()
                    If nVals = 1 OrElse Math.Abs(vTo - vFrom) < 0.000000001 Then
                        vals.Add(vFrom)
                    Else
                        ' nVals values inclusive of both endpoints
                        Dim stepSz As Double = (vTo - vFrom) / CDbl(nVals - 1)
                        For k As Integer = 0 To nVals - 1
                            vals.Add(Math.Round(vFrom + stepSz * CDbl(k), 9))
                        Next
                    End If
                    sv.Values = vals.ToArray()
                    LogMsg(String.Format("  {0}: From {1} To {2}, {3} values -> {4}", _
                        sv.Name, vFrom, vTo, nVals, _
                        String.Join(", ", Array.ConvertAll(sv.Values, Function(d) d.ToString()))))
                Else
                    Continue For
                End If
                If sv.Values.Length > 0 Then vars.Add(sv)
            Next
            Dim mode As Integer = If(cbSweepMode.SelectedIndex = 1, _
                SweepMatrix.ModeFullFactorial, SweepMatrix.ModeOneAtATime)
            _rows = SweepMatrix.Expand(_base, vars, mode, chkBothDir.Checked)
            LogMsg(String.Format("Matrix generated: {0} runs", _rows.Count))

            Dim allOk As Boolean = _runner.PrecheckAll(_rows)
            FillGrid()
            UpdateSummary()
            btnRun.Enabled = allOk AndAlso _rows.Count > 0
            If Not allOk Then
                LogMsg("Precheck FAILED — fix flagged rows (red) before running.")
            End If
        Catch ex As System.Exception
            LogMsg("Generate failed: " & ex.Message)
        End Try
    End Sub

    Private Sub btnLoadCsv_Click(ByVal sender As Object, ByVal e As EventArgs)
        Try
            Using dlg As New OpenFileDialog()
                dlg.Filter = "CSV run table|*.csv"
                dlg.InitialDirectory = _base.OutputDir
                If dlg.ShowDialog() = DialogResult.OK Then
                    _rows = SweepMatrix.LoadCsv(dlg.FileName)
                    tbCheckpoint.Text = dlg.FileName
                    LogMsg(String.Format("Loaded {0} rows from {1}", _rows.Count, dlg.FileName))
                    Dim allOk As Boolean = _runner.PrecheckAll(_rows)
                    FillGrid()
                    UpdateSummary()
                    btnRun.Enabled = allOk AndAlso _rows.Count > 0
                End If
            End Using
        Catch ex As System.Exception
            LogMsg("Load failed: " & ex.Message)
        End Try
    End Sub

    Private Sub btnSaveCsv_Click(ByVal sender As Object, ByVal e As EventArgs)
        If _rows Is Nothing OrElse _rows.Count = 0 Then
            LogMsg("Nothing to save — generate a matrix first.")
            Return
        End If
        Try
            Using dlg As New SaveFileDialog()
                dlg.Filter = "CSV run table|*.csv"
                dlg.InitialDirectory = _base.OutputDir
                dlg.FileName = "BatchRunTable.csv"
                If dlg.ShowDialog() = DialogResult.OK Then
                    SweepMatrix.SaveCsv(_rows, dlg.FileName)
                    tbCheckpoint.Text = dlg.FileName
                    LogMsg("Saved: " & dlg.FileName)
                End If
            End Using
        Catch ex As System.Exception
            LogMsg("Save failed: " & ex.Message)
        End Try
    End Sub

    Private Sub btnRun_Click(ByVal sender As Object, ByVal e As EventArgs)
        If _running OrElse _rows Is Nothing OrElse _rows.Count = 0 Then Return

        ' Standing-rule gate: backup before every sweep run
        Dim msg As String = _
            "Standing rule check before starting the batch:" & vbCrLf & vbCrLf & _
            "1. Database / results backup completed?" & vbCrLf & _
            "2. Precheck table reviewed (geometry + collisions)?" & vbCrLf & _
            "3. SpiderDesigner / EdgeDesigner authorization for this matrix?" & vbCrLf & vbCrLf & _
            "Start the batch?"
        If MessageBox.Show(msg, "Pre-sweep checklist", MessageBoxButtons.YesNo, _
                           MessageBoxIcon.Question) <> DialogResult.Yes Then
            LogMsg("Batch start declined at pre-sweep checklist.")
            Return
        End If

        _running = True
        btnRun.Enabled = False
        btnGenerate.Enabled = False
        btnLoadCsv.Enabled = False
        btnAbort.Enabled = True
        Try
            Dim passCount As Integer = _runner.Execute(_rows, tbCheckpoint.Text.Trim(), _
                CInt(nudRestartEvery.Value), chkPauseFirst.Checked)
            FillGrid()
            UpdateSummary()
            LogMsg(String.Format("Session result: {0} PASS", passCount))
        Catch ex As System.Exception
            LogMsg("Batch exception: " & ex.Message)
        Finally
            _running = False
            btnRun.Enabled = True
            btnGenerate.Enabled = True
            btnLoadCsv.Enabled = True
            btnAbort.Enabled = False
        End Try
    End Sub

    Private Sub btnAbort_Click(ByVal sender As Object, ByVal e As EventArgs)
        _runner.AbortRequested = True
        LogMsg("Abort requested — batch will stop before the next row.")
    End Sub

    Private Sub btnDeleteRows_Click(ByVal sender As Object, ByVal e As EventArgs)
        ' Prune hand-picked rows from the run table (for coupled-tuple
        ' campaigns: generate the nearest factorial, then delete the
        ' combinations that are not wanted). RunIDs are kept stable for
        ' traceability; the table re-prechecks after pruning.
        If _running Then Return
        If _rows Is Nothing OrElse grid.SelectedRows.Count = 0 Then
            LogMsg("No rows selected.")
            Return
        End If
        Dim doomed As New Dictionary(Of Integer, Boolean)()
        For Each gr As DataGridViewRow In grid.SelectedRows
            Dim rid As Integer
            If Integer.TryParse(Convert.ToString(gr.Cells(0).Value), rid) Then
                doomed(rid) = True
            End If
        Next
        ' Refuse to delete rows that already ran — their files exist and the
        ' table is the record of them.
        Dim kept As New List(Of BatchRow)()
        Dim removed As Integer = 0
        Dim refused As Integer = 0
        For Each r As BatchRow In _rows
            If doomed.ContainsKey(r.RunID) Then
                If r.Status = "PASS" OrElse r.Status = "PARTIAL" OrElse r.Status = "FAIL" Then
                    refused += 1
                    kept.Add(r)
                Else
                    removed += 1
                End If
            Else
                kept.Add(r)
            End If
        Next
        _rows = kept
        LogMsg(String.Format("Deleted {0} row(s).{1}", removed, _
            If(refused > 0, String.Format(" Refused {0} already-run row(s) — they are the record of completed work.", refused), "")))
        Dim allOk As Boolean = False
        If _rows.Count > 0 Then allOk = _runner.PrecheckAll(_rows)
        FillGrid()
        UpdateSummary()
        btnRun.Enabled = allOk AndAlso _rows.Count > 0
    End Sub

    Private Function VerifyFirstRunDialog(ByVal r As BatchRow) As Boolean
        Dim msg As String = String.Format( _
            "Run 1 complete ({0})." & vbCrLf & vbCrLf & _
            "Verify the output files and DB writes now:" & vbCrLf & "{1}" & vbCrLf & vbCrLf & _
            "Continue with the remaining rows unattended?", _
            r.Status, r.OutputFile)
        Return MessageBox.Show(msg, "First-run verification", _
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) = DialogResult.Yes
    End Function

    Private Sub OnProgress(ByVal i As Integer, ByVal total As Integer, ByVal r As BatchRow)
        Try
            Me.Text = String.Format("Batch Sweep — run {0}/{1} (RunID {2})", i + 1, total, r.RunID)
            Application.DoEvents()
        Catch : End Try
    End Sub

    Private Sub FillGrid()
        grid.Columns.Clear()
        grid.Rows.Clear()
        For Each c As String In New String() { _
            "RunID", "Status", "Dir", "OD", "ID", "N", "H_pp", "T", "Lip", _
            "Type", "Angle", "SLen", "Taper", "Pt%", "natH", "Mat", "E", "MaxDisp", "Steps", _
            "EffPitch", "MaxHpp", "Margin", "Precheck", "Note/Message"}
            grid.Columns.Add(c, c)
        Next
        If _rows Is Nothing Then Return
        For Each r As BatchRow In _rows
            Dim ri As Integer = grid.Rows.Add( _
                r.RunID, r.Status, r.Direction, r.OD, r.ID, r.N, r.H_pp, r.T, r.LipWidth, _
                BatchRunner.ProfileTag(r.ToProfile()), r.ConnectorAngle, r.StraightLength, _
                r.TaperPct, r.PitchTaperPct, If(r.UseNaturalH, "Y", ""), _
                r.MaterialName, r.E, _
                r.MaxDisp, r.Steps, _
                r.EffPitch.ToString("F3"), r.MaxHpp.ToString("F2"), r.Margin.ToString("F2"), _
                If(r.PrecheckPass, "PASS", "FAIL"), _
                If(r.Message.Length > 0, r.Message, r.PrecheckNote))
            If Not r.PrecheckPass Then
                grid.Rows(ri).DefaultCellStyle.BackColor = Color.FromArgb(255, 215, 215)
            ElseIf r.Status = "PASS" Then
                grid.Rows(ri).DefaultCellStyle.BackColor = Color.FromArgb(215, 245, 215)
            ElseIf r.Status = "PARTIAL" Then
                grid.Rows(ri).DefaultCellStyle.BackColor = Color.FromArgb(255, 250, 200)
            ElseIf r.Status = "FAIL" Then
                grid.Rows(ri).DefaultCellStyle.BackColor = Color.FromArgb(255, 235, 200)
            End If
        Next
    End Sub

    Private Sub UpdateSummary()
        If _rows Is Nothing Then Return
        Dim pend As Integer = 0, passed As Integer = 0, failed As Integer = 0, badPre As Integer = 0
        For Each r As BatchRow In _rows
            If Not r.PrecheckPass Then badPre += 1
            Select Case r.Status
                Case "PASS", "PARTIAL" : passed += 1
                Case "FAIL", "SKIP" : failed += 1
                Case Else : pend += 1
            End Select
        Next
        Dim mpr As Double = 8.0
        Double.TryParse(tbMinPerRun.Text, mpr)
        Dim est As Double = pend * mpr / 60.0
        lblSummary.Text = String.Format( _
            "{0} runs | pending {1} | pass {2} | fail {3} | precheck-fail {4} | est {5:F1} h", _
            _rows.Count, pend, passed, failed, badPre, est)
    End Sub
End Class
