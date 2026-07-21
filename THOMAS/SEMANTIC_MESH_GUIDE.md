# Semantic Mesh Tool: Full Guide (Use, Concepts, Handoff, Roadmap)

This is the complete manual for the Semantic Mesh tool. It is written to be read by
a person OR by Claude Code in a fresh session: it is deliberately thorough, so a new
session can pick up the tool, understand every moving part, use it, and extend it
without re-deriving anything. If you are Claude Code reading this cold, read it top
to bottom before touching code; the "Known issues" and "Roadmap" sections at the end
are the current work queue.

Companion doc: `ML2_HANDOFF_QUEUE.md` is the build plan and per-feature acceptance
record. This guide is the how-to-use-and-extend manual.

Everything ships under `Assets/SemanticMesh/`. Nothing outside that folder is
touched by the tool.

**If you just want to finish and ship it, go straight to section 11 (the runbook).**
Sections 0-10 explain the tool; section 11 is the ordered list of exactly what to do.

---

## 0. Why this exists (the pitch)

The environment is a photoreal Gaussian Splat "Digital Twin" of a real space. For
the study we need the space LABELED: which areas are walkable, which are stairs,
grass, buildings, hazards, obstacles. The splat has no such labels, and you cannot
put a collider on it.

The naive approach is to hand-place line segments or primitives all over the scene
to mark zones. That is slow, imprecise, does not follow terrain, does not carry
meaning (a line is just a line), and is miserable to edit.

This tool replaces that. You TRACE a region by clicking its corners on a scan mesh.
The tool builds a single low-poly surface that:
- drapes over the real terrain (keeps the true click heights, so ramps and stairs
  slope correctly),
- carries its SEMANTIC category (walkable, stairs, ...) as data, not just a color,
- is shaded by a distance-aware grid so near zones read densely and far zones read
  sparsely,
- hides correctly behind real walls (via the depth occluder),
- and exports as ONE prefab that drops onto the colleague's twin at any scale with
  zero math.

So: trace regions, do not hand-place lines. That is the whole point. A region traced
once is meaningful, editable, styleable from one palette, and reusable.

---

## 1. The pieces (what is in the package)

Under `Assets/SemanticMesh/`:

- `Editor/SurfaceTracerWindow.cs`
  The main tool. An `EditorWindow` that draws in the Scene view, handles clicking /
  dragging / closing shapes, builds the surface meshes, and exports prefabs. All
  authoring lives here.
- `Editor/SemanticMeshMenu.cs`
  Menu commands that build disposable test scenes (a bare one, and the alignment one
  that wires the real splat + a scan collider).
- `Editor/PolygonTriangulator.cs`
  Ear-clipping triangulation for a simple 2D polygon. Turns your traced outline into
  triangles. Handles convex and simple concave shapes, not self-intersecting or
  holed polygons.
- `SemanticSurface.cs`
  A tiny `MonoBehaviour` put on every generated surface. It holds ONLY the category
  (`SurfaceType`). The look (color + grid density) is pulled from the palette at
  runtime via a `MaterialPropertyBlock`, so a value never lives in two places.
  Marked `[ExecuteAlways]` so surfaces show their real color in the Editor without
  entering Play.
- `SurfacePalette.cs` + `Resources/SurfacePalette.asset`
  The ONE source of truth for how each category looks: its grid color and cell size.
  One asset, auto-loaded from Resources, read by both the editor tool and any runtime
  build. Do not copy these values onto individual surfaces.
- `Shader/SemanticGrid.shader` + `Material/M_SemanticGrid.mat`
  The procedural LOD grid. No line geometry: the grid is computed in the fragment
  shader from the surface's UV (in meters). Transparent between lines so it overlays
  in AR. See section 6 for every knob.
- `Shader/DepthOccluder.shader` + `Material/M_DepthOccluder.mat`
  An invisible material that writes depth only. Put it on the real walls so grids and
  gems behind them are hidden. The effect is only fully visible on device.

`SurfaceType` (the categories) is an enum with STABLE integer values so serialized
data never re-maps if the list grows: Walkable=0, Stairs=1, Grass=2, Building=3,
Hazard=4, Obstacle=5.

---

## 2. Core concepts (understand these before using)

### 2.1 Semantic surface
A thin mesh you trace, tagged with a `SemanticSurface` component that stores its
category. It is the unit you author, style, and hand off.

### 2.2 The palette (one source of truth for looks)
`Resources/SurfacePalette.asset` maps each category to a grid color and a cell size
(meters per grid cell at the nearest LOD, smaller = denser). Change the palette and
EVERY surface of that category updates. Surfaces never store their own color; they
read the palette through a `MaterialPropertyBlock`, so one shared material drives all
categories with no per-surface material instances (no leaks, batching preserved).

### 2.3 The grid shader and LOD
The grid is generated mathematically from UV meters, not from line meshes. Line width
is derivative-based (`fwidth`), so distant lines stay crisp instead of shimmering. As
a fragment gets farther from the camera, cells grow (sparser) and lines thin. Near =
dense and detailed, far = sparse and calm. See section 6 for the exact properties.

### 2.4 Drape (real heights, not a flat plane)
When you close a shape, the tool fits a best-fit plane to your points ONLY to get a
2D basis for triangulation and UVs. The actual mesh vertices keep your real click
heights. So the surface hugs terrain: a ramp slopes, stairs step, flat stays flat.
More clicks = tighter fit.

### 2.5 Working plane / hole bridging
Scan meshes have holes (missing geometry). When your click ray misses the mesh, the
tool drops the point onto a "working plane" instead so you can bridge the gap:
- if you already have 3+ points, the plane is the best-fit plane of those points,
- otherwise it is a horizontal plane through your last point.
Points placed this way are "inferred" and drawn ORANGE; points on the real mesh are
YELLOW. Drape uses the plane height for inferred points and the real height for
on-mesh points.

### 2.6 Surface Root and the local frame (the scale-proof trick)
This is the key idea that makes handoff trivial. If you assign a Surface Root (the
environment twin transform), every surface is authored in THAT transform's local
space: hit points are converted with `InverseTransformPoint`, all math runs local,
and surfaces are created as identity-local children of a "Semantic Surfaces"
container that is itself an identity-local child of the root. So the surfaces are
expressed relative to the twin, not the world. Parent the result under the twin
(any scale, any rotation) and it lands correctly with zero tuning. Leave Surface Root
empty to author in world space (fallback; not scale-proof).

CONSEQUENCE TO KNOW: if the twin is at scale 0.01, then "twin-local" coordinates are
100x world size. A prefab authored twin-local, dropped into an EMPTY scene at scale
1, will render about 100x too big and far from the origin (so it looks like "nothing
came over"). It did come over; it just needs to sit under a 0.01-scale root to look
right. See section 8 troubleshooting.

### 2.7 The depth occluder
Grids and gems should not show through real walls. The occluder material writes depth
but no color. Put it on the wall/environment renderers and the grid's `ZTest LEqual`
+ `ZWrite Off` makes anything behind the wall fail the depth test and disappear. Only
truly visible on device.

---

## 3. The tool window, button by button

Open: **Tools > Semantic Mesh > Surface Tracer**.

- **Surface Root** (object field)
  Drag the environment twin transform here to author in its local frame (recommended,
  scale-proof). Empty = world space.
- **Surface Type** (dropdown)
  The category the next traced region will be tagged as.
- **Dense interior sampling** (toggle) + **Sample spacing (m)**
  Off (default), a surface has vertices only at your click corners, so interiors
  interpolate corner-to-corner (a smooth ramp). On, the region is subdivided and
  the real scan mesh is raycast at every interior point, so the drape follows true
  steps and bumps. ALWAYS forced on for Stairs. Spacing is in real-world meters
  (converted to the Surface Root's local units automatically, so twin scale does
  not change the density). Needs the target collider present. Final look is
  device / real-scan dependent (roadmap 9.7).
- **Start Tracing**
  Begins a shape. Clears any in-progress points.
- **Close Shape (Enter)**
  Finalizes the current outline into a surface (needs 3+ points).
- **Undo Point** / Backspace
  Removes the last placed point.
- **Cancel (Esc)**
  Discards the in-progress shape.
- **Prepare Target (add MeshCollider to selection)**
  Adds a MeshCollider to the selected mesh(es) so you can raycast/click them. Not
  needed if the collider is already there (the alignment scene adds it for you).
- **Add Depth Occluder to selection subtree**
  Adds the occluder material to every MeshRenderer under each selected object, in one
  click (walks the whole subtree, including inactive renderers).
- **Save Surfaces as ONE Prefab**
  Writes the whole "Semantic Surfaces" container to
  `Assets/SemanticMesh/Semantic_Environment.prefab`.
- **Save a Prefab PER Surface Type**
  Writes one prefab per category present (`Semantic_Walkable.prefab`,
  `Semantic_Grass.prefab`, ...). Clones into temp roots so the working scene is
  untouched. Use this if the colleague wants to toggle categories independently.

Scene-view interaction while tracing:
- Left-click the mesh to drop a point (yellow on mesh, orange over a hole).
- Drag any placed point to nudge it; it re-snaps to the mesh, or the working plane if
  it misses. Grabbing a handle never also drops a stray point.
- Dotted white line previews the next segment; dotted green previews the closing edge.

---

## 4. Step-by-step: authoring a labeled environment

1. **Open a scene with the scan and (ideally) the splat.**
   - Fastest tool check: **Tools > Semantic Mesh > Create Test Scene**, drag any mesh
     in, select it, **Prepare Target**.
   - Real authoring: **Tools > Semantic Mesh > Build Alignment Test Scene**. It drops
     in the Digital Twin splat, parents the scan under it with colliders, tips it
     upright with a cosmetic rig, and opens the tracer.
2. **Assign Surface Root** = the `Digital Twin` transform (NOT the rig). This is what
   makes the export scale-proof.
3. **Check alignment** (see section 5): does the scan sit on the splat? If not, nudge
   the scan transform until it does.
4. **Trace region 1:** set Surface Type, Start Tracing, click the corners, Enter to
   close. A grid-shaded surface appears under "Semantic Surfaces".
5. **Repeat** for every region and every category. They all collect under the one
   container. Trace as many as you like.
6. **Tune the look** by editing `Resources/SurfacePalette.asset` (color + cell size
   per category). All surfaces of a category update at once.
7. **Export:** "Save Surfaces as ONE Prefab" (everything) and/or "Save a Prefab PER
   Surface Type" (toggle-able categories).
8. **Verify the export** by parenting the prefab under a 0.01-scale object (or the
   twin) and framing it. Do not judge it dropped into a bare empty scene at scale 1
   (it will be 100x and off-screen; see section 8).

---

## 5. Alignment ladder (trace mesh onto the splat)

You cannot collide a Gaussian Splat, so you trace an FBX/OBJ/PLY scan while SEEING
the splat. Get them to overlay, cheapest first:

1. **Free path (try first).** Parent the scan under the twin at local identity so it
   inherits the twin's scale + flip. If scan and splat came from the same scan they
   overlay for free. `Build Alignment Test Scene` sets this up. Uncheck the scan's
   Mesh Renderer to trace the invisible collider while seeing the splat.
2. **Manual nudge (fallback).** If they do not overlay, select the scan and adjust its
   Transform (move / rotate / uniform scale) with the splat visible, by eye. About two
   minutes, always works.
3. **3-point solver (only if needed).** A correspondence solver; not built, because
   the splat side is hard to click (no collider). See Roadmap.

The test scene's "Twin Upright (test rig)" parent only tips the Z-up scan data
upright for comfortable viewing. It is cosmetic, never ships, and does not affect the
local-frame authoring.

---

## 6. The grid shader in detail (and the rounding effect)

Material: `Material/M_SemanticGrid.mat`, shader `SemanticMesh/SemanticGrid`.
`SemanticSurface` pushes `_Grid_Color`, `_Cell_Size`, `_Grid_Style`, `_Lod_Start`,
`_Lod_End`, and `_Lod_Coarsen` per-category from the palette; `_Grid_Thickness` and
`_Line_Softness` are material-wide defaults.

Properties:
- `_Grid_Color` (HDR) - line color. Set per-category by the palette.
- `_Base_Color` - color BETWEEN lines. Default fully transparent (0,0,0,0) so only
  the lines show in AR.
- `_Cell_Size` - meters per cell up close (near). Set per-category by the palette.
- `_Grid_Style` (0/1/2) - square / rounded / criss-cross. Set per-category by the
  palette (roadmap 9.1). Square drops softness+thickness for a sharp line; rounded
  is the original soft look; criss-cross rotates the cells 45 degrees.
- `_Grid_Thickness` (0.001-0.5) - line width as a fraction of a cell.
- `_Lod_Start` / `_Lod_End` (m) - distance range over which the grid coarsens. Set
  per-category by the palette (the palette's "Fade from / Fade to", roadmap 9.6).
- `_Lod_Coarsen` (1-16) - how many times bigger cells get at full LOD distance. Set
  per-category by the palette, DERIVED as far cell / near cell (not edited directly).
- `_Line_Softness` (0.5-3) - anti-alias width of the lines.

### The "rounding effect" you are seeing
It is not a separate feature; it is the anti-aliasing. `grid_coverage` builds each
line with `smoothstep` over a derivative-based width and then unions the two axes.
Where two lines cross, the softened coverage makes the intersection read as a rounded
corner, and at distance the LOD "wash" (lines fading to a faint fill) rounds things
further. It is a normal square grid with soft edges. Turning `_Line_Softness` down
toward 0.5 and `_Grid_Thickness` down makes it sharper/squarer; up makes it rounder.

This is a good candidate to expose as a per-category STYLE, see Roadmap 9.1.

---

## 7. Handoff: EXACTLY what the colleague does

Deliverable = the prefab(s) + the `Assets/SemanticMesh/` folder. No scale tuning,
because surfaces were authored twin-local.

In the colleague's project:
1. Copy the `Assets/SemanticMesh/` folder in (scripts, shaders, materials, palette,
   Resources). This carries the components and the look.
2. Drag `Semantic_Environment.prefab` (or the per-type prefabs) into his scene.
3. **Parent it under his environment twin/anchor transform, then zero its LOCAL
   transform** (localPosition 0, localRotation identity, localScale 1). It now
   inherits the twin's real-world scale + orientation and lands on the environment.
   This is the single most important step: the prefab is expressed in the twin's local
   frame, so it must be a child of that twin at local identity.
4. **Occluder (optional but recommended):** select his wall/environment root and click
   **Add Depth Occluder to selection subtree**. Grids and gems behind real walls then
   stop rendering.
5. **Restyle (optional):** edit `Resources/SurfacePalette.asset` to change any
   category's color or grid density. Never edit surfaces one by one.

If the prefab lands rotated, offset, or wrongly scaled, the transform he parented
under is not the frame the surfaces were authored in. Re-check that Surface Root at
authoring time was that exact twin transform.

---

## 8. Troubleshooting

- **"I exported the prefab and the grids did not come over."**
  Most likely they DID come over but are 100x too big and off-screen, because they
  were authored in the twin's local frame (twin scale 0.01 => local units are 100x
  world). Diagnose:
  1. Look in the Hierarchy of the new scene. Are the `Semantic_*` objects present
     under the prefab? If YES, it is a scale/position/visibility problem, not a save
     problem. Select the prefab and press F to frame it; you will likely find a giant
     grid far from origin.
  2. To view it correctly, parent the prefab under a 0.01-scale empty (or the twin)
     and zero its local transform.
  If the `Semantic_*` objects are NOT in the hierarchy, then the save failed; check
  the Console for warnings. NOTE: the old "grids come over with no geometry" bug
  (meshes saved as null) is fixed, meshes now embed in the prefab (roadmap 9.2). If
  you re-export an OLD prefab that predates the fix, it will still be empty until you
  re-save it from a live trace with the current tool.
  Secondary cause: the color comes from a `MaterialPropertyBlock` that is not
  serialized; it is re-applied by `SemanticSurface` `OnEnable` (`[ExecuteAlways]`). If
  a surface shows geometry but no grid color, that re-apply did not run; selecting it
  or toggling it forces `OnValidate`/`OnEnable`. Roadmap 9.2 hardens this.
- **"I only see the gray scan, not the splat."**
  The Gaussian Splat renderer may not draw in the Scene view. Try Play mode. Tracing
  still works either way (it uses the collider, not the splat).
- **"It is upside down / sideways / tiny in the Editor."**
  Off-device the twin's real pose comes from ArUco + WorldLocking at runtime; in
  edit/play it falls back to the authored anchor and can look wrong. This does not
  affect the tool (local-frame authoring). Do not chase it in the Editor.
- **"Triangulation failed" dialog.**
  The outline self-intersects or is degenerate. Re-trace with a simple boundary.
- **"Stairs look wrong / the grid does not step cleanly."**
  Stairs now use Dense interior sampling automatically (roadmap 9.7): the surface is
  subdivided and the real mesh is raycast across the interior, so it follows steps
  instead of ramping. If it still looks rough, the scan mesh at that spot is noisy;
  lower the Sample spacing for more detail, or fall back to marking the stair AREA
  with a rough boundary and trusting the real mesh. This is device / real-scan
  dependent, do not chase it in the Editor on a placeholder mesh.

---

## 9. Known issues and roadmap (the work queue for the next session)

These are the open items. Nothing here blocks using the tool; they are polish and
features. Ordered roughly by value.

### 9.1 Grid style options (requested) - DONE (edit-mode; final read device-only)
Implemented. Each palette row has a `grid_style` (Square / Rounded / Criss-cross)
pushed to the shader's `_Grid_Style` through the property block, same path as color
and cell size. The frag shader branches on it (criss-cross uses diagonal cell
coords; square sharpens softness/thickness; rounded is the default look). Default
per-category assignments are in `SurfacePalette.asset`. Whether each style actually
reads well at real AR distances is device-only, tune on the ML2.

Original notes (kept for reference):
Right now every surface uses the same soft square grid. Add a per-category STYLE so
different zones can read differently. Target styles:
- **Square** (current, sharp): axis-aligned lines, low softness.
- **Rounded** (current default look): axis-aligned lines, higher softness/thickness.
- **Criss-cross / diagonal**: 45-degree lines.
Implementation sketch:
- Add `_Grid_Style` (float, 0/1/2) to `SemanticGrid.shader` `Properties` and the
  `UnityPerMaterial` CBUFFER.
- Add a `grid_style` field to `SurfaceEntry` in `SurfacePalette.cs` and a matching
  column in `SurfacePalette.asset`.
- In `SemanticSurface.apply_visual`, push it via the property block alongside
  `_Grid_Color` / `_Cell_Size` (new `Shader.PropertyToID("_Grid_Style")`).
- In the fragment shader, branch: for criss-cross, compute coverage on diagonal
  coordinates `float2(uv.x + uv.y, uv.x - uv.y)` instead of `uv`; for square vs
  rounded, drive `_Line_Softness`/thickness from the style. Keep it a cheap `if` or
  `lerp`, no separate passes.
- Because color/cell already flow palette -> MPB, this is the same wiring one more
  time. Keep the "one source of truth" rule: style lives on the palette entry only.

### 9.2 Prefab export robustness (bug) - DONE + VERIFIED (2026-07-20, prefab confirmed on device)
Fixed and confirmed: a re-exported prefab now shows its grids. The real bug: the
procedural meshes are `new Mesh()` in memory and never
persisted, so `SaveAsPrefabAsset` wrote every `MeshFilter.m_Mesh` as null (the old
`Semantic_Environment.prefab` had `{fileID: 0}` on all of them). A dropped prefab
had NO geometry at all, regardless of scale. `save_prefab_with_meshes` now embeds a
copy of each surface's mesh INTO the prefab file (`AddObjectToAsset` + re-save), so
the prefab is self-contained and its grids actually come over. Same fix applies to
the per-type export. Color re-apply is hardened: `SemanticSurface.apply_visual` now
runs from `Awake` as well as `OnEnable`, so a freshly instantiated/loaded surface
paints even before selection. The export also pops a report (surface count, world
bounds, twin-local reminder). VERIFY: re-export, drop the prefab under a 0.01 root,
zero its local transform, confirm the grids render. Occluder look stays device-only.

Original notes (kept for reference):
Harden the export so a dropped prefab reliably shows:
- Confirm the procedural `Mesh` is embedded in the prefab (it should be via
  `SaveAsPrefabAsset`; if not, `AssetDatabase.AddObjectToAsset` the meshes or save the
  meshes as assets next to the prefab and reference them).
- Consider baking the palette color into a serialized form so a surface renders even
  before `OnEnable` re-applies the property block (e.g., a per-instance material only
  at export, or writing vertex colors). Weigh this against the "no second state" rule;
  the property block should stay the live source. A minimal fix is to call
  `apply_visual` from `Awake` too, and to document that the prefab must be parented
  under a 0.01 root to be seen.
- Add a short "export report" dialog: how many surfaces, the bounds, and a reminder
  that the prefab is twin-local.

### 9.3 cell_size world-units caveat
Once twin-local at 0.01 scale, `cell_size` 0.25 local = 0.0025 m real. The shader
takes LOD distance from world position (correct) but cells from local UV meters.
Either divide UV by the object's world scale in the shader to keep cells in world
meters, or document palette cell sizes as local units. Decide with the real twin in
front of you during device tuning.

### 9.4 Post-finalize editing
Today you can drag points only WHILE tracing, before closing. Add editing of a
finalized surface (select it, move its points, regenerate the mesh). Store the traced
points on the `SemanticSurface` component so a finalized surface can be reopened.

### 9.5 3-point alignment solver
Optional. A correspondence solver to align the scan to the splat from 3 clicked
pairs. Low priority because the splat side is hard to click (no collider; depth must
be dragged by orbit parallax). Manual nudge covers it in ~2 minutes.

### 9.6 Easy control of grid size vs distance (requested) - DONE (edit-mode; device fold-in pending)
Implemented per-category on the palette (the one-source-of-truth choice). Each row
now carries `cell_size` (near), `far_cell_size` (far), `lod_start` (fade from), and
`lod_end` (fade to); the coarsen multiplier is DERIVED as far/near so the two cell
sizes are the only knobs and cannot drift. `SemanticSurface.apply_visual` pushes all
of them through the property block. A custom palette inspector
(`Editor/SurfacePaletteEditor.cs`) presents them as plain "Near cell / Far cell /
Fade from / Fade to" per category in one place. Edits are LIVE: `SurfacePalette`
raises a `changed` event on validate and every `[ExecuteAlways]` surface re-pulls,
so moving a value repaints the scene with no Play mode and no re-selecting. SCOPING
DECISION (2026-07-20): this stays EDITOR-ONLY for this project. Tuning happens in the
Editor palette, not via an on-device `DevTunable`. The dev-settings fold-in described
in the original notes below is intentionally NOT being done here.

Original notes (kept for reference):
Make "how big the grid cells are at a given distance" easy to dial, ideally live.
Today it is spread across four material fields (`_Cell_Size`, `_Lod_Start`,
`_Lod_End`, `_Lod_Coarsen`) that are not obvious and are not per-category tunable in
one place. Plan:
- Promote these to the palette (per-category) and/or a single `SemanticMeshProfile`
  asset (global), so all the distance/size knobs live in ONE editable place, matching
  the project's "one source of truth" rule. `SemanticSurface.apply_visual` already
  pushes `_Cell_Size` via the property block; push `_Lod_Start`/`_Lod_End`/
  `_Lod_Coarsen` the same way so they can differ per category.
- Give the tracer window (or a small dedicated inspector) simple sliders:
  "Near cell size (m)", "Far cell size (m)" (derive `_Lod_Coarsen` = far/near),
  "Detail fades from (m)" -> `_Lod_Start`, "to (m)" -> `_Lod_End`. Two distances and
  two cell sizes is far more intuitive than the raw coarsen multiplier.
- Update live: because these flow through the property block and `SemanticSurface` is
  `[ExecuteAlways]`, moving a slider should re-apply and repaint immediately, no Play
  mode. Wire an `OnChanged` that calls `apply_visual` on all surfaces.
- On device this must be reachable per the project's dev-settings pattern (a
  `DevTunable` in a dev window, visible on the dashboard), not just an Editor slider,
  since final grid readability is judged on the ML2. Fold these into that system when
  the tool goes on device.

### 9.7 Better stairs - DONE + IMPROVED (2026-07-20, "a bit better" on the real scan)
Implemented the "sample the mesh more densely" option as an opt-in **Dense interior
sampling** toggle (see section 3), forced on for Stairs. Confirmed to help on the real
scan: stairs read better than the old corner-to-corner ramp, though not perfect (scan
noise still shows). Lower the Sample spacing for more detail if a stair scans poorly.
On finalize, each boundary
triangle is subdivided to a single global level (so shared edges split identically,
no T-junction cracks) and the real scan mesh is raycast at every interior point, so
the draped surface follows true steps instead of interpolating corner-to-corner.
Corner vertices still keep their exact click heights. Spacing is authored in real
meters (scale-converted). Guarded by a subdivision cap and a vertex cap that falls
back to the corner-only drape if you set the spacing too fine. HOW GOOD it looks
depends entirely on the scan mesh quality at the stairs and is only judgeable on
device / against the real scan: this removes the "it is a ramp" limitation but does
not guarantee clean steps on a noisy scan. The "mark the area, trust the mesh"
workaround still applies if a given stair scans badly.

### 9.8 Device verification (the real gate)
The prefab export (9.2) and dense stairs (9.7) were checked on device 2026-07-20:
the prefab shows its grids and stairs read better. Occluder look and fine grid
readability at real distances are still ultimately ML2-judged; tune palette cell
sizes and `_Lod_*` (via the palette) there if needed.

## STATUS: this tool is DONE for this project (2026-07-20)
Thomas closed the Semantic Mesh tool out here. It stays EDITOR-ONLY: authoring and
all look/size tuning happen in the Unity Editor (the tracer window + the palette),
NOT via an on-device dev window. The remaining roadmap items below (9.3 units caveat,
9.4 post-finalize editing, 9.5 3-point solver, and the on-device `DevTunable` fold-in
of 9.6) are intentionally NOT planned for this project. Reopen only if the tool is
picked up again elsewhere.

---

## 10. Extending the tool

- **Add a new category:** add a value to `SurfaceType` in `SemanticSurface.cs` (give
  it the next stable integer), add a row to `SurfacePalette.asset` for its color/cell,
  done. It appears in the tracer dropdown automatically.
- **Add a grid style:** see 9.1.
- **Change default looks:** edit `SurfacePalette.asset` only. Never hardcode a color
  in a script or on a surface.
- **Where the math lives:** `SurfaceTracerWindow.build_surface` (centroid, Newell
  normal, plane basis, projection, triangulation, drape, parenting). Read it with this
  guide's section 2.4-2.6 in hand.

---

## 11. Runbook: exactly what to do to finish and ship

Do these in order. This is the whole job from "tool exists" to "labeled environment
on the headset." Each step says what to click and what "done" looks like. If a step
does not look right, go to the referenced section rather than improvising.

### Phase A: set up the authoring scene (once)
A1. In Unity: **Tools > Semantic Mesh > Build Alignment Test Scene**. Done when a
    scene opens showing the environment (splat and/or gray scan) and the Surface
    Tracer window is open.
A2. If the environment is the wrong/old scan, replace it: open
    `Assets/SemanticMesh/Editor/SemanticMeshMenu.cs`, change `scan_mesh_path` to the
    mesh that matches the CURRENT splat, and re-run A1. (Or just open your real scene,
    drag your scan mesh in, select it, click **Prepare Target**.) Done when the scan
    you click matches the environment you want to label.
A3. Confirm alignment: does the scan sit ON the splat? If not, select the scan and
    move/rotate/uniform-scale its Transform until it does (section 5, fallback 2).
    Done when clicking the scan lands points where the splat surface is.

### Phase B: rough first pass and prove alignment (do this BEFORE the careful pass)
Do NOT invest hours tracing precisely until you have proven the labels land in the
right place. Prove it cheaply first.
B1. In the tracer, drag your environment twin transform into **Surface Root**.
B2. Trace just a FEW rough regions: one on a building, one on a walking path, one on
    grass. Keep them crude, a handful of points each. This is throwaway, only to check
    placement. Done when three quick grids exist.
B3. Export (`Save Surfaces as ONE Prefab`) and drop it into the colleague's actual
    scene, parented under his twin at local identity (Phase E steps). The point is to
    see the rough grids in the REAL scene.
B4. **Remove the old wireframe overlay from his scene.** It is being replaced by these
    semantic grids and should not ship alongside them. You may keep it hidden
    temporarily ONLY to compare placement during this check, but it must be gone from
    the final scene. If the wireframe itself is not known to be accurate, do not trust
    it as the reference: prefer the next step.
B5. **Verify alignment the easy way:** either compare the rough grids against the
    (temporarily kept) wireframe, OR just put the headset on outside/on-site and walk
    it: does the "grass" grid sit on grass, the "path" grid on the path? Done when the
    rough labels clearly land on the right real-world surfaces.
B6. If they do NOT land right, fix alignment now (section 5, nudge the scan) and repeat
    B2-B5. Do not proceed to the careful pass until this is solid. When it is, delete
    the throwaway rough regions.

### Phase C: the careful pass (be meticulous, do it once)
Once alignment is proven, trace the real regions properly. The goal is to get it
accurate on the FIRST run so you never have to redo it.
C1. For EACH region: pick the **Surface Type**, **Start Tracing**, click along the
    region's real boundary, **Enter** to close.
C2. **Place plenty of points.** Roughly one every 10 feet (about 3 m) along a
    boundary, and more where the edge curves or the ground changes height. Dense points
    are what keep the region accurate and let the drape follow the ground. Sparse
    points are the main cause of a region that drifts off the real edge.
C3. **Stairs:** the drape does not step cleanly (mesh quality and edge smoothing fight
    it, and rushing makes it worse). The simplest reliable approach right now is to just
    mark the stair AREA as `Stairs` with a rough boundary and let the real mesh (and the
    wireframe, if you keep one for that spot only) convey the actual steps. Do not fight
    to make the grid itself step perfectly. Improving stair drape is a later item.
C4. Label the whole space: every walkable area, path, grass, building, hazard, obstacle.
    They collect under one "Semantic Surfaces" object.
C5. Tune the look: open `Assets/SemanticMesh/Resources/SurfacePalette.asset` and set
    each category's color and cell size. All surfaces of a category update at once.

Rule of thumb: accuracy comes from Phase B (alignment) and dense points in C2. Do not
overlay the wireframe on the final result unless it is known to be accurate; a good,
dense trace is the deliverable, and settings can be played with afterward.

### Phase D: export the deliverable
D1. Click **Save Surfaces as ONE Prefab**. Done when
    `Assets/SemanticMesh/Semantic_Environment.prefab` exists. (Also click **Save a
    Prefab PER Surface Type** if you want to toggle categories separately.)
D2. Sanity-check the export: drag the prefab into a scene, parent it under a
    0.01-scale object (or your twin), zero its local transform, press F to frame it.
    Done when you see the grids. If you see nothing in a bare empty scene at scale 1,
    that is expected (section 8): it is 100x and off-screen, not missing.

### Phase E: integrate into the real project
E1. Copy the entire `Assets/SemanticMesh/` folder into the target project.
E2. Drag `Semantic_Environment.prefab` into the scene that renders the twin.
E3. Parent it under the twin/anchor transform, then set its LOCAL transform to
    position 0, rotation identity, scale 1. Done when the grids sit on the environment.
E4. **Delete the old wireframe overlay** from that scene if it is still there. The
    semantic grids replace it.
E5. Optional occluder: select the wall/environment root, click **Add Depth Occluder to
    selection subtree**. Done when the occluder material is on those renderers.

### Phase F: build and tune on device (the only real test)
F1. Build to the ML2 (your normal build path) and look at the environment.
F2. Tune for readability: adjust `SurfacePalette.asset` cell sizes and the grid
    material's LOD fields (`_Lod_Start`, `_Lod_End`, `_Lod_Coarsen`) until near zones
    read dense and far zones read calm (see section 6, and roadmap 9.6 for making this
    easier). Done when the labels are legible at real distances.
F3. Confirm the occluder: grids behind real walls should disappear. Done when nothing
    shows through walls.

That is the entire path. Phase B (rough + alignment) protects you from redoing work;
Phase C (the careful, dense trace) is the real content effort; Phase F needs the
headset.

---

## 12. A few project things beyond this tool (please read, done kindly)

These are not part of the Semantic Mesh tool, but they matter for the study working
end to end, and they are easy to lose track of. Written gently, because everyone is
stretched thin right now and the professor is under real pressure to get this running.
Knocking these out removes a chunk of that worry.

### 12.1 Set the gem-finding distance to 3 meters (please do this one)
The distance at which gems are found/collected still needs to be set to **3 meters**.
This has been asked for before and is easy to forget, so here it is written down as a
required step: find the gem interaction/proximity distance (it lives with the study /
gem interaction configuration, on the `StudyProfile` or the gem interaction component,
tuned on device via the ArUco 231 dev window per the study docs) and set it to 3 m.
If you are unsure which field it is, ask Thomas to point at the exact one, but the
value is 3 meters. Please do not skip this; the trials assume it.

### 12.2 Eye gaze and permissions (a reassurance and a quick check)
There has been some worry about whether the eye-gaze setup and its permissions are
correct. The good news: the eye-tracking permission IS declared in the manifest
(`com.magicleap.permission.EYE_TRACKING` in
`Assets/EyeGaze/Assets/Plugins/Android/AndroidManifest.xml` and the project manifest),
so the plumbing is in place. To put the worry to rest, verify it once on device rather
than in theory:
- On the ML2, when the app first uses eye tracking, confirm the permission prompt
  appears and is granted (Magic Leap eye tracking is a runtime-granted permission).
- Run the app's startup self-test / check the logs for the eye-tracking subsystem
  coming up (see `DEBUGGING.md` for the `[APITEST]` self-test and per-subsystem log
  filtering). If gaze data is flowing, it is working.
- If the permission prompt never appears or gaze data is empty, the usual causes are:
  the OpenXR eye-gaze feature not enabled alongside the manifest permission, or the
  eye-tracking calibration not completed on the headset. Enable the feature, redo
  calibration, and retest.
This is verifiable in a few minutes on device, and once you have seen gaze data come
through, you can stop worrying about it. If anything looks off, capture the log per
`DEBUGGING.md` and send it to Thomas rather than guessing.
