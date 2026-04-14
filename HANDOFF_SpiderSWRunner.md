# SpiderSWRunner Handoff
Date: 2026-04-07

## Project Location
`C:\Projects\SpiderSWRunner\` — VB.NET WinForms, .NET Framework 4.0, VS2010

## Status: CircularArc profile broken, all others working

## What Works
- Sinusoidal profile: ✓ correct shape, simulations running, data extracted
- ArcLines profile: ✓ correct shape (diagonal straights + arcs)
- SineLines profile: ✓ correct shape
- COMSOL profile: ✓ correct shape
- FEA pipeline: ✓ connect, create part, setup study, mesh+run, extract all working
- Data extraction: ✓ Kms(x), per-roll ratios, reaction forces

## Data Collected So Far
All at ID=67, OD=172, N varies, H_pp=7.4, T=0.8, E=6.1, Nu=0.49

### Sinusoidal N-sweep (complete):
- Spider_N3_ID67_OD172_Sin_auto.csv ✓
- Spider_N5_ID67_OD172_Sin_auto.csv ✓
- Spider_N7_ID67_OD172_Sin_auto.csv ✓
- Spider_N9_ID67_OD172_Sin_auto.csv ✓

### ArcLines angle sweep N=7 (complete):
- Spider_N7_ID67_OD172_ArcLines_A30_auto.csv ✓
- Spider_N7_ID67_OD172_ArcLines_A45_auto.csv ✓
- Spider_N7_ID67_OD172_ArcLines_A60_auto.csv ✓

### SineLines angle sweep N=7 (complete):
- Spider_N7_ID67_OD172_SineLines_A30_auto.csv ✓
- Spider_N7_ID67_OD172_SineLines_A45_auto.csv ✓
- Spider_N7_ID67_OD172_SineLines_A60_auto.csv ✓

### COMSOL Arc+Lines N=7 (complete):
- Spider_N7_ID67_OD172_COMSOL_auto.csv ✓

### Circular Arc: NOT YET RUN (profile broken)

## CircularArc Problem — CRITICAL

**Root cause:** SW's `CreateSpline2` interpolates smoothly through all
points. Even with mathematically correct Z=0 at zero-crossings and Z=H_eff
at peaks, the spline adds S-curve curvature at the connections between
adjacent rolls. The profile looks like smooth S-curves instead of
distinct flat-top arcs with sharp zero-crossing transitions.

**What the profile should look like:**
- Each roll: one circular arc (dome/bowl shape)
- Zero-crossings: sharp angle transitions (NOT smooth S-curves)
- Adjacent rolls connect at exactly Z=0 with a visible corner

**The fix required:**
The spline approach cannot produce sharp corners at zero-crossings.
Instead use SEPARATE sketch entities per roll:
- Each roll = one spline arc (just the curved part)
- Between rolls = CreateLine at Z=0 connecting adjacent arc endpoints
- This produces the sharp corner at zero-crossings

**Implementation approach for next session:**
In `SWAutomation.vb CreatePart()`, detect ProfileType=1 (CircularArc)
and instead of one big spline, loop through rolls:
```
For each roll:
    CreateLine from previous arc end to this arc start (both at Z=0)
    CreateSpline for this arc only (40 points, Z=0 at both ends)
Next
```

The arc spline points come from `GeneratePoints_CircularArc()` which
is MATHEMATICALLY CORRECT — it's only the spline-smoothing that's wrong.
Extract per-roll arc points from the full point list (already have Z=0
markers to split on).

## Files — Current State
All in `C:\Projects\SpiderSWRunner\SpiderSWRunner\`:
- `Form1.vb` — main form, BuildProfile(), button handlers
- `Form1.Designer.vb` — UI layout, all controls including cbProfile, tbConnAngle, tbStraightLen
- `SWAutomation.vb` — SW COM automation, CreatePart uses single spline
- `SpiderProfile.vb` — profile generators for all types
- `SpiderGeometry.vb` (reference only, from SpiderDesigner) — in uploads

## UI Controls
- Profile combo: Sinusoidal / Circular Arc / Arc+Lines / Sine+Lines
- Angle°: connector angle for ArcLines/SineLines (30/45/60)
- SLen: StraightLength for ArcLines/SineLines (default 1.0mm)
- COMSOL checkbox: overrides profile to exact STEP geometry

## Remaining Runs Needed
1. Circular Arc N=3,5,7,9 (once CircularArc fixed)
2. Klippel spider dimensions (ID=39, OD=115, N=9, H_pp=3.0, T=0.25)
   for direct comparison with Klippel Kms(x) measurement

## Key Physics Results (for SpiderDesigner)
- x_break scales with N: ~6mm/roll (sinusoidal, H_pp=7.4)
- Connector angle has massive effect on Kms0 (8x range across 30-60°)
- COMSOL tapered profile 1.71x more compliant than uniform sinusoidal
- SineLines 60° ≈ sinusoidal in shape and stiffness
- N=7 COMSOL Kms0 = 0.0477 N/mm matches COMSOL reference to 1.4%
