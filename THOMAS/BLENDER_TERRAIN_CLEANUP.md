# Blender Terrain Cleanup (Shrinkwrap Ground)

A short, self-contained guide for cleaning the shrinkwrap terrain FBX so it drapes a
clean grid in the AR scene. You do not need to know the rest of the project to do
this. Budget 15 to 30 minutes.

## What you are looking at

`TerrainShrinkwrap.fbx` is a draped (shrinkwrapped) copy of a raw scan. The flat
ground came out great. The problem is vertical junk sticking up:

- **Sky spikes:** where the scan had a hole, the drape stretched a vertex straight up
  into a spire.
- **Buildings/walls:** vertical stretched geometry, same artifact, just bigger.

Your job: keep the ground, remove the vertical junk, and (optionally) fill the holes.

## What you do NOT have to fix

The Unity grid shader already **discards near-vertical faces automatically**, so the
tall walls of the spikes and buildings get culled in-engine either way. That means
Blender is really only needed for two things the shader cannot do:

1. Remove the **floating caps** (the little horizontal tops left hovering up high
   after the walls are culled).
2. **Fill holes** so the gaps in the ground get grid lines too.

So even a rough pass helps. If you only have five minutes, do steps 1 to 4 below (the
height cull) and stop; that is 90 percent of the win.

## Work on a copy

Duplicate the FBX first (or Save As a new .blend) so the original is safe.

## Step 1 to 4: cull the vertical junk (the fast part)

This deletes spikes and buildings in one sweep by selecting everything above the
ground from the side.

1. Select the terrain, press `Tab` to enter Edit Mode, press `1` for vertex select.
2. Front orthographic view: `Numpad 1`. Turn on X-Ray with `Alt+Z` so box-select
   reaches through the mesh, not just the front layer.
3. Box-select everything **above the ground band** (drag a box over the spikes and
   buildings, leaving the ground untouched), then press `X` and choose **Vertices**.
4. Rotate to the side view: `Numpad 3`, and repeat the box-select-and-delete to catch
   anything the first angle missed.

Then remove orphaned bits:

- Press `A` (select all), then `Mesh > Clean Up > Delete Loose`.

Tip: it is fine to leave a short stub at the base of a spike. The shader culls the
remaining vertical part, so you do not need to be surgical, just get the bulk.

## Step 5 to 6: fill the holes (optional, bridges the gaps)

Filled holes automatically get matching grid lines because the grid is projected
top-down, so this is worth doing where there are obvious gaps.

5. `Select > All by Trait > Non Manifold` (this grabs the open hole borders).
6. `Mesh > Clean Up > Fill Holes`, then in the operator panel (bottom-left) raise
   **Sides** to a large number (e.g. 1000) so big holes fill too.

For one large, clean, roughly rectangular gap, you can instead select its border edge
loop and use `Face > Grid Fill` for a tidier result.

## Step 7: recalculate normals (do this, it matters)

The shader's vertical-face cull reads the surface normals, so they must be correct.

- Press `A` (select all), then `Shift+N` (Recalculate Outside). Same as
  `Mesh > Normals > Recalculate Outside`.

## Step 8: export

- **Do not move the mesh or change its origin.** Deleting and filling geometry is
  fine, but do not translate/rotate/scale the object or re-set its origin, or it will
  land in a different spot in Unity and no longer line up with the rest of the scene.
- Select only the terrain.
- `File > Export > FBX (.fbx)`.
- Check **Limit to > Selected Objects**.
- Keep the **same axis and scale settings** you used when you first exported this
  terrain, so it lands in Unity the same way (same orientation and size).

Send the exported FBX back (or drop it in place of the existing one). It must sit
**flat (ground facing up)** in Unity; if it ends up on its side, the vertical-face
cull will cull the wrong faces. If that happens, just rotate it upright in Unity or
re-export with the correct axis.

## Quick checklist

- [ ] Worked on a copy.
- [ ] Height-culled the spikes and buildings (steps 1 to 4).
- [ ] Deleted loose geometry.
- [ ] Filled holes (optional).
- [ ] Recalculated normals (`Shift+N`).
- [ ] Exported selected only, same axis/scale, ground lies flat.
