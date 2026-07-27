using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Bulk edits Static Editor Flags on the current selection and all of its children.
///
/// Written for the 13Baseline wireframe hierarchy, where marking everything "Static"
/// switched on flags that do nothing useful for this project:
///   - Contribute GI (Lightmap Static) does nothing without a baked lighting pass, and
///     leaves ~2,900 objects primed to start one if anyone hits Generate Lighting.
///   - Occluder/Occludee Static do nothing without baked occlusion culling data.
///
/// Batching Static is the flag that is actually earning its keep, so nothing here touches
/// it. Each menu item clears exactly one flag and reports what it changed.
///
/// Usage: select the root object(s) in the Hierarchy, then pick an item from
/// Tools > Static Flags.
/// </summary>
public static class StaticFlagsTool
{
    [MenuItem("Tools/Static Flags/Clear Contribute GI (Lightmap Static)")]
    public static void ClearContributeGI()
    {
        ClearFlag(StaticEditorFlags.ContributeGI, "Contribute GI");
    }

    [MenuItem("Tools/Static Flags/Clear Occluder + Occludee Static")]
    public static void ClearOcclusion()
    {
        ClearFlag(StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic,
                  "Occluder/Occludee Static");
    }

    [MenuItem("Tools/Static Flags/Report Static Flags")]
    public static void Report()
    {
        GameObject[] roots = Selection.gameObjects;
        if (!HasSelection(roots)) return;

        int total = 0, contributeGI = 0, batching = 0, occluder = 0, occludee = 0, navigation = 0, reflection = 0;

        foreach (GameObject root in roots)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                total++;
                StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(t.gameObject);
                if ((flags & StaticEditorFlags.ContributeGI) != 0) contributeGI++;
                if ((flags & StaticEditorFlags.BatchingStatic) != 0) batching++;
                if ((flags & StaticEditorFlags.OccluderStatic) != 0) occluder++;
                if ((flags & StaticEditorFlags.OccludeeStatic) != 0) occludee++;
                if ((flags & StaticEditorFlags.NavigationStatic) != 0) navigation++;
                if ((flags & StaticEditorFlags.ReflectionProbeStatic) != 0) reflection++;
            }
        }

        string msg = $"{total} GameObject(s) in selection (including children):\n\n" +
                     $"Contribute GI (Lightmap):  {contributeGI}\n" +
                     $"Batching Static:           {batching}\n" +
                     $"Occluder Static:           {occluder}\n" +
                     $"Occludee Static:           {occludee}\n" +
                     $"Navigation Static:         {navigation}\n" +
                     $"Reflection Probe Static:   {reflection}";

        Debug.Log($"[StaticFlags] {msg}");
        EditorUtility.DisplayDialog("Static Flags Report", msg, "OK");
    }

    private static void ClearFlag(StaticEditorFlags flagsToClear, string label)
    {
        GameObject[] roots = Selection.gameObjects;
        if (!HasSelection(roots)) return;

        int total = 0;
        int changed = 0;

        foreach (GameObject root in roots)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                total++;
                GameObject go = t.gameObject;

                StaticEditorFlags current = GameObjectUtility.GetStaticEditorFlags(go);
                StaticEditorFlags updated = current & ~flagsToClear;

                if (updated == current) continue;

                Undo.RecordObject(go, "Clear " + label);
                GameObjectUtility.SetStaticEditorFlags(go, updated);
                EditorUtility.SetDirty(go);
                changed++;
            }
        }

        if (changed > 0)
        {
            // Dirty the scene of each selected root rather than every open scene.
            foreach (GameObject root in roots)
            {
                if (root.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(root.scene);
            }
        }

        string msg = $"Cleared '{label}' on {changed} of {total} GameObject(s).\n\n" +
                     $"Batching Static was not touched.\n\n" +
                     $"Remember to save the scene (Ctrl+S).";

        Debug.Log($"[StaticFlags] {msg}");
        EditorUtility.DisplayDialog("Clear " + label, msg, "OK");
    }

    private static bool HasSelection(GameObject[] roots)
    {
        if (roots != null && roots.Length > 0) return true;

        EditorUtility.DisplayDialog(
            "Nothing selected",
            "Select one or more GameObjects in the Hierarchy first.\n\n" +
            "The operation applies to the selection and all of its children.",
            "OK");
        return false;
    }
}
