Imports System
Imports System.Collections.Generic
Imports System.Text

    Public Structure ProfilePoint
        Public R As Double
        Public Z As Double
        Public Sub New(ByVal r As Double, ByVal z As Double)
            Me.R = r
            Me.Z = z
        End Sub
        Public Overrides Function ToString() As String
            Return String.Format("({0:F3}, {1:F3})", R, Z)
        End Function
    End Structure

    Public Structure ProfileSegment
        Public IsLine As Boolean
        Public Points As List(Of ProfilePoint)
        Public Sub New(isLine As Boolean, pts As List(Of ProfilePoint))
            Me.IsLine = isLine
            Me.Points = pts
        End Sub
    End Structure

    ' One true circular arc per roll, in absolute (R, Z) coordinates (mm).
    ' Used by SWAutomation.CreatePart for ProfileType=1, 10, 11 to draw real arc
    ' sketch entities (CreateArc) instead of a smoothed spline. Adjacent
    ' rolls sharing the endpoint at z=0 produce sharp zero-crossing corners.
    Public Structure CircularArcRoll
        Public Rc As Double         ' arc center R (mm)
        Public Zc As Double         ' arc center Z (mm), signed
        Public R_arc As Double      ' arc radius (mm)
        Public R_start As Double    ' start point R at z=0 (mm)
        Public R_end As Double      ' end point R at z=0 (mm)
        Public IsUp As Boolean      ' True = dome above z=0, False = valley below
    End Structure

    ' General arc with arbitrary start/end points (not restricted to z=0).
    ' Used for Bullet profile (ProfileType=12) where the three arcs meet at
    ' tangent points above z=0.
    Public Structure GeneralArc
        Public Rc As Double         ' arc center R (mm)
        Public Zc As Double         ' arc center Z (mm)
        Public R_arc As Double      ' arc radius (mm)
        Public Rs As Double         ' start point R (mm)
        Public Zs As Double         ' start point Z (mm)
        Public Re As Double         ' end point R (mm)
        Public Ze As Double         ' end point Z (mm)
        Public Direction As Integer ' SW arc direction: -1=CW, +1=CCW
    End Structure

    ''' <summary>
    ''' Per-roll realized geometry for the FEA calibration matrix.
    ''' Returned by ComputeRollMetrics(). All arrays are length N.
    ''' Class (not Structure) to avoid VS2010 value-type return quirks.
    ''' </summary>
    Public Class RollMetrics
        Public RollStart() As Double    ' start radius of each roll (mm)
        Public Pitch() As Double        ' pitch of each roll (mm)
        Public H_eff_roll() As Double   ' effective half-height of each roll (mm)
        Public H_natural() As Double    ' natural half-height (ArcLines/SineLines only, mm)
        Public RollCount As Integer     ' = N
    End Class

    Public Class SpiderProfile

        ' ── Mode ──
        ' 0 = Spider, 1 = Edge
        Public ComponentMode As Integer = 0

        ' ── Geometry inputs ──
        Public ID As Double = 67.0
        Public OD As Double = 172.0
        Public N As Integer = 7
        Public H_pp As Double = 7.4
        Public T As Double = 0.8
        Public LipWidth As Double = 5.0
        Public InnerLipWidth As Double = 5.0
        Public FirstRollUp As Boolean = True

        ' ── Profile type ──
        ' Spider profiles:
        '   0 = Sinusoidal
        '   1 = CircularArc (half-arc: semicircular peaks/valleys)
        '   2 = ArcLines    (circular arc at peak/valley + straight tangent connectors)
        '   3 = SineLines   (sinusoidal arc at peak/valley + straight tangent connectors)
        '   99 = COMSOL     (exact STEP geometry, N=7 only)
        ' Edge profiles:
        '   10 = HalfRoll   (single circular arc, N forced to 1)
        '   11 = DoubleRoll (two circular arcs same direction + center flat)
        Public ProfileType As Integer = 0

        ' For ArcLines and SineLines: physical length of diagonal straight connector (mm)
        ' and angle of straight from horizontal (degrees)
        ' 30° = shorter straights (rounder), 60° = longer straights (more trapezoidal)
        Public StraightLength As Double = 1.0
        Public ConnectorAngle As Double = 45.0

        ' For DoubleRoll (ProfileType=11): flat section width between the two rolls (mm)
        Public CenterFlat As Double = 2.0

        ' For Bullet (ProfileType=12): three-arc bullet built in SW via sketch relations.
        ' User inputs:
        '   BulletR_Top = R2, radius of center top arc (Arc 2)
        '   BulletCx    = Cx, horizontal distance from ID vertical line to Arc 2 center
        ' Constraints: width/3 <= Cx <= 2*width/3 where width = (OD - ID)/2 in Edge mode.
        ' Arcs 1 and 3 are tangent arcs drawn by SW's solver.
        Public BulletR_Top As Double = 3.0
        Public BulletCx As Double = 0.0

        ' ── Material inputs ──
        Public E As Double = 6.1
        Public Nu As Double = 0.49
        Public Density As Double = 1000.0
        Public MaterialName As String = "Rubber"

        ' ── FEA Calibration Campaign: Taper & Variable Pitch ──
        ' TaperPct: 0 = uniform, 100 = inner roll at 10% floor.
        '   taper_frac = clamp(TaperPct/100, 0, 1)
        '   hScale_inner = max(0.10, 1 - taper_frac)
        '   Per-roll: hScale_n = hScale_inner + (1 - hScale_inner) * t_n, t_n = n/(N-1)
        '   H_eff_n = H_eff * hScale_n
        ' VariablePitch / PitchTaperPct: linearly varies pitch from inner to outer.
        '   Positive PitchTaperPct -> inner wider, outer narrower.
        '   Pitches are normalized to sum to W_corr exactly.
        ' UseNaturalH: when True, ArcLines/SineLines use H_n_natural derived from
        '   pitch_n and ConnectorAngle instead of H_eff_n. The hScale vertical
        '   stretching is bypassed, preserving the true arc geometry and tangent angles.
        '   Ignored for Sinusoidal (where H is always a free parameter).
        '   Required for FEA calibration runs on ArcLines/SineLines profiles.
        Public TaperPct As Double = 0.0
        Public VariablePitch As Boolean = False
        Public PitchTaperPct As Double = 0.0
        Public UseNaturalH As Boolean = False

        ' ── Computed geometry ──
        Public ReadOnly Property R_inner As Double
            Get
                Return ID / 2.0
            End Get
        End Property

        Public ReadOnly Property R_outer As Double
            Get
                Return OD / 2.0
            End Get
        End Property

        Public ReadOnly Property R_rolls_outer As Double
            Get
                Return R_outer - LipWidth
            End Get
        End Property

        Public ReadOnly Property FilletR As Double
            Get
                Return 2.0 * T
            End Get
        End Property

        Public ReadOnly Property R_roll_start As Double
            Get
                Return R_inner + FilletR
            End Get
        End Property

        Public ReadOnly Property EffPitch As Double
            Get
                If N <= 0 Then Return 1.0
                Return (R_rolls_outer - R_roll_start) / N
            End Get
        End Property

        ''' <summary>
        ''' For DoubleRoll, effective pitch per roll accounting for CenterFlat.
        ''' For all other types, same as EffPitch.
        ''' </summary>
        Public ReadOnly Property EffPitchPerRoll As Double
            Get
                If ProfileType = 11 Then
                    Dim span As Double = R_rolls_outer - R_roll_start
                    Dim flat As Double = Math.Max(0, Math.Min(CenterFlat, span * 0.8))
                    Return (span - flat) / 2.0
                End If
                Return EffPitch
            End Get
        End Property

        Public ReadOnly Property H_eff As Double
            Get
                Return H_pp / 2.0
            End Get
        End Property

        Public ReadOnly Property MaxH_pp As Double
            Get
                Return 2.0 * 0.75 * EffPitchPerRoll
            End Get
        End Property

        Public ReadOnly Property AspectRatio As Double
            Get
                Dim p As Double = EffPitchPerRoll
                If p <= 0 Then Return 0
                Return H_eff / p
            End Get
        End Property

        ' ══════════════════════════════════════════════════════════════
        ' Per-roll geometry computation (FEA calibration campaign)
        ' Implements handoff §4.4 (taper) and §4.5 (variable pitch)
        ' ══════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Compute natural half-height for ArcLines at a given pitch and angle.
        ''' This is the geometric height determined solely by connector angle + pitch.
        ''' Matches handoff §4.7.
        ''' </summary>
        Public Shared Function ComputeNaturalH_ArcLines(ByVal pitch As Double, _
                ByVal angleDeg As Double, ByVal straightLen As Double) As Double
            Dim PI As Double = Math.PI
            Dim theta As Double = Math.Max(0.1, Math.Min(89.9, angleDeg)) * PI / 180.0
            Dim sth As Double = Math.Sin(theta)
            Dim cth As Double = Math.Cos(theta)
            Dim s As Double = Math.Max(0.0, Math.Min(straightLen, pitch * 0.48))
            Dim r_j As Double = (s / 2.0) * cth
            Dim z_j As Double = (s / 2.0) * sth
            Dim wa As Double = pitch - 2.0 * r_j
            If wa <= 0.001 Then Return z_j
            Dim a As Double = wa / 2.0
            Return z_j + a * (1.0 - cth) / Math.Max(sth, 0.001)
        End Function

        ''' <summary>
        ''' Compute natural half-height for SineLines at a given pitch and angle.
        ''' </summary>
        Public Shared Function ComputeNaturalH_SineLines(ByVal pitch As Double, _
                ByVal angleDeg As Double, ByVal straightLen As Double) As Double
            Dim PI As Double = Math.PI
            Dim theta As Double = Math.Max(0.1, Math.Min(89.9, angleDeg)) * PI / 180.0
            Dim sth As Double = Math.Sin(theta)
            Dim cth As Double = Math.Cos(theta)
            Dim s As Double = Math.Max(0.0, Math.Min(straightLen, pitch * 0.48))
            Dim r_j As Double = (s / 2.0) * cth
            Dim z_j As Double = (s / 2.0) * sth
            Dim wa As Double = pitch - 2.0 * r_j
            If wa <= 0.001 Then Return z_j
            Dim amp As Double = Math.Tan(theta) * wa / PI
            Return z_j + amp
        End Function

        ''' <summary>
        ''' Compute per-roll geometry arrays: start radii, pitches, and half-heights.
        ''' Implements handoff §4.4 (taper with 10% floor) and §4.5 (variable pitch).
        '''
        ''' When TaperPct=0 and VariablePitch=False and UseNaturalH=False:
        '''   All Pitch(n) = EffPitch, all H_eff_roll(n) = H_eff — identical to legacy.
        '''
        ''' When UseNaturalH=True and ProfileType is ArcLines(2) or SineLines(3):
        '''   H_eff_roll(n) = H_natural(n) computed from Pitch(n) and ConnectorAngle.
        '''   The TaperPct-derived linear taper is computed but stored only in H_natural
        '''   for logging; it does NOT override the natural H.
        '''   For Sinusoidal (ProfileType=0), UseNaturalH is ignored.
        ''' </summary>
        ' Diagnostic output from last ComputeRollMetrics call
        Public _diagCRM As String = ""

        Public Function ComputeRollMetrics() As RollMetrics
            Dim rm As New RollMetrics()
            If N <= 0 Then
                rm.RollCount = 0
                rm.RollStart = New Double() {}
                rm.Pitch = New Double() {}
                rm.H_eff_roll = New Double() {}
                rm.H_natural = New Double() {}
                _diagCRM = "N<=0, early return"
                Return rm
            End If

            rm.RollCount = N

            Dim rollStarts(N - 1) As Double
            Dim pitches(N - 1) As Double
            Dim hEffRolls(N - 1) As Double
            Dim hNaturals(N - 1) As Double

            Dim W_corr As Double = R_rolls_outer - R_roll_start

            Dim basePitch As Double
            If N > 0 Then
                basePitch = W_corr / CDbl(N)
            Else
                basePitch = W_corr
            End If

            ' §4.5 — Variable pitch
            Dim pitchTaperFrac As Double = 0.0
            If VariablePitch Then
                pitchTaperFrac = PitchTaperPct / 100.0
            End If
            Dim maxDelta As Double = basePitch * 0.90
            Dim delta As Double = basePitch * pitchTaperFrac
            If delta > maxDelta Then delta = maxDelta
            If delta < -maxDelta Then delta = -maxDelta
            Dim pitch_id_raw As Double = basePitch + delta
            Dim pitch_od_raw As Double = basePitch - delta

            _diagCRM = String.Format("W={0:F4} bp={1:F4} pidr={2:F4} podr={3:F4} N={4}", _
                W_corr, basePitch, pitch_id_raw, pitch_od_raw, N)

            ' Compute raw per-roll pitches and normalize
            Dim pitchSum As Double = 0.0
            For idx As Integer = 0 To N - 1
                Dim t_n As Double
                If N > 1 Then
                    t_n = CDbl(idx) / CDbl(N - 1)
                Else
                    t_n = 0.0
                End If
                pitches(idx) = pitch_id_raw + (pitch_od_raw - pitch_id_raw) * t_n
                pitchSum += pitches(idx)
            Next

            _diagCRM = _diagCRM & String.Format(" pSum={0:F4} p0={1:F4} p6={2:F4}", _
                pitchSum, pitches(0), pitches(N - 1))

            ' Normalize so pitches sum to W_corr exactly
            If pitchSum > 0.0001 Then
                Dim scale As Double = W_corr / pitchSum
                For idx As Integer = 0 To N - 1
                    pitches(idx) = pitches(idx) * scale
                Next
            End If

            _diagCRM = _diagCRM & String.Format(" p0post={0:F4}", pitches(0))

            ' Compute roll start radii
            rollStarts(0) = R_roll_start
            For idx As Integer = 1 To N - 1
                rollStarts(idx) = rollStarts(idx - 1) + pitches(idx - 1)
            Next

            ' §4.4 — Taper (applied to H)
            Dim taper_frac As Double = Math.Max(0.0, Math.Min(1.0, TaperPct / 100.0))
            Dim hScale_inner As Double = Math.Max(0.10, 1.0 - taper_frac)
            Dim hScale_outer As Double = 1.0
            Dim h_outer As Double = H_eff

            _diagCRM = _diagCRM & String.Format(" hOuter={0:F4}", h_outer)

            For idx As Integer = 0 To N - 1
                Dim t_n As Double
                If N > 1 Then
                    t_n = CDbl(idx) / CDbl(N - 1)
                Else
                    t_n = 0.0
                End If
                Dim hScale_n As Double = hScale_inner + (hScale_outer - hScale_inner) * t_n
                Dim h_tapered As Double = h_outer * hScale_n

                ' Compute natural H for ArcLines/SineLines (always, for logging)
                If ProfileType = 2 Then
                    hNaturals(idx) = ComputeNaturalH_ArcLines(pitches(idx), ConnectorAngle, StraightLength)
                ElseIf ProfileType = 3 Then
                    hNaturals(idx) = ComputeNaturalH_SineLines(pitches(idx), ConnectorAngle, StraightLength)
                Else
                    hNaturals(idx) = 0.0
                End If

                ' Determine effective H for this roll
                If UseNaturalH AndAlso (ProfileType = 2 OrElse ProfileType = 3) Then
                    hEffRolls(idx) = hNaturals(idx)
                Else
                    hEffRolls(idx) = h_tapered
                End If
            Next

            ' Assign local arrays to class
            rm.RollStart = rollStarts
            rm.Pitch = pitches
            rm.H_eff_roll = hEffRolls
            rm.H_natural = hNaturals

            Return rm
        End Function

        ' ══════════════════════════════════════════════════════════════
        ' COMSOL Arc+Lines exact profile — decoded from STEP file
        ' 9 arcs: ID fillet + 7 rolls + OD fillet
        ' ══════════════════════════════════════════════════════════════
        Private Shared ReadOnly _Rc() As Double = {36.25, 40.32, 45.09, 50.97, 58.27, 66.69, 75.11, 82.10, 86.00}
        Private Shared ReadOnly _Yc() As Double = {-1.0,   0.5,   1.0,   0.5,   1.5,   0.5,   1.5,   1.0,   1.0}
        Private Shared ReadOnly _Ra() As Double = { 1.0,   2.0,   2.5,   3.0,   4.0,   4.0,   4.0,   2.5,   1.0}
        Private Shared ReadOnly _d()  As Integer = {-1,   +1,    -1,   +1,    -1,   +1,    -1,   +1,    -1}

        Private Shared Sub ComputeTangentPoints(i As Integer,
                ByRef TR_R As Double, ByRef TR_Y As Double,
                ByRef TL_R As Double, ByRef TL_Y As Double)
            Dim DR As Double = _Rc(i+1) - _Rc(i)
            Dim DY As Double = _Yc(i+1) - _Yc(i)
            Dim L  As Double = Math.Sqrt(DR*DR + DY*DY)
            Dim Rt As Double = _Ra(i) + _Ra(i+1)
            Dim phi As Double = Math.Atan2(_d(i) * DY, DR)
            Dim theta As Double = Math.Asin(Math.Max(-1.0, Math.Min(1.0, Rt / L))) - phi
            Dim sT As Double = Math.Sin(theta)
            Dim cT As Double = Math.Cos(theta)
            TR_R = _Rc(i)   + _Ra(i)   *  sT
            TR_Y = _Yc(i)   + _Ra(i)   * (_d(i)   * cT)
            TL_R = _Rc(i+1) + _Ra(i+1) * (-sT)
            TL_Y = _Yc(i+1) + _Ra(i+1) * (_d(i+1) * cT)
        End Sub

        Private Shared Function ArcAngle(i As Integer, PR As Double, PY As Double) As Double
            Dim sinA As Double = (PR - _Rc(i)) / _Ra(i)
            Dim cosA As Double = (PY - _Yc(i)) / (_d(i) * _Ra(i))
            Return Math.Atan2(sinA, Math.Max(-1.0, Math.Min(1.0, cosA)))
        End Function

        Public Function GeneratePoints_COMSOL(Optional ByVal ptsPerArc As Integer = 20) As List(Of ProfilePoint)
            Const nArcs As Integer = 9

            Dim TL_R(nArcs-1) As Double, TL_Y(nArcs-1) As Double
            Dim TR_R(nArcs-1) As Double, TR_Y(nArcs-1) As Double

            TL_R(0) = _Rc(0) : TL_Y(0) = 0.0

            For i As Integer = 0 To nArcs - 2
                ComputeTangentPoints(i, TR_R(i), TR_Y(i), TL_R(i+1), TL_Y(i+1))
            Next

            TR_R(nArcs-1) = _Rc(nArcs-1) : TR_Y(nArcs-1) = 0.0

            Dim pts As New List(Of ProfilePoint)
            pts.Add(New ProfilePoint(R_inner, 0.0))

            For i As Integer = 1 To nArcs - 1
                Dim alphaL As Double = ArcAngle(i, TL_R(i), TL_Y(i))
                Dim alphaR As Double = ArcAngle(i, TR_R(i), TR_Y(i))
                Dim nPts As Integer = If(i = nArcs-1, ptsPerArc \ 2, ptsPerArc)

                pts.Add(New ProfilePoint(TL_R(i), TL_Y(i)))

                For k As Integer = 1 To nPts - 1
                    Dim a As Double = alphaL + (alphaR - alphaL) * CDbl(k) / CDbl(nPts)
                    pts.Add(New ProfilePoint(_Rc(i) + _Ra(i) * Math.Sin(a),
                                             _Yc(i) + _Ra(i) * _d(i) * Math.Cos(a)))
                Next

                pts.Add(New ProfilePoint(TR_R(i), TR_Y(i)))
            Next

            pts.Add(New ProfilePoint(R_outer + 5.0, 0.0))
            Return pts
        End Function

        ''' <summary>
        ''' Generate profile points. Dispatches by ProfileType.
        ''' </summary>
        Public Function GeneratePoints(Optional ByVal pointsPerRoll As Integer = 30) As List(Of ProfilePoint)
            Select Case ProfileType
                Case 1 : Return GeneratePoints_CircularArc(pointsPerRoll)
                Case 2 : Return GeneratePoints_ArcLines(pointsPerRoll)
                Case 3 : Return GeneratePoints_SineLines(pointsPerRoll)
                Case 10 : Return GeneratePoints_HalfRoll(pointsPerRoll)
                Case 11 : Return GeneratePoints_DoubleRoll(pointsPerRoll)
                Case 12 : Return GeneratePoints_Bullet(pointsPerRoll)
                Case 99 : Return GeneratePoints_COMSOL(pointsPerRoll)
                Case Else : Return GeneratePoints_Sinusoidal(pointsPerRoll)
            End Select
        End Function

        ' ══════════════════════════════════════════════════════════════
        ' CircularArc (ProfileType=1) — spider multi-roll arcs
        ' ══════════════════════════════════════════════════════════════

        Public Function GetCircularArcRolls() As List(Of CircularArcRoll)
            Dim rolls As New List(Of CircularArcRoll)
            Dim pitch   As Double = EffPitch
            Dim h       As Double = H_eff
            Dim half_hp As Double = pitch / 2.0
            If h <= 0.0001 OrElse pitch <= 0.0001 Then Return rolls

            Dim R_arc   As Double = (h * h + half_hp * half_hp) / (2.0 * h)
            Dim ZcUp    As Double = (h * h - half_hp * half_hp) / (2.0 * h)

            For roll As Integer = 0 To N - 1
                Dim isUp    As Boolean = If(roll Mod 2 = 0, FirstRollUp, Not FirstRollUp)
                Dim rs      As Double  = R_roll_start + roll * pitch
                Dim re      As Double  = rs + pitch
                Dim rc      As Double  = rs + half_hp
                Dim zc      As Double  = If(isUp, ZcUp, -ZcUp)

                Dim rl As New CircularArcRoll()
                rl.Rc = rc
                rl.Zc = zc
                rl.R_arc = R_arc
                rl.R_start = rs
                rl.R_end = re
                rl.IsUp = isUp
                rolls.Add(rl)
            Next
            Return rolls
        End Function

        Public Function GeneratePoints_CircularArc(Optional ByVal ptsPerArc As Integer = 40) As List(Of ProfilePoint)
            Dim pts As New List(Of ProfilePoint)
            pts.Add(New ProfilePoint(R_inner, 0.0))
            pts.Add(New ProfilePoint(R_roll_start, 0.0))

            Dim rolls As List(Of CircularArcRoll) = GetCircularArcRolls()
            If rolls.Count = 0 Then
                pts.Add(New ProfilePoint(R_rolls_outer, 0.0))
                pts.Add(New ProfilePoint(R_rolls_outer + LipWidth + 5.0, 0.0))
                Return pts
            End If

            Dim PI As Double = Math.PI
            For Each rl As CircularArcRoll In rolls
                Dim aStart As Double = Math.Atan2(0.0 - rl.Zc, rl.R_start - rl.Rc)
                Dim aEnd   As Double = Math.Atan2(0.0 - rl.Zc, rl.R_end   - rl.Rc)

                If rl.IsUp Then
                    If aStart < PI / 2.0 Then aStart += 2.0 * PI
                    If aEnd   > aStart Then aEnd   -= 2.0 * PI
                Else
                    If aStart > -PI / 2.0 Then aStart -= 2.0 * PI
                    If aEnd   < aStart   Then aEnd   += 2.0 * PI
                End If

                For k As Integer = 1 To ptsPerArc
                    Dim t As Double = CDbl(k) / CDbl(ptsPerArc)
                    Dim a As Double = aStart + (aEnd - aStart) * t
                    Dim r As Double = rl.Rc + rl.R_arc * Math.Cos(a)
                    Dim z As Double = rl.Zc + rl.R_arc * Math.Sin(a)
                    pts.Add(New ProfilePoint(r, z))
                Next
            Next

            pts.Add(New ProfilePoint(R_rolls_outer + LipWidth + 5.0, 0.0))
            Return pts
        End Function

        ' ══════════════════════════════════════════════════════════════
        ' HalfRoll (ProfileType=10) — single circular arc, edge surround
        ' Uses same math as CircularArc but always one roll spanning the
        ' full corrugation zone. N is ignored; pitch = full span.
        ' ══════════════════════════════════════════════════════════════

        Public Function GetHalfRollArc() As List(Of CircularArcRoll)
            Dim rolls As New List(Of CircularArcRoll)
            Dim span    As Double = R_rolls_outer - R_roll_start
            Dim h       As Double = H_eff
            Dim half_hp As Double = span / 2.0
            If h <= 0.0001 OrElse span <= 0.0001 Then Return rolls

            Dim R_arc As Double = (h * h + half_hp * half_hp) / (2.0 * h)
            Dim ZcUp  As Double = (h * h - half_hp * half_hp) / (2.0 * h)

            Dim rl As New CircularArcRoll()
            rl.R_start = R_roll_start
            rl.R_end = R_rolls_outer
            rl.Rc = R_roll_start + half_hp
            rl.Zc = If(FirstRollUp, ZcUp, -ZcUp)
            rl.R_arc = R_arc
            rl.IsUp = FirstRollUp
            rolls.Add(rl)
            Return rolls
        End Function

        Public Function GeneratePoints_HalfRoll(Optional ByVal ptsPerArc As Integer = 40) As List(Of ProfilePoint)
            Dim pts As New List(Of ProfilePoint)
            pts.Add(New ProfilePoint(R_inner, 0.0))
            pts.Add(New ProfilePoint(R_roll_start, 0.0))

            Dim rolls As List(Of CircularArcRoll) = GetHalfRollArc()
            If rolls.Count = 0 Then
                pts.Add(New ProfilePoint(R_rolls_outer, 0.0))
                pts.Add(New ProfilePoint(R_rolls_outer + LipWidth + 5.0, 0.0))
                Return pts
            End If

            Dim PI As Double = Math.PI
            Dim rl As CircularArcRoll = rolls(0)
            Dim aStart As Double = Math.Atan2(0.0 - rl.Zc, rl.R_start - rl.Rc)
            Dim aEnd   As Double = Math.Atan2(0.0 - rl.Zc, rl.R_end   - rl.Rc)

            If rl.IsUp Then
                If aStart < PI / 2.0 Then aStart += 2.0 * PI
                If aEnd   > aStart Then aEnd   -= 2.0 * PI
            Else
                If aStart > -PI / 2.0 Then aStart -= 2.0 * PI
                If aEnd   < aStart   Then aEnd   += 2.0 * PI
            End If

            For k As Integer = 1 To ptsPerArc
                Dim t As Double = CDbl(k) / CDbl(ptsPerArc)
                Dim a As Double = aStart + (aEnd - aStart) * t
                pts.Add(New ProfilePoint(rl.Rc + rl.R_arc * Math.Cos(a),
                                         rl.Zc + rl.R_arc * Math.Sin(a)))
            Next

            pts.Add(New ProfilePoint(R_rolls_outer + LipWidth + 5.0, 0.0))
            Return pts
        End Function

        ' ══════════════════════════════════════════════════════════════
        ' DoubleRoll (ProfileType=11) — two circular arcs, same direction,
        ' separated by a flat center section. Typical double-roll surround.
        '
        ' Span is divided: roll_pitch + CenterFlat + roll_pitch
        ' Both arcs go in the direction set by FirstRollUp (both up or both down).
        ' ══════════════════════════════════════════════════════════════

        Public Function GetDoubleRollArcs() As List(Of CircularArcRoll)
            Dim rolls As New List(Of CircularArcRoll)
            Dim span As Double = R_rolls_outer - R_roll_start
            Dim flat As Double = Math.Max(0.0, Math.Min(CenterFlat, span * 0.8))
            Dim roll_pitch As Double = (span - flat) / 2.0
            Dim h As Double = H_eff
            Dim half_hp As Double = roll_pitch / 2.0
            If h <= 0.0001 OrElse roll_pitch <= 0.0001 Then Return rolls

            Dim R_arc As Double = (h * h + half_hp * half_hp) / (2.0 * h)
            Dim ZcVal As Double = (h * h - half_hp * half_hp) / (2.0 * h)
            Dim Zc As Double = If(FirstRollUp, ZcVal, -ZcVal)

            ' Roll 1
            Dim rl1 As New CircularArcRoll()
            rl1.R_start = R_roll_start
            rl1.R_end = R_roll_start + roll_pitch
            rl1.Rc = R_roll_start + half_hp
            rl1.Zc = Zc
            rl1.R_arc = R_arc
            rl1.IsUp = FirstRollUp
            rolls.Add(rl1)

            ' Roll 2 (same direction as roll 1)
            Dim r2_start As Double = R_roll_start + roll_pitch + flat
            Dim rl2 As New CircularArcRoll()
            rl2.R_start = r2_start
            rl2.R_end = r2_start + roll_pitch
            rl2.Rc = r2_start + half_hp
            rl2.Zc = Zc
            rl2.R_arc = R_arc
            rl2.IsUp = FirstRollUp
            rolls.Add(rl2)

            Return rolls
        End Function

        Public Function GeneratePoints_DoubleRoll(Optional ByVal ptsPerArc As Integer = 40) As List(Of ProfilePoint)
            Dim pts As New List(Of ProfilePoint)
            pts.Add(New ProfilePoint(R_inner, 0.0))
            pts.Add(New ProfilePoint(R_roll_start, 0.0))

            Dim rolls As List(Of CircularArcRoll) = GetDoubleRollArcs()
            If rolls.Count = 0 Then
                pts.Add(New ProfilePoint(R_rolls_outer, 0.0))
                pts.Add(New ProfilePoint(R_rolls_outer + LipWidth + 5.0, 0.0))
                Return pts
            End If

            Dim PI As Double = Math.PI
            For ri As Integer = 0 To rolls.Count - 1
                Dim rl As CircularArcRoll = rolls(ri)
                Dim aStart As Double = Math.Atan2(0.0 - rl.Zc, rl.R_start - rl.Rc)
                Dim aEnd   As Double = Math.Atan2(0.0 - rl.Zc, rl.R_end   - rl.Rc)

                If rl.IsUp Then
                    If aStart < PI / 2.0 Then aStart += 2.0 * PI
                    If aEnd   > aStart Then aEnd   -= 2.0 * PI
                Else
                    If aStart > -PI / 2.0 Then aStart -= 2.0 * PI
                    If aEnd   < aStart   Then aEnd   += 2.0 * PI
                End If

                ' Start point of this arc (at z=0)
                pts.Add(New ProfilePoint(rl.R_start, 0.0))

                For k As Integer = 1 To ptsPerArc
                    Dim t As Double = CDbl(k) / CDbl(ptsPerArc)
                    Dim a As Double = aStart + (aEnd - aStart) * t
                    pts.Add(New ProfilePoint(rl.Rc + rl.R_arc * Math.Cos(a),
                                             rl.Zc + rl.R_arc * Math.Sin(a)))
                Next

                ' If there's a flat gap to next arc, add the flat endpoint
                If ri < rolls.Count - 1 Then
                    Dim nextStart As Double = rolls(ri + 1).R_start
                    If nextStart - rl.R_end > 0.01 Then
                        pts.Add(New ProfilePoint(rl.R_end, 0.0))
                        pts.Add(New ProfilePoint(nextStart, 0.0))
                    End If
                End If
            Next

            pts.Add(New ProfilePoint(R_rolls_outer, 0.0))
            pts.Add(New ProfilePoint(R_rolls_outer + LipWidth + 5.0, 0.0))
            Return pts
        End Function

        ' ══════════════════════════════════════════════════════════════
        ' Bullet (ProfileType=12) — three-arc bullet built in SolidWorks via
        ' sketch relations and dimensions.
        '
        ' User inputs:
        '   BulletR_Top   = R2, radius of center top arc (Arc 2)
        '   BulletCx      = Cx, distance from ID vertical construction line to
        '                       Arc 2 center. Constrained to [width/3, 2*width/3].
        '   InnerLipWidth = inner lip flat length (applies to all edge types)
        '
        ' Construction happens entirely in SWAutomation.CreatePart using
        ' Create3PointArc + SketchAddConstraints + AddDimension2 calls.
        ' The VB-side GetBulletArcs() returns an approximation only for CSV
        ' exports and fallback; it does not drive the SW geometry.
        ' ══════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Bullet width in mm: (OD - ID)/2 per user specification.
        ''' This is the literal span from Cone_OD edge to Basket_ID edge in Edge mode.
        ''' </summary>
        Public ReadOnly Property BulletWidth As Double
            Get
                Return (OD - ID) / 2.0
            End Get
        End Property

        ''' <summary>
        ''' Compute effective Cx (auto-centers if BulletCx = 0).
        ''' </summary>
        Public Function GetBulletCx() As Double
            Dim w As Double = BulletWidth
            If BulletCx <= 0 Then Return w / 2.0
            Return BulletCx
        End Function

        ''' <summary>
        ''' Compute bullet radii (legacy compatibility out-params).
        ''' Both return R2 (the center top arc radius).
        ''' </summary>
        Public Function ComputeBulletRadii(ByRef R_id As Double, ByRef R_od As Double) As Boolean
            If BulletWidth <= 0 OrElse H_pp <= 0 OrElse BulletR_Top <= 0 Then Return False
            R_id = BulletR_Top
            R_od = BulletR_Top
            Return True
        End Function

        ''' <summary>
        ''' Validate bullet parameters. Returns empty string if OK, or error message.
        ''' </summary>
        Public Function ValidateBulletParams() As String
            Dim w As Double = BulletWidth
            If w <= 0 Then Return String.Format("Bullet width = {0:F2}mm must be positive (OD > ID required).", w)
            If H_pp <= 0 Then Return "H_pp must be positive."
            If BulletR_Top <= 0 Then Return "R2 (bullet top radius) must be positive."

            Dim cx As Double = GetBulletCx()
            Dim cxMin As Double = w / 3.0
            Dim cxMax As Double = 2.0 * w / 3.0
            If cx < cxMin OrElse cx > cxMax Then
                Return String.Format("Cx={0:F2}mm must be between {1:F2} and {2:F2} (width/3 to 2*width/3).", _
                    cx, cxMin, cxMax)
            End If
            Return ""
        End Function

        ''' <summary>
        ''' Returns an APPROXIMATE 3-arc bullet in absolute (R, Z) coordinates.
        ''' For ProfileType=12, SW builds the real geometry from sketch relations
        ''' (see SWAutomation.CreatePart). This function is used for CSV exports,
        ''' point-count diagnostics, and fallback spline drawing only.
        '''
        ''' Local-coords construction:
        '''   rectangle (0,0)-(width, H_pp), width = (OD-ID)/2
        '''   Arc 2 seed: three points (width/3, 2H/3), (width/2, H), (2width/3, 2H/3)
        '''   but with user-specified R2 and Cx center.
        '''   Arc 1 tangent arc from (0,0) to Arc 2 start, tangent to x=0 vertical.
        '''   Arc 3 tangent arc from (width,0) to Arc 2 end, tangent to x=width vertical.
        ''' </summary>
        Public Function GetBulletArcs() As List(Of GeneralArc)
            Dim arcs As New List(Of GeneralArc)
            Dim w As Double = BulletWidth
            Dim H As Double = H_pp
            Dim R2 As Double = BulletR_Top
            Dim Cx As Double = GetBulletCx()
            If w <= 0 OrElse H <= 0 OrElse R2 <= 0 Then Return arcs

            ' Arc 2: center at (Cx, H - R2), radius R2. Apex at (Cx, H).
            ' T1 and T2 are where Arc 2 meets Arcs 1 and 3. We pick T1 and T2 at
            ' the left and right extremes of Arc 2 at height H - R2 (i.e., the
            ' equator of Arc 2). This gives vertical tangents at T1/T2 which is
            ' not exactly what SW produces but is a reasonable approximation.
            Dim C2r As Double = Cx
            Dim C2z As Double = H - R2
            If C2z < 0 Then C2z = 0  ' clamp if R2 > H

            ' For a reasonable 3-arc look, place T1 at (Cx - R2*sin(t), C2z + R2*cos(t))
            ' where t is chosen so Arc 1 tangent at (0,0) (vertical) is consistent.
            ' Use seed points from user spec: Arc 2 starts roughly at (w/3, 2H/3).
            Dim seedT1r As Double = w / 3.0
            Dim seedT1z As Double = 2.0 * H / 3.0
            Dim seedT2r As Double = 2.0 * w / 3.0
            Dim seedT2z As Double = 2.0 * H / 3.0

            ' Project seed points onto the circle of Arc 2 (C2r, C2z, R2)
            Dim v1r As Double = seedT1r - C2r
            Dim v1z As Double = seedT1z - C2z
            Dim l1 As Double = Math.Sqrt(v1r * v1r + v1z * v1z)
            If l1 < 0.0001 Then l1 = 1.0
            Dim T1r As Double = C2r + R2 * v1r / l1
            Dim T1z As Double = C2z + R2 * v1z / l1

            Dim v2r As Double = seedT2r - C2r
            Dim v2z As Double = seedT2z - C2z
            Dim l2 As Double = Math.Sqrt(v2r * v2r + v2z * v2z)
            If l2 < 0.0001 Then l2 = 1.0
            Dim T2r As Double = C2r + R2 * v2r / l2
            Dim T2z As Double = C2z + R2 * v2z / l2

            ' Arc 1: tangent to vertical at (0,0), passes through T1.
            ' Center at (R1, z1) on perpendicular to vertical (horizontal) through tangent point.
            ' Simplest placement: tangent point at (0, 0) -> center at (R1, 0).
            ' Then (T1r - R1)^2 + T1z^2 = R1^2 -> R1 = (T1r^2 + T1z^2) / (2*T1r)
            Dim R1 As Double = 0
            If T1r > 0.0001 Then R1 = (T1r * T1r + T1z * T1z) / (2.0 * T1r)
            Dim C1r As Double = R1
            Dim C1z As Double = 0.0

            ' Arc 3: tangent to vertical at (w, 0), passes through T2.
            ' Center at (w - R3, 0). (T2r - (w - R3))^2 + T2z^2 = R3^2
            ' (w - T2r)^2 - 2R3(w - T2r) + R3^2 + T2z^2 = R3^2
            ' R3 = ((w - T2r)^2 + T2z^2) / (2(w - T2r))
            Dim R3 As Double = 0
            Dim dR As Double = w - T2r
            If dR > 0.0001 Then R3 = (dR * dR + T2z * T2z) / (2.0 * dR)
            Dim C3r As Double = w - R3
            Dim C3z As Double = 0.0

            Dim d As Double = If(FirstRollUp, 1.0, -1.0)
            Dim rOff As Double = R_inner  ' local x=0 maps to R_inner in absolute r

            Dim a1 As New GeneralArc()
            a1.Rc = rOff + C1r : a1.Zc = d * C1z : a1.R_arc = R1
            a1.Rs = rOff : a1.Zs = 0
            a1.Re = rOff + T1r : a1.Ze = d * T1z
            a1.Direction = ArcDirection(a1)
            arcs.Add(a1)

            Dim a2 As New GeneralArc()
            a2.Rc = rOff + C2r : a2.Zc = d * C2z : a2.R_arc = R2
            a2.Rs = rOff + T1r : a2.Zs = d * T1z
            a2.Re = rOff + T2r : a2.Ze = d * T2z
            a2.Direction = ArcDirection(a2)
            arcs.Add(a2)

            Dim a3 As New GeneralArc()
            a3.Rc = rOff + C3r : a3.Zc = d * C3z : a3.R_arc = R3
            a3.Rs = rOff + T2r : a3.Zs = d * T2z
            a3.Re = rOff + w : a3.Ze = 0
            a3.Direction = ArcDirection(a3)
            arcs.Add(a3)

            Return arcs
        End Function

        Private Shared Function ArcDirection(ByRef ga As GeneralArc) As Integer
            Dim cross As Double = (ga.Rs - ga.Rc) * (ga.Ze - ga.Zc) - _
                                   (ga.Zs - ga.Zc) * (ga.Re - ga.Rc)
            Return If(cross >= 0, 1, -1)
        End Function

        ''' <summary>
        ''' Compute midpoint on a circular arc (short arc path).
        ''' Returns the arc span in degrees.
        ''' </summary>
        Public Shared Function ArcMidpoint(cx As Double, cz As Double, r As Double, _
                                             sx As Double, sz As Double, _
                                             ex As Double, ez As Double, _
                                             ByRef midR As Double, ByRef midZ As Double) As Double
            Dim angS As Double = Math.Atan2(sz - cz, sx - cx)
            Dim angE As Double = Math.Atan2(ez - cz, ex - cx)
            Dim angDiff As Double = angE - angS
            If angDiff > Math.PI Then angDiff -= 2.0 * Math.PI
            If angDiff < -Math.PI Then angDiff += 2.0 * Math.PI
            Dim angMid As Double = angS + angDiff / 2.0
            midR = cx + r * Math.Cos(angMid)
            midZ = cz + r * Math.Sin(angMid)
            Return Math.Abs(angDiff) * 180.0 / Math.PI
        End Function

        Public Function GeneratePoints_Bullet(Optional ByVal ptsPerArc As Integer = 40) As List(Of ProfilePoint)
            ' Bullet uses absolute coords r = R_inner + local_x, where local_x spans [0, width].
            ' Inner lip: (R_inner - InnerLipWidth, 0) -> (R_inner, 0)
            ' Bullet:    three arcs from (R_inner, 0) up and over to (R_inner + width, 0)
            ' Outer lip: (R_inner + width, 0) -> (R_outer + LipWidth, 0)
            Dim pts As New List(Of ProfilePoint)
            Dim w As Double = BulletWidth
            Dim bulletEndR As Double = R_inner + w

            ' Inner lip
            pts.Add(New ProfilePoint(R_inner - InnerLipWidth - 5.0, 0.0))
            pts.Add(New ProfilePoint(R_inner, 0.0))

            Dim bulletArcs As List(Of GeneralArc) = GetBulletArcs()
            If bulletArcs.Count = 0 Then
                pts.Add(New ProfilePoint(bulletEndR, 0.0))
                pts.Add(New ProfilePoint(bulletEndR + LipWidth + 5.0, 0.0))
                Return pts
            End If

            For Each ga As GeneralArc In bulletArcs
                Dim aStart As Double = Math.Atan2(ga.Zs - ga.Zc, ga.Rs - ga.Rc)
                Dim aEnd As Double = Math.Atan2(ga.Ze - ga.Zc, ga.Re - ga.Rc)
                If ga.Direction > 0 Then
                    If aEnd < aStart Then aEnd += 2.0 * Math.PI
                Else
                    If aEnd > aStart Then aEnd -= 2.0 * Math.PI
                End If
                For k As Integer = 1 To ptsPerArc
                    Dim t As Double = CDbl(k) / CDbl(ptsPerArc)
                    Dim a As Double = aStart + (aEnd - aStart) * t
                    pts.Add(New ProfilePoint(ga.Rc + ga.R_arc * Math.Cos(a), _
                                             ga.Zc + ga.R_arc * Math.Sin(a)))
                Next
            Next

            ' Outer lip
            pts.Add(New ProfilePoint(bulletEndR + LipWidth + 5.0, 0.0))
            Return pts
        End Function

        ' ══════════════════════════════════════════════════════════════
        ' Sinusoidal (ProfileType=0) — per-roll construction
        '
        ' Now uses ComputeRollMetrics() for per-roll pitch and H_eff.
        ' When TaperPct=0 and VariablePitch=False: bit-for-bit identical
        ' to the previous single-loop implementation.
        '
        ' Per-roll wave: Z_n(r) = dir_n * H_eff_n * sin(pi * (r - rs_n) / pitch_n)
        ' Matches handoff §4.6.
        ' ══════════════════════════════════════════════════════════════
        Public Function GeneratePoints_Sinusoidal(Optional ByVal pointsPerRoll As Integer = 30) As List(Of ProfilePoint)
            Dim pts As New List(Of ProfilePoint)
            Dim dir0 As Double = If(FirstRollUp, 1.0, -1.0)
            Dim rm As RollMetrics = ComputeRollMetrics()

            pts.Add(New ProfilePoint(R_inner, 0.0))

            If FilletR > 0.01 Then
                pts.Add(New ProfilePoint(R_roll_start, 0.0))
            End If

            For roll As Integer = 0 To N - 1
                Dim d As Double = dir0 * If(roll Mod 2 = 0, 1.0, -1.0)
                Dim rs As Double = rm.RollStart(roll)
                Dim p As Double = rm.Pitch(roll)
                Dim h As Double = rm.H_eff_roll(roll)

                ' Points within this roll (k=1 to pointsPerRoll-1)
                For k As Integer = 1 To pointsPerRoll - 1
                    Dim t As Double = CDbl(k) / CDbl(pointsPerRoll)
                    Dim rv As Double = rs + p * t
                    Dim z As Double = d * h * Math.Sin(Math.PI * t)
                    pts.Add(New ProfilePoint(rv, z))
                Next

                ' Zero-crossing at end of this roll (shared with start of next)
                If roll < N - 1 Then
                    pts.Add(New ProfilePoint(rs + p, 0.0))
                End If
            Next

            pts.Add(New ProfilePoint(R_rolls_outer, 0.0))
            pts.Add(New ProfilePoint(R_outer, 0.0))

            Return pts
        End Function

        ' ══════════════════════════════════════════════════════════════
        ' ArcLines (ProfileType=2) — per-roll construction
        '
        ' Now uses ComputeRollMetrics() for per-roll pitch and H_eff.
        ' When UseNaturalH=True: uses H_n_natural from pitch and angle,
        '   no hScale vertical stretching. Preserves true arc geometry.
        ' When UseNaturalH=False: uses H_eff_n with hScale as before.
        ' When TaperPct=0 and VariablePitch=False and UseNaturalH=False:
        '   bit-for-bit identical to the previous implementation.
        ' ══════════════════════════════════════════════════════════════
        Public Function GeneratePoints_ArcLines(Optional ByVal ptsPerRoll As Integer = 20) As List(Of ProfilePoint)
            Dim pts As New List(Of ProfilePoint)
            Dim PI As Double = Math.PI
            Dim rm As RollMetrics = ComputeRollMetrics()

            Dim theta As Double = Math.Max(0.1, Math.Min(89.9, ConnectorAngle)) * PI / 180.0
            Dim sth As Double = Math.Sin(theta)
            Dim cth As Double = Math.Cos(theta)

            pts.Add(New ProfilePoint(R_inner, 0.0))

            For roll As Integer = 0 To N - 1
                Dim rollUp As Boolean = If(roll Mod 2 = 0, FirstRollUp, Not FirstRollUp)
                Dim d      As Double  = If(rollUp, 1.0, -1.0)
                Dim rs     As Double  = rm.RollStart(roll)
                Dim pitch  As Double  = rm.Pitch(roll)
                Dim h      As Double  = rm.H_eff_roll(roll)  ' target H for this roll

                ' Compute per-roll arc geometry from this roll's pitch
                Dim s     As Double = Math.Max(0.0, Math.Min(StraightLength, pitch * 0.48))
                Dim r_j   As Double = (s / 2.0) * cth
                Dim z_j   As Double = (s / 2.0) * sth
                Dim wa    As Double = pitch - 2.0 * r_j
                Dim a_half As Double = wa / 2.0

                Dim R_arc As Double = If(wa > 0.001 AndAlso sth > 0.001, a_half / sth, h)
                Dim z_c   As Double = z_j - a_half * cth / Math.Max(sth, 0.001)

                ' Compute natural H and hScale for this roll
                Dim H_geom As Double
                If wa <= 0.001 Then
                    H_geom = h
                Else
                    H_geom = z_j + a_half * (1.0 - cth) / Math.Max(sth, 0.001)
                End If

                ' When UseNaturalH=True, h is already H_natural (set by ComputeRollMetrics),
                ' so hScale = H_natural / H_geom = 1.0 (since H_geom IS H_natural).
                ' When UseNaturalH=False, h is H_eff_n (from taper), hScale stretches to match.
                Dim hScale As Double = If(H_geom > 0.001, h / H_geom, 1.0)

                For k As Integer = 0 To ptsPerRoll
                    Dim r_loc As Double = pitch * CDbl(k) / CDbl(ptsPerRoll)
                    Dim z_abs As Double

                    If wa <= 0.001 Then
                        Dim t As Double = r_loc / pitch
                        z_abs = h * (1.0 - 2.0 * Math.Abs(t - 0.5))
                    ElseIf r_loc <= r_j Then
                        z_abs = z_j * (r_loc / Math.Max(0.001, r_j))
                    ElseIf r_loc <= r_j + wa Then
                        Dim r_arc_loc As Double = r_loc - r_j
                        Dim disc      As Double = R_arc * R_arc - (r_arc_loc - a_half) * (r_arc_loc - a_half)
                        If disc < 0 Then disc = 0
                        z_abs = z_c + Math.Sqrt(disc)
                    Else
                        Dim r_fall As Double = r_loc - r_j - wa
                        z_abs = z_j * (1.0 - r_fall / Math.Max(0.001, r_j))
                    End If

                    pts.Add(New ProfilePoint(rs + r_loc, d * z_abs * hScale))
                Next
            Next

            pts.Add(New ProfilePoint(R_rolls_outer, 0.0))
            pts.Add(New ProfilePoint(R_rolls_outer + LipWidth + 5.0, 0.0))
            Return pts
        End Function

        ' ══════════════════════════════════════════════════════════════
        ' SineLines (ProfileType=3) — per-roll construction
        '
        ' Same per-roll pattern as ArcLines. Uses ComputeRollMetrics().
        ' UseNaturalH works the same way.
        ' ══════════════════════════════════════════════════════════════
        Public Function GeneratePoints_SineLines(Optional ByVal ptsPerRoll As Integer = 20) As List(Of ProfilePoint)
            Dim pts As New List(Of ProfilePoint)
            Dim PI As Double = Math.PI
            Dim rm As RollMetrics = ComputeRollMetrics()

            Dim theta As Double = Math.Max(0.1, Math.Min(89.9, ConnectorAngle)) * PI / 180.0
            Dim sth As Double = Math.Sin(theta)
            Dim cth As Double = Math.Cos(theta)

            pts.Add(New ProfilePoint(R_inner, 0.0))

            For roll As Integer = 0 To N - 1
                Dim rollUp As Boolean = If(roll Mod 2 = 0, FirstRollUp, Not FirstRollUp)
                Dim d      As Double  = If(rollUp, 1.0, -1.0)
                Dim rs     As Double  = rm.RollStart(roll)
                Dim pitch  As Double  = rm.Pitch(roll)
                Dim h      As Double  = rm.H_eff_roll(roll)

                Dim s     As Double = Math.Max(0.0, Math.Min(StraightLength, pitch * 0.48))
                Dim r_j   As Double = (s / 2.0) * cth
                Dim z_j   As Double = (s / 2.0) * sth
                Dim wa    As Double = pitch - 2.0 * r_j
                Dim amp   As Double = If(wa > 0.001, Math.Tan(theta) * wa / PI, h)

                Dim H_geom As Double = If(wa > 0.001, z_j + amp, h)
                Dim hScale As Double = If(H_geom > 0.001, h / H_geom, 1.0)

                For k As Integer = 0 To ptsPerRoll
                    Dim r_loc As Double = pitch * CDbl(k) / CDbl(ptsPerRoll)
                    Dim z_abs As Double

                    If wa <= 0.001 Then
                        Dim t As Double = r_loc / pitch
                        z_abs = h * (1.0 - 2.0 * Math.Abs(t - 0.5))
                    ElseIf r_loc <= r_j Then
                        z_abs = z_j * (r_loc / Math.Max(0.001, r_j))
                    ElseIf r_loc <= r_j + wa Then
                        z_abs = z_j + amp * Math.Sin(PI * (r_loc - r_j) / wa)
                    Else
                        Dim r_fall As Double = r_loc - r_j - wa
                        z_abs = z_j * (1.0 - r_fall / Math.Max(0.001, r_j))
                    End If

                    pts.Add(New ProfilePoint(rs + r_loc, d * z_abs * hScale))
                Next
            Next

            pts.Add(New ProfilePoint(R_rolls_outer, 0.0))
            pts.Add(New ProfilePoint(R_rolls_outer + LipWidth + 5.0, 0.0))
            Return pts
        End Function

        ''' <summary>
        ''' Returns typed sketch segments for CreatePart.
        ''' Straight sections -> IsLine=True. Arc sections -> IsLine=False (spline).
        ''' For continuous profiles (Sin, Arc, COMSOL) returns one spline segment.
        ''' For ArcLines/SineLines returns alternating line+spline segments.
        ''' Arc-based edge profiles (HalfRoll, DoubleRoll) are handled by CreateArc
        ''' in SWAutomation, not by this method.
        ''' </summary>
        Public Function GetSketchSegments(Optional ptsPerRoll As Integer = 30) As List(Of ProfileSegment)
            Dim segs As New List(Of ProfileSegment)

            If ProfileType = 2 OrElse ProfileType = 3 Then
                ' ArcLines or SineLines: alternate line segments and arc splines
                ' Now uses per-roll geometry from ComputeRollMetrics
                Dim rm As RollMetrics = ComputeRollMetrics()
                Dim PI As Double = Math.PI
                Dim theta As Double = Math.Max(0.1, Math.Min(89.9, ConnectorAngle)) * PI / 180.0
                Dim sth As Double = Math.Sin(theta)
                Dim cth As Double = Math.Cos(theta)
                Dim ptsPerArc As Integer = Math.Max(6, ptsPerRoll \ 2)

                ' Lead-in line from R_inner to first arc start
                Dim s0 As Double = Math.Max(0.0, Math.Min(StraightLength, rm.Pitch(0) * 0.48))
                Dim straightFrac0 As Double = Math.Max(0.0, Math.Min(0.95, ConnectorAngle / 90.0))
                Dim straightHalf0 As Double = (rm.Pitch(0) / 2.0) * straightFrac0
                Dim arcHalf0 As Double = (rm.Pitch(0) / 2.0) - straightHalf0

                Dim firstArcStartR As Double = rm.RollStart(0) + straightHalf0

                Dim linePts As New List(Of ProfilePoint)
                linePts.Add(New ProfilePoint(R_inner, 0.0))
                linePts.Add(New ProfilePoint(firstArcStartR, 0.0))
                segs.Add(New ProfileSegment(True, linePts))

                For roll As Integer = 0 To N - 1
                    Dim rollUp As Boolean = If(roll Mod 2 = 0, FirstRollUp, Not FirstRollUp)
                    Dim d As Double = If(rollUp, 1.0, -1.0)
                    Dim pitch As Double = rm.Pitch(roll)
                    Dim h As Double = rm.H_eff_roll(roll)
                    Dim rs As Double = rm.RollStart(roll)

                    Dim straightFrac As Double = Math.Max(0.0, Math.Min(0.95, ConnectorAngle / 90.0))
                    Dim straightHalf As Double = (pitch / 2.0) * straightFrac
                    Dim arcHalf As Double = (pitch / 2.0) - straightHalf

                    Dim Rc As Double = rs + pitch / 2.0

                    ' Compute arc geometry for this roll
                    Dim R_arc As Double = If(arcHalf > 0.001, arcHalf*arcHalf/(2.0*h) + h/2.0, h)
                    Dim halfAngle As Double = Math.Asin(Math.Min(1.0, arcHalf/R_arc))

                    Dim Zc As Double = -d * (R_arc - h)

                    Dim arcPts As New List(Of ProfilePoint)
                    If ProfileType = 2 Then
                        For k As Integer = 0 To ptsPerArc
                            Dim alpha As Double = -halfAngle + 2.0*halfAngle*CDbl(k)/CDbl(ptsPerArc)
                            arcPts.Add(New ProfilePoint(Rc + R_arc*Math.Sin(alpha), Zc + d*R_arc*Math.Cos(alpha)))
                        Next
                    Else  ' SineLines
                        Dim phaseFrac As Double = 1.0 - straightFrac
                        Dim phaseStart As Double = PI/2.0 - PI/2.0*phaseFrac
                        Dim phaseEnd As Double = PI/2.0 + PI/2.0*phaseFrac
                        For k As Integer = 0 To ptsPerArc
                            Dim t As Double = CDbl(k)/CDbl(ptsPerArc)
                            Dim rv As Double = (Rc - arcHalf) + 2.0*arcHalf*t
                            Dim zv As Double = d*h*Math.Sin(phaseStart + (phaseEnd-phaseStart)*t)
                            arcPts.Add(New ProfilePoint(rv, zv))
                        Next
                    End If
                    segs.Add(New ProfileSegment(False, arcPts))

                    ' Line from end of this arc to start of next (or to R_rolls_outer)
                    Dim lineEndR As Double
                    If roll < N - 1 Then
                        Dim nextStraightFrac As Double = Math.Max(0.0, Math.Min(0.95, ConnectorAngle / 90.0))
                        Dim nextStraightHalf As Double = (rm.Pitch(roll + 1) / 2.0) * nextStraightFrac
                        Dim nextArcHalf As Double = (rm.Pitch(roll + 1) / 2.0) - nextStraightHalf
                        lineEndR = rm.RollStart(roll + 1) + nextStraightHalf
                    Else
                        lineEndR = R_rolls_outer
                    End If

                    Dim lp As New List(Of ProfilePoint)
                    lp.Add(New ProfilePoint(Rc + arcHalf, 0.0))
                    lp.Add(New ProfilePoint(lineEndR, 0.0))
                    segs.Add(New ProfileSegment(True, lp))
                Next

                Dim lipPts As New List(Of ProfilePoint)
                lipPts.Add(New ProfilePoint(R_rolls_outer, 0.0))
                lipPts.Add(New ProfilePoint(R_rolls_outer + LipWidth + 5.0, 0.0))
                segs.Add(New ProfileSegment(True, lipPts))
            Else
                ' Continuous profile (Sin, Arc, COMSOL): one spline + one lip line
                ' (HalfRoll/DoubleRoll are drawn with CreateArc, not via this path)
                Dim pts As List(Of ProfilePoint) = GeneratePoints(30)
                Dim splinePts As New List(Of ProfilePoint)
                For i As Integer = 0 To pts.Count - 2
                    splinePts.Add(pts(i))
                Next
                segs.Add(New ProfileSegment(False, splinePts))
                Dim lp As New List(Of ProfilePoint)
                lp.Add(pts(pts.Count - 2))
                lp.Add(pts(pts.Count - 1))
                segs.Add(New ProfileSegment(True, lp))
            End If

            Return segs
        End Function

        ''' <summary>
        ''' Returns arc roll descriptors for any arc-based profile type.
        ''' Used by SWAutomation to draw with CreateArc.
        ''' </summary>
        Public Function GetArcRolls() As List(Of CircularArcRoll)
            Select Case ProfileType
                Case 1 : Return GetCircularArcRolls()
                Case 10 : Return GetHalfRollArc()
                Case 11 : Return GetDoubleRollArcs()
                Case Else : Return Nothing
            End Select
        End Function

        ''' <summary>
        ''' True if this profile type uses CreateArc entities in SW instead of splines.
        ''' </summary>
        Public ReadOnly Property UsesArcEntities As Boolean
            Get
                Return ProfileType = 1 OrElse ProfileType = 10 OrElse ProfileType = 11
            End Get
        End Property

        Public Function GetRollCrestRadii() As List(Of Double)
            If ProfileType = 99 Then
                Dim crests As New List(Of Double)()
                For i As Integer = 1 To 7
                    crests.Add(_Rc(i))
                Next
                Return crests
            End If

            If ProfileType = 10 Then
                ' HalfRoll: one crest at midpoint
                Dim crests As New List(Of Double)()
                crests.Add(R_roll_start + (R_rolls_outer - R_roll_start) / 2.0)
                Return crests
            End If

            If ProfileType = 11 Then
                ' DoubleRoll: crest of each roll
                Dim crests As New List(Of Double)()
                Dim span As Double = R_rolls_outer - R_roll_start
                Dim flat As Double = Math.Max(0.0, Math.Min(CenterFlat, span * 0.8))
                Dim roll_pitch As Double = (span - flat) / 2.0
                crests.Add(R_roll_start + roll_pitch / 2.0)
                crests.Add(R_roll_start + roll_pitch + flat + roll_pitch / 2.0)
                Return crests
            End If

            If ProfileType = 12 Then
                ' Bullet: crest at Arc 2 center = R_inner + Cx
                Dim crests As New List(Of Double)()
                crests.Add(R_inner + GetBulletCx())
                Return crests
            End If

            ' Spider profiles with per-roll pitch
            Dim rm As RollMetrics = ComputeRollMetrics()
            Dim crestsS As New List(Of Double)
            For i As Integer = 0 To N - 1
                crestsS.Add(rm.RollStart(i) + rm.Pitch(i) / 2.0)
            Next
            Return crestsS
        End Function

        Public Function Summary() As String
            Dim sb As New StringBuilder()
            Dim modeStr As String = If(ComponentMode = 1, "Edge", "Spider")
            sb.AppendLine(String.Format("=== {0} Profile Summary [STAGE1-v5] ===", modeStr))
            Dim profileName As String
            Select Case ProfileType
                Case 1 : profileName = "Circular Arc"
                Case 2 : profileName = "Arc+Lines"
                Case 3 : profileName = "Sine+Lines"
                Case 10 : profileName = "Half Roll"
                Case 11 : profileName = "Double Roll"
                Case 12 : profileName = "Bullet"
                Case 99 : profileName = "COMSOL Arc+Lines (STEP)"
                Case Else : profileName = "Sinusoidal"
            End Select
            sb.AppendLine(String.Format("Profile: {0}", profileName))
            If ComponentMode = 1 Then
                sb.AppendLine(String.Format("Cone OD={0:F1}  Basket ID={1:F1}  N={2}  T={3:F2}", ID, OD, N, T))
            Else
                sb.AppendLine(String.Format("ID={0:F1}  OD={1:F1}  N={2}  T={3:F2}", ID, OD, N, T))
            End If
            sb.AppendLine(String.Format("H_pp={0:F2}  H_eff={1:F2}  MaxH_pp={2:F2}", H_pp, H_eff, MaxH_pp))
            sb.AppendLine(String.Format("R_inner={0:F2}  R_roll_start={1:F2}", R_inner, R_roll_start))
            sb.AppendLine(String.Format("R_rolls_outer={0:F2}  R_outer={1:F2}", R_rolls_outer, R_outer))
            sb.AppendLine(String.Format("EffPitch={0:F3}  AspectRatio={1:F3}", EffPitchPerRoll, AspectRatio))
            sb.AppendLine(String.Format("LipWidth={0:F1}  FilletR={1:F2}", LipWidth, FilletR))
            sb.AppendLine(String.Format("E={0}  Nu={1}  FirstRollUp={2}", E, Nu, FirstRollUp))
            If ProfileType = 2 OrElse ProfileType = 3 Then
                sb.AppendLine(String.Format("StraightLength={0:F2}mm  ConnectorAngle={1:F1}deg", StraightLength, ConnectorAngle))
            End If
            If ProfileType = 11 Then
                sb.AppendLine(String.Format("CenterFlat={0:F2}mm", CenterFlat))
            End If
            If ProfileType = 12 Then
                sb.AppendLine(String.Format("Bullet: width={0:F2}mm  H_pp={1:F2}mm  R2={2:F2}mm  Cx={3:F2}mm  InnerLip={4:F2}mm", _
                    BulletWidth, H_pp, BulletR_Top, GetBulletCx(), InnerLipWidth))
            End If

            ' FEA calibration fields (show when non-default)
            If TaperPct <> 0 OrElse VariablePitch OrElse UseNaturalH Then
                sb.AppendLine(String.Format("TaperPct={0:F1}  VariablePitch={1}  PitchTaperPct={2:F1}  UseNaturalH={3}", _
                    TaperPct, VariablePitch, PitchTaperPct, UseNaturalH))
            End If

            ' Roll crests
            Dim crests As List(Of Double) = GetRollCrestRadii()
            sb.Append("Roll crests (r): ")
            For i As Integer = 0 To crests.Count - 1
                If i > 0 Then sb.Append(", ")
                sb.Append(String.Format("{0:F2}", crests(i)))
            Next
            sb.AppendLine()

            ' DIAGNOSTIC: always dump ComputeRollMetrics for debugging
            If N > 0 Then
                Dim rmDbg As RollMetrics = ComputeRollMetrics()
                sb.AppendLine(String.Format("DIAG: rmDbg.RollCount={0}  rmDbg.Pitch is Nothing={1}", _
                    rmDbg.RollCount, rmDbg.Pitch Is Nothing))
                sb.AppendLine(String.Format("DIAG CRM: {0}", _diagCRM))
                If rmDbg.Pitch IsNot Nothing AndAlso rmDbg.Pitch.Length > 0 Then
                    sb.Append("DIAG pitch: ")
                    For i As Integer = 0 To Math.Min(N - 1, rmDbg.Pitch.Length - 1)
                        If i > 0 Then sb.Append(", ")
                        sb.Append(String.Format("{0:F4}", rmDbg.Pitch(i)))
                    Next
                    sb.AppendLine()
                    sb.Append("DIAG H_eff: ")
                    For i As Integer = 0 To Math.Min(N - 1, rmDbg.H_eff_roll.Length - 1)
                        If i > 0 Then sb.Append(", ")
                        sb.Append(String.Format("{0:F4}", rmDbg.H_eff_roll(i)))
                    Next
                    sb.AppendLine()
                    sb.Append("DIAG starts: ")
                    For i As Integer = 0 To Math.Min(N - 1, rmDbg.RollStart.Length - 1)
                        If i > 0 Then sb.Append(", ")
                        sb.Append(String.Format("{0:F4}", rmDbg.RollStart(i)))
                    Next
                    sb.AppendLine()
                End If
            End If

            ' Per-roll detail (show when taper, variable pitch, or natural H active)
            If (TaperPct <> 0 OrElse VariablePitch OrElse UseNaturalH) AndAlso N > 0 Then
                Dim rm As RollMetrics = ComputeRollMetrics()
                sb.Append("Per-roll pitch: ")
                For i As Integer = 0 To N - 1
                    If i > 0 Then sb.Append(", ")
                    sb.Append(String.Format("{0:F3}", rm.Pitch(i)))
                Next
                sb.AppendLine()
                sb.Append("Per-roll H_eff: ")
                For i As Integer = 0 To N - 1
                    If i > 0 Then sb.Append(", ")
                    sb.Append(String.Format("{0:F3}", rm.H_eff_roll(i)))
                Next
                sb.AppendLine()
                If (ProfileType = 2 OrElse ProfileType = 3) Then
                    sb.Append("Per-roll H_natural: ")
                    For i As Integer = 0 To N - 1
                        If i > 0 Then sb.Append(", ")
                        sb.Append(String.Format("{0:F3}", rm.H_natural(i)))
                    Next
                    sb.AppendLine()
                End If
            End If

            Return sb.ToString()
        End Function

    End Class
