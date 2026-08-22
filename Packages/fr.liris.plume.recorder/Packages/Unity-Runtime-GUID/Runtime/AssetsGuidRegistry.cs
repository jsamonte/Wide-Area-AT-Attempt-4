using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityRuntimeGuid
{
    [Serializable]
    public class AssetsGuidRegistry : ScriptableObject
    {
        private const string BuiltinResourcesPath = "Resources/unity_builtin_extra";
        private const string BuiltinExtraResourcesPath = "Library/unity default resources";

        private static string _assetsGuidRegistryName = "AssetsGuidRegistry";
        private static string _assetsGuidRegistryPath = $"Resources/{_assetsGuidRegistryName}.asset";
        
        private static AssetsGuidRegistry _instance;
        
        [SerializeField] private GuidRegistry<AssetGuidRegistryEntry> registry = new();

        public static AssetsGuidRegistry GetOrCreate()
        {
            if (_instance != null)
                return _instance;
            
#if UNITY_EDITOR
            var registryAssetPath = GetAssetPath();

            try
            {
                _instance = AssetDatabase.LoadAssetAtPath(registryAssetPath, typeof(AssetsGuidRegistry)) as AssetsGuidRegistry;
            }
            catch (Exception e)
            {
                Debug.LogWarningFormat("Unable to load AssetsGuidRegistry from {0}, error {1}", registryAssetPath,
                    e.Message);
            }

            if (_instance != null) return _instance;
            _instance = CreateInstance<AssetsGuidRegistry>();
            AssetDatabase.CreateAsset(_instance, registryAssetPath);
            _instance.AddToPreloadedAssets();
#else
            _instance = Resources.Load<AssetsGuidRegistry>(_assetsGuidRegistryName);
            if (_instance == null)
            {
                Debug.LogWarning("Failed to load assets GUID registry. Using default registry instead.");
                _instance = CreateInstance<AssetsGuidRegistry>();
            }
#endif
            return _instance;
        }

        public bool TryAdd(AssetGuidRegistryEntry guidEntry)
        {
            return registry.TryAdd(guidEntry);
        }

        public void Clear()
        {
            registry.Clear();
        }

        public GuidRegistry<AssetGuidRegistryEntry> Copy()
        {
            return registry.Copy();
        }

        public bool TryGetValue(Object obj, out AssetGuidRegistryEntry entry)
        {
            return registry.TryGetEntry(obj, out entry);
        }

        public bool Remove(Object obj)
        {
            return registry.Remove(obj);
        }

        public AssetGuidRegistryEntry GetOrCreateEntry(Object obj)
        {
            return registry.GetOrCreateEntry(obj, o => new AssetGuidRegistryEntry
            {
                @object = o,
#if UNITY_EDITOR
                guid = GetAssetGuid(o),
                assetBundlePath = GetFullAssetBundlePath(o)
#else
                guid = Guid.NewGuid().ToString("N"),
                assetBundlePath = ""
#endif
            });
        }
        
        public IEnumerable<AssetGuidRegistryEntry> GetAllEntries()
        {
            return registry.GetAllEntries();
        }

#if UNITY_EDITOR
        private static string GetAssetPath()
        {
            var fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, _assetsGuidRegistryPath));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var configUri = new Uri(fullPath);
            var projectUri = new Uri(Application.dataPath);
            var relativeUri = projectUri.MakeRelativeUri(configUri);
            return relativeUri.ToString();
        }

        public void Commit()
        {
            var registryAssetPath = GetAssetPath();
            if (AssetDatabase.GetAssetPath(this) != registryAssetPath)
            {
                Debug.LogWarningFormat("The asset path of AssetsGuidRegistry is wrong. Expect {0}, get {1}",
                    registryAssetPath, AssetDatabase.GetAssetPath(this));
            }

            EditorUtility.SetDirty(this);
        }

        internal void AddToPreloadedAssets()
        {
            var preloadedAssets = PlayerSettings.GetPreloadedAssets().ToList();
            if (preloadedAssets.Contains(this)) return;
            preloadedAssets.Add(this);
            PlayerSettings.SetPreloadedAssets(preloadedAssets.ToArray());
        }
        
        private static string GetAssetGuid(Object asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            var isBuiltin = path.Equals(BuiltinResourcesPath) || path.Equals(BuiltinExtraResourcesPath);

            if (isBuiltin)
                return Guid.NewGuid().ToString("N");
            
            var guid = AssetDatabase.GUIDFromAssetPath(path).ToString();
            
            if(string.IsNullOrEmpty(guid))
                return Guid.NewGuid().ToString("N");
            
            return Guid.Parse(guid).ToString("N");
        }

        private static string GetFullAssetBundlePath(Object asset)
        {
            if (asset == null || !AssetDatabase.Contains(asset))
                return "";

            var path = AssetDatabase.GetAssetPath(asset);

            var prefix = path.Equals(BuiltinResourcesPath) || path.Equals(BuiltinExtraResourcesPath)
                ? "Builtin"
                : "Custom";

            var assetType = asset.GetType().FullName + ", " + asset.GetType().Assembly.GetName().Name;

            return $"{prefix}:{assetType}:{path}:{asset.name}";
        }
#endif
        
        
    }
}