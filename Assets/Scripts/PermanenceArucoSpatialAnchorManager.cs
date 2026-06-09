// PermanenceArucoSpatialAnchorManager.cs
// Corrected version with proper persistence across sessions (following official ML2 docs).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using MagicLeap.Android;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;
using MagicLeap.OpenXR.Features.SpatialAnchors;
using MagicLeap.OpenXR.Features.LocalizationMaps;
using MagicLeap.OpenXR.Subsystems;
using Unity.XR.CoreUtils;

public class PermanenceArucoSpatialAnchorManager : MonoBehaviour
{
    [Header("=== ARUCO → PREFAB MAPPINGS ===")]
    [SerializeField] private List<ArucoPrefabMapping> arucoMappings = new List<ArucoPrefabMapping>();

    [Header("Global ArUco Detector Settings")]
    [SerializeField] private ArucoType arucoDictionary = ArucoType.Dictionary_5x5_250;
    [SerializeField] private float arucoPhysicalLengthMeters = 0.15f;
    [SerializeField] private bool estimateArucoLength = false;

    [Header("XR Origin")]
    [SerializeField] private XROrigin xrOrigin;

    // Persistence
    private Dictionary<string, ulong> anchorMapPosIdToArucoID = new Dictionary<string, ulong>();
    private const string PREFS_KEY = "ArucoToSpatialAnchorMappings";

    // State
    private MagicLeapMarkerUnderstandingFeature markerFeature;
    private MagicLeapSpatialAnchorsFeature spatialAnchorsFeature;
    private MagicLeapSpatialAnchorsStorageFeature storageFeature;
    private MLXrAnchorSubsystem activeSubsystem;

    private Dictionary<ulong, ARAnchor> createdAnchorsByArucoID = new Dictionary<ulong, ARAnchor>();
    private List<ARAnchor> localAnchors = new List<ARAnchor>();
    private List<ARAnchor> storedAnchors = new List<ARAnchor>();

    private bool permissionGranted = false;
    private bool hasInitializedDetector = false;

    [Serializable]
    public class ArucoPrefabMapping
    {
        public ulong arucoID;
        public GameObject prefab;
        public float offsetX = 0f;
        public float offsetY = 0f;
        public float offsetZ = 0f;
        public float scaleMultiplier = 1.0f;
        public Vector3 rotationOffset = new Vector3(270f, 0f, 0f);
    }

    private void OnValidate()
    {
        if (xrOrigin == null)
            xrOrigin = FindAnyObjectByType<XROrigin>();
    }

    private IEnumerator Start()
    {
        yield return new WaitUntil(AreSubsystemsLoaded);

        markerFeature = OpenXRSettings.Instance.GetFeature<MagicLeapMarkerUnderstandingFeature>();
        spatialAnchorsFeature = OpenXRSettings.Instance.GetFeature<MagicLeapSpatialAnchorsFeature>();
        storageFeature = OpenXRSettings.Instance.GetFeature<MagicLeapSpatialAnchorsStorageFeature>();

        if (markerFeature == null || spatialAnchorsFeature == null || storageFeature == null)
        {
            Debug.LogError("❌ Required Magic Leap features missing.");
            enabled = false;
            yield break;
        }

        if (xrOrigin == null)
            xrOrigin = FindAnyObjectByType<XROrigin>();

        LoadAnchorMappings();

        Permissions.RequestPermission(Permissions.SpaceImportExport, OnSpacePermissionGranted, OnPermissionDenied);

        if (storageFeature != null)
            storageFeature.OnQueryComplete += OnQueryComplete;

        CreateMarkerDetector();
    }

    private bool AreSubsystemsLoaded()
    {
        if (XRGeneralSettings.Instance?.Manager?.activeLoader == null) return false;
        activeSubsystem = XRGeneralSettings.Instance.Manager.activeLoader.GetLoadedSubsystem<XRAnchorSubsystem>() as MLXrAnchorSubsystem;
        return activeSubsystem != null;
    }

    private void LoadAnchorMappings()
    {
        if (PlayerPrefs.HasKey(PREFS_KEY))
        {
            string json = PlayerPrefs.GetString(PREFS_KEY);
            var wrapper = JsonUtility.FromJson<AnchorMappingWrapper>(json);
            if (wrapper?.mappings != null)
                anchorMapPosIdToArucoID = wrapper.mappings.ToDictionary(m => m.mapPosId, m => m.arucoID);
        }
        Debug.Log($"[Persistence] Loaded {anchorMapPosIdToArucoID.Count} saved mappings.");
    }

    private void SaveAnchorMappings()
    {
        var list = anchorMapPosIdToArucoID.Select(kvp => new AnchorMapping { mapPosId = kvp.Key, arucoID = kvp.Value }).ToList();
        var wrapper = new AnchorMappingWrapper { mappings = list };
        PlayerPrefs.SetString(PREFS_KEY, JsonUtility.ToJson(wrapper));
        PlayerPrefs.Save();
    }

    [Serializable] private class AnchorMapping { public string mapPosId; public ulong arucoID; }
    [Serializable] private class AnchorMappingWrapper { public List<AnchorMapping> mappings; }

    private void CreateMarkerDetector()
    {
        if (hasInitializedDetector || markerFeature == null) return;

        var settings = new MarkerDetectorSettings
        {
            MarkerDetectorProfile = MarkerDetectorProfile.Default,
            MarkerType = MarkerType.Aruco,
            ArucoSettings = new ArucoSettings
            {
                ArucoType = arucoDictionary,
                ArucoLength = arucoPhysicalLengthMeters,
                EstimateArucoLength = estimateArucoLength
            }
        };

        markerFeature.CreateMarkerDetector(settings);
        hasInitializedDetector = true;
    }

    private void OnSpacePermissionGranted(string permission)
    {
        permissionGranted = true;
        Debug.Log("[Persistence] Permission granted. Querying stored anchors...");
        QueryExistingAnchors();
    }

    private void OnPermissionDenied(string permission)
    {
        permissionGranted = false;
    }

    void Update()
    {
        if (markerFeature == null || markerFeature.MarkerDetectors.Count == 0) return;

        markerFeature.UpdateMarkerDetectors();

        foreach (var detector in markerFeature.MarkerDetectors)
        {
            if (detector.Settings.MarkerType != MarkerType.Aruco) continue;

            foreach (var data in detector.Data)
            {
                if (data.MarkerPose == null || !data.MarkerNumber.HasValue) continue;

                ulong id = data.MarkerNumber.Value;
                Pose pose = data.MarkerPose.Value;
                if (pose.position.sqrMagnitude < 0.0001f) continue;

                var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == id);
                if (mapping == null || mapping.prefab == null) continue;

                if (createdAnchorsByArucoID.TryGetValue(id, out ARAnchor existing) && existing != null)
                {
                    UpdateInstanceTransform(existing.gameObject, mapping, pose);
                    continue;
                }

                CreateAndPublishAnchorFromMarker(id, mapping, pose);
            }
        }

        UpdateStoredAnchorTransforms();
    }

    private void CreateAndPublishAnchorFromMarker(ulong arucoID, ArucoPrefabMapping mapping, Pose markerRelativePose)
    {
        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
            ? xrOrigin.CameraFloorOffsetObject.transform : null;

        Vector3 worldPos = originT != null ? originT.TransformPoint(markerRelativePose.position) : markerRelativePose.position;
        Quaternion worldRot = originT != null ? originT.rotation * markerRelativePose.rotation : markerRelativePose.rotation;

        GameObject instance = Instantiate(mapping.prefab, worldPos, worldRot);
        instance.SetActive(true);

        ARAnchor arAnchor = instance.AddComponent<ARAnchor>();
        var rend = instance.GetComponent<MeshRenderer>();
        if (rend != null) rend.material.color = Color.grey;

        createdAnchorsByArucoID[arucoID] = arAnchor;
        localAnchors.Add(arAnchor);

        UpdateInstanceTransform(instance, mapping, markerRelativePose);
        PublishSingleAnchor(arAnchor);
    }

    private void UpdateInstanceTransform(GameObject instance, ArucoPrefabMapping mapping, Pose markerRelativePose)
    {
        if (instance == null) return;

        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
            ? xrOrigin.CameraFloorOffsetObject.transform : null;

        Vector3 worldPos = originT != null ? originT.TransformPoint(markerRelativePose.position) : markerRelativePose.position;
        Quaternion worldRot = originT != null ? originT.rotation * markerRelativePose.rotation : markerRelativePose.rotation;

        Vector3 localOffset = new Vector3(mapping.offsetX, mapping.offsetY, mapping.offsetZ);
        Vector3 finalPos = worldPos + (worldRot * localOffset);
        Quaternion finalRot = worldRot * Quaternion.Euler(mapping.rotationOffset);

        instance.transform.SetPositionAndRotation(finalPos, finalRot);
        instance.transform.localScale = Vector3.one * arucoPhysicalLengthMeters * mapping.scaleMultiplier;
    }

    private void PublishSingleAnchor(ARAnchor anchor)
    {
        if (!permissionGranted || storageFeature == null || anchor?.trackingState != TrackingState.Tracking) return;
        storageFeature.PublishSpatialAnchorsToStorage(new List<ARAnchor> { anchor }, 0);
    }

    // ==================== CORRECTED OnQueryComplete ====================
    private void OnQueryComplete(List<string> anchorMapPositionIds)
    {
        Debug.Log($"[Persistence] OnQueryComplete received {anchorMapPositionIds.Count} anchor IDs from storage.");

        List<string> tracked = new List<string>();

        // Check currently tracked stored anchors
        foreach (ARAnchor stored in storedAnchors.ToList())
        {
            string id = activeSubsystem?.GetAnchorMapPositionId(stored);
            if (!string.IsNullOrEmpty(id))
            {
                tracked.Add(id);
                if (!anchorMapPositionIds.Contains(id))
                {
                    Debug.Log($"[Persistence] Removing expired anchor: {id}");
                    Destroy(stored.gameObject);
                    storedAnchors.Remove(stored);
                }
            }
        }

        // Find new anchors that need to be created from storage
        var newAnchors = anchorMapPositionIds.Except(tracked).ToList();
        if (newAnchors.Count > 0)
        {
            Debug.Log($"[Persistence] Creating {newAnchors.Count} anchors from storage...");
            storageFeature.CreateSpatialAnchorsFromStorage(newAnchors);
        }
    }
    // ================================================================

    private void OnAnchorsChanged(ARAnchorsChangedEventArgs args)
    {
        foreach (ARAnchor anchor in args.added)
        {
            if (activeSubsystem != null && activeSubsystem.IsStoredAnchor(anchor))
            {
                storedAnchors.Add(anchor);

                string mapPosId = activeSubsystem.GetAnchorMapPositionId(anchor);
                if (!string.IsNullOrEmpty(mapPosId) && anchorMapPosIdToArucoID.TryGetValue(mapPosId, out ulong savedArucoID))
                {
                    var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == savedArucoID);
                    if (mapping != null && mapping.prefab != null)
                    {
                        GameObject instance = Instantiate(mapping.prefab, anchor.transform.position, anchor.transform.rotation);
                        instance.transform.SetParent(anchor.transform);
                        UpdateInstanceTransformRelativeToAnchor(instance, mapping, anchor);
                        Debug.Log($"[Persistence] ✅ Restored prefab for ArUco {savedArucoID}");
                    }
                }
            }
        }

        foreach (ARAnchor anchor in args.updated)
        {
            if (activeSubsystem != null && activeSubsystem.IsStoredAnchor(anchor) && localAnchors.Contains(anchor))
            {
                string mapPosId = activeSubsystem.GetAnchorMapPositionId(anchor);
                if (!string.IsNullOrEmpty(mapPosId))
                {
                    ulong arucoID = createdAnchorsByArucoID.FirstOrDefault(kvp => kvp.Value == anchor).Key;
                    if (arucoID != 0)
                    {
                        anchorMapPosIdToArucoID[mapPosId] = arucoID;
                        SaveAnchorMappings();
                        Debug.Log($"[Persistence] Saved mapping for new anchor (ArUco {arucoID})");
                    }
                }

                var rend = anchor.GetComponent<MeshRenderer>();
                if (rend != null) rend.material.color = Color.white;

                storedAnchors.Add(anchor);
                localAnchors.Remove(anchor);
            }
        }

        foreach (ARAnchor anchor in args.removed)
            storedAnchors.Remove(anchor);
    }

    private void UpdateInstanceTransformRelativeToAnchor(GameObject instance, ArucoPrefabMapping mapping, ARAnchor anchor)
    {
        if (instance == null || anchor == null) return;

        Vector3 localOffset = new Vector3(mapping.offsetX, mapping.offsetY, mapping.offsetZ);
        Vector3 finalPos = anchor.transform.position + (anchor.transform.rotation * localOffset);
        Quaternion finalRot = anchor.transform.rotation * Quaternion.Euler(mapping.rotationOffset);

        instance.transform.SetPositionAndRotation(finalPos, finalRot);
        instance.transform.localScale = Vector3.one * arucoPhysicalLengthMeters * mapping.scaleMultiplier;
    }

    private void UpdateStoredAnchorTransforms()
    {
        if (activeSubsystem == null) return;
        foreach (var anchor in storedAnchors)
            if (anchor != null)
            {
                var p = activeSubsystem.GetAnchorPose(anchor);
                anchor.transform.SetPositionAndRotation(p.position, p.rotation);
            }
    }

    public void QueryExistingAnchors()
    {
        if (storageFeature != null && xrOrigin != null)
            storageFeature.QueryStoredSpatialAnchors(xrOrigin.transform.position, 20f);
    }

    public void DestroyAll()
    {
        foreach (var a in localAnchors) if (a) Destroy(a.gameObject);
        foreach (var a in storedAnchors) if (a) Destroy(a.gameObject);
        localAnchors.Clear();
        storedAnchors.Clear();
        createdAnchorsByArucoID.Clear();
        if (markerFeature != null) markerFeature.DestroyAllMarkerDetectors();
        hasInitializedDetector = false;
    }

    private void OnDestroy()
    {
        if (storageFeature != null) storageFeature.OnQueryComplete -= OnQueryComplete;
        DestroyAll();
    }
}