# START HERE (Semantic Mesh + Ground Grid Handoff)

Read this first. It is the one entry point. It tells you the single path that is
built and works, points you at the detail docs, and flags the two ideas that are NOT
built so you do not waste time on them.

If you only remember one thing: **clean the terrain mesh in Blender, drop it in, put
`M_GroundGrid` on it, and trace the important zones with the tool set to Generic.**

---

## The plan in one picture

The AR environment is split in two, because the two halves want different treatment:

1. **The ground** (streets, sidewalks, dirt): a draped shrinkwrap mesh with a
   top-down grid. You clean it in Blender, drop it in, assign one material. No
   tracing. This is the fast part.
2. **The labels** (walkable zones, stairs, grass, hazards, buildings): traced with
   the Semantic Mesh tool. Set the tool to **Generic** and trace the space if you
   just need it done; use the real categories if you have time. This is the part
   that takes effort, because a messy real space has to be traced by hand.

You do not have to do both perfectly. The ground grid alone already gives a readable
AR surface. The labels add meaning on top.

---

## Part 1: The ground (do this first, it is quick)

### 1a. Clean the mesh (Blender)
The raw shrinkwrap has vertical spikes (holes in the scan stretched to the sky) and
building walls sticking up. Clean them off. Full steps are in
**`BLENDER_TERRAIN_CLEANUP.md`** (send-to-colleague ready). The short version:
- Height-cull the spikes and buildings from a side view.
- Fill holes (optional, bridges gaps).
- Recalculate normals (`Shift+N`).
- Export selected only, and **do not move the object or its origin** or it will not
  line up in Unity.

Note: the grid material already discards near-vertical faces in-engine, so even a
rough Blender pass looks fine. Blender mainly removes the floating spike tops and
fills holes.

### 1b. Drop it into the scene
1. Drag your cleaned FBX into the scene.
2. Put **`Assets/SemanticMesh/Material/M_GroundGrid.mat`** on its MeshRenderer(s).
3. Make sure it lies **flat (ground facing up)**. The spike cull reads world
   normals, so a sideways mesh culls the wrong faces. Rotate it upright if needed.

That is it. The material projects the grid top-down from world X/Z, so it needs no
UVs and re-tuning nothing when you swap in a new/cleaned mesh. Because your mesh keeps
its origin, it lands in the same place as the rest of the scene automatically.

### 1c. Verify quickly (optional)
Run **Tools > Semantic Mesh > Build Ground Grid Test Scene**. It drops the terrain in
with the material and adds the original scan (`BaselineGray2`) as an alignment
reference. If the grid sits on that reference, the placement is correct.

### 1d. Tune the look (all live on `M_GroundGrid`)
- **Color / visibility:** `_Grid_Color` (brighter = more visible), `_Grid_Thickness`
  (line width), `_Base_Color` alpha (a faint fill so it reads in the Editor; set the
  alpha to 0 for device, AR wants only the lines).
- **Density:** `_Cell_Size` (meters per cell; smaller = more lines).
- **LOD:** `_Lod_Enable` = 0 gives a uniform square grid everywhere; = 1 makes far
  cells grow sparse and lines thin. Ground ships with LOD off (complete square).
- **Spike cull:** `_Up_Threshold` (raise toward 1 to cull more aggressively; lower to
  keep more). `_Spike_Discard` = 0 turns the cull off entirely.

Final readability is judged on the ML2, not the monitor: thin bright lines read very
differently through AR passthrough. Tune there.

---

## Part 2: The labels (the Semantic Mesh tool)

The full manual is **`SEMANTIC_MESH_GUIDE.md`**. It is thorough; section 11 is the
step-by-step runbook. The short version for the rushed case:

1. Open **Tools > Semantic Mesh > Surface Tracer**.
2. The Surface Type dropdown defaults to **Generic** (blue, square grid). That is the
   "just label the space, do not sort by meaning" category. Trace the space with it.
3. Or, if you have time, pick the real categories (Walkable, Stairs, Grass, Building,
   Hazard, Obstacle) so zones carry meaning and color.
4. Export: **Save Surfaces as ONE Prefab**. Drop the prefab into the scene, parent it
   under the environment twin, zero its local transform.

Generic exists so a messy space can be labeled fast without deciding walkable vs grass
vs hazard on every region. It is a real traced, draped, occluded surface, just with no
per-zone meaning.

---

## Future idea (NOT built): shrinkwrap the buildings too

The same Blender shrinkwrap-and-clean process could be run on the **buildings**, not
just the ground, to get a low-poly building shell to drop in. Worth noting so it is
not lost, but it is not built, and there is one real catch:

- The ground grid works because it is projected **top-down (world X/Z)**, which is
  perfect for horizontal surfaces. That same projection **smears on vertical walls**.
- So a shrinkwrapped building shell would need a different grid projection for its
  walls: either trace the walls with the tool (the tool's UV-based grid already
  handles verticals cleanly), or extend the shader with triplanar / per-axis
  projection so a vertical face grids along its own plane.

Recommendation if this gets picked up: clean the building shell in Blender the same
way, but grid the walls by tracing them with the tool (Generic), not with
`M_GroundGrid`. Building the triplanar shader path is a larger job; ask before
starting it.

---

## Ignore these two docs (ideas only, NOT built)

Both are AI-generated design sketches in this folder. They read like build specs but
nothing in them exists. Do not try to build them unless the path above fails:

- **`AR_Mesh_Hybrid_Workflow_Spec.md`** proposes a world-space triplanar shader. The
  real shader is UV-based and already handles walls; that spec would rewrite a working
  thing and reintroduce a solved problem.
- **`Last_Resort.md`** proposes a from-scratch screen-space edge-detection render
  feature. It is real engineering, device-only to verify, and gives no labels. It is a
  genuine last resort only if everything above is abandoned.

---

## Gotchas (save yourself an hour)

- **Build will not compile after a pull?** The `com.xgrids.lccsdk` package lives
  OUTSIDE the repo and goes missing after a `git pull`. That blocks compilation and
  looks like broken code. It is not this tool. Restore the package and it compiles.
- **Keep the terrain flat and its origin unmoved** (both covered above). Those are the
  two ways the ground grid ends up misplaced or culling wrong.
- **Do not judge the look on the monitor.** The grid is built for AR passthrough. Tune
  cell size, color, and thickness on the ML2.

---

## The files, at a glance

- `SEMANTIC_MESH_GUIDE.md` - full manual for the tracing tool.
- `BLENDER_TERRAIN_CLEANUP.md` - the mesh cleanup steps (send to whoever does Blender).
- `ML2_HANDOFF_QUEUE.md` - the tool's build/acceptance record.
- `Assets/SemanticMesh/` - the whole tool: scripts, shaders, materials, palette. Copy
  this folder into the target project.
  - `Material/M_GroundGrid.mat` - the ground grid material (Part 1).
  - `Resources/SurfacePalette.asset` - the per-category look, including Generic.
