// Prototype2.cs
// Wide-area relocalization: ALL ArUco IDs drive a SINGLE shared prefab instance.
// Each marker re-locks the prefab's world pose when re-acquired (relocalization),
// keeping it registered to the physical world as you move around.
// Thumbstick left/right → shared rotationOffset.Y, up/down → rotationOffset.X

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

public class Prototype2 : MonoBehaviour
{
    // ------------------------------------------------------------------
    // Shared prefab + per-marker ID list
    // All ArUco IDs in arucoIDs drive the same sharedPrefab instance.
    // ------------------------------------------------------------------
    [Header("=== Shared Prefab & Scale ===")]
    [Tooltip("The single prefab instance that all ArUco markers will relocalize.")]
    [SerializeField] private GameObject sharedPrefab;
    [Tooltip("Uniform scale multiplier for the shared prefab.")]
    [SerializeField] private float scaleMultiplier = 1.0f;

    [Header("=== ARUCO -> PREFAB MAPPINGS ===")]
    [SerializeField] private List<ArucoMapping> arucoMappings = new List<ArucoMapping>();

    [Serializable]
    public class ArucoMapping
    {
        public ulong arucoID;
        public float offsetX = 0f;
        public float offsetY = 0f;
        public float offsetZ = 0f;
        public Vector3 rotationOffset = new Vector3(270f, 0f, 0f);
    }

    [Header("Global ArUco Detector Settings")]
    [SerializeField] private ArucoType arucoDictionary = ArucoType.Dictionary_5x5_250;
    [SerializeField] private float arucoPhysicalLengthMeters = 0.15f;
    [SerializeField] private bool estimateArucoLength = false;

    [Header("XR Origin")]
    [SerializeField] private XROrigin xrOrigin;

    [Header("=== Detection Smoothing ===")]
    [Tooltip("Seconds to keep treating a marker as visible after the last detector hit.")]
    [SerializeField] private float visibleHoldSeconds = 0.75f;
    [Tooltip("Seconds of recent samples to average for the reported marker pose.")]
    [SerializeField] private float poseAverageSeconds = 0.25f;
    [Tooltip("Minimum stable samples before relocalizing on a newly-seen marker.")]
    [SerializeField] private int minSamplesForRelocalize = 2;

    [Header("=== Lock-on-Snap ===")]
    [Tooltip("Max age (seconds) of a sample for it to be trusted as a fresh lock.")]
    [SerializeField] private float freshLockSeconds = 0.20f;

    [Header("=== Controller Offset Adjustment ===")]
    [SerializeField] private bool enableControllerAdjustment = true;
    [SerializeField] private float offsetAdjustSpeed = 0.8f;
    [Tooltip("Degrees per second the rotation offset changes when tilting the thumbstick while grabbed.")]
    [SerializeField] private float rotationAdjustSpeed = 45f;
    [Tooltip("Multiplier per second the scale changes when tilting the thumbstick vertically.")]
    [SerializeField] private float scaleAdjustSpeed = 0.6f;
    [SerializeField] private float inputDeadzone = 0.12f;
    [Tooltip("Enable or disable Z-axis rotation via thumbstick while grabbed.")]
    [SerializeField] private bool enableZRotation = true;

    [Header("=== Debug Alignment Info Text ===")]
    [SerializeField] private bool showAlignmentInfoText = true;
    [SerializeField] private float textHeightAboveMarker = 0.12f;
    [SerializeField] private Color textColor = Color.cyan;
    [SerializeField] private float textWorldScale = 0.022f;
    [SerializeField] private int textFontSize = 72;

    // ------------------------------------------------------------------
    // Runtime state
    // ------------------------------------------------------------------
    private MagicLeapMarkerUnderstandingFeature markerFeature;
    private MagicLeapSpatialAnchorsFeature spatialAnchorsFeature;
    private MagicLeapSpatialAnchorsStorageFeature storageFeature;
    private MLXrAnchorSubsystem activeSubsystem;

    private bool permissionGranted = false;
    private bool hasInitializedDetector = false;

    private const ulong INVALID_ARUCO_ID = ulong.MaxValue;
    private ulong lastSeenArucoID = INVALID_ARUCO_ID;

    // The one shared prefab instance in the scene.
    private GameObject _sharedInstance;
    // The prefab's native root scale, captured at Instantiate time.
    // All scale math multiplies this by scaleMultiplier so that 1.0 = designed size.
    private Vector3 _prefabBaseScale = Vector3.one;

    // Whether the user has manually edited the rotation (thumbstick or grab-release).
    // When true, re-localization from any marker updates position but NOT rotation,
    // so the user's edits are preserved.
    private bool _userEditedRotation = false;

    // Per-marker smoothed world poses (same smoothing as before).
    private Dictionary<ulong, Pose> lastDetectedMarkerPoses = new Dictionary<ulong, Pose>();

    // Lock-on-snap state, keyed per marker so each can independently gate.
    private readonly Dictionary<ulong, Pose> _lockedMarkerPose = new Dictionary<ulong, Pose>();
    private readonly HashSet<ulong> _lockedThisAcquisition = new HashSet<ulong>();

    // Smoothing internals
    private struct MarkerSample { public Pose pose; public float t; }
    private readonly Dictionary<ulong, Queue<MarkerSample>> _samples = new Dictionary<ulong, Queue<MarkerSample>>();
    private readonly Dictionary<ulong, float> _lastSeen = new Dictionary<ulong, float>();
    private List<ulong> _evictBuf;

    private GameObject alignmentInfoTextObj;
    private TextMesh alignmentInfoTextMesh;
    private Camera mainCamera;

    private List<InputDevice> rightHandDevices = new List<InputDevice>();
    private List<InputDevice> leftHandDevices = new List<InputDevice>();

    // ------------------------------------------------------------------
    // Init
    // ------------------------------------------------------------------

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
            mainCamera = (xrOrigin != null && xrOrigin.Camera != null) ? xrOrigin.Camera : Camera.main;

        Permissions.RequestPermission(Permissions.SpaceImportExport, OnSpacePermissionGranted, OnPermissionDenied);

        CreateMarkerDetector();
        InitializeAlignmentText();
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
    }

    private void OnSpacePermissionGranted(string permission) { permissionGranted = true; }
    private void OnPermissionDenied(string permission) { permissionGranted = false; }

    // ------------------------------------------------------------------
    // Update — detection → smoothing → relocalization
    // ------------------------------------------------------------------

    void Update()
    {
        if (markerFeature == null || markerFeature.MarkerDetectors.Count == 0) return;

        markerFeature.UpdateMarkerDetectors();
        float now = Time.time;

        // ---- Pass 1: collect raw detections into per-marker sample window ----
        foreach (var detector in markerFeature.MarkerDetectors)
        {
            if (detector.Settings.MarkerType != MarkerType.Aruco) continue;

            foreach (var data in detector.Data)
            {
                if (data.MarkerPose == null || !data.MarkerNumber.HasValue) continue;

                ulong id = data.MarkerNumber.Value;
                if (!arucoMappings.Any(m => m.arucoID == id)) continue;

                Pose trackingPose = data.MarkerPose.Value;
                if (trackingPose.position.sqrMagnitude < 0.0001f) continue;

                Pose worldPose = ToWorld(trackingPose);
                PushSample(id, worldPose, now);
                _lastSeen[id] = now;
            }
        }

        EvictStaleMarkers(now);
        RefreshSmoothedPoses(now);

        // ---- Pass 2: relocalize shared instance from any visible marker ----
        foreach (var kvp in lastDetectedMarkerPoses)
        {
            ulong id = kvp.Key;
            Pose markerWorldPose = kvp.Value;

            lastSeenArucoID = id;

            bool isFresh = SecondsSinceSeen(id) <= Mathf.Max(0.01f, freshLockSeconds);
            bool isStable = RecentSampleCount(id) >= Mathf.Max(1, minSamplesForRelocalize);

            // Spawn the shared instance on first ever detection
            if (_sharedInstance == null && sharedPrefab != null && isFresh && isStable)
            {
                _sharedInstance = Instantiate(sharedPrefab);
                _sharedInstance.SetActive(true);
                _prefabBaseScale = _sharedInstance.transform.localScale;
                _sharedInstance.transform.localScale = _prefabBaseScale * scaleMultiplier;
                SetupGrabInteraction(_sharedInstance);
                Debug.Log($"[Prototype2] Shared prefab spawned on ArUco {id}. BaseScale={_prefabBaseScale}, scaleMultiplier={scaleMultiplier}, final={_sharedInstance.transform.localScale}");
            }

            if (_sharedInstance == null) continue;

            // Re-lock once per acquisition for this marker, then hold
            if (isFresh && isStable && !_lockedThisAcquisition.Contains(id))
            {
                _lockedMarkerPose[id] = markerWorldPose;
                _lockedThisAcquisition.Add(id);

                // Relocalize: update the shared instance's transform.
                // If the user has manually edited rotation, preserve their rotation
                // and only update position (re-anchors to physical world).
                ApplySharedTransform(markerWorldPose, preserveRotation: _userEditedRotation);

                Debug.Log($"[XYaxisalignment] Relocalized from ArUco {id}.");
            }
        }

        UpdateAlignmentInfoText();

        if (enableControllerAdjustment && !IsSharedInstanceGrabbed())
            HandleControllerOffsetAdjustment();
    }

    // ------------------------------------------------------------------
    // Transform application
    // ------------------------------------------------------------------

    /// <summary>
    /// Applies positionOffset + rotationOffset to the shared instance relative
    /// to the given marker pose. If preserveRotation is true, only position is
    /// updated so the user's manual rotation edits are not overwritten.
    /// </summary>
    private void ApplySharedTransform(Pose markerWorldPose, bool preserveRotation = false)
    {
        if (_sharedInstance == null) return;

        var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == lastSeenArucoID);
        if (mapping == null) return;

        var grab = _sharedInstance.GetComponent<XRGrabInteractable>();
        if (grab != null && grab.isSelected) return;

        Vector3 localOffset = new Vector3(mapping.offsetX, mapping.offsetY, mapping.offsetZ);
        Vector3 finalPos = markerWorldPose.position + (markerWorldPose.rotation * localOffset);

        if (preserveRotation)
        {
            _sharedInstance.transform.position = finalPos;
        }
        else
        {
            Quaternion finalRot = markerWorldPose.rotation * Quaternion.Euler(mapping.rotationOffset);
            _sharedInstance.transform.SetPositionAndRotation(finalPos, finalRot);
        }

        _sharedInstance.transform.localScale = _prefabBaseScale * scaleMultiplier;
        Debug.Log($"[Prototype2] ApplySharedTransform: scaleMultiplier={scaleMultiplier}, localScale={_sharedInstance.transform.localScale}");
    }

    // ------------------------------------------------------------------
    // Grab setup & release
    // ------------------------------------------------------------------

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
        grab.useDynamicAttach = true;
        grab.matchAttachPosition = true;
        grab.matchAttachRotation = true;
        grab.snapToColliderVolume = false;

        grab.selectExited.AddListener(OnGrabReleased);
    }

    private void OnGrabReleased(SelectExitEventArgs args)
    {
        if (lastSeenArucoID == INVALID_ARUCO_ID) return;

        // Get the reference marker pose we should measure the offset against
        if (!_lockedMarkerPose.TryGetValue(lastSeenArucoID, out Pose markerWorldPose)
            && !lastDetectedMarkerPoses.TryGetValue(lastSeenArucoID, out markerWorldPose))
            return;

        var releasedObject = args.interactableObject?.transform;
        if (releasedObject == null) return;

        var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == lastSeenArucoID);
        if (mapping == null) return;

        // Bake the new position offset in marker-local space
        Vector3 newLocalOffset = Quaternion.Inverse(markerWorldPose.rotation) * (releasedObject.position - markerWorldPose.position);
        mapping.offsetX = newLocalOffset.x;
        mapping.offsetY = newLocalOffset.y;
        mapping.offsetZ = newLocalOffset.z;

        // Bake the new rotation offset
        Quaternion newLocalRot = Quaternion.Inverse(markerWorldPose.rotation) * releasedObject.rotation;
        mapping.rotationOffset = newLocalRot.eulerAngles;

        _userEditedRotation = true;
        UpdateAlignmentInfoText();
    }

    // ------------------------------------------------------------------
    // Thumbstick grab-time rotation adjustment
    // ------------------------------------------------------------------

    // ==================== Manual Rotation/Scale Adjustment while grabbed ====================

    void LateUpdate()
    {
        if (_sharedInstance == null) return;

        var grab = _sharedInstance.GetComponent<XRGrabInteractable>();
        if (grab == null || !grab.isSelected || grab.interactorsSelecting.Count == 0) return;

        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightHandDevices);
        if (rightHandDevices.Count == 0) return;

        var dev = rightHandDevices[0];
        if (!dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis)) return;

        if (Mathf.Abs(axis.x) <= inputDeadzone && Mathf.Abs(axis.y) <= inputDeadzone) return;

        // Get the pivot point (the controller grab attach point)
        Vector3 pivot = grab.interactorsSelecting[0].transform.position;

        float rotSpeed = rotationAdjustSpeed * Time.deltaTime;
        float sclSpeed = scaleAdjustSpeed * Time.deltaTime;
        bool changed = false;

        // Horizontal thumbstick -> Z rotation (roll) around the pivot
        if (enableZRotation && Mathf.Abs(axis.x) > inputDeadzone)
        {
            float zDelta = axis.x * rotSpeed;
            // Rotate around the controller pivot using the prefab's local Z axis
            _sharedInstance.transform.RotateAround(pivot, _sharedInstance.transform.forward, zDelta);
            changed = true;
        }

        // Vertical thumbstick -> Scale around the pivot (proportional)
        if (Mathf.Abs(axis.y) > inputDeadzone)
        {
            float oldScaleMultiplier = scaleMultiplier;
            // Proportional: multiply by (1 + delta) so the change is a percentage of current size
            scaleMultiplier *= (1f + axis.y * sclSpeed);
            scaleMultiplier = Mathf.Max(0.01f, scaleMultiplier);

            float scaleRatio = scaleMultiplier / oldScaleMultiplier;

            // Scale around pivot: move the position so the pivot stays stationary relative to the mesh
            Vector3 offsetFromPivot = _sharedInstance.transform.position - pivot;
            _sharedInstance.transform.position = pivot + (offsetFromPivot * scaleRatio);

            // Apply the actual scale
            _sharedInstance.transform.localScale = _prefabBaseScale * scaleMultiplier;
            Debug.Log($"[Prototype2] Thumbstick scale: scaleMultiplier={scaleMultiplier}, localScale={_sharedInstance.transform.localScale}");

            changed = true;
        }

        if (changed)
        {
            _userEditedRotation = true;

            // Bake the new world transform back into positionOffset and rotationOffset
            // so the change persists when we release grab.
            if (lastSeenArucoID != INVALID_ARUCO_ID
                && (_lockedMarkerPose.TryGetValue(lastSeenArucoID, out Pose p)
                    || lastDetectedMarkerPoses.TryGetValue(lastSeenArucoID, out p)))
            {
                var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == lastSeenArucoID);
                if (mapping != null)
                {
                    // Bake position offset (marker local space)
                    Vector3 newLocalOffset = Quaternion.Inverse(p.rotation) * (_sharedInstance.transform.position - p.position);
                    mapping.offsetX = newLocalOffset.x;
                    mapping.offsetY = newLocalOffset.y;
                    mapping.offsetZ = newLocalOffset.z;

                    // Bake rotation offset
                    Quaternion newLocalRot = Quaternion.Inverse(p.rotation) * _sharedInstance.transform.rotation;
                    mapping.rotationOffset = newLocalRot.eulerAngles;
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Non-grabbed controller offset adjustment (left/right stick nudge)
    // ------------------------------------------------------------------

    private bool IsSharedInstanceGrabbed()
    {
        if (_sharedInstance == null) return false;
        var grab = _sharedInstance.GetComponent<XRGrabInteractable>();
        return grab != null && grab.isSelected;
    }

    private void HandleControllerOffsetAdjustment()
    {
        if (lastSeenArucoID == INVALID_ARUCO_ID) return;

        var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == lastSeenArucoID);
        if (mapping == null) return;

        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightHandDevices);
        InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, leftHandDevices);

        float speed = offsetAdjustSpeed * Time.deltaTime;
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
        {
            if (_lockedMarkerPose.TryGetValue(lastSeenArucoID, out Pose p)
                || lastDetectedMarkerPoses.TryGetValue(lastSeenArucoID, out p))
                ApplySharedTransform(p, preserveRotation: _userEditedRotation);
        }
    }

    // ------------------------------------------------------------------
    // Debug text
    // ------------------------------------------------------------------

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
        if (!showAlignmentInfoText || alignmentInfoTextMesh == null || lastSeenArucoID == INVALID_ARUCO_ID)
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
                alignmentInfoTextObj.transform.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
        }
        else
        {
            alignmentInfoTextObj.transform.rotation = markerWorldPose.rotation;
        }

        alignmentInfoTextObj.transform.localScale = Vector3.one * textWorldScale;

        var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == lastSeenArucoID);
        if (mapping == null)
        {
            if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
            return;
        }

        alignmentInfoTextMesh.text =
            $"ArUco {lastSeenArucoID} (relocalize)\n" +
            $"Offset   X: {mapping.offsetX:F3}   Y: {mapping.offsetY:F3}   Z: {mapping.offsetZ:F3}\n" +
            $"Rotation X: {mapping.rotationOffset.x:F1}°  Y: {mapping.rotationOffset.y:F1}°  Z: {mapping.rotationOffset.z:F1}°\n" +
            $"Scale: {scaleMultiplier:F2}x";

        alignmentInfoTextObj.SetActive(true);
    }

    // ------------------------------------------------------------------
    // Reset / cleanup
    // ------------------------------------------------------------------

    public void DestroyAll()
    {
        if (_sharedInstance != null)
        {
            Destroy(_sharedInstance);
            _sharedInstance = null;
        }

        lastDetectedMarkerPoses.Clear();
        _lockedMarkerPose.Clear();
        _lockedThisAcquisition.Clear();
        _samples.Clear();
        _lastSeen.Clear();
        _userEditedRotation = false;
        lastSeenArucoID = INVALID_ARUCO_ID;

        if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);

        if (markerFeature != null) markerFeature.DestroyAllMarkerDetectors();
        hasInitializedDetector = false;
    }

    private void OnDestroy() => DestroyAll();

    // ------------------------------------------------------------------
    // Smoothing internals (unchanged from before)
    // ------------------------------------------------------------------

    private Pose ToWorld(Pose tracking)
    {
        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
            ? xrOrigin.CameraFloorOffsetObject.transform : null;
        if (originT == null) return tracking;
        return new Pose(originT.TransformPoint(tracking.position), originT.rotation * tracking.rotation);
    }

    private void PushSample(ulong id, Pose worldPose, float t)
    {
        if (!_samples.TryGetValue(id, out var q)) { q = new Queue<MarkerSample>(); _samples[id] = q; }
        q.Enqueue(new MarkerSample { pose = worldPose, t = t });
        while (q.Count > 240) q.Dequeue();
    }

    private int RecentSampleCount(ulong markerId) =>
        _samples.TryGetValue(markerId, out var q) ? q.Count : 0;

    private float SecondsSinceSeen(ulong markerId) =>
        _lastSeen.TryGetValue(markerId, out var t) ? Time.time - t : float.PositiveInfinity;

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
            _lockedThisAcquisition.Remove(id);
        }
    }

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
