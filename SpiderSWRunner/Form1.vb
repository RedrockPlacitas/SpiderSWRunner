Imports System
Imports System.Windows.Forms
Imports Microsoft.VisualBasic

    Partial Public Class Form1
        Inherits Form

        Private _sw As SWAutomation
        Private _suppressModeChange As Boolean = False

        Public Sub New()
            InitializeComponent()
            _sw = New SWAutomation(AddressOf LogMessage)
            UpdateComputed()
            LogMessage("Spider / Edge -> SolidWorks Runner ready.")
            LogMessage("")
            LogMessage("Workflow: Connect -> Create Part -> Setup Study -> Mesh+Run -> Extract")
            LogMessage("Or click 'Run All' to do everything in sequence.")
            LogMessage("")
        End Sub

        ''' <summary>
        ''' Short string tag for the current profile type, used in filenames.
        ''' </summary>
        Private Function ProfileTag(p As SpiderProfile) As String
            Select Case p.ProfileType
                Case 1 : Return "Arc"
                Case 2 : Return "ArcLines"
                Case 3 : Return "SineLines"
                Case 10 : Return "HalfRoll"
                Case 11 : Return "DblRoll"
                Case 12 : Return "Bullet"
                Case 99 : Return "COMSOL"
                Case Else : Return "Sin"
            End Select
        End Function

        ''' <summary>
        ''' Build a unique output filename encoding ALL sweep-varying parameters.
        ''' Prefix is Spider_ or Edge_ based on mode.
        ''' </summary>
        Private Function BuildOutputFilename(p As SpiderProfile) As String
            Dim prefix As String = If(p.ComponentMode = 1, "Edge", "Spider")
            Dim tag As String = ProfileTag(p)
            Dim anglePart As String = ""
            If p.ProfileType >= 2 AndAlso p.ProfileType <= 3 Then
                anglePart = "_A" & CInt(p.ConnectorAngle).ToString()
            End If
            Dim flatPart As String = ""
            If p.ProfileType = 11 Then
                flatPart = "_F" & p.CenterFlat.ToString("F1").TrimEnd("0"c).TrimEnd("."c)
            End If
            Dim bulletPart As String = ""
            If p.ProfileType = 12 Then
                bulletPart = "_R2" & p.BulletR_Top.ToString("F1").TrimEnd("0"c).TrimEnd("."c) & _
                             "_Cx" & p.GetBulletCx().ToString("F1").TrimEnd("0"c).TrimEnd("."c)
            End If
            Return String.Format( _
                "{0}_N{1}_ID{2}_OD{3}_{4}_Hpp{5:F1}_T{6}_E{7}_Nu{8}{9}{10}{11}_auto.csv", _
                prefix, p.N, CInt(p.ID), CInt(p.OD), tag, _
                p.H_pp, _
                p.T.ToString("F2").TrimEnd("0"c).TrimEnd("."c), _
                p.E.ToString("F1").TrimEnd("0"c).TrimEnd("."c), _
                p.Nu.ToString("F2").TrimEnd("0"c).TrimEnd("."c), _
                anglePart, flatPart, bulletPart)
        End Function

        Private Function D(ByVal tb As TextBox, ByVal def As Double) As Double
            Dim v As Double
            If Double.TryParse(tb.Text, v) Then Return v
            Return def
        End Function

        Private Function I(ByVal tb As TextBox, ByVal def As Integer) As Integer
            Dim v As Integer
            If Integer.TryParse(tb.Text, v) Then Return v
            Return def
        End Function

        ''' <summary>
        ''' Map cbProfile.SelectedIndex to ProfileType integer based on current mode.
        ''' Spider: 0=Sin, 1=Arc, 2=ArcLines, 3=SineLines
        ''' Edge:   0=HalfRoll, 1=DoubleRoll, 2=Sin, 3=Arc, 4=ArcLines, 5=SineLines
        ''' </summary>
        Private Function ProfileTypeFromCombo() As Integer
            Dim idx As Integer = If(cbProfile IsNot Nothing, cbProfile.SelectedIndex, 0)
            If cbMode IsNot Nothing AndAlso cbMode.SelectedIndex = 1 Then
                ' Edge mode
                Select Case idx
                    Case 0 : Return 10  ' Half Roll
                    Case 1 : Return 11  ' Double Roll
                    Case 2 : Return 12  ' Bullet
                    Case 3 : Return 0   ' Sinusoidal
                    Case 4 : Return 1   ' Circular Arc
                    Case 5 : Return 2   ' ArcLines
                    Case 6 : Return 3   ' SineLines
                    Case Else : Return 10
                End Select
            Else
                ' Spider mode: direct mapping
                Return idx
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

            ' Force N for edge arc profiles
            If p.ProfileType = 10 Then p.N = 1
            If p.ProfileType = 11 Then p.N = 2
            If p.ProfileType = 12 Then p.N = 1

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
                    "Half Roll", "Double Roll", "Bullet", "Sinusoidal", "Circular Arc", "Arc+Lines", "Sine+Lines"})
                cbProfile.SelectedIndex = 0

                ' Edge defaults
                tbID.Text = "100"
                tbOD.Text = "120"
                tbN.Text = "1"
                tbHpp.Text = "5.0"
                tbT.Text = "0.5"
                tbLipWidth.Text = "3.0"
                tbMaxDisp.Text = "10"

                ' Hide COMSOL checkbox (not relevant for edges)
                chkCOMSOL.Visible = False
                chkCOMSOL.Checked = False

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
                    "Sinusoidal", "Circular Arc", "Arc+Lines", "Sine+Lines"})
                cbProfile.SelectedIndex = 0

                ' Spider defaults
                tbID.Text = "67"
                tbOD.Text = "172"
                tbN.Text = "7"
                tbHpp.Text = "7.4"
                tbT.Text = "0.8"
                tbLipWidth.Text = "5.0"
                tbMaxDisp.Text = "35"

                ' Show COMSOL checkbox
                chkCOMSOL.Visible = True

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

            ' CenterFlat only for DoubleRoll
            lblCenterFlat.Visible = (pt = 11)
            tbCenterFlat.Visible = (pt = 11)

            ' Bullet Cx and R2 only for Bullet
            lblBulletRid.Visible = (pt = 12)
            tbBulletRid.Visible = (pt = 12)
            lblBulletRtop.Visible = (pt = 12)
            tbBulletRtop.Visible = (pt = 12)

            ' InnerLipWidth (tbBulletRod) for all edge types (HalfRoll, DoubleRoll, Bullet)
            Dim showInnerLip As Boolean = (pt = 10 OrElse pt = 11 OrElse pt = 12)
            lblBulletRod.Visible = showInnerLip
            tbBulletRod.Visible = showInnerLip
            If showInnerLip Then
                lblBulletRod.Text = "InnerLip"
                tbBulletRod.ReadOnly = False
                If String.IsNullOrWhiteSpace(tbBulletRod.Text) OrElse tbBulletRod.Text.Contains("/") Then
                    tbBulletRod.Text = "5.0"
                End If
            End If

            ' ConnectorAngle/StraightLen for ArcLines/SineLines
            Dim showConn As Boolean = (pt = 2 OrElse pt = 3)
            lblConnAngle.Visible = showConn
            tbConnAngle.Visible = showConn
            lblStraightLen.Visible = showConn
            tbStraightLen.Visible = showConn

            ' N is forced for HalfRoll/DoubleRoll/Bullet
            If pt = 10 Then tbN.Text = "1"
            If pt = 11 Then tbN.Text = "2"
            If pt = 12 Then tbN.Text = "1"
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
                Case 1  ' Cotton Cloth
                    tbE.Text = "3000" : tbNu.Text = "0.30" : tbDensity.Text = "1200"
                Case 2  ' Nomex
                    tbE.Text = "8000" : tbNu.Text = "0.28" : tbDensity.Text = "1100"
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
            MarkButton(btnSetupStudy, _sw.SetupStudy(p, maxD, steps))
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
            If Not _sw.SetupStudy(p, maxD, steps) Then
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

        Private Sub GeometryChanged(ByVal sender As Object, ByVal e As EventArgs) _
            Handles tbID.TextChanged, tbOD.TextChanged, tbN.TextChanged, _
                    tbHpp.TextChanged, tbT.TextChanged, tbLipWidth.TextChanged, _
                    cbFirstRoll.SelectedIndexChanged, _
                    tbBulletRid.TextChanged, tbBulletRtop.TextChanged, tbBulletRod.TextChanged
            UpdateComputed()
        End Sub

    End Class
