# Semantic Mesh Tool: ML2 + Handoff Queue

Self-contained work queue for the next chat. The tracing tool and grid shader are
BUILT and compile clean (zero errors, confirmed in Editor 2026-07-20) on branch
`semantic-mesh-tool`. This doc is the plan for getting it onto the ML2 and into the
colleague's ArUco scene, in a form he will actually use.

## Where things stand

- Package lives entirely under `Assets/SemanticMesh/` (nothing of the colleague's
  was touched). Scripts, 2 shaders, 2 materials, `Resources/SurfacePalette.asset`.
- `Tools > Semantic Mesh > Surface Tracer` and `> Create Test Scene` work.
- Editor-verified: compiles, tool traces, planes generate. NOT device-verified,
  and NOT yet solved for scale alignment or terrain height (see below).

## Scene reality (investigated 2026-07-20) - read this before anything

The colleague's environment is NOT an FBX. It is a **Gaussian Splat "Digital Twin"**
(`Assets/Prefab/Digital Twin.prefab`, GsplatAsset) placed at:

- **scale 0.01** (1/100),
- **rotation 180 deg** (LocalRotation 0,-1,0,0; euler hint 180,0,180),
- anchored via **WorldLocking Tools + XR Rig + ArUco** in
  `Assets/EyeGaze/Assets/Scenes/Application.unity`.

The traceable FBX scans (BaselineGray*, Scan*) import at **scale 1**. So there is a
**100x scale gap and a 180 deg flip** between what the tracer can raycast (an FBX
collider) and what the colleague renders (the splat). This mismatch is almost
certainly the "took a long time to figure out" scaling pain.

You cannot put a MeshCollider on a Gaussian splat, so tracing must happen against an
FBX. Alignment therefore depends entirely on expressing surfaces in the twin's
coordinate frame, not world space.

## The handoff model (scale-proof, replaces the old "trace at identity" advice)

Deliverable to the colleague: ONE prefab plus the `Assets/SemanticMesh/` folder.
His integration: parent the prefab under his twin/anchor root at local identity, add
the occluder material to his wall mesh. Done. No scale tuning.

The rule that makes this work: **surfaces are authored in the LOCAL space of a chosen
"Surface Root", not in world space.** If the root is the twin's transform, surfaces
are stored in twin-local coordinates, so parenting the prefab under the colleague's
twin root (which carries the 0.01 scale + flip) reproduces the exact alignment at any
world scale. Scale and rotation are inherited, never re-tuned.

This requires the tool change in item 1 below. Until that lands, surfaces are
world-space and will fight the 0.01/flip twin.

## Queue (ordered, highest-value first)

### 1. Author surfaces in a Surface Root's local space (THE scale fix)
- Add a `Transform surface_root` field to `SurfaceTracerWindow`. Default null =
  current world-space behavior.
- When set, convert each raycast hit with `surface_root.InverseTransformPoint`, and
  parent the generated surface (and the "Semantic Surfaces" container) under
  `surface_root` at local identity. Store the mesh in that local frame.
- Thomas's flow: place an FBX scan as a child of / exactly overlapping the twin root,
  set Surface Root = twin root, trace. Surfaces come out in twin-local coords.
- Acceptance (edit-mode testable): with the FBX overlapping the twin, traced grids
  sit ON the twin. Re-scale/rotate the root and grids follow with zero drift.
- Device-test note: none for this item itself; alignment is visible in the Editor.

### 2. Drape: keep real click heights instead of flattening (THE height fix)
- In `build_surface`, stop projecting vertices onto the best-fit plane. Use the
  best-fit plane ONLY for the 2D projection that feeds triangulation + UVs; build the
  mesh vertices from the ORIGINAL raycast hit points (in root-local space per item 1).
- Result: each vertex sits exactly on the mesh; the low-poly plane drapes over
  terrain height. Ramps/stairs keep their real slope; more clicks = tighter fit.
- Keep normals recalculated from the draped triangles (`RecalculateNormals`).
- Acceptance: trace an undulating path; the grid hugs the surface, no floating.

### 3. "Save Surfaces as Prefab" button
- Button on `SurfaceTracerWindow` that saves the "Semantic Surfaces" container as a
  prefab (e.g. `Assets/SemanticMesh/Semantic_Environment.prefab`).
- Acceptance: dragging that prefab into an empty scene shows all traced grids in
  place, in the root-local frame from item 1.

### 4. "Add Occluder to whole environment" convenience
- Extend the existing per-selection occluder button with a "whole subtree under a
  chosen root" option, so the colleague adds it in one click, not by multi-select.
- Device-test note: on ML2, grids (and gems) behind a wall should not render.

### 5. Thomas self-test build (the gate, before any handoff)
- Minimal scene: twin (or the overlapping FBX) + surfaces prefab + occluder on the
  wall. Thomas builds and deploys (per guardrail, build/deploy is his).
- Verify on device: grid renders per surface; category colors correct; LOD sparser
  on far/building, denser on near/stairs; between-lines transparent; occlusion works.
- Do not hand off until this passes.
- Device-only risk: transparent sorting and the ML2 compositor's handling of the
  overlay are unverifiable off-device. Treat as high risk.

### 6. Tune LOD + palette on the real space scale
- On device, set `_Lod_Start` / `_Lod_End` / `_Lod_Coarsen` on `M_SemanticGrid` and
  per-category `cell_size` on `SurfacePalette`. Note: because surfaces live in
  twin-local space (0.01), `cell_size` and LOD distances are interpreted in that
  local scale unless the shader uses world position. The shader computes LOD from
  world distance (correct) but grid cells from local UV meters. Confirm cell density
  reads right at the twin's 0.01 scale; if cells look 100x off, that is the knob.

### 7. Colleague handoff doc
- Short doc: import `Assets/SemanticMesh/`, parent `Semantic_Environment.prefab`
  under the twin/anchor root at local identity, add occluder to the wall mesh. Explain
  the twin-local coordinate rule so he trusts why it aligns.

## Open questions for Thomas / colleague

1. **Is there a scene where an FBX scan is already co-registered (overlapping) with the
   Gsplat twin?** If yes, trace there. If no, Thomas must align an FBX onto the twin
   once (the painful step). This gates item 1's usefulness.
2. One surfaces prefab for the whole space, or several by region so they can toggle?
   Recommend several small regions (matches the per-patch tracing workflow) grouped
   under one container prefab.
3. Confirm the twin's transform is the anchor the colleague parents content under, so
   Surface Root = that transform is correct.

## Note on cell_size units after item 1

Once surfaces are twin-local (0.01 scale), a `cell_size` of 0.25 in local units is
0.0025 m in the real world. Either interpret `cell_size` in world meters in the shader
(divide UV by the object's world scale) or document that palette cell sizes are in
local units. Decide this during item 6 with the real twin in front of you.
