using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SemanticMesh.EditorTools
{
    // Scene-view tool for tracing low-poly semantic planes over a high-res mesh.
    //
    // Workflow: open the window, optionally assign a Surface Root (the Digital
    // Twin transform), pick a surface type, click Start Tracing, then left-click
    // in the Scene view to drop points onto the target mesh. Ray-misses over a
    // hole fall back to a working plane so gaps can be bridged. Placed points can
    // be dragged during tracing. Close the shape (Enter or the button) to draped
    // a low-poly mesh over the real hit heights, tag it with a SemanticSurface,
    // and apply the grid material. Backspace removes the last point, Escape
    // cancels the in-progress shape. Save Surfaces as Prefab writes the whole
    // container out for handoff.
    public class SurfaceTracerWindow : EditorWindow
    {
        private const string grid_material_path = "Assets/SemanticMesh/Material/M_SemanticGrid.mat";
        private const string occluder_material_path = "Assets/SemanticMesh/Material/M_DepthOccluder.mat";
        private const string container_name = "Semantic Surfaces";
        private const string prefab_path = "Assets/SemanticMesh/Semantic_Environment.prefab";

        // One traced point. Kept in world space while tracing; on_mesh is false
        // when the point was placed via the working plane over a hole (item 3),
        // true when it landed on the real collider.
        private struct TracePoint
        {
            public Vector3 world;
            public bool on_mesh;

            public TracePoint(Vector3 world, bool on_mesh)
            {
                this.world = world;
                this.on_mesh = on_mesh;
            }
        }

        // Author surfaces in this transform's local space so parenting the
        // deliverable prefab under the twin/anchor root reproduces alignment at
        // any world scale. Null = legacy world-space authoring (fallback).
        [SerializeField] private Transform surface_root;

        private SurfaceType surface_type = SurfaceType.Walkable;

        // Dense interior drape (roadmap 9.7). Off, a surface has vertices only at
        // the click corners, so interiors interpolate corner-to-corner (a ramp).
        // On, the traced region is subdivided and the real scan mesh is raycast at
        // each interior point, so the drape follows true steps. Always forced on
        // for Stairs. sample_spacing is in real-world meters (converted to the
        // Surface Root's local units internally so twin scale does not change it).
        [SerializeField] private bool dense_drape;
        [SerializeField] private float sample_spacing = 0.25f;
        private const int dense_subdiv_cap = 32;
        private const int dense_vertex_cap = 60000;

        private bool tracing;
        private readonly List<TracePoint> points = new List<TracePoint>();
        private Vector3 hover_point;
        private bool hover_on_mesh;
        private bool has_hover;

        private static readonly Color color_on_mesh = Color.yellow;
        private static readonly Color color_inferred = new Color(1f, 0.45f, 0.1f);

        [MenuItem("Tools/Semantic Mesh/Surface Tracer")]
        public static void Open()
        {
            var window = GetWindow<SurfaceTracerWindow>("Surface Tracer");
            window.minSize = new Vector2(300, 320);
            window.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGui;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGui;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Semantic Surface Tracer", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            surface_root = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("Surface Root",
                    "Author surfaces in this transform's local space. Parent it under the Digital Twin so the handoff prefab aligns at any world scale. Leave empty for world-space authoring."),
                surface_root, typeof(Transform), true);
            if (surface_root == null)
            {
                EditorGUILayout.HelpBox("No Surface Root: authoring in world space (fallback). Set the twin root before the final pass.", MessageType.None);
            }

            EditorGUILayout.Space();
            surface_type = (SurfaceType)EditorGUILayout.EnumPopup("Surface Type", surface_type);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Drape", EditorStyles.boldLabel);
            dense_drape = EditorGUILayout.ToggleLeft(
                new GUIContent("Dense interior sampling (better stairs/ramps)",
                    "Subdivide the region and raycast the real scan mesh at each interior point so the drape follows true steps instead of interpolating corner-to-corner. Always on for Stairs. Needs the target collider present. Judge the final look on device / a real scan."),
                dense_drape);
            bool spacing_visible = dense_drape || surface_type == SurfaceType.Stairs;
            using (new EditorGUI.DisabledScope(!spacing_visible))
            {
                sample_spacing = EditorGUILayout.FloatField(
                    new GUIContent("Sample spacing (m)",
                        "Real-world meters between interior samples. Smaller = denser and slower. Converted to the Surface Root's local units automatically."),
                    sample_spacing);
                if (sample_spacing < 0.01f)
                {
                    sample_spacing = 0.01f;
                }
            }
            if (surface_type == SurfaceType.Stairs && !dense_drape)
            {
                EditorGUILayout.HelpBox("Stairs always use dense sampling so the grid follows the steps.", MessageType.None);
            }

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(tracing))
            {
                if (GUILayout.Button("Start Tracing", GUILayout.Height(28)))
                {
                    tracing = true;
                    points.Clear();
                    SceneView.RepaintAll();
                }
            }

            using (new EditorGUI.DisabledScope(!tracing))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Close Shape (Enter)"))
                    {
                        finalize_shape();
                    }
                    if (GUILayout.Button("Undo Point"))
                    {
                        remove_last_point();
                    }
                }
                if (GUILayout.Button("Cancel (Esc)"))
                {
                    cancel_shape();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                tracing
                    ? "Left-click the target mesh to drop points. Over a hole the point drops onto a working plane (orange). Drag any point to nudge it. Enter closes the shape, Backspace removes the last point, Esc cancels."
                    : "Click Start Tracing, then click points in the Scene view. The target needs a collider: use \"Prepare Target\" below.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Target helpers", EditorStyles.boldLabel);
            if (GUILayout.Button("Prepare Target (add MeshCollider to selection)"))
            {
                prepare_target();
            }
            if (GUILayout.Button("Add Depth Occluder to selection subtree"))
            {
                add_occluder_to_selection();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Handoff", EditorStyles.boldLabel);
            if (GUILayout.Button("Save Surfaces as ONE Prefab"))
            {
                save_as_prefab();
            }
            if (GUILayout.Button("Save a Prefab PER Surface Type"))
            {
                save_prefabs_per_type();
            }

            if (tracing)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField($"Points placed: {points.Count}");
            }
        }

        private void OnSceneGui(SceneView scene_view)
        {
            if (!tracing)
            {
                return;
            }

            var e = Event.current;
            int control_id = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(control_id);

            // Only refresh the hover point when no handle is being dragged, so a
            // drag does not fight the placement raycast.
            if (GUIUtility.hotControl == 0)
            {
                has_hover = resolve_pointer(e.mousePosition, out hover_point, out hover_on_mesh);
            }

            // Place a point only when the plain scene (not a point handle) is the
            // nearest control, so grabbing a handle never also drops a point.
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt
                && HandleUtility.nearestControl == control_id)
            {
                if (has_hover)
                {
                    points.Add(new TracePoint(hover_point, hover_on_mesh));
                    e.Use();
                    Repaint();
                }
            }
            else if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    finalize_shape();
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Backspace)
                {
                    remove_last_point();
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    cancel_shape();
                    e.Use();
                }
            }

            draw_point_handles();
            draw_scene_gizmos();

            if (e.type == EventType.MouseMove)
            {
                scene_view.Repaint();
            }
        }

        // A draggable handle per placed point. On drag, re-snap to the mesh (or
        // the working plane if the mesh is missed there) so the point stays glued
        // to the surface. Item 4.
        private void draw_point_handles()
        {
            for (int i = 0; i < points.Count; i++)
            {
                Handles.color = points[i].on_mesh ? color_on_mesh : color_inferred;
                float size = HandleUtility.GetHandleSize(points[i].world) * 0.09f;

                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(points[i].world, size, Vector3.zero, Handles.SphereHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    if (resolve_pointer(HandleUtility.WorldToGUIPoint(moved), out Vector3 snapped, out bool on_mesh))
                    {
                        points[i] = new TracePoint(snapped, on_mesh);
                    }
                    else
                    {
                        points[i] = new TracePoint(moved, false);
                    }
                    Repaint();
                }
            }
        }

        private void draw_scene_gizmos()
        {
            // Connecting outline between placed points.
            Handles.color = color_on_mesh;
            for (int i = 1; i < points.Count; i++)
            {
                Handles.DrawLine(points[i - 1].world, points[i].world);
            }

            if (points.Count > 0 && has_hover && GUIUtility.hotControl == 0)
            {
                Handles.color = new Color(1f, 1f, 1f, 0.5f);
                Handles.DrawDottedLine(points[points.Count - 1].world, hover_point, 4f);
                if (points.Count > 1)
                {
                    Handles.color = new Color(0f, 1f, 0.5f, 0.35f);
                    Handles.DrawDottedLine(hover_point, points[0].world, 2f);
                }

                Handles.color = hover_on_mesh ? Color.cyan : color_inferred;
                float size = HandleUtility.GetHandleSize(hover_point) * 0.05f;
                Handles.SphereHandleCap(0, hover_point, Quaternion.identity, size, EventType.Repaint);
            }
        }

        // Resolve where the pointer lands: the real collider if the ray hits it,
        // otherwise the working plane over a hole. on_mesh reports which. Returns
        // false only when there is nothing to place against (no hit and no plane).
        private bool resolve_pointer(Vector2 gui_position, out Vector3 world, out bool on_mesh)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(gui_position);
            if (Physics.Raycast(ray, out RaycastHit hit, 5000f))
            {
                world = hit.point;
                on_mesh = true;
                return true;
            }

            if (try_working_plane(out Plane plane) && plane.Raycast(ray, out float enter))
            {
                world = ray.GetPoint(enter);
                on_mesh = false;
                return true;
            }

            world = Vector3.zero;
            on_mesh = false;
            return false;
        }

        // The plane used to place points over a hole (item 3): the best-fit plane
        // of already-placed points when there are >= 3, else a horizontal plane
        // through the last placed point. False when no points exist yet.
        private bool try_working_plane(out Plane plane)
        {
            if (points.Count >= 3)
            {
                Vector3 centroid = Vector3.zero;
                for (int i = 0; i < points.Count; i++)
                {
                    centroid += points[i].world;
                }
                centroid /= points.Count;

                Vector3 normal = newell_normal(points);
                if (normal.sqrMagnitude < 1e-8f)
                {
                    normal = Vector3.up;
                }
                else
                {
                    normal.Normalize();
                }
                plane = new Plane(normal, centroid);
                return true;
            }

            if (points.Count >= 1)
            {
                plane = new Plane(Vector3.up, points[points.Count - 1].world);
                return true;
            }

            plane = default;
            return false;
        }

        private void remove_last_point()
        {
            if (points.Count > 0)
            {
                points.RemoveAt(points.Count - 1);
                Repaint();
                SceneView.RepaintAll();
            }
        }

        private void cancel_shape()
        {
            points.Clear();
            tracing = false;
            Repaint();
            SceneView.RepaintAll();
        }

        private void finalize_shape()
        {
            if (points.Count < 3)
            {
                EditorUtility.DisplayDialog("Surface Tracer", "Place at least 3 points before closing the shape.", "OK");
                return;
            }

            var surface = build_surface(points, surface_type);
            if (surface != null)
            {
                Undo.RegisterCreatedObjectUndo(surface, "Create Semantic Surface");
                Selection.activeGameObject = surface;
            }

            points.Clear();
            tracing = false;
            Repaint();
            SceneView.RepaintAll();
        }

        // Fit a plane to the traced points to get a 2D basis for triangulation and
        // UVs, then DRAPE: keep the real (root-local) hit heights as the mesh
        // vertices instead of flattening them onto the plane. Authored in the
        // Surface Root's local space when one is set (item 1) so the deliverable
        // aligns wherever the root is parented.
        private GameObject build_surface(List<TracePoint> trace, SurfaceType type)
        {
            int n = trace.Count;

            // Move into the working space: root-local when a root is set, else world.
            var pts = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                pts[i] = surface_root != null
                    ? surface_root.InverseTransformPoint(trace[i].world)
                    : trace[i].world;
            }

            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                centroid += pts[i];
            }
            centroid /= n;

            // Newell's method: robust plane normal for an arbitrary polygon.
            Vector3 normal = newell_normal_arr(pts);
            if (normal.sqrMagnitude < 1e-8f)
            {
                normal = Vector3.up;
            }
            normal.Normalize();

            // Build a plane basis.
            Vector3 axis_u = Vector3.Cross(normal, Vector3.up);
            if (axis_u.sqrMagnitude < 1e-6f)
            {
                axis_u = Vector3.Cross(normal, Vector3.right);
            }
            axis_u.Normalize();
            Vector3 axis_v = Vector3.Cross(normal, axis_u).normalized;

            // Project points to 2D plane coords (meters), relative to centroid.
            // These feed triangulation and UVs only, not the vertex heights.
            var uv2d = new List<Vector2>(n);
            for (int i = 0; i < n; i++)
            {
                Vector3 rel = pts[i] - centroid;
                uv2d.Add(new Vector2(Vector3.Dot(rel, axis_u), Vector3.Dot(rel, axis_v)));
            }

            var triangles = PolygonTriangulator.Triangulate(uv2d);
            if (triangles.Count < 3)
            {
                EditorUtility.DisplayDialog("Surface Tracer",
                    "Could not triangulate that shape. Avoid self-intersecting outlines.", "OK");
                return null;
            }

            // DRAPE: vertices sit at real heights. Corner vertices are the ORIGINAL
            // hit points (minus centroid), so each keeps its true click height;
            // over holes the working-plane height is used. UVs come from the flat
            // projection so the grid reads cleanly.
            List<Vector3> vertices;
            List<Vector2> uvs;
            List<int> tris;

            // Stairs always drape densely; other types opt in (roadmap 9.7). Dense
            // subdivides each triangle and raycasts the real scan mesh at every new
            // interior point, so the surface follows steps instead of interpolating
            // corner-to-corner.
            bool dense = dense_drape || type == SurfaceType.Stairs;
            if (dense && build_dense_geometry(pts, centroid, axis_u, axis_v, normal,
                    uv2d, triangles, out vertices, out uvs, out tris))
            {
                // dense geometry produced.
            }
            else
            {
                vertices = new List<Vector3>(n);
                uvs = new List<Vector2>(n);
                for (int i = 0; i < n; i++)
                {
                    vertices.Add(pts[i] - centroid);
                    uvs.Add(uv2d[i]);
                }
                tris = triangles;
            }

            var mesh = new Mesh { name = $"SemanticSurface_{type}" };
            if (vertices.Count > 65534)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject($"Semantic_{type}");
            var container = get_or_create_container(surface_root);

            if (surface_root != null)
            {
                // Container is at local identity under the root, so a local
                // centroid places the surface exactly over the traced points.
                go.transform.SetParent(container.transform, false);
                go.transform.localPosition = centroid;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
            }
            else
            {
                go.transform.position = centroid;
                go.transform.rotation = Quaternion.identity;
                go.transform.SetParent(container.transform, true);
            }

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mesh_renderer = go.AddComponent<MeshRenderer>();
            mesh_renderer.sharedMaterial = load_grid_material();
            mesh_renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var semantic = go.AddComponent<SemanticSurface>();
            semantic.surface_type = type;
            semantic.apply_visual();

            return go;
        }

        // Dense drape (roadmap 9.7). Subdivide every boundary triangle to a single
        // GLOBAL level so shared edges split identically (no T-junction cracks),
        // then place each new vertex by raycasting the real collider. Corner
        // vertices dedup back to the exact click points, keeping their real
        // heights. Returns false (caller falls back to the corner-only mesh) if the
        // subdivision would blow past the vertex cap.
        //
        // Everything here is in the Surface Root's LOCAL space (pts, centroid,
        // axes, normal are already local); raycasts convert to world and back so
        // the drape works at any twin scale.
        private bool build_dense_geometry(
            Vector3[] pts, Vector3 centroid, Vector3 axis_u, Vector3 axis_v, Vector3 normal,
            List<Vector2> uv2d, List<int> boundary_tris,
            out List<Vector3> out_verts, out List<Vector2> out_uvs, out List<int> out_tris)
        {
            out_verts = new List<Vector3>();
            out_uvs = new List<Vector2>();
            out_tris = new List<int>();

            // Local aliases: the get_or_add local function cannot capture the out
            // parameters directly (C# forbids it), so it appends to these instead.
            // They reference the same list objects the out parameters point to.
            var verts = out_verts;
            var uvs = out_uvs;
            var tris = out_tris;

            int n = pts.Length;
            if (boundary_tris.Count < 3)
            {
                return false;
            }

            // Sample spacing is authored in real-world meters; convert to local
            // units so the density is stable regardless of twin scale (the 9.3
            // caveat: local units are 1/scale world meters).
            float root_scale = 1f;
            if (surface_root != null)
            {
                Vector3 ls = surface_root.lossyScale;
                root_scale = (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f;
            }
            if (root_scale < 1e-6f)
            {
                root_scale = 1f;
            }
            float local_spacing = Mathf.Max(1e-4f, sample_spacing / root_scale);

            // One global subdivision level from the longest triangle edge, so every
            // triangle splits the same way and shared edges line up exactly.
            float max_edge = 0f;
            for (int t = 0; t < boundary_tris.Count; t += 3)
            {
                Vector2 a = uv2d[boundary_tris[t]];
                Vector2 b = uv2d[boundary_tris[t + 1]];
                Vector2 c = uv2d[boundary_tris[t + 2]];
                max_edge = Mathf.Max(max_edge, (a - b).magnitude, (b - c).magnitude, (c - a).magnitude);
            }
            int level = Mathf.Clamp(Mathf.CeilToInt(max_edge / local_spacing), 1, dense_subdiv_cap);

            // Rough vertex-count guard before committing to the raycasts.
            int tri_count = boundary_tris.Count / 3;
            long est_verts = (long)tri_count * (level + 1) * (level + 2) / 2;
            if (est_verts > dense_vertex_cap)
            {
                Debug.LogWarning($"[SemanticMesh] Dense drape skipped: estimated {est_verts} vertices exceeds cap {dense_vertex_cap}. Increase Sample spacing. Falling back to corner drape.");
                return false;
            }

            // Raycast setup: cast along the surface normal in WORLD space. The cast
            // range spans the boundary's deviation from the fitted plane plus a
            // margin, so real steps within that band are captured.
            Vector3 world_normal = surface_root != null
                ? surface_root.TransformDirection(normal).normalized
                : normal.normalized;
            Vector3 world_centroid = surface_root != null
                ? surface_root.TransformPoint(centroid)
                : centroid;
            float max_abs = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 wp = surface_root != null ? surface_root.TransformPoint(pts[i]) : pts[i];
                max_abs = Mathf.Max(max_abs, Mathf.Abs(Vector3.Dot(wp - world_centroid, world_normal)));
            }
            float cast_range = max_abs + 0.5f;
            float cast_dist = cast_range * 2f;

            var vert_lookup = new Dictionary<long, int>();

            // Seed the boundary corners so they dedup to their real click heights.
            for (int i = 0; i < n; i++)
            {
                long key = quantize_uv(uv2d[i]);
                if (!vert_lookup.ContainsKey(key))
                {
                    vert_lookup[key] = verts.Count;
                    verts.Add(pts[i] - centroid);
                    uvs.Add(uv2d[i]);
                }
            }

            // Local: turn a 2D plane coord into a mesh vertex, raycasting the real
            // mesh for its height and falling back to the flat plane on a miss.
            int get_or_add(Vector2 coord)
            {
                long key = quantize_uv(coord);
                if (vert_lookup.TryGetValue(key, out int existing))
                {
                    return existing;
                }

                Vector3 plane_local = centroid + coord.x * axis_u + coord.y * axis_v;
                Vector3 vert_local = plane_local - centroid;

                Vector3 world_plane = surface_root != null
                    ? surface_root.TransformPoint(plane_local)
                    : plane_local;
                Vector3 origin = world_plane + world_normal * cast_range;
                if (Physics.Raycast(origin, -world_normal, out RaycastHit hit, cast_dist))
                {
                    Vector3 hit_local = surface_root != null
                        ? surface_root.InverseTransformPoint(hit.point)
                        : hit.point;
                    vert_local = hit_local - centroid;
                }

                int idx = verts.Count;
                vert_lookup[key] = idx;
                verts.Add(vert_local);
                uvs.Add(coord);
                return idx;
            }

            // Barycentric lattice over each boundary triangle at the global level.
            for (int t = 0; t < boundary_tris.Count; t += 3)
            {
                Vector2 a = uv2d[boundary_tris[t]];
                Vector2 b = uv2d[boundary_tris[t + 1]];
                Vector2 c = uv2d[boundary_tris[t + 2]];

                var lattice = new int[level + 1][];
                for (int i = 0; i <= level; i++)
                {
                    lattice[i] = new int[level + 1 - i];
                    for (int j = 0; j <= level - i; j++)
                    {
                        float wa = (float)(level - i - j) / level;
                        float wb = (float)i / level;
                        float wc = (float)j / level;
                        Vector2 coord = wa * a + wb * b + wc * c;
                        lattice[i][j] = get_or_add(coord);
                    }
                }

                for (int i = 0; i < level; i++)
                {
                    for (int j = 0; j < level - i; j++)
                    {
                        tris.Add(lattice[i][j]);
                        tris.Add(lattice[i + 1][j]);
                        tris.Add(lattice[i][j + 1]);
                        if (i + j < level - 1)
                        {
                            tris.Add(lattice[i + 1][j]);
                            tris.Add(lattice[i + 1][j + 1]);
                            tris.Add(lattice[i][j + 1]);
                        }
                    }
                }
            }

            return tris.Count >= 3;
        }

        // Quantize a 2D plane coord to a stable integer key so points shared
        // between triangles (and the seeded corners) merge to one vertex.
        private static long quantize_uv(Vector2 uv)
        {
            long kx = (long)Mathf.RoundToInt(uv.x * 10000f);
            long ky = (long)Mathf.RoundToInt(uv.y * 10000f);
            return (kx << 32) ^ (ky & 0xffffffffL);
        }

        private static Vector3 newell_normal(IList<TracePoint> p)
        {
            Vector3 normal = Vector3.zero;
            int n = p.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 c = p[i].world;
                Vector3 nx = p[(i + 1) % n].world;
                normal.x += (c.y - nx.y) * (c.z + nx.z);
                normal.y += (c.z - nx.z) * (c.x + nx.x);
                normal.z += (c.x - nx.x) * (c.y + nx.y);
            }
            return normal;
        }

        private static Vector3 newell_normal_arr(IList<Vector3> p)
        {
            Vector3 normal = Vector3.zero;
            int n = p.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 c = p[i];
                Vector3 nx = p[(i + 1) % n];
                normal.x += (c.y - nx.y) * (c.z + nx.z);
                normal.y += (c.z - nx.z) * (c.x + nx.x);
                normal.z += (c.x - nx.x) * (c.y + nx.y);
            }
            return normal;
        }

        private static GameObject get_or_create_container(Transform root)
        {
            if (root != null)
            {
                var child = root.Find(container_name);
                if (child != null)
                {
                    return child.gameObject;
                }
                var scoped = new GameObject(container_name);
                Undo.RegisterCreatedObjectUndo(scoped, "Create Semantic Surfaces Container");
                scoped.transform.SetParent(root, false);
                scoped.transform.localPosition = Vector3.zero;
                scoped.transform.localRotation = Quaternion.identity;
                scoped.transform.localScale = Vector3.one;
                return scoped;
            }

            var existing = GameObject.Find(container_name);
            if (existing != null)
            {
                return existing;
            }
            var container = new GameObject(container_name);
            Undo.RegisterCreatedObjectUndo(container, "Create Semantic Surfaces Container");
            return container;
        }

        private static Material load_grid_material()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(grid_material_path);
            if (material == null)
            {
                Debug.LogWarning($"[SemanticMesh] Grid material not found at {grid_material_path}. Surface will use default material.");
            }
            return material;
        }

        // Write the whole "Semantic Surfaces" container out as the deliverable
        // prefab (item 5). Uses the container under the Surface Root when one is
        // set, so the prefab carries the root-local frame from item 1.
        private void save_as_prefab()
        {
            GameObject container = find_container();
            if (container == null)
            {
                EditorUtility.DisplayDialog("Surface Tracer",
                    "No \"Semantic Surfaces\" container found. Trace at least one surface first.", "OK");
                return;
            }

            var saved = save_prefab_with_meshes(container, prefab_path);
            if (saved != null)
            {
                AssetDatabase.Refresh();
                Selection.activeObject = saved;
                EditorGUIUtility.PingObject(saved);
                Debug.Log($"[SemanticMesh] Saved surfaces prefab to {prefab_path}.");
                show_export_report(container, prefab_path);
            }
            else
            {
                EditorUtility.DisplayDialog("Surface Tracer", $"Failed to save prefab to {prefab_path}.", "OK");
            }
        }

        // Save a live root as a prefab AND embed its procedural meshes into the
        // prefab asset (roadmap 9.2). Plain SaveAsPrefabAsset writes
        // MeshFilter.m_Mesh as null for any mesh that is not an asset, which every
        // traced surface's mesh is (it is built with new Mesh() in memory), so a
        // dropped prefab has no geometry at all. Fix: after saving, copy each
        // source mesh, AddObjectToAsset it INTO the prefab file, repoint the
        // MeshFilter, and re-save. Result is one self-contained prefab whose
        // geometry actually persists. Returns the saved asset, or null on failure.
        private static GameObject save_prefab_with_meshes(GameObject live_root, string path)
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(live_root, path, out bool ok);
            if (!ok || prefab == null)
            {
                return null;
            }

            var live_filters = live_root.GetComponentsInChildren<MeshFilter>(true);
            var prefab_filters = prefab.GetComponentsInChildren<MeshFilter>(true);
            // Both hierarchies are identical (the prefab was just saved from the
            // live root), so GetComponentsInChildren returns them in the same order.
            int count = Mathf.Min(live_filters.Length, prefab_filters.Length);
            bool embedded_any = false;
            for (int i = 0; i < count; i++)
            {
                var src = live_filters[i].sharedMesh;
                if (src == null)
                {
                    continue;
                }
                if (AssetDatabase.Contains(src))
                {
                    // Already a persisted asset (e.g. a re-export): keep the ref.
                    prefab_filters[i].sharedMesh = src;
                    continue;
                }
                var copy = Object.Instantiate(src);
                copy.name = string.IsNullOrEmpty(src.name) ? $"SurfaceMesh_{i}" : src.name;
                AssetDatabase.AddObjectToAsset(copy, prefab);
                prefab_filters[i].sharedMesh = copy;
                embedded_any = true;
            }

            if (embedded_any)
            {
                PrefabUtility.SavePrefabAsset(prefab);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(path);
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }
            return prefab;
        }

        // Post-export summary (roadmap 9.2): surface count, world bounds, and the
        // twin-local reminder so "nothing came over" is not mistaken for a failed
        // save when it is really a scale/parenting issue.
        private static void show_export_report(GameObject container, string path)
        {
            var surfaces = container.GetComponentsInChildren<SemanticSurface>(true);
            var renderers = container.GetComponentsInChildren<MeshRenderer>(true);
            Bounds bounds = default;
            bool has_bounds = false;
            foreach (var r in renderers)
            {
                if (!has_bounds)
                {
                    bounds = r.bounds;
                    has_bounds = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }
            string bounds_str = has_bounds
                ? $"{bounds.size.x:F2} x {bounds.size.y:F2} x {bounds.size.z:F2} m (world)"
                : "n/a";

            EditorUtility.DisplayDialog("Surface Tracer",
                $"Saved {surfaces.Length} surface(s) to\n{path}\n\n" +
                $"World bounds: {bounds_str}\n\n" +
                "The prefab is authored in the Surface Root's LOCAL frame. Parent it under the twin (or a 0.01-scale root) and zero its local transform to see it correctly. Dropped into a bare scene at scale 1 it looks ~100x too big and off-screen (not missing).",
                "OK");
        }

        // Export one prefab per SurfaceType (item Thomas asked for): all Walkable
        // surfaces to Semantic_Walkable.prefab, all Grass to Semantic_Grass.prefab,
        // etc. Clones the surfaces into a fresh per-type root that mirrors the
        // container's frame, so the working scene is left untouched.
        private void save_prefabs_per_type()
        {
            GameObject container = find_container();
            if (container == null)
            {
                EditorUtility.DisplayDialog("Surface Tracer",
                    "No \"Semantic Surfaces\" container found. Trace at least one surface first.", "OK");
                return;
            }

            var by_type = new Dictionary<SurfaceType, List<SemanticSurface>>();
            foreach (var surface in container.GetComponentsInChildren<SemanticSurface>(true))
            {
                if (!by_type.TryGetValue(surface.surface_type, out var list))
                {
                    list = new List<SemanticSurface>();
                    by_type[surface.surface_type] = list;
                }
                list.Add(surface);
            }

            if (by_type.Count == 0)
            {
                EditorUtility.DisplayDialog("Surface Tracer",
                    "The container has no tagged surfaces to export.", "OK");
                return;
            }

            int saved = 0;
            foreach (var pair in by_type)
            {
                // A temp root sharing the container's frame, so cloned surfaces keep
                // their exact placement inside the exported prefab.
                var temp = new GameObject($"Semantic_{pair.Key}");
                temp.transform.SetParent(container.transform.parent, false);
                temp.transform.localPosition = container.transform.localPosition;
                temp.transform.localRotation = container.transform.localRotation;
                temp.transform.localScale = container.transform.localScale;

                foreach (var surface in pair.Value)
                {
                    var clone = Object.Instantiate(surface.gameObject);
                    clone.name = surface.gameObject.name;
                    clone.transform.SetParent(temp.transform, true);
                }

                string path = $"Assets/SemanticMesh/Semantic_{pair.Key}.prefab";
                var saved_go = save_prefab_with_meshes(temp, path);
                if (saved_go != null)
                {
                    saved++;
                }
                else
                {
                    Debug.LogWarning($"[SemanticMesh] Failed to save {path}.");
                }
                Object.DestroyImmediate(temp);
            }

            AssetDatabase.Refresh();
            Debug.Log($"[SemanticMesh] Saved {saved} per-type prefab(s) to Assets/SemanticMesh/.");
            EditorUtility.DisplayDialog("Surface Tracer",
                $"Saved {saved} prefab(s), one per surface type, to Assets/SemanticMesh/.", "OK");
        }

        private GameObject find_container()
        {
            if (surface_root != null)
            {
                var child = surface_root.Find(container_name);
                return child != null ? child.gameObject : null;
            }
            return GameObject.Find(container_name);
        }

        private void prepare_target()
        {
            var targets = Selection.gameObjects;
            if (targets == null || targets.Length == 0)
            {
                EditorUtility.DisplayDialog("Surface Tracer",
                    "Select the high-res mesh object(s) in the Hierarchy first.", "OK");
                return;
            }

            int added = 0;
            foreach (var go in targets)
            {
                foreach (var filter in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.GetComponent<MeshCollider>() == null && filter.sharedMesh != null)
                    {
                        Undo.AddComponent<MeshCollider>(filter.gameObject);
                        added++;
                    }
                }
            }
            Debug.Log($"[SemanticMesh] Added {added} MeshCollider(s) for tracing.");
        }

        // Add the depth occluder to every MeshRenderer under each selected root in
        // one click (item 6). GetComponentsInChildren already walks the subtree,
        // including inactive renderers.
        private void add_occluder_to_selection()
        {
            var occluder = AssetDatabase.LoadAssetAtPath<Material>(occluder_material_path);
            if (occluder == null)
            {
                EditorUtility.DisplayDialog("Surface Tracer",
                    $"Occluder material not found at {occluder_material_path}.", "OK");
                return;
            }

            var targets = Selection.gameObjects;
            if (targets == null || targets.Length == 0)
            {
                EditorUtility.DisplayDialog("Surface Tracer",
                    "Select the wall / environment root(s) whose whole subtree should hide things behind it.", "OK");
                return;
            }

            int changed = 0;
            foreach (var go in targets)
            {
                foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var materials = new List<Material>(renderer.sharedMaterials);
                    if (materials.Contains(occluder))
                    {
                        continue;
                    }
                    Undo.RecordObject(renderer, "Add Depth Occluder");
                    materials.Add(occluder);
                    renderer.sharedMaterials = materials.ToArray();
                    changed++;
                }
            }
            Debug.Log($"[SemanticMesh] Added depth occluder to {changed} renderer(s).");
        }
    }
}
