using UnityEngine;

namespace SemanticMesh
{
    // The semantic categories a traced surface can be tagged as.
    // Values are stable ints so serialized data never re-maps if the list grows.
    public enum SurfaceType
    {
        Walkable = 0,
        Stairs = 1,
        Grass = 2,
        Building = 3,
        Hazard = 4,
        Obstacle = 5,
    }

    // Attached to every generated low-poly plane. Holds only the category; the
    // look (grid color + cell size) is pulled from the one SurfacePalette so a
    // value never lives in two places. Runs in edit mode so authored surfaces
    // show their real color in the Scene view without entering Play.
    [ExecuteAlways]
    [RequireComponent(typeof(MeshRenderer))]
    public class SemanticSurface : MonoBehaviour
    {
        public SurfaceType surface_type = SurfaceType.Walkable;

        // Optional. Left null uses the shared default palette in Resources so a
        // build (the colleague's runtime) resolves colors with no wiring.
        [SerializeField] private SurfacePalette palette_override;

        private static readonly int grid_color_id = Shader.PropertyToID("_Grid_Color");
        private static readonly int cell_size_id = Shader.PropertyToID("_Cell_Size");

        private MaterialPropertyBlock mpb;

        private void OnEnable()
        {
            apply_visual();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Guard against the renderer not existing yet during some imports.
            if (GetComponent<MeshRenderer>() != null)
            {
                apply_visual();
            }
        }
#endif

        // Push this surface's category color and grid density onto the shared
        // material via a property block, so every surface can differ without a
        // per-surface material instance (no leaks, batching preserved).
        public void apply_visual()
        {
            var mesh_renderer = GetComponent<MeshRenderer>();
            if (mesh_renderer == null)
            {
                return;
            }

            var palette = palette_override != null ? palette_override : SurfacePalette.default_palette;

            var grid_color = Color.white;
            var cell_size = 0.25f;
            if (palette != null && palette.try_get(surface_type, out var entry))
            {
                grid_color = entry.grid_color;
                cell_size = entry.cell_size;
            }

            if (mpb == null)
            {
                mpb = new MaterialPropertyBlock();
            }

            mesh_renderer.GetPropertyBlock(mpb);
            mpb.SetColor(grid_color_id, grid_color);
            mpb.SetFloat(cell_size_id, cell_size);
            mesh_renderer.SetPropertyBlock(mpb);
        }
    }
}
