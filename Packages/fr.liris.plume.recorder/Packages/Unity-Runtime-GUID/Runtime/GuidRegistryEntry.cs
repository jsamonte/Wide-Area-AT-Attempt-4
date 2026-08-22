using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityRuntimeGuid
{
    [Serializable]
    public abstract class GuidRegistryEntry
    {
        [SerializeField] public Object @object;
        [SerializeField] public string guid;
    }

    [Serializable]
    public class AssetGuidRegistryEntry : GuidRegistryEntry
    {
        [SerializeField] public string assetBundlePath;
    }

    [Serializable]
    public class SceneGuidRegistryEntry : GuidRegistryEntry
    {
    }
}