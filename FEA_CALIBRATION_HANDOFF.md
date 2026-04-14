# SpiderSWRunner FEA Calibration Campaign — Handoff

**Target workstation:** secondary (VS2010, .NET 4.0, SW2014 + Simulation Premium)
**Target tool:** `C:\Projects\SpiderSWRunner\`
**Primary purpose:** generate FEA data to calibrate a taper correction for the Model A Kms formula in SpeaDWPF and validate the variable-pitch path.
**Secondary purpose:** seed the FEA module planned for SpeaDWPF v-next.

---

## 1. Context & problem

The current Kms formula in `SpeaDWPF.Physics\SpiderGeometry.vb` (Model A, FEA-calibrated) is a **single-H** formula:

```
Kms = K_bend * (E/E_ref) * (T/T_ref)^1.35 * (H/H_ref)^1.04 * fN * cProf
    + K_hoop * (E/E_ref) * (T/T_ref)^1.36 * (H/H_ref)^2.80 * fN * cProf * nu_factor
```

The `H` here is `H_outer_eff` — only the outer roll's half-height. The formula has no input for inner-roll H, no per-roll summation, no taper dimension. It was calibrated against uniform spiders only.

**Consequence 1 — Taper has zero effect on Kms and Xmax.** Taper changes inner-roll H but not outer-roll H, so `h_ratio` is unchanged, and Kms and Xmax don't move. Tested this session on all five profile types — same result everywhere.

**Consequence 2 — Variable pitch reaches Kms indirectly, possibly incompletely.** Variable pitch narrows per-roll pitch → `MaxH_pp` takes the min across rolls → the narrowest roll becomes the binding cap → `H_outer` drops → `h_ratio` drops → Kms drops. This path exists but there's no direct per-roll pitch-stiffness term. Looks about right but unvalidated.

**Consequence 3 — Xmax uses single-H.** `Xmax = H_outer_eff * C_xc`, where `C_xc = 10.8 * cProf^1.33`. Same blindness to taper.

This campaign's job: produce FEA data that lets us fit a taper correction and validate the pitch path.

---

## 2. Approach — Architecture 1 (Equivalent-H correction)

Keep the existing single-H Model A formula and introduce a fitted equivalent-H input:

```
Kms_tapered = Kms_formula(H_eq, T, E, N, profile)
where H_eq = H_eq(H_outer, H_inner, pitch_taper)
```

For uniform spiders, `H_eq = H_outer` by construction, so existing validated values are unchanged. This is a minimal-change fix: same formula, different input. The alternative (rewriting to per-roll summation) is heavier and can wait. If the fitted correction proves inadequate we can escalate later using the same FEA data.

---

## 3. Profile choice — Sinusoidal primary, Arc+Lines secondary

**Sinusoidal** is the primary calibration profile because:

1. SpiderSWRunner already generates sinusoidal spider shells — no new geometry needed.
2. Sinusoidal has no coupling between H and pitch. The wave is `Z(r) = H * sin(pi * (r - r_start) / pitch)` — H is a pure free parameter. Taper and variable pitch map cleanly to per-roll `(H_n, pitch_n)` with no geometric ambiguity.
3. Clean calibration data: any taper effect measured is purely a taper effect, not contaminated by shape distortions.

**Arc+Lines** is the secondary profile, to verify the Sinusoidal-fitted correction generalizes through the existing `cProf` multiplier that Model A already uses per profile. If the Sinusoidal fit + `cProf` predicts the Arc+Lines FEA data within ~5%, the correction is profile-independent and we're done. If not, Arc+Lines gets its own calibration pass — we'll have the data either way.

---

## 4. Precise definitions

This section is the authoritative spec. **SW geometry generation must match these formulas exactly** or the FEA runs won't validate the WPF app.

### 4.1 Coordinate system

- `r` = radial coordinate (mm), increasing outward from spider center
- `z` = axial coordinate (mm), positive toward the cone side
- Axisymmetric about the z-axis. 2D axisymmetric FEA is sufficient.

### 4.2 Global geometry inputs

| Symbol | Meaning | Reference value |
|---|---|---|
| `ID` | Inner diameter (mm) | 67.0 |
| `OD` | Outer diameter (mm) | 172.0 |
| `N` | Total roll count | 7 |
| `T` | Shell thickness (mm) | 0.8 |
| `E` | Young's modulus (N/mm^2) | 6.1 |
| `nu` | Poisson ratio | 0.49 |
| `LipWidth` | OD attachment lip width (mm) | 5.0 |
| `NeckType` | 0 = Neck Up | 0 |
| `FirstRollUp` | First roll direction | True |
| `LipNeckPos` | 0 = Centred at lip | 0 |

### 4.3 Derived geometry (matches `Calculate` in SpiderGeometry.vb)

```
R_inner       = ID / 2                                 = 33.5 mm
R_outer       = OD / 2                                 = 86.0 mm
R_rolls_outer = max(R_inner + 1, R_outer - LipWidth)   = 81.0 mm
FilletR       = 2 * T   (NeckType=0 AND FirstRollUp=True, else 0)  = 1.6 mm
R_roll_start  = R_inner + FilletR                      = 35.1 mm
W_corr        = R_rolls_outer - R_roll_start           = 45.9 mm
EffPitch      = W_corr / N                             = 6.5571 mm
```

### 4.4 Taper definition — precise

Taper is applied to **H (wave half-height)**, not to pitch.

```
taper_frac    = clamp(TaperPct / 100, 0, 1)
hScale_inner  = max(0.10, 1 - taper_frac)      [10% FLOOR — not zero]
hScale_outer  = 1.0
```

For each roll `n` in `[0, N-1]`:

```
t_n       = n / (N - 1)                        [0 at inner, 1 at outer]
hScale_n  = hScale_inner + (hScale_outer - hScale_inner) * t_n
H_eff_n   = H_outer_eff * hScale_n             [half-height at roll n]
H_pp_n    = 2 * H_eff_n                        [peak-to-peak at roll n]
```

**The 10% floor matters.** At `TaperPct = 100` the inner roll is NOT zero height — it's 10% of outer. Do not let the SW parametric zero out the inner roll.

Worked example at `H_outer_eff = 3.175 mm`, `N = 7`, `TaperPct = 50`:

| n | t_n   | hScale_n | H_eff_n (mm) |
|---|-------|----------|--------------|
| 0 (inner) | 0.000 | 0.500 | 1.588 |
| 1 | 0.167 | 0.583 | 1.852 |
| 2 | 0.333 | 0.667 | 2.117 |
| 3 | 0.500 | 0.750 | 2.381 |
| 4 | 0.667 | 0.833 | 2.646 |
| 5 | 0.833 | 0.917 | 2.910 |
| 6 (outer) | 1.000 | 1.000 | 3.175 |

### 4.5 Variable pitch definition

```
pitchTaperFrac  = VariablePitch ? (PitchTaperPct / 100) : 0
maxDelta        = EffPitch * 0.90
delta           = clamp(EffPitch * pitchTaperFrac, -maxDelta, +maxDelta)
pitch_id_raw    = EffPitch + delta
pitch_od_raw    = EffPitch - delta
```

So **positive** `PitchTaperPct` -> inner pitch WIDER, outer NARROWER. Negative -> reversed.

For each roll `n` in `[0, N-1]`:

```
t_n          = n / (N - 1)
pitch_n_raw  = pitch_id_raw + (pitch_od_raw - pitch_id_raw) * t_n
```

Then normalize so the pitches sum to `W_corr` exactly:

```
pitchSum  = sum(pitch_n_raw, n=0..N-1)
scale     = W_corr / pitchSum
pitch_n   = pitch_n_raw * scale               [final per-roll pitch]
```

Roll start radii:

```
RollStart_0   = R_roll_start
RollStart_n   = RollStart_{n-1} + pitch_{n-1}    for n > 0
R_mean_n      = RollStart_n + pitch_n / 2        [roll centerline radius]
```

### 4.6 Per-roll wave shape — Sinusoidal

For roll `n` with start radius `RollStart_n`, pitch `pitch_n`, half-height `H_eff_n`:

```
Z_n(r) = dir_n * H_eff_n * sin(pi * (r - RollStart_n) / pitch_n)
```

where `r` ranges over `[RollStart_n, RollStart_n + pitch_n]`.

`dir_0 = +1` if `FirstRollUp = True`, else `-1`. Subsequent rolls alternate sign: `dir_{n+1} = -dir_n`.

### 4.7 Per-roll wave shape — Arc + Lines

Use a **single global theta** derived from SafetyPct (not per-roll):

```
theta = 2 * atan(SafetyPct / 100)
```

At `SafetyPct = 84`, `theta ~= 80.07°`. Use `StraightLength s = 1.0 mm` for all runs.

For roll `n` at pitch `pitch_n`, the geometric half-height is:

```
r_j = (s/2) * cos(theta)
z_j = (s/2) * sin(theta)
wa  = pitch_n - 2 * r_j
a   = wa / 2
H_n_natural = z_j + a * (1 - cos(theta)) / sin(theta)
```

**This is a constraint, not a free value.** Arc+Lines at fixed theta and variable pitch produces a variable `H_n_natural` per roll. For Arc+Lines FEA runs with taper, **use `H_n_natural`, not the linearly-tapered `H_eff_n` from §4.4.** Record both values in the run log so we can see the difference.

Profile for roll `n`, local coordinate `r_loc` in `[0, pitch_n]` (0 at left/inner edge of the roll):

1. **Ramp up:** linear from `(0, 0)` to `(r_j, z_j)`, slope `tan(theta)`
2. **Arc:** circular from `(r_j, z_j)` peaking at `(pitch_n/2, H_n_natural)` and down to `(pitch_n - r_j, z_j)`. Arc center is at `z_c = z_j - a * cos(theta) / sin(theta)`, radius `R = a / sin(theta)`.
3. **Ramp down:** linear from `(pitch_n - r_j, z_j)` to `(pitch_n, 0)`, slope `-tan(theta)`

Then apply `dir_n` sign as in §4.6.

---

## 5. Test matrix — 14 runs

Two profiles × 7 runs each. All runs share: reference global geometry (§4.2), `N = 7`, `SafetyPct = 84`, rubber shell (E=6.1, nu=0.49, rho=1000), T=0.8 mm, OD lip fully clamped, ID attached to a rigid disc representing the voice coil former.

| Run | Profile   | TaperPct | PitchTaperPct | Purpose |
|-----|-----------|----------|---------------|---------|
| S1  | Sinusoidal | 0  | 0   | Baseline — must match Model A (Kms ~= 0.053) |
| S2  | Sinusoidal | 25 | 0   | Taper light |
| S3  | Sinusoidal | 50 | 0   | Taper medium |
| S4  | Sinusoidal | 75 | 0   | Taper heavy (inner at 25% of outer) |
| S5  | Sinusoidal | 0  | +20 | Pitch variation, inner wider |
| S6  | Sinusoidal | 0  | -20 | Pitch variation, inner narrower (symmetry check) |
| S7  | Sinusoidal | 50 | +20 | Combined — tests separability |
| A1  | Arc+Lines  | 0  | 0   | Baseline — should match existing COMSOL reference |
| A2  | Arc+Lines  | 25 | 0   | Taper light |
| A3  | Arc+Lines  | 50 | 0   | Taper medium |
| A4  | Arc+Lines  | 75 | 0   | Taper heavy |
| A5  | Arc+Lines  | 0  | +20 | Pitch variation + |
| A6  | Arc+Lines  | 0  | -20 | Pitch variation - |
| A7  | Arc+Lines  | 50 | +20 | Combined |

**Note on Arc+Lines taper runs (A2–A4, A7):** Arc+Lines geometry cannot freely set per-roll H at fixed theta. Use the **natural** `H_n_natural` per roll (which varies with pitch_n), and record the resulting H distribution in the log. We will compare Kms_FEA against what Model A predicts for that exact H distribution.

---

## 6. Measurement protocol per run

### 6.1 Small-signal Kms_0 (primary calibration target)

Apply a small axial displacement `Delta = 0.05 * H_outer_eff` to the voice-coil former attachment. Record the total axial reaction force at the OD clamp. Then:

```
Kms_0_pos = F(+Delta) / (+Delta)
Kms_0_neg = F(-Delta) / (-Delta)
Kms_0     = (F(+Delta) - F(-Delta)) / (2 * Delta)      [signed average]
```

Verify linearity by also running at `Delta_small = 0.02 * H_outer_eff`. The two Kms_0 values should agree within ~1%. If not, you're outside the small-signal regime — reduce Delta further.

### 6.2 Force-displacement curve for Xmax extraction

Sweep displacement in both directions out to `±2 * H_outer_eff`. Sample at least 30 points per side. Record `F(x)` and compute numerical derivative `Kms(x) = dF/dx` (centered differences).

**Klippel XC definition** (from Klippel Application Notes and HTML report documentation — verified this session):

> XC is the displacement at which `Cms(x) = Cmin * Cms(0)`, equivalently `Kms(x) = Kms(0) / Cmin`.

Standard thresholds:
- `Cmin = 0.75` — midbass / mid driver, 10% THD target -> `Kms(x)/Kms(0) = 1.333`
- `Cmin = 0.50` — subwoofer, 20% THD target -> `Kms(x)/Kms(0) = 2.000`

The SpeaDWPF code's current `C_xc = 10.8 * cProf^1.33` formula is commented in the source as "Klippel XC (Cmin=75%)", so the **primary Xmax extraction is at Kms(x)/Kms(0) = 1.333**.

Extract both thresholds from the FEA sweep for flexibility:

```
XC_75_pos = smallest positive x where Kms(x)/Kms(0) >= 1.333
XC_75_neg = smallest-magnitude negative x where Kms(x)/Kms(0) >= 1.333
XC_50_pos = similar, threshold 2.000
XC_50_neg = similar, threshold 2.000
```

For a geometrically symmetric uniform spider, `XC_pos ~= XC_neg`. Any asymmetry is physically meaningful (and expected on tapered runs) — record both.

### 6.3 Per-roll displacement at small x

At `x = 0.25 * H_outer_eff` (still in the small-signal regime), record the **axial displacement of each roll's peak/trough** — the z-coordinate at `r = R_mean_n` for each roll `n` in `[0, N-1]`. Report as the ratio:

```
dz_n / x_applied     for n = 0..6
```

This validates the `DecayRatio()` per-roll participation model at taper and at pitch variation, and will feed the fix for the drawing-side bug (inner rolls not visually shrinking on Arc+Lines and Sine+Lines in the WPF app).

### 6.4 Per-run log format

One summary CSV `SpiderFEA_Matrix.csv`, one row per run, columns:

```
RunID, Profile, TaperPct, PitchTaperPct, H_outer_eff,
H_eff_0, H_eff_1, H_eff_2, H_eff_3, H_eff_4, H_eff_5, H_eff_6,
pitch_0, pitch_1, pitch_2, pitch_3, pitch_4, pitch_5, pitch_6,
Kms_0_pos, Kms_0_neg, Kms_0,
XC_75_pos, XC_75_neg, XC_50_pos, XC_50_neg,
dz_n_over_x_0, dz_n_over_x_1, dz_n_over_x_2, dz_n_over_x_3, dz_n_over_x_4, dz_n_over_x_5, dz_n_over_x_6
```

Per run, a separate F-x dump: `<RunID>_Fx.csv` with columns `x, F, Kms_numerical` at ~30 points per side.

---

## 7. What we do with the data

Once all 14 runs are complete:

1. **Baseline validation (S1, A1).** These should match current Model A Kms within ~5%. If they don't, diagnose before fitting anything.

2. **Fit `H_eq(H_outer, H_inner)` from Sinusoidal taper data (S1–S4).** Candidate forms, in order of simplicity:
   - Arithmetic mean: `H_eq = (H_outer + H_inner) / 2`
   - Outer-weighted: `H_eq = w * H_outer + (1-w) * H_inner`, fit w
   - Functional: `H_eq = H_outer * g(taper_frac)`, fit g

   Pick the simplest form that fits within ~5%.

3. **Fit pitch correction** from S5/S6. Verify ± symmetry. If the existing formula's indirect pitch effect already matches FEA, no new term is needed. Otherwise add a per-roll pitch stiffness term.

4. **Test separability with S7.** If `Kms(S7) ~= Kms_base * f_taper(50) * f_pitch(20)`, effects are separable. If not, an interaction term is needed.

5. **Repeat for Arc+Lines (A1–A7).** Check whether the Sinusoidal fit + existing `cProf` multiplier predicts Arc+Lines. If yes: profile-independent correction. If no: profile-specific corrections.

6. **Recalibrate `C_xc` for taper.** Current: `C_xc = 10.8 * cProf^1.33`. New: `C_xc = 10.8 * cProf^1.33 * h(taper_frac)`, fit `h` from XC_75 vs taper.

7. **Per-roll displacement data -> fix drawing bug.** The FEA data tells us the true per-roll participation profile, which calibrates `DecayRatio()` and drives the fix for the H_eff cancellation in the Arc+Lines / Sine+Lines wave functions.

---

## 8. Open items / watchouts

1. **Rubber material model.** E=6.1 N/mm^2, nu=0.49 is near-incompressible. If SW Simulation Premium has Mooney-Rivlin or Neo-Hookean available, use hyperelastic. Otherwise use linear elastic with `nu = 0.45` to avoid volumetric locking. Record which model was used in each run's notes.

2. **Mesh convergence.** Run S1 at two mesh densities (baseline and ~2x refined). Kms_0 should match within 2%. If not, refine until it does, then use that mesh for all 14 runs. Include the convergence study in deliverables.

3. **Geometric nonlinearity.** The F(x) sweeps extend to ±2*H_outer_eff — well outside linear. Use **nonlinear static** (large displacement = on) throughout, not linear perturbation.

4. **Inner disc stiffness.** The voice-coil former attachment should be rigid (at least 100x stiffer than the spider) so all measured stiffness comes from the spider. If using a flexible disc, subtract its contribution.

5. **OD clamp.** Fully fix all 6 DOF on the outermost edge of the OD lip. The lip attachment zone (from `R_rolls_outer` to `R_outer`) is part of the spider mesh but constrained at the very outer edge.

6. **Sign of taper effect.** Physically, tapering down the inner rolls should **increase** Kms_0 slightly (shorter corrugations are stiffer for same T) and **reduce** XC (less travel available before the inner rolls bottom out). If the FEA shows the opposite, something is wrong with the model setup.

7. **COM quirks on SW2014.** Per `SpiderSWRunner` notes from previous sessions: shell thickness and material cannot be committed programmatically via .NET interop on SW2014 — these require GUI clicks. The existing 3-click convenience workflow still applies. Plan the matrix accordingly so you can batch the parametric part.

---

## 9. Deliverables back to the main workstation

When all 14 runs are done, send back:

- `SpiderFEA_Matrix.csv` — the 14-row summary log
- 14 × `<RunID>_Fx.csv` — the F-x sweep dumps
- Mesh convergence study (S1 coarse vs fine, Kms_0 and XC_75 comparison)
- Notes on any runs that didn't converge or required parameter adjustment
- Screenshots of deformed shape at approximately `x = XC_75` for **S1, S3, A1, A3** — visual sanity check of large-signal behavior
- Brief notes on which material model and element type were used

---

## 10. Notes for the future SpeaDWPF FEA module (v-next)

The geometry spec in §4 is the canonical definition. When the SpeaDWPF internal FEA module is built, it should:

- Use the same `taper_frac`, `pitch_taper`, and per-roll algorithm defined here
- Reproduce the Kms_0 values from this campaign within FEA precision (~2%)
- Reproduce the per-roll displacement ratios from §6.3 within ~5%
- Use the same XC_75 extraction criterion for Xmax

The Triangle.dll + LST axisymmetric + Total Lagrangian solver pipeline (already established in the FEA branch, per earlier session notes) is the target. The 14 runs in this campaign become the first regression test set for that solver.

---

## 11. Summary

- **14 FEA runs** (7 Sinusoidal + 7 Arc+Lines)
- **Per run, measure:** Kms_0, full F(x) curve (Xmax extraction at Cmin=75% and 50%), per-roll displacement ratios at x=0.25*H_outer_eff
- **Output:** fitted `H_eq(H_outer, H_inner)` correction for Kms, recalibrated `C_xc` for Xmax, validated pitch path, per-roll decay model for drawing fix
- **Deliverable:** CSV files back to main workstation for analysis and Model A update

Once the data is in hand, the SpiderGeometry.vb changes required will be small and localized — only the `Kms` computation and `Xmax` line in `Calculate`, plus possibly a new helper for `H_eq`. No profile-dispatch changes, no wave function changes. Rule #1 compliant.

Let me know when SpiderSWRunner modifications are ready to generate the 14 geometries, and I'll walk through any implementation questions on the parametric side.
