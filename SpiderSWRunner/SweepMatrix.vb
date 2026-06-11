Imports System
Imports System.Collections.Generic
Imports System.Text
Imports Microsoft.VisualBasic

' ══════════════════════════════════════════════════════════════════
'  SweepMatrix.vb — batch run-table model and sweep expansion
'
'  One BatchRow = one FEA run. The sweep UI generates rows from a
'  base configuration plus swept variables; the rows serialize to a
'  CSV run table (single source of truth — eliminates GUI field
'  carryover risk) and feed BatchRunner.
' ══════════════════════════════════════════════════════════════════

Public Class BatchRow
    ' ── Identity / control ──
    Public RunID As Integer = 0
    Public Status As String = "PENDING"     ' PENDING / PASS / FAIL / SKIP
    Public Message As String = ""

    ' ── Geometry ──
    Public ComponentMode As Integer = 0     ' 0=Spider, 1=Edge
    Public ID As Double = 67.0
    Public OD As Double = 172.0
    Public N As Integer = 7
    Public H_pp As Double = 7.4
    Public T As Double = 0.8
    Public LipWidth As Double = 5.0
    Public InnerLipWidth As Double = 5.0
    Public FirstRollUp As Boolean = True

    ' ── Profile ──
    Public ProfileType As Integer = 0
    Public ConnectorAngle As Double = 45.0
    Public StraightLength As Double = 1.0
    Public CenterFlat As Double = 2.0
    Public BulletR_Top As Double = 3.0
    Public BulletCx As Double = 0.0
    Public TaperPct As Double = 0.0
    Public VariablePitch As Boolean = False
    Public PitchTaperPct As Double = 0.0
    Public UseNaturalH As Boolean = False

    ' ── Material ──
    Public MaterialName As String = "Rubber"
    Public E As Double = 6.1
    Public Nu As Double = 0.49
    Public Density As Double = 1000.0

    ' ── Simulation ──
    Public MaxDisp As Double = 10.0
    Public Steps As Integer = 20
    Public Direction As String = "Push"     ' Push / Pull
    Public OutputDir As String = "C:\SpiderSW_Results\Spider_Asymmetry"

    ' ── Precheck results (populated by BatchPrecheck) ──
    Public EffPitch As Double = 0.0
    Public MaxHpp As Double = 0.0
    Public Margin As Double = 0.0
    Public PrecheckPass As Boolean = False
    Public PrecheckNote As String = ""
    Public OutputFile As String = ""        ' full path of _auto.csv

    Public Function Clone() As BatchRow
        Return DirectCast(Me.MemberwiseClone(), BatchRow)
    End Function

    Public Function IsPull() As Boolean
        Return Direction.Trim().ToLower().StartsWith("pull")
    End Function

    ' Build the SpiderProfile this row describes
    Public Function ToProfile() As SpiderProfile
        Dim p As New SpiderProfile()
        p.ComponentMode = ComponentMode
        p.ID = ID
        p.OD = OD
        p.N = N
        p.H_pp = H_pp
        p.T = T
        p.LipWidth = LipWidth
        p.InnerLipWidth = InnerLipWidth
        p.FirstRollUp = FirstRollUp
        p.ProfileType = ProfileType
        p.ConnectorAngle = ConnectorAngle
        p.StraightLength = StraightLength
        p.CenterFlat = CenterFlat
        p.BulletR_Top = BulletR_Top
        p.BulletCx = BulletCx
        p.TaperPct = TaperPct
        p.VariablePitch = VariablePitch
        p.PitchTaperPct = PitchTaperPct
        p.UseNaturalH = UseNaturalH
        p.MaterialName = MaterialName
        p.E = E
        p.Nu = Nu
        p.Density = Density
        ' Forced N for single-roll edge types (mirrors Form1.BuildProfile)
        If p.ProfileType = 10 Then p.N = 1
        If p.ProfileType = 12 Then p.N = 1
        If p.ProfileType = 13 Then p.N = 1
        If p.ProfileType = 17 Then p.N = 1
        Return p
    End Function

    ' Geometry key ignoring direction — Push/Pull pair detection
    Public Function GeomKey() As String
        Return String.Format("{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11}|{12}|{13}|{14}|{15}|{16}|{17}|{18}|{19}|{20}", _
            ComponentMode, ID, OD, N, H_pp, T, LipWidth, InnerLipWidth, FirstRollUp, _
            ProfileType, ConnectorAngle, StraightLength, CenterFlat, BulletR_Top, BulletCx, _
            TaperPct, VariablePitch, PitchTaperPct, UseNaturalH, MaterialName, E)
    End Function
End Class

' ──────────────────────────────────────────────────────────────────
Public Class SweepVar
    Public Name As String = ""
    Public Values() As Double = New Double() {}

    ' Parse "0.25,0.3,0.38" or "0.2:0.05:0.4" (start:step:stop, inclusive)
    Public Shared Function ParseValues(ByVal text As String) As Double()
        Dim t As String = text.Trim()
        If t.Length = 0 Then Return New Double() {}
        Dim result As New List(Of Double)()
        If t.Contains(":") Then
            Dim parts() As String = t.Split(":"c)
            If parts.Length = 3 Then
                Dim v0 As Double = Double.Parse(parts(0).Trim())
                Dim st As Double = Double.Parse(parts(1).Trim())
                Dim v1 As Double = Double.Parse(parts(2).Trim())
                If st <= 0.0 Then Throw New System.Exception("Step must be > 0 in start:step:stop")
                Dim v As Double = v0
                Dim guard As Integer = 0
                While v <= v1 + 0.0000001 AndAlso guard < 10000
                    result.Add(Math.Round(v, 9))
                    v += st
                    guard += 1
                End While
            Else
                Throw New System.Exception("Range format is start:step:stop")
            End If
        Else
            For Each s As String In t.Split(","c)
                If s.Trim().Length > 0 Then result.Add(Double.Parse(s.Trim()))
            Next
        End If
        Return result.ToArray()
    End Function
End Class

' ──────────────────────────────────────────────────────────────────
Public Class SweepMatrix

    Public Const ModeOneAtATime As Integer = 0
    Public Const ModeFullFactorial As Integer = 1

    ' Sweepable variable names (Select Case in ApplyVar must match)
    Public Shared ReadOnly VarNames() As String = New String() { _
        "T", "H_pp", "OD", "ID", "N", "LipWidth", "InnerLipWidth", _
        "ConnAngle", "SLen", "TaperPct", "PitchTaperPct", _
        "E", "Nu", "Density", "MaxDisp", "Steps", _
        "BulletR_Top", "BulletCx", "CenterFlat"}

    Public Shared Sub ApplyVar(ByVal row As BatchRow, ByVal name As String, ByVal v As Double)
        Select Case name
            Case "T" : row.T = v
            Case "H_pp" : row.H_pp = v
            Case "OD" : row.OD = v
            Case "ID" : row.ID = v
            Case "N" : row.N = CInt(v)
            Case "LipWidth" : row.LipWidth = v
            Case "InnerLipWidth" : row.InnerLipWidth = v
            Case "ConnAngle" : row.ConnectorAngle = v
            Case "SLen" : row.StraightLength = v
            Case "TaperPct" : row.TaperPct = v
            Case "PitchTaperPct" : row.PitchTaperPct = v : row.VariablePitch = (v <> 0.0)
            Case "E" : row.E = v
            Case "Nu" : row.Nu = v
            Case "Density" : row.Density = v
            Case "MaxDisp" : row.MaxDisp = v
            Case "Steps" : row.Steps = CInt(v)
            Case "BulletR_Top" : row.BulletR_Top = v
            Case "BulletCx" : row.BulletCx = v
            Case "CenterFlat" : row.CenterFlat = v
            Case Else
                Throw New System.Exception("Unknown sweep variable: " & name)
        End Select
    End Sub

    ' ── Expand base + swept variables into a run table ──
    Public Shared Function Expand(ByVal base As BatchRow, _
                                  ByVal vars As List(Of SweepVar), _
                                  ByVal mode As Integer, _
                                  ByVal bothDirections As Boolean) As List(Of BatchRow)
        Dim combos As New List(Of BatchRow)()

        If vars Is Nothing OrElse vars.Count = 0 Then
            combos.Add(base.Clone())
        ElseIf mode = ModeOneAtATime Then
            ' Base point first, then each variable swept alone
            combos.Add(base.Clone())
            For Each sv As SweepVar In vars
                For Each v As Double In sv.Values
                    Dim r As BatchRow = base.Clone()
                    ApplyVar(r, sv.Name, v)
                    combos.Add(r)
                Next
            Next
        Else
            ' Full factorial (Cartesian product)
            combos.Add(base.Clone())
            For Each sv As SweepVar In vars
                Dim nextCombos As New List(Of BatchRow)()
                For Each c As BatchRow In combos
                    For Each v As Double In sv.Values
                        Dim r As BatchRow = c.Clone()
                        ApplyVar(r, sv.Name, v)
                        nextCombos.Add(r)
                    Next
                Next
                combos = nextCombos
            Next
        End If

        ' De-duplicate identical geometry+sim combinations
        Dim seen As New Dictionary(Of String, Boolean)()
        Dim unique As New List(Of BatchRow)()
        For Each c As BatchRow In combos
            Dim k As String = c.GeomKey() & "|" & c.MaxDisp & "|" & c.Steps
            If Not seen.ContainsKey(k) Then
                seen(k) = True
                unique.Add(c)
            End If
        Next

        ' Direction expansion (Push/Pull pairs adjacent: Push first)
        Dim final As New List(Of BatchRow)()
        For Each c As BatchRow In unique
            If bothDirections Then
                Dim push As BatchRow = c.Clone() : push.Direction = "Push"
                Dim pull As BatchRow = c.Clone() : pull.Direction = "Pull"
                final.Add(push)
                final.Add(pull)
            Else
                final.Add(c.Clone())
            End If
        Next

        For i As Integer = 0 To final.Count - 1
            final(i).RunID = i + 1
            final(i).Status = "PENDING"
        Next
        Return final
    End Function

    ' ── CSV run-table serialization ──
    Private Shared ReadOnly CsvCols() As String = New String() { _
        "RunID", "Status", "ComponentMode", "ID", "OD", "N", "H_pp", "T", _
        "LipWidth", "InnerLipWidth", "FirstRollUp", "ProfileType", _
        "ConnAngle", "SLen", "CenterFlat", "BulletR_Top", "BulletCx", _
        "TaperPct", "VariablePitch", "PitchTaperPct", "UseNaturalH", _
        "Material", "E", "Nu", "Density", "MaxDisp", "Steps", "Direction", _
        "OutputDir", "Message"}

    Public Shared Sub SaveCsv(ByVal rows As List(Of BatchRow), ByVal path As String)
        Dim sb As New StringBuilder()
        sb.AppendLine(String.Join(",", CsvCols))
        For Each r As BatchRow In rows
            Dim f() As String = New String() { _
                r.RunID.ToString(), r.Status, r.ComponentMode.ToString(), _
                r.ID.ToString(), r.OD.ToString(), r.N.ToString(), _
                r.H_pp.ToString(), r.T.ToString(), r.LipWidth.ToString(), _
                r.InnerLipWidth.ToString(), r.FirstRollUp.ToString(), _
                r.ProfileType.ToString(), r.ConnectorAngle.ToString(), _
                r.StraightLength.ToString(), r.CenterFlat.ToString(), _
                r.BulletR_Top.ToString(), r.BulletCx.ToString(), _
                r.TaperPct.ToString(), r.VariablePitch.ToString(), _
                r.PitchTaperPct.ToString(), r.UseNaturalH.ToString(), _
                CsvSafe(r.MaterialName), r.E.ToString(), r.Nu.ToString(), _
                r.Density.ToString(), r.MaxDisp.ToString(), r.Steps.ToString(), _
                r.Direction, CsvSafe(r.OutputDir), CsvSafe(r.Message)}
            sb.AppendLine(String.Join(",", f))
        Next
        System.IO.File.WriteAllText(path, sb.ToString())
    End Sub

    Private Shared Function CsvSafe(ByVal s As String) As String
        If s Is Nothing Then Return ""
        Return s.Replace(",", ";")
    End Function

    Public Shared Function LoadCsv(ByVal path As String) As List(Of BatchRow)
        Dim rows As New List(Of BatchRow)()
        Dim lines() As String = System.IO.File.ReadAllLines(path)
        If lines.Length < 2 Then Return rows
        Dim hdr() As String = lines(0).Split(","c)
        Dim ix As New Dictionary(Of String, Integer)()
        For i As Integer = 0 To hdr.Length - 1
            ix(hdr(i).Trim()) = i
        Next
        For li As Integer = 1 To lines.Length - 1
            Dim line As String = lines(li).Trim()
            If line.Length = 0 OrElse line.StartsWith("#") Then Continue For
            Dim f() As String = line.Split(","c)
            Dim r As New BatchRow()
            r.RunID = GetI(f, ix, "RunID", li)
            r.Status = GetS(f, ix, "Status", "PENDING")
            r.ComponentMode = GetI(f, ix, "ComponentMode", 0)
            r.ID = GetD(f, ix, "ID", 67.0)
            r.OD = GetD(f, ix, "OD", 172.0)
            r.N = GetI(f, ix, "N", 7)
            r.H_pp = GetD(f, ix, "H_pp", 7.4)
            r.T = GetD(f, ix, "T", 0.8)
            r.LipWidth = GetD(f, ix, "LipWidth", 5.0)
            r.InnerLipWidth = GetD(f, ix, "InnerLipWidth", 5.0)
            r.FirstRollUp = GetB(f, ix, "FirstRollUp", True)
            r.ProfileType = GetI(f, ix, "ProfileType", 0)
            r.ConnectorAngle = GetD(f, ix, "ConnAngle", 45.0)
            r.StraightLength = GetD(f, ix, "SLen", 1.0)
            r.CenterFlat = GetD(f, ix, "CenterFlat", 2.0)
            r.BulletR_Top = GetD(f, ix, "BulletR_Top", 3.0)
            r.BulletCx = GetD(f, ix, "BulletCx", 0.0)
            r.TaperPct = GetD(f, ix, "TaperPct", 0.0)
            r.VariablePitch = GetB(f, ix, "VariablePitch", False)
            r.PitchTaperPct = GetD(f, ix, "PitchTaperPct", 0.0)
            r.UseNaturalH = GetB(f, ix, "UseNaturalH", False)
            r.MaterialName = GetS(f, ix, "Material", "Rubber")
            r.E = GetD(f, ix, "E", 6.1)
            r.Nu = GetD(f, ix, "Nu", 0.49)
            r.Density = GetD(f, ix, "Density", 1000.0)
            r.MaxDisp = GetD(f, ix, "MaxDisp", 10.0)
            r.Steps = GetI(f, ix, "Steps", 20)
            r.Direction = GetS(f, ix, "Direction", "Push")
            r.OutputDir = GetS(f, ix, "OutputDir", "C:\SpiderSW_Results\Spider_Asymmetry")
            r.Message = GetS(f, ix, "Message", "")
            rows.Add(r)
        Next
        Return rows
    End Function

    Private Shared Function GetS(ByVal f() As String, ByVal ix As Dictionary(Of String, Integer), _
                                 ByVal col As String, ByVal def As String) As String
        If ix.ContainsKey(col) AndAlso ix(col) < f.Length Then
            Dim v As String = f(ix(col)).Trim()
            If v.Length > 0 Then Return v
        End If
        Return def
    End Function
    Private Shared Function GetD(ByVal f() As String, ByVal ix As Dictionary(Of String, Integer), _
                                 ByVal col As String, ByVal def As Double) As Double
        Dim v As Double
        If Double.TryParse(GetS(f, ix, col, ""), v) Then Return v
        Return def
    End Function
    Private Shared Function GetI(ByVal f() As String, ByVal ix As Dictionary(Of String, Integer), _
                                 ByVal col As String, ByVal def As Integer) As Integer
        Dim v As Integer
        If Integer.TryParse(GetS(f, ix, col, ""), v) Then Return v
        Return def
    End Function
    Private Shared Function GetB(ByVal f() As String, ByVal ix As Dictionary(Of String, Integer), _
                                 ByVal col As String, ByVal def As Boolean) As Boolean
        Dim s As String = GetS(f, ix, col, "").ToLower()
        If s = "true" OrElse s = "1" Then Return True
        If s = "false" OrElse s = "0" Then Return False
        Return def
    End Function
End Class
