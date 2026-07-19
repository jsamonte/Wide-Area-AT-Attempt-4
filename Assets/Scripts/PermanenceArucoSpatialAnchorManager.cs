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
    private MagicLeapLocalizationMapFeature localizationMapFeature;
    private MLXrAnchorSubsystem activeSubsystem;

    private Dictionary<ulong, ARAnchor> createdAnchorsByArucoID = new Dictionary<ulong, ARAnchor>();
    private Dictionary<ulong, GameObject> spawnedPrefabsByArucoID = new Dictionary<ulong, GameObject>();
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
        localizationMapFeature = OpenXRSettings.Instance.GetFeature<MagicLeapLocalizationMapFeature>();

        if (markerFeature == null || spatialAnchorsFeature == null || storageFeature == null || localizationMapFeature == null)
        {
            Debug.LogError("❌ Required Magic Leap features missing.");
            enabled = false;
            yield break;
        }

        if (xrOrigin == null)
            xrOrigin = FindAnyObjectByType<XROrigin>();

        LoadAnchorMappings();

        Permissions.RequestPermissions(new string[] { Permissions.SpaceImportExport, "com.magicleap.permission.SPATIAL_ANCHOR" }, OnPermissionsGranted, OnPermissionsDenied);

        if (storageFeature != null)
            storageFeature.OnQueryComplete += OnQueryComplete;
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

    private void OnPermissionsGranted(string permission)
    {
        permissionGranted = true;

        localizationMapFeature.EnableLocalizationEvents(true);
        CreateMarkerDetector();

        Debug.Log("[Persistence] Permission granted. Querying stored anchors...");
        QueryExistingAnchors();
    }

    private void OnPermissionsDenied(string permission)
    {
        permissionGranted = false;
    }

    private bool IsLocalized()
    {
        if (localizationMapFeature == null) return false;
        localizationMapFeature.GetLatestLocalizationMapData(out LocalizationEventData mapData);
        return mapData.State == LocalizationMapState.Localized;
    }

    void Update()
    {
        if (!permissionGranted) return;

        CheckAndSavePrefabOffsets();

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
                    UpdateAnchorTransform(existing.gameObject, pose);
                    continue;
                }

                CreateAndPublishAnchorFromMarker(id, mapping, pose);
            }
        }

        UpdateStoredAnchorTransforms();
    }

    private void CheckAndSavePrefabOffsets()
    {
        foreach (var kvp in spawnedPrefabsByArucoID)
        {
            if (kvp.Value != null && kvp.Value.transform.hasChanged)
            {
                SaveCustomOffset(kvp.Key, kvp.Value.transform);
                kvp.Value.transform.hasChanged = false;
            }
        }
    }

    private void SaveCustomOffset(ulong arucoID, Transform prefabTransform)
    {
        PlayerPrefs.SetFloat($"Aruco_{arucoID}_PosX", prefabTransform.localPosition.x);
        PlayerPrefs.SetFloat($"Aruco_{arucoID}_PosY", prefabTransform.localPosition.y);
        PlayerPrefs.SetFloat($"Aruco_{arucoID}_PosZ", prefabTransform.localPosition.z);
        PlayerPrefs.SetFloat($"Aruco_{arucoID}_RotX", prefabTransform.localRotation.x);
        PlayerPrefs.SetFloat($"Aruco_{arucoID}_RotY", prefabTransform.localRotation.y);
        PlayerPrefs.SetFloat($"Aruco_{arucoID}_RotZ", prefabTransform.localRotation.z);
        PlayerPrefs.SetFloat($"Aruco_{arucoID}_RotW", prefabTransform.localRotation.w);
        PlayerPrefs.SetInt($"Aruco_{arucoID}_HasCustomOffset", 1);
        PlayerPrefs.Save();
    }

    private bool LoadCustomOffset(ulong arucoID, Transform prefabTransform)
    {
        if (PlayerPrefs.GetInt($"Aruco_{arucoID}_HasCustomOffset", 0) == 1)
        {
            float px = PlayerPrefs.GetFloat($"Aruco_{arucoID}_PosX");
            float py = PlayerPrefs.GetFloat($"Aruco_{arucoID}_PosY");
            float pz = PlayerPrefs.GetFloat($"Aruco_{arucoID}_PosZ");
            float rx = PlayerPrefs.GetFloat($"Aruco_{arucoID}_RotX");
            float ry = PlayerPrefs.GetFloat($"Aruco_{arucoID}_RotY");
            float rz = PlayerPrefs.GetFloat($"Aruco_{arucoID}_RotZ");
            float rw = PlayerPrefs.GetFloat($"Aruco_{arucoID}_RotW");
            prefabTransform.localPosition = new Vector3(px, py, pz);
            prefabTransform.localRotation = new Quaternion(rx, ry, rz, rw);
            prefabTransform.hasChanged = false;
            return true;
        }
        return false;
    }

    private void CreateAndPublishAnchorFromMarker(ulong arucoID, ArucoPrefabMapping mapping, Pose markerRelativePose)
    {
        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
            ? xrOrigin.CameraFloorOffsetObject.transform : null;

        Vector3 worldPos = originT != null ? originT.TransformPoint(markerRelativePose.position) : markerRelativePose.position;
        Quaternion worldRot = originT != null ? originT.rotation * markerRelativePose.rotation : markerRelativePose.rotation;

        GameObject anchorObj = new GameObject($"Anchor_Aruco_{arucoID}");
        anchorObj.transform.SetPositionAndRotation(worldPos, worldRot);

        ARAnchor arAnchor = anchorObj.AddComponent<ARAnchor>();
        createdAnchorsByArucoID[arucoID] = arAnchor;
        localAnchors.Add(arAnchor);

        GameObject instance = Instantiate(mapping.prefab, anchorObj.transform);
        instance.SetActive(true);
        spawnedPrefabsByArucoID[arucoID] = instance;

        ApplyInitialOrSavedOffset(arucoID, instance, mapping);
        PublishSingleAnchor(arAnchor);
    }

    private void ApplyInitialOrSavedOffset(ulong arucoID, GameObject instance, ArucoPrefabMapping mapping)
    {
        if (!LoadCustomOffset(arucoID, instance.transform))
        {
            instance.transform.localPosition = new Vector3(mapping.offsetX, mapping.offsetY, mapping.offsetZ);
            instance.transform.localRotation = Quaternion.Euler(mapping.rotationOffset);
            instance.transform.hasChanged = false;
        }
        instance.transform.localScale = Vector3.one * arucoPhysicalLengthMeters * mapping.scaleMultiplier;
    }

    private void UpdateAnchorTransform(GameObject anchorObj, Pose markerRelativePose)
    {
        if (anchorObj == null) return;
        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
            ? xrOrigin.CameraFloorOffsetObject.transform : null;

        Vector3 markerWorldPos = originT != null ? originT.TransformPoint(markerRelativePose.position) : markerRelativePose.position;
        Quaternion markerWorldRot = originT != null ? originT.rotation * markerRelativePose.rotation : markerRelativePose.rotation;

        anchorObj.transform.SetPositionAndRotation(markerWorldPos, markerWorldRot);
    }

    private void PublishSingleAnchor(ARAnchor anchor)
    {
        if (!permissionGranted || storageFeature == null || anchor?.trackingState != TrackingState.Tracking) return;
        if (!IsLocalized()) return;
        storageFeature.PublishSpatialAnchorsToStorage(new List<ARAnchor> { anchor }, 0);
    }

    private void OnQueryComplete(List<string> anchorMapPositionIds)
    {
        Debug.Log($"[Persistence] OnQueryComplete received {anchorMapPositionIds.Count} anchor IDs from storage.");

        List<string> tracked = new List<string>();

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

        var newAnchors = anchorMapPositionIds.Except(tracked).ToList();
        if (newAnchors.Count > 0 && IsLocalized())
        {
            Debug.Log($"[Persistence] Creating {newAnchors.Count} anchors from storage...");
            storageFeature.CreateSpatialAnchorsFromStorage(newAnchors);
        }
    }

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
                        GameObject instance = Instantiate(mapping.prefab, anchor.transform);
                        spawnedPrefabsByArucoID[savedArucoID] = instance;
                        ApplyInitialOrSavedOffset(savedArucoID, instance, mapping);
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

                storedAnchors.Add(anchor);
                localAnchors.Remove(anchor);
            }
        }

        foreach (ARAnchor anchor in args.removed)
            storedAnchors.Remove(anchor);
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
        if (storageFeature != null && xrOrigin != null && IsLocalized())
            storageFeature.QueryStoredSpatialAnchors(xrOrigin.transform.position, 20f);
    }

    public void DestroyAll()
    {
        foreach (var a in localAnchors) if (a) Destroy(a.gameObject);
        foreach (var a in storedAnchors) if (a) Destroy(a.gameObject);
        localAnchors.Clear();
        storedAnchors.Clear();
        createdAnchorsByArucoID.Clear();
        spawnedPrefabsByArucoID.Clear();
        if (markerFeature != null) markerFeature.DestroyAllMarkerDetectors();
        hasInitializedDetector = false;
    }

    private void OnDestroy()
    {
        if (localizationMapFeature != null) localizationMapFeature.EnableLocalizationEvents(false);
        if (storageFeature != null) storageFeature.OnQueryComplete -= OnQueryComplete;
        DestroyAll();
    }
}