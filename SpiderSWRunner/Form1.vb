Imports System
Imports System.Windows.Forms
Imports Microsoft.VisualBasic

    Partial Public Class Form1
        Inherits Form

        Private WithEvents _sw As SWAutomation
        Private _suppressModeChange As Boolean = False

        Public Sub New()
            InitializeComponent()
            _sw = New SWAutomation(AddressOf LogMessage)

            ' Default to Edge mode
            cbMode.SelectedIndex = 1

            ' Banner
            LogMessage("Spider / Edge -> SolidWorks Runner ready.")
            LogMessage("Workflow: Connect -> Create Part -> Setup Study -> Mesh+Run -> Extract")
            LogMessage("Or click 'Run All' to do everything in sequence.")

            UpdateComputed()
        End Sub

        ' Filename convention lives in BatchRunner (single source of truth
        ' for single runs AND batch runs). These wrappers preserve callers.
        Private Function ProfileTag(p As SpiderProfile) As String
            Return BatchRunner.ProfileTag(p)
        End Function

        ''' <summary>
        ''' Returns True if Pull (-Y) is selected, False for Push (+Y).
        ''' </summary>
        Private Function IsPullDirection() As Boolean
            If cbDirection IsNot Nothing Then
                Return (cbDirection.SelectedIndex = 1)
            End If
            Return False
        End Function

        Private Function BuildOutputFilename(p As SpiderProfile) As String
            Return BatchRunner.BuildOutputFilename(p, IsPullDirection())
        End Function

        Private Function D(ByVal tb As TextBox, ByVal def As Double) As Double
            Dim val As Double
            If Double.TryParse(tb.Text, val) Then
                Return val
            End If
            Return def
        End Function

        Private Function I(ByVal tb As TextBox, ByVal def As Integer) As Integer
            Dim val As Integer
            If Integer.TryParse(tb.Text, val) Then
                Return val
            End If
            Return def
        End Function

        ''' <summary>
        ''' Map the profile combo's selected index to a ProfileType integer.
        ''' Spider mode: 0=Sinusoidal, 1=CircularArc, 2=ArcLines, 3=SineLines, 4=Triangle(Accordion)
        ''' Edge mode:   10=HalfRoll, 11=DoubleRoll, 12=Bullet, then 0,1,2,3
        ''' </summary>
        Private Function ProfileTypeFromCombo() As Integer
            Dim idx As Integer = cbProfile.SelectedIndex
            Dim isEdge As Boolean = (cbMode IsNot Nothing AndAlso cbMode.SelectedIndex = 1)

            If isEdge Then
                Select Case idx
                    Case 0 : Return 10  ' Half Roll
                    Case 1 : Return 10  ' Inverted Roll (same type, auto-flip direction)
                    Case 2 : Return 13  ' Flat / Cloth
                    Case 3 : Return 14  ' Accordion
                    Case 4 : Return 17  ' Tri-Radius
                    Case Else : Return 10
                End Select
            Else
                Select Case idx
                    Case 0 : Return 0   ' Sinusoidal
                    Case 1 : Return 1   ' Circular Arc
                    Case 2 : Return 2   ' Arc+Lines
                    Case 3 : Return 3   ' Sine+Lines
                    Case 4 : Return 14  ' Triangle (Accordion)
                    Case Else : Return 0
                End Select
            End If
        End Function

        Private Function BuildProfile() As SpiderProfile
            Dim p As New SpiderProfile()
            p.ComponentMode = If(cbMode IsNot Nothing AndAlso cbMode.SelectedIndex = 1, 1, 0)
            p.ID = D(tbID, 67)
            p.OD = D(tbOD, 172)
            p.N = I(tbN, 7)
            p.H_pp = D(tbHpp, 7.4)
            p.T = D(tbT, 0.8)
            p.LipWidth = D(tbLipWidth, 5.0)
            p.FirstRollUp = (cbFirstRoll.SelectedIndex = 0)
            p.E = D(tbE, 6.1)
            p.Nu = D(tbNu, 0.49)
            p.Density = D(tbDensity, 1000.0)
            p.MaterialName = cbMaterial.SelectedItem.ToString()

            ' Profile type: COMSOL checkbox overrides combo
            Dim useCOMSOL As Boolean = (chkCOMSOL IsNot Nothing AndAlso chkCOMSOL.Checked)
            If useCOMSOL Then
                p.ProfileType = 99
            Else
                p.ProfileType = ProfileTypeFromCombo()
            End If

            ' Connector angle and straight length
            If tbConnAngle IsNot Nothing Then
                p.ConnectorAngle = D(tbConnAngle, 45.0)
            End If
            If tbStraightLen IsNot Nothing Then
                p.StraightLength = D(tbStraightLen, 1.0)
            End If
            ' Center flat for DoubleRoll
            If tbCenterFlat IsNot Nothing Then
                p.CenterFlat = D(tbCenterFlat, 2.0)
            End If
            ' Bullet parameters:
            '   tbBulletRid  = Cx (distance from ID vertical to Arc 2 center)
            '   tbBulletRtop = R2 (Arc 2 radius)
            '   tbBulletRod  = InnerLipWidth (applies to all edge types)
            If tbBulletRid IsNot Nothing Then
                p.BulletCx = D(tbBulletRid, 0.0)
            End If
            If tbBulletRtop IsNot Nothing Then
                p.BulletR_Top = D(tbBulletRtop, 3.0)
            End If
            If tbBulletRod IsNot Nothing Then
                p.InnerLipWidth = D(tbBulletRod, 5.0)
            End If
            ' Edge mode: override InnerLipWidth from dedicated control
            If tbInnerLip IsNot Nothing AndAlso tbInnerLip.Visible Then
                p.InnerLipWidth = D(tbInnerLip, 5.0)
            End If

            ' Force N for edge arc profiles
            If p.ProfileType = 10 Then p.N = 1
            If p.ProfileType = 12 Then p.N = 1
            If p.ProfileType = 13 Then p.N = 1
            ' ProfileType 14 (Accordion): N = user-specified pleat count
            If p.ProfileType = 17 Then p.N = 1

            ' FEA calibration controls
            If tbTaperPct IsNot Nothing Then
                p.TaperPct = D(tbTaperPct, 0.0)
            End If
            If chkVariablePitch IsNot Nothing Then
                p.VariablePitch = chkVariablePitch.Checked
            End If
            If tbPitchTaperPct IsNot Nothing Then
                p.PitchTaperPct = D(tbPitchTaperPct, 0.0)
            End If
            If chkUseNaturalH IsNot Nothing Then
                p.UseNaturalH = chkUseNaturalH.Checked
            End If

            Return p
        End Function

        ' ══════════════════════════════════════
        '  MODE SWITCHING
        ' ══════════════════════════════════════

        Private Sub cbMode_SelectedIndexChanged(ByVal sender As Object, ByVal e As EventArgs) _
            Handles cbMode.SelectedIndexChanged
            If _suppressModeChange Then Return
            _suppressModeChange = True
            Try
                Dim isEdge As Boolean = (cbMode.SelectedIndex = 1)
                ApplyModeUI(isEdge)
            Finally
                _suppressModeChange = False
            End Try
            UpdateComputed()
        End Sub

        Private Sub ApplyModeUI(isEdge As Boolean)
            ' Guard: called during InitializeComponent before controls exist
            If grpGeometry Is Nothing OrElse cbProfile Is Nothing Then Return

            If isEdge Then
                ' Edge mode: relabel, change defaults, swap profile combo
                grpGeometry.Text = "Edge Geometry"
                lblID.Text = "Cone OD:"
                lblOD.Text = "Basket ID:"
                Me.Text = "Edge -> SolidWorks Runner"

                ' Swap profile combo to edge types
                cbProfile.Items.Clear()
                cbProfile.Items.AddRange(New Object() { _
                    "Half Roll", "Inverted Roll", "Flat / Cloth", _
                    "Triangle", "Tri-Radius"})
                cbProfile.SelectedIndex = 0

                ' Edge defaults (Phase 1 HalfRoll matrix geometry)
                tbID.Text = "148"
                tbOD.Text = "207"
                tbN.Text = "1"
                tbHpp.Text = "29.5"
                tbT.Text = "0.80"
                tbLipWidth.Text = "3.0"
                tbMaxDisp.Text = "20"

                ' Edge output directory
                tbOutputDir.Text = "C:\SpiderSW_Results\Edge"

                ' Default to NBR_SBR_rubber (first edge matrix material)
                cbMaterial.SelectedIndex = 2

                ' Relabel Lip for edge mode
                If lblLipWidth IsNot Nothing Then lblLipWidth.Text = "Outer Lip:"

                ' Show Inner Lip (at T's position), move T down to First Roll row
                If lblInnerLip IsNot Nothing Then
                    lblInnerLip.Visible = True
                    tbInnerLip.Visible = True
                End If
                If lblT IsNot Nothing Then
                    lblT.Location = New System.Drawing.Point(170, 104 + 3)
                    tbT.Location = New System.Drawing.Point(250, 104)
                End If

                ' Hide COMSOL checkbox (not relevant for edges)
                chkCOMSOL.Visible = False
                chkCOMSOL.Checked = False

                ' Hide calibration group (not relevant for edges)
                If grpCalibration IsNot Nothing Then
                    grpCalibration.Visible = False
                End If

                ' Show/hide edge-specific controls
                UpdateEdgeProfileControls()
            Else
                ' Spider mode: restore labels and defaults
                grpGeometry.Text = "Geometry"
                lblID.Text = "ID (mm):"
                lblOD.Text = "OD (mm):"
                Me.Text = "Spider -> SolidWorks Runner"

                ' Swap profile combo to spider types
                cbProfile.Items.Clear()
                cbProfile.Items.AddRange(New Object() { _
                    "Sinusoidal", "Circular Arc", "Arc+Lines", "Sine+Lines", "Triangle"})
                cbProfile.SelectedIndex = 0

                ' Spider defaults (Asymmetry campaign geometry)
                tbID.Text = "67"
                tbOD.Text = "172"
                tbN.Text = "7"
                tbHpp.Text = "7.4"
                tbT.Text = "0.8"
                tbLipWidth.Text = "5.0"
                tbMaxDisp.Text = "35"

                ' Spider output directory
                tbOutputDir.Text = "C:\SpiderSW_Results\Spider_Asymmetry"

                ' Default to Rubber for spider asymmetry campaign
                cbMaterial.SelectedIndex = 0

                ' Restore Lip label for spider mode
                If lblLipWidth IsNot Nothing Then lblLipWidth.Text = "Lip (mm):"

                ' Hide Inner Lip, restore T to normal position
                If lblInnerLip IsNot Nothing Then
                    lblInnerLip.Visible = False
                    tbInnerLip.Visible = False
                End If
                If lblT IsNot Nothing Then
                    lblT.Location = New System.Drawing.Point(170, 76 + 3)
                    tbT.Location = New System.Drawing.Point(250, 76)
                End If

                ' Show COMSOL checkbox
                chkCOMSOL.Visible = True

                ' Show calibration group
                If grpCalibration IsNot Nothing Then
                    grpCalibration.Visible = True
                End If

                ' Hide edge-specific controls, show spider controls
                lblCenterFlat.Visible = False
                tbCenterFlat.Visible = False
                lblBulletRid.Visible = False
                tbBulletRid.Visible = False
                lblBulletRtop.Visible = False
                tbBulletRtop.Visible = False
                lblBulletRod.Visible = False
                tbBulletRod.Visible = False
                lblConnAngle.Visible = True
                tbConnAngle.Visible = True
                lblStraightLen.Visible = True
                tbStraightLen.Visible = True
            End If
        End Sub

        ''' <summary>
        ''' Show/hide edge-specific controls based on selected edge profile type.
        ''' </summary>
        Private Sub UpdateEdgeProfileControls()
            If cbMode Is Nothing OrElse cbMode.SelectedIndex <> 1 Then Return
            Dim pt As Integer = ProfileTypeFromCombo()
            Dim comboIdx As Integer = cbProfile.SelectedIndex

            ' Inverted Roll (combo index 1): auto-set FirstRoll to Down
            ' Half Roll (combo index 0): auto-set FirstRoll to Up
            If comboIdx = 1 Then
                cbFirstRoll.SelectedIndex = 1  ' Down
            ElseIf comboIdx = 0 Then
                cbFirstRoll.SelectedIndex = 0  ' Up
            End If

            ' CenterFlat: not used (Double Roll removed)
            lblCenterFlat.Visible = False
            tbCenterFlat.Visible = False

            ' Tri-Radius: show R2 Rad and R2 Ctr next to Profile combo
            Dim isTriRadius As Boolean = (pt = 17)
            lblBulletRid.Visible = isTriRadius
            tbBulletRid.Visible = isTriRadius
            lblBulletRtop.Visible = isTriRadius
            tbBulletRtop.Visible = isTriRadius
            If isTriRadius Then
                lblBulletRid.Text = "R2 Rad:"
                lblBulletRtop.Text = "R2 Ctr:"
                ' Reposition next to Profile combo (where ConnAngle normally sits)
                ' Profile combo is at (70, yRow), so these go right of it
                Dim yRow As Integer = cbProfile.Location.Y
                lblBulletRid.Location = New System.Drawing.Point(205, yRow + 3)
                tbBulletRid.Location = New System.Drawing.Point(260, yRow)
                tbBulletRid.Size = New System.Drawing.Size(55, 23)
                lblBulletRtop.Location = New System.Drawing.Point(320, yRow + 3)
                tbBulletRtop.Location = New System.Drawing.Point(370, yRow)
                tbBulletRtop.Size = New System.Drawing.Size(55, 23)
                ' Seed with working defaults matching template
                If tbBulletRid.Text = "0" OrElse String.IsNullOrWhiteSpace(tbBulletRid.Text) Then
                    tbBulletRid.Text = "3.0"
                End If
                If tbBulletRtop.Text = "3.0" OrElse tbBulletRtop.Text = "5.0" OrElse String.IsNullOrWhiteSpace(tbBulletRtop.Text) Then
                    ' Default R2 Center to midpoint of Cone/Basket radii
                    Dim rInner As Double = D(tbID, 100.0) / 2.0
                    Dim rOuter As Double = D(tbOD, 120.0) / 2.0
                    tbBulletRtop.Text = String.Format("{0:F1}", (rInner + rOuter) / 2.0)
                End If
            End If

            ' tbBulletRod: not used for current edge types (R1/R3 are SW-driven for TriRadius)
            lblBulletRod.Visible = False
            tbBulletRod.Visible = False

            ' ConnectorAngle/StraightLen: not used for edge types
            lblConnAngle.Visible = False
            tbConnAngle.Visible = False
            lblStraightLen.Visible = False
            tbStraightLen.Visible = False

            ' N forcing — most edge types force N
            If pt = 10 Then tbN.Text = "1"
            If pt = 13 Then tbN.Text = "1"
            ' Accordion (14): leave N as user-specified
            If pt = 17 Then tbN.Text = "1"
        End Sub

        Private Sub cbProfile_SelectedIndexChanged(ByVal sender As Object, ByVal e As EventArgs) _
            Handles cbProfile.SelectedIndexChanged
            If cbMode IsNot Nothing AndAlso cbMode.SelectedIndex = 1 Then
                UpdateEdgeProfileControls()
            End If
            UpdateComputed()
        End Sub

        ' ══════════════════════════════════════
        '  MATERIAL PRESET
        ' ══════════════════════════════════════

        Private Sub cbMaterial_SelectedIndexChanged(ByVal sender As Object, ByVal e As EventArgs) _
            Handles cbMaterial.SelectedIndexChanged
            If tbE Is Nothing Then Return
            Select Case cbMaterial.SelectedIndex
                Case 0  ' Rubber
                    tbE.Text = "6.1" : tbNu.Text = "0.49" : tbDensity.Text = "1000"
                Case 1  ' Nomex
                    tbE.Text = "8000" : tbNu.Text = "0.28" : tbDensity.Text = "1100"
                Case 2  ' NBR_SBR_rubber
                    tbE.Text = "12" : tbNu.Text = "0.47" : tbDensity.Text = "1100"
                Case 3  ' Polyester_foam
                    tbE.Text = "1.0" : tbNu.Text = "0.30" : tbDensity.Text = "90"
                Case 4  ' Cloth_spider
                    tbE.Text = "89" : tbNu.Text = "0.30" : tbDensity.Text = "660"
                Case 5  ' Cloth_A_PolyCotton
                    tbE.Text = "600" : tbNu.Text = "0.30" : tbDensity.Text = "1320"
                Case 6  ' Cloth_B_CottonPoly
                    tbE.Text = "900" : tbNu.Text = "0.30" : tbDensity.Text = "1340"
                Case 7  ' Cloth_C_Isotropic
                    tbE.Text = "750" : tbNu.Text = "0.30" : tbDensity.Text = "1350"
                Case 8  ' Cloth_D_SoftCotton
                    tbE.Text = "500" : tbNu.Text = "0.20" : tbDensity.Text = "1280"
                Case 9  ' Cloth_E_Aramid
                    tbE.Text = "1500" : tbNu.Text = "0.25" : tbDensity.Text = "1390"
                Case 10  ' Bimax_DKM
                    tbE.Text = "1200" : tbNu.Text = "0.30" : tbDensity.Text = "1100"
            End Select
        End Sub

        Private Sub UpdateComputed()
            If tbID Is Nothing OrElse lblEffPitch Is Nothing Then Return
            Dim p = BuildProfile()
            lblEffPitch.Text = String.Format( _
                "EffPitch={0:F2}  Aspect={1:F3}  MaxH_pp={2:F2}", _
                p.EffPitchPerRoll, p.AspectRatio, p.MaxH_pp)
            If p.H_pp > p.MaxH_pp Then
                lblEffPitch.ForeColor = System.Drawing.Color.Red
                lblEffPitch.Text &= "  *** H_pp > MAX ***"
            ElseIf p.ProfileType = 12 Then
                Dim valErr As String = p.ValidateBulletParams()
                If valErr.Length > 0 Then
                    lblEffPitch.ForeColor = System.Drawing.Color.Red
                    lblEffPitch.Text = "BULLET: " & valErr
                Else
                    lblEffPitch.ForeColor = System.Drawing.Color.DarkGreen
                    lblEffPitch.Text = String.Format("BULLET: width={0:F2}  H_pp={1:F2}  R2={2:F2}  Cx={3:F2}  InnerLip={4:F2}", _
                        p.BulletWidth, p.H_pp, p.BulletR_Top, p.GetBulletCx(), p.InnerLipWidth)
                End If
            Else
                lblEffPitch.ForeColor = System.Drawing.Color.DarkBlue
            End If
        End Sub

        Private Sub LogMessage(ByVal msg As String)
            If txtLog.InvokeRequired Then
                txtLog.Invoke(New Action(Of String)(AddressOf LogMessage), msg)
                Return
            End If
            txtLog.AppendText(msg & vbCrLf)
            txtLog.SelectionStart = txtLog.TextLength
            txtLog.ScrollToCaret()
        End Sub

        Private Sub MarkButton(ByVal btn As Button, ByVal success As Boolean)
            If success Then
                btn.BackColor = System.Drawing.Color.FromArgb(180, 255, 180)
            Else
                btn.BackColor = System.Drawing.Color.FromArgb(255, 180, 180)
            End If
        End Sub

        ' ══════════════════════════════════════
        '  BUTTON HANDLERS
        ' ══════════════════════════════════════

        Private Sub btnConnect_Click(ByVal sender As Object, ByVal e As EventArgs) _
            Handles btnConnect.Click
            Cursor = Cursors.WaitCursor
            MarkButton(btnConnect, _sw.Connect())
            Cursor = Cursors.Default
        End Sub

        Private Sub btnCreatePart_Click(ByVal sender As Object, ByVal e As EventArgs) _
            Handles btnCreatePart.Click
            Cursor = Cursors.WaitCursor
            MarkButton(btnCreatePart, _sw.CreatePart(BuildProfile()))
            Cursor = Cursors.Default
        End Sub

        Private Sub btnSetupStudy_Click(ByVal sender As Object, ByVal e As EventArgs) _
            Handles btnSetupStudy.Click
            Cursor = Cursors.WaitCursor
            Dim p = BuildProfile()
            Dim maxD As Double = D(tbMaxDisp, 10.0)
            Dim steps As Integer = I(tbSteps, 20)
            If IsPullDirection() Then
                LogMessage("PULL (-Y): applied automatically as a negative prescribed displacement.")
                LogMessage("(Equivalent to the old manual 'Reverse direction' checkbox — verify Kms0 vs a prior manual Pull run on first use.)")
            End If
            MarkButton(btnSetupStudy, _sw.SetupStudy(p, maxD, steps, IsPullDirection()))
            Cursor = Cursors.Default
        End Sub

        Private Sub btnMeshRun_Click(ByVal sender As Object, ByVal e As EventArgs) _
            Handles btnMeshRun.Click
            Cursor = Cursors.WaitCursor
            MarkButton(btnMeshRun, _sw.MeshAndRun())
            Cursor = Cursors.Default
        End Sub

        Private Sub btnExtract_Click(ByVal sender As Object, ByVal e As EventArgs) _
            Handles btnExtract.Click
            Cursor = Cursors.WaitCursor
            Dim p = BuildProfile()
            Dim maxD As Double = D(tbMaxDisp, 35.0)
            Dim outDir As String = tbOutputDir.Text.Trim()
            If Not System.IO.Directory.Exists(outDir) Then
                System.IO.Directory.CreateDirectory(outDir)
            End If
            Dim outPath As String = System.IO.Path.Combine(outDir, BuildOutputFilename(p))
            LogMessage("Output file: " & outPath)
            MarkButton(btnExtract, _sw.ExtractResultsAuto(p, maxD, I(tbSteps, 40), outPath))
            Cursor = Cursors.Default
        End Sub

        Private Sub btnRunAll_Click(ByVal sender As Object, ByVal e As EventArgs) _
            Handles btnRunAll.Click
            Cursor = Cursors.WaitCursor
            Dim p = BuildProfile()
            Dim maxD As Double = D(tbMaxDisp, 10.0)
            Dim steps As Integer = I(tbSteps, 20)

            ' Step 1 — Connect
            If Not _sw.Connect() Then
                MarkButton(btnConnect, False)
                Cursor = Cursors.Default
                Return
            End If
            MarkButton(btnConnect, True)

            ' Step 2 — Create Part
            If Not _sw.CreatePart(p) Then
                MarkButton(btnCreatePart, False)
                Cursor = Cursors.Default
                Return
            End If
            MarkButton(btnCreatePart, True)

            ' Step 3 — Setup Study
            If IsPullDirection() Then
                LogMessage("PULL (-Y): applied automatically as a negative prescribed displacement.")
            End If
            If Not _sw.SetupStudy(p, maxD, steps, IsPullDirection()) Then
                MarkButton(btnSetupStudy, False)
                Cursor = Cursors.Default
                Return
            End If
            MarkButton(btnSetupStudy, True)

            ' Step 4 — Mesh + Run
            If Not _sw.MeshAndRun() Then
                MarkButton(btnMeshRun, False)
                Cursor = Cursors.Default
                Return
            End If
            MarkButton(btnMeshRun, True)

            ' Step 5 — Extract
            Dim outDir As String = tbOutputDir.Text.Trim()
            If Not System.IO.Directory.Exists(outDir) Then
                System.IO.Directory.CreateDirectory(outDir)
            End If
            Dim outPath As String = System.IO.Path.Combine(outDir, BuildOutputFilename(p))
            LogMessage("Output file: " & outPath)
            MarkButton(btnExtract, _sw.ExtractResults(p, outPath))

            Cursor = Cursors.Default
        End Sub

        Private Sub btnExportProfile_Click(ByVal sender As Object, ByVal e As EventArgs) _
            Handles btnExportProfile.Click
            Try
                Dim p = BuildProfile()
                Dim outDir As String = tbOutputDir.Text.Trim()
                If Not System.IO.Directory.Exists(outDir) Then
                    System.IO.Directory.CreateDirectory(outDir)
                End If
                Dim prefix As String = If(p.ComponentMode = 1, "EdgeProfile", "SpiderProfile")
                Dim csvPath As String = System.IO.Path.Combine(outDir, _
                    String.Format("{0}_N{1}_Hpp{2:F1}.csv", prefix, p.N, p.H_pp))
                _sw.ExportProfileCSV(p, csvPath)
            Catch ex As System.Exception
                LogMessage("ERROR: " & ex.Message)
            End Try
        End Sub

        Private Sub btnClearLog_Click(ByVal sender As Object, ByVal e As EventArgs) _
            Handles btnClearLog.Click
            txtLog.Clear()
        End Sub

        Private Sub btnProbeStress_Click(ByVal sender As Object, ByVal e As EventArgs) _
            Handles btnProbeStress.Click
            ' ─────────────────────────────────────────────────────────
            ' Runs SWAutomation.EnumerateResults — probes for batch stress API.
            ' Requires: SW open, part loaded, study solved, ID/OD in GUI match
            ' the open part. No files written. Logs only.
            ' ─────────────────────────────────────────────────────────
            Cursor = Cursors.WaitCursor
            Try
                Dim p = BuildProfile()
                _sw.EnumerateResults(p)
            Catch ex As System.Exception
                LogMessage("ERROR: " & ex.Message)
            End Try
            Cursor = Cursors.Default
        End Sub

        Private Sub btnBatch_Click(ByVal sender As Object, ByVal e As EventArgs) _
            Handles btnBatch.Click
            ' ─────────────────────────────────────────────────────────
            ' Opens the Batch Sweep form. The CURRENT main-form fields
            ' become the base configuration; swept variables override
            ' them per generated row.
            ' ─────────────────────────────────────────────────────────
            Try
                Dim p As SpiderProfile = BuildProfile()
                Dim base As New BatchRow()
                base.ComponentMode = p.ComponentMode
                base.ID = p.ID
                base.OD = p.OD
                base.N = p.N
                base.H_pp = p.H_pp
                base.T = p.T
                base.LipWidth = p.LipWidth
                base.InnerLipWidth = p.InnerLipWidth
                base.FirstRollUp = p.FirstRollUp
                base.ProfileType = p.ProfileType
                base.ConnectorAngle = p.ConnectorAngle
                base.StraightLength = p.StraightLength
                base.CenterFlat = p.CenterFlat
                base.BulletR_Top = p.BulletR_Top
                base.BulletCx = p.BulletCx
                base.TaperPct = p.TaperPct
                base.VariablePitch = p.VariablePitch
                base.PitchTaperPct = p.PitchTaperPct
                base.UseNaturalH = p.UseNaturalH
                base.MaterialName = p.MaterialName
                base.E = p.E
                base.Nu = p.Nu
                base.Density = p.Density
                base.MaxDisp = D(tbMaxDisp, 10.0)
                base.Steps = I(tbSteps, 20)
                base.Direction = If(IsPullDirection(), "Pull", "Push")
                base.OutputDir = tbOutputDir.Text.Trim()

                Dim f As New BatchForm(base, _sw)
                f.Show(Me)
            Catch ex As System.Exception
                LogMessage("ERROR opening Batch form: " & ex.Message)
            End Try
        End Sub

        Private Sub btnDumpApi_Click(ByVal sender As Object, ByVal e As EventArgs) _
            Handles btnDumpApi.Click
            ' ─────────────────────────────────────────────────────────
            ' Dumps the SW2014 Simulation interop member names (shell,
            ' material, mesh, nonlinear options...) to a text file.
            ' Run ONCE and report the file — it locks the exact property
            ' names for stepping/material/mesh on this SW version.
            ' No SolidWorks connection required (reflects the DLL).
            ' ─────────────────────────────────────────────────────────
            Try
                Dim outPath As String = "C:\SpiderSW_Results\SimAPI_Members.txt"
                Dim dir As String = System.IO.Path.GetDirectoryName(outPath)
                If Not System.IO.Directory.Exists(dir) Then
                    System.IO.Directory.CreateDirectory(dir)
                End If
                _sw.DumpSimApiMembers(outPath)
            Catch ex As System.Exception
                LogMessage("ERROR: " & ex.Message)
            End Try
        End Sub

        ' ══════════════════════════════════════
        '  CALIBRATION CONTROL EVENTS
        ' ══════════════════════════════════════

        Private Sub chkVariablePitch_CheckedChanged(ByVal sender As Object, ByVal e As EventArgs) _
            Handles chkVariablePitch.CheckedChanged
            ' Enable/disable PitchTaperPct textbox based on checkbox
            If tbPitchTaperPct IsNot Nothing Then
                tbPitchTaperPct.Enabled = chkVariablePitch.Checked
                If Not chkVariablePitch.Checked Then
                    tbPitchTaperPct.Text = "0"
                End If
            End If
            UpdateComputed()
        End Sub

        Private Sub GeometryChanged(ByVal sender As Object, ByVal e As EventArgs) _
            Handles tbID.TextChanged, tbOD.TextChanged, tbN.TextChanged, _
                    tbHpp.TextChanged, tbT.TextChanged, tbLipWidth.TextChanged, _
                    cbFirstRoll.SelectedIndexChanged, _
                    tbBulletRid.TextChanged, tbBulletRtop.TextChanged, tbBulletRod.TextChanged, _
                    tbTaperPct.TextChanged, tbPitchTaperPct.TextChanged, _
                    chkUseNaturalH.CheckedChanged, tbInnerLip.TextChanged
            UpdateComputed()
        End Sub

    End Class
