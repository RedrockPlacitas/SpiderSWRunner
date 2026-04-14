# Bullet Profile — Handoff Document
Date: 2026-04-11

## The Requirement

A bullet-shaped surround cross-section defined by **three circular arcs**, all **mutually tangent to each other**, but **NOT tangent to the inner/outer flanges**. The arcs meet the flat flanges at whatever angle falls out of the geometry.

User specifies four parameters:
- **R_id** — inner arc radius (mm)
- **R_top** — crown arc radius (mm)
- **R_od** — outer arc radius (mm)
- **H_pp** — peak-to-peak height (mm), H_eff = H_pp/2

The geometry must work for asymmetric cases (R_id ≠ R_od).

## Visual Description

```
                    ___R_top___
                   /           \
             R_id /             \ R_od
                 /               \
    ──ID flat──/                   \──OD flat──
              ^                     ^
          angle ≠ 90°           angle ≠ 90°
          (not tangent           (not tangent
           to flat)               to flat)
```

Three distinct arcs visible in the cross-section:
1. Arc 1 (R_id): rises from the inner flat up to tangent point T12
2. Arc 2 (R_top): crown, from T12 over the top to T23
3. Arc 3 (R_od): descends from T23 down to the outer flat

At T12: Arc 1 and Arc 2 share a common tangent line (smooth transition).
At T23: Arc 2 and Arc 3 share a common tangent line (smooth transition).
At the flanges: the arcs meet the horizontal flat at an angle — there IS a kink here, and that's correct.

## What Works in the Current Codebase

- **Half Roll** (ProfileType=10): fully working, draws and simulates correctly
- **Spider mode**: completely unaffected by edge changes
- **Edge mode UI**: mode toggle, profile combo, all controls working
- **Filename builder**: includes all sweep parameters plus Nu
- **Nu sweep data**: 4 runs complete
- **All spider calibration data**: 24 CSV files complete

## What Doesn't Work

**Bullet (ProfileType=12)** fails to draw correctly in SolidWorks. Multiple approaches tried:

### Attempt 1: Three arcs tangent to flat AND to each other
- C1 directly above start point, C3 directly above end point (forces tangent-to-flat)
- R_top computed by bisection solver
- **Problem**: over-constrained. With R_id=8, R_od=5 on an 11mm span, R_top came out as 0.89mm — a tiny sharp peak. The tangent-to-flat constraint eats all the available space.

### Attempt 2: Two arcs only (dropped tangent-to-flat, dropped R_top)
- Two arcs meeting at crown with horizontal tangent
- R_od computed from R_id and span
- **Problem**: not a bullet shape — only two arcs, no distinct crown section. User correctly rejected this as not meeting the requirement.

### Attempt 3: Three arcs, equal takeoff angle α (numerical solver)
- Both side arcs leave flanges at angle α from vertical
- Bisect α until crown height matches H_eff
- **Problem**: solver failed — at many alpha values the circle-circle intersection for C2 doesn't exist. When it did work, CreateArc direction was wrong (full circles drawn instead of short arcs).

### Attempt 4: Deterministic placement (no solver)
- C2 placed at (span × R_id/(R_id+R_od), H_eff−R_top)
- C1 and C3 from circle-circle intersections
- **Problem**: the deterministic C2 placement doesn't produce the correct geometry. The resulting shape has wrong proportions and the arc directions via CreateArc continue to fail.

### Attempt 5: Create3PointArc instead of CreateArc
- Compute midpoint on each arc to avoid direction ambiguity
- **Problem**: midpoint computation via angle averaging was wrong for arcs that cross angle discontinuities. The arcs drew but in wrong positions — the geometry math was still producing incorrect center positions.

## Root Cause Analysis

Two separate problems have been conflated:

### Problem A: Geometry math (computing centers, tangent points)
The fundamental question: given R_id, R_top, R_od, H_eff, and span, where do the three arc centers go?

This is NOT a simple closed-form problem because:
- The takeoff angles at the flanges are unknown (that's the whole point — not tangent to flat)
- The crown position along the span is unknown (depends on the radius ratio)
- The height constraint couples everything

The correct approach is probably:
1. Parameterize C1 position by a single angle θ₁ (angle from vertical at ID endpoint)
2. C1 is at distance R_id from (0,0) at angle θ₁
3. From tangency constraint |C1-C2| = R_id+R_top, C2 lies on a circle around C1
4. From tangency constraint |C2-C3| = R_top+R_od, and C3 at distance R_od from (span,0)
5. The height constraint C2_z + R_top = H_eff fixes C2_z
6. This gives enough equations to solve for θ₁ (one unknown, one equation)

### Problem B: SolidWorks arc drawing
Even when geometry is correct, SW's CreateArc with a direction parameter (-1/+1) has been unreliable for arcs that aren't simple up/down domes from z=0. The cross product direction computation fails in edge cases.

**Solution for Problem B**: Use Create3PointArc(start, end, midpoint) exclusively. This requires computing a valid midpoint on each arc, which is straightforward once the geometry is correct — sample the arc at the halfway angle between start and end.

## Project File Locations

```
C:\Projects\SpiderSWRunner\SpiderSWRunner\   ← all source files
```

Files involved:
- **SpiderProfile.vb** — GeneralArc struct, GetBulletArcs(), GeneratePoints_Bullet(), ArcDirection()
- **SWAutomation.vb** — CreatePart bullet block (lines ~107-155), uses Create3PointArc
- **Form1.vb** — BuildProfile reads BulletR_ID, BulletR_Top, BulletR_OD; ProfileTypeFromCombo maps index 2→12
- **Form1.Designer.vb** — tbBulletRid, tbBulletRtop, tbBulletRod controls

## Test Parameters

Use these for verification:
- Component: Edge
- Profile: Bullet
- ID (Cone OD): 100
- OD (Basket ID): 130
- H_pp: 5.0
- T: 0.5
- R_id: 8
- R_top: 3
- R_od: 5
- LipWidth: 3.0

Span = (OD/2 - LipWidth) - (ID/2 + FilletR) = 62 - 51 = 11mm
H_eff = 2.5mm

## What to Upload at Start of Next Session

1. This handoff document
2. SpiderProfile.vb (current version)
3. SWAutomation.vb (current version)
4. Form1.vb (current version)
5. Form1.Designer.vb (current version)

## Key Principles

- Three arcs, mutually tangent, NOT tangent to flanges
- Use Create3PointArc in SW (start, end, midpoint) — never CreateArc with direction parameter for bullet
- Complete files only — never diffs or partials
- Do NOT break spider mode or Half Roll — those work correctly
- Circular arc ≠ sine wave — never use sine approximations
- Verify geometry visually in SW before attempting simulation
