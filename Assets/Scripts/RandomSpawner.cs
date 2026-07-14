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

    public enum SpawnMode
    {
        Placeholders,
        Planes
    }

    [Header("Spawn Mode Options")]
    [Tooltip("Select how to spawn the objects.")]
    public SpawnMode currentSpawnMode = SpawnMode.Placeholders;

    [Tooltip("Planes to spawn on (if SpawnMode is Planes). Use Colliders (BoxCollider, MeshCollider, etc).")]
    public List<Collider> targetPlanes = new List<Collider>();
    
    [Tooltip("Height above plane to spawn objects")]
    public float spawnHeightOffset = 0.1f;

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

    [Header("Toggle Features")]
    [Tooltip("If unchecked, you must manually call SpawnObjects() from another script (like a HUD button).")]
    public bool spawnOnAwake = true;

    // Track all spawned objects so they can be cleaned up between trials
    private List<GameObject> spawnedObjects = new List<GameObject>();

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

    [ContextMenu("Preview Spawn In Editor")]
    public void SpawnObjects()
    {
        // Always clean up existing objects before spawning new ones
        DestroyAllSpawnedObjects();

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
        
        if (currentSpawnMode == SpawnMode.Placeholders)
        {
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
                SpawnAndConfigure(targetPrefab, spawnPoint.position, spawnPoint.rotation, "DwellDestroyTarget", targetLayer, true, false);
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

                SpawnAndConfigure(recallObjectToSpawn, spawnPoint.position, spawnPoint.rotation, "Untagged", recallObjectLayer, false, false);
            }
        }
        else if (currentSpawnMode == SpawnMode.Planes)
        {
            if (targetPlanes == null || targetPlanes.Count == 0)
            {
                Debug.LogError("RandomSpawner: No planes assigned for Plane spawn mode!");
                return;
            }

            // Shuffle unique prefabs for planes as well
            List<GameObject> shuffledRecallObjects = new List<GameObject>(uniquePrefabs);
            for (int i = 0; i < shuffledRecallObjects.Count; i++)
            {
                GameObject temp = shuffledRecallObjects[i];
                int randomIndex = Random.Range(i, shuffledRecallObjects.Count);
                shuffledRecallObjects[i] = shuffledRecallObjects[randomIndex];
                shuffledRecallObjects[randomIndex] = temp;
            }

            // Dart Throwing logic
            List<Vector3> validPoints = new List<Vector3>();
            
            // Calculate total approximate area of all planes based on collider bounds (X * Z)
            float totalArea = 0f;
            foreach(var plane in targetPlanes)
            {
                if (plane != null)
                {
                    Vector3 size = plane.bounds.size;
                    totalArea += size.x * size.z;
                }
            }

            // Ideal distance if tightly packed circles: ~ sqrt(Area / N)
            // We use a fraction of it as a starting minimum distance to ensure we can place them
            float minimumDistance = Mathf.Sqrt(totalArea / totalToSpawn) * 0.7f;
            
            System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            int maxFailsBeforeDistanceReduction = 15;
            int failsInARow = 0;
            int totalAttempts = 0;

            // Loop until we have all points, or 2 minutes (120,000 ms) have passed
            while (validPoints.Count < totalToSpawn && stopwatch.ElapsedMilliseconds < 120000)
            {
                totalAttempts++;
                // Pick random plane
                Collider randomPlane = targetPlanes[Random.Range(0, targetPlanes.Count)];
                if (randomPlane == null) continue;

                Bounds bounds = randomPlane.bounds;
                float rx = Random.Range(bounds.min.x, bounds.max.x);
                float rz = Random.Range(bounds.min.z, bounds.max.z);
                
                // Raycast from above bounds down
                Vector3 rayOrigin = new Vector3(rx, bounds.max.y + 1f, rz);
                if (randomPlane.Raycast(new Ray(rayOrigin, Vector3.down), out RaycastHit hit, bounds.size.y + 2f))
                {
                    // Check minimum distance
                    bool tooClose = false;
                    foreach(var p in validPoints)
                    {
                        if (Vector3.Distance(p, hit.point) < minimumDistance)
                        {
                            tooClose = true;
                            break;
                        }
                    }

                    if (!tooClose)
                    {
                        validPoints.Add(hit.point);
                        failsInARow = 0;
                    }
                    else
                    {
                        failsInARow++;
                        if (failsInARow > maxFailsBeforeDistanceReduction)
                        {
                            minimumDistance *= 0.9f; // Reduce distance requirement
                            failsInARow = 0;
                        }
                    }
                }
            }

            if (validPoints.Count < totalToSpawn)
            {
                Debug.LogWarning($"RandomSpawner: Only found {validPoints.Count} valid spawn points on planes for {totalToSpawn} objects after {totalAttempts} attempts (time limit reached). Spawning what we found.");
            }

            // Spawn Targets
            int spawnIndex = 0;
            for (int i = 0; i < targetCount && spawnIndex < validPoints.Count; i++, spawnIndex++)
            {
                Vector3 pos = validPoints[spawnIndex] + Vector3.up * spawnHeightOffset;
                SpawnAndConfigure(targetPrefab, pos, Quaternion.identity, "DwellDestroyTarget", targetLayer, true, true);
            }

            // Spawn Recall Objects
            for (int i = 0; i < recallObjectCount && spawnIndex < validPoints.Count; i++, spawnIndex++)
            {
                Vector3 pos = validPoints[spawnIndex] + Vector3.up * spawnHeightOffset;
                GameObject recallObj = shuffledRecallObjects[i % shuffledRecallObjects.Count];
                SpawnAndConfigure(recallObj, pos, Quaternion.identity, "Untagged", recallObjectLayer, false, true);
            }
        }

        // Force the physics engine to immediately register all new colliders
        // so raycasts can hit them from the very first frame
        Physics.SyncTransforms();
    }

    private void SpawnAndConfigure(GameObject prefab, Vector3 position, Quaternion rotation, string tagToApply, string layerToApply, bool rotate90, bool alignBottomToPosition)
    {
        GameObject spawnedObj = Instantiate(prefab, position, rotation);
        
        // Only apply the 90-degree rotation fix if requested (usually just for flat targets)
        if (rotate90)
        {
            spawnedObj.transform.Rotate(90f, 0f, 0f, Space.Self);
        }

        // Parent it to this GameObject for a clean hierarchy
        spawnedObj.transform.SetParent(transform);

        // Optional: Ensure the visual bottom of the object aligns with the spawn position
        if (alignBottomToPosition)
        {
            Renderer[] renderers = spawnedObj.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }
                
                float bottomY = bounds.min.y;
                float difference = position.y - bottomY;
                
                // Only push the object UP if the visual mesh clips below the target height
                if (difference > 0)
                {
                    spawnedObj.transform.position += new Vector3(0, difference, 0);
                }
            }

            // Hard constraint: The object's transform Y position can NEVER be lower than the target position.
            // This guarantees zero items have a y position lower than the plane they spawn from.
            if (spawnedObj.transform.position.y < position.y)
            {
                Vector3 fixedPos = spawnedObj.transform.position;
                fixedPos.y = position.y;
                spawnedObj.transform.position = fixedPos;
            }
        }

        // Keep track of it so we can destroy it later
        spawnedObjects.Add(spawnedObj);

        // Force the tag and layer on the root object only.
        // We rely on EyeAndHeadTracker.cs to correctly identify child colliders 
        // without overriding specially configured child layers (like "Invisible").
        spawnedObj.tag = tagToApply;
        int layerId = LayerMask.NameToLayer(layerToApply);
        if (layerId > -1) {
            spawnedObj.layer = layerId;
        } else {
            Debug.LogWarning($"RandomSpawner: Layer '{layerToApply}' does not exist in Unity! Falling back to Default.");
        }
    }

    /// <summary>
    /// Destroys all objects spawned by this spawner and clears the list.
    /// Call this before starting a new trial.
    /// </summary>
    [ContextMenu("Clear Preview")]
    public void DestroyAllSpawnedObjects()
    {
        foreach (GameObject obj in spawnedObjects)
        {
            if (obj != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(obj);
                }
                else
                {
                    DestroyImmediate(obj);
                }
            }
        }
        spawnedObjects.Clear();
    }
}
