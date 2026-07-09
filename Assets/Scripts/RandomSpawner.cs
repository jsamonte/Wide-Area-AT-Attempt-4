using System.Collections.Generic;
using UnityEngine;

public class RandomSpawner : MonoBehaviour
{
    [Header("Spawn Settings")]
    [Tooltip("The actual Target prefab to spawn.")]
    public GameObject targetPrefab;
    [Tooltip("The actual Distractor prefab to spawn.")]
    public GameObject distractorPrefab;

    [Tooltip("Number of targets to spawn.")]
    public int targetCount = 20;
    [Tooltip("Number of distractors to spawn.")]
    public int distractorCount = 60;

    [Header("Tags and Layers")]
    [Tooltip("The Unity Layer to force targets onto (MUST match the EyeTracker's LayerMask)")]
    public string targetLayer = "Default";
    public string distractorLayer = "Default";

    [Header("Size Randomization")]
    [Tooltip("If true, objects will be randomly assigned one of the 3 sizes below.")]
    public bool randomizeSize = true;

    public Vector3 normalSize = Vector3.one;
    public Vector3 bigSize = new Vector3(1.5f, 1.5f, 1.5f);
    public Vector3 biggerSize = new Vector3(2.0f, 2.0f, 2.0f);

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
        if (targetPrefab == null || distractorPrefab == null)
        {
            Debug.LogError("RandomSpawner: Target or Distractor prefab is not assigned!");
            return;
        }

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
        for (int i = targetCount; i < targetCount + distractorCount; i++)
        {
            Transform spawnPoint = shuffledLocations[i];
            SpawnAndConfigure(distractorPrefab, spawnPoint, "Untagged", distractorLayer);
        }

        // Force the physics engine to immediately register all new colliders
        // so raycasts can hit them from the very first frame
        Physics.SyncTransforms();
    }

    private void SpawnAndConfigure(GameObject prefab, Transform spawnPoint, string tagToApply, string layerToApply)
    {
        // Remember the prefab's original scale before instantiating and reparenting
        Vector3 baseScale = prefab.transform.localScale;

        GameObject spawnedObj = Instantiate(prefab, spawnPoint.position, spawnPoint.rotation);
        
        // Correct the Target Prefab's orientation
        if (prefab == targetPrefab)
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

        if (randomizeSize)
        {
            int sizeChoice = Random.Range(0, 3);
            switch (sizeChoice)
            {
                case 0:
                    spawnedObj.transform.localScale = Vector3.Scale(baseScale, normalSize);
                    break;
                case 1:
                    spawnedObj.transform.localScale = Vector3.Scale(baseScale, bigSize);
                    break;
                case 2:
                    spawnedObj.transform.localScale = Vector3.Scale(baseScale, biggerSize);
                    break;
            }
        }
        else
        {
            // If we don't randomize, default to the normal size to prevent 
            // the object from inheriting extreme scales from its parent
            spawnedObj.transform.localScale = Vector3.Scale(baseScale, normalSize);
        }
    }
}
