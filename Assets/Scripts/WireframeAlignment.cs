// WireframeAlignment.cs
// trackRotation = false + Manual Z rotation via right thumbstick horizontal
//
// Detection + persistence updated to reuse the approach from
// ArucoCalibrationManager.cs / CalibrationManager.cs / RoomAnchorCalibrationStore.cs:
//   - Marker poses are converted tracking-space -> world-space once, centrally
//     (ToWorld), instead of being re-converted inline in five different places.
//   - Marker poses are smoothed via a per-marker rolling sample window
//     (poseAverageSeconds) and held briefly after the last real detection
//     (visibleHoldSeconds) so the detector's intermittent dropouts don't cause
//     prefab jitter or flicker.
//   - A brand-new anchor is only created once a marker has accumulated
//     minSamplesForAnchorCreate stable recent samples, so a single noisy frame
//     can't permanently place an anchor in the wrong spot. Already-placed
//     prefabs keep tracking continuously off the smoothed pose.
//   - Persistence moved from PlayerPrefs to a JSON file via
//     WireframeMarkerAnchorStore (mirrors RoomAnchorCalibrationStore), with a
//     one-time automatic migration from the old PlayerPrefs data.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using MagicLeap.Android;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;
using MagicLeap.OpenXR.Features.SpatialAnchors;
using MagicLeap.OpenXR.Features.LocalizationMaps;
using MagicLeap.OpenXR.Subsystems;
using Unity.XR.CoreUtils;

/// <summary>
/// Global synchronization for ArUco marker tracking across multiple scripts.
/// Prevents multiple scripts from independently calling UpdateMarkerDetectors() 
/// in the same frame, which blocks the ML2 OS perception pipeline and causes 
/// the main thread to freeze (the "camera stuck" bug). Also prevents redundant
/// detector instances from being created.
/// </summary>
public static class ArucoTrackerSync
{
    private static int lastUpdateFrame = -1;

    public static void UpdateDetectorsOncePerFrame(MagicLeapMarkerUnderstandingFeature feature)
    {
        if (feature == null || feature.MarkerDetectors.Count == 0) return;

        if (Time.frameCount != lastUpdateFrame)
        {
            feature.UpdateMarkerDetectors();
            lastUpdateFrame = Time.frameCount;
        }
    }

    public static bool GlobalDetectorExists(MagicLeapMarkerUnderstandingFeature feature, MarkerType type)
    {
        if (feature == null) return false;
        foreach (var detector in feature.MarkerDetectors)
        {
            if (detector.Settings.MarkerType == type) return true;
        }
        return false;
    }
}

public class WireframeAlignment : MonoBehaviour
{
    [Header("=== ARUCO → PREFAB MAPPINGS ===")]
    [SerializeField] private List<ArucoPrefabMapping> arucoMappings = new List<ArucoPrefabMapping>();

    [Header("Global ArUco Detector Settings")]
    [SerializeField] private ArucoType arucoDictionary = ArucoType.Dictionary_5x5_250;
    [SerializeField] private float arucoPhysicalLengthMeters = 0.15f;
    [SerializeField] private bool estimateArucoLength = false;

    [Header("XR Origin")]
    [SerializeField] private XROrigin xrOrigin;

    [Header("=== Detection Smoothing (ported from ArucoCalibrationManager) ===")]
    [Tooltip("Seconds to keep treating a marker as visible/tracked after the last detector hit. ML2's marker detector returns intermittently even when the marker is clearly in view; this debounces that flicker.")]
    [SerializeField] private float visibleHoldSeconds = 0.75f;
    [Tooltip("Seconds of recent samples to average together for the reported marker pose. Higher = more stable but slower to react. Kept short here (vs. the room-snap manager) so grabbed/placed prefabs stay responsive.")]
    [SerializeField] private float poseAverageSeconds = 0.25f;
    [Tooltip("Minimum number of recent samples required before a NEW anchor is created for a marker we haven't placed yet. Prevents permanently placing an anchor from a single noisy frame. Does not affect already-placed prefabs, which keep tracking every frame they're visible. Lower = more responsive placement, higher = more resistant to jitter.")]
    [SerializeField] private int minSamplesForAnchorCreate = 2;

    [Header("=== Debug Alignment Info Text ===")]
    [SerializeField] private bool showAlignmentInfoText = true;
    [SerializeField] private float textHeightAboveMarker = 0.12f;
    [SerializeField] private Color textColor = Color.cyan;
    [SerializeField] private float textWorldScale = 0.022f;
    [SerializeField] private int textFontSize = 72;

    [Header("=== Controller Offset Adjustment ===")]
    [SerializeField] private bool enableControllerAdjustment = true;
    [SerializeField] private float offsetAdjustSpeed = 0.8f;
    [SerializeField] private float inputDeadzone = 0.12f;

    [Header("=== Grab Behavior ===")]
    [SerializeField] private float scaleAdjustSpeed = 0.6f;
    [SerializeField] private float rotationSpeed = 90f; // degrees per second for Z rotation

    // Persistence (now file-backed via WireframeMarkerAnchorStore; see LoadAnchorMappings/SaveAnchorMappings)
    private Dictionary<string, ulong> anchorMapPosIdToArucoID = new Dictionary<string, ulong>();

    private MagicLeapMarkerUnderstandingFeature markerFeature;
    private MagicLeapSpatialAnchorsFeature spatialAnchorsFeature;
    private MagicLeapSpatialAnchorsStorageFeature storageFeature;
    private MLXrAnchorSubsystem activeSubsystem;

    private Dictionary<ulong, ARAnchor> createdAnchorsByArucoID = new Dictionary<ulong, ARAnchor>();
    private List<ARAnchor> localAnchors = new List<ARAnchor>();
    private List<ARAnchor> storedAnchors = new List<ARAnchor>();

    private bool permissionGranted = false;
    private bool hasInitializedDetector = false;

    private const ulong INVALID_ARUCO_ID = ulong.MaxValue;
    private ulong lastSeenArucoID = INVALID_ARUCO_ID;

    // Smoothed, WORLD-SPACE pose per currently-tracked marker (refreshed each
    // Update from the per-marker sample window). This replaces the old
    // raw/tracking-space single-frame pose dictionary of the same name -- every
    // consumer below now receives an already-world-space pose and no longer
    // does its own origin conversion.
    private Dictionary<ulong, Pose> lastDetectedMarkerPoses = new Dictionary<ulong, Pose>();

    // ---- Lock-on-snap (ported from CalibrationManager.TryArucoAutoSnap) ----
    // A placed prefab's pose is captured ONCE from a fresh, stable detection and
    // then held -- it is NOT re-set every frame from the live marker pose. This
    // is what stops the prefab from drifting/wobbling as the viewing angle
    // changes (ArUco rotation estimates get noisier off-axis; chasing them every
    // frame chases that noise). The lock is recomputed only when the marker is
    // re-acquired after being lost (or on the initial placement), exactly like
    // CalibrationManager's "snap once, hard-set, and lock" pattern.
    [Header("=== Lock-on-Snap (stops perspective-shift drift) ===")]
    [Tooltip("How fresh a sample must be (seconds since last real detector hit) to be trusted for a (re)lock. Mirrors ArucoCalibrationManager.SecondsSinceSeen freshness gating in CalibrationManager.")]
    [SerializeField] private float freshLockSeconds = 0.20f;
    private readonly Dictionary<ulong, Pose> _lockedMarkerPose = new Dictionary<ulong, Pose>();
    private readonly HashSet<ulong> _lockedThisAcquisition = new HashSet<ulong>();
    // Tracks markers whose rotationOffset has been edited by the user (thumbstick
    // or grab-release). On re-acquisition we still update the locked marker pose
    // reference, but we do NOT call UpdateInstanceTransform -- that would snap the
    // prefab back to the raw marker pose and discard the user's edits.
    private readonly HashSet<ulong> _userEditedRotation = new HashSet<ulong>();

    private GameObject alignmentInfoTextObj;
    private TextMesh alignmentInfoTextMesh;
    private Camera mainCamera;

    private List<InputDevice> rightHandDevices = new List<InputDevice>();
    private List<InputDevice> leftHandDevices = new List<InputDevice>();

    // ---- Smoothing internals (mirrors ArucoCalibrationManager) ----
    private struct MarkerSample { public Pose pose; public float t; }
    private readonly Dictionary<ulong, Queue<MarkerSample>> _samples = new Dictionary<ulong, Queue<MarkerSample>>();
    private readonly Dictionary<ulong, float> _lastSeen = new Dictionary<ulong, float>();
    private List<ulong> _evictBuf;
    // O(1) mapping lookup built once from arucoMappings in Start().
    // Replaces per-frame LINQ FirstOrDefault calls that allocate a new delegate
    // closure each invocation and add GC pressure on the main thread.
    private readonly Dictionary<ulong, ArucoPrefabMapping> _mappingLookup = new Dictionary<ulong, ArucoPrefabMapping>();
    // EnforceSingleActivePrefab IPC skip: tracks the last ID we evaluated so we
    // can skip the GetAnchorMapPositionId() loop when nothing has changed.
    // Reset to INVALID_ARUCO_ID by OnAnchorsChanged whenever storedAnchors changes.
    private ulong _lastEnforcedArucoID = INVALID_ARUCO_ID;
    // Reusable yield object for DetectionCollectionLoop; avoids allocating a new
    // WaitForEndOfFrame every iteration (each allocation creates GC pressure).
    private WaitForEndOfFrame _waitForEndOfFrame;

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

        if (mainCamera == null)
        {
            mainCamera = (xrOrigin != null && xrOrigin.Camera != null) ? xrOrigin.Camera : Camera.main;
        }

        LoadAnchorMappings();
        BuildMappingLookup();

        Permissions.RequestPermission(Permissions.SpaceImportExport, OnSpacePermissionGranted, OnPermissionDenied);

        if (storageFeature != null)
            storageFeature.OnQueryComplete += OnQueryComplete;

        CreateMarkerDetector();
        InitializeAlignmentText();

        // Move the blocking UpdateMarkerDetectors() call to a post-render coroutine
        // so ML2 perception-pipeline stalls happen after xrEndFrame and cannot
        // delay the current frame's submission to the compositor.
        _waitForEndOfFrame = new WaitForEndOfFrame();
        StartCoroutine(DetectionCollectionLoop());
    }

    private bool AreSubsystemsLoaded()
    {
        if (XRGeneralSettings.Instance?.Manager?.activeLoader == null) return false;
        activeSubsystem = XRGeneralSettings.Instance.Manager.activeLoader.GetLoadedSubsystem<XRAnchorSubsystem>() as MLXrAnchorSubsystem;
        return activeSubsystem != null;
    }

    // ------------------------------------------------------------------
    // Persistence (file-backed via WireframeMarkerAnchorStore, mirrors
    // RoomAnchorCalibrationStore's pattern). Migrates old PlayerPrefs data
    // automatically the first time LoadAnchorMappings runs after upgrade.
    // ------------------------------------------------------------------

    private void LoadAnchorMappings()
    {
        anchorMapPosIdToArucoID = WireframeMarkerAnchorStore.LoadAll();
    }

    private void SaveAnchorMappings()
    {
        WireframeMarkerAnchorStore.SaveAll(anchorMapPosIdToArucoID);
    }

    /// <summary>
    /// Builds _mappingLookup from arucoMappings for O(1) ID lookup.
    /// Call once in Start() after arucoMappings is populated.
    /// </summary>
    private void BuildMappingLookup()
    {
        _mappingLookup.Clear();
        foreach (var m in arucoMappings)
            if (m != null) _mappingLookup[m.arucoID] = m;
    }

    private void CreateMarkerDetector()
    {
        if (hasInitializedDetector || markerFeature == null) return;

        if (ArucoTrackerSync.GlobalDetectorExists(markerFeature, MarkerType.Aruco))
        {
            Debug.Log("[Console] ARUCO: Detector already exists globally. Skipping duplicate creation.");
            hasInitializedDetector = true;
            return;
        }

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
        QueryExistingAnchors();
    }

    private void OnPermissionDenied(string permission) { permissionGranted = false; }

    void Update()
    {
        if (markerFeature == null || markerFeature.MarkerDetectors.Count == 0) return;

        // UpdateMarkerDetectors() and Pass 1 (raw sample collection) live in
        // DetectionCollectionLoop() -- a WaitForEndOfFrame coroutine that fires
        // after xrEndFrame (the ML2 compositor commit point). Any ML2 pipeline
        // stall there cannot delay the current frame and cannot cause the
        // "camera stuck" freeze. Detection has <=1 frame latency, which is
        // acceptable for anchor placement (smoothing window spans 0.25 s).
        float now = Time.time;

        // ---- Evict markers we haven't actually seen in a while (debounce) ----
        EvictStaleMarkers(now);

        // ---- Recompute the smoothed world pose for every still-tracked marker ----
        RefreshSmoothedPoses(now);

        // ---- Pass 2: drive placement / anchors / grab logic off the smoothed poses ----
        foreach (var kvp in lastDetectedMarkerPoses)
        {
            ulong id = kvp.Key;
            Pose markerWorldPose = kvp.Value;

            if (!_mappingLookup.TryGetValue(id, out var mapping) || mapping.prefab == null) continue;

            lastSeenArucoID = id;

            // Is this sample fresh (a true detector hit, not just held over via
            // visibleHoldSeconds) and stable (enough recent samples)? Only fresh+
            // stable samples are allowed to (re)lock the pose -- this is the same
            // gating CalibrationManager.TryArucoAutoSnap uses before it hard-sets
            // and locks the twin.
            bool isFresh = SecondsSinceSeen(id) <= Mathf.Max(0.01f, freshLockSeconds);
            bool isStable = RecentSampleCount(id) >= Mathf.Max(1, minSamplesForAnchorCreate);

            if (createdAnchorsByArucoID.TryGetValue(id, out ARAnchor existing) && existing != null)
            {
                // Lock once per acquisition instead of chasing the live pose every
                // frame -- this is what stops the prefab from drifting as the
                // viewing angle/perspective shifts. _lockedThisAcquisition is
                // cleared in EvictStaleMarkers when the marker is actually lost,
                // so the next fresh, stable sighting re-locks (self-corrects)
                // exactly once, then holds again.
                if (isFresh && isStable && !_lockedThisAcquisition.Contains(id))
                {
                    _lockedMarkerPose[id] = markerWorldPose;
                    _lockedThisAcquisition.Add(id);
                    // Only snap the prefab transform on the very first lock.
                    // If the user has edited the rotation via thumbstick or grab,
                    // skip UpdateInstanceTransform so their changes aren't overwritten.
                    if (!_userEditedRotation.Contains(id))
                        UpdateInstanceTransform(existing.gameObject, mapping, markerWorldPose);
                }
                // else: already locked this acquisition (or not fresh/stable yet) --
                // hold the existing transform, do NOT re-snap from the live pose.
                continue;
            }

            if (HasAnchorForArucoID(id)) continue;

            // Don't commit a brand-new permanent anchor from a single noisy frame --
            // require a short run of stable samples first.
            if (!isStable) continue;

            CreateAndPublishAnchorFromMarker(id, mapping, markerWorldPose);
            _lockedMarkerPose[id] = markerWorldPose;
            _lockedThisAcquisition.Add(id);
        }

        UpdateStoredAnchorTransforms();
        EnforceSingleActivePrefab();
        UpdateAlignmentInfoText();

        if (enableControllerAdjustment && !IsAnyActiveObjectGrabbed())
            HandleControllerOffsetAdjustment();
    }

    /// <summary>
    /// Runs UpdateMarkerDetectors() and the raw-sample-collection pass in a
    /// WaitForEndOfFrame coroutine so the potentially-blocking ML2 perception-
    /// pipeline sync fires AFTER xrEndFrame -- after the frame is committed to
    /// the ML2 compositor. Even if it stalls for >16ms, the committed frame is
    /// already in the compositor queue and ATW can reproject it correctly.
    /// This eliminates the root cause of the "camera stuck" display freeze:
    /// UpdateMarkerDetectors() can no longer block Unity's Update loop.
    /// Detection results have at most one frame of latency, which is acceptable
    /// for anchor placement (the pose-smoothing window already spans 0.25 s).
    /// </summary>
    private IEnumerator DetectionCollectionLoop()
    {
        while (true)
        {
            yield return _waitForEndOfFrame;

            if (markerFeature == null || markerFeature.MarkerDetectors.Count == 0)
                continue;

            ArucoTrackerSync.UpdateDetectorsOncePerFrame(markerFeature);
            float now = Time.time;

            // ---- Pass 1: collect raw detections into the per-marker sample window ----
            foreach (var detector in markerFeature.MarkerDetectors)
            {
                if (detector.Settings.MarkerType != MarkerType.Aruco) continue;

                foreach (var data in detector.Data)
                {
                    if (data.MarkerPose == null || !data.MarkerNumber.HasValue) continue;

                    ulong id = data.MarkerNumber.Value;
                    Pose trackingPose = data.MarkerPose.Value;
                    if (trackingPose.position.sqrMagnitude < 0.0001f) continue;

                    if (!_mappingLookup.TryGetValue(id, out var mapping) || mapping.prefab == null) continue;

                    Pose worldPose = ToWorld(trackingPose);
                    PushSample(id, worldPose, now);
                    _lastSeen[id] = now;
                }
            }
        }
    }

    private bool IsAnyActiveObjectGrabbed()
    {
        if (lastSeenArucoID == INVALID_ARUCO_ID) return false;

        if (createdAnchorsByArucoID.TryGetValue(lastSeenArucoID, out ARAnchor created) && created != null)
        {
            if (created.transform.childCount > 0)
            {
                var grab = created.transform.GetChild(0).GetComponent<XRGrabInteractable>();
                return grab != null && grab.isSelected;
            }
        }

        foreach (var anchor in storedAnchors)
        {
            if (anchor != null && anchor.transform.childCount > 0)
            {
                var child = anchor.transform.GetChild(0).gameObject;
                var grab = child.GetComponent<XRGrabInteractable>();
                if (grab != null && grab.isSelected) return true;
            }
        }
        return false;
    }

    private void HandleControllerOffsetAdjustment()
    {
        if (lastSeenArucoID == INVALID_ARUCO_ID) return;

        if (!_mappingLookup.TryGetValue(lastSeenArucoID, out var mapping)) return;

        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightHandDevices);
        InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, leftHandDevices);

        float dt = Time.deltaTime;
        float speed = offsetAdjustSpeed * dt;
        bool changed = false;

        if (rightHandDevices.Count > 0)
        {
            var dev = rightHandDevices[0];
            if (dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis))
            {
                if (Mathf.Abs(axis.x) > inputDeadzone) { mapping.offsetX += axis.x * speed; changed = true; }
                if (Mathf.Abs(axis.y) > inputDeadzone) { mapping.offsetZ += axis.y * speed; changed = true; }
            }
        }

        if (leftHandDevices.Count > 0)
        {
            var dev = leftHandDevices[0];
            if (dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis))
            {
                if (Mathf.Abs(axis.y) > inputDeadzone) { mapping.offsetY += axis.y * speed; changed = true; }
            }
        }

        if (changed)
            ForceUpdateActivePrefabTransform(mapping);
    }

    private void ForceUpdateActivePrefabTransform(ArucoPrefabMapping mapping)
    {
        // Use the locked pose (same one the prefab is currently held at), not the
        // live/raw marker pose -- otherwise nudging the offset with the thumbstick
        // while the headset moves would reintroduce the same perspective-shift
        // jitter this fix removes. Fall back to the live pose only if we don't
        // have a lock yet (e.g. very first frame before Pass 2 has locked once).
        if (!_lockedMarkerPose.TryGetValue(lastSeenArucoID, out Pose markerWorldPose)
            && !lastDetectedMarkerPoses.TryGetValue(lastSeenArucoID, out markerWorldPose))
            return;

        if (createdAnchorsByArucoID.TryGetValue(lastSeenArucoID, out ARAnchor created) && created != null && created.gameObject != null)
        {
            if (created.transform.childCount > 0)
            {
                UpdateInstanceTransformRelativeToAnchor(created.transform.GetChild(0).gameObject, mapping, created);
            }
            return;
        }

        if (activeSubsystem == null) return;

        foreach (ARAnchor anchor in storedAnchors)
        {
            if (anchor == null || anchor.gameObject == null) continue;
            string mapId = activeSubsystem.GetAnchorMapPositionId(anchor);
            if (string.IsNullOrEmpty(mapId)) continue;
            if (!anchorMapPosIdToArucoID.TryGetValue(mapId, out ulong mapped) || mapped != lastSeenArucoID) continue;

            if (anchor.transform.childCount > 0)
            {
                GameObject child = anchor.transform.GetChild(0).gameObject;
                UpdateInstanceTransformRelativeToAnchor(child, mapping, anchor);
            }
            break;
        }
    }

    private bool HasAnchorForArucoID(ulong arucoID)
    {
        if (createdAnchorsByArucoID.TryGetValue(arucoID, out ARAnchor ca) && ca != null && ca.gameObject != null)
            return true;

        if (activeSubsystem == null) return false;

        foreach (ARAnchor sa in storedAnchors.ToList())
        {
            if (sa == null) continue;
            string mapId = activeSubsystem.GetAnchorMapPositionId(sa);
            if (!string.IsNullOrEmpty(mapId) && anchorMapPosIdToArucoID.TryGetValue(mapId, out ulong mapped) && mapped == arucoID)
                return true;
        }
        return false;
    }

    private void EnforceSingleActivePrefab()
    {
        if (lastSeenArucoID == INVALID_ARUCO_ID) return;
        // Skip the per-anchor IPC pass entirely when the active marker ID is
        // unchanged. GetAnchorMapPositionId is a synchronous ML2 subsystem call;
        // firing it for every stored anchor every frame adds pipeline stalls.
        // _lastEnforcedArucoID is reset by OnAnchorsChanged when storedAnchors
        // changes, so a newly added/removed anchor always triggers a fresh pass.
        if (lastSeenArucoID == _lastEnforcedArucoID) return;
        _lastEnforcedArucoID = lastSeenArucoID;

        foreach (var kvp in createdAnchorsByArucoID)
        {
            if (kvp.Value != null && kvp.Value.gameObject != null)
            {
                bool shouldBeActive = kvp.Key == lastSeenArucoID;
                if (kvp.Value.gameObject.activeSelf != shouldBeActive)
                    kvp.Value.gameObject.SetActive(shouldBeActive);
            }
        }

        if (activeSubsystem != null)
        {
            // Direct iteration (no .ToList() allocation): SetActive and
            // GetAnchorMapPositionId do not modify storedAnchors, so safe.
            foreach (ARAnchor anchor in storedAnchors)
            {
                if (anchor == null || anchor.gameObject == null) continue;
                string mapPosId = activeSubsystem.GetAnchorMapPositionId(anchor);
                bool shouldShow = false;
                if (!string.IsNullOrEmpty(mapPosId) && anchorMapPosIdToArucoID.TryGetValue(mapPosId, out ulong mappedAruco))
                    shouldShow = (mappedAruco == lastSeenArucoID);
                if (anchor.gameObject.activeSelf != shouldShow)
                    anchor.gameObject.SetActive(shouldShow);
            }
        }
    }

    private void CreateAndPublishAnchorFromMarker(ulong arucoID, ArucoPrefabMapping mapping, Pose markerWorldPose)
    {
        GameObject anchorObj = new GameObject($"Anchor_{arucoID}");
        anchorObj.transform.SetPositionAndRotation(markerWorldPose.position, markerWorldPose.rotation);
        ARAnchor arAnchor = anchorObj.AddComponent<ARAnchor>();

        GameObject instance = Instantiate(mapping.prefab, markerWorldPose.position, markerWorldPose.rotation);
        instance.transform.SetParent(anchorObj.transform);
        instance.SetActive(true);

        SetupGrabInteraction(instance);

        var rend = instance.GetComponent<MeshRenderer>();
        if (rend != null) rend.material.color = Color.grey;

        createdAnchorsByArucoID[arucoID] = arAnchor;
        localAnchors.Add(arAnchor);

        UpdateInstanceTransformRelativeToAnchor(instance, mapping, arAnchor);
        PublishSingleAnchor(arAnchor);
    }

    private void UpdateInstanceTransform(GameObject anchorObj, ArucoPrefabMapping mapping, Pose markerWorldPose)
    {
        if (anchorObj == null) return;
        anchorObj.transform.SetPositionAndRotation(markerWorldPose.position, markerWorldPose.rotation);
        if (anchorObj.transform.childCount > 0)
        {
            var child = anchorObj.transform.GetChild(0).gameObject;
            ARAnchor arAnchor = anchorObj.GetComponent<ARAnchor>();
            if (arAnchor != null)
                UpdateInstanceTransformRelativeToAnchor(child, mapping, arAnchor);
        }
    }

    private void PublishSingleAnchor(ARAnchor anchor)
    {
        if (!permissionGranted || storageFeature == null || anchor?.trackingState != TrackingState.Tracking) return;
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
        if (newAnchors.Count > 0)
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
                    _mappingLookup.TryGetValue(savedArucoID, out var mapping);
                    if (mapping != null && mapping.prefab != null)
                    {
                        GameObject instance = Instantiate(mapping.prefab, anchor.transform.position, anchor.transform.rotation);
                        SetupGrabInteraction(instance);
                        instance.transform.SetParent(anchor.transform);
                        UpdateInstanceTransformRelativeToAnchor(instance, mapping, anchor);
                        Debug.Log($"[Persistence] ✅ Restored prefab for ArUco {savedArucoID}");
                        anchor.gameObject.SetActive(false);
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
        // Anchor list changed; force EnforceSingleActivePrefab to re-evaluate
        // visibility on the next Update instead of using the cached result.
        _lastEnforcedArucoID = INVALID_ARUCO_ID;
    }

    private void UpdateInstanceTransformRelativeToAnchor(GameObject instance, ArucoPrefabMapping mapping, ARAnchor anchor)
    {
        if (instance == null || anchor == null) return;

        var grab = instance.GetComponent<XRGrabInteractable>();
        if (grab != null && grab.isSelected) return;

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
            // Only update pose for active anchors. EnforceSingleActivePrefab
            // hides all anchors except the currently-matched one, so calling
            // GetAnchorPose (a synchronous ML2 IPC call) for every inactive
            // anchor every frame wastes pipeline bandwidth unnecessarily.
            if (anchor != null && anchor.gameObject.activeSelf)
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
        lastDetectedMarkerPoses.Clear();
        _lockedMarkerPose.Clear();
        _lockedThisAcquisition.Clear();
        _userEditedRotation.Clear();
        _samples.Clear();
        _lastSeen.Clear();
        lastSeenArucoID = 0;
        _lastEnforcedArucoID = INVALID_ARUCO_ID;

        if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);

        if (markerFeature != null) markerFeature.DestroyAllMarkerDetectors();
        hasInitializedDetector = false;
    }

    private void OnDestroy()
    {
        if (storageFeature != null) storageFeature.OnQueryComplete -= OnQueryComplete;
        DestroyAll();
    }

    // ==================== Grab Setup ====================

    void SetupGrabInteraction(GameObject instance)
    {
        if (instance == null) return;

        var rb = instance.GetComponent<Rigidbody>();
        if (rb == null) rb = instance.AddComponent<Rigidbody>();

        rb.isKinematic = true;
        rb.useGravity = false;

        var grab = instance.GetComponent<XRGrabInteractable>();
        if (grab == null) grab = instance.AddComponent<XRGrabInteractable>();


        grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
        grab.trackPosition = true;
        grab.trackRotation = false;
        grab.throwOnDetach = false;
        grab.retainTransformParent = false;
        grab.smoothPosition = false;
        grab.smoothRotation = false;

        // NEW: preserve current pose/offset relative to controller on grab
        grab.useDynamicAttach = true;
        grab.matchAttachPosition = true;
        grab.matchAttachRotation = true;
        grab.snapToColliderVolume = false; // don't re-center on the collider either



        grab.selectExited.AddListener(OnGrabReleased);
    }

    private void OnGrabReleased(SelectExitEventArgs args)
    {
        if (lastSeenArucoID == INVALID_ARUCO_ID) return;

        if (!_mappingLookup.TryGetValue(lastSeenArucoID, out var mapping)) return;

        var releasedObject = args.interactableObject?.transform;
        if (releasedObject == null) return;

        ARAnchor parentAnchor = releasedObject.GetComponentInParent<ARAnchor>();
        if (parentAnchor != null)
        {
            Vector3 newLocalOffset = parentAnchor.transform.InverseTransformPoint(releasedObject.position);
            mapping.offsetX = newLocalOffset.x;
            mapping.offsetY = newLocalOffset.y;
            mapping.offsetZ = newLocalOffset.z;

            Quaternion newLocalRot = Quaternion.Inverse(parentAnchor.transform.rotation) * releasedObject.rotation;
            mapping.rotationOffset = newLocalRot.eulerAngles;
        }

        _userEditedRotation.Add(lastSeenArucoID);
        UpdateAlignmentInfoText();
    }

    // ==================== Manual Z Rotation + Scale while grabbed ====================

    void LateUpdate()
    {
        if (lastSeenArucoID == INVALID_ARUCO_ID) return;

        if (!_mappingLookup.TryGetValue(lastSeenArucoID, out var mapping)) return;

        GameObject grabbedObject = null;

        if (createdAnchorsByArucoID.TryGetValue(lastSeenArucoID, out ARAnchor created) && created != null)
        {
            if (created.transform.childCount > 0)
            {
                var child = created.transform.GetChild(0).gameObject;
                var grab = child.GetComponent<XRGrabInteractable>();
                if (grab != null && grab.isSelected) grabbedObject = child;
            }
        }
        else
        {
            foreach (var anchor in storedAnchors)
            {
                if (anchor != null && anchor.transform.childCount > 0)
                {
                    var child = anchor.transform.GetChild(0).gameObject;
                    var grab = child.GetComponent<XRGrabInteractable>();
                    if (grab != null && grab.isSelected)
                    {
                        grabbedObject = child;
                        break;
                    }
                }
            }
        }

        if (grabbedObject != null)
        {
            InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightHandDevices);
            if (rightHandDevices.Count > 0)
            {
                var dev = rightHandDevices[0];
                if (dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis))
                {
                    // Horizontal = Z rotation (roll)
                    if (Mathf.Abs(axis.x) > 0.1f)
                    {
                        float zDelta = axis.x * rotationSpeed * Time.deltaTime;
                        grabbedObject.transform.Rotate(0, 0, zDelta, Space.Self);
                        _userEditedRotation.Add(lastSeenArucoID);
                    }

                    // Vertical = Scale
                    if (Mathf.Abs(axis.y) > 0.1f)
                    {
                        mapping.scaleMultiplier += axis.y * scaleAdjustSpeed * Time.deltaTime;
                        mapping.scaleMultiplier = Mathf.Max(0.1f, mapping.scaleMultiplier);
                        grabbedObject.transform.localScale = Vector3.one * arucoPhysicalLengthMeters * mapping.scaleMultiplier;
                    }
                }
            }
        }
    }

    private void InitializeAlignmentText()
    {
        if (!showAlignmentInfoText) return;
        if (alignmentInfoTextObj != null) return;

        alignmentInfoTextObj = new GameObject("ArUcoAlignmentInfoText");
        alignmentInfoTextObj.transform.SetParent(null);

        alignmentInfoTextMesh = alignmentInfoTextObj.AddComponent<TextMesh>();
        alignmentInfoTextMesh.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        alignmentInfoTextMesh.fontSize = textFontSize;
        alignmentInfoTextMesh.color = textColor;
        alignmentInfoTextMesh.anchor = TextAnchor.MiddleCenter;
        alignmentInfoTextMesh.alignment = TextAlignment.Center;

        alignmentInfoTextObj.SetActive(false);
    }

    private void UpdateAlignmentInfoText()
    {
        if (!showAlignmentInfoText || alignmentInfoTextMesh == null || lastSeenArucoID == 0)
        {
            if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
            return;
        }

        var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == lastSeenArucoID);
        if (mapping == null || mapping.prefab == null)
        {
            if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
            return;
        }

        if (!lastDetectedMarkerPoses.TryGetValue(lastSeenArucoID, out Pose markerWorldPose))
        {
            if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
            return;
        }

        Vector3 textPos = markerWorldPose.position + Vector3.up * textHeightAboveMarker;
        alignmentInfoTextObj.transform.position = textPos;

        if (mainCamera != null)
        {
            Vector3 toCamera = mainCamera.transform.position - textPos;
            if (toCamera.sqrMagnitude > 0.0001f)
            {
                alignmentInfoTextObj.transform.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
            }
        }
        else
        {
            alignmentInfoTextObj.transform.rotation = markerWorldPose.rotation;
        }

        alignmentInfoTextObj.transform.localScale = Vector3.one * textWorldScale;

        alignmentInfoTextMesh.text =
            $"ArUco {lastSeenArucoID}\n" +
            $"Offset   X: {mapping.offsetX:F3}   Y: {mapping.offsetY:F3}   Z: {mapping.offsetZ:F3}\n" +
            $"Rotation X: {mapping.rotationOffset.x:F1}°  Y: {mapping.rotationOffset.y:F1}°  Z: {mapping.rotationOffset.z:F1}°\n" +
            $"Scale: {mapping.scaleMultiplier:F2}x";

        alignmentInfoTextObj.SetActive(true);
    }

    // ------------------------------------------------------------------
    // Detection smoothing internals (ported from ArucoCalibrationManager.cs)
    // ------------------------------------------------------------------

    /// <summary>
    /// Convert a marker pose reported by Magic Leap (in XR Origin tracking
    /// space) into world space. Centralizes the conversion that used to be
    /// duplicated inline in CreateAndPublishAnchorFromMarker,
    /// UpdateInstanceTransform, OnGrabReleased, and UpdateAlignmentInfoText.
    /// </summary>
    private Pose ToWorld(Pose tracking)
    {
        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
            ? xrOrigin.CameraFloorOffsetObject.transform : null;
        if (originT == null) return tracking;

        Vector3 worldPos = originT.TransformPoint(tracking.position);
        Quaternion worldRot = originT.rotation * tracking.rotation;
        return new Pose(worldPos, worldRot);
    }

    private void PushSample(ulong id, Pose worldPose, float t)
    {
        if (!_samples.TryGetValue(id, out var q))
        {
            q = new Queue<MarkerSample>();
            _samples[id] = q;
        }
        q.Enqueue(new MarkerSample { pose = worldPose, t = t });
        // Hard cap so the queue can't grow unbounded if poseAverageSeconds is huge.
        while (q.Count > 240) q.Dequeue();
    }

    /// <summary>Number of samples currently in the averaging window for a given marker.</summary>
    private int RecentSampleCount(ulong markerId)
    {
        return _samples.TryGetValue(markerId, out var q) ? q.Count : 0;
    }

    /// <summary>
    /// Seconds since this marker was last *actually* detected by the ML2 detector
    /// (not just held over via visibleHoldSeconds). Mirrors
    /// ArucoCalibrationManager.SecondsSinceSeen -- used to gate (re)locking so we
    /// only trust a true, fresh detection rather than a held/stale one.
    /// </summary>
    private float SecondsSinceSeen(ulong markerId)
    {
        if (!_lastSeen.TryGetValue(markerId, out var t)) return float.PositiveInfinity;
        return Time.time - t;
    }

    /// <summary>Evict markers we haven't actually seen in over visibleHoldSeconds.</summary>
    private void EvictStaleMarkers(float now)
    {
        float cutoff = now - Mathf.Max(0f, visibleHoldSeconds);
        if (_evictBuf == null) _evictBuf = new List<ulong>();
        _evictBuf.Clear();
        foreach (var kv in _lastSeen) if (kv.Value < cutoff) _evictBuf.Add(kv.Key);
        for (int i = 0; i < _evictBuf.Count; i++)
        {
            ulong id = _evictBuf[i];
            _lastSeen.Remove(id);
            _samples.Remove(id);
            lastDetectedMarkerPoses.Remove(id);
            // Marker genuinely lost (not just a held-over hiccup) -- allow the
            // next fresh, stable re-acquisition to re-lock the prefab pose once.
            // Note: _lockedMarkerPose is intentionally left in place (it's the
            // last good pose, still rendered) until a new lock overwrites it.
            _lockedThisAcquisition.Remove(id);
        }
    }

    /// <summary>Recompute lastDetectedMarkerPoses[id] as the average of samples within poseAverageSeconds.</summary>
    private void RefreshSmoothedPoses(float now)
    {
        float winCutoff = now - Mathf.Max(0.001f, poseAverageSeconds);
        foreach (var kv in _samples)
        {
            ulong id = kv.Key;
            var buf = kv.Value;
            while (buf.Count > 0 && buf.Peek().t < winCutoff) buf.Dequeue();
            if (buf.Count == 0) continue;
            lastDetectedMarkerPoses[id] = AveragePose(buf);
        }
    }

    private static Pose AveragePose(Queue<MarkerSample> samples)
    {
        Vector3 sumPos = Vector3.zero;
        Vector4 sumQ = Vector4.zero;
        Quaternion first = Quaternion.identity;
        bool haveFirst = false;
        int n = 0;
        foreach (var s in samples)
        {
            sumPos += s.pose.position;
            Quaternion r = s.pose.rotation;
            if (!haveFirst) { first = r; haveFirst = true; }
            if (Quaternion.Dot(first, r) < 0f) r = new Quaternion(-r.x, -r.y, -r.z, -r.w);
            sumQ += new Vector4(r.x, r.y, r.z, r.w);
            n++;
        }
        if (n == 0) return new Pose(Vector3.zero, Quaternion.identity);
        sumQ.Normalize();
        return new Pose(sumPos / n, new Quaternion(sumQ.x, sumQ.y, sumQ.z, sumQ.w));
    }
}
