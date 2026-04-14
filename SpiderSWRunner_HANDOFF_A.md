# SpiderSWRunner — Handoff

## Project: SpiderSWRunner (WinForms / VB.NET / .NET 4.0)

**Purpose:** Standalone research tool that automates SolidWorks 2014 + Simulation Premium to create spider geometry, run nonlinear FEA, and extract per-roll displacement data for calibrating the SpiderDesigner analytic model.

**Machine:** Secondary workstation running VS2010, .NET Framework 4.0, SolidWorks 2014 + Simulation Premium.  
**Location:** `C:\Projects\SpiderSWRunner\`  
**Not shipping** — research/calibration tool only.

---

## Solution structure

```
SpiderSWRunner.sln
└── SpiderSWRunner\
    ├── SpiderSWRunner.vbproj
    ├── Program.vb
    ├── Form1.vb
    ├── Form1.Designer.vb
    ├── SpiderProfile.vb          ← geometry + point generation
    ├── SWAutomation.vb           ← all SolidWorks/CosmosWorks API calls
    └── My Project\AssemblyInfo.vb
```

---

## COM references

All from `C:\Program Files\SolidWorks Corp\SolidWorks\api\redist\`:
- `SolidWorks.Interop.sldworks.dll` (2,160 KB)
- `SolidWorks.Interop.swconst.dll` (335 KB)
- `SolidWorks.Interop.cosworks.dll` (293 KB)

---

## Current automation state

### Fully automated (steps 1–3):
1. **Connect** — `GetObject("", "SldWorks.Application")`
2. **Create Part** — sketch profile on Front Plane, spline corrugations, revolve 360° as surface
3. **Setup Study:**
   - Create nonlinear study via `CreateNewStudy3("SpiderNL", 6, 0, errCode)`
   - Pre-load shell thickness + material (values set but not committed by SW2014)
   - Fixed restraint on OD edge (scan body edges by radius, pass as dispatch array)
   - Prescribed displacement on ID edge (type 5 = Use Reference Geometry, Front Plane ref)
   - NonLinearStudyOptions: UseLargeDisplacement=ON, TimeIncrement=1/nSteps

### Manual (3 clicks in SolidWorks):
1. Right-click Part in study → Edit Definition → accept thickness (shows pre-loaded value) → checkmark
2. Right-click Part in study → Apply Material → Spider Materials → [material name]
3. Right-click Mesh → Create Mesh

### Automated (steps 4–5):
4. **Mesh + Run** — checks mesh exists (NodeCount > 0), calls `RunAnalysis()`
5. **Extract** — writes roll crest guide file (data extraction not yet automated)

---

## SW2014 API — confirmed working signatures

### CosmosWorks access chain
```
GetAddInObject("CosmosWorks.CosmosWorks")
  → CwAddincallback (cast)
    → .CosmosWorks property
      → CWModelDoc = .ActiveDoc
        → CWStudyManager = .StudyManager
          → .CreateNewStudy3(name, studyType=6, meshType=0, ByRef errCode)
          → .ActiveStudy = index (integer, not object)
          → CWStudy = .GetStudy(index)
```

### Restraints (CWLoadsAndRestraintsManager)
```vb
' AddRestraint signature (4 args):
lrMgr.AddRestraint(type As Integer, DispArray As Object, RefGeom As Object, ByRef ErrorCode As Integer) As CWRestraint

' Fixed: type = swsRestraintTypeFixed enum
' Prescribed displacement: type = 5 (Use Reference Geometry)
'   DispArray = Object() containing edge dispatch objects
'   RefGeom = Feature object (e.g. Front Plane)

' SetTranslationComponentsValues (6 args):
restraint.SetTranslationComponentsValues(BVal1, BVal2, BVal3, DVal1, DVal2, DVal3)
'   BVal = 0/1 (off/on per axis), DVal = displacement in meters

' RestraintEndEdit takes NO args, returns Integer
```

### Study options
```vb
' Property name: NonLinearStudyOptions (not StudyOptions)
Dim opts As Object = CallByName(study, "NonLinearStudyOptions", CallType.Get)
CallByName(opts, "UseLargeDisplacement", CallType.Set, 1)
CallByName(opts, "IncrementEndTimeValue", CallType.Set, 1.0)
CallByName(opts, "TimeIncrement", CallType.Set, 0.05)  ' = 1/20 steps
' Note: "AutoTimeStep" does NOT exist on this object
```

### Shell (CWShell) — KNOWN LIMITATION
```vb
' ShellThickness, ShellUnit, Formulation are settable via CallByName
' BUT SW2014 does not commit shell definition without GUI interaction
' ShellEndEdit() returns 0 (success) but SW still shows "Thickness: not defined"
' EntityCount shows correct values, State reads back correctly
' Material via SetLibraryMaterial("Spider Materials", "Rubber") — also not committed
' THIS IS A SW2014 API BUG/LIMITATION — user must click Edit Definition checkmark
```

### Mesh (CWMesh) — KNOWN LIMITATION
```vb
' CreateMesh is NOT exposed via .NET interop in SW2014
' ElementSize, Tolerance, Quality are read-only in interop (settable via CallByName for Quality only)
' RunAnalysis does NOT auto-mesh (returns err=18 without mesh)
' User must right-click Mesh → Create Mesh manually
```

### Key rules
- Use `CallByName` (not `Type.InvokeMember`) for all COM late binding — `InvokeMember` fails on `System.__ComObject`
- `DirectCast(comObj, Object)` bypasses interop read-only restrictions for CallByName
- Edge selection: scan body edges via `Body2.GetEdges()`, match by `Curve.CircleParams(6)` radius
- Pass edges to AddRestraint as `Object()` array, not via selection manager marks

---

## GUI

### Layout
- **Geometry group:** ID, OD, N, Lip, H_pp, T, First roll combo, computed EffPitch/Aspect/MaxH_pp
- **Material group:** Preset combo (Rubber/Cotton Cloth/Nomex) auto-fills E/Nu/Density
- **Simulation group:** Max disp, Load steps, Output folder
- **Buttons row 1:** Connect, Create Part, Setup Study, Mesh+Run, Extract
- **Buttons row 2:** Export Profile CSV, Clear Log, Setup (1-3)
- **Log:** dark console-style multiline textbox

### Material presets (must match SW Custom Materials library)
| Name | E (N/mm²) | Nu | Density (kg/m³) |
|---|---|---|---|
| Rubber | 6.1 | 0.49 | 1000 |
| Cotton Cloth | 3000 | 0.30 | 1200 |
| Nomex | 8000 | 0.28 | 1100 |

Category in SW: "Spider Materials" under Custom Materials.

---

## Default geometry (COMSOL rubber validation case)

```
ID=67, OD=172, N=7, T=0.8mm, H_pp=7.4mm
E=6.1 MPa, Nu=0.49, Density=1000 kg/m³
Profile: Sinusoidal (only profile implemented in this tool)
LipWidth=5.0, FilletR=2×T=1.6mm
EffPitch=6.557, AspectRatio=0.564
Roll crests (r): 38.38, 44.94, 51.49, 58.05, 64.61, 71.16, 77.72 mm
```

---

## Pending work

### Next session priorities
- [ ] **Automate data extraction** — reaction force at each time step (F(x) curve), per-roll crest Z displacement at each step
- [ ] Verify extracted Kms(x) matches previous manual SolidWorks run
- [ ] Run N-sweep: same geometry with N=3,5,7,9 — validates Kms ∝ N (parallel assembly model)
- [ ] Run Nu-sweep: same geometry with Nu=0.30 vs 0.49 — tests inner-roll-first hypothesis

### Future improvements
- [ ] Investigate shell definition commit — possible via SolidWorks macro (.swp) run from API
- [ ] Investigate mesh creation via macro
- [ ] Add profile types beyond sinusoidal (Arc+Lines, etc.)
- [ ] Batch run mode: queue multiple geometry/material combinations
- [ ] Auto-save part files with parametric naming

---

## Files to upload to new chat

```
SpiderSWRunner.vbproj
Program.vb
Form1.vb
Form1.Designer.vb
SpiderProfile.vb
SWAutomation.vb
My Project\AssemblyInfo.vb
```

**Also paste this HANDOFF.md as first message.**
