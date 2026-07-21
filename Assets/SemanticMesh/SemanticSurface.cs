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
        // Catch-all "just label the space" category. Use it when there is no time
        // to sort regions by meaning: trace everything as Generic and ship. Draws
        // a plain blue square grid (see SurfacePalette.asset).
        Generic = 6,
    }

    // How a category's grid draws. Stable ints so serialized palette data never
    // re-maps. Pushed to the shader's _Grid_Style through the property block, the
    // same one-source-of-truth path as color and cell size (roadmap 9.1).
    //   Square    - axis-aligned, sharp (low softness/thickness).
    //   Rounded   - axis-aligned, softer/thicker (the original default look).
    //   CrissCross- 45-degree diagonal lines.
    public enum GridStyle
    {
        Square = 0,
        Rounded = 1,
        CrissCross = 2,
    }

    // Attached to every generated low-poly plane. Holds only the category; the
    // look (grid color, cell size, style, LOD distances) is pulled from the one
    // SurfacePalette so a value never lives in two places. Runs in edit mode so
    // authored surfaces show their real look in the Scene view without entering
    // Play, and re-applies live when the palette changes (roadmap 9.6).
    [ExecuteAlways]
    [RequireComponent(typeof(MeshRenderer))]
    public class SemanticSurface : MonoBehaviour
    {
        public SurfaceType surface_type = SurfaceType.Generic;

        // Optional. Left null uses the shared default palette in Resources so a
        // build (the colleague's runtime) resolves colors with no wiring.
        [SerializeField] private SurfacePalette palette_override;

        private static readonly int grid_color_id = Shader.PropertyToID("_Grid_Color");
        private static readonly int cell_size_id = Shader.PropertyToID("_Cell_Size");
        private static readonly int grid_style_id = Shader.PropertyToID("_Grid_Style");
        private static readonly int lod_start_id = Shader.PropertyToID("_Lod_Start");
        private static readonly int lod_end_id = Shader.PropertyToID("_Lod_End");
        private static readonly int lod_coarsen_id = Shader.PropertyToID("_Lod_Coarsen");

        private MaterialPropertyBlock mpb;

        // apply_visual runs from Awake AND OnEnable so a freshly instantiated or
        // loaded surface (a dropped prefab, a runtime build) paints its grid even
        // before anything selects or validates it. The property block is instance
        // state, not serialized, so it must be re-pushed on load (roadmap 9.2).
        private void Awake()
        {
            apply_visual();
        }

        private void OnEnable()
        {
            SurfacePalette.changed += apply_visual;
            apply_visual();
        }

        private void OnDisable()
        {
            SurfacePalette.changed -= apply_visual;
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

        // Push this surface's category look onto the shared material via a property
        // block, so every surface can differ without a per-surface material
        // instance (no leaks, batching preserved). Color and cell size are always
        // set; when the palette carries the full entry, the grid style and the LOD
        // distance/coarsen are pushed too, so those knobs are per-category and live
        // from one place (roadmap 9.1 / 9.6). Categories with no palette row fall
        // back to the material's own style/LOD defaults.
        public void apply_visual()
        {
            var mesh_renderer = GetComponent<MeshRenderer>();
            if (mesh_renderer == null)
            {
                return;
            }

            var palette = palette_override != null ? palette_override : SurfacePalette.default_palette;

            // entry is initialized so definite-assignment holds: storing the &&
            // result in a local loses the "assigned when true" flow the compiler
            // needs (CS0170), and try_get overwrites it whenever it is called.
            SurfaceEntry entry = default;
            bool has_entry = palette != null && palette.try_get(surface_type, out entry);

            if (mpb == null)
            {
                mpb = new MaterialPropertyBlock();
            }

            mesh_renderer.GetPropertyBlock(mpb);

            if (has_entry)
            {
                mpb.SetColor(grid_color_id, entry.grid_color);
                mpb.SetFloat(cell_size_id, Mathf.Max(0.001f, entry.cell_size));
                mpb.SetFloat(grid_style_id, (int)entry.grid_style);
                mpb.SetFloat(lod_start_id, entry.lod_start);
                mpb.SetFloat(lod_end_id, entry.lod_end);
                // Coarsen is derived, not stored: far cell / near cell. Keeping it
                // derived means "near size" and "far size" are the only two cell
                // knobs anyone edits, and they cannot drift out of sync.
                float coarsen = entry.cell_size > 0.0001f
                    ? Mathf.Max(1f, entry.far_cell_size / entry.cell_size)
                    : 1f;
                mpb.SetFloat(lod_coarsen_id, coarsen);
            }
            else
            {
                // No palette row: fall back to a neutral color/cell and leave the
                // material's own style/LOD defaults in place.
                mpb.SetColor(grid_color_id, Color.white);
                mpb.SetFloat(cell_size_id, 0.25f);
            }

            mesh_renderer.SetPropertyBlock(mpb);
        }
    }
}
