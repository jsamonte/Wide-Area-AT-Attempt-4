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

        // The shrinkwrap ground FBX and the world-XZ / spike-cull material that
        // drapes a grid on it. Both ship under the tool folder.
        private const string ground_fbx_path = "Assets/SemanticMesh/Test/TerrainShrinkwrap.fbx";
        private const string ground_material_path = "Assets/SemanticMesh/Material/M_GroundGrid.mat";
        private const string ground_scene_path = "Assets/SemanticMesh/Test/GroundGridTest.unity";
        // The original scan the shrinkwrap was draped from. Dropped in at identity
        // as an alignment reference: if the shrinkwrap kept its origin, the two
        // overlay. Delete it once you trust the placement.
        private const string baseline_fbx_path = "Assets/Prefab/BaselineGray2.fbx";

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

        // One click: drape the grid on the shrinkwrap ground so Thomas can eyeball
        // it. Loads the ground FBX, applies M_GroundGrid (world-XZ projection +
        // vertical-face discard) to every renderer on it, frames it, and SAVES the
        // scene so it can be reopened. The material's spike discard hides the sky
        // spikes and building walls; only the terrain grid should remain. Blender
        // cleanup of the raw spikes (if wanted) stays a separate, manual job.
        [MenuItem("Tools/Semantic Mesh/Build Ground Grid Test Scene")]
        public static void BuildGroundGridTestScene()
        {
            var ground_fbx = AssetDatabase.LoadAssetAtPath<GameObject>(ground_fbx_path);
            if (ground_fbx == null)
            {
                EditorUtility.DisplayDialog("Semantic Mesh",
                    $"Could not find the ground FBX at {ground_fbx_path}.\n\n" +
                    "If Unity has not imported it yet, focus the Editor once so it " +
                    "imports, then run this again.", "OK");
                return;
            }

            var ground_material = AssetDatabase.LoadAssetAtPath<Material>(ground_material_path);
            if (ground_material == null)
            {
                EditorUtility.DisplayDialog("Semantic Mesh",
                    $"Could not find the ground material at {ground_material_path}.", "OK");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            add_camera_and_light();

            var ground = (GameObject)PrefabUtility.InstantiatePrefab(ground_fbx);
            ground.name = "Shrinkwrap Ground";
            ground.transform.SetParent(null, false);

            // Paint every renderer under the FBX with the ground grid material.
            int painted = 0;
            foreach (var renderer in ground.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = new Material[renderer.sharedMaterials.Length == 0 ? 1 : renderer.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    mats[i] = ground_material;
                }
                renderer.sharedMaterials = mats;
                painted++;
            }

            // Colliders, so the tracers can actually raycast this scene. Without
            // them every click misses and silently falls through to the tracer's
            // working plane, which reads as "clicking goes through the ground".
            // BuildAlignmentTestScene has always done this; this scene did not.
            int colliders = 0;
            foreach (var filter in ground.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null && filter.GetComponent<MeshCollider>() == null)
                {
                    filter.gameObject.AddComponent<MeshCollider>();
                    colliders++;
                }
            }

            // Alignment reference: drop the original scan at identity so its overlay
            // with the shrinkwrap answers "did the origin survive the wrap?". Skipped
            // silently if the FBX is not present.
            var baseline_fbx = AssetDatabase.LoadAssetAtPath<GameObject>(baseline_fbx_path);
            bool has_baseline = baseline_fbx != null;
            if (has_baseline)
            {
                var baseline = (GameObject)PrefabUtility.InstantiatePrefab(baseline_fbx);
                baseline.name = "BaselineGray2 (alignment reference - delete when happy)";
                baseline.transform.SetParent(null, false);
                baseline.transform.localPosition = Vector3.zero;
                baseline.transform.localRotation = Quaternion.identity;
                baseline.transform.localScale = Vector3.one;
            }

            SceneManager.SetActiveScene(scene);
            EditorSceneManager.MarkSceneDirty(scene);

            Selection.activeGameObject = ground;
            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.FrameSelected();
            }

            EditorSceneManager.SaveScene(scene, ground_scene_path);

            EditorUtility.DisplayDialog(
                "Semantic Mesh - Ground Grid Test Scene",
                "Built and saved the ground grid scene:\n\n" +
                $"- \"Shrinkwrap Ground\" is the terrain FBX with M_GroundGrid on {painted} renderer(s).\n" +
                $"- {colliders} MeshCollider(s) added so the Surface / Line tracers can click it.\n" +
                "- The material projects the grid top-down from world X/Z and discards\n" +
                "  near-vertical faces, so the sky spikes and building walls should be\n" +
                "  culled and only the terrain grid remains.\n" +
                (has_baseline
                    ? "- \"BaselineGray2 (alignment reference)\" is the original scan at identity.\n"
                    : "- (BaselineGray2.fbx not found, so no alignment reference was added.)\n") +
                $"\nSaved to {ground_scene_path} (reopen it any time).\n\n" +
                "WHAT TO CHECK:\n" +
                "- Blue square grid draped over the terrain, spikes gone.\n" +
                (has_baseline
                    ? "- ALIGNMENT: does the grid sit ON the BaselineGray2 scan? If yes, the\n" +
                      "  shrinkwrap kept its origin and drops into any scene the same way.\n" +
                      "  Delete the reference object once you trust it.\n"
                    : "") +
                "- If the terrain imported ON ITS SIDE, rotate \"Shrinkwrap Ground\" so it\n" +
                "  lies flat (world normals drive the spike cull, so it must sit Y-up).\n\n" +
                "TUNE on M_GroundGrid (all live):\n" +
                "- Hard to see: _Grid_Thickness up, _Grid_Color brighter, _Base_Color\n" +
                "  alpha up (zero it again for device).\n" +
                "- _Lod_Enable 0 = uniform grid everywhere (no distance sparsening).\n" +
                "- _Up_Threshold up = cull more aggressively. _Cell_Size = density.\n" +
                "- Holes in the scan stay as gaps: that is the Blender fill job.",
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
