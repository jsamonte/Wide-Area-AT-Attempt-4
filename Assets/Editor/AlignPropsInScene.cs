using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.Linq;
using System.Collections.Generic;

public class AlignPropsInScene : ScriptableWizard
{
    [Header("Size Grouping Thresholds")]
    [Tooltip("Use automatic thresholds based on the smallest and largest props in the scene.")]
    public bool useAutomaticThresholds = false;

    [Tooltip("Props smaller than this will be Small.")]
    public float smallMediumThreshold = 1.0f;
    
    [Tooltip("Props smaller than this (but larger than the Small threshold) will be Medium. Anything larger is Large.")]
    public float mediumLargeThreshold = 2.0f;

    private struct PropData
    {
        public GameObject go;
        public Bounds bounds;
        public bool hasBounds;
        public float size;
    }

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
            
            // For the automatic run on load, use automatic thresholds
            PerformAlignment(scene, true, 0, 0);
        };
    }
    
    [MenuItem("Tools/Align Props")]
    public static void ShowWizard()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.name != "Props")
        {
            Debug.LogError("PROPS ALIGNMENT: Active scene is not 'Props'.");
            return;
        }

        ScriptableWizard.DisplayWizard<AlignPropsInScene>("Align Props Setup", "Align");
    }

    void OnWizardUpdate()
    {
        helpString = "Adjust the thresholds and click Align. 'Use Automatic Thresholds' will calculate the best distribution based on the smallest and largest props.";
    }

    void OnWizardCreate()
    {
        PerformAlignment(SceneManager.GetActiveScene(), useAutomaticThresholds, smallMediumThreshold, mediumLargeThreshold);
    }

    private static void PerformAlignment(Scene scene, bool autoThresholds, float manualSmallMedium, float manualMediumLarge)
    {
        GameObject[] rootObjects = scene.GetRootGameObjects();
        // Filter out common non-prop things like Camera, Light, EventSystem
        var objectsToAlign = rootObjects.Where(go => 
            go.GetComponent<Camera>() == null && 
            go.GetComponent<Light>() == null &&
            go.name != "EventSystem" &&
            go.name != "Directional Light").ToArray();

        if (objectsToAlign.Length == 0)
        {
            Debug.Log("PROPS ALIGNMENT: No props found to align.");
            return;
        }

        List<PropData> props = new List<PropData>();
        float minSize = float.MaxValue;
        float maxSize = float.MinValue;

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
            
            // Calculate size based on the bounding box diagonal magnitude
            float size = hasBounds ? bounds.size.magnitude : 0f;
            if (size < minSize) minSize = size;
            if (size > maxSize) maxSize = size;
            
            props.Add(new PropData { go = go, bounds = bounds, hasBounds = hasBounds, size = size });
        }

        float thresholdSmallMedium = manualSmallMedium;
        float thresholdMediumLarge = manualMediumLarge;

        if (autoThresholds)
        {
            float range = maxSize - minSize;
            thresholdSmallMedium = minSize + (range / 3f);
            thresholdMediumLarge = minSize + (2f * range / 3f);
        }

        List<PropData> smallProps = new List<PropData>();
        List<PropData> mediumProps = new List<PropData>();
        List<PropData> largeProps = new List<PropData>();

        foreach (var p in props)
        {
            if (p.size <= thresholdSmallMedium) smallProps.Add(p);
            else if (p.size <= thresholdMediumLarge) mediumProps.Add(p);
            else largeProps.Add(p);
        }

        // Sort each bucket by size (ascending)
        smallProps = smallProps.OrderBy(p => p.size).ToList();
        mediumProps = mediumProps.OrderBy(p => p.size).ToList();
        largeProps = largeProps.OrderBy(p => p.size).ToList();

        // Calculate dynamic X spacing based on maximum widths
        float maxSmallWidth = smallProps.Count > 0 ? smallProps.Max(p => p.hasBounds ? p.bounds.size.x : 0f) : 0f;
        float maxMediumWidth = mediumProps.Count > 0 ? mediumProps.Max(p => p.hasBounds ? p.bounds.size.x : 0f) : 0f;
        
        // Arrange each group in its own row with 10 units of padding between rows
        float smallX = 0f;
        float mediumX = smallX + maxSmallWidth + 10f;
        float largeX = mediumX + maxMediumWidth + 10f;

        LayoutGroup(smallProps, smallX);
        LayoutGroup(mediumProps, mediumX);
        LayoutGroup(largeProps, largeX);
        
        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"PROPS ALIGNMENT: Successfully aligned {objectsToAlign.Length} objects (Small: {smallProps.Count}, Medium: {mediumProps.Count}, Large: {largeProps.Count}) in 3 rows.");
    }

    private static void LayoutGroup(List<PropData> group, float xOffset)
    {
        float currentZ = 0f;
        foreach (var p in group)
        {
            // Add padding for the first half of the object
            float zExtent = p.hasBounds ? p.bounds.extents.z : 0f;
            currentZ += zExtent;
            
            // Offset x by the chosen row coordinate, keep original y, and offset z by the bounds center offset
            Vector3 centerOffset = p.hasBounds ? (p.bounds.center - p.go.transform.position) : Vector3.zero;
            p.go.transform.position = new Vector3(xOffset, p.go.transform.position.y, currentZ - centerOffset.z);
            
            // Add padding for the second half of the object + 3 meters space
            currentZ += zExtent + 3f;
        }
    }
}
