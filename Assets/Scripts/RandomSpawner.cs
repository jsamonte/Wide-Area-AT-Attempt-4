using System.Collections.Generic;
using UnityEngine;

public class RandomSpawner : MonoBehaviour
{
    [Header("Spawn Settings")]
    [Tooltip("The actual Target prefab to spawn.")]
    public GameObject targetPrefab;
    [Tooltip("A list of Distractor prefabs to choose from. It will pick randomly without duplicates (until it runs out).")]
    public List<GameObject> distractorPrefabs = new List<GameObject>();

    [Tooltip("Number of targets to spawn.")]
    public int targetCount = 20;

    [Header("Tags and Layers")]
    [Tooltip("The Unity Layer to force targets onto (MUST match the EyeTracker's LayerMask)")]
    public string targetLayer = "Default";
    public string distractorLayer = "Default";

    [Header("Placeholders")]
    [Tooltip("If left empty, this script will automatically find all child GameObjects and use them as placeholder locations.")]
    public List<Transform> placeholderLocations = new List<Transform>();

    private void Awake()
    {
        InitializeLocations();
        SpawnObjects();
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

    private void SpawnObjects()
    {
        if (targetPrefab == null || distractorPrefabs == null || distractorPrefabs.Count == 0)
        {
            Debug.LogError("RandomSpawner: Target or Distractor prefabs are not assigned!");
            return;
        }

        // Guarantee we only have unique prefabs (in case the same one was dragged in twice)
        List<GameObject> uniquePrefabs = new List<GameObject>();
        foreach (var p in distractorPrefabs)
        {
            if (p != null && !uniquePrefabs.Contains(p))
            {
                uniquePrefabs.Add(p);
            }
        }

        // The number of distractors is exactly the number of unique prefabs provided
        int distractorCount = uniquePrefabs.Count;

        int totalToSpawn = targetCount + distractorCount;
        if (totalToSpawn > placeholderLocations.Count)
        {
            Debug.LogWarning($"RandomSpawner: Not enough placeholder locations! Tried to spawn {totalToSpawn} objects but only found {placeholderLocations.Count} locations. Reducing spawn counts proportionally.");
            
            // Adjust proportionally if there aren't enough slots
            float ratio = (float)placeholderLocations.Count / totalToSpawn;
            targetCount = Mathf.FloorToInt(targetCount * ratio);
            distractorCount = placeholderLocations.Count - targetCount;
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
            SpawnAndConfigure(targetPrefab, spawnPoint, "DwellDestroyTarget", targetLayer);
        }

        // 3. Spawn Distractors
        // Shuffle the strictly unique list
        List<GameObject> shuffledDistractors = new List<GameObject>(uniquePrefabs);
        for (int i = 0; i < shuffledDistractors.Count; i++)
        {
            GameObject temp = shuffledDistractors[i];
            int randomIndex = Random.Range(i, shuffledDistractors.Count);
            shuffledDistractors[i] = shuffledDistractors[randomIndex];
            shuffledDistractors[randomIndex] = temp;
        }

        for (int i = targetCount; i < targetCount + distractorCount; i++)
        {
            Transform spawnPoint = shuffledLocations[i];
            
            // Pick the next unique distractor (we know distractorCount <= shuffledDistractors.Count)
            int distractorIndex = i - targetCount;
            GameObject distractorToSpawn = shuffledDistractors[distractorIndex];

            SpawnAndConfigure(distractorToSpawn, spawnPoint, "Untagged", distractorLayer);
        }

        // Force the physics engine to immediately register all new colliders
        // so raycasts can hit them from the very first frame
        Physics.SyncTransforms();
    }

    private void SpawnAndConfigure(GameObject prefab, Transform spawnPoint, string tagToApply, string layerToApply)
    {
        GameObject spawnedObj = Instantiate(prefab, spawnPoint.position, spawnPoint.rotation);
        
        // Correct orientation for both targets and distractors
        spawnedObj.transform.Rotate(90f, 0f, 0f, Space.Self);

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
