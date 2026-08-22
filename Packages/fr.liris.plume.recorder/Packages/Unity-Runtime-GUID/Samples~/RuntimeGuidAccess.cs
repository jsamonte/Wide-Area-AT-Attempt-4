using UnityEngine;
using UnityRuntimeGuid;

public class RuntimeGuidAccess : MonoBehaviour
{
	// Reference to a scene object (GameObject, Transform, AudioClip, Camera, ...)
	public Object sceneObject;
	
	// Reference to a project asset (Material, Prefab, Texture, ...)
	public Object assetObject;

    void Start()
    {
        var assetsRegistry = AssetsGuidRegistry.GetOrCreate();
        var assetGuidEntry = assetsRegistry.GetOrCreateEntry(assetObject);
        Debug.LogFormat("Asset {0} has GUID {1} and path {2}", assetGuidEntry.@object, assetGuidEntry.guid,
            assetGuidEntry.assetBundlePath);
        
        var sceneRegistry = SceneGuidRegistry.GetOrCreate(gameObject.scene);
        var sceneGuidEntry = sceneRegistry.GetOrCreateEntry(sceneObject);
        Debug.LogFormat("Scene object {0} has GUID {1}", sceneGuidEntry.@object, sceneGuidEntry.guid);
    }
}