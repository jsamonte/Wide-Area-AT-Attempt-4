// Prototype8.cs
// Combines Prototype6's WLT GameObjects architecture with Prototype5's 
// Lock-on-Snap stability logic.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using MagicLeap.Android;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;
using Microsoft.MixedReality.WorldLocking.Core;
using Microsoft.MixedReality.WorldLocking.Tools;

public class Prototype8 : MonoBehaviour
{
    [Header("=== Building / Prefab (Kirby-style Pre-placed) ===")]
    [Tooltip("Prefab asset to instantiate if no pre-placed building is assigned below.")]
    [SerializeField] private GameObject sharedPrefab;

    [Tooltip("If assigned, this existing GameObject in the scene (the pre-placed building/prefab instance) is used. " +
             "Its current transform defines the Modeling space. Recommended for Kirby-style workflow.")]
    [SerializeField] private GameObject preplacedBuildingRoot;

    [Header("=== ARUCO -> VIRTUAL PIN MAPPINGS (Kirby-style) ===")]
    [Tooltip("For each ArUco ID, assign the GameObject inside (or associated with) the building prefab " +
             "whose transform represents the virtual location of that marker. " +
             "The script will attach SpacePinOrientable to it. This is the primary, recommended method.")]
    [SerializeField] private List<ArucoMapping> arucoMappings = new List<ArucoMapping>();

    [Serializable]
    public class ArucoMapping
    {
        public ulong arucoID;

        [Tooltip("The GameObject (usually a child of the building prefab) that marks the virtual position/orientation of this ArUco marker. " +
                 "Its transform at Start() becomes the ModelingPose. Highly recommended.")]
        public GameObject virtualPinGO;

        // --- Legacy / Fallback fields (used only if virtualPinGO is null) ---
        [Header("Legacy Fallback (only if virtualPinGO is empty)")]
        public float offsetX = 0f;
        public float offsetY = 0f;
        public float offsetZ = 0f;
        public Vector3 rotationOffset = new Vector3(270f, 0f, 0f);

        [Tooltip("If true, strips pitch/roll from the detected marker pose before feeding it to the SpacePin, keeping only heading (yaw). Ported from ArucoCalibrationManager.YawOnly. Recommended for wall/floor markers where ArUco's pitch/roll estimate is the dominant noise source and the building should stay upright regardless of marker tilt jitter. Leave off if you rely on WLT's multi-pin Orienter fit to average out rotation noise across several markers.")]
        public bool useYawOnlyRotation = false;
    }

    [Header("Global ArUco Detector Settings")]
    [SerializeField] private ArucoType arucoDictionary = ArucoType.Dictionary_5x5_250;
    [SerializeField] private float arucoPhysicalLengthMeters = 0.15f;
    [SerializeField] private bool estimateArucoLength = false;

    [Header("=== Detection Smoothing & Dwell ===")]
    [SerializeField] private float visibleHoldSeconds = 0.75f;
    [SerializeField] private float poseAverageSeconds = 0.25f;
    [SerializeField] private int minSamplesForRelocalize = 2;
    [SerializeField] private float freshLockSeconds = 0.20f;
    [Tooltip("Total time the user must keep looking at a marker, head held still, before it locks in.")]
    [SerializeField] private float requiredDwellSeconds = 2.0f;

    [Header("=== Gaze + Stillness Requirement ===")]
    [Tooltip("The camera used to test 'is the user looking at the marker'. Defaults to Camera.main if left empty.")]
    [SerializeField] private Camera headCamera;
    [Tooltip("Max angle (degrees) between where the user is looking and the marker's direction.")]
    [SerializeField] private float maxGazeAngleDegrees = 25f;

    [Tooltip("If true, requires the user to look at the marker AND hold head still for the full dwell time. " +
             "Set to FALSE (default) to get immediate Proto7-style behavior so alignment works right away.")]
    [SerializeField] private bool requireGazeAndHeadStillForDwell = false;

    [Tooltip("Max head translation speed (m/s) while dwelling (only used when requireGazeAndHeadStillForDwell = true).")]
    [SerializeField] private float maxHeadSpeedMetersPerSecond = 0.20f;
    [Tooltip("Max head rotation speed (deg/s) while dwelling (only used when requireGazeAndHeadStillForDwell = true).")]
    [SerializeField] private float maxHeadTurnDegreesPerSecond = 20f;

    [Header("=== Controller Offset Adjustment ===")]
    [SerializeField] private bool enableControllerAdjustment = true;
    [SerializeField] private float offsetAdjustSpeed = 0.8f;
    [SerializeField] private float rotationAdjustSpeed = 45f;
    [SerializeField] private float inputDeadzone = 0.12f;
    [SerializeField] private bool enableZAxisRotation = true;

    [Header("=== Debug Alignment Info Text ===")]
    [SerializeField] private bool showAlignmentInfoText = true;
    [SerializeField] private float textHeightAboveMarker = 0.12f;
    [SerializeField] private Color textColor = Color.cyan;
    [SerializeField] private float textWorldScale = 0.022f;
    [SerializeField] private int textFontSize = 72;

    [Header("=== PLUME / Misc ===")]
    [SerializeField] private GameObject emptyAnchorPrefab;

    // ------------------------------------------------------------------
    // Runtime state
    // ------------------------------------------------------------------
    private MagicLeapMarkerUnderstandingFeature markerFeature;
    private bool hasInitializedDetector = false;

    private const ulong INVALID_ARUCO_ID = ulong.MaxValue;
    private ulong lastSeenArucoID = INVALID_ARUCO_ID;

    private GameObject _sharedInstance;
    private Orienter _orienter;
    private Dictionary<ulong, GameObject> _pinObjects = new Dictionary<ulong, GameObject>();
    private Dictionary<ulong, SpacePinOrientable> _spacePins = new Dictionary<ulong, SpacePinOrientable>();
    private HashSet<ulong> _activatedPins = new HashSet<ulong>();
    private bool _buildingVisible = false;
    private bool _pinsReady = false;
    private bool _alreadyDestroyed = false;

    private Dictionary<ulong, Pose> lastDetectedMarkerPoses = new Dictionary<ulong, Pose>();
    private readonly Dictionary<ulong, Pose> _lockedMarkerPose = new Dictionary<ulong, Pose>();
    private readonly HashSet<ulong> _lockedThisAcquisition = new HashSet<ulong>();

    private readonly Dictionary<ulong, float> _firstSeen = new Dictionary<ulong, float>();
    private struct MarkerSample { public Pose pose; public float t; }
    private readonly Dictionary<ulong, Queue<MarkerSample>> _samples = new Dictionary<ulong, Queue<MarkerSample>>();
    private readonly Dictionary<ulong, float> _lastSeen = new Dictionary<ulong, float>();
    private List<ulong> _evictBuf;

    private readonly Dictionary<ulong, float> _gazeStableSince = new Dictionary<ulong, float>();
    private Vector3 _lastHeadPos;
    private Quaternion _lastHeadRot;
    private bool _haveLastHead;
    private bool _lastGazeOk;
    private bool _lastHeadStillOk;

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
            Debug.LogError("❌ Magic Leap Marker feature missing.");
            enabled = false;
            yield break;
        }

        // Register with the shared per-frame pump so UpdateMarkerDetectors() is
        // called exactly once per frame (at end-of-frame) across all scripts.
        MarkerDetectorPump.Instance.Register(markerFeature);

        if (headCamera == null) headCamera = Camera.main;

        Permissions.RequestPermission(Permissions.SpaceImportExport, OnSpacePermissionGranted, OnPermissionDenied);
        CreateMarkerDetector();
        InitializeAlignmentText();

        if (preplacedBuildingRoot != null)
        {
            _sharedInstance = preplacedBuildingRoot;
            Debug.Log("[WLT v2] Using PRE-PLACED building root from scene.");
        }
        else if (sharedPrefab != null)
        {
            _sharedInstance = Instantiate(sharedPrefab);
            Debug.Log("[WLT v2] Instantiated building from prefab.");
        }
        else
        {
            Debug.LogError("❌ No preplacedBuildingRoot and no sharedPrefab assigned.");
            enabled = false;
            yield break;
        }

        SetupGrabInteraction(_sharedInstance);

        var orienterObj = new GameObject("ArUcoOrienter");
        _orienter = orienterObj.AddComponent<Orienter>();

        foreach (var mapping in arucoMappings)
        {
            GameObject pinGO = mapping.virtualPinGO;

            if (pinGO == null)
            {
                Debug.LogWarning($"[WLT v2] No virtualPinGO for ArUco {mapping.arucoID}. Using legacy fallback.");
                Quaternion rot = Quaternion.Euler(mapping.rotationOffset);
                Vector3 virtualPos = -(rot * new Vector3(mapping.offsetX, mapping.offsetY, mapping.offsetZ));

                pinGO = emptyAnchorPrefab != null 
                    ? Instantiate(emptyAnchorPrefab, virtualPos, Quaternion.identity)
                    : new GameObject($"LegacyPin_{mapping.arucoID}");

                pinGO.transform.position = virtualPos;
                pinGO.transform.SetParent(_sharedInstance.transform, false);
            }

            if (pinGO != null)
            {
                SpacePinOrientable pin = pinGO.GetComponent<SpacePinOrientable>();
                if (pin == null) pin = pinGO.AddComponent<SpacePinOrientable>();

                pin.Orienter = _orienter;
                _spacePins[mapping.arucoID] = pin;
                _pinObjects[mapping.arucoID] = pinGO;

                Debug.Log($"[WLT v2] Wired SpacePinOrientable for ArUco {mapping.arucoID} to '{pinGO.name}'.");
            }
        }

        yield return null;
        _pinsReady = true;
        Debug.Log($"[WLT v2] Ready with {_spacePins.Count} pin(s).");
    }

    private bool AreSubsystemsLoaded()
    {
        return XRGeneralSettings.Instance?.Manager?.activeLoader != null;
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

    private void OnSpacePermissionGranted(string permission) { }
    private void OnPermissionDenied(string permission) { }

    // ------------------------------------------------------------------
    // Update
    // ------------------------------------------------------------------

    void Update()
    {
        if (markerFeature == null || markerFeature.MarkerDetectors.Count == 0) return;
        if (!_pinsReady || _sharedInstance == null) return;

        // UpdateMarkerDetectors() is handled once per frame by MarkerDetectorPump
        // (WaitForEndOfFrame coroutine) — do not call it here.
        float now = Time.time;

        // Collect detections
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

                PushSample(id, trackingPose, now);
                _lastSeen[id] = now;
                if (!_firstSeen.ContainsKey(id)) _firstSeen[id] = now;
            }
        }

        EvictStaleMarkers(now);
        RefreshSmoothedPoses(now);

        // Head stillness (simple per-frame, only used when toggle is true)
        bool headStillThisFrame = true;
        if (headCamera != null && Time.deltaTime > 0f)
        {
            Vector3 headPos = headCamera.transform.position;
            Quaternion headRot = headCamera.transform.rotation;
            if (_haveLastHead)
            {
                float speed = Vector3.Distance(headPos, _lastHeadPos) / Time.deltaTime;
                float turnRate = Quaternion.Angle(_lastHeadRot, headRot) / Time.deltaTime;
                headStillThisFrame = speed <= maxHeadSpeedMetersPerSecond && turnRate <= maxHeadTurnDegreesPerSecond;
            }
            _lastHeadPos = headPos;
            _lastHeadRot = headRot;
            _haveLastHead = true;
        }
        _lastHeadStillOk = headStillThisFrame;

        // Feed to pins
        foreach (var kvp in lastDetectedMarkerPoses)
        {
            ulong id = kvp.Key;
            Pose markerSpongyPose = kvp.Value;

            lastSeenArucoID = id;

            bool isFresh = SecondsSinceSeen(id) <= Mathf.Max(0.01f, freshLockSeconds);
            bool isStable = RecentSampleCount(id) >= Mathf.Max(1, minSamplesForRelocalize);

            bool hasDwelt;

            if (!requireGazeAndHeadStillForDwell)
            {
                // === DEFAULT: Proto7 behavior - simple time since first seen ===
                hasDwelt = _firstSeen.TryGetValue(id, out float firstTime) &&
                           (now - firstTime >= requiredDwellSeconds);
                _lastGazeOk = true;
                _lastHeadStillOk = true;
            }
            else
            {
                // === Advanced mode: gaze + head stillness required ===
                bool gazeOk = true;
                if (headCamera != null && _pinObjects.TryGetValue(id, out var pinGO) && pinGO != null)
                {
                    Vector3 toMarker = pinGO.transform.position - headCamera.transform.position;
                    gazeOk = toMarker.sqrMagnitude > 0.0001f &&
                             Vector3.Angle(headCamera.transform.forward, toMarker) <= maxGazeAngleDegrees;
                }
                _lastGazeOk = gazeOk;

                bool dwellConditionsHold = isFresh && isStable && gazeOk && headStillThisFrame;
                if (dwellConditionsHold)
                {
                    if (!_gazeStableSince.ContainsKey(id)) _gazeStableSince[id] = now;
                }
                else
                {
                    _gazeStableSince.Remove(id);
                }

                hasDwelt = _gazeStableSince.TryGetValue(id, out float stableSince) &&
                           (now - stableSince >= requiredDwellSeconds);
            }

            if (!isFresh || !isStable || !hasDwelt) continue;

            if (_lockedThisAcquisition.Contains(id)) continue;

            _lockedMarkerPose[id] = markerSpongyPose;
            _lockedThisAcquisition.Add(id);

            if (_spacePins.TryGetValue(id, out var pin))
            {
                var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == id);
                Pose poseToFeed = markerSpongyPose;
                if (mapping != null && mapping.useYawOnlyRotation)
                {
                    poseToFeed = new Pose(markerSpongyPose.position, YawOnly(markerSpongyPose.rotation));
                }
                pin.SetSpongyPose(poseToFeed);

                bool wasNew = _activatedPins.Add(id);
                if (wasNew)
                    Debug.Log($"[WLT v2] Fed ArUco {id} to its virtual pin GameObject.");
            }

            if (!_buildingVisible && _activatedPins.Count > 0)
            {
                if (!_sharedInstance.activeSelf) _sharedInstance.SetActive(true);
                _buildingVisible = true;
            }
        }

        UpdateAlignmentInfoText();

        if (!IsSharedInstanceGrabbed() && enableControllerAdjustment)
            HandleControllerOffsetAdjustment();
    }

    // ------------------------------------------------------------------
    // Grab / LateUpdate / Controller (unchanged)
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

        grab.selectEntered.AddListener(OnGrabEntered);
        grab.selectExited.AddListener(OnGrabReleased);
    }

    private void OnGrabEntered(SelectEnterEventArgs args) { }
    private void OnGrabReleased(SelectExitEventArgs args) { UpdateAlignmentInfoText(); }

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

        if (Mathf.Abs(axis.x) > inputDeadzone && enableZAxisRotation)
            _sharedInstance.transform.RotateAround(pivot, _sharedInstance.transform.forward, axis.x * rotSpeed);
    }

    private bool IsSharedInstanceGrabbed()
    {
        if (_sharedInstance == null) return false;
        var grab = _sharedInstance.GetComponent<XRGrabInteractable>();
        return grab != null && grab.isSelected;
    }

    private void HandleControllerOffsetAdjustment()
    {
        if (_sharedInstance == null || !_buildingVisible) return;

        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightHandDevices);
        InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, leftHandDevices);
        float speed = offsetAdjustSpeed * Time.deltaTime;

        if (rightHandDevices.Count > 0 && rightHandDevices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 rAxis))
        {
            Vector3 pos = _sharedInstance.transform.position;
            if (Mathf.Abs(rAxis.x) > inputDeadzone) pos.x += rAxis.x * speed;
            if (Mathf.Abs(rAxis.y) > inputDeadzone) pos.z += rAxis.y * speed;
            _sharedInstance.transform.position = pos;
        }

        if (leftHandDevices.Count > 0 && leftHandDevices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 lAxis))
        {
            if (Mathf.Abs(lAxis.y) > inputDeadzone)
            {
                Vector3 pos = _sharedInstance.transform.position;
                pos.y += lAxis.y * speed;
                _sharedInstance.transform.position = pos;
            }
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

        Pose markerSpongyPose;
        if (!_lockedMarkerPose.TryGetValue(lastSeenArucoID, out markerSpongyPose))
        {
            if (!lastDetectedMarkerPoses.TryGetValue(lastSeenArucoID, out markerSpongyPose))
            {
                if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
                return;
            }
        }

        Pose markerWorldPose = markerSpongyPose;
        var wltMgr = WorldLockingManager.GetInstance();
        if (wltMgr != null)
            markerWorldPose = wltMgr.LockedFromSpongy.Multiply(markerSpongyPose);

        Vector3 textPos = markerWorldPose.position + Vector3.up * textHeightAboveMarker;
        alignmentInfoTextObj.transform.position = textPos;

        if (Camera.main != null)
        {
            Vector3 toCamera = Camera.main.transform.position - textPos;
            if (toCamera.sqrMagnitude > 0.0001f)
                alignmentInfoTextObj.transform.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
        }
        else
        {
            alignmentInfoTextObj.transform.rotation = markerWorldPose.rotation;
        }

        alignmentInfoTextObj.transform.localScale = Vector3.one * textWorldScale;

        if (!_lockedThisAcquisition.Contains(lastSeenArucoID))
        {
            if (_gazeStableSince.TryGetValue(lastSeenArucoID, out float stableSince))
            {
                float remaining = Mathf.Max(0, requiredDwellSeconds - (Time.time - stableSince));
                alignmentInfoTextMesh.text = $"ArUco {lastSeenArucoID} - HOLD STILL\nLocking in: {remaining:F1}s";
            }
            else if (!_lastGazeOk)
            {
                alignmentInfoTextMesh.text = $"ArUco {lastSeenArucoID}\nLook directly at the marker";
            }
            else if (!_lastHeadStillOk)
            {
                alignmentInfoTextMesh.text = $"ArUco {lastSeenArucoID}\nHold your head still";
            }
            else
            {
                alignmentInfoTextMesh.text = $"ArUco {lastSeenArucoID}\nStarting...";
            }
        }
        else
        {
            alignmentInfoTextMesh.text = $"ArUco {lastSeenArucoID} (WLT Pin GO)\n" +
                $"Active Pins: {_activatedPins.Count} / {arucoMappings.Count}\n" +
                (_activatedPins.Contains(lastSeenArucoID) ? "✓ Pin ACTIVE — Prefab aligned" : "○ Pending");
        }
        alignmentInfoTextObj.SetActive(true);
    }

    // ------------------------------------------------------------------
    // Cleanup
    // ------------------------------------------------------------------

    public void DestroyAll()
    {
        if (_sharedInstance != null && preplacedBuildingRoot == null)
        {
            Destroy(_sharedInstance);
        }
        _sharedInstance = null;

        if (_orienter != null)
        {
            Destroy(_orienter.gameObject);
            _orienter = null;
        }

        _pinObjects.Clear();
        _spacePins.Clear();
        _activatedPins.Clear();
        lastDetectedMarkerPoses.Clear();
        _lockedMarkerPose.Clear();
        _lockedThisAcquisition.Clear();
        _samples.Clear();
        _lastSeen.Clear();
        _firstSeen.Clear();
        _gazeStableSince.Clear();
        _haveLastHead = false;

        _buildingVisible = false;
        _pinsReady = false;
        lastSeenArucoID = INVALID_ARUCO_ID;

        if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
        if (markerFeature != null && !_alreadyDestroyed) 
        {
            markerFeature.DestroyAllMarkerDetectors();
            _alreadyDestroyed = true;
            Debug.Log("[MarkerDet] destroyed");
        }
        else if (_alreadyDestroyed)
        {
            Debug.Log("[MarkerDet] already destroyed – skip");
        }
        hasInitializedDetector = false;
    }

    private void OnDestroy() => DestroyAll();

    // ------------------------------------------------------------------
    // Smoothing helpers
    // ------------------------------------------------------------------

    private void PushSample(ulong id, Pose pose, float t)
    {
        if (!_samples.TryGetValue(id, out var q)) { q = new Queue<MarkerSample>(); _samples[id] = q; }
        q.Enqueue(new MarkerSample { pose = pose, t = t });
        while (q.Count > 240) q.Dequeue();
    }

    private int RecentSampleCount(ulong markerId) => _samples.TryGetValue(markerId, out var q) ? q.Count : 0;
    private float SecondsSinceSeen(ulong markerId) => _lastSeen.TryGetValue(markerId, out var t) ? Time.time - t : float.PositiveInfinity;

    private static Quaternion YawOnly(Quaternion r)
    {
        Vector3 fwd = r * Vector3.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f)
        {
            Vector3 right = r * Vector3.right;
            right.y = 0f;
            if (right.sqrMagnitude < 1e-6f) return Quaternion.identity;
            fwd = Vector3.Cross(Vector3.up, right.normalized);
        }
        return Quaternion.LookRotation(fwd.normalized, Vector3.up);
    }

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
            _lockedMarkerPose.Remove(id);
            _lockedThisAcquisition.Remove(id);
            _firstSeen.Remove(id);
            _gazeStableSince.Remove(id);
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
