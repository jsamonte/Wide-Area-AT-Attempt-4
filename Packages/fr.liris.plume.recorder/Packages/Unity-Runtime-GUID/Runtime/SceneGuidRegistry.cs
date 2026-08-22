using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityRuntimeGuid
{
    // TODO: refactor to extend GuidRegistry and make another class for the MonoBehavior
    [DisallowMultipleComponent]
    [ExecuteInEditMode]
    public class SceneGuidRegistry : MonoBehaviour
    {
        private const string GameObjectName = "SceneGuidRegistry";

        private static readonly Dictionary<Scene, SceneGuidRegistry> SceneGuidRegistries = new();

        [SerializeField] private string sceneGuid = Guid.NewGuid().ToString("N");

        [SerializeField] private GuidRegistry<SceneGuidRegistryEntry> registry = new();
        
        public string SceneGuid => sceneGuid;
        
        public void Awake()
        {
            var sceneGuidRegistries =
                FindObjectsByType<SceneGuidRegistry>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Where(r => r.gameObject.scene == gameObject.scene);
            if (sceneGuidRegistries.Count() <= 1) return;
            Debug.LogWarning("Only one scene GUID registry is allowed per scene. Deleting.");
            DestroyImmediate(this);
        }

        public static SceneGuidRegistry GetOrCreate(Scene scene)
        {
            if (!scene.IsValid())
                throw new InvalidOperationException("Scene is invalid.");

            SceneGuidRegistries.TryGetValue(scene, out var sceneObjectsGuidRegistry);

            if (sceneObjectsGuidRegistry == null)
            {
                sceneObjectsGuidRegistry =
                    FindObjectsByType<SceneGuidRegistry>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                        .FirstOrDefault(r => r.gameObject.scene == scene);
            }

            if (sceneObjectsGuidRegistry == null)
            {
                var go = new GameObject(GameObjectName);
                SceneManager.MoveGameObjectToScene(go, scene);
                sceneObjectsGuidRegistry = go.AddComponent<SceneGuidRegistry>();
            }

            SceneGuidRegistries[scene] = sceneObjectsGuidRegistry;

            return sceneObjectsGuidRegistry;
        }

        public bool TryAdd(SceneGuidRegistryEntry guidEntry)
        {
            return registry.TryAdd(guidEntry);
        }

        public void Clear()
        {
            registry.Clear();
        }

        public IReadOnlyList<SceneGuidRegistryEntry> GetAllEntries()
        {
            return registry.GetAllEntries();
        }

        public GuidRegistry<SceneGuidRegistryEntry> Copy()
        {
            return registry.Copy();
        }

        public bool TryGetEntry(Object obj, out SceneGuidRegistryEntry entry)
        {
            return registry.TryGetEntry(obj, out entry);
        }

        public bool TryGetEntry(string guid, out SceneGuidRegistryEntry entry)
        {
            return registry.TryGetEntry(guid, out entry);
        }

        public bool Remove(Object obj)
        {
            return registry.Remove(obj);
        }

        public SceneGuidRegistryEntry GetOrCreateEntry(Object obj)
        {
            return registry.GetOrCreateEntry(obj, _ =>
                new SceneGuidRegistryEntry
                {
                    @object = obj,
                    guid = Guid.NewGuid().ToString("N")
                });
        }
    }
}