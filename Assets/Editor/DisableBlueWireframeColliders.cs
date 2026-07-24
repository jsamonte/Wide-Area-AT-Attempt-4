using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor utility that disables all Collider components on every
/// "Blue Wireframe" GameObject found in the currently open scene(s),
/// including all of their children.
/// Run via  Tools > Disable Blue Wireframe Colliders.
/// Safe to delete this script after use.
/// </summary>
public static class DisableBlueWireframeColliders
{
    [MenuItem("Tools/Disable Blue Wireframe Colliders")]
    public static void Run()
    {
        int totalDisabled = 0;
        int totalFound = 0;
        int blueWireframeCount = 0;

        // Search every root object in every loaded scene
        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                // Search the full hierarchy for any object named "Blue Wireframe"
                Transform[] allTransforms = root.GetComponentsInChildren<Transform>(true);
                foreach (Transform t in allTransforms)
                {
                    if (t.name == "Blue Wireframe")
                    {
                        blueWireframeCount++;
                        Debug.Log($"[DisableColliders] Found 'Blue Wireframe' under '{GetFullPath(t)}'");

                        Collider[] colliders = t.GetComponentsInChildren<Collider>(true);
                        foreach (Collider col in colliders)
                        {
                            totalFound++;
                            if (col.enabled)
                            {
                                Undo.RecordObject(col, "Disable Collider");
                                col.enabled = false;
                                EditorUtility.SetDirty(col);
                                totalDisabled++;
                            }
                        }
                    }
                }
            }

            if (totalDisabled > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        // Also update the prefab asset if it exists
        const string prefabPath = "Assets/Prefab/13Baseline.prefab";
        GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefabRoot != null)
        {
            GameObject prefabContents = PrefabUtility.LoadPrefabContents(prefabPath);
            Transform[] allInPrefab = prefabContents.GetComponentsInChildren<Transform>(true);
            int prefabDisabled = 0;
            foreach (Transform t in allInPrefab)
            {
                if (t.name == "Blue Wireframe")
                {
                    Collider[] colliders = t.GetComponentsInChildren<Collider>(true);
                    foreach (Collider col in colliders)
                    {
                        if (col.enabled)
                        {
                            col.enabled = false;
                            prefabDisabled++;
                        }
                    }
                }
            }
            PrefabUtility.SaveAsPrefabAsset(prefabContents, prefabPath);
            PrefabUtility.UnloadPrefabContents(prefabContents);
            Debug.Log($"[DisableColliders] Prefab: disabled {prefabDisabled} collider(s) in '{prefabPath}'.");
        }

        string msg = $"Done!\n\nFound {blueWireframeCount} 'Blue Wireframe' object(s) in scene.\n" +
                     $"Disabled {totalDisabled} collider(s) in scene ({totalFound} total found).\n\n" +
                     $"Remember to save the scene (Ctrl+S).";

        Debug.Log($"[DisableColliders] {msg}");
        EditorUtility.DisplayDialog("Disable Blue Wireframe Colliders", msg, "OK");
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
