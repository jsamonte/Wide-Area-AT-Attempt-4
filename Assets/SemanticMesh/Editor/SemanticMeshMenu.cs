using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SemanticMesh.EditorTools
{
    // Menu commands that stand up disposable test scenes for the tracer, so no
    // existing scene is ever touched. "Create Test Scene" is a bare scene you drag
    // a mesh into. "Build Alignment Test Scene" wires the real Digital Twin splat
    // and the scan collider together for the free-path alignment check.
    public static class SemanticMeshMenu
    {
        // The colleague's Gaussian Splat twin (scale 0.01, 180 flip) and the bare
        // scan mesh that carries the collider we actually trace against.
        private const string twin_prefab_path = "Assets/Prefab/Digital Twin.prefab";
        private const string scan_mesh_path = "Assets/Prefab/1st Building 3.obj";

        [MenuItem("Tools/Semantic Mesh/Create Test Scene")]
        public static void CreateTestScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            add_camera_and_light();

            SceneManager.SetActiveScene(scene);
            EditorSceneManager.MarkSceneDirty(scene);

            EditorUtility.DisplayDialog(
                "Semantic Mesh",
                "Empty test scene created.\n\n1. Drag your high-res mesh into the scene.\n2. Select it and click \"Prepare Target\" in the Surface Tracer window.\n3. Trace away.\n\nSave the scene wherever you like (nothing existing was touched).",
                "OK");

            SurfaceTracerWindow.Open();
        }

        // One click: build the scene the alignment free-path check needs. Places
        // the Digital Twin splat, parents the scan mesh under it at local identity
        // (so the scan inherits the twin's 0.01 scale + flip), and gives the scan a
        // MeshCollider so the tracer can raycast it while you see the splat.
        [MenuItem("Tools/Semantic Mesh/Build Alignment Test Scene")]
        public static void BuildAlignmentTestScene()
        {
            var twin_prefab = AssetDatabase.LoadAssetAtPath<GameObject>(twin_prefab_path);
            if (twin_prefab == null)
            {
                EditorUtility.DisplayDialog("Semantic Mesh",
                    $"Could not find the Digital Twin prefab at {twin_prefab_path}.", "OK");
                return;
            }

            var scan_prefab = AssetDatabase.LoadAssetAtPath<GameObject>(scan_mesh_path);
            if (scan_prefab == null)
            {
                EditorUtility.DisplayDialog("Semantic Mesh",
                    $"Could not find the scan mesh at {scan_mesh_path}.", "OK");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            add_camera_and_light();

            // The scan/splat data is Z-up, so the twin lies on its side at world
            // identity. Copy the EXACT anchor transform the live Application scene
            // puts above its twin (tips it upright ~-90 X, 15 Y; x99.86 cancels the
            // twin's 0.01) so this test looks like device. VIEW-ONLY: surfaces
            // author in the twin's LOCAL frame (item 1), so the rig never ships and
            // the handoff is unaffected.
            var rig = new GameObject("Twin Upright (test rig - not shipped)");
            rig.transform.SetParent(null, false);
            rig.transform.localRotation = new Quaternion(-0.70093507f, 0.09231203f, 0.09227982f, 0.70117974f);
            rig.transform.localScale = new Vector3(99.86021f, 99.86021f, 99.86021f);

            // The twin renders the splat and is the Surface Root you trace in.
            var twin = (GameObject)PrefabUtility.InstantiatePrefab(twin_prefab);
            twin.transform.SetParent(rig.transform, false);

            // Parent the scan under the twin at local identity: it inherits the
            // twin's 0.01 scale and 180 flip. If scan and splat share the LCC frame
            // they now overlay for free.
            var scan = (GameObject)PrefabUtility.InstantiatePrefab(scan_prefab);
            scan.transform.SetParent(twin.transform, false);
            scan.transform.localPosition = Vector3.zero;
            scan.transform.localRotation = Quaternion.identity;
            scan.transform.localScale = Vector3.one;

            // Give every scan mesh a collider so the tracer can raycast it. Leave
            // the renderers ON for now so the overlay can be eyeballed against the
            // splat; uncheck the scan's Mesh Renderer to trace the invisible mesh.
            int colliders = 0;
            foreach (var filter in scan.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null && filter.GetComponent<MeshCollider>() == null)
                {
                    filter.gameObject.AddComponent<MeshCollider>();
                    colliders++;
                }
            }

            SceneManager.SetActiveScene(scene);
            EditorSceneManager.MarkSceneDirty(scene);

            // Frame the twin so it is on screen even at 0.01 scale.
            Selection.activeGameObject = twin;
            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.FrameSelected();
            }

            SurfaceTracerWindow.Open();

            EditorUtility.DisplayDialog(
                "Semantic Mesh - Alignment Test Scene",
                "Built the alignment scene:\n\n" +
                "- \"Twin Upright (test rig)\" only tips the Z-up data upright so you can\n" +
                "  see it. It is cosmetic and never ships.\n" +
                "- \"Digital Twin\" renders the splat and is your Surface Root (NOT the rig).\n" +
                $"- \"1st Building 3\" (scan) is parented under it with {colliders} collider(s) for tracing.\n\n" +
                "STEP 1 - overlay check (settles the free-path question):\n" +
                "Does the gray scan mesh sit ON the splat (same walls, same footprint)?\n" +
                "  YES  -> alignment is free, nothing to tune.\n" +
                "  NO   -> select \"1st Building 3\" and nudge its Transform (move/rotate/\n" +
                "          uniform scale) until it lines up with the splat.\n\n" +
                "STEP 2 - trace:\n" +
                "In the Surface Tracer window, drag \"Digital Twin\" into the Surface Root\n" +
                "field. Then uncheck the scan's Mesh Renderer (one checkbox, top of its\n" +
                "Inspector) so you see the splat, and trace. Save the scene when happy.",
                "OK");
        }

        private static void add_camera_and_light()
        {
            var camera_go = new GameObject("Main Camera");
            camera_go.tag = "MainCamera";
            var camera = camera_go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.1f, 0.1f, 0.12f, 1f);
            camera_go.transform.position = new Vector3(0f, 3f, -6f);
            camera_go.transform.rotation = Quaternion.Euler(25f, 0f, 0f);
            camera_go.AddComponent<AudioListener>();

            var light_go = new GameObject("Directional Light");
            var light = light_go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
            light_go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }
    }
}
