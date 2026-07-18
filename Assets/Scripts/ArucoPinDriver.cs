using System;
using System.Collections.Generic;
using UnityEngine;
using Microsoft.MixedReality.WorldLocking.Core;

[RequireComponent(typeof(SpacePinOrientable))]
public class ArucoPinDriver : MonoBehaviour
{
    [Header("=== ArUco Configuration ===")]
    [Tooltip("The ID of the ArUco marker that will drive this SpacePin.")]
    public ulong arucoID;

    [Tooltip("If true, strips pitch/roll from the detected marker pose, keeping only heading (yaw). " +
             "Helpful if you trust the single marker's yaw but not its pitch/roll (e.g. wall marker).")]
    public bool useYawOnlyRotation = false;

    [Tooltip("If true, mathematically locks the perceived height of the marker so World Locking Tools will NEVER elevate or lower the floor.")]
    public bool lockElevation = true;

    [Tooltip("If true, mathematically locks the perceived height of the marker so World Locking Tools will NEVER tilt the floor when multiple markers are detected.")]
    public bool lockTilt = true;

    [Tooltip("CHECK THIS if the marker is flat on the floor! If true, it forces the Z-axis of the marker to point perfectly up at the ceiling, eliminating tilt without putting the prefab on its side.")]
    public bool isFloorMarker = true;

    [Header("=== Smoothing & Dwell ===")]
    [SerializeField] private float poseAverageSeconds = 0.25f;
    [SerializeField] private float requiredDwellSeconds = 2.0f;
    [SerializeField] private int minSamplesForRelocalize = 2;
    [SerializeField] private float visibleHoldSeconds = 0.75f;

    [Header("=== Gaze & Head Stillness ===")]
    [Tooltip("Camera used to test if the user is looking at the marker. Defaults to Camera.main.")]
    [SerializeField] private Camera headCamera;
    [SerializeField] private float maxGazeAngleDegrees = 25f;
    [SerializeField] private bool requireGazeAndHeadStillForDwell = false;
    [SerializeField] private float maxHeadSpeedMetersPerSecond = 0.20f;
    [SerializeField] private float maxHeadTurnDegreesPerSecond = 20f;

    [Header("=== Debug Info ===")]
    [SerializeField] private bool showAlignmentInfoText = true;
    [SerializeField] private float textHeightAboveMarker = 0.12f;
    [SerializeField] private Color textColor = Color.cyan;
    [SerializeField] private float textWorldScale = 0.022f;
    [SerializeField] private int textFontSize = 72;

    // References
    private SpacePinOrientable _spacePin;

    // Tracking state
    private struct MarkerSample { public Pose pose; public float t; }
    private Queue<MarkerSample> _samples = new Queue<MarkerSample>();
    private float _lastSeenTime = -1f;
    private float _firstSeenTime = -1f;
    private float _gazeStableSince = -1f;
    private Pose _smoothedPose;
    private bool _hasLockedThisSession = false;

    // Head state
    private Vector3 _lastHeadPos;
    private Quaternion _lastHeadRot;
    private bool _haveLastHead;

    // Debug text
    private GameObject _alignmentInfoTextObj;
    private TextMesh _alignmentInfoTextMesh;
    private bool _lastGazeOk;
    private bool _lastHeadStillOk;

    // Refinement State
    private float _lastLockTime = -1f;
    private Pose _lastLockedPose;

    private void Start()
    {
        _spacePin = GetComponent<SpacePinOrientable>();
        if (headCamera == null) headCamera = Camera.main;

        if (ArucoMarkerManager.Instance != null)
        {
            if (ArucoMarkerManager.Instance.SharedOrienter != null)
            {
                _spacePin.Orienter = ArucoMarkerManager.Instance.SharedOrienter;
            }
            ArucoMarkerManager.Instance.RegisterDriver(arucoID, this);
        }
        else
        {
            Debug.LogWarning($"[WLT v2] No ArucoMarkerManager found in scene to register ArUco {arucoID}.");
        }

        InitializeAlignmentText();
    }

    private void OnDestroy()
    {
        if (ArucoMarkerManager.Instance != null)
        {
            ArucoMarkerManager.Instance.UnregisterDriver(arucoID);
        }
    }

    public void ReceiveMarkerPose(Pose rawPose, float timestamp)
    {
        PushSample(rawPose, timestamp);
        _lastSeenTime = timestamp;
        if (_firstSeenTime < 0) _firstSeenTime = timestamp;
    }

    private void Update()
    {
        float now = Time.time;

        // 1. Evict stale samples
        if (now - _lastSeenTime > visibleHoldSeconds)
        {
            _samples.Clear();
            _firstSeenTime = -1f;
            _gazeStableSince = -1f;
            _hasLockedThisSession = false;
        }

        // 2. Head stillness check
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

        // 3. Process if we have samples
        if (_samples.Count >= minSamplesForRelocalize)
        {
            RefreshSmoothedPose(now);

            bool isFresh = (now - _lastSeenTime) <= 0.20f;
            bool hasDwelt = false;

            if (!requireGazeAndHeadStillForDwell)
            {
                // Simple time since first seen
                hasDwelt = _firstSeenTime > 0 && (now - _firstSeenTime >= requiredDwellSeconds);
                _lastGazeOk = true;
                _lastHeadStillOk = true;
            }
            else
            {
                // Gaze check
                bool gazeOk = true;
                if (headCamera != null)
                {
                    Vector3 toMarker = _smoothedPose.position - headCamera.transform.position; // Check against tracked position
                    gazeOk = toMarker.sqrMagnitude > 0.0001f &&
                             Vector3.Angle(headCamera.transform.forward, toMarker) <= maxGazeAngleDegrees;
                }
                _lastGazeOk = gazeOk;

                bool dwellConditionsHold = isFresh && gazeOk && headStillThisFrame;
                if (dwellConditionsHold)
                {
                    if (_gazeStableSince < 0) _gazeStableSince = now;
                }
                else
                {
                    _gazeStableSince = -1f;
                }

                hasDwelt = _gazeStableSince > 0 && (now - _gazeStableSince >= requiredDwellSeconds);
            }

            // 4. Update SpacePin
            if (isFresh && hasDwelt)
            {
                UpdateSpacePin(_smoothedPose, now);
            }
        }

        UpdateAlignmentInfoText();
    }

    private void UpdateSpacePin(Pose spongyPose, float now)
    {
        // Only update once per continuous visual acquisition to avoid 
        // destroying and recreating WLT spatial anchors continuously, 
        // which crashes the Magic Leap OpenXR runtime backend.
        if (!_hasLockedThisSession)
        {
            Pose poseToFeed = spongyPose;
            
            if (lockElevation || lockTilt)
            {
                var mgr = WorldLockingManager.GetInstance();
                // Find where the marker is EXPECTED to be in tracking space if the building is exactly at its current height/rotation
                Pose expectedSpongyPose = mgr.SpongyFromLocked.Multiply(_spacePin.ModelingPoseGlobal);

                // By feeding WLT exactly the Y coordinate it expects, we tell WLT "there is zero height error".
                // This guarantees WLT will not try to elevate the building, and also ensures multiple pins won't tilt the building.
                if (lockElevation || lockTilt)
                {
                    poseToFeed.position.y = expectedSpongyPose.position.y;
                }
            }

            if (useYawOnlyRotation || lockTilt)
            {
                poseToFeed.rotation = isFloorMarker ? FloorMarkerYawOnly(spongyPose.rotation) : YawOnly(spongyPose.rotation);
            }
            
            _spacePin.SetSpongyPose(poseToFeed);
            
            _lastLockedPose = spongyPose;
            _lastLockTime = now;
            _hasLockedThisSession = true;

            Debug.Log($"[WLT v2] Updated SpacePin for ArUco {arucoID}. Elevation Locked: {lockElevation}, Tilt Locked: {lockTilt}");
        }
    }

    // --- Smoothing Helpers ---

    private void PushSample(Pose pose, float t)
    {
        _samples.Enqueue(new MarkerSample { pose = pose, t = t });
        while (_samples.Count > 240) _samples.Dequeue();
    }

    private void RefreshSmoothedPose(float now)
    {
        float winCutoff = now - Mathf.Max(0.001f, poseAverageSeconds);
        while (_samples.Count > 0 && _samples.Peek().t < winCutoff) _samples.Dequeue();
        
        if (_samples.Count > 0)
        {
            _smoothedPose = AveragePose(_samples);
        }
    }

    private static Pose AveragePose(IEnumerable<MarkerSample> samples)
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

    private static Quaternion FloorMarkerYawOnly(Quaternion r)
    {
        // For a floor marker, the printed "top" of the marker (its Y axis) points along the floor.
        // Its normal (Z axis) points straight up into the ceiling.
        Vector3 markerTop = r * Vector3.up;
        markerTop.y = 0f; // Flatten to floor

        if (markerTop.sqrMagnitude < 1e-6f)
        {
            // If it was pointing straight up/down, fallback to its X axis (right)
            Vector3 markerRight = r * Vector3.right;
            markerRight.y = 0f;
            if (markerRight.sqrMagnitude < 1e-6f) return Quaternion.identity;
            markerTop = Vector3.Cross(markerRight.normalized, Vector3.up);
        }

        // We want a rotation where Forward (Z) is World UP, and Up (Y) is the markerTop vector.
        return Quaternion.LookRotation(Vector3.up, markerTop.normalized);
    }

    // --- Debug Text ---

    private void InitializeAlignmentText()
    {
        if (!showAlignmentInfoText) return;
        if (_alignmentInfoTextObj != null) return;
        _alignmentInfoTextObj = new GameObject($"ArucoInfo_{arucoID}");
        _alignmentInfoTextObj.transform.SetParent(null);
        _alignmentInfoTextMesh = _alignmentInfoTextObj.AddComponent<TextMesh>();
        _alignmentInfoTextMesh.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        _alignmentInfoTextMesh.fontSize = textFontSize;
        _alignmentInfoTextMesh.color = textColor;
        _alignmentInfoTextMesh.anchor = TextAnchor.MiddleCenter;
        _alignmentInfoTextMesh.alignment = TextAlignment.Center;
        _alignmentInfoTextObj.SetActive(false);
    }

    private void UpdateAlignmentInfoText()
    {
        if (!showAlignmentInfoText || _alignmentInfoTextMesh == null)
        {
            if (_alignmentInfoTextObj != null) _alignmentInfoTextObj.SetActive(false);
            return;
        }

        if (_samples.Count == 0 && _lastLockTime < 0)
        {
            _alignmentInfoTextObj.SetActive(false);
            return;
        }

        Pose markerWorldPose;
        if (_samples.Count > 0)
        {
            // Use currently tracking smoothed pose to show where it is right now
            markerWorldPose = _smoothedPose; 
            var wltMgr = WorldLockingManager.GetInstance();
            if (wltMgr != null)
                markerWorldPose = wltMgr.LockedFromSpongy.Multiply(_smoothedPose);
        }
        else
        {
            // Use locked pose if we aren't currently tracking
            markerWorldPose = _spacePin.LockedPose;
        }

        Vector3 textPos = markerWorldPose.position + Vector3.up * textHeightAboveMarker;
        _alignmentInfoTextObj.transform.position = textPos;

        if (Camera.main != null)
        {
            Vector3 toCamera = Camera.main.transform.position - textPos;
            if (toCamera.sqrMagnitude > 0.0001f)
                _alignmentInfoTextObj.transform.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
        }

        _alignmentInfoTextObj.transform.localScale = Vector3.one * textWorldScale;

        float now = Time.time;
        if (now - _lastSeenTime > visibleHoldSeconds)
        {
            // Not visible
            _alignmentInfoTextMesh.text = $"ArUco {arucoID} (Lost)\n" +
                (_spacePin.PinActive ? "✓ Pin ACTIVE" : "○ Pending");
        }
        else if (now - _lastLockTime > 1.0f || _lastLockTime < 0) // Meaning we are currently evaluating dwell
        {
            if (requireGazeAndHeadStillForDwell && _gazeStableSince > 0)
            {
                float remaining = Mathf.Max(0, requiredDwellSeconds - (now - _gazeStableSince));
                _alignmentInfoTextMesh.text = $"ArUco {arucoID} - HOLD STILL\nLocking in: {remaining:F1}s";
            }
            else if (requireGazeAndHeadStillForDwell && !_lastGazeOk)
            {
                _alignmentInfoTextMesh.text = $"ArUco {arucoID}\nLook directly at the marker";
            }
            else if (requireGazeAndHeadStillForDwell && !_lastHeadStillOk)
            {
                _alignmentInfoTextMesh.text = $"ArUco {arucoID}\nHold your head still";
            }
            else
            {
                // Simple dwell
                if (!requireGazeAndHeadStillForDwell && _firstSeenTime > 0)
                {
                    float remaining = Mathf.Max(0, requiredDwellSeconds - (now - _firstSeenTime));
                    _alignmentInfoTextMesh.text = $"ArUco {arucoID} - {remaining:F1}s";
                }
                else
                {
                    _alignmentInfoTextMesh.text = $"ArUco {arucoID}\nTracking...";
                }
            }
        }
        else
        {
            _alignmentInfoTextMesh.text = $"ArUco {arucoID}\n✓ Locked & Active";
        }
        
        _alignmentInfoTextObj.SetActive(true);
    }
}
