# Semantic Mesh Tool: ML2 + Handoff Queue

> **Use-facing guide: `SEMANTIC_MESH_GUIDE.md`** (how to author + exactly what the
> colleague does). This file is the build plan / acceptance record; the guide is the
> manual. Items 1-6 built 2026-07-20.

Self-contained work plan for a fresh chat. The tracing tool and grid shader are
BUILT and compile clean (Editor 2026-07-20) on branch `semantic-mesh-tool`
(1 commit). This doc is the single entry point: point the next chat at it. Do NOT
commit without Thomas asking.

## Where things stand

- Package under `Assets/SemanticMesh/` (nothing of the colleague's touched):
  scripts, 2 shaders, 2 materials, `Resources/SurfacePalette.asset`.
- `Tools > Semantic Mesh > Surface Tracer` and `> Create Test Scene` work.
- Editor-verified: compiles, traces, generates planes. NOT device-verified. Two
  known gaps (scale alignment, terrain height) plus authoring ergonomics (holes,
  visibility) are specced below.

## Scene reality (investigated 2026-07-20) - read before touching anything

- Colleague's environment is a **Gaussian Splat "Digital Twin"**
  (`Assets/Prefab/Digital Twin.prefab`, GsplatAsset) at **scale 0.01** and
  **rotation 180 deg** (LocalRotation 0,-1,0,0; euler hint 180,0,180), anchored via
  **WorldLocking + XR Rig + ArUco** in `Assets/EyeGaze/Assets/Scenes/Application.unity`.
- Traceable FBX/OBJ/PLY scans import at **scale 1**. So there is a **100x scale gap
  and a 180 deg flip** between what the tracer can raycast (an FBX collider) and what
  the colleague renders (the splat). This is the scaling pain.
- You cannot put a MeshCollider on a Gaussian splat, so tracing must use an FBX/PLY.

## Color / texture finding (why it is hard to see what you trace)

The scan meshes carry **NO color and NO texture**: `1st Building 3.obj` verts are bare
XYZ, and the LCC `.ply` meshes have only x/y/z + faces, no color properties. There is
nothing to "put a texture back on"; the meshes never had one. The photoreal color that
distinguishes grass from concrete exists **only in the Gaussian Splat**. So visibility
while tracing depends on rendering the splat as a visual reference over the (invisible,
collider-only) trace mesh. See the Alignment + Visibility section.

The scan mesh also has **large holes** in places (missing geometry). Raycasts miss over
holes, so authoring needs a way to place points across a gap. See item 3.

## Handoff model (scale-proof)

Deliverable: ONE prefab + the `Assets/SemanticMesh/` folder. Colleague parents the
prefab under his twin/anchor root at local identity and adds the occluder to his wall
mesh. No scale tuning. This works only if surfaces are authored in the twin's LOCAL
frame (item 1), so parenting under the twin reproduces alignment at any world scale.

## Queue: buildable NOW (all independent of the open questions)

Everything here can be built and edit-mode tested without resolving co-registration.
Do not commit; Thomas tests in the Editor between/after.

> **STATUS 2026-07-20: items 1-6 BUILT (edit-mode, NOT device-verified).** All six
> land in `Editor/SurfaceTracerWindow.cs`; nothing else changed. Compiles in my head
> only. Edit-mode acceptance checks Thomas should walk:
> - **1 Surface Root:** assign the twin transform to the new "Surface Root" field,
>   trace on a mesh parented under it, then scale/rotate the root: grids move with it,
>   zero drift. Empty field = old world-space behavior.
> - **2 Drape:** trace an undulating path; every vertex hugs the surface, no floating
>   flat plane. (Was: flattened onto best-fit plane.)
> - **3 Hole bridging:** trace across a hole; ray-miss points drop onto a working plane
>   and draw ORANGE (on-mesh points are yellow). Region closes across the gap.
> - **4 Draggable points:** during tracing, drag any placed point; it re-snaps to the
>   mesh (or the working plane over a hole). Grabbing a handle never drops a stray point.
> - **5 Save prefab:** two buttons. "Save Surfaces as ONE Prefab" writes the whole container
>   to `Assets/SemanticMesh/Semantic_Environment.prefab`. "Save a Prefab PER Surface Type"
>   writes one prefab per type (`Semantic_Walkable.prefab`, `Semantic_Grass.prefab`, ...) by
>   cloning, leaving the working scene untouched. Drag either into an empty scene: grids sit
>   in the root-local frame.
> - **6 Occluder subtree:** the occluder button already walks the whole selected subtree
>   (now includes inactive renderers). This one's real acceptance is DEVICE-ONLY (grids
>   behind a wall stop rendering).
>
> **Device-only vs edit-mode:** items 1-5 are fully edit-mode testable. Item 6's occlusion
> effect is device-only (depth-write look). The alignment free-path overlay (below) is an
> Editor VISUAL check only Thomas can run: I can't render Unity or see the splat here.

### 1. Author surfaces in a Surface Root's local space (scale fix)
- Add `[SerializeField] Transform surface_root;` to `SurfaceTracerWindow`, exposed as
  an ObjectField in `OnGUI`. Null = current world-space behavior (keep as fallback).
- In `build_surface`, when `surface_root` is set: transform each world raycast hit by
  `surface_root.InverseTransformPoint` FIRST, then do all centroid / Newell-normal /
  projection math in root-local space. Create the surface GameObject and the
  "Semantic Surfaces" container as children of `surface_root` with localRotation
  identity and localScale one; localPosition = local centroid. Mesh vertices are
  local to that centroid.
- Acceptance (edit-mode): with the trace mesh sitting under the twin root, traced grids
  land ON the mesh; scaling/rotating the root moves grids with zero drift.

### 2. Drape: keep real click heights instead of flattening (height fix)
- In `build_surface`, use the best-fit plane ONLY to get axis_u/axis_v for the 2D
  projection that feeds triangulation + UVs. Build mesh vertices from the ORIGINAL
  root-local hit points (minus local centroid), NOT the projected-flat points.
- `mesh.RecalculateNormals()` after setting triangles so draped normals are correct.
- Result: every vertex sits on the mesh; the plane drapes over terrain. Ramps/stairs
  keep real slope; more clicks = tighter fit.
- Acceptance: trace an undulating path; grid hugs the surface, no floating.

### 3. Hole bridging (place points over gaps)
- In `OnSceneGui`, when the raycast MISSES and at least one point exists, intersect the
  mouse ray with a "working plane" and use that as the hover/placed point:
  - working plane = best-fit plane of already-placed points if >= 3, else a horizontal
    plane through the last placed point.
- Tag inferred (off-mesh) points so drape (item 2) uses the plane height for them and
  the real mesh height for on-mesh points. Draw inferred points in a distinct color.
- Acceptance: can bridge a hole and close a clean region across it.

### 4. Draggable point editing during tracing
- Give each placed point a `Handles.FreeMoveHandle`/position handle while tracing. On
  drag, re-raycast to snap to the mesh; if it misses, fall back to the working plane
  (item 3). Update the preview live.
- (Stretch, note only) post-finalize editing that regenerates the mesh: skip for now.
- Acceptance: nudge a mis-placed point before closing; shape follows.

### 5. Save Surfaces as Prefab
- Button in `SurfaceTracerWindow` calling `PrefabUtility.SaveAsPrefabAsset` on the
  "Semantic Surfaces" container to e.g. `Assets/SemanticMesh/Semantic_Environment.prefab`.
- Acceptance: dragging the prefab into an empty scene shows all grids in place, in the
  root-local frame from item 1.

### 6. Occluder to a whole subtree
- Extend the occluder button with a "root" mode: add `M_DepthOccluder` to every
  MeshRenderer under a chosen root in one click.
- Acceptance (device): grids and gems behind a wall stop rendering.

## Alignment + Visibility (do the free path first, escalate only if needed)

Goal: trace against the invisible FBX collider while SEEING the photoreal splat, so
grass vs concrete is obvious, and so surfaces come out in the twin frame.

> **ONE-CLICK SETUP (2026-07-20):** `Tools > Semantic Mesh > Build Alignment Test Scene`
> builds the whole free-path scene: instantiates `Digital Twin`, parents `1st Building 3.obj`
> under it at local identity (inherits the 0.01 scale + flip), adds MeshCollider(s) to the
> scan, frames the twin, and opens the tracer. A dialog walks the overlay check and the
> two-step trace. Scan renderers are left ON so the overlay can be eyeballed; uncheck the
> scan's Mesh Renderer to trace against the invisible collider while seeing the splat.
> If they do NOT overlay, nudge the scan's Transform (fallback 2 below). Save the scene when set.

1. **Free path (try first):** the FBX/OBJ/PLY scan and the splat likely share the LCC
   pipeline's coordinate frame. Parent the trace mesh under the twin root at local
   identity (inherits 0.01 + flip), disable the trace mesh's MeshRenderer (keep its
   MeshCollider), keep the splat visible. If they came from the same scan they overlay
   for free, no math. Verify in the Editor.
2. **Manual nudge (fallback):** if they do not overlay, adjust the trace mesh's
   Transform (position / rotation / uniform scale) with a standard gizmo while the splat
   is visible, eyeballed with orbit. Always works, ~2 minutes.
3. **3-point solver (optional stretch):** a correspondence solver is nice but the
   splat-side points are hard to pick (no collider to raycast, so depth must be dragged
   by hand with orbit parallax). Only build if 1 and 2 prove insufficient.

Interim if the splat cannot be shown at all: shade the bare trace mesh by slope/height
so stairs/ramps/flat read differently (does NOT show grass vs concrete).

## Blocked until Thomas/colleague answers (content, not tool)

- The FINAL authoring pass: click the real regions on the aligned twin and hand over the
  exact `Semantic_Environment.prefab`. This is inherently Thomas's (or a joint) step and
  only needs the tool above plus the alignment sorted.

## Open questions for Thomas / colleague

1. Do the scan mesh and the splat share a coordinate frame (same LCC scan)? If yes,
   alignment is the free path above. Editor test settles it.
2. One prefab for the whole space, or several regions grouped under one container so
   they can toggle? Recommend several small regions under one container prefab.
3. Confirm the twin's transform is the anchor root the colleague parents content under
   (so Surface Root = that transform is correct).

## cell_size units caveat (revisit during device tuning)

Once surfaces are twin-local (0.01 scale), a `cell_size` of 0.25 local units is 0.0025 m
in the real world. The shader computes LOD distance from WORLD position (correct) but
grid cells from local UV meters. Either divide UV by the object's world scale in the
shader to keep cells in world meters, or document that palette cell sizes are local
units. Decide with the real twin in front of you.
