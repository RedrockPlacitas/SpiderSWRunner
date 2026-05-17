# SpiderSWRunner — Remaining Cloth Runs
## Status: ArcLines N7 forced H_pp rerun COMPLETE ✓

## Solver settings — ALL runs
Fixed steps, 100 steps, ±35mm, 0.35mm step size.
OD fully fixed. ID prescribed axial only, radial DOF free.
Material: Cloth (E=89 MPa, Nu=0.30, density=660 kg/m³)
T=0.8mm, H_pp=7.4mm forced for all runs unless noted.
N=7, Roll crests: 38.38, 44.94, 51.49, 58.05, 64.61, 71.16, 77.72mm

---

## Batch 1 — Angle sweeps (8 runs)

| # | Filename | Profile | Angle | Dir |
|---|---|---|---|---|
| 1 | `Spider_N7_ID67_OD172_ArcLines_Hpp7_4_T0_8_Cloth_spider_A30_Push_auto.csv` | ArcLines | 30° | +35mm |
| 2 | `Spider_N7_ID67_OD172_ArcLines_Hpp7_4_T0_8_Cloth_spider_A30_Pull_auto.csv` | ArcLines | 30° | −35mm |
| 3 | `Spider_N7_ID67_OD172_ArcLines_Hpp7_4_T0_8_Cloth_spider_A60_Push_auto.csv` | ArcLines | 60° | +35mm |
| 4 | `Spider_N7_ID67_OD172_ArcLines_Hpp7_4_T0_8_Cloth_spider_A60_Pull_auto.csv` | ArcLines | 60° | −35mm |
| 5 | `Spider_N7_ID67_OD172_SineLines_Hpp7_4_T0_8_Cloth_spider_A30_Push_auto.csv` | SineLines | 30° | +35mm |
| 6 | `Spider_N7_ID67_OD172_SineLines_Hpp7_4_T0_8_Cloth_spider_A30_Pull_auto.csv` | SineLines | 30° | −35mm |
| 7 | `Spider_N7_ID67_OD172_SineLines_Hpp7_4_T0_8_Cloth_spider_A60_Push_auto.csv` | SineLines | 60° | +35mm |
| 8 | `Spider_N7_ID67_OD172_SineLines_Hpp7_4_T0_8_Cloth_spider_A60_Pull_auto.csv` | SineLines | 60° | −35mm |

---

## Batch 2 — Taper sweep (6 runs, Sin profile)

H_pp=7.4mm outer. TaperPct reduces H_eff linearly from OD roll to ID roll.
- Tp25: inner roll H_eff = 75% of outer
- Tp50: inner roll H_eff = 50% of outer
- Tp75: inner roll H_eff = 25% of outer

| # | Filename | TaperPct | Dir |
|---|---|---|---|
| 9  | `Spider_N7_ID67_OD172_Sin_Hpp7_4_T0_8_Cloth_spider_Tp25_Push_auto.csv` | 25% | +35mm |
| 10 | `Spider_N7_ID67_OD172_Sin_Hpp7_4_T0_8_Cloth_spider_Tp25_Pull_auto.csv` | 25% | −35mm |
| 11 | `Spider_N7_ID67_OD172_Sin_Hpp7_4_T0_8_Cloth_spider_Tp50_Push_auto.csv` | 50% | +35mm |
| 12 | `Spider_N7_ID67_OD172_Sin_Hpp7_4_T0_8_Cloth_spider_Tp50_Pull_auto.csv` | 50% | −35mm |
| 13 | `Spider_N7_ID67_OD172_Sin_Hpp7_4_T0_8_Cloth_spider_Tp75_Push_auto.csv` | 75% | +35mm |
| 14 | `Spider_N7_ID67_OD172_Sin_Hpp7_4_T0_8_Cloth_spider_Tp75_Pull_auto.csv` | 75% | −35mm |

---

## Batch 3 — Variable pitch (4 runs, Sin profile)

PitchTaperPct varies pitch linearly from ID to OD.
+20: ID pitch 20% wider than mean, OD pitch 20% narrower
-20: ID pitch 20% narrower than mean, OD pitch 20% wider

| # | Filename | PitchTaper | Dir |
|---|---|---|---|
| 15 | `Spider_N7_ID67_OD172_Sin_Hpp7_4_T0_8_Cloth_spider_Pt20_Push_auto.csv` | +20% | +35mm |
| 16 | `Spider_N7_ID67_OD172_Sin_Hpp7_4_T0_8_Cloth_spider_Pt20_Pull_auto.csv` | +20% | −35mm |
| 17 | `Spider_N7_ID67_OD172_Sin_Hpp7_4_T0_8_Cloth_spider_Pt-20_Push_auto.csv` | −20% | +35mm |
| 18 | `Spider_N7_ID67_OD172_Sin_Hpp7_4_T0_8_Cloth_spider_Pt-20_Pull_auto.csv` | −20% | −35mm |

---

## Batch 4 — T=0.4 fixed step reruns (6 runs)

Previous T=0.4 auto-step runs stalled at 15–18mm.
Reuse existing models — change solver to fixed steps only. No geometry changes.

| # | Filename | Profile | Dir |
|---|---|---|---|
| 19 | `Spider_N7_ID67_OD172_Sin_Hpp7_4_T0_4_Cloth_spider_Push_Fixed_auto.csv` | Sin | +35mm |
| 20 | `Spider_N7_ID67_OD172_Sin_Hpp7_4_T0_4_Cloth_spider_Pull_Fixed_auto.csv` | Sin | −35mm |
| 21 | `Spider_N7_ID67_OD172_SineLines_Hpp7_4_T0_4_Cloth_spider_A45_Push_Fixed_auto.csv` | SineLines | +35mm |
| 22 | `Spider_N7_ID67_OD172_SineLines_Hpp7_4_T0_4_Cloth_spider_A45_Pull_Fixed_auto.csv` | SineLines | −35mm |
| 23 | `Spider_N7_ID67_OD172_ArcLines_Hpp7_4_T0_4_Cloth_spider_A45_Push_Fixed_auto.csv` | ArcLines | +35mm |
| 24 | `Spider_N7_ID67_OD172_ArcLines_Hpp7_4_T0_4_Cloth_spider_A45_Pull_Fixed_auto.csv` | ArcLines | −35mm |

---

## Run order

Batches 1 and 2 in parallel if two sessions available.
Batch 3 after Batch 2 — same Sin model, just change pitch setting.
Batch 4 last — existing models, solver change only.

## What each batch answers

| Batch | Question |
|---|---|
| 1 Angle sweep | How does connector angle affect Kms0, XC, buckling onset? |
| 2 Taper | Does cloth taper bring XC into measurable range? Does pull buckle earlier? |
| 3 Pitch taper | How does variable pitch affect stiffness distribution and asymmetry? |
| 4 T=0.4 fixed | Physical buckling vs solver limit at thin cloth? |
