# SpiderDesigner FEA Calibration — Handoff & Test Matrix
Date: 2026-04-10

## Purpose

Use SolidWorks FEA (SpiderSWRunner) to collect ground-truth data for calibrating
SpiderDesigner's (future SpeaD) closed-form analytical model. Three calibration targets:

1. **Kms0 per profile type** — fit CProf coefficients so analytical Kms0 matches FEA
2. **Per-roll displacement ratios** — replace the parallel-spring assumption with a
   clamped-shell decay model calibrated against FEA roll ratios
3. **Kms(x) shape** — softening dip, terminal stiffening, x_break per roll

## Project Locations

```
C:\Projects\SpiderSWRunner\SpiderSWRunner\   ← FEA automation (VB.NET, VS2010)
C:\Projects\SpiderDesigner\                  ← analytical model (VB.NET)
```

Key files in SpiderSWRunner:
- SpiderProfile.vb   — profile point generation (all types working)
- SWAutomation.vb    — SW COM automation, CreatePart, SetupStudy, MeshAndRun, ExtractResults
- Form1.vb           — UI, BuildProfile()

Key file in SpiderDesigner:
- SpiderGeometry.vb  — Calculate(), ParallelKms_at_internal(), CProf/CBreak tables, Z_rolls()

## Current Status

### What Works (all profile types confirmed)
- Sinusoidal (ProfileType=0): correct geometry, simulations complete
- CircularArc (ProfileType=1): **FIXED this session** — uses real SW CreateArc primitives
  instead of spline. Handles >180° arcs (h > pitch/2) correctly. N=3,5,7,9 sweep complete.
- ArcLines (ProfileType=2): correct geometry, H_pp scaling fix applied previously
- SineLines (ProfileType=3): correct geometry, H_pp scaling fix applied previously
- COMSOL (ProfileType=99): correct geometry from decoded STEP file

### Automation Gaps (manual steps still required)
- Material assignment: code tries SetLibraryMaterial via reflection, may or may not stick.
  User must right-click Part → Edit Definition → confirm thickness and material.
- Shell thickness: same issue — CallByName approach, needs manual verify.
- Mesh creation: user must right-click Mesh → Create Mesh after SetupStudy.
- These are fixable with a one-time API interrogation run (see section below).

### ProfileType Numbering Mismatch (TO BE FIXED)
SpiderSWRunner and SpiderGeometry use different integers for the same shape:

| Shape        | SpiderSWRunner | SpiderGeometry (current) |
|--------------|----------------|--------------------------|
| Sinusoidal   | 0              | 0                        |
| CircularArc  | 1              | 3                        |
| ArcLines     | 2              | 4                        |
| SineLines    | 3              | 2                        |

Decision: Harmonize SpiderGeometry to match SpiderSWRunner. No saved .spider files
exist, so no migration needed. Do this before any coefficient fitting.

---

## Completed Data

All at ID=67, OD=172, H_pp=7.4, T=0.8, E=6.1, Nu=0.49

| Run | File | Status |
|-----|------|--------|
| Sinusoidal N=3 | Spider_N3_ID67_OD172_Sin_auto.csv | ✓ |
| Sinusoidal N=5 | Spider_N5_ID67_OD172_Sin_auto.csv | ✓ |
| Sinusoidal N=7 | Spider_N7_ID67_OD172_Sin_auto.csv | ✓ |
| Sinusoidal N=9 | Spider_N9_ID67_OD172_Sin_auto.csv | ✓ |
| CircularArc N=3 | Spider_N3_ID67_OD172_Arc_auto.csv | ✓ |
| CircularArc N=5 | Spider_N5_ID67_OD172_Arc_auto.csv | ✓ |
| CircularArc N=7 | Spider_N7_ID67_OD172_Arc_auto.csv | ✓ |
| CircularArc N=9 | Spider_N9_ID67_OD172_Arc_auto.csv | ✓ |
| ArcLines N=7 A=30° | Spider_N7_ID67_OD172_ArcLines_A30_auto.csv | ✓ |
| ArcLines N=7 A=45° | Spider_N7_ID67_OD172_ArcLines_A45_auto.csv | ✓ |
| ArcLines N=7 A=60° | Spider_N7_ID67_OD172_ArcLines_A60_auto.csv | ✓ |
| SineLines N=7 A=30° | Spider_N7_ID67_OD172_SineLines_A30_auto.csv | ✓ |
| SineLines N=7 A=45° | Spider_N7_ID67_OD172_SineLines_A45_auto.csv | ✓ |
| SineLines N=7 A=60° | Spider_N7_ID67_OD172_SineLines_A60_auto.csv | ✓ |
| COMSOL N=7 | Spider_N7_ID67_OD172_COMSOL_auto.csv | ✓ |

### Key Results from Completed Data

CircularArc N-sweep Kms0 (N/mm):
- N=3: 0.073  |  N=5: 0.134  |  N=7: 0.090  |  N=9: 0.061
- Non-monotonic — peaks at N=5 due to >180° arc transition at N=7,9

Cross-profile Kms0 comparison at N=7:
- COMSOL: 0.0477  |  Sinusoidal: ~0.085  |  CircularArc: 0.090

Per-roll ratios (N=9 CircularArc, small x):
- 0.87, 0.76, 0.64, 0.55, 0.41, 0.32, 0.18, 0.075, 0.013
- Strong radial decay, NOT equal participation — invalidates parallel-spring model

---

## Test Matrix — Remaining Runs

### GROUP A: H_pp Sensitivity (4 runs)
Purpose: Fit Kms0 vs H_pp scaling law. Currently only one H_pp tested.

| # | Profile | N | H_pp | T | E | Nu | Notes |
|---|---------|---|------|---|---|-----|-------|
| A1 | Sinusoidal | 7 | 4.0 | 0.8 | 6.1 | 0.49 | Low amplitude |
| A2 | Sinusoidal | 7 | 6.0 | 0.8 | 6.1 | 0.49 | |
| A3 | Sinusoidal | 7 | 9.0 | 0.8 | 6.1 | 0.49 | |
| A4 | Sinusoidal | 7 | 11.0 | 0.8 | 6.1 | 0.49 | High amplitude |

Expected: Kms0 ∝ 1/H_eff² from Castigliano. Data confirms or corrects the exponent.

### GROUP B: Thickness Sensitivity (3 runs)
Purpose: Fit bending exponent (T^k where k should be ~3 for bending-dominated).

| # | Profile | N | H_pp | T | E | Nu | Notes |
|---|---------|---|------|---|---|-----|-------|
| B1 | Sinusoidal | 7 | 7.4 | 0.4 | 6.1 | 0.49 | Half thickness |
| B2 | Sinusoidal | 7 | 7.4 | 0.6 | 6.1 | 0.49 | |
| B3 | Sinusoidal | 7 | 7.4 | 1.2 | 6.1 | 0.49 | 1.5x thickness |

Expected: Kms0 ∝ T³. Also reveals bending-vs-stretch transition — if exponent
drops below 3 at T=1.2, stretch is becoming significant.

### GROUP C: Modulus Sensitivity (1 run)
Purpose: Confirm Kms0 scales linearly with E (should be exact in linear regime).

| # | Profile | N | H_pp | T | E | Nu | Notes |
|---|---------|---|------|---|---|-----|-------|
| C1 | Sinusoidal | 7 | 7.4 | 0.8 | 12.2 | 0.49 | Double E |

Expected: Kms0 exactly 2x the baseline. If not, nonlinearity or contact is involved.

### GROUP D: Material Regime Check (1 run)
Purpose: Test whether per-roll ratios and Kms(x) shape transfer between rubber
and realistic phenolic-like material.

| # | Profile | N | H_pp | T | E | Nu | Notes |
|---|---------|---|------|---|---|-----|-------|
| D1 | Sinusoidal | 7 | 7.4 | 0.25 | 300 | 0.30 | Fake phenolic |

Expected per-roll ratios: should approximately match rubber ratios (geometry-dominated).
Expected Kms(x) shape: terminal stiffening knee should shift to lower x/H_pp ratio
(stretch kicks in sooner at this bending/stretch ratio).

### GROUP E: Klippel Validation Geometry (3 runs)
Purpose: Direct comparison with Klippel Kms(x) measurement (when physical samples tested).

| # | Profile | N | H_pp | T | E | Nu | Notes |
|---|---------|---|------|---|---|-----|-------|
| E1 | Sinusoidal | 9 | 3.0 | 0.25 | TBD | TBD | Match Klippel sample |
| E2 | ArcLines | 9 | 3.0 | 0.25 | TBD | TBD | Match Klippel sample |
| E3 | SineLines | 9 | 3.0 | 0.25 | TBD | TBD | Match Klippel sample |

ID=39, OD=115. Material properties TBD — need to measure or estimate from sample.
These runs anchor FEA predictions against physical measurement.

---

## Run Priority Order

**Phase 1 — Do first (enables immediate calibration work):**
1. A1–A4 (H_pp sweep) — highest value, directly fits a model coefficient
2. B1–B3 (T sweep) — second highest, validates the core scaling law

**Phase 2 — Do next (validates assumptions):**
3. C1 (E doubling) — quick single run, confirms linear E scaling
4. D1 (fake phenolic) — critical for knowing whether rubber calibration transfers

**Phase 3 — Do when physical samples ready:**
5. E1–E3 (Klippel geometry) — blocked on material property measurement

Total: 12 runs. Groups A+B+C+D = 9 runs (no blockers, can start immediately).

---

## Run Procedure Reminder

For each run:
1. Set geometry in SpiderSWRunner UI
2. Click Create Part — verify profile looks correct in SW
3. Click Setup Study — manually confirm shell thickness and material in study tree
4. Right-click Mesh → Create Mesh
5. Click Mesh + Run
6. Click Extract Auto — verify CSV file writes correctly
7. Spot-check: Kms at first step should be reasonable for the geometry

For the FIRST run of any new parameter combination (especially B1 at T=0.4 — very
thin shell), watch for mesh quality issues. If solver fails or gives nonsense,
try doubling mesh density.

---

## What Happens With This Data

### Step 1: Harmonize ProfileType numbering
Change SpiderGeometry.vb to match SpiderSWRunner (1=Arc, 2=ArcLines, 3=SineLines).

### Step 2: Fit per-roll displacement ratio model
Using all completed data, fit a radial decay function:
  ratio(n) = f(r_mean_n, R_inner, R_outer, N)
Replace the current RollCmsShare (1/r, visualization only) with a mechanically
grounded ratio model that enters the stiffness calculation.

### Step 3: Rewrite Kms0 formula
Current: Kms = Σ k_bend_n (parallel, equal participation)
New: Kms = Σ k_bend_n · ratio_n² (energy-weighted participation)
Recalibrate CProf per profile type against corrected formula.

### Step 4: Fit H_pp and T scaling
Using Groups A and B data, verify or correct the exponents in:
  k_bend ∝ E · T^α / H_eff^β
Expected α=3, β=2. Data may show different effective exponents.

### Step 5: Validate Kms(x) shape
Compare FEA Kms(x) curves against ParallelKms_at_internal predictions.
Recalibrate x_break, lambda, K2_geometric per profile type.

### Step 6: Material transferability
Using Group D data, determine whether rubber-calibrated coefficients can be
rescaled to other materials via E·T^α, or whether separate calibration is needed.

---

## Automation Fix (Deferred)

Shell thickness and material assignment can be fully automated with a one-time
API interrogation run. Approach:
1. Add a "Dump API" button that enumerates every method/property on CWStudy,
   CWShell, CWSolidComponent, CWSolidBody for this specific SW version
2. From the dump, identify exact method signatures for thickness and material
3. Replace CallByName/reflection with direct typed calls + verification reads

This is worth doing before the 12-run matrix to save manual clicking, but is
not blocking — runs can proceed with manual confirmation steps.

SW version needed: Tools → About SolidWorks → full version string.

---

## Files to Upload at Start of Next Session

For calibration fitting, the next session needs ALL completed CSV files:
- All 15 files from the "Completed Data" table above
- Plus any new files from the test matrix that have been run

Also upload current versions of:
- SpiderGeometry.vb (from SpiderDesigner — the file being calibrated)
- SpiderProfile.vb (from SpiderSWRunner — for reference)
- SWAutomation.vb (from SpiderSWRunner — for automation fix if desired)

---

## Key Principles (from prior sessions)

- Complete files only — never diffs, partials, or snippets
- Circular arc ≠ sine wave — no sine fallbacks for CircularArc, ever
- Per-roll ratios are geometry-dominated, not material-dominated (hypothesis to verify)
- The parallel-spring model is wrong — FEA data proves unequal roll participation
- Verify DB/file writes after first run of any new parameter combination
- Non-FEA model is the current target; FEA tiers (V2–V4) are future work
