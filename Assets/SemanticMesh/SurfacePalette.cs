using System;
using System.Collections.Generic;
using UnityEngine;

namespace SemanticMesh
{
    // One row of the palette: a category and how its grid should look.
    [Serializable]
    public struct SurfaceEntry
    {
        public SurfaceType surface_type;
        public Color grid_color;

        // Meters per grid cell at the nearest LOD. Smaller = denser grid.
        public float cell_size;
    }

    // The single source of truth for how each SurfaceType looks. Lives as one
    // asset in Resources so both the editor tool and a runtime build read the
    // same values. Do not copy these onto individual surfaces.
    [CreateAssetMenu(fileName = "SurfacePalette", menuName = "Semantic Mesh/Surface Palette")]
    public class SurfacePalette : ScriptableObject
    {
        public List<SurfaceEntry> entries = new List<SurfaceEntry>();

        private static SurfacePalette cached_default;

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
    }
}
