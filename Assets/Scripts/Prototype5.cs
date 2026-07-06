// Prototype5.cs
// WLT SpacePin implementation.

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
using Unity.XR.CoreUtils;
using Microsoft.MixedReality.WorldLocking.Core;
using Microsoft.MixedReality.WorldLocking.Tools;

public class Prototype5 : MonoBehaviour
{
    [Header("=== Shared Prefab & Scale ===")]
    [Tooltip("The single prefab instance that all ArUco markers will relocalize.")]
    [SerializeField] private GameObject sharedPrefab;

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
    [SerializeField] private float inputDeadzone = 0.12f;

    [Header("=== Axis Constraints ===")]
    [Tooltip("When disabled, the Z component of rotationOffset is forced to 0, preventing roll.")]
    [SerializeField] private bool enableZAxisRotation = true;

    [Header("=== Debug Alignment Info Text ===")]
    [SerializeField] private bool showAlignmentInfoText = true;
    [SerializeField] private float textHeightAboveMarker = 0.12f;
    [SerializeField] private Color textColor = Color.cyan;
    [SerializeField] private float textWorldScale = 0.022f;
    [SerializeField] private int textFontSize = 72;

    [Header("=== WLT SpacePin ===")]
    [Tooltip("If true, WLT SpacePins will be used to lock the coordinate system to the ArUco marker.")]
    // This statement is describing a hybrid of the two systems you are looking at!
    // Pin 1: Translates the entire virtual world to match the physical origin.
    // Pin 2: Rotates the entire virtual world around Pin 1 to fix the orientation.
    // Pin 3+: Adjusts scale and corrects localized drift.
    [SerializeField] private bool useSpacePins = true;

    [Header("=== PLUME Replay Fix ===")]
    [Tooltip("Empty prefab to act as PlumeWrapper. Solves the 0,0,0 coordinate frame bug in PLUME.")]
    [SerializeField] private GameObject emptyAnchorPrefab;

    [Header("=== Gaze Countdown (Dwell) ===")]
    [Tooltip("How many seconds the user must continuously look at the marker before it drops the pin.")]
    [SerializeField] private float requiredDwellSeconds = 2.0f;

    // ------------------------------------------------------------------
    // Runtime state
    // ------------------------------------------------------------------
    private MagicLeapMarkerUnderstandingFeature markerFeature;

    private bool permissionGranted = false;
    private bool hasInitializedDetector = false;

    private const ulong INVALID_ARUCO_ID = ulong.MaxValue;
    private ulong lastSeenArucoID = INVALID_ARUCO_ID;
    private ulong anchoredArucoID = INVALID_ARUCO_ID;

    private GameObject _sharedInstance;
    private Dictionary<ulong, GameObject> _pinObjects = new Dictionary<ulong, GameObject>();
    private Dictionary<ulong, SpacePinOrientable> _spacePins = new Dictionary<ulong, SpacePinOrientable>();
    private bool _userEditedRotation = false;

    // Now storing Spongy Pose (raw physical tracking pose)
    private Dictionary<ulong, Pose> lastDetectedMarkerPoses = new Dictionary<ulong, Pose>();
    private readonly Dictionary<ulong, Pose> _lockedMarkerPose = new Dictionary<ulong, Pose>();
    private readonly HashSet<ulong> _lockedThisAcquisition = new HashSet<ulong>();

    private struct MarkerSample { public Pose pose; public float t; }
    private readonly Dictionary<ulong, Queue<MarkerSample>> _samples = new Dictionary<ulong, Queue<MarkerSample>>();
    private readonly Dictionary<ulong, float> _lastSeen = new Dictionary<ulong, float>();
    private readonly Dictionary<ulong, float> _firstSeen = new Dictionary<ulong, float>();
    private List<ulong> _evictBuf;

    private GameObject alignmentInfoTextObj;
    private TextMesh alignmentInfoTextMesh;

    private List<InputDevice> rightHandDevices = new List<InputDevice>();
    private List<InputDevice> leftHandDevices = new List<InputDevice>();

    // ------------------------------------------------------------------
    // Init
    // ------------------------------------------------------------------

    private IEnumerator Start()
    {
        yield return new WaitUntil(AreSubsystemsLoaded);

        markerFeature = OpenXRSettings.Instance.GetFeature<MagicLeapMarkerUnderstandingFeature>();

        if (markerFeature == null)
        {
            Debug.LogError("❌ Required Magic Leap Marker feature missing.");
            enabled = false;
            yield break;
        }

        Permissions.RequestPermission(Permissions.SpaceImportExport, OnSpacePermissionGranted, OnPermissionDenied);

        CreateMarkerDetector();
        InitializeAlignmentText();
    }

    private bool AreSubsystemsLoaded()
    {
        if (XRGeneralSettings.Instance?.Manager?.activeLoader == null) return false;
        return true;
    }

    private void CreateMarkerDetector()
    {
        if (hasInitializedDetector || markerFeature == null) return;
        var settings = new MarkerDetectorSettings {
            MarkerDetectorProfile = MarkerDetectorProfile.Default,
            MarkerType = MarkerType.Aruco,
            ArucoSettings = new ArucoSettings {
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

    void Update()
    {
        if (markerFeature == null || markerFeature.MarkerDetectors.Count == 0) return;
        markerFeature.UpdateMarkerDetectors();
        float now = Time.time;

        foreach (var detector in markerFeature.MarkerDetectors)
        {
            if (detector.Settings.MarkerType != MarkerType.Aruco) continue;
            foreach (var data in detector.Data)
            {
                if (data.MarkerPose == null || !data.MarkerNumber.HasValue) continue;
                ulong id = data.MarkerNumber.Value;
                if (!arucoMappings.Any(m => m.arucoID == id)) continue;

                // Spongy pose from the sensor
                Pose trackingPose = data.MarkerPose.Value;
                if (trackingPose.position.sqrMagnitude < 0.0001f) continue;
                PushSample(id, trackingPose, now);
                _lastSeen[id] = now;
                if (!_firstSeen.ContainsKey(id)) _firstSeen[id] = now;
            }
        }

        EvictStaleMarkers(now);
        RefreshSmoothedPoses(now);

        foreach (var kvp in lastDetectedMarkerPoses)
        {
            ulong id = kvp.Key;
            Pose markerSpongyPose = kvp.Value;
            
            // --- Gravity Alignment Fix (Floor Markers) ---
            Vector3 rawUp = markerSpongyPose.rotation * Vector3.up;
            Vector3 rawForward = markerSpongyPose.rotation * Vector3.forward;
            
            Vector3 flatForward = rawForward;
            flatForward.y = 0;
            
            if (flatForward.sqrMagnitude < 0.1f)
            {
                Vector3 flatUp = rawUp;
                flatUp.y = 0;
                Vector3 perfectZ = (rawForward.y > 0) ? Vector3.up : Vector3.down;
                if (flatUp.sqrMagnitude > 0.001f)
                {
                    markerSpongyPose.rotation = Quaternion.LookRotation(perfectZ, flatUp.normalized);
                }
            }
            else
            {
                Vector3 perfectY = (rawUp.y > 0) ? Vector3.up : Vector3.down;
                markerSpongyPose.rotation = Quaternion.LookRotation(flatForward.normalized, perfectY);
            }
            // ---------------------------------------------
            
            lastSeenArucoID = id;

            bool isFresh = SecondsSinceSeen(id) <= Mathf.Max(0.01f, freshLockSeconds);
            bool isStable = RecentSampleCount(id) >= Mathf.Max(1, minSamplesForRelocalize);
            bool hasDwelt = _firstSeen.TryGetValue(id, out float firstSeenTime) && (Time.time - firstSeenTime >= requiredDwellSeconds);

            if (_sharedInstance == null && sharedPrefab != null && isFresh && isStable && hasDwelt)
            {
                _sharedInstance = Instantiate(sharedPrefab, Vector3.zero, Quaternion.identity);
                _sharedInstance.SetActive(true);
                SetupGrabInteraction(_sharedInstance);
                Debug.Log($"[WLT] Shared prefab instance spawned. Preparing to lock to ArUco {id}.");
            }

            if (_sharedInstance == null) continue;

            if (isFresh && isStable && hasDwelt && !_lockedThisAcquisition.Contains(id))
            {
                _lockedMarkerPose[id] = markerSpongyPose;
                _lockedThisAcquisition.Add(id);

                if (useSpacePins && !_spacePins.ContainsKey(id))
                {
                    GameObject pinObj;
                    if (emptyAnchorPrefab != null)
                    {
                        pinObj = Instantiate(emptyAnchorPrefab, Vector3.zero, Quaternion.identity);
                    }
                    else
                    {
                        pinObj = new GameObject($"SpacePin_{id}");
                        pinObj.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    }
                    pinObj.name = $"SpacePin_{id}";
                    _pinObjects[id] = pinObj;
                    _spacePins[id] = pinObj.AddComponent<SpacePinOrientable>();
                }

                if (useSpacePins && anchoredArucoID == id && _spacePins.ContainsKey(id))
                {
                    // WLT handles continuous spongy pose updates smoothly
                }

                anchoredArucoID = id;
                ApplySharedTransform(id, markerSpongyPose, preserveRotation: _userEditedRotation);
                Debug.Log($"[WLT] Relocalized and set SpacePin on ArUco {id}.");
            }
        }

        UpdateAlignmentInfoText();

        if (!IsSharedInstanceGrabbed())
        {
            if (enableControllerAdjustment)
                HandleControllerOffsetAdjustment();
        }
    }

    private void ApplySharedTransform(ulong id, Pose markerSpongyPose, bool preserveRotation = false)
    {
        if (_sharedInstance == null) return;
        var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == id);
        if (mapping == null) return;
        var grab = _sharedInstance.GetComponent<XRGrabInteractable>();
        if (grab != null && grab.isSelected) return;

        Matrix4x4 prefabLocal = Matrix4x4.TRS(
            new Vector3(mapping.offsetX, mapping.offsetY, mapping.offsetZ),
            Quaternion.Euler(mapping.rotationOffset),
            Vector3.one
        );

        if (useSpacePins && _spacePins.TryGetValue(id, out var pin))
        {
            Matrix4x4 pinVirtual = prefabLocal.inverse;
            pin.transform.SetPositionAndRotation(pinVirtual.GetColumn(3), pinVirtual.rotation);
            pin.SetSpongyPose(markerSpongyPose);
        }
        else if (!useSpacePins)
        {
            Matrix4x4 spongyMat = Matrix4x4.TRS(markerSpongyPose.position, markerSpongyPose.rotation, Vector3.one);
            Matrix4x4 instanceMat = spongyMat * prefabLocal;
            _sharedInstance.transform.SetPositionAndRotation(instanceMat.GetColumn(3), instanceMat.rotation);
        }
    }

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

        grab.selectEntered.AddListener(OnGrabEntered);
        grab.selectExited.AddListener(OnGrabReleased);
    }

    private void OnGrabEntered(SelectEnterEventArgs args)
    {
        // No longer need to destroy anchor! WLT keeps the world stable.
    }

    private void OnGrabReleased(SelectExitEventArgs args)
    {
        if (lastSeenArucoID == INVALID_ARUCO_ID) return;

        var releasedObject = args.interactableObject?.transform;
        if (releasedObject == null) return;

        UpdateAllMappingsFromCurrentTransforms();

        _userEditedRotation = true;
        UpdateAlignmentInfoText();
    }

    private void UpdateAllMappingsFromCurrentTransforms()
    {
        if (_sharedInstance == null) return;

        Matrix4x4 sharedMat = Matrix4x4.TRS(_sharedInstance.transform.position, _sharedInstance.transform.rotation, Vector3.one);

        foreach (var mapping in arucoMappings)
        {
            if (useSpacePins && _pinObjects.TryGetValue(mapping.arucoID, out var pinObj))
            {
                Matrix4x4 pinMat = Matrix4x4.TRS(pinObj.transform.position, pinObj.transform.rotation, Vector3.one);
                Matrix4x4 prefabLocal = pinMat.inverse * sharedMat;
                
                mapping.offsetX = prefabLocal.GetColumn(3).x;
                mapping.offsetY = prefabLocal.GetColumn(3).y;
                mapping.offsetZ = prefabLocal.GetColumn(3).z;
                mapping.rotationOffset = prefabLocal.rotation.eulerAngles;
            }
            else if (!useSpacePins && lastSeenArucoID == mapping.arucoID)
            {
                if (lastDetectedMarkerPoses.TryGetValue(mapping.arucoID, out Pose markerSpongyPose))
                {
                    Matrix4x4 spongyMat = Matrix4x4.TRS(markerSpongyPose.position, markerSpongyPose.rotation, Vector3.one);
                    Matrix4x4 prefabLocal = spongyMat.inverse * sharedMat;
                    
                    mapping.offsetX = prefabLocal.GetColumn(3).x;
                    mapping.offsetY = prefabLocal.GetColumn(3).y;
                    mapping.offsetZ = prefabLocal.GetColumn(3).z;
                    mapping.rotationOffset = prefabLocal.rotation.eulerAngles;
                }
            }
        }
    }

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

        Vector3 pivot = grab.interactorsSelecting[0].transform.position;
        float rotSpeed = rotationAdjustSpeed * Time.deltaTime;
        bool changed = false;

        if (Mathf.Abs(axis.x) > inputDeadzone && enableZAxisRotation)
        {
            _sharedInstance.transform.RotateAround(pivot, _sharedInstance.transform.forward, axis.x * rotSpeed);
            changed = true;
        }

        if (changed)
        {
            _userEditedRotation = true;
            UpdateAllMappingsFromCurrentTransforms();
        }
    }

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

        if (rightHandDevices.Count > 0 && rightHandDevices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 rAxis))
        {
            if (Mathf.Abs(rAxis.x) > inputDeadzone) { mapping.offsetX += rAxis.x * speed; changed = true; }
            if (Mathf.Abs(rAxis.y) > inputDeadzone) { mapping.offsetZ += rAxis.y * speed; changed = true; }
        }

        if (leftHandDevices.Count > 0 && leftHandDevices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 lAxis))
        {
            if (Mathf.Abs(lAxis.y) > inputDeadzone) { mapping.offsetY += lAxis.y * speed; changed = true; }
        }

        if (changed)
        {
            if (useSpacePins && _pinObjects.TryGetValue(lastSeenArucoID, out var pinObj))
            {
                Matrix4x4 pinMat = Matrix4x4.TRS(pinObj.transform.position, pinObj.transform.rotation, Vector3.one);
                Matrix4x4 prefabLocal = Matrix4x4.TRS(
                    new Vector3(mapping.offsetX, mapping.offsetY, mapping.offsetZ),
                    Quaternion.Euler(mapping.rotationOffset),
                    Vector3.one
                );
                Matrix4x4 newSharedMat = pinMat * prefabLocal;
                _sharedInstance.transform.SetPositionAndRotation(newSharedMat.GetColumn(3), newSharedMat.rotation);
                
                UpdateAllMappingsFromCurrentTransforms();
            }
            else if (!useSpacePins)
            {
                if (lastDetectedMarkerPoses.TryGetValue(lastSeenArucoID, out Pose markerSpongyPose))
                {
                    Matrix4x4 spongyMat = Matrix4x4.TRS(markerSpongyPose.position, markerSpongyPose.rotation, Vector3.one);
                    Matrix4x4 prefabLocal = Matrix4x4.TRS(
                        new Vector3(mapping.offsetX, mapping.offsetY, mapping.offsetZ),
                        Quaternion.Euler(mapping.rotationOffset),
                        Vector3.one
                    );
                    Matrix4x4 newSharedMat = spongyMat * prefabLocal;
                    _sharedInstance.transform.SetPositionAndRotation(newSharedMat.GetColumn(3), newSharedMat.rotation);
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
        if (!showAlignmentInfoText || alignmentInfoTextMesh == null || lastSeenArucoID == INVALID_ARUCO_ID)
        {
            if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
            return;
        }
        if (!lastDetectedMarkerPoses.TryGetValue(lastSeenArucoID, out Pose markerSpongyPose))
        {
            if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
            return;
        }

        // We want to show text in world space, so we convert spongy back to locked/world for rendering.
        Pose markerWorldPose = markerSpongyPose;
        if (WorldLockingManager.GetInstance() != null)
        {
            markerWorldPose = WorldLockingManager.GetInstance().LockedFromSpongy.Multiply(markerSpongyPose);
        }

        Vector3 textPos = markerWorldPose.position + Vector3.up * textHeightAboveMarker;
        alignmentInfoTextObj.transform.position = textPos;

        if (Camera.main != null)
        {
            Vector3 toCamera = Camera.main.transform.position - textPos;
            if (toCamera.sqrMagnitude > 0.0001f)
                alignmentInfoTextObj.transform.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
        }
        else alignmentInfoTextObj.transform.rotation = markerWorldPose.rotation;

        alignmentInfoTextObj.transform.localScale = Vector3.one * textWorldScale;

        var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == lastSeenArucoID);
        if (mapping == null)
        {
            if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
            return;
        }

        if (!_lockedThisAcquisition.Contains(lastSeenArucoID) && _firstSeen.TryGetValue(lastSeenArucoID, out float firstTime))
        {
            float remaining = Mathf.Max(0, requiredDwellSeconds - (Time.time - firstTime));
            alignmentInfoTextMesh.text = $"ArUco {lastSeenArucoID} - HOLD STILL\n" +
                                         $"Locking in: {remaining:F1}s";
        }
        else
        {
            alignmentInfoTextMesh.text = $"ArUco {lastSeenArucoID} (WLT SpacePin)\n" +
                $"Offset X:{mapping.offsetX:F3} Y:{mapping.offsetY:F3} Z:{mapping.offsetZ:F3}\n" +
                $"Rotation X:{mapping.rotationOffset.x:F1}° Y:{mapping.rotationOffset.y:F1}° Z:{mapping.rotationOffset.z:F1}°";
        }
        alignmentInfoTextObj.SetActive(true);
    }

    public void DestroyAll()
    {
        if (_sharedInstance != null) { Destroy(_sharedInstance); _sharedInstance = null; }
        foreach (var pin in _pinObjects.Values)
        {
            if (pin != null) Destroy(pin);
        }
        _pinObjects.Clear();
        _spacePins.Clear();
        lastDetectedMarkerPoses.Clear();
        _lockedMarkerPose.Clear();
        _lockedThisAcquisition.Clear();
        _samples.Clear();
        _lastSeen.Clear();
        _firstSeen.Clear();
        _userEditedRotation = false;
        lastSeenArucoID = INVALID_ARUCO_ID;
        anchoredArucoID = INVALID_ARUCO_ID;
        if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
        if (markerFeature != null) markerFeature.DestroyAllMarkerDetectors();
        hasInitializedDetector = false;
    }

    private void OnDestroy() => DestroyAll();

    private void PushSample(ulong id, Pose pose, float t)
    {
        if (!_samples.TryGetValue(id, out var q)) { q = new Queue<MarkerSample>(); _samples[id] = q; }
        q.Enqueue(new MarkerSample { pose = pose, t = t });
        while (q.Count > 240) q.Dequeue();
    }

    private int RecentSampleCount(ulong markerId) => _samples.TryGetValue(markerId, out var q) ? q.Count : 0;
    private float SecondsSinceSeen(ulong markerId) => _lastSeen.TryGetValue(markerId, out var t) ? Time.time - t : float.PositiveInfinity;

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
            _firstSeen.Remove(id);
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
