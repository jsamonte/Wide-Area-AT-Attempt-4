using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityRuntimeGuid.Editor;

[InitializeOnLoad]
public class CustomRegistryUpdater : IPreprocessBuildWithReport
{
    public int callbackOrder => int.MaxValue;

    static CustomRegistryUpdater()
    {
        // Update the registries when entering play mode in the Editor
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.ExitingEditMode)
                return;

            var activeScene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(activeScene);
            EditorSceneManager.SaveScene(activeScene);

            GuidRegistryUpdater.UpdateAssetsGuidRegistry(GuidRegistryUpdater.GetAllScenePaths(true));
            GuidRegistryUpdater.UpdateScenesGuidRegistry(GuidRegistryUpdater.GetAllScenePaths(true));
        };
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        // Update the registries when building the application
        GuidRegistryUpdater.UpdateAssetsGuidRegistry(GuidRegistryUpdater.GetAllScenePaths(false));
        GuidRegistryUpdater.UpdateScenesGuidRegistry(GuidRegistryUpdater.GetAllScenePaths(false));
    }
}