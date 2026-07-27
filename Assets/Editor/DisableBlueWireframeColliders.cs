using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor utility that enables or disables all Collider components on every
/// "Blue Wireframe" GameObject found in the currently open scene(s), including
/// all of their children, and in the 13Baseline prefab asset.
///
/// Run via  Tools > Disable Blue Wireframe Colliders
///      or  Tools > Enable Blue Wireframe Colliders.
///
/// NOTE ON ENABLING: there is no record of which colliders were already disabled
/// before the disable pass ran, so enabling turns on EVERY collider under a
/// "Blue Wireframe" object. If some were deliberately off for an unrelated reason,
/// this will switch those on too.
///
/// Be aware that live wireframe colliders sit between the user and anything behind
/// them, so they will be hit by gaze raycasts first -- see EyeAndHeadTracker's
/// layersToIncludeWithRay and gazeLogLayers.
/// </summary>
public static class DisableBlueWireframeColliders
{
    private const string WireframeObjectName = "Blue Wireframe";
    private const string PrefabPath = "Assets/Prefab/13Baseline.prefab";

    [MenuItem("Tools/Disable Blue Wireframe Colliders")]
    public static void Run()
    {
        SetCollidersEnabled(false);
    }

    [MenuItem("Tools/Enable Blue Wireframe Colliders")]
    public static void Enable()
    {
        SetCollidersEnabled(true);
    }

    private static void SetCollidersEnabled(bool enable)
    {
        string verb = enable ? "Enabled" : "Disabled";
        string verbing = enable ? "Enable" : "Disable";
        string tag = enable ? "[EnableColliders]" : "[DisableColliders]";

        int totalChanged = 0;
        int totalFound = 0;
        int blueWireframeCount = 0;

        // Search every root object in every loaded scene
        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;

            // Counted per scene so we only dirty the scenes we actually touched.
            int changedInScene = 0;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                // Search the full hierarchy for any object named "Blue Wireframe"
                Transform[] allTransforms = root.GetComponentsInChildren<Transform>(true);
                foreach (Transform t in allTransforms)
                {
                    if (t.name == WireframeObjectName)
                    {
                        blueWireframeCount++;
                        Debug.Log($"{tag} Found '{WireframeObjectName}' under '{GetFullPath(t)}'");

                        Collider[] colliders = t.GetComponentsInChildren<Collider>(true);
                        foreach (Collider col in colliders)
                        {
                            totalFound++;
                            if (col.enabled != enable)
                            {
                                Undo.RecordObject(col, verbing + " Collider");
                                col.enabled = enable;
                                EditorUtility.SetDirty(col);
                                changedInScene++;
                            }
                        }
                    }
                }
            }

            totalChanged += changedInScene;
            if (changedInScene > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        // Also update the prefab asset if it exists
        int prefabChanged = 0;
        GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefabRoot != null)
        {
            GameObject prefabContents = PrefabUtility.LoadPrefabContents(PrefabPath);
            Transform[] allInPrefab = prefabContents.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in allInPrefab)
            {
                if (t.name == WireframeObjectName)
                {
                    Collider[] colliders = t.GetComponentsInChildren<Collider>(true);
                    foreach (Collider col in colliders)
                    {
                        if (col.enabled != enable)
                        {
                            col.enabled = enable;
                            prefabChanged++;
                        }
                    }
                }
            }

            // Only write the asset back if something actually changed, so a no-op run
            // doesn't churn the prefab file and show up as a spurious git diff.
            if (prefabChanged > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(prefabContents, PrefabPath);
            }
            PrefabUtility.UnloadPrefabContents(prefabContents);

            Debug.Log($"{tag} Prefab: {verb.ToLower()} {prefabChanged} collider(s) in '{PrefabPath}'.");
        }

        string msg = $"Done!\n\nFound {blueWireframeCount} '{WireframeObjectName}' object(s) in scene.\n" +
                     $"{verb} {totalChanged} collider(s) in scene ({totalFound} total found).\n" +
                     $"{verb} {prefabChanged} collider(s) in the 13Baseline prefab.\n\n" +
                     $"Remember to save the scene (Ctrl+S).";

        Debug.Log($"{tag} {msg}");
        EditorUtility.DisplayDialog($"{verbing} Blue Wireframe Colliders", msg, "OK");
    }

    private static string GetFullPath(Transform t)
    {
        string path = t.name;
        Transform parent = t.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }
}
