using System;
using System.Collections.Generic;
using UnityEngine;

namespace SemanticMesh
{
    // One row of the palette: a category and how its grid should look. This is the
    // ONLY place a category's look lives (roadmap 9.1 / 9.6): color, style, and the
    // whole size-vs-distance behavior. SemanticSurface pushes these to the shader.
    [Serializable]
    public struct SurfaceEntry
    {
        public SurfaceType surface_type;
        public Color grid_color;

        // Line pattern: square / rounded / criss-cross (roadmap 9.1).
        public GridStyle grid_style;

        // Meters per grid cell up close (at lod_start and nearer). Smaller = denser.
        // This is the "near cell size".
        public float cell_size;

        // Meters per grid cell far away (at lod_end and beyond). Must be >=
        // cell_size. The shader's coarsen multiplier is derived as
        // far_cell_size / cell_size, so these two sizes are the only cell knobs and
        // cannot drift apart (roadmap 9.6).
        public float far_cell_size;

        // Distance (m) where the grid starts coarsening from near to far.
        public float lod_start;

        // Distance (m) where the grid reaches the far cell size.
        public float lod_end;
    }

    // The single source of truth for how each SurfaceType looks. Lives as one
    // asset in Resources so both the editor tool and a runtime build read the
    // same values. Do not copy these onto individual surfaces.
    [CreateAssetMenu(fileName = "SurfacePalette", menuName = "Semantic Mesh/Surface Palette")]
    public class SurfacePalette : ScriptableObject
    {
        public List<SurfaceEntry> entries = new List<SurfaceEntry>();

        private static SurfacePalette cached_default;

        // Raised when any palette is edited in the Editor. SemanticSurface
        // subscribes and re-applies, so tuning the palette repaints every surface
        // live with no Play mode and no re-selecting (roadmap 9.6).
        public static event Action changed;

        // Auto-loads Assets/SemanticMesh/Resources/SurfacePalette.asset.
        public static SurfacePalette default_palette
        {
            get
            {
                if (cached_default == null)
                {
                    cached_default = Resources.Load<SurfacePalette>("SurfacePalette");
                }
                return cached_default;
            }
        }

        public bool try_get(SurfaceType type, out SurfaceEntry entry)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].surface_type == type)
                {
                    entry = entries[i];
                    return true;
                }
            }
            entry = default;
            return false;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Editor-only: notify live surfaces to re-pull their look.
            changed?.Invoke();
        }
#endif
    }
}
