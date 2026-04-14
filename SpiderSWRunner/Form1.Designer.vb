    <Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
    Partial Class Form1
        Inherits System.Windows.Forms.Form

        Protected Overrides Sub Dispose(ByVal disposing As Boolean)
            Try
                If disposing AndAlso components IsNot Nothing Then
                    components.Dispose()
                End If
            Finally
                MyBase.Dispose(disposing)
            End Try
        End Sub

        Private components As System.ComponentModel.IContainer

        Private Sub InitializeComponent()
            Me.Text = "Spider / Edge -> SolidWorks Runner"
            Me.ClientSize = New System.Drawing.Size(700, 790)
            Me.MinimumSize = New System.Drawing.Size(700, 640)
            Me.Font = New System.Drawing.Font("Segoe UI", 9.0F)

            Dim yPos As Integer = 8

            ' ══════════════════════════════════════
            '  MODE SELECTOR — top of form
            ' ══════════════════════════════════════
            AddLabel(Me, "Component:", 8, yPos)
            cbMode = New System.Windows.Forms.ComboBox()
            cbMode.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
            cbMode.Items.AddRange(New String() {"Spider", "Edge"})
            cbMode.SelectedIndex = 0
            cbMode.Location = New System.Drawing.Point(90, yPos)
            cbMode.Size = New System.Drawing.Size(90, 22)
            cbMode.Font = New System.Drawing.Font("Segoe UI", 9.0F, System.Drawing.FontStyle.Bold)
            Me.Controls.Add(cbMode)

            yPos += 30

            ' ══════════════════════════════════════
            '  GEOMETRY GROUP
            ' ══════════════════════════════════════
            grpGeometry = New System.Windows.Forms.GroupBox()
            grpGeometry.Text = "Geometry"
            grpGeometry.Location = New System.Drawing.Point(8, yPos)
            grpGeometry.Size = New System.Drawing.Size(320, 185)
            Me.Controls.Add(grpGeometry)

            Dim gy As Integer = 20
            Dim c1 As Integer = 10, c2 As Integer = 105, c3 As Integer = 170, c4 As Integer = 250

            lblID = AddLabelCtrl(grpGeometry, "ID (mm):", c1, gy)
            tbID = AddTextBox(grpGeometry, "67", c2, gy, 55)
            lblOD = AddLabelCtrl(grpGeometry, "OD (mm):", c3, gy)
            tbOD = AddTextBox(grpGeometry, "172", c4, gy, 55)
            gy += 28

            AddLabel(grpGeometry, "N rolls:", c1, gy)
            tbN = AddTextBox(grpGeometry, "7", c2, gy, 55)
            AddLabel(grpGeometry, "Lip (mm):", c3, gy)
            tbLipWidth = AddTextBox(grpGeometry, "5.0", c4, gy, 55)
            gy += 28

            AddLabel(grpGeometry, "H_pp (mm):", c1, gy)
            tbHpp = AddTextBox(grpGeometry, "7.4", c2, gy, 55)
            AddLabel(grpGeometry, "T (mm):", c3, gy)
            tbT = AddTextBox(grpGeometry, "0.8", c4, gy, 55)
            gy += 28

            AddLabel(grpGeometry, "First roll:", c1, gy)
            cbFirstRoll = New System.Windows.Forms.ComboBox()
            cbFirstRoll.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
            cbFirstRoll.Items.AddRange(New String() {"Up", "Down"})
            cbFirstRoll.SelectedIndex = 0
            cbFirstRoll.Location = New System.Drawing.Point(c2, gy)
            cbFirstRoll.Size = New System.Drawing.Size(55, 21)
            grpGeometry.Controls.Add(cbFirstRoll)
            gy += 28

            lblEffPitch = New System.Windows.Forms.Label()
            lblEffPitch.Location = New System.Drawing.Point(c1, gy)
            lblEffPitch.Size = New System.Drawing.Size(300, 18)
            lblEffPitch.ForeColor = System.Drawing.Color.DarkBlue
            grpGeometry.Controls.Add(lblEffPitch)

            ' ══════════════════════════════════════
            '  MATERIAL GROUP
            ' ══════════════════════════════════════
            grpMaterial = New System.Windows.Forms.GroupBox()
            grpMaterial.Text = "Material"
            grpMaterial.Location = New System.Drawing.Point(340, yPos)
            grpMaterial.Size = New System.Drawing.Size(350, 100)
            Me.Controls.Add(grpMaterial)

            Dim my As Integer = 20
            AddLabel(grpMaterial, "Preset:", 10, my)
            cbMaterial = New System.Windows.Forms.ComboBox()
            cbMaterial.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
            cbMaterial.Items.AddRange(New String() {"Rubber", "Cotton Cloth", "Nomex"})
            cbMaterial.SelectedIndex = 0
            cbMaterial.Location = New System.Drawing.Point(70, my)
            cbMaterial.Size = New System.Drawing.Size(120, 21)
            grpMaterial.Controls.Add(cbMaterial)
            my += 28
            AddLabel(grpMaterial, "E (N/mm2):", 10, my)
            tbE = AddTextBox(grpMaterial, "6.1", 105, my, 65)
            AddLabel(grpMaterial, "Nu:", 185, my)
            tbNu = AddTextBox(grpMaterial, "0.49", 215, my, 55)
            my += 28
            AddLabel(grpMaterial, "Density:", 10, my)
            tbDensity = AddTextBox(grpMaterial, "1000", 105, my, 65)
            AddLabel(grpMaterial, "kg/m3", 175, my)

            ' ══════════════════════════════════════
            '  SIMULATION GROUP
            ' ══════════════════════════════════════
            grpSim = New System.Windows.Forms.GroupBox()
            grpSim.Text = "Nonlinear Study"
            grpSim.Location = New System.Drawing.Point(340, yPos + 108)
            grpSim.Size = New System.Drawing.Size(350, 103)
            Me.Controls.Add(grpSim)

            Dim sy As Integer = 20
            AddLabel(grpSim, "Max disp (mm):", 10, sy)
            tbMaxDisp = AddTextBox(grpSim, "35", 120, sy, 55)
            sy += 28
            AddLabel(grpSim, "Load steps:", 10, sy)
            tbSteps = AddTextBox(grpSim, "40", 120, sy, 55)
            sy += 28
            AddLabel(grpSim, "Output folder:", 10, sy)
            tbOutputDir = AddTextBox(grpSim, "C:\SpiderSW_Results", 120, sy, 215)

            ' ══════════════════════════════════════
            '  BUTTONS — Row 1: main workflow
            ' ══════════════════════════════════════
            yPos += 220
            Dim bw As Integer = 130
            Dim bh As Integer = 32
            Dim gap As Integer = 4

            btnConnect = AddButton("1. Connect", 8, yPos, bw, bh)
            btnCreatePart = AddButton("2. Create Part", 8 + (bw + gap), yPos, bw, bh)
            btnSetupStudy = AddButton("3. Setup Study", 8 + (bw + gap) * 2, yPos, bw, bh)
            btnMeshRun = AddButton("4. Mesh + Run", 8 + (bw + gap) * 3, yPos, bw, bh)
            btnExtract = AddButton("5. Extract", 8 + (bw + gap) * 4, yPos, bw, bh)

            ' ── Row 2: utilities ──
            yPos += bh + 4
            btnExportProfile = AddButton("Export Profile CSV", 8, yPos, bw, bh)
            btnClearLog = AddButton("Clear Log", 8 + bw + gap, yPos, 80, bh)
            btnRunAll = AddButton("Run All (1-5)", 8 + (bw + gap) * 2, yPos, bw, bh)
            btnRunAll.BackColor = System.Drawing.Color.FromArgb(220, 230, 255)

            ' COMSOL profile checkbox
            chkCOMSOL = New System.Windows.Forms.CheckBox()
            chkCOMSOL.Text = "COMSOL Arc+Lines Profile (exact)"
            chkCOMSOL.Location = New System.Drawing.Point(8, yPos + bh + 6)
            chkCOMSOL.Size = New System.Drawing.Size(300, 20)
            chkCOMSOL.Checked = False
            Me.Controls.Add(chkCOMSOL)

            ' Profile type combo
            lblProfile = AddLabelCtrl(Me, "Profile:", 8, yPos + bh + 30)
            cbProfile = New System.Windows.Forms.ComboBox()
            cbProfile.Location = New System.Drawing.Point(70, yPos + bh + 27)
            cbProfile.Size = New System.Drawing.Size(130, 22)
            cbProfile.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
            cbProfile.Items.AddRange(New Object() {"Sinusoidal", "Circular Arc", "Arc+Lines", "Sine+Lines"})
            cbProfile.SelectedIndex = 0
            Me.Controls.Add(cbProfile)

            ' Connector angle and straight length (ArcLines/SineLines)
            lblConnAngle = AddLabelCtrl(Me, "Angle°:", 205, yPos + bh + 30)
            tbConnAngle = AddTextBox(Me, "45", 255, yPos + bh + 27, 38)
            lblStraightLen = AddLabelCtrl(Me, "SLen:", 298, yPos + bh + 30)
            tbStraightLen = AddTextBox(Me, "1.0", 335, yPos + bh + 27, 38)

            ' Center flat (DoubleRoll only — hidden by default)
            lblCenterFlat = AddLabelCtrl(Me, "Flat:", 380, yPos + bh + 30)
            tbCenterFlat = AddTextBox(Me, "2.0", 415, yPos + bh + 27, 38)
            lblCenterFlat.Visible = False
            tbCenterFlat.Visible = False

            ' Bullet controls (Bullet only — hidden by default)
            lblBulletRid = AddLabelCtrl(Me, "Cx:", 380, yPos + bh + 30)
            tbBulletRid = AddTextBox(Me, "0", 405, yPos + bh + 27, 32)
            lblBulletRtop = AddLabelCtrl(Me, "Rtop:", 441, yPos + bh + 30)
            tbBulletRtop = AddTextBox(Me, "3.0", 475, yPos + bh + 27, 32)
            lblBulletRod = AddLabelCtrl(Me, "Rid/Rod:", 511, yPos + bh + 30)
            tbBulletRod = AddTextBox(Me, "", 562, yPos + bh + 27, 55)
            tbBulletRod.ReadOnly = True
            tbBulletRod.BackColor = System.Drawing.SystemColors.Control
            lblBulletRid.Visible = False
            tbBulletRid.Visible = False
            lblBulletRtop.Visible = False
            tbBulletRtop.Visible = False
            lblBulletRod.Visible = False
            tbBulletRod.Visible = False

            ' ══════════════════════════════════════
            '  LOG
            ' ══════════════════════════════════════
            yPos += bh + 8

            lblLog = New System.Windows.Forms.Label()
            lblLog.Text = "Log:"
            lblLog.Location = New System.Drawing.Point(8, yPos + 46)
            lblLog.AutoSize = True
            Me.Controls.Add(lblLog)
            yPos += 64

            txtLog = New System.Windows.Forms.TextBox()
            txtLog.Multiline = True
            txtLog.ScrollBars = System.Windows.Forms.ScrollBars.Both
            txtLog.WordWrap = False
            txtLog.ReadOnly = True
            txtLog.BackColor = System.Drawing.Color.FromArgb(20, 20, 30)
            txtLog.ForeColor = System.Drawing.Color.FromArgb(180, 220, 180)
            txtLog.Font = New System.Drawing.Font("Consolas", 9.0F)
            txtLog.Location = New System.Drawing.Point(8, yPos)
            txtLog.Size = New System.Drawing.Size(680, 430)
            txtLog.Anchor = System.Windows.Forms.AnchorStyles.Top Or _
                            System.Windows.Forms.AnchorStyles.Bottom Or _
                            System.Windows.Forms.AnchorStyles.Left Or _
                            System.Windows.Forms.AnchorStyles.Right
            Me.Controls.Add(txtLog)

        End Sub

        ' ── Control fields ──
        Friend WithEvents cbMode As System.Windows.Forms.ComboBox
        Friend WithEvents grpGeometry As System.Windows.Forms.GroupBox
        Friend WithEvents grpMaterial As System.Windows.Forms.GroupBox
        Friend WithEvents grpSim As System.Windows.Forms.GroupBox
        Friend WithEvents lblID As System.Windows.Forms.Label
        Friend WithEvents lblOD As System.Windows.Forms.Label
        Friend WithEvents tbID As System.Windows.Forms.TextBox
        Friend WithEvents tbOD As System.Windows.Forms.TextBox
        Friend WithEvents tbN As System.Windows.Forms.TextBox
        Friend WithEvents tbLipWidth As System.Windows.Forms.TextBox
        Friend WithEvents tbHpp As System.Windows.Forms.TextBox
        Friend WithEvents tbT As System.Windows.Forms.TextBox
        Friend WithEvents cbFirstRoll As System.Windows.Forms.ComboBox
        Friend WithEvents cbMaterial As System.Windows.Forms.ComboBox
        Friend WithEvents lblEffPitch As System.Windows.Forms.Label
        Friend WithEvents tbE As System.Windows.Forms.TextBox
        Friend WithEvents tbNu As System.Windows.Forms.TextBox
        Friend WithEvents tbDensity As System.Windows.Forms.TextBox
        Friend WithEvents tbMaxDisp As System.Windows.Forms.TextBox
        Friend WithEvents tbSteps As System.Windows.Forms.TextBox
        Friend WithEvents tbOutputDir As System.Windows.Forms.TextBox
        Friend WithEvents btnConnect As System.Windows.Forms.Button
        Friend WithEvents btnCreatePart As System.Windows.Forms.Button
        Friend WithEvents btnSetupStudy As System.Windows.Forms.Button
        Friend WithEvents btnMeshRun As System.Windows.Forms.Button
        Friend WithEvents btnExtract As System.Windows.Forms.Button
        Friend WithEvents btnExportProfile As System.Windows.Forms.Button
        Friend WithEvents btnClearLog As System.Windows.Forms.Button
        Friend WithEvents btnRunAll As System.Windows.Forms.Button
        Friend WithEvents lblLog As System.Windows.Forms.Label
        Friend WithEvents chkCOMSOL As System.Windows.Forms.CheckBox
        Friend WithEvents lblProfile As System.Windows.Forms.Label
        Friend WithEvents cbProfile As System.Windows.Forms.ComboBox
        Friend WithEvents lblConnAngle As System.Windows.Forms.Label
        Friend WithEvents tbConnAngle As System.Windows.Forms.TextBox
        Friend WithEvents lblStraightLen As System.Windows.Forms.Label
        Friend WithEvents tbStraightLen As System.Windows.Forms.TextBox
        Friend WithEvents lblCenterFlat As System.Windows.Forms.Label
        Friend WithEvents tbCenterFlat As System.Windows.Forms.TextBox
        Friend WithEvents lblBulletRid As System.Windows.Forms.Label
        Friend WithEvents tbBulletRid As System.Windows.Forms.TextBox
        Friend WithEvents lblBulletRtop As System.Windows.Forms.Label
        Friend WithEvents tbBulletRtop As System.Windows.Forms.TextBox
        Friend WithEvents lblBulletRod As System.Windows.Forms.Label
        Friend WithEvents tbBulletRod As System.Windows.Forms.TextBox
        Friend WithEvents txtLog As System.Windows.Forms.TextBox

        ' ── Layout helpers ──
        Private Sub AddLabel(ByVal parent As System.Windows.Forms.Control, _
                             ByVal text As String, ByVal x As Integer, ByVal y As Integer)
            Dim lbl As New System.Windows.Forms.Label()
            lbl.Text = text
            lbl.Location = New System.Drawing.Point(x, y + 3)
            lbl.AutoSize = True
            parent.Controls.Add(lbl)
        End Sub

        ''' <summary>AddLabel that returns the control for later relabelling.</summary>
        Private Function AddLabelCtrl(ByVal parent As System.Windows.Forms.Control, _
                                       ByVal text As String, ByVal x As Integer, ByVal y As Integer) As System.Windows.Forms.Label
            Dim lbl As New System.Windows.Forms.Label()
            lbl.Text = text
            lbl.Location = New System.Drawing.Point(x, y + 3)
            lbl.AutoSize = True
            parent.Controls.Add(lbl)
            Return lbl
        End Function

        Private Function AddTextBox(ByVal parent As System.Windows.Forms.Control, _
                                     ByVal defaultText As String, _
                                     ByVal x As Integer, ByVal y As Integer, _
                                     ByVal w As Integer) As System.Windows.Forms.TextBox
            Dim tb As New System.Windows.Forms.TextBox()
            tb.Text = defaultText
            tb.Location = New System.Drawing.Point(x, y)
            tb.Size = New System.Drawing.Size(w, 23)
            parent.Controls.Add(tb)
            Return tb
        End Function

        Private Function AddButton(ByVal text As String, _
                                    ByVal x As Integer, ByVal y As Integer, _
                                    ByVal w As Integer, ByVal h As Integer) As System.Windows.Forms.Button
            Dim btn As New System.Windows.Forms.Button()
            btn.Text = text
            btn.Location = New System.Drawing.Point(x, y)
            btn.Size = New System.Drawing.Size(w, h)
            Me.Controls.Add(btn)
            Return btn
        End Function

    End Class
