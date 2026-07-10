using System.Collections.Generic;
using UnityEngine;

public class RandomSpawner : MonoBehaviour
{
    [Header("Spawn Settings")]
    [Tooltip("The actual Target prefab to spawn.")]
    public GameObject targetPrefab;
    public enum PoolSelection
    {
        Pool1,
        Pool2,
        Pool3,
        Pool4
    }

    [Header("Randomization")]
    [Tooltip("If true, uses the fixed seed associated with the selected pool to generate the exact same locations every time.")]
    public bool useFixedSeed = true;

    [Tooltip("Seed for Pool 1")]
    public int pool1Seed = 100;
    [Tooltip("Seed for Pool 2")]
    public int pool2Seed = 200;
    [Tooltip("Seed for Pool 3")]
    public int pool3Seed = 300;
    [Tooltip("Seed for Pool 4")]
    public int pool4Seed = 400;

    [Tooltip("Select which pool to use for Recall Objects.")]
    public PoolSelection selectedPool = PoolSelection.Pool1;

    [Tooltip("Recall Object Pool 1")]
    public List<GameObject> recallObjectPool1 = new List<GameObject>();
    [Tooltip("Recall Object Pool 2")]
    public List<GameObject> recallObjectPool2 = new List<GameObject>();
    [Tooltip("Recall Object Pool 3")]
    public List<GameObject> recallObjectPool3 = new List<GameObject>();
    [Tooltip("Recall Object Pool 4")]
    public List<GameObject> recallObjectPool4 = new List<GameObject>();

    [Tooltip("Number of targets to spawn.")]
    public int targetCount = 20;

    [Header("Tags and Layers")]
    [Tooltip("The Unity Layer to force targets onto (MUST match the EyeTracker's LayerMask)")]
    public string targetLayer = "Default";
    public string recallObjectLayer = "Default";

    [Header("Placeholders")]
    [Tooltip("If left empty, this script will automatically find all child GameObjects and use them as placeholder locations.")]
    public List<Transform> placeholderLocations = new List<Transform>();

    [Tooltip("If true, automatically spawns objects when the scene starts. If false, you must call SpawnObjects() manually.")]
    public bool spawnOnAwake = true;

    private void Awake()
    {
        InitializeLocations();
        if (spawnOnAwake)
        {
            SpawnObjects();
        }
    }

    private void InitializeLocations()
    {
        // If the user didn't manually assign placeholders, grab all direct children
        if (placeholderLocations.Count == 0)
        {
            foreach (Transform child in transform)
            {
                placeholderLocations.Add(child);
            }
        }

        // Disable all placeholder meshes so they are invisible
        foreach (Transform placeholder in placeholderLocations)
        {
            MeshRenderer renderer = placeholder.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.enabled = false;
            }
            
            Collider collider = placeholder.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }
        }
    }

    private List<GameObject> GetActivePool()
    {
        switch (selectedPool)
        {
            case PoolSelection.Pool1: return recallObjectPool1;
            case PoolSelection.Pool2: return recallObjectPool2;
            case PoolSelection.Pool3: return recallObjectPool3;
            case PoolSelection.Pool4: return recallObjectPool4;
            default: return recallObjectPool1;
        }
    }

    public void SpawnObjects()
    {
        List<GameObject> activeRecallObjectPrefabs = GetActivePool();

        if (targetPrefab == null || activeRecallObjectPrefabs == null || activeRecallObjectPrefabs.Count == 0)
        {
            Debug.LogError($"RandomSpawner: Target or Recall Object prefabs (in {selectedPool}) are not assigned!");
            return;
        }

        // Guarantee we only have unique prefabs (in case the same one was dragged in twice)
        List<GameObject> uniquePrefabs = new List<GameObject>();
        foreach (var p in activeRecallObjectPrefabs)
        {
            if (p != null && !uniquePrefabs.Contains(p))
            {
                uniquePrefabs.Add(p);
            }
        }

        if (useFixedSeed)
        {
            int seedToUse = pool1Seed;
            switch (selectedPool)
            {
                case PoolSelection.Pool1: seedToUse = pool1Seed; break;
                case PoolSelection.Pool2: seedToUse = pool2Seed; break;
                case PoolSelection.Pool3: seedToUse = pool3Seed; break;
                case PoolSelection.Pool4: seedToUse = pool4Seed; break;
            }
            Random.InitState(seedToUse);
        }

        // The number of recall objects is exactly the number of unique prefabs provided
        int recallObjectCount = uniquePrefabs.Count;

        int totalToSpawn = targetCount + recallObjectCount;
        if (totalToSpawn > placeholderLocations.Count)
        {
            Debug.LogWarning($"RandomSpawner: Not enough placeholder locations! Tried to spawn {totalToSpawn} objects but only found {placeholderLocations.Count} locations. Reducing spawn counts proportionally.");
            
            // Adjust proportionally if there aren't enough slots
            float ratio = (float)placeholderLocations.Count / totalToSpawn;
            targetCount = Mathf.FloorToInt(targetCount * ratio);
            recallObjectCount = placeholderLocations.Count - targetCount;
        }

        // 1. Shuffle the locations using Fisher-Yates shuffle
        List<Transform> shuffledLocations = new List<Transform>(placeholderLocations);
        for (int i = 0; i < shuffledLocations.Count; i++)
        {
            Transform temp = shuffledLocations[i];
            int randomIndex = Random.Range(i, shuffledLocations.Count);
            shuffledLocations[i] = shuffledLocations[randomIndex];
            shuffledLocations[randomIndex] = temp;
        }

        // 2. Spawn Targets
        for (int i = 0; i < targetCount; i++)
        {
            Transform spawnPoint = shuffledLocations[i];
            SpawnAndConfigure(targetPrefab, spawnPoint, "DwellDestroyTarget", targetLayer, true);
        }

        // 3. Spawn Recall Objects
        // Shuffle the strictly unique list
        List<GameObject> shuffledRecallObjects = new List<GameObject>(uniquePrefabs);
        for (int i = 0; i < shuffledRecallObjects.Count; i++)
        {
            GameObject temp = shuffledRecallObjects[i];
            int randomIndex = Random.Range(i, shuffledRecallObjects.Count);
            shuffledRecallObjects[i] = shuffledRecallObjects[randomIndex];
            shuffledRecallObjects[randomIndex] = temp;
        }

        for (int i = targetCount; i < targetCount + recallObjectCount; i++)
        {
            Transform spawnPoint = shuffledLocations[i];
            
            // Pick the next unique recall object (we know recallObjectCount <= shuffledRecallObjects.Count)
            int recallObjectIndex = i - targetCount;
            GameObject recallObjectToSpawn = shuffledRecallObjects[recallObjectIndex];

            SpawnAndConfigure(recallObjectToSpawn, spawnPoint, "Untagged", recallObjectLayer, false);
        }

        // Force the physics engine to immediately register all new colliders
        // so raycasts can hit them from the very first frame
        Physics.SyncTransforms();
    }

    private void SpawnAndConfigure(GameObject prefab, Transform spawnPoint, string tagToApply, string layerToApply, bool rotate90)
    {
        GameObject spawnedObj = Instantiate(prefab, spawnPoint.position, spawnPoint.rotation);
        
        // Only apply the 90-degree rotation fix if requested (usually just for flat targets)
        if (rotate90)
        {
            spawnedObj.transform.Rotate(90f, 0f, 0f, Space.Self);
        }

        // Parent it to this GameObject for a clean hierarchy
        spawnedObj.transform.SetParent(transform);

        // Force the tag and layer
        spawnedObj.tag = tagToApply;
        int layerId = LayerMask.NameToLayer(layerToApply);
        if (layerId > -1) {
            spawnedObj.layer = layerId;
        } else {
            Debug.LogWarning($"RandomSpawner: Layer '{layerToApply}' does not exist in Unity! Falling back to Default.");
        }
    }
}
