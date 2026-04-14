# Bullet Profile — Handoff Document v2
Date: 2026-04-11

## What Works NOW

**Bullet (ProfileType=12) draws correctly in SolidWorks** using the tangent-to-flat construction with spline drawing. User confirmed: "The spline now matches your drawing."

### Working Implementation

**Geometry: Tangent-to-flat, three circular arcs**
- C1 = (0, R_id) — directly above inner flange endpoint → tangent to flat
- C2 = (Cx, H_eff − R_top) — crown center
- C3 = (span, R_od) — directly above outer flange endpoint → tangent to flat
- R_id = (Cx² + H_eff² − 2·H_eff·R_top) / (2·H_eff)  — closed form
- R_od = ((span−Cx)² + H_eff² − 2·H_eff·R_top) / (2·H_eff) — closed form
- External tangency: |C1-C2| = R_id + R_top, |C3-C2| = R_od + R_top
- Tangent points T12, T23 on center-to-center lines

**Drawing: Spline through sampled points**
- GeneratePoints_Bullet produces 93 points tracing the exact arc paths
- SWAutomation draws these as a spline (same method as sinusoidal profiles)
- SW's CreateArc/Create3PointArc APIs are unreliable for arc-side selection in SW2022 — do NOT use them for bullet arcs

**UI Parameters:**
- R_top (crown radius, mm) — user input via tbBulletRtop
- Cx (crown center X along span, mm; 0 = auto-center at span/2) — user input via tbBulletRid (repurposed)
- R_id, R_od — computed and displayed read-only in tbBulletRod
- Live validation in status label

### Test Parameters
- Component: Edge
- Profile: Bullet
- Cone OD: 100, Basket ID: 130
- H_pp: 5.0, T: 0.5, LipWidth: 3.0
- Rtop: 3.0, Cx: 0 (auto-centers at 5.5)
- Computed: R_id=4.30, R_od=4.30

### Files (all in C:\Projects\SpiderSWRunner\SpiderSWRunner\)
1. **SpiderProfile.vb** — BulletCx parameter, ComputeBulletRadii(), ValidateBulletParams(), GetBulletArcs() with tangent-to-flat construction, GeneratePoints_Bullet()
2. **SWAutomation.vb** — Bullet block uses spline through GeneratePoints_Bullet samples. Validation and arc geometry logged for diagnostics.
3. **Form1.vb** — BuildProfile reads BulletCx from tbBulletRid. UpdateComputed shows computed R_id/R_od. GeometryChanged handles bullet textbox events.
4. **Form1.Designer.vb** — Cx textbox (default "0"), Rtop textbox (default "3.0"), Rid/Rod read-only display.

## What Doesn't Work

- **CreateArc API** — SW2022 draws full circles or wrong arc side regardless of direction parameter. Do NOT use for bullet.
- **Create3PointArc API** — SW2022 does not respect the midpoint for arc-side selection on certain geometries (crown arc consistently wrong). Do NOT use for bullet.
- Both arc APIs work correctly for HalfRoll/DoubleRoll/CircularArc profiles — the issue is specific to the bullet geometry configuration.

## What Was NOT Changed

- Half Roll (ProfileType=10): fully working
- Double Roll (ProfileType=11): fully working  
- Spider mode: completely unaffected
- All spider calibration data: intact
- Filename builder: updated for Cx parameter

## Key Lessons Learned

1. SW2022's CreateArc direction parameter is unreliable for arcs where the center is not between the two endpoints (non-HalfRoll geometries)
2. Create3PointArc does NOT use the third point to select arc side — it appears to always sweep CCW from point1 to point2
3. Spline through sampled arc points is the reliable drawing method for complex multi-arc profiles
4. The tangent-to-flat construction gives a clean bullet dome shape with arcs tangent to the flanges
5. R_id and R_od are fully determined by R_top, Cx, and H_eff — no solver needed

## RunAnalysis err=18

The study creation sometimes fails with err=1 (duplicate study name) or RunAnalysis returns err=18 (mesh issue). Fix:
1. Right-click Part in study tree → Edit Definition → accept 0.5mm shell thickness
2. Right-click Mesh → Create Mesh
3. Click Mesh + Run again

## Future: Non-tangent-to-flat Bullet

If a non-tangent-to-flat bullet is needed later (arcs meet flanges at an angle), the geometry requires:
- User specifies R_id, R_top, R_od (three radii)
- External tangency with upper intersection for C1, C3
- The side arcs are slightly concave — this is geometrically correct for ogive construction
- Spline drawing handles this correctly
- The original parameterization (R_id, R_top, R_od all user-specified) works for this case
