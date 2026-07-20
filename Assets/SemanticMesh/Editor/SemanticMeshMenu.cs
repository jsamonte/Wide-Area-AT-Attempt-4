using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SemanticMesh.EditorTools
{
    // Spins up a fresh, empty scene to test the tracer in, so no existing scene
    // is ever touched. Adds a camera and a light; you drag your high-res mesh in
    // and use the Surface Tracer window from there.
    public static class SemanticMeshMenu
    {
        [MenuItem("Tools/Semantic Mesh/Create Test Scene")]
        public static void CreateTestScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

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

            SceneManager.SetActiveScene(scene);
            EditorSceneManager.MarkSceneDirty(scene);

            EditorUtility.DisplayDialog(
                "Semantic Mesh",
                "Empty test scene created.\n\n1. Drag your high-res mesh into the scene.\n2. Select it and click \"Prepare Target\" in the Surface Tracer window.\n3. Trace away.\n\nSave the scene wherever you like (nothing existing was touched).",
                "OK");

            SurfaceTracerWindow.Open();
        }
    }
}
