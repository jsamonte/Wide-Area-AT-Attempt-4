using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;

/// <summary>
/// ArUco-based room re-snap. Spatial anchors drift between sessions; the printed
/// ArUco markers (DICT_4X4_50, ids 0/1/2, 16.51 cm/6.5") give us a stable absolute
/// reference. When at least one saved marker is visible, the ArUco snap overrides
/// the spatial-anchor median snap.
///
/// Storage: per-space ArucoOffset entries on RoomAnchorCalibrationStore.SpaceCalib.
/// Each offset is the virtualTwin pose expressed in the marker's local frame at
/// save time, so reconstruction is `markerPose * offset` per visible marker, then
/// averaged across all currently-visible saved markers.
/// </summary>
public class ArucoCalibrationManager : MonoBehaviour
{
    [Header("Marker config (must match printed PNGs)")]
    [Tooltip("ArUco dictionary that the markers were generated from.")]
    public ArucoType arucoDictionary = ArucoType.Dictionary_4x4_50;
    [Tooltip("Physical edge length of the printed marker, in meters. 6.5\" = 0.1651 m.")]
    public float markerLengthMeters = 0.1651f;
    [Tooltip("Marker IDs that this app should react to. Markers with other IDs are ignored.")]
    public List<int> acceptedMarkerIds = new List<int> { 0, 1, 2 };

    [Header("Detection smoothing")]
    [Tooltip("Seconds to keep treating a marker as visible after the last detector hit. ML2's marker detector returns intermittently even when the marker is clearly in view; this debounces that flicker.")]
    public float visibleHoldSeconds = 0.75f;
    [Tooltip("Seconds of recent samples to average together for the reported marker pose. Higher = more stable but slower to react. 0 disables averaging.")]
    public float poseAverageSeconds = 0.6f;
    [Tooltip("Minimum number of recent samples required before a snap is considered valid. Prevents snapping on a single noisy frame.")]
    public int minSamplesForSnap = 6;

    [Header("Debug visualizer")]
    [Tooltip("Spawn a thin square plate + axis gizmo on every visible accepted marker.")]
    public bool showDebugVisualizer = true;
    [Tooltip("Color while the room has NOT yet been ArUco-snapped this localization.")]
    public Color colorBeforeSnap = new Color(1f, 0.25f, 0.25f, 0.55f);
    [Tooltip("Color after the ArUco snap has fired.")]
    public Color colorAfterSnap = new Color(0.25f, 1f, 0.4f, 0.55f);
    /// <summary>Set by CalibrationManager once an ArUco snap has been applied this localization.</summary>
    [System.NonSerialized] public bool snappedThisLoc;

    MagicLeapMarkerUnderstandingFeature _feature;
    MarkerDetector _detector;
    bool _ready;
    // ML2's MarkerPose is reported in XR Origin tracking space, NOT world space.
    // We have to push every sampled pose through this transform before storing/
    // using it, otherwise visualizers and the saved snap offsets are off by
    // exactly the XR Origin's transform.
    Transform _xrOriginXform;
    // Reusable yield instruction for DetectionLoop; allocated once to avoid GC.
    WaitForEndOfFrame _waitForEndOfFrame;

    /// <summary>
    /// Snapshot of currently-visible accepted markers (id -> world pose) refreshed each Update.
    /// </summary>
    public Dictionary<int, Pose> VisibleMarkers { get; private set; } = new Dictionary<int, Pose>();

    void Start()
    {
        _feature = OpenXRSettings.Instance != null
            ? OpenXRSettings.Instance.GetFeature<MagicLeapMarkerUnderstandingFeature>()
            : null;
        if (_feature == null || !_feature.enabled)
        {
            Debug.LogWarning("[Console] ARUCO: MagicLeapMarkerUnderstandingFeature not enabled. ArUco snap disabled.");
            return;
        }

        var settings = new MarkerDetectorSettings
        {
            MarkerType = MarkerType.Aruco,
            MarkerDetectorProfile = MarkerDetectorProfile.Default,
            ArucoSettings = new ArucoSettings
            {
                ArucoType = arucoDictionary,
                ArucoLength = markerLengthMeters,
                EstimateArucoLength = false,
            },
        };
        _detector = _feature.CreateMarkerDetector(settings);
        if (_detector == null)
        {
            Debug.LogError("[Console] ARUCO: CreateMarkerDetector failed.");
            return;
        }
        _ready = true;
        _waitForEndOfFrame = new WaitForEndOfFrame();
        StartCoroutine(DetectionLoop());
        Debug.LogWarning($"[Console] ARUCO: detector created. dict={arucoDictionary} length={markerLengthMeters:F4}m accepted=[{string.Join(",", acceptedMarkerIds)}]");
    }

    void OnDestroy()
    {
        if (_feature != null && _detector != null)
        {
            _feature.DestroyMarkerDetector(_detector);
        }
        _detector = null;
        _ready = false;
        ClearVisualizers();
    }

    void Update()
    {
        if (!_ready || _feature == null || _detector == null) return;
        // UpdateMarkerDetectors() and raw sample collection have been moved to
        // DetectionLoop() (WaitForEndOfFrame coroutine). That coroutine fires
        // after xrEndFrame so any ML2 pipeline stall can't delay frame submission
        // and can't cause the "camera stuck" display freeze.
        float now = Time.time;

        // Evict markers we haven't seen in a while. ML2's detector returns intermittently
        // (often ~1 Hz) even when the marker is in clear view, so we hold the last pose
        // for `visibleHoldSeconds` to avoid flickering the HUD/visualizer/snap state.
        float visCutoff = now - Mathf.Max(0f, visibleHoldSeconds);
        if (_evictBuf == null) _evictBuf = new List<int>();
        _evictBuf.Clear();
        foreach (var kv in _lastSeen) if (kv.Value < visCutoff) _evictBuf.Add(kv.Key);
        for (int i = 0; i < _evictBuf.Count; i++)
        {
            int id = _evictBuf[i];
            _lastSeen.Remove(id);
            _samples.Remove(id);
            VisibleMarkers.Remove(id);
        }

        // Refresh VisibleMarkers from the smoothed average of recent samples.
        float winCutoff = now - Mathf.Max(0.001f, poseAverageSeconds);
        foreach (var kv in _samples)
        {
            int id = kv.Key;
            var buf = kv.Value;
            // Drop samples older than the averaging window.
            while (buf.Count > 0 && buf.Peek().t < winCutoff) buf.Dequeue();
            if (buf.Count == 0) continue;
            VisibleMarkers[id] = AveragePose(buf);
        }

        UpdateVisualizers();
    }

    /// <summary>
    /// Runs UpdateMarkerDetectors() and raw sample collection in a WaitForEndOfFrame
    /// coroutine so the blocking ML2 perception-pipeline sync fires after xrEndFrame.
    /// Any stall here cannot delay the current frame's submission to the compositor
    /// and therefore cannot cause the "camera stuck" display freeze.
    /// VisibleMarkers is updated from these samples on the NEXT Update(), introducing
    /// at most one frame of latency -- acceptable for calibration snap logic.
    /// </summary>
    private IEnumerator DetectionLoop()
    {
        while (true)
        {
            yield return _waitForEndOfFrame;
            if (!_ready || _feature == null || _detector == null) continue;
            _feature.UpdateMarkerDetectors();
            float now = Time.time;
            if (_detector.Status == MarkerDetectorStatus.Ready)
            {
                var data = _detector.Data;
                if (data != null)
                {
                    for (int i = 0; i < data.Count; i++)
                    {
                        var d = data[i];
                        if (!d.MarkerNumber.HasValue) continue;
                        if (!d.MarkerPose.HasValue) continue;
                        int id = (int)d.MarkerNumber.Value;
                        if (acceptedMarkerIds != null && acceptedMarkerIds.Count > 0 && !acceptedMarkerIds.Contains(id)) continue;
                        // Convert tracking-space marker pose -> world space.
                        Pose worldPose = ToWorld(d.MarkerPose.Value);
                        PushSample(id, worldPose, now);
                        _lastSeen[id] = now;
                    }
                }
            }
        }
    }

    /// <summary>Number of samples currently in the averaging window for a given marker.</summary>
    public int RecentSampleCount(int markerId)
    {
        return _samples.TryGetValue(markerId, out var q) ? q.Count : 0;
    }

    /// <summary>
    /// Seconds since this marker was last *actually* detected by the ML2 detector.
    /// Returns float.PositiveInfinity if we've never seen it (or it has been evicted).
    /// VisibleMarkers stays populated for `visibleHoldSeconds` (default 0.75s) after
    /// the last detection to avoid HUD flicker, so callers that need true freshness
    /// (e.g. snap eligibility) should use this rather than VisibleMarkers.ContainsKey.
    /// </summary>
    public float SecondsSinceSeen(int markerId)
    {
        if (!_lastSeen.TryGetValue(markerId, out var t)) return float.PositiveInfinity;
        return Time.time - t;
    }

    struct Sample { public Pose p; public float t; }
    private readonly Dictionary<int, Queue<Sample>> _samples = new Dictionary<int, Queue<Sample>>();
    private readonly Dictionary<int, float> _lastSeen = new Dictionary<int, float>();
    private List<int> _evictBuf;

    void PushSample(int id, Pose p, float t)
    {
        if (!_samples.TryGetValue(id, out var q))
        {
            q = new Queue<Sample>();
            _samples[id] = q;
        }
        q.Enqueue(new Sample { p = p, t = t });
        // Hard cap so the queue can't grow unbounded if poseAverageSeconds is huge.
        while (q.Count > 240) q.Dequeue();
    }

    /// <summary>
    /// Convert a marker pose reported by Magic Leap (in XR Origin tracking space)
    /// into world space. The XR Origin's CameraOffset/Camera-Floor transform
    /// drifts the camera origin around relative to world; without this conversion
    /// our visualizers and saved offsets are wrong by that exact transform.
    /// </summary>
    Pose ToWorld(Pose tracking)
    {
        var x = GetXrOriginXform();
        if (x == null) return tracking;
        Vector3 wp = x.TransformPoint(tracking.position);
        Quaternion wr = x.rotation * tracking.rotation;
        return new Pose(wp, wr);
    }

    Transform GetXrOriginXform()
    {
        if (_xrOriginXform != null) return _xrOriginXform;
        var origin = FindObjectOfType<XROrigin>();
        if (origin == null) return null;
        // CameraFloorOffsetObject is the transform marker poses are relative to
        // when present (it's the actual tracking-space origin). Fall back to the
        // XROrigin transform itself if the offset object isn't set.
        _xrOriginXform = origin.CameraFloorOffsetObject != null
            ? origin.CameraFloorOffsetObject.transform
            : origin.transform;
        return _xrOriginXform;
    }

    static Pose AveragePose(Queue<Sample> samples)
    {
        Vector3 sumPos = Vector3.zero;
        Vector4 sumQ = Vector4.zero;
        Quaternion first = Quaternion.identity;
        bool haveFirst = false;
        int n = 0;
        foreach (var s in samples)
        {
            sumPos += s.p.position;
            Quaternion r = s.p.rotation;
            if (!haveFirst) { first = r; haveFirst = true; }
            if (Quaternion.Dot(first, r) < 0f) r = new Quaternion(-r.x, -r.y, -r.z, -r.w);
            sumQ += new Vector4(r.x, r.y, r.z, r.w);
            n++;
        }
        if (n == 0) return new Pose(Vector3.zero, Quaternion.identity);
        sumQ.Normalize();
        return new Pose(sumPos / n, new Quaternion(sumQ.x, sumQ.y, sumQ.z, sumQ.w));
    }

    // ------------------------------------------------------------------
    // Debug visualizer: thin square plate + 3-axis gizmo on each visible marker.
    // Always on in the calibration scene (this MonoBehaviour only lives there).
    // ------------------------------------------------------------------
    private readonly Dictionary<int, GameObject> _vizRoots = new Dictionary<int, GameObject>();

    void UpdateVisualizers()
    {
        // Default true in the calibration scene; CalibrationManager.EnterLiveSnapMode
        // flips this off when the study starts, and StudyManager flips it back on
        // for as long as the debug HUD is visible.
        if (!showDebugVisualizer)
        {
            if (_vizRoots.Count > 0) ClearVisualizers();
            return;
        }
        Color tint = snappedThisLoc ? colorAfterSnap : colorBeforeSnap;

        // Hide visualizers for markers no longer visible.
        var stale = new List<int>();
        foreach (var kv in _vizRoots)
            if (!VisibleMarkers.ContainsKey(kv.Key)) stale.Add(kv.Key);
        foreach (var id in stale) { Destroy(_vizRoots[id]); _vizRoots.Remove(id); }

        foreach (var kv in VisibleMarkers)
        {
            int id = kv.Key;
            Pose pose = kv.Value;
            if (!_vizRoots.TryGetValue(id, out var root) || root == null)
            {
                root = BuildVisualizer(id);
                _vizRoots[id] = root;
                Debug.LogWarning($"[Console] ARUCO-VIZ: built visualizer for marker id={id} at world pos={pose.position} (size={markerLengthMeters:F4}m)");
            }
            root.transform.SetPositionAndRotation(pose.position, pose.rotation);
            ApplyTint(root, tint);
        }
    }

    // Cached materials so we don't load them every frame.
    static Material _matRed, _matGreen, _matBlue;
    static bool _matsAttempted;
    static void EnsureMaterialsLoaded()
    {
        if (_matsAttempted) return;
        _matsAttempted = true;
        _matRed   = Resources.Load<Material>("AnchorMarkerMaterials/Red");
        _matGreen = Resources.Load<Material>("AnchorMarkerMaterials/Green");
        _matBlue  = Resources.Load<Material>("AnchorMarkerMaterials/Blue");
        Debug.LogWarning($"[Console] ARUCO-VIZ: materials loaded red={_matRed!=null} green={_matGreen!=null} blue={_matBlue!=null}");
    }

    GameObject BuildVisualizer(int id)
    {
        EnsureMaterialsLoaded();
        var root = new GameObject($"ArucoViz_id{id}");

        // Wire-square outline sized exactly to the printed marker. 4 thin edge
        // cubes around the perimeter, in the marker's local XY plane (+Z faces
        // the camera). If the printed marker fits inside this square exactly,
        // the detected pose + size are correct.
        float edge  = markerLengthMeters;
        float thick = markerLengthMeters * 0.04f;
        BuildEdge(root.transform, "edgeT", new Vector3(0,  edge * 0.5f, 0), new Vector3(edge,  thick, thick), _matRed);
        BuildEdge(root.transform, "edgeB", new Vector3(0, -edge * 0.5f, 0), new Vector3(edge,  thick, thick), _matRed);
        BuildEdge(root.transform, "edgeL", new Vector3(-edge * 0.5f, 0, 0), new Vector3(thick, edge,  thick), _matRed);
        BuildEdge(root.transform, "edgeR", new Vector3( edge * 0.5f, 0, 0), new Vector3(thick, edge,  thick), _matRed);

        // Axes: red=X, green=Y, blue=Z. Each is a thin elongated cube.
        BuildAxis(root.transform, "axisX", Vector3.right,   _matRed);
        BuildAxis(root.transform, "axisY", Vector3.up,      _matGreen);
        BuildAxis(root.transform, "axisZ", Vector3.forward, _matBlue);

        return root;
    }

    void BuildEdge(Transform parent, string name, Vector3 localPos, Vector3 localScale, Material mat)
    {
        var e = GameObject.CreatePrimitive(PrimitiveType.Cube);
        e.name = name;
        var c = e.GetComponent<Collider>(); if (c) Destroy(c);
        e.transform.SetParent(parent, false);
        e.transform.localPosition = localPos;
        e.transform.localScale = localScale;
        var mr = e.GetComponent<MeshRenderer>();
        if (mr != null && mat != null) mr.sharedMaterial = mat;
    }

    void BuildAxis(Transform parent, string name, Vector3 dir, Material mat)
    {
        var ax = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ax.name = name;
        var col = ax.GetComponent<Collider>(); if (col) Destroy(col);
        ax.transform.SetParent(parent, false);
        float len = markerLengthMeters * 0.6f;
        float thick = markerLengthMeters * 0.04f;
        // Scale so the cube extends `len` along `dir` from origin and `thick` on the other axes.
        Vector3 scale = new Vector3(thick, thick, thick);
        if (dir == Vector3.right) scale.x = len;
        else if (dir == Vector3.up) scale.y = len;
        else scale.z = len;
        ax.transform.localScale = scale;
        ax.transform.localPosition = dir * (len * 0.5f);

        var mr = ax.GetComponent<MeshRenderer>();
        if (mr != null && mat != null) mr.sharedMaterial = mat;
    }

    void ApplyTint(GameObject root, Color tint)
    {
        // No-op now that we use the proven shared Resources materials. The
        // colorBeforeSnap / colorAfterSnap fields on this component are kept
        // for backward compat but visual snap status is shown by the HUD.
        // (Tinting a sharedMaterial would mutate the asset for every user.)
    }

    void ClearVisualizers()
    {
        foreach (var kv in _vizRoots) if (kv.Value != null) Destroy(kv.Value);
        _vizRoots.Clear();
    }

    /// <summary>
    /// True if any currently-visible marker has a saved offset for this space
    /// AND has accumulated enough recent samples to be considered stable.
    /// </summary>
    public bool HasVisibleSavedMarker(RoomAnchorCalibrationStore.SpaceCalib calib)
    {
        if (calib == null || calib.arucoOffsets == null || calib.arucoOffsets.Count == 0) return false;
        if (VisibleMarkers.Count == 0) return false;
        foreach (var off in calib.arucoOffsets)
        {
            if (!VisibleMarkers.ContainsKey(off.markerId)) continue;
            if (RecentSampleCount(off.markerId) < Mathf.Max(1, minSamplesForSnap)) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Compute the room snap pose by averaging contributions from every currently-
    /// visible marker that also has a saved offset. Returns false if none match.
    /// </summary>
    public bool TryGetArucoSnapPose(RoomAnchorCalibrationStore.SpaceCalib calib, out Vector3 pos, out Quaternion rot, out int matched)
    {
        return TryGetArucoSnapPose(calib, null, out pos, out rot, out matched);
    }

    /// <summary>
    /// Same as <see cref="TryGetArucoSnapPose(RoomAnchorCalibrationStore.SpaceCalib, out Vector3, out Quaternion, out int)"/>
    /// but restricted to a specific set of marker IDs. Pass null to allow all.
    /// </summary>
    public bool TryGetArucoSnapPose(RoomAnchorCalibrationStore.SpaceCalib calib, HashSet<int> allowedIds, out Vector3 pos, out Quaternion rot, out int matched)
    {
        pos = Vector3.zero; rot = Quaternion.identity; matched = 0;
        if (calib == null || calib.arucoOffsets == null) return false;

        Vector3 sumPos = Vector3.zero;
        // Average rotations as a running normalized lerp; for 1-3 markers in a room
        // this is fine. (Quaternion averaging via summed components + Normalize.)
        Vector4 sumQ = Vector4.zero;
        Quaternion first = Quaternion.identity;
        bool haveFirst = false;

        foreach (var off in calib.arucoOffsets)
        {
            if (allowedIds != null && !allowedIds.Contains(off.markerId)) continue;
            if (!VisibleMarkers.TryGetValue(off.markerId, out var markerPose)) continue;
            // worldTwin = markerPose * twinLocalPose
            Vector3 wp = markerPose.position + markerPose.rotation * off.twinLocalPos;
            Quaternion wr = markerPose.rotation * off.twinLocalRot;
            sumPos += wp;
            if (!haveFirst) { first = wr; haveFirst = true; }
            // Flip to same hemisphere as `first` to avoid cancelling on average.
            if (Quaternion.Dot(first, wr) < 0f) wr = new Quaternion(-wr.x, -wr.y, -wr.z, -wr.w);
            sumQ += new Vector4(wr.x, wr.y, wr.z, wr.w);
            matched++;
        }
        if (matched == 0) return false;
        pos = sumPos / matched;
        sumQ.Normalize();
        rot = new Quaternion(sumQ.x, sumQ.y, sumQ.z, sumQ.w);
        return true;
    }

    /// <summary>
    /// Capture a fresh set of ArUco offsets from every currently-visible accepted
    /// marker, replacing any prior entries for those same marker IDs. Offsets for
    /// markers not currently visible are preserved.
    /// </summary>
    public int CaptureOffsetsFromVisible(RoomAnchorCalibrationStore.SpaceCalib calib, Pose twinWorldPose)
    {
        if (calib == null) return 0;
        if (calib.arucoOffsets == null) calib.arucoOffsets = new List<RoomAnchorCalibrationStore.ArucoOffset>();
        if (VisibleMarkers.Count == 0) return 0;

        // Force the twin we save to be world-upright. The room/floor is flat,
        // so the twin's pitch/roll has no physical meaning and any non-zero
        // pitch/roll here is just ML2 marker-orientation noise leaking in.
        Quaternion twinYaw = YawOnly(twinWorldPose.rotation);

        int written = 0;
        foreach (var kv in VisibleMarkers)
        {
            int id = kv.Key;
            Pose mp = kv.Value;
            // Use yaw-only marker rotation as the local frame for the offset.
            // ArUco's reported pitch/roll on a small printed square is noisy
            // (sub-pixel corner errors -> degrees of tilt around in-plane axes),
            // and that noise differs every time the marker is re-detected. By
            // working in the marker's yaw-only frame on BOTH save and replay,
            // we keep what ArUco is good at (position + heading) and discard
            // what it's bad at (tilt). This is what makes per-marker offsets
            // mutually consistent across re-localizations.
            Quaternion mpYaw = YawOnly(mp.rotation);
            Quaternion mpYawInv = Quaternion.Inverse(mpYaw);
            Vector3 localPos = mpYawInv * (twinWorldPose.position - mp.position);
            Quaternion localRot = mpYawInv * twinYaw;

            calib.arucoOffsets.RemoveAll(o => o.markerId == id);
            // Also capture marker-in-twin-local for the green ground-truth
            // visualizer. Using yaw-only twin rotation keeps the saved frame
            // consistent with how we reconstruct on load.
            Quaternion twinYawInv = Quaternion.Inverse(twinYaw);
            Vector3 markerLocalPos = twinYawInv * (mp.position - twinWorldPose.position);
            Quaternion markerLocalRot = twinYawInv * mp.rotation;
            calib.arucoOffsets.Add(new RoomAnchorCalibrationStore.ArucoOffset
            {
                markerId = id,
                twinLocalPos = localPos,
                twinLocalRot = localRot,
                markerInTwinLocalPos = markerLocalPos,
                markerInTwinLocalRot = markerLocalRot,
            });
            written++;
        }
        return written;
    }

    /// <summary>
    /// Project a rotation to its yaw-only (heading-around-world-up) component.
    /// Removes pitch and roll. Used for ArUco snap math because the room twin
    /// is upright by construction and ArUco's reported pitch/roll on a small
    /// marker is the dominant noise source.
    /// </summary>
    public static Quaternion YawOnly(Quaternion r)
    {
        Vector3 fwd = r * Vector3.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f)
        {
            // Marker is facing straight up/down (ceiling/floor). Fall back to
            // its right vector projected to horizontal so we still get a yaw.
            Vector3 right = r * Vector3.right;
            right.y = 0f;
            if (right.sqrMagnitude < 1e-6f) return Quaternion.identity;
            fwd = Vector3.Cross(Vector3.up, right.normalized);
        }
        return Quaternion.LookRotation(fwd.normalized, Vector3.up);
    }
}
