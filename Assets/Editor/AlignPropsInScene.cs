using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.Linq;

public static class AlignPropsInScene
{
    [InitializeOnLoadMethod]
    static void RunAlignOnce()
    {
        if (SessionState.GetBool("AlignPropsDone", false))
            return;
        
        SessionState.SetBool("AlignPropsDone", true);
        
        EditorApplication.delayCall += () => {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != "Props")
            {
                Debug.LogWarning("PROPS ALIGNMENT: Please open the 'Props' scene. Once opened, you can trigger alignment by going to Menu -> Tools -> Align Props.");
                return;
            }
            
            AlignActiveSceneProps();
        };
    }
    
    [MenuItem("Tools/Align Props")]
    public static void AlignActiveSceneProps()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.name != "Props")
        {
            Debug.LogError("PROPS ALIGNMENT: Active scene is not 'Props'.");
            return;
        }

        GameObject[] rootObjects = scene.GetRootGameObjects();
        // Filter out common non-prop things like Camera, Light, EventSystem
        var objectsToAlign = rootObjects.Where(go => 
            go.GetComponent<Camera>() == null && 
            go.GetComponent<Light>() == null &&
            go.name != "EventSystem" &&
            go.name != "Directional Light").ToArray();
            
        float currentZ = 0f;
        
        foreach (var go in objectsToAlign)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
            Collider[] colliders = go.GetComponentsInChildren<Collider>();
            
            Bounds bounds = new Bounds(go.transform.position, Vector3.zero);
            bool hasBounds = false;
            
            if (renderers.Length > 0)
            {
                bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                hasBounds = true;
            }
            else if (colliders.Length > 0)
            {
                bounds = colliders[0].bounds;
                for (int i = 1; i < colliders.Length; i++) bounds.Encapsulate(colliders[i].bounds);
                hasBounds = true;
            }
            
            // Add padding for the first half of the object
            float zExtent = hasBounds ? bounds.extents.z : 0f;
            currentZ += zExtent;
            
            // Keep the original x and y, and offset z by the bounds center offset
            Vector3 centerOffset = hasBounds ? (bounds.center - go.transform.position) : Vector3.zero;
            go.transform.position = new Vector3(0, go.transform.position.y, currentZ - centerOffset.z);
            
            // Add padding for the second half of the object + 3 meters space
            currentZ += zExtent + 3f;
        }
        
        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log("PROPS ALIGNMENT: Successfully aligned " + objectsToAlign.Length + " objects in a straight line along the Z axis.");
    }
}
