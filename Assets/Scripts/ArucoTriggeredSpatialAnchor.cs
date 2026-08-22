// ArucoTriggeredSpatialAnchor.cs
// Final version - Prefab + Spatial Anchor lock to LAST KNOWN position of the ArUco.
// While tracked: updates live. When lost: freezes at last good pose.
// One anchor per ArUco ID. Full per-ArUco offset/scale/rotation support.

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

public class ArucoTriggeredSpatialAnchor : MonoBehaviour
{
    [Header("=== ARUCO → PREFAB MAPPINGS ===")]
    [SerializeField] private List<ArucoPrefabMapping> arucoMappings = new List<ArucoPrefabMapping>();

    [Header("Global ArUco Detector Settings")]
    [SerializeField] private ArucoType arucoDictionary = ArucoType.Dictionary_5x5_250;
    [SerializeField] private float arucoPhysicalLengthMeters = 0.15f;
    [SerializeField] private bool estimateArucoLength = false;

    [Header("XR Origin")]
    [SerializeField] private XROrigin xrOrigin;

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

    private const float NotLocalizedWarnInterval = 5f;
    private float lastNotLocalizedWarnTime = -999f;

    [Serializable]
    public class ArucoPrefabMapping
    {
        public ulong arucoID;
        public GameObject prefab;

        [Header("Per-ArUco Settings (marker local space)")]
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
            Debug.LogError("❌ Required Magic Leap features missing. Enable them in OpenXR settings.");
            enabled = false;
            yield break;
        }

        if (xrOrigin == null)
            xrOrigin = FindAnyObjectByType<XROrigin>();

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
        Debug.Log($"✅ ArUco detector ready");
    }

    private void OnPermissionsGranted(string permission)
    {
        permissionGranted = true;
        
        localizationMapFeature.EnableLocalizationEvents(true);
        CreateMarkerDetector();

        // 🚨 BUG FIX: Commenting out QueryStoredSpatialAnchors to prevent Magic Leap OS crash (too many SQL variables) when map is empty or corrupted.
        // if (storageFeature != null && xrOrigin != null)
        //    QueryExistingAnchors();
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

    // Update() retries every frame a marker stays visible, so this is throttled to keep
    // logcat readable.
    private void WarnNotLocalized(ulong arucoID)
    {
        if (Time.unscaledTime - lastNotLocalizedWarnTime < NotLocalizedWarnInterval) return;
        lastNotLocalizedWarnTime = Time.unscaledTime;
        Debug.LogWarning($"[Anchors] Not localized into a map — deferring anchor creation for ArUco {arucoID}.");
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

                if (createdAnchorsByArucoID.TryGetValue(id, out ARAnchor existingAnchor) && existingAnchor != null)
                {
                    // Move the root anchor object
                    UpdateAnchorTransform(existingAnchor.gameObject, pose);
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
        // An anchor created before the headset has localized into a map has no map position to
        // bind to. The ML runtime then fails its tracked-anchor query and throws on its own
        // polling thread, which aborts the process. Publishing and querying already gate on
        // this; creation has to as well.
        if (!IsLocalized())
        {
            WarnNotLocalized(arucoID);
            return;
        }

        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
            ? xrOrigin.CameraFloorOffsetObject.transform
            : null;

        Vector3 markerWorldPos = originT != null ? originT.TransformPoint(markerRelativePose.position) : markerRelativePose.position;
        Quaternion markerWorldRot = originT != null ? originT.rotation * markerRelativePose.rotation : markerRelativePose.rotation;

        GameObject anchorObj = new GameObject($"Anchor_Aruco_{arucoID}");
        anchorObj.transform.SetPositionAndRotation(markerWorldPos, markerWorldRot);
        
        ARAnchor arAnchor = anchorObj.AddComponent<ARAnchor>();
        createdAnchorsByArucoID[arucoID] = arAnchor;
        localAnchors.Add(arAnchor);

        GameObject instance = Instantiate(mapping.prefab, anchorObj.transform);
        instance.SetActive(true);
        spawnedPrefabsByArucoID[arucoID] = instance;

        ApplyInitialOrSavedOffset(arucoID, instance, mapping);

        Debug.Log($"✅ Created + published spatial anchor for ArUco {arucoID}");
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
        var tracked = new List<string>();
        foreach (var stored in storedAnchors.ToList())
        {
            string id = activeSubsystem?.GetAnchorMapPositionId(stored);
            if (!string.IsNullOrEmpty(id))
            {
                tracked.Add(id);
                if (!anchorMapPositionIds.Contains(id)) Destroy(stored.gameObject);
            }
        }
        var newOnes = anchorMapPositionIds.Except(tracked);
        if (newOnes.Any() && IsLocalized()) 
        {
            storageFeature.CreateSpatialAnchorsFromStorage(newOnes.ToList());
        }
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

    private void OnAnchorsChanged(ARAnchorsChangedEventArgs args)
    {
        foreach (var a in args.added)
            if (activeSubsystem != null && activeSubsystem.IsStoredAnchor(a)) storedAnchors.Add(a);

        foreach (var a in args.updated)
            if (activeSubsystem != null && activeSubsystem.IsStoredAnchor(a) && localAnchors.Contains(a))
            {
                storedAnchors.Add(a);
                localAnchors.Remove(a);
            }

        foreach (var a in args.removed) storedAnchors.Remove(a);
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