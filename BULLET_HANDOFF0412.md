# SpiderSWRunner — Bullet Profile Sketch Construction Handoff

## Status
**Broken.** Sketch almost works but 3 of 4 point merges fail in the COM
automation, leaving arcs floating. The geometry construction recipe itself
is validated — the user built it successfully by hand in SolidWorks before
writing out the step-by-step instructions below.

---

## USER'S VERBATIM CONSTRUCTION INSTRUCTIONS (AUTHORITATIVE)

The user wrote these steps after manually building the bullet sketch
successfully in SolidWorks. **This recipe is final. Do not re-interpret,
re-order, or substitute geometry.** Reproduce it exactly in VB.NET via
the SolidWorks COM API.

```
We are going to start this over. I created a 3 arc bullet drawing as follows.
Make sure to note the changes to GUI.

Draw rectangle for construction
    bottom corner 0,0
    Height = H_pp
    Width = (Basket ID - Cone OD) / 2
Add constraints: vertical lines vertical, horizontal lines horizontal

Draw 3 point arc (Arc 2) from:
    x = width/3   to x = width/3*2,  and y = H_pp/3*2 for both
    3rd point at x = width/2 and y = Height
Note that these points are not fixed — only starting points for Arc 2.
(Let's define left arc point for Arc 2 = T1 and right arc point = T2
 for simplicity.)

Make this arc and top of construction line tangent.

Draw tangent arc from 0,0 to T1
Draw tangent arc from width,0 to T2

Make Arc 1 tangent to vertical construction line at 0,0
Make Arc 3 tangent to vertical construction line at width

Make Arc 1 tangent to Arc 2
Make Arc 3 tangent to Arc 2

Create a dimension between vertical line at 0,0 and center of Arc 2 = Cx
Create a radius dimension for Arc 2 = R2 Radius  (new value on GUI)

Create a horizontal line from 0,0 to -InnerLipWidth, 0
    (InnerLipWidth: new value on GUI for all edge types)
    Make this line constrained horizontal.

Create a line from width,0 to Lip+Width, 0
    Make this line constrained horizontal.

This will create a true bullet shape with three arcs, and has the ability
to choose the center point of Arc 2 for asymmetrical designs.

Note that this Cx value is limited to 1/3 width to 1/3 width * 2.
```

### User's clarification on tangent arcs
> "You can draw them as tangent arcs and then eliminate the step of make
> them tangent to the vertical construction lines (Duplicate instructions)"

So Arcs 1 and 3 should be drawn with SW's **Tangent Arc** tool, which
auto-tangents to the entity at the starting endpoint. If that's used, the
explicit "Make Arc 1 tangent to vertical construction line" step becomes
redundant and can be dropped. Regular 3-point arcs + explicit tangent
relations is the fallback if Tangent Arc tool selection is unreliable.

### User's earlier hard constraints (prior sessions)
- Sine wave ≠ circular arc. Never substitute sine for arc as a fallback.
- Complete files only, never diffs or partials.
- Full destination path on the same line as every filename.
- No preamble, no step-by-step commentary unless asked.
- Follow instructions literally. Don't reinterpret geometry.

---

## GUI changes required

No new form controls. Repurpose existing bullet textboxes:

| Control | Old meaning | New meaning |
|---|---|---|
| `tbBulletRid`  | Cx (reused) | **Cx** — distance from ID vertical to Arc 2 center |
| `tbBulletRtop` | R_top crown radius | **R2** — Arc 2 radius |
| `tbBulletRod`  | computed-radii display | **InnerLipWidth** — visible for ALL edge types (HalfRoll=10, DoubleRoll=11, Bullet=12) |

The `tbBulletRod` textbox was left as ReadOnly=True from the old
computed-radii display mode. Must force `ReadOnly = False` at runtime when
shown, or the user can't type into it.

Cx is constrained: `width/3 ≤ Cx ≤ 2·width/3`, where `width = (OD−ID)/2`.

---

## Current state of the code

### SpiderProfile.vb
- Added field `Public InnerLipWidth As Double = 5.0`
- `BulletR_Top` field reused as R2 (comment updated)
- `BulletCx` field reused as Cx
- `BulletWidth` readonly property returns `(OD − ID) / 2`
- `GetBulletCx()` method: returns `BulletCx` or `BulletWidth/2` if 0
- `ComputeBulletRadii(ByRef, ByRef)` legacy stub — both out params = BulletR_Top
- `ValidateBulletParams()` checks width, H_pp, R2, and Cx in [w/3, 2w/3]
- `GetBulletArcs()` returns a best-effort 3-arc approximation using seed
  geometry projection onto Arc 2's circle — for CSV exports and fallback
  spline drawing only. Does NOT drive the SW sketch.
- `GeneratePoints_Bullet()` generates profile preview points from the
  approximation. Profile absolute r spans `[R_inner − InnerLipWidth − 5,
  R_inner + width + LipWidth + 5]`.
- `GetRollCrestRadii()` for ProfileType=12 returns `R_inner + GetBulletCx()`
- `Summary()` outputs `Bullet: width=...  H_pp=...  R2=...  Cx=...  InnerLip=...`

### Form1.vb
- `BuildProfile()` reads `tbBulletRid → BulletCx`, `tbBulletRtop → BulletR_Top`,
  `tbBulletRod → InnerLipWidth`
- `UpdateEdgeProfileControls()`:
  - `tbBulletRid`, `tbBulletRtop` visible only for bullet (pt=12)
  - `tbBulletRod` visible for all edge types (pt=10, 11, 12)
  - Label text set to "InnerLip", textbox `ReadOnly = False`, default "5.0"
- `UpdateComputed()` bullet status shows the new parameter summary
- Filename uses `_R2...` `_Cx...` pattern

### SWAutomation.vb — bullet block flow (current, broken)
```
1.  Validate bullet params
2.  Set SketchManager.AddToDB = True  (supposed to disable proximity-snap)
3.  Save original AddToDB
4.  Compute meter-unit constants (w, h, r2, cx, innerLip, outerLip)
5.  Compute rectangle edge midpoints at 40% along length
    (avoids midpoint-snap collisions)
6.  Create 4 rectangle lines (botLine, rightLine, topLine, leftLine)
7.  Mark all rectangle lines as construction
8.  Add H/V relations via coordinate-based SelectByID2 at 40% mid points
9.  Fix bottom corners (rOff, 0) and (rOff+w, 0) via sgFIXED
10. Add driving dimensions for rectangle WIDTH (on botLine) and HEIGHT
    (on leftLine) so tangents can't deform the rectangle
11. Create Arc 2 with seed endpoints (seedT1x, seedT1z), (seedT2x, seedT2z),
    midpoint (seedMidX, seedMidZ) where seedMidZ = 0.95h (slightly below
    top line to avoid ambiguous selection)
    *** No constraints on Arc 2 yet — need valid seed coords for later ***
12. Create Arc 1 with OFFSET seed endpoints:
    arc1StartX = w*0.05, arc1StartZ = h*0.02   (offset from corner 0,0)
    arc1EndX   = seedT1x - w*0.02              (offset from Arc 2 start)
    arc1EndZ   = seedT1z - h*0.02
    arc1MidX   = seedT1x * 0.5
    arc1MidZ   = seedT1z * 0.7
13. Merge Arc1.start → (rOff, 0) corner       [FAILS: True/False]
14. Merge Arc1.end   → Arc2.start seed        [FAILS: True/False]
15. Create Arc 3 with mirrored offset seeds
16. Merge Arc3.end   → (rOff+w, 0) corner     [FAILS: True/False]
17. Merge Arc3.start → Arc2.end seed          [WORKS: True/True]
18. Apply tangent Arc2 ↔ topLine              [WORKS]
19. Apply tangent Arc1 ↔ leftLine             [applied but meaningless — Arc 1 is floating]
20. Apply tangent Arc1 ↔ Arc2                 [applied but meaningless]
21. Apply tangent Arc3 ↔ rightLine            [applied but meaningless]
22. Apply tangent Arc3 ↔ Arc2                 [applied but meaningless]
23. Dimension R2 on Arc 2                     [works]
24. Dimension Cx (leftLine ↔ Arc 2 center)    [works]
25. Inner lip line, outer lip line
26. Restore SketchManager.AddToDB = addToDbOrig
```

---

## Merge selection code pattern

For every point merge, the code does:
```vb
_model.ClearSelection2(True)
Dim s1 As Boolean = _model.Extension.SelectByID2("", "SKETCHPOINT", _
    rOff + arcEndpointX, arcEndpointZ, 0, False, 0, Nothing, 0)
Dim s2 As Boolean = _model.Extension.SelectByID2("", "SKETCHPOINT", _
    rOff + targetX, targetZ, 0, True, 0, Nothing, 0)
Log(String.Format("  Arc? label: {0}/{1}", s1, s2))
If s1 AndAlso s2 Then
    _model.SketchAddConstraints("sgMERGEPOINTS")
End If
_model.ClearSelection2(True)
```

The first select at the arc's own offset seed always succeeds.
The second select (append) fails for 3 of 4 merges.

---

## Latest log (current failing state)

```
── Bullet geometry (3-arc via sketch relations) ──
  AddToDB enabled (auto-snap disabled)
  width=10.00mm  H_pp=10.00mm  R2=3.00mm  Cx=5.00mm  InnerLip=2.00mm
  rect H/V constraints applied
  Rectangle height dim = 10.00mm
  Rectangle width dim = 10.00mm
  Rectangle built (H/V + fixed + dimensioned)
  Arc 2 created
  Arc1 start/corner select: True/False       ← FAIL
  Arc1end/Arc2start select: True/False       ← FAIL
  Arc3 end/corner select: True/False         ← FAIL
  Arc3start/Arc2end select: True/True        ← only this one works
  Arc 3 start merged with Arc 2 end
  Arc2/Top select: True/True
  Arc 2 tangent to top line applied
  Arc 1 tangent to left vertical
  Arc 1 tangent to Arc 2
  Arc 3 tangent to right vertical
  Arc 3 tangent to Arc 2
  R2 select on arc: True
  R2 dimension set to 3.00mm
  Cx dimension set to 5.00mm
  Inner + outer lip lines drawn
  Bullet sketch complete
```

### Failure pattern

| Merge | When | Target entity | Result |
|---|---|---|---|
| Arc1.start → corner (0,0)   | 1st | rect corner     | True/**False** |
| Arc1.end → Arc2.start       | 2nd | Arc 2 endpoint  | True/**False** |
| Arc3.end → corner (w,0)     | 3rd | rect corner     | True/**False** |
| Arc3.start → Arc2.end       | 4th | Arc 2 endpoint  | **True/True** ✓ |

The ONE working merge is the LAST one attempted.

---

## What's been ruled out

- **Select4 on segment refs** — unreliable, returned False silently
- **Name-based selection (GetName)** — `GetName()` returns empty strings
  on SketchSegment in SW2022 for this user
- **Coordinate collisions with top line** — fixed by moving Arc 2 apex
  seed to 0.95h (below top line)
- **Midpoint-snap collisions on rect lines** — fixed by moving rect line
  selections to 40% along length instead of 50%
- **Auto-merge during Arc 1 creation** — `SketchManager.AddToDB = True`
  set but selection still fails, suggesting proximity-snap was not the
  root cause OR AddToDB doesn't fully prevent it in this SW version
- **Reflection `Select4` on SketchPoint via InvokeMember** — always
  returns False
- **Dimension order (tangent-before-R2 vs R2-before-tangent)** — neither
  order fixed the merge problem
- **EditRebuild3() between steps** — made things worse, shifted coords
  and broke later selections

---

## Strongest hypotheses for why 3 of 4 merges fail

1. **State/timing.** The ONLY merge that works is the LAST one, after
   three failed attempts. Something about the first 3 failures may be
   putting SW into a state where the 4th succeeds. Could be a forced
   solve on error recovery. Retry loops are the obvious test.

2. **Rectangle corner points aren't selectable after sgFIXED.** The
   fixed constraint applied at (rOff, 0) and (rOff+w, 0) may have made
   those sketch points non-selectable by coordinate, explaining both
   corner-merge failures. The Arc2 endpoint at seedT2x/seedT2z (working)
   is NOT fixed — supports this hypothesis. But then why does
   Arc1end→Arc2start fail when Arc2start also isn't fixed? Different
   coordinates, same entity type, different result.

3. **`append=True` doesn't do what we think it does in SelectByID2.**
   The second SelectByID2 call with append=True may be deselecting
   rather than appending, or may be looking for the entity in a
   non-existent selection slot.

4. **Sketch points at rectangle corners don't exist as SKETCHPOINT
   entities.** The rectangle lines share endpoints as merge-points,
   but SW may not expose them as individually selectable points at
   those coordinates. The first SelectByID2 on the rectangle corner
   might succeed because it picks up something else nearby (the origin?),
   not the actual corner point.

---

## What to try next (ordered by likelihood of fix)

### 1. Retry failed merges (hypothesis 1)
Wrap each merge in a loop:
```vb
For attempt As Integer = 1 To 3
    ClearSelection
    s1 = SelectByID2(arcEndpoint...)
    s2 = SelectByID2(target...)
    If s1 AndAlso s2 Then
        SketchAddConstraints("sgMERGEPOINTS")
        Exit For
    End If
    ' Force a solve between attempts
    _model.EditRebuild3()
Next
```

### 2. Create explicit standalone sketch points at corners (hypothesis 4)
Before the rectangle, use `SketchManager.CreateSketchPoint` or
`CreatePoint` to create named selectable sketch points at (rOff, 0)
and (rOff+w, 0). Those become the anchors for Arc 1 / Arc 3 merges.
Skip the "fix bottom corners of rectangle" step; instead merge the
rectangle corners TO the standalone points (or fix the standalone
points directly).

### 3. Try EXTSKETCHPOINT (external sketch point) selection type
For the target of each merge, use
`SelectByID2("", "EXTSKETCHPOINT", x, y, z, True, 0, Nothing, 0)`
instead of `SKETCHPOINT`. External sketch points are picked up by some
SW API paths when regular sketch points aren't.

### 4. Use SketchManager.CreateTangentArc for Arcs 1 and 3
Per user's clarification, tangent arcs eliminate the vertical-tangent
step. `CreateTangentArc(x1, y1, z1, x2, y2, z2, swTangentArcTypes_e.
swForward)` — needs the starting endpoint to be on an existing segment.
If you draw a short line from (0,0) upward along the left vertical
first, you can start the tangent arc from that line's endpoint.
Trickier, but avoids the merge-to-corner selection problem entirely
because the tangent arc auto-connects.

### 5. Bigger seed offsets
Current offset between Arc 1 end and Arc 2 start seeds is 0.2mm
(w*0.02 and h*0.02). If AddToDB=True isn't actually preventing
proximity-snap in this SW version, try 10% instead of 2% — or make
the arcs start/end at clearly distinct positions like (w*0.4, h*0.3)
and (w*0.45, h*0.5).

### 6. New sketch feature
The existing sketch already contains a centerline from earlier in
`CreatePart` (for the revolve axis). Create a NEW sketch feature
specifically for the bullet cross-section, isolated from the
centerline. Merge the resulting sketch with the revolve axis sketch
afterward, or add the centerline to the new sketch.

### 7. Try `ISketchRelationManager::AddRelation` instead of selection
`ISketchRelationManager::AddRelation` takes an **array of entities**
directly, bypassing selection state entirely. Use
`Arc1.GetStartPoint2()` and an explicit object reference for the
rectangle corner, pass them both in an object array to `AddRelation`.

---

## Files in this handoff

| File | State |
|---|---|
| `SWAutomation.vb` | Current broken state, has AddToDB attempt |
| `SpiderProfile.vb` | Has BulletWidth, GetBulletCx, ValidateBulletParams, BulletR_Top (=R2), BulletCx, InnerLipWidth fields |
| `Form1.vb` | tbBulletRod repurposed as InnerLipWidth, forced writable, filename uses _R2/_Cx |
| `BULLET_HANDOFF.md` | This document |

## Destination paths (user's system)
- `C:\Projects\SpiderSWRunner\SpiderSWRunner\SWAutomation.vb`
- `C:\Projects\SpiderSWRunner\SpiderSWRunner\SpiderProfile.vb`
- `C:\Projects\SpiderSWRunner\SpiderSWRunner\Form1.vb`

## Tools / environment
- SolidWorks 2022 (22.4.0), COM API via VB.NET interop
- CosmosWorks for FEA
- Front Plane sketch, revolve around Y axis
- All API distances in meters (mm / 1000)
