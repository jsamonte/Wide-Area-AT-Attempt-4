using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SemanticMesh.EditorTools
{
    // Scene-view tool for drawing PERSISTENT polylines over a scanned mesh, as a
    // sibling to SurfaceTracerWindow. Same point-picking feel (click to place,
    // drag to nudge, Backspace to undo, Esc to cancel), but instead of
    // triangulating a filled panel it commits each finished run of points to a
    // real LineRenderer GameObject that stays in the scene, ships in a build, and
    // records into PLUME.
    //
    // Why LineRenderer: PLUME's MeshFilter recorder serializes only an asset GUID
    // for a mesh, never vertices or topology, so a procedurally built
    // MeshTopology.Lines mesh records as an unresolvable reference and draws
    // nothing in the viewer. The LineRenderer recorder writes positions, width
    // and color directly into the sample stream. Lines topology is additionally
    // pinned to 1px on Magic Leap 2, which shimmers on a see-through display.
    public class LineTracerWindow : EditorWindow
    {
        private const string line_material_path = "Assets/SemanticMesh/Material/M_SemanticLine.mat";
        private const string container_name = "Semantic Lines";
        private const string prefab_path = "Assets/SemanticMesh/Semantic_Lines.prefab";

        // What "End Line Segment" does once the current polyline is committed.
        private enum EndBehavior
        {
            // Commit and immediately begin another polyline: the usual mode when
            // marking up a whole space in one sitting.
            StartNewLine = 0,

            // Commit and leave tracing mode, so the next click is a normal Scene
            // view selection again.
            StopTracing = 1,
        }

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

        // Author in this transform's local space so the lines stay glued to the
        // twin when it is moved or rescaled. Null = world-space fallback.
        [SerializeField] private Transform surface_root;

        private SurfaceType surface_type = SurfaceType.Generic;

        // Stroke width in real-world meters. SemanticLine divides by the root's
        // scale at apply time, so this stays meaningful under a 0.01-scale twin.
        [SerializeField] private float world_width = 0.02f;

        // Close the polyline back to its first point.
        [SerializeField] private bool loop;

        [SerializeField] private EndBehavior end_behavior = EndBehavior.StartNewLine;

        private bool tracing;
        private readonly List<TracePoint> points = new List<TracePoint>();
        private Vector3 hover_point;
        private bool hover_on_mesh;
        private bool has_hover;
        private int committed_this_session;

        // Colliders present when tracing started. -1 = not checked yet. Surfaced
        // in the window so "my clicks do nothing" reads as "nothing to click on"
        // instead of looking like a broken tool.
        private int collider_count = -1;

        // Name of the object the pointer ray last landed on. Shown in the window
        // so "it clicked through the surface" becomes "it hit THAT object instead"
        // without any guessing.
        private string last_hit_name;

        private static readonly Color color_on_mesh = Color.yellow;
        private static readonly Color color_inferred = new Color(1f, 0.45f, 0.1f);

        [MenuItem("Tools/Semantic Mesh/Line Tracer")]
        public static void Open()
        {
            var window = GetWindow<LineTracerWindow>("Line Tracer");
            window.minSize = new Vector2(300, 340);
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
            EditorGUILayout.LabelField("Semantic Line Tracer", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            surface_root = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("Surface Root",
                    "Author lines in this transform's local space so they follow the twin when it moves or rescales. Leave empty for world-space authoring."),
                surface_root, typeof(Transform), true);
            if (surface_root == null)
            {
                EditorGUILayout.HelpBox("No Surface Root: authoring in world space (fallback). Set the twin root before the final pass.", MessageType.None);
            }

            EditorGUILayout.Space();
            surface_type = (SurfaceType)EditorGUILayout.EnumPopup(
                new GUIContent("Category", "Drives the line color, read from the same SurfacePalette the filled surfaces use."),
                surface_type);

            world_width = EditorGUILayout.FloatField(
                new GUIContent("Width (m)", "Stroke width in real-world meters. Divided by the Surface Root's scale automatically, so a 0.01-scale twin does not render it 100x too thin."),
                world_width);
            if (world_width < 0.001f)
            {
                world_width = 0.001f;
            }

            loop = EditorGUILayout.ToggleLeft(
                new GUIContent("Closed loop", "Join the last point back to the first."),
                loop);

            EditorGUILayout.Space();
            end_behavior = (EndBehavior)EditorGUILayout.EnumPopup(
                new GUIContent("On End Segment",
                    "What Enter / the End Line Segment button does after committing the current polyline."),
                end_behavior);
            EditorGUILayout.HelpBox(
                end_behavior == EndBehavior.StartNewLine
                    ? "Enter commits this line and immediately starts another. Use \"End & Stop\" (or Shift+Enter) to commit and leave tracing."
                    : "Enter commits this line and leaves tracing mode. Use \"End & Keep Tracing\" (or Shift+Enter) to commit and start another.",
                MessageType.None);

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(tracing))
            {
                if (GUILayout.Button("Start Tracing", GUILayout.Height(28)))
                {
                    tracing = true;
                    points.Clear();
                    committed_this_session = 0;
                    collider_count = Object.FindObjectsOfType<Collider>().Length;
                    if (collider_count == 0)
                    {
                        Debug.LogWarning("[SemanticMesh] Line Tracer started, but the scene has no active colliders. Clicks will land on a flat working plane, not the scan. Select the scan mesh and use \"Prepare Target\".");
                    }
                    SceneView.RepaintAll();
                }
            }

            using (new EditorGUI.DisabledScope(!tracing))
            {
                if (GUILayout.Button("End Line Segment (Enter)", GUILayout.Height(24)))
                {
                    end_segment(end_behavior);
                }

                // The other mode is always one click away, so neither choice is
                // buried in the dropdown mid-trace.
                var other = end_behavior == EndBehavior.StartNewLine
                    ? EndBehavior.StopTracing
                    : EndBehavior.StartNewLine;
                string other_label = other == EndBehavior.StopTracing
                    ? "End & Stop (Shift+Enter)"
                    : "End & Keep Tracing (Shift+Enter)";
                if (GUILayout.Button(other_label))
                {
                    end_segment(other);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Undo Point (Backspace)"))
                    {
                        remove_last_point();
                    }
                    if (GUILayout.Button("Cancel (Esc)"))
                    {
                        cancel_shape();
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                tracing
                    ? "Left-click the target to drop points. Over a hole the point drops onto a working plane (orange). Drag any point to nudge it. Enter ends the segment, Backspace removes the last point, Esc discards it."
                    : "Click Start Tracing, then click points in the Scene view. The target needs a collider: use \"Prepare Target\" below.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Target helpers", EditorStyles.boldLabel);
            if (GUILayout.Button("Prepare ENTIRE Scene (add colliders to every mesh)", GUILayout.Height(24)))
            {
                prepare_whole_scene();
            }
            if (GUILayout.Button("Prepare Target (selection only)"))
            {
                prepare_target();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Handoff", EditorStyles.boldLabel);
            if (GUILayout.Button("Save Lines as Prefab"))
            {
                save_as_prefab();
            }

            if (tracing)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField($"Points in this line: {points.Count}    Lines committed: {committed_this_session}");

                // Live pointer readout. Move the mouse over the Scene view and
                // this says whether the ray is finding the scan, so a dead click
                // can be diagnosed without guessing.
                string pointer_state = !has_hover
                    ? "nothing resolved"
                    : hover_on_mesh
                        ? $"HIT -> {last_hit_name}"
                        : "working plane (ray missed every collider)";
                EditorGUILayout.LabelField($"Pointer: {pointer_state}");

                if (collider_count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "This scene has no active colliders, so clicks cannot land on the scan - points will drop onto a flat working plane instead. Select the scan mesh and click \"Prepare Target\" below.",
                        MessageType.Warning);
                }
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

            // Only refresh the hover point when nothing is being dragged, so a
            // handle drag does not fight the placement raycast.
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
                    // Shift inverts the configured behavior so both modes are
                    // reachable without touching the dropdown mid-trace.
                    var behavior = end_behavior;
                    if (e.shift)
                    {
                        behavior = end_behavior == EndBehavior.StartNewLine
                            ? EndBehavior.StopTracing
                            : EndBehavior.StartNewLine;
                    }
                    end_segment(behavior);
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
                // Also repaint the window so the pointer readout tracks live.
                Repaint();
            }
        }

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
            Handles.color = color_on_mesh;
            for (int i = 1; i < points.Count; i++)
            {
                Handles.DrawLine(points[i - 1].world, points[i].world);
            }
            if (loop && points.Count > 2)
            {
                Handles.color = new Color(1f, 1f, 0f, 0.5f);
                Handles.DrawDottedLine(points[points.Count - 1].world, points[0].world, 3f);
            }

            // Drawn even with zero points placed: before the first click this
            // marker is the ONLY confirmation that tracing is live and the ray is
            // resolving, so gating it on points.Count made a working tool look
            // dead until after a point already existed.
            if (has_hover && GUIUtility.hotControl == 0)
            {
                if (points.Count > 0)
                {
                    Handles.color = new Color(1f, 1f, 1f, 0.5f);
                    Handles.DrawDottedLine(points[points.Count - 1].world, hover_point, 4f);
                }

                Handles.color = hover_on_mesh ? Color.cyan : color_inferred;
                float size = HandleUtility.GetHandleSize(hover_point) * 0.05f;
                Handles.SphereHandleCap(0, hover_point, Quaternion.identity, size, EventType.Repaint);
            }
        }

        // Resolve where the pointer lands: the real collider if the ray hits it,
        // otherwise a working plane so gaps over holes can still be bridged.
        private bool resolve_pointer(Vector2 gui_position, out Vector3 world, out bool on_mesh)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(gui_position);

            // Scanned meshes routinely have inconsistent or inward-facing normals,
            // and a non-convex MeshCollider does NOT register a hit when the ray
            // strikes a triangle from behind unless this flag is set. That is the
            // difference between clicking the surface and the ray sailing straight
            // through it into whatever sits behind. Restored immediately so the
            // project's own physics behavior is untouched.
            bool prev_backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            bool hit_anything = Physics.Raycast(ray, out RaycastHit hit, 5000f);
            Physics.queriesHitBackfaces = prev_backfaces;

            if (hit_anything)
            {
                world = hit.point;
                on_mesh = true;
                last_hit_name = hit.collider != null ? hit.collider.gameObject.name : "(unnamed)";
                return true;
            }
            last_hit_name = null;

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

        // The plane used whenever the ray misses every collider. With points
        // already down it is horizontal through the last one, so a run carries on
        // at its current height across a gap.
        //
        // With NO points down it is horizontal through the Scene view's pivot,
        // roughly the height of whatever you are orbiting. That case matters more
        // than it looks: returning false here means the very first click has
        // nothing to land on when the target has no collider, so the tool accepts
        // no points at all and appears completely dead with no error.
        private bool try_working_plane(out Plane plane)
        {
            if (points.Count >= 1)
            {
                plane = new Plane(Vector3.up, points[points.Count - 1].world);
                return true;
            }

            var view = SceneView.lastActiveSceneView;
            Vector3 origin = view != null
                ? view.pivot
                : (surface_root != null ? surface_root.position : Vector3.zero);
            plane = new Plane(Vector3.up, origin);
            return true;
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

        // Commit the current polyline, then either begin another or leave tracing.
        private void end_segment(EndBehavior behavior)
        {
            if (points.Count < 2)
            {
                EditorUtility.DisplayDialog("Line Tracer", "Place at least 2 points before ending the segment.", "OK");
                return;
            }

            var line = build_line(points, surface_type);
            if (line != null)
            {
                Undo.RegisterCreatedObjectUndo(line, "Create Semantic Line");
                Selection.activeGameObject = line;
                committed_this_session++;
            }

            points.Clear();
            tracing = behavior == EndBehavior.StartNewLine;
            Repaint();
            SceneView.RepaintAll();
        }

        // Commit a run of points to a persistent LineRenderer. Positions are
        // stored LOCAL to the new object (useWorldSpace = false) and the object
        // sits at the run's centroid inside the root-local container, so moving or
        // rescaling the twin carries the line with it.
        private GameObject build_line(List<TracePoint> trace, SurfaceType type)
        {
            int n = trace.Count;

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

            var go = new GameObject($"SemanticLine_{type}");
            var container = get_or_create_container(surface_root);

            if (surface_root != null)
            {
                // The container sits at local identity under the root, so a local
                // centroid places the line exactly over the traced points.
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

            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.positionCount = n;
            for (int i = 0; i < n; i++)
            {
                line.SetPosition(i, pts[i] - centroid);
            }
            line.loop = loop;
            line.sharedMaterial = load_or_create_line_material();
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            // View alignment keeps the stroke facing the headset from any angle,
            // which is what makes it readable on a see-through display.
            line.alignment = LineAlignment.View;
            line.numCornerVertices = 2;
            line.numCapVertices = 2;
            line.textureMode = LineTextureMode.Stretch;

            var semantic = go.AddComponent<SemanticLine>();
            semantic.surface_type = type;
            semantic.world_width = world_width;
            semantic.apply_visual();

            return go;
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
                Undo.RegisterCreatedObjectUndo(scoped, "Create Semantic Lines Container");
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
            Undo.RegisterCreatedObjectUndo(container, "Create Semantic Lines Container");
            return container;
        }

        // Create the shared line material on first use rather than shipping a .mat
        // whose shader GUID might not match this project's pipeline. Per-line color
        // rides on a property block, so one shared material serves every category
        // without instancing.
        private static Material load_or_create_line_material()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(line_material_path);
            if (material != null)
            {
                return material;
            }

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }
            if (shader == null)
            {
                Debug.LogWarning("[SemanticMesh] No unlit shader found; lines will use the default material.");
                return null;
            }

            material = new Material(shader) { name = "M_SemanticLine" };
            material.SetColor("_BaseColor", Color.white);
            AssetDatabase.CreateAsset(material, line_material_path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[SemanticMesh] Created line material at {line_material_path}.");
            return material;
        }

        private void save_as_prefab()
        {
            GameObject container = find_container();
            if (container == null)
            {
                EditorUtility.DisplayDialog("Line Tracer",
                    "No \"Semantic Lines\" container found. Trace at least one line first.", "OK");
                return;
            }

            // No mesh-embedding dance here: a LineRenderer stores its positions on
            // the component itself, so a plain prefab save keeps the geometry (this
            // is the same property that makes it survive PLUME recording).
            var saved = PrefabUtility.SaveAsPrefabAsset(container, prefab_path, out bool ok);
            if (!ok || saved == null)
            {
                EditorUtility.DisplayDialog("Line Tracer", $"Failed to save prefab to {prefab_path}.", "OK");
                return;
            }

            AssetDatabase.Refresh();
            Selection.activeObject = saved;
            EditorGUIUtility.PingObject(saved);

            int count = container.GetComponentsInChildren<SemanticLine>(true).Length;
            Debug.Log($"[SemanticMesh] Saved {count} line(s) to {prefab_path}.");
            EditorUtility.DisplayDialog("Line Tracer",
                $"Saved {count} line(s) to\n{prefab_path}\n\n" +
                "The prefab is authored in the Surface Root's LOCAL frame. Parent it under the twin and zero its local transform to see it correctly.",
                "OK");
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

        // Selection-independent collider pass over the whole scene. prepare_target
        // only touches what happens to be selected, so picking the wrong object -
        // or none - leaves the scene exactly as un-clickable as before while the
        // button still appears to have worked. This one cannot miss.
        private void prepare_whole_scene()
        {
            int added = 0;
            int re_enabled = 0;
            foreach (var filter in Object.FindObjectsOfType<MeshFilter>())
            {
                if (filter.sharedMesh == null)
                {
                    continue;
                }

                var collider = filter.GetComponent<MeshCollider>();
                if (collider == null)
                {
                    Undo.AddComponent<MeshCollider>(filter.gameObject);
                    added++;
                }
                else if (!collider.enabled)
                {
                    Undo.RecordObject(collider, "Enable MeshCollider");
                    collider.enabled = true;
                    re_enabled++;
                }
            }

            collider_count = Object.FindObjectsOfType<Collider>().Length;
            Debug.Log($"[SemanticMesh] Prepared entire scene: added {added} MeshCollider(s), re-enabled {re_enabled}. Scene now has {collider_count} active collider(s).");
            EditorUtility.DisplayDialog("Line Tracer",
                $"Added {added} MeshCollider(s), re-enabled {re_enabled}.\n\n" +
                $"The scene now has {collider_count} active collider(s).\n\n" +
                (collider_count == 0
                    ? "Still zero - nothing in this scene has a MeshFilter with a mesh. Check that the ground object is actually loaded."
                    : "Save the scene (Ctrl+S) to keep them."),
                "OK");
        }

        private void prepare_target()
        {
            var targets = Selection.gameObjects;
            if (targets == null || targets.Length == 0)
            {
                EditorUtility.DisplayDialog("Line Tracer",
                    "Select the high-res mesh object(s) in the Hierarchy first.", "OK");
                return;
            }

            int added = 0;
            int re_enabled = 0;
            foreach (var go in targets)
            {
                foreach (var filter in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null)
                    {
                        continue;
                    }

                    var collider = filter.GetComponent<MeshCollider>();
                    if (collider == null)
                    {
                        Undo.AddComponent<MeshCollider>(filter.gameObject);
                        added++;
                    }
                    else if (!collider.enabled)
                    {
                        // A DISABLED collider is not null, so testing only for
                        // absence left it switched off and every ray kept passing
                        // through the very object this button exists to make
                        // clickable.
                        Undo.RecordObject(collider, "Enable MeshCollider");
                        collider.enabled = true;
                        re_enabled++;
                    }
                }
            }
            Debug.Log($"[SemanticMesh] Prepare Target: added {added} MeshCollider(s), re-enabled {re_enabled} disabled one(s).");
        }
    }
}
