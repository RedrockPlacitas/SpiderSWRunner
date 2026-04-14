# SpiderDesigner Calibration — Handoff from FEA Data Collection
Date: 2026-04-10

## Summary

All FEA data collection runs (Groups A–D) are complete. This document details what was collected, what needs to change in SpiderDesigner, and the step-by-step calibration procedure.

---

## Data Files Location

```
C:\Projects\SpiderDesigner\CalibrationData\
```

### Previously Completed (15 files)

| File | Profile | N | H_pp | T | E | Nu |
|------|---------|---|------|---|---|-----|
| Spider_N3_ID67_OD172_Sin_auto.csv | Sinusoidal | 3 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N5_ID67_OD172_Sin_auto.csv | Sinusoidal | 5 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_Sin_auto.csv | Sinusoidal | 7 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N9_ID67_OD172_Sin_auto.csv | Sinusoidal | 9 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N3_ID67_OD172_Arc_auto.csv | CircularArc | 3 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N5_ID67_OD172_Arc_auto.csv | CircularArc | 5 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_Arc_auto.csv | CircularArc | 7 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N9_ID67_OD172_Arc_auto.csv | CircularArc | 9 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_ArcLines_A30_auto.csv | ArcLines | 7 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_ArcLines_A45_auto.csv | ArcLines | 7 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_ArcLines_A60_auto.csv | ArcLines | 7 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_SineLines_A30_auto.csv | SineLines | 7 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_SineLines_A45_auto.csv | SineLines | 7 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_SineLines_A60_auto.csv | SineLines | 7 | 7.4 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_COMSOL_auto.csv | COMSOL | 7 | 7.4 | 0.8 | 6.1 | 0.49 |

NOTE: These 15 files use the OLD naming convention (no Hpp/T/E in filename). They are unambiguous because all share the same H_pp=7.4, T=0.8, E=6.1.

### New Sweep Runs (9 files, new naming convention)

| File | Group | Profile | N | H_pp | T | E | Nu |
|------|-------|---------|---|------|---|---|-----|
| Spider_N7_ID67_OD172_Sin_Hpp4.0_T0.8_E6.1_auto.csv | A1 | Sinusoidal | 7 | 4.0 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_Sin_Hpp6.0_T0.8_E6.1_auto.csv | A2 | Sinusoidal | 7 | 6.0 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_Sin_Hpp9.0_T0.8_E6.1_auto.csv | A3 | Sinusoidal | 7 | 9.0 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_Sin_Hpp9.5_T0.8_E6.1_auto.csv | A4 | Sinusoidal | 7 | 9.5 | 0.8 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_Sin_Hpp7.4_T0.4_E6.1_auto.csv | B1 | Sinusoidal | 7 | 7.4 | 0.4 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_Sin_Hpp7.4_T0.6_E6.1_auto.csv | B2 | Sinusoidal | 7 | 7.4 | 0.6 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_Sin_Hpp7.4_T1.2_E6.1_auto.csv | B3 | Sinusoidal | 7 | 7.4 | 1.2 | 6.1 | 0.49 |
| Spider_N7_ID67_OD172_Sin_Hpp7.4_T0.8_E12.2_auto.csv | C1 | Sinusoidal | 7 | 7.4 | 0.8 | 12.2 | 0.49 |
| Spider_N7_ID67_OD172_Sin_Hpp7.4_T0.25_E300_auto.csv | D1 | Sinusoidal | 7 | 7.4 | 0.25 | 300 | 0.30 |

NOTE: A4 was changed from H_pp=11.0 to 9.5 because 11.0 exceeded MaxH_pp (9.84) and caused solver convergence failure.

### Deferred — Klippel Validation (Group E, 3 runs)

Blocked on material property measurement from physical samples. ID=39, OD=115, N=9, H_pp=3.0, T=0.25. Profiles: Sinusoidal, ArcLines, SineLines.

---

## CSV File Format

Each CSV contains per-step data with a comment header block:

```
Step,Time_frac,X_applied_mm,F_reaction_N,Kms_N_per_mm,Z_roll1_mm,...,Ratio_roll1,...
# ID=67 OD=172 N=7 H_pp=7.4 T=0.8 E=6.1 Nu=0.49
# Roll crests R (mm): 76.50, 83.71, 90.93, ...
# Crest node IDs: ...
# Crest node R (mm): ...
# Crest node Y (mm): ...
1,0.050000,0.500000,0.042553,0.085106,0.435000,...,0.870000,...
```

Key columns for calibration:
- **Kms_N_per_mm** = F_reaction / X_applied — this is the target for Kms0 and Kms(x) fitting
- **Ratio_rollN** = Z_rollN / X_applied — per-roll participation ratios for decay model

Kms0 = Kms at the first step (smallest displacement, closest to linear regime).

---

## Key FEA Results Already Known

### CircularArc N-sweep Kms0 (N/mm)
- N=3: 0.073 | N=5: 0.134 | N=7: 0.090 | N=9: 0.061
- Non-monotonic — peaks at N=5 due to >180° arc transition at N≥7

### Cross-profile Kms0 at N=7
- COMSOL: 0.0477 | Sinusoidal: ~0.085 | CircularArc: 0.090

### Per-roll ratios (N=9 CircularArc, small x)
- 0.87, 0.76, 0.64, 0.55, 0.41, 0.32, 0.18, 0.075, 0.013
- Strong radial decay from ID to OD
- Invalidates the parallel-spring (equal participation) model

---

## Required Changes to SpiderDesigner

### Change 1: Harmonize ProfileType Numbering

SpiderSWRunner and SpiderGeometry currently disagree:

| Shape | SpiderSWRunner | SpiderGeometry (current) | SpiderGeometry (new) |
|-------|----------------|--------------------------|----------------------|
| Sinusoidal | 0 | 0 | 0 (no change) |
| CircularArc | 1 | 3 | 1 |
| ArcLines | 2 | 4 | 2 |
| SineLines | 3 | 2 | 3 |

No saved .spider files exist, so no migration needed. This must be done before any coefficient fitting so profile type indices match between data files and the analytical model.

Files affected: SpiderGeometry.vb — all Select Case blocks on ProfileType, CProf tables, CBreak tables.

### Change 2: Replace Parallel-Spring Model with Radial Decay

Current model: `Kms = Σ k_bend_n` (all rolls contribute equally)
New model: `Kms = Σ k_bend_n × ratio_n²` (energy-weighted participation)

The ratio function to fit from FEA data:
```
ratio(n) = f(r_mean_n, R_inner, R_outer, N)
```

This replaces the current `RollCmsShare` (1/r weighting, visualization only) with a mechanically grounded decay model that enters the actual stiffness calculation.

Data sources for fitting:
- N=9 CircularArc per-roll ratios (strongest signal, most rolls)
- N=3,5,7,9 CircularArc and Sinusoidal ratios (verify N-independence)
- All Ratio_rollN columns from all 24 CSV files

### Change 3: Fit H_pp and T Scaling Exponents

Current analytical assumption: `k_bend ∝ E × T³ / H_eff²`

Group A data (H_pp sweep at constant T) fits or corrects β in: `Kms0 ∝ 1/H_eff^β`
Group B data (T sweep at constant H_pp) fits or corrects α in: `Kms0 ∝ T^α`

Expected: α=3 (bending-dominated), β=2 (Castigliano). If B3 (T=1.2) shows α<3, stretch is becoming significant at that thickness.

### Change 4: Recalibrate CProf Per Profile Type

After Changes 2 and 3 are implemented, refit CProf coefficients for each profile type so that the analytical Kms0 matches FEA Kms0 across all N values and parameter combinations.

### Change 5: Fit Kms(x) Shape Parameters

Compare FEA Kms(x) curves against ParallelKms_at_internal predictions. Recalibrate per profile type:
- x_break — where softening dip transitions to terminal stiffening
- lambda — decay rate in softening region
- K2_geometric — terminal stiffening multiplier

### Change 6: Validate Material Transferability

Group D1 (E=300, Nu=0.30, T=0.25 — fake phenolic) tests whether rubber-calibrated coefficients transfer to other materials via E×T^α scaling, or whether separate calibration per material class is needed.

Compare D1 per-roll ratios against baseline rubber ratios. If they match, the decay model is geometry-dominated (good — single calibration works). If they diverge, material enters the ratio model.

---

## Calibration Procedure — Step by Step

### Step 1: Harmonize ProfileType numbering
Edit SpiderGeometry.vb Select Case blocks. Quick mechanical change.

### Step 2: Load all CSV files, extract Kms0 and per-roll ratios
Write a loader (or use the existing CSV infrastructure) to parse the comment headers and data rows. Build a table of Kms0 values indexed by (ProfileType, N, H_pp, T, E).

### Step 3: Fit radial decay function
Using per-roll ratios from all runs, fit: `ratio(n) = A × exp(-B × (r_n - R_inner) / (R_outer - R_inner))` or similar. Verify that the fitted function is approximately independent of material (compare rubber vs D1).

### Step 4: Implement energy-weighted Kms0
Replace the parallel sum in Calculate() with: `Kms = Σ k_bend_n × ratio_n²`

### Step 5: Fit scaling exponents
Using Group A (5 H_pp values: 4.0, 6.0, 7.4, 9.0, 9.5) and Group B (4 T values: 0.4, 0.6, 0.8, 1.2):
- Log-log regression of Kms0 vs H_eff → β
- Log-log regression of Kms0 vs T → α
- Verify C1 gives exactly 2× baseline (linear E scaling)

### Step 6: Refit CProf
With the corrected formula (energy-weighted, correct exponents), solve for CProf per profile type to match FEA Kms0 across all available data points.

### Step 7: Fit Kms(x) shape
Compare full Kms(x) curves from FEA against ParallelKms_at_internal. Adjust x_break, lambda, K2_geometric per profile type.

---

## Files Needed at Start of Calibration Session

Upload to Claude:
1. **SpiderGeometry.vb** — from `C:\Projects\SpiderDesigner\` (the file being modified)
2. **All 24 CSV files** — from `C:\Projects\SpiderDesigner\CalibrationData\`
3. This handoff document

---

## Key Principles

- Complete files only — never diffs, partials, or snippets
- Circular arc ≠ sine wave — no sine fallbacks for CircularArc, ever
- The parallel-spring model is wrong — FEA data proves unequal roll participation
- Per-roll ratios are hypothesized to be geometry-dominated (D1 data will confirm or deny)
- Verify outputs after first run of any new calculation path
- ProfileType numbering must match between SpiderSWRunner and SpiderDesigner before any fitting
