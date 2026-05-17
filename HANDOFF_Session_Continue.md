# SpiderSWRunner — Session Handoff
## Date: 2026-05-16
## Purpose: Continue edge FEA campaign and verify new profile extraction feature

---

## 1. What Was Just Built — Profile Cross-Section Extraction

A new Phase 6 was added to `ExtractResultsAuto` in `SWAutomation.vb` (1965 lines, delivered to `C:\Projects\SpiderSWRunner\SpiderSWRunner\SWAutomation.vb`).

**What it does:** After every Extract, automatically creates a `_profile.csv` alongside the main `_auto.csv`. This file contains the deformed cross-section shape of the spider/edge at 4 excursion levels (25%, 50%, 75%, last step) plus the undeformed shape.

**How it works:**
1. Finds mesh nodes near θ=0 (Front Plane slice) using angular tolerance 0.06 rad (~3.4°), widens to 0.15 rad if fewer than 10 nodes found
2. Sorts by R from inner to outer
3. Extracts UX (comp=0, radial) and UY (comp=1, axial) displacements at each selected step
4. Computes deformed coordinates: R_def = R_undef + UX, Z_def = Z_undef + UY
5. Writes wide-format CSV

**Output format:**
```
NodeID,R_undef_mm,Z_undef_mm,R_at_5_0mm,Z_at_5_0mm,R_at_10_0mm,Z_at_10_0mm,R_at_15_0mm,Z_at_15_0mm,R_at_20_0mm,Z_at_20_0mm
# ID=148 OD=207 N=1 H_pp=29.5 T=0.5 E=12 Nu=0.47 Density=1100 Material=NBR_SBR_rubber
# Profile nodes: 47  Angle tolerance: 0.060 rad
# Excursion levels (mm): 5.0, 10.0, 15.0, 20.0
# Steps used: 25, 50, 75, 100
37,74.0000,0.0000,74.0120,0.4500,...
```

**Profile filename convention:** Same as main CSV but `_auto.csv` → `_profile.csv`
Example: `Edge_N1_ID148_OD207_HalfRoll_Hpp29_5_T0_50_NBR_SBR_rubber_Push_profile.csv`

**FIRST TASK FOR NEW CHAT:** Patrick will upload a `_profile.csv` file. The new chat should plot R vs Z for all excursion levels to verify the extraction is working correctly. Expect to see the dome profile flattening/inverting as excursion increases.

**Known concern:** UX (comp=0) from `GetDisplacementComponentForAllStepsAtNode` returns the X-component in global Cartesian coordinates, not radial. For nodes near θ=0 (x>0, z≈0), UX ≈ radial displacement. For nodes at other angles this would be wrong, but we filter to θ < 0.06 rad so the error is negligible (<0.2%). If profiles look wrong (nodes crossing or non-physical R shifts), this is the first thing to check.

---

## 2. Edge Campaign Status

### 2.1 Completed Edge Runs

**Batch 0 (anchor, T=0.80, NBR_SBR_rubber):**
| Run | Kms₀ | Converged to | Status |
|---|---|---|---|
| HalfRoll T=0.80 Push | 0.021 | 20mm ✓ | Valid |
| HalfRoll T=0.80 Pull | 0.015 | 14mm | Valid |

**Batch 1 Run 3 (just completed):**
| Run | Kms₀ | Converged to | Status |
|---|---|---|---|
| HalfRoll T=0.50 Push | 0.0203 | 20mm (100 steps) ✓ | Valid |

### 2.2 Remaining Batch 1 Runs (3 runs)

All use: Half Roll, N=1, ID=148, OD=207, OuterLip=3.0, InnerLip=5.0, NBR_SBR_rubber (index 2), MaxDisp=20, 100 fixed steps, standard mesh finest density.

**Each T change requires a fresh Create Part (FilletR = 2×T changes geometry).**

| Run | T | H_pp (GUI) | Direction | Filename |
|---|---|---|---|---|
| 4 | **1.20** | 29.5 | Push | `Edge_N1_ID148_OD207_HalfRoll_Hpp29_5_T1_20_NBR_SBR_rubber_Push_auto.csv` |
| 5 | 0.80 | **12.0** | Push | `Edge_N1_ID148_OD207_HalfRoll_Hpp12_0_T0_80_NBR_SBR_rubber_Push_auto.csv` |
| 6 | 0.80 | **20.0** | Push | `Edge_N1_ID148_OD207_HalfRoll_Hpp20_0_T0_80_NBR_SBR_rubber_Push_auto.csv` |

**Screenshot filenames for each run (6 per run):**
```
..._Push_side_step001.jpg
..._Push_side_step025.jpg
..._Push_side_step050.jpg
..._Push_side_step075.jpg
..._Push_side_stepLast.jpg
..._Push_top_stepLast.jpg
```

**To capture screenshots at different steps:** In SW Simulation, double-click Displacement plot → in the Plot Step section, change the step number → view updates → right-click plot name in study tree → Save As → JPG.

### 2.3 Batch 1 Validation Gates (from EdgeConeDesigner spec)

**Gate A — Push/Pull asymmetry:** Already confirmed from Batch 0 — 40% asymmetry, direction is a calibrated effect.

**Gate B — T-scaling (Runs 1, 3, 4):** Fit log(Kms₀) vs log(T) across T={0.50, 0.80, 1.20}. Run 3 gave Kms₀=0.0203 which is nearly identical to T=0.80 (0.021) — only 3% difference despite 37% thickness change. This suggests membrane-dominated behavior. Run 4 (T=1.20) will complete the picture.

**Gate C — H-scaling (Runs 1, 5, 6):** Fit log(Kms₀) vs log(H_pp) across H={6.0, 10.0, 14.75}. Prediction: β positive, +0.5 to +1.2.

**Gate D — Convergence:** All runs should reach full 20mm. T=0.50 did (100 steps).

### 2.4 InnerLip Discrepancy (OPEN)

The EdgeConeDesigner handoff originally said InnerLip=3.0mm, but GUI default is 5.0mm and Batch 0 used 5.0mm. Revised spec says InnerLip=3.0 "matches Batch 0" — but we verified Batch 0 used the GUI default of 5.0. **New runs are using InnerLip=5.0 to match Batch 0.** EdgeConeDesigner should be informed that InnerLip=5.0 is the actual value used throughout.

---

## 3. Spider Campaign — COMPLETE

### 3.1 Final Status

42 validated CSVs across 4 batches:
- Batch 1: T-sweep (T=0.20, 0.42, 0.80) Push+Pull pairs, Cloth_A_PolyCotton
- Batch 2: H-sweep (H=4.0, 7.4, 9.0), ArcLines A45, N=5 validation
- Batch 3: Triangle profile, Bimax_DKM E-linearity, Arc asymmetry, OD=200 geometry, Triangle N=5
- Batch 4: OD=200 N-sweep (N=4, 6, 8) — final runs

**V1 calibration RMSE < 5%, max error < 10% after Batch 4.** No further spider FEA planned.

### 3.2 Key Spider Findings

- T-exponent ≈ 1.51 (not Castigliano T³)
- H-exponent ≈ +0.89 (positive, opposite to Castigliano −2.0)
- E-scaling linear (verified 600→750→1200 MPa)
- Push/Pull Kms₀ match within ~2% at small displacement
- Profile factors: Sin=1.00, ArcLines A45≈1.27, Arc≈0.94, Triangle≈5.0
- Triangle much stiffer than predicted (5× Sin), excluded from V1

---

## 4. Materials in GUI and SW

### 4.1 GUI Material Combo (indices 0–10)

| Index | Name | E (MPa) | ν | ρ (kg/m³) | Notes |
|---|---|---|---|---|---|
| 0 | Rubber | 6.1 | 0.49 | 1000 | Spider legacy, SW library |
| 1 | Nomex | 8000 | 0.28 | 1100 | Legacy — values unverified |
| 2 | NBR_SBR_rubber | 12 | 0.47 | 1100 | **Edge surround** |
| 3 | Polyester_foam | 3 | 0.20 | 200 | Edge surround |
| 4 | Cloth_spider | 89 | 0.30 | 660 | **Retired — too soft** |
| 5 | Cloth_A_PolyCotton | 600 | 0.30 | 1320 | Spider primary |
| 6 | Cloth_B_CottonPoly | 900 | 0.30 | 1340 | Spider — not in SW DB |
| 7 | Cloth_C_Isotropic | 750 | 0.30 | 1350 | Spider calibration |
| 8 | Cloth_D_SoftCotton | 500 | 0.20 | 1280 | Spider |
| 9 | Cloth_E_Aramid | 1500 | 0.25 | 1390 | Spider |
| 10 | Bimax_DKM | 1200 | 0.30 | 1100 | Spider E-linearity |

### 4.2 Edge Material Defaults

Edge mode defaults to NBR_SBR_rubber (index 2).
Spider mode defaults to Rubber (index 0).

### 4.3 Materials in SW Custom Materials Database

**Under Spider Materials:** Rubber, Nomex, Cloth_Real (=Cloth_C), Cloth_A_PolyCotton, Bimax_DKM
**Under Edge_Materials:** NBR_SBR_rubber, Polyester_foam

### 4.4 Surround Materials NOT YET in SW (deferred to Batch 2+)

| SW Name | E (N/m²) | ν | G (N/m²) | ρ (kg/m³) |
|---|---|---|---|---|
| Surround_Rubber_Soft | 1.5e+07 | 0.47 | 5.102e+06 | 1150 |
| Surround_Rubber_Med | 3.0e+07 | 0.47 | 1.020e+07 | 1200 |
| Surround_Rubber_Hard | 5.0e+07 | 0.47 | 1.701e+07 | 1250 |
| Surround_Rubber_FibreComp | 8.0e+07 | 0.45 | 2.759e+07 | 1280 |
| Surround_Foam_Soft | 2.0e+06 | 0.15 | 8.696e+05 | 150 |
| Surround_Foam_Med | 5.0e+06 | 0.20 | 2.083e+06 | 200 |
| Surround_Foam_Stiff | 1.0e+07 | 0.25 | 4.0e+06 | 300 |
| Surround_Cloth_Treated | 2.0e+08 | 0.30 | 7.692e+07 | 800 |

---

## 5. Hard Rules (Permanent — in memory edits)

1. **NEVER tell user a parameter can be changed independently without FIRST searching the code for all dependencies.** FilletR = 2×T was the lesson. Every T change requires fresh Create Part.

2. **Default to full workflow (Create Part → Setup Study → Run) when uncertain.** Never recommend shortcuts without code verification.

3. **FEA CSV data checks — all four, every time:**
   - Crest R positions match expected geometry for stated T value
   - Crest Y values within ±0.05mm of nominal H_eff
   - Push/Pull Kms₀ match within 1% (flag if 1–2.5%, reject if >2.5%)
   - Header material/E/Nu match what was applied in SW

4. **Do not state things as fact unless verified in code or data.** Label assumptions explicitly.

5. **Curvature-based mesh is permanently broken.** Causes ~10× stiffness reduction. Standard mesh only.

6. **Every batch handoff varying OD, N, H_pp, T, or LipWidth must include a geometry precheck table.**

7. **Screen captures use .jpg format.** Full filenames provided for every run before running.

---

## 6. SolidWorks API Limitations (Permanent)

These require manual clicks per run:
1. Shell thickness acceptance — green check in study tree
2. Custom material application — apply manually in study tree
3. Fixed stepping radio — click Fixed in Study Properties (API pre-loads FixedTimeIncrement=0.01)

These are confirmed unfixable in SW 2022 COM API after extensive testing (brute-force property probing of 80+ names, SendKeys attempted and abandoned).

---

## 7. Solver Settings Standard

| Setting | Value |
|---|---|
| Study type | Nonlinear static |
| Large displacement | ON |
| Mesh | Standard, finest density (NOT curvature-based) |
| Element quality | High (quadratic, 6-node thin shell) |
| Stepping | Fixed, 100 steps |
| Displacement ramp | ±20mm (edges), ±35mm (spiders) |
| Solver | FFEPlus |

---

## 8. Code File Summary

All at `C:\Projects\SpiderSWRunner\SpiderSWRunner\`:

| File | Lines | Last delivered | Key changes this session |
|---|---|---|---|
| SWAutomation.vb | 1965 | 2026-05-16 | **Profile extraction (Phase 6)**, Edge_Materials DB, FixedTimeIncrement, cleaned probes |
| Form1.vb | ~640 | 2026-05-09 | Edge defaults, 11 materials, Triangle in spider+edge combos, Cloth_A default for spider |
| Form1.Designer.vb | ~380 | 2026-05-09 | 11 material items, Bimax_DKM |
| SpiderProfile.vb | 1772 | 2026-04-23 | ArcLines/SineLines segment bug fixed (removed broken GetSketchSegments block) |

---

## 9. File Locations

| Item | Path |
|---|---|
| Source code | C:\Projects\SpiderSWRunner\SpiderSWRunner\ |
| Spider results | C:\SpiderSW_Results\Spider_Asymmetry\ |
| Edge results | C:\SpiderSW_Results\Edge\ |
| Spider Batch 3 | C:\SSR\B3\ (shorter path for long filenames) |
| SW solver logs | c:\users\cad\appdata\local\temp\ |
| GitHub repo | https://github.com/RedrockPlacitas/SpiderSWRunner.git |

---

## 10. Immediate Next Steps

1. **Verify profile extraction:** Run Extract on any existing edge study. Upload the `_profile.csv`. New chat plots R vs Z curves at all excursion levels to confirm the data is correct.

2. **Complete Batch 1:** Runs 4, 5, 6 (settings in §2.2 above). Each needs fresh Create Part. Capture 6 screenshots per run.

3. **Send Batch 1 data to EdgeConeDesigner** for gate evaluation (T-scaling, H-scaling).

4. **Await Batch 2 spec** from EdgeConeDesigner (estimated ~7 runs: W/R decoupling, off-anchor Pull, high-E material, OuterLip sensitivity).

5. **Git commit** all code changes before next campaign runs.

---

## 11. Git Commit Reminder

Open Git Bash, navigate to project:
```
cd /c/Projects/SpiderSWRunner
git add -A
git status
git commit -m "Edge profile extraction, Triangle profile, Bimax material, surround material presets"
git push origin main
```
