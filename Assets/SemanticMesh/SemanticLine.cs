using UnityEngine;

namespace SemanticMesh
{
    // Attached to every traced polyline. Holds the category and the authored
    // width; the color comes from the same SurfacePalette the surfaces use, so a
    // line and a filled surface of the same category always match.
    //
    // Why LineRenderer and not a Lines-topology mesh: PLUME's MeshFilter recorder
    // stores only an asset GUID for a mesh, never its vertices or topology, so a
    // procedural mesh records as an unresolvable reference and shows nothing in
    // the viewer. PLUME's LineRenderer recorder writes positions, width and color
    // straight into the sample stream, so a LineRenderer survives the round trip
    // with no asset-bundle dependency. Lines topology is also stuck at 1px on
    // Magic Leap 2 (wide lines are an optional device feature and Unity exposes no
    // width control for that topology), which reads as shimmer on a see-through
    // display.
    [ExecuteAlways]
    [RequireComponent(typeof(LineRenderer))]
    public class SemanticLine : MonoBehaviour
    {
        public SurfaceType surface_type = SurfaceType.Generic;

        // Authored stroke width in REAL-WORLD meters. LineRenderer multiplies its
        // width by the transform's lossy scale, so under a 0.01-scale twin a raw
        // width would render 100x too thin. apply_visual divides this by the
        // current scale every time, which also means rescaling the twin keeps the
        // line looking the same thickness instead of vanishing.
        public float world_width = 0.02f;

        // Optional, same contract as SemanticSurface: null uses the shared palette
        // in Resources so a runtime build resolves colors with no wiring.
        [SerializeField] private SurfacePalette palette_override;

        private static readonly int base_color_id = Shader.PropertyToID("_BaseColor");
        private static readonly int color_id = Shader.PropertyToID("_Color");

        private MaterialPropertyBlock mpb;

        // Awake AND OnEnable, matching SemanticSurface: the property block is
        // instance state and is not serialized, so a dropped prefab or a loaded
        // build has to re-push it before anything selects the object.
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
            if (GetComponent<LineRenderer>() != null)
            {
                apply_visual();
            }
        }
#endif

        public void apply_visual()
        {
            var line = GetComponent<LineRenderer>();
            if (line == null)
            {
                return;
            }

            var palette = palette_override != null ? palette_override : SurfacePalette.default_palette;

            SurfaceEntry entry = default;
            bool has_entry = palette != null && palette.try_get(surface_type, out entry);
            Color color = has_entry ? entry.grid_color : Color.white;

            // Undo the transform's scale so the stroke lands at the authored
            // real-world width whatever the root is scaled to.
            Vector3 ls = transform.lossyScale;
            float scale = (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f;
            if (scale < 1e-6f)
            {
                scale = 1f;
            }
            line.widthMultiplier = Mathf.Max(1e-5f, world_width / scale);

            // Set the gradient as well as the property block. The gradient is what
            // PLUME's LineRenderer recorder serializes, so the color survives into
            // the viewer even though the property block does not.
            line.startColor = color;
            line.endColor = color;

            if (mpb == null)
            {
                mpb = new MaterialPropertyBlock();
            }
            line.GetPropertyBlock(mpb);
            mpb.SetColor(base_color_id, color);
            mpb.SetColor(color_id, color);
            line.SetPropertyBlock(mpb);
        }
    }
}
