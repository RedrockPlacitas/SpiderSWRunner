# SpiderSWRunner — Session Handoff for Phase B Testing

## What this project is
SpiderSWRunner is a VB.NET tool (VS2010, .NET 4.0, SolidWorks 2014) that generates parametric spider cross-section profiles for FEA simulation. It automates the workflow: generate geometry → create SW part → setup nonlinear study → mesh & run → extract results CSV.

## What was accomplished in the previous session

### Git setup
- Repo initialized at `C:\Projects\SpiderSWRunner\`
- Remote: `https://github.com/RedrockPlacitas/SpiderSWRunner.git` (private)
- Git for Windows 2.46.2 (last version supporting Win7)
- Current commit: `2f1cbf9` "Stage 2: FEA calibration UI controls"
- Commit workflow: Git Bash → `cd /c/Projects/SpiderSWRunner` → `git add .` → `git commit -m "msg"` → `git push`

### Stage 1 — Per-roll geometry (SpiderProfile.vb) ✓
Added `TaperPct`, `PitchTaperPct`, `VariablePitch`, `UseNaturalH` fields plus `ComputeRollMetrics()` which computes per-roll arrays (RollStart, Pitch, H_eff_roll, H_natural). Rewrote `GeneratePoints_Sinusoidal`, `GeneratePoints_ArcLines`, `GeneratePoints_SineLines` to use per-roll geometry.

**Critical VS2010 bugs found and fixed:**
- `RollMetrics` changed from `Structure` to `Class` (VS2010 value-type return semantics lose array data)
- Loop variable `n` renamed to `idx` in `ComputeRollMetrics` (VB.NET case-insensitive: `n` shadows field `N`)
- All `If()` ternaries replaced with explicit `If/Then/Else` blocks

**Validated:** S1 (Sinusoidal) reproduces Phase A reference data bit-for-bit. A1 (ArcLines with UseNaturalH=True) produces correct natural-H geometry (crest Y ≈ ±1.55, Kms ≈ 0.024 matching reference).

### Stage 2 — UI controls (Form1.vb, Form1.Designer.vb) ✓
Added "FEA Calibration" group box (Spider mode only) with:
- Taper% textbox → `TaperPct`
- Variable Pitch checkbox → enables Pt% textbox
- Pt% textbox → `PitchTaperPct`
- Use Natural H checkbox → `UseNaturalH`

Output directory default: `C:\SpiderSW_Results\PhaseA_20260414`
Output filenames include `_Tp25`, `_Pt20`, `_natH` suffixes when active.

### Phase A — Topology validation ✓
- S1 (Sinusoidal): full pipeline pass, Kms matches reference (0.099)
- A1 (ArcLines A45): topology pass, Kms mismatch diagnosed (hScale bug → UseNaturalH fix)
- A1_A30 (ArcLines A30): topology pass

### Key discovery: ArcLines hScale
The existing hScale fix (line 775 old code) vertically stretches ArcLines to match H_pp, destroying arc geometry. At A45: natural H_eff ≈ 1.55mm, but hScale stretches to 3.70mm, producing a pseudo-sinusoid (Kms ≈ 0.101 instead of reference 0.024). UseNaturalH bypasses hScale, preserving true arc geometry. The reference dataset was generated before hScale was added.

## FEA Calibration Campaign — Frozen Matrix (14 runs)

### Measurand
`Kms_x10 = (F(x=+1.0) - F(x=-1.0)) / 2.0` at x = ±1.0 mm (not small-signal Kms₀).
Linearity check at x = 0.2 mm is informational only.

### Matrix rows
| # | RunID | Profile | Taper% | Pitch% | Angle | RefSource | Reference |
|---|-------|---------|--------|--------|-------|-----------|-----------|
| 1 | S1 | Sin | 0 | 0 | — | SW | 0.099 |
| 2 | S2 | Sin | 25 | 0 | — | ModelA | computed |
| 3 | S3 | Sin | 50 | 0 | — | ModelA | computed |
| 4 | S4 | Sin | 75 | 0 | — | ModelA | computed |
| 5 | S5 | Sin | 0 | +20 | — | None | sanity 0.3×–3× of S1 |
| 6 | S6 | Sin | 0 | -20 | — | None | sanity 0.3×–3× of S1 |
| 7 | S7 | Sin | 50 | +20 | — | Separability | post-hoc ratio 0.5–1.5 |
| 8 | A1 | Arc+Lines | 0 | 0 | 45 | SW | 0.024 |
| 9 | A1_A30 | Arc+Lines | 0 | 0 | 30 | SW | 0.0128 |
| 10 | A1_A60 | Arc+Lines | 0 | 0 | 60 | SW | 0.0409 |
| 11 | A5 | Arc+Lines | 0 | +20 | 45 | None | sanity 0.3×–3× of A1 |
| 12 | A6 | Arc+Lines | 0 | -20 | 45 | None | sanity 0.3×–3× of A1 |
| 13 | A7 | Arc+Lines | 0 | +20 | 45 | Separability | cross-profile transfer |
| 14 | A8_A30 | Arc+Lines | 0 | +20 | 30 | None | sanity 0.3×–3× of A1_A30 |

### Key decisions documented
- A2/A3/A4 (Arc+Lines taper sweeps) **dropped**: §4.7 forbids independent taper at fixed theta + uniform pitch (geometry identical to A1)
- A7 uses **cross-profile separability**: `Kms(A7) ≈ Kms(A1) × [Kms(S3)/Kms(S1)] × [Kms(A5)/Kms(A1)]`
- All ArcLines runs use **UseNaturalH=True** (checkbox in UI)
- Sinusoidal runs leave UseNaturalH unchecked
- SafetyPct-to-angle mapping: A30→26.8%, A45→41.4%, A60→57.7%

### Reference data
- SW reference dataset: 20 CSV files in `C:\SpiderSW_Results\` (backed up)
- Reference values are Kms at x=1.05mm from that dataset
- ArcLines A30 hardens aggressively (0.013→0.063 over 1–9mm); A60 is flat

### Output
- Results go to `C:\SpiderSW_Results\PhaseA_20260414\` (or timestamped subfolder for matrix)
- No silent filename overwrites — filenames include taper/pitch/angle/natH suffixes

## What needs to happen next

### Phase B — Dry-run remaining matrix rows
Test all 14 rows through the UI with the new Stage 2 controls. For each row:
1. Set inputs per the matrix table
2. Create Part — verify sketch closes, revolve succeeds, geometry looks correct
3. For the first few rows: run through full pipeline (mesh, solve, extract) to verify
4. For rows that are geometrically similar to passed rows: topology check only

Priority concerns:
- S2/S3/S4 (taper): inner rolls get shorter — check for self-collision at S4 (H_eff_inner = 0.925mm vs T=0.8mm)
- S5/S6 (variable pitch): outer rolls get narrower — check aspect ratio
- S7 (combined): steepest combination
- A8_A30 (pitch+angle at A30): most likely to break

### After Phase B
- All rows pass → proceed with the 14-run FEA campaign (manual: change values, run, extract)
- Any row fails topology → diagnose and fix or drop the row

## File inventory
| File | Path | Purpose |
|------|------|---------|
| SpiderProfile.vb | SpiderSWRunner\SpiderSWRunner\ | Profile geometry + per-roll computation |
| Form1.vb | SpiderSWRunner\SpiderSWRunner\ | UI logic, BuildProfile, event handlers |
| Form1.Designer.vb | SpiderSWRunner\SpiderSWRunner\ | UI layout, control creation |
| SWAutomation.vb | SpiderSWRunner\SpiderSWRunner\ | SolidWorks COM automation |
| FEA_CALIBRATION_HANDOFF.md | SpiderSWRunner\ | Campaign spec (§4 geometry, §5 matrix, §6 protocol) |

## User preferences
- **Complete files only** — never diffs, partials, or instructions to edit manually
- **Never ask user to search files** — ask for uploads
- **Always give complete paths** for files delivered and requested
- **Output folder:** `C:\SpiderSW_Results\PhaseA_20260414\`
- **Commit reminder** after every successful test, with full Git Bash directions
- User cleans and rebuilds every time — don't ask about it
- VB.NET on VS2010: avoid `n` as loop variable (collides with field `N`), avoid `Structure` with array fields, avoid `If()` ternaries
