using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SemanticMesh.EditorTools
{
    // Scene-view tool for tracing low-poly semantic planes over a high-res mesh.
    //
    // Workflow: open the window, pick a surface type, click Start Tracing, then
    // left-click in the Scene view to drop points onto the target mesh. Close
    // the shape (Enter or the button) to generate a flat plane, tag it with a
    // SemanticSurface, and apply the grid material. Backspace removes the last
    // point, Escape cancels the in-progress shape.
    public class SurfaceTracerWindow : EditorWindow
    {
        private const string grid_material_path = "Assets/SemanticMesh/Material/M_SemanticGrid.mat";
        private const string occluder_material_path = "Assets/SemanticMesh/Material/M_DepthOccluder.mat";
        private const string container_name = "Semantic Surfaces";

        private SurfaceType surface_type = SurfaceType.Walkable;
        private bool tracing;
        private readonly List<Vector3> points = new List<Vector3>();
        private Vector3 hover_point;
        private bool has_hover;

        [MenuItem("Tools/Semantic Mesh/Surface Tracer")]
        public static void Open()
        {
            var window = GetWindow<SurfaceTracerWindow>("Surface Tracer");
            window.minSize = new Vector2(280, 260);
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

            surface_type = (SurfaceType)EditorGUILayout.EnumPopup("Surface Type", surface_type);

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
                    ? "Left-click the target mesh to drop points. The target needs a collider: use \"Prepare Target\" below. Enter closes the shape, Backspace removes the last point, Esc cancels."
                    : "Click Start Tracing, then click points in the Scene view.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Target helpers", EditorStyles.boldLabel);
            if (GUILayout.Button("Prepare Target (add MeshCollider to selection)"))
            {
                prepare_target();
            }
            if (GUILayout.Button("Add Depth Occluder to selection"))
            {
                add_occluder_to_selection();
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

            // Raycast the mesh under the cursor.
            has_hover = raycast_scene(e.mousePosition, out hover_point);

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                if (has_hover)
                {
                    points.Add(hover_point);
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

            draw_scene_gizmos();

            if (e.type == EventType.MouseMove)
            {
                scene_view.Repaint();
            }
        }

        private void draw_scene_gizmos()
        {
            Handles.color = Color.yellow;
            for (int i = 0; i < points.Count; i++)
            {
                float size = HandleUtility.GetHandleSize(points[i]) * 0.06f;
                Handles.SphereHandleCap(0, points[i], Quaternion.identity, size, EventType.Repaint);
                if (i > 0)
                {
                    Handles.DrawLine(points[i - 1], points[i]);
                }
            }

            if (points.Count > 0 && has_hover)
            {
                Handles.color = new Color(1f, 1f, 1f, 0.5f);
                Handles.DrawDottedLine(points[points.Count - 1], hover_point, 4f);
                if (points.Count > 1)
                {
                    Handles.color = new Color(0f, 1f, 0.5f, 0.35f);
                    Handles.DrawDottedLine(hover_point, points[0], 2f);
                }
            }

            if (has_hover)
            {
                Handles.color = Color.cyan;
                float size = HandleUtility.GetHandleSize(hover_point) * 0.05f;
                Handles.SphereHandleCap(0, hover_point, Quaternion.identity, size, EventType.Repaint);
            }
        }

        private static bool raycast_scene(Vector2 gui_position, out Vector3 hit_point)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(gui_position);
            if (Physics.Raycast(ray, out RaycastHit hit, 5000f))
            {
                hit_point = hit.point;
                return true;
            }
            hit_point = Vector3.zero;
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

        // Fit a plane to the traced points, project them onto it, triangulate in
        // 2D, and build a centered mesh whose UVs are meters on the plane.
        private GameObject build_surface(List<Vector3> world_points, SurfaceType type)
        {
            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < world_points.Count; i++)
            {
                centroid += world_points[i];
            }
            centroid /= world_points.Count;

            // Newell's method: robust plane normal for an arbitrary polygon.
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < world_points.Count; i++)
            {
                Vector3 current = world_points[i];
                Vector3 next = world_points[(i + 1) % world_points.Count];
                normal.x += (current.y - next.y) * (current.z + next.z);
                normal.y += (current.z - next.z) * (current.x + next.x);
                normal.z += (current.x - next.x) * (current.y + next.y);
            }
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
            var uv2d = new List<Vector2>(world_points.Count);
            for (int i = 0; i < world_points.Count; i++)
            {
                Vector3 rel = world_points[i] - centroid;
                uv2d.Add(new Vector2(Vector3.Dot(rel, axis_u), Vector3.Dot(rel, axis_v)));
            }

            var triangles = PolygonTriangulator.Triangulate(uv2d);
            if (triangles.Count < 3)
            {
                EditorUtility.DisplayDialog("Surface Tracer",
                    "Could not triangulate that shape. Avoid self-intersecting outlines.", "OK");
                return null;
            }

            // Vertices are on the fitted plane (flattened), local to centroid.
            var vertices = new Vector3[world_points.Count];
            var normals = new Vector3[world_points.Count];
            var uvs = new Vector2[world_points.Count];
            for (int i = 0; i < world_points.Count; i++)
            {
                vertices[i] = axis_u * uv2d[i].x + axis_v * uv2d[i].y;
                normals[i] = normal;
                uvs[i] = uv2d[i];
            }

            var mesh = new Mesh { name = $"SemanticSurface_{type}" };
            mesh.SetVertices(new List<Vector3>(vertices));
            mesh.SetNormals(new List<Vector3>(normals));
            mesh.SetUVs(0, new List<Vector2>(uvs));
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();

            var go = new GameObject($"Semantic_{type}");
            go.transform.position = centroid;
            go.transform.rotation = Quaternion.identity;

            var container = get_or_create_container();
            go.transform.SetParent(container.transform, true);

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mesh_renderer = go.AddComponent<MeshRenderer>();
            mesh_renderer.sharedMaterial = load_grid_material();
            mesh_renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var semantic = go.AddComponent<SemanticSurface>();
            semantic.surface_type = type;
            semantic.apply_visual();

            return go;
        }

        private static GameObject get_or_create_container()
        {
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
                foreach (var filter in go.GetComponentsInChildren<MeshFilter>())
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
                    "Select the wall / environment renderer(s) that should hide things behind them.", "OK");
                return;
            }

            int changed = 0;
            foreach (var go in targets)
            {
                foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>())
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
