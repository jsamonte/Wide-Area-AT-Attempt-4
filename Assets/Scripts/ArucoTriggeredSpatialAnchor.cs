// ArucoTriggeredSpatialAnchor.cs
// Fixed version — handles nullable MarkerNumber from Magic Leap API.
// Per-ArUco offset/scale/rotation + automatic spatial anchor creation + publish.

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
    [Header("=== ARUCO → PREFAB MAPPINGS (one row per ArUco code) ===")]
    [SerializeField] private List<ArucoPrefabMapping> arucoMappings = new List<ArucoPrefabMapping>();

    [Header("Global ArUco Detector Settings")]
    [SerializeField] private ArucoType arucoDictionary = ArucoType.Dictionary_5x5_250;
    [SerializeField] private float arucoPhysicalLengthMeters = 0.15f;
    [SerializeField] private bool estimateArucoLength = false;

    [Header("XR Origin (auto-found if empty)")]
    [SerializeField] private XROrigin xrOrigin;

    // Internal state
    private MagicLeapMarkerUnderstandingFeature markerFeature;
    private MagicLeapSpatialAnchorsFeature spatialAnchorsFeature;
    private MagicLeapSpatialAnchorsStorageFeature storageFeature;
    private MagicLeapLocalizationMapFeature localizationMapFeature;
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

        [Header("Per-ArUco Settings (marker local space)")]
        [Tooltip("Left (negative) / Right (positive) — along marker X")]
        public float offsetX = 0f;
        [Tooltip("Down (negative) / Up (positive) — along marker Y")]
        public float offsetY = 0f;
        [Tooltip("Backward (negative) / Forward (positive) — along marker Z")]
        public float offsetZ = 0f;

        [Tooltip("Scale multiplier relative to the physical ArUco size")]
        public float scaleMultiplier = 1.0f;

        [Tooltip("Rotation offset in degrees (X is most commonly used, e.g. 270 or 275)")]
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

        if (markerFeature == null || spatialAnchorsFeature == null || storageFeature == null)
        {
            Debug.LogError("❌ Required Magic Leap features missing. Enable Marker Understanding + Spatial Anchors in OpenXR settings.");
            enabled = false;
            yield break;
        }

        if (xrOrigin == null)
            xrOrigin = FindAnyObjectByType<XROrigin>();

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
        Debug.Log($"✅ ArUco detector ready (dictionary: {arucoDictionary}, size: {arucoPhysicalLengthMeters * 1000f} mm)");
    }

    private void OnSpacePermissionGranted(string permission)
    {
        permissionGranted = true;
        Debug.Log("✅ Space permission granted. Auto-publish enabled.");
        if (storageFeature != null && xrOrigin != null)
            storageFeature.QueryStoredSpatialAnchors(xrOrigin.transform.position, 15f);
    }

    private void OnPermissionDenied(string permission)
    {
        permissionGranted = false;
        Debug.LogError("Publishing disabled — SpaceImportExport permission required.");
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
                // === FIX: Handle nullable MarkerNumber from Magic Leap API ===
                if (data.MarkerPose == null || !data.MarkerNumber.HasValue) continue;

                ulong id = data.MarkerNumber.Value;           // safe unwrap
                Pose pose = data.MarkerPose.Value;

                if (pose.position.sqrMagnitude < 0.0001f) continue; // skip bad/zero poses

                var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == id);
                if (mapping == null || mapping.prefab == null) continue;

                if (createdAnchorsByArucoID.ContainsKey(id)) continue; // only ONE anchor per ArUco

                CreateAndPublishAnchorFromMarker(id, mapping, pose);
            }
        }

        UpdateStoredAnchorTransforms();
    }

    private void CreateAndPublishAnchorFromMarker(ulong arucoID, ArucoPrefabMapping mapping, Pose markerRelativePose)
    {
        // World-space transform using XROrigin (prevents lag/jitter)
        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
            ? xrOrigin.CameraFloorOffsetObject.transform
            : null;

        Vector3 markerWorldPos = originT != null ? originT.TransformPoint(markerRelativePose.position) : markerRelativePose.position;
        Quaternion markerWorldRot = originT != null ? originT.rotation * markerRelativePose.rotation : markerRelativePose.rotation;

        // Spawn
        GameObject instance = Instantiate(mapping.prefab, markerWorldPos, markerWorldRot);
        instance.SetActive(true);

        ARAnchor arAnchor = instance.AddComponent<ARAnchor>();
        var renderer = instance.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.material.color = Color.grey;

        createdAnchorsByArucoID[arucoID] = arAnchor;
        localAnchors.Add(arAnchor);

        // === Apply YOUR per-ArUco offset / scale / rotation ===
        Vector3 localOffset = new Vector3(mapping.offsetX, mapping.offsetY, mapping.offsetZ);
        Vector3 worldOffset = markerWorldRot * localOffset;
        Vector3 finalPos = markerWorldPos + worldOffset;

        Quaternion rotOffsetQuat = Quaternion.Euler(mapping.rotationOffset);
        Quaternion finalRot = markerWorldRot * rotOffsetQuat;

        instance.transform.SetPositionAndRotation(finalPos, finalRot);
        instance.transform.localScale = Vector3.one * arucoPhysicalLengthMeters * mapping.scaleMultiplier;

        Debug.Log($"✅ Anchor created + published for ArUco {arucoID}");

        PublishSingleAnchor(arAnchor);
    }

    private void PublishSingleAnchor(ARAnchor anchor)
    {
        if (!permissionGranted || storageFeature == null || anchor?.trackingState != TrackingState.Tracking) return;
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
        if (newOnes.Any()) storageFeature.CreateSpatialAnchorsFromStorage(newOnes.ToList());
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
                var rend = a.GetComponent<MeshRenderer>();
                if (rend != null) rend.material.color = Color.white;
                storedAnchors.Add(a);
                localAnchors.Remove(a);
            }

        foreach (var a in args.removed) storedAnchors.Remove(a);
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