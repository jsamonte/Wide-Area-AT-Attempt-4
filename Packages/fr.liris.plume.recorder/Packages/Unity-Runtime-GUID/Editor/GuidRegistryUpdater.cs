using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

#if UNITY_EDITOR

namespace UnityRuntimeGuid.Editor
{
    public static class GuidRegistryUpdater
    {
        public static IEnumerable<string> GetAllScenePaths(bool forceIncludeActiveScene)
        {
            var activeScenePath = SceneManager.GetActiveScene().path;

            var scenePaths = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .DefaultIfEmpty(activeScenePath).ToList();

            if (forceIncludeActiveScene && !scenePaths.Contains(activeScenePath))
            {
                scenePaths.Add(activeScenePath);
            }

            return scenePaths;
        }

        public static void UpdateAssetsGuidRegistry(IEnumerable<string> scenePaths)
        {
            if (Application.isPlaying)
                throw new Exception($"Calling {nameof(UpdateAssetsGuidRegistry)} in play mode is not allowed.");

            var assetsGuidRegistry = AssetsGuidRegistry.GetOrCreate();
            var prevAssetsGuid = assetsGuidRegistry.Copy();

            assetsGuidRegistry.Clear();
            assetsGuidRegistry.Commit();

            var assets = new HashSet<Object>();

            for (var i = 0; i < QualitySettings.count; ++i)
            {
                var renderPipelineAsset = QualitySettings.GetRenderPipelineAssetAt(i);
                assets.Add(renderPipelineAsset);
            }

            assets.Add(GraphicsSettings.defaultRenderPipeline);

#if URP_ENABLED
            var urpGlobalSettings = GraphicsSettings.GetSettingsForRenderPipeline<UnityEngine.Rendering.Universal.UniversalRenderPipeline>();
            assets.Add(urpGlobalSettings);
#endif

            var openedScenes = new List<Scene>();
            var openedScenesPath = new List<string>();

            for (var sceneIdx = 0; sceneIdx < SceneManager.sceneCount; sceneIdx++)
            {
                var scene = SceneManager.GetSceneAt(sceneIdx);
                openedScenes.Add(scene);
                openedScenesPath.Add(scene.path);
            }

            foreach (var scenePath in scenePaths)
            {
                bool wasOpened;
                Scene scene;

                if (openedScenesPath.Contains(scenePath))
                {
                    wasOpened = true;
                    scene = openedScenes[openedScenesPath.IndexOf(scenePath)];
                }
                else
                {
                    wasOpened = false;
                    scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                }

                var rootGameObjects = scene.GetRootGameObjects().Cast<Object>().ToArray();
                var dependencies = EditorUtility.CollectDependencies(rootGameObjects);

                foreach (var dependency in dependencies)
                {
                    if (dependency == null)
                        continue;
                    if (dependency.hideFlags == HideFlags.DontSaveInBuild)
                        continue;
                    assets.Add(dependency);
                }

                if (!wasOpened)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            foreach (var asset in assets.Where(IsAsset))
            {
                if (asset.GetType().Namespace == typeof(UnityEditor.Editor).Namespace)
                    continue;
                var hasPrevGuid = prevAssetsGuid.TryGetEntry(asset, out var prevAssetGuid);

                if (hasPrevGuid)
                    assetsGuidRegistry.TryAdd(prevAssetGuid);
                else
                    assetsGuidRegistry.GetOrCreateEntry(asset);
            }

            assetsGuidRegistry.Commit();
        }

        public static void ClearAssetsGuidRegistry()
        {
            var assetsGuidRegistry = AssetsGuidRegistry.GetOrCreate();
            assetsGuidRegistry.Clear();
            assetsGuidRegistry.Commit();
        }

        [SuppressMessage("ReSharper", "CoVariantArrayConversion")]
        public static void UpdateScenesGuidRegistry(IEnumerable<string> scenePaths)
        {
            if (Application.isPlaying)
                throw new Exception($"Calling {nameof(UpdateScenesGuidRegistry)} in play mode is not allowed.");

            var prevScenePath = SceneManager.GetActiveScene().path;

            foreach (var scenePath in scenePaths)
            {
                if (SceneManager.GetActiveScene().path != scenePath)
                    EditorSceneManager.OpenScene(scenePath);

                var sceneObjects = new List<Object>();

                var scene = SceneManager.GetActiveScene();
                var sceneObjectsGuidRegistry = SceneGuidRegistry.GetOrCreate(scene);
                var prevSceneObjectsGuid = sceneObjectsGuidRegistry.Copy();
                sceneObjectsGuidRegistry.Clear();

                sceneObjects.AddRange(scene.GetRootGameObjects());
                sceneObjects.AddRange(EditorUtility.CollectDependencies(scene.GetRootGameObjects())
                    .Where(dependency => dependency != null));

                foreach (var sceneObject in sceneObjects.Where(sceneObject => !IsAsset(sceneObject)))
                {
                    if (sceneObject.GetType().Namespace == typeof(UnityEditor.Editor).Namespace)
                        continue;

                    var hasPrevGuid = prevSceneObjectsGuid.TryGetEntry(sceneObject, out var prevSceneObjectGuid);

                    if (hasPrevGuid)
                        sceneObjectsGuidRegistry.TryAdd(prevSceneObjectGuid);
                    else
                        sceneObjectsGuidRegistry.GetOrCreateEntry(sceneObject);
                }

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            if (SceneManager.GetActiveScene().path != prevScenePath)
                EditorSceneManager.OpenScene(prevScenePath);
        }

        public static void ClearScenesGuidRegistry(IEnumerable<string> scenePaths)
        {
            var prevScenePath = SceneManager.GetActiveScene().path;

            foreach (var scenePath in scenePaths)
            {
                if (SceneManager.GetActiveScene().path != scenePath)
                    EditorSceneManager.OpenScene(scenePath);

                var scene = SceneManager.GetActiveScene();
                var sceneObjectsGuidRegistry = SceneGuidRegistry.GetOrCreate(scene);
                sceneObjectsGuidRegistry.Clear();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            if (SceneManager.GetActiveScene().path != prevScenePath)
                EditorSceneManager.OpenScene(prevScenePath);
        }

        private static bool IsAsset(Object obj)
        {
            return obj != null && AssetDatabase.Contains(obj);
        }
    }
}
#endif