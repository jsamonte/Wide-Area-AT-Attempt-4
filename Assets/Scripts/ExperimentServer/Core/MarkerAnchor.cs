using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.XR.CoreUtils;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;

namespace ARCockpit.Core
{
    /// <summary>
    /// Places the object this is on at a printed ArUco marker, so you can position an AR element (the
    /// terrain map, the NavBall, anything) by holding the marker where you want it, then pull the marker
    /// away -- the element stays put. While the marker is visible the transform follows its smoothed
    /// pose; the moment the marker is lost, the last pose is held (no reset). It only acts on device,
    /// where the Magic Leap marker subsystem exists; in the Editor it does nothing and leaves the
    /// authored transform alone.
    ///
    /// Which IDs place this element (and their printed sizes and dictionary) come from the element's
    /// config asset via <see cref="IMarkerSource"/> (MapProfile / NavBallProfile): assign it to
    /// <see cref="markerProfile"/> and the profile is the single source of truth. The inline
    /// <see cref="markers"/> / <see cref="dictionary"/> are only a fallback used when no profile is set.
    ///
    /// Multi-size: a LIST of marker IDs, each with its own printed edge length, so one element can be
    /// placed by any of several differently sized tags (e.g. 210 = 3.5 in, 211 = 3 in, 212 = 2.5 in for
    /// the map's three scales). Whichever you show is the one that places it, and the id is published as
    /// <see cref="ActiveMarkerId"/> so another component (MovingMap, NavBallGlobe) can pick a scale from
    /// it. Pose is only ever averaged from one marker's samples, never blended across two.
    ///
    /// SHARED DETECTORS (critical): detectors and the once-per-frame UpdateMarkerDetectors() pump are
    /// STATIC and shared across every MarkerAnchor in the app. Two anchors that both want the same
    /// (dictionary, length) get the SAME detector, because the ML runtime does not reliably feed two
    /// detectors with identical settings (you get "Detected same marker at least twice" and one is
    /// starved), and calling UpdateMarkerDetectors() from several components per frame stalls the ML2
    /// perception pipeline. Each anchor still reads only its own IDs from the shared detectors.
    ///
    /// ArUco length is a per-detector setting that scales the reported distance, so each distinct printed
    /// size needs its own detector; the pool is keyed by (dictionary, length).
    ///
    /// Marker poses come back in XR Origin tracking space, so they are converted to world space through
    /// the rig's CameraFloorOffset before use.
    /// </summary>
    public class MarkerAnchor : MonoBehaviour
    {
        [System.Serializable]
        public struct AcceptedMarker
        {
            [Tooltip("ArUco marker ID to accept, e.g. 210. Other IDs are ignored.")]
            public int markerId;
            [Tooltip("Printed edge length in meters for THIS id. 3.5 in = 0.0889, 3 in = 0.0762, " +
                     "2.5 in = 0.0635.")]
            public float markerLengthMeters;
        }

        [Header("Marker source (single source of truth)")]
        [Tooltip("The element's config asset (MapProfile / NavBallProfile). When set, the accepted IDs, " +
                 "printed sizes, and dictionary are read from it and the fields below are ignored. Leave " +
                 "empty to use the inline fallback below.")]
        public ScriptableObject markerProfile;

        [Header("Fallback markers (used only when no marker source is assigned)")]
        [Tooltip("Dictionary the markers were generated from. IDs up to 249 need a 250-size dictionary " +
                 "(e.g. 5x5_250), not a 50-size one.")]
        public ArucoType dictionary = ArucoType.Dictionary_5x5_250;
        [Tooltip("Every tag that can place this element, each with its own printed size. Show any one of " +
                 "them to position the element; the one you show wins and its id is published as " +
                 "ActiveMarkerId.")]
        public AcceptedMarker[] markers = new AcceptedMarker[]
        {
            new AcceptedMarker { markerId = 210, markerLengthMeters = 0.0889f },
            new AcceptedMarker { markerId = 211, markerLengthMeters = 0.0762f },
            new AcceptedMarker { markerId = 212, markerLengthMeters = 0.0635f },
        };

        [Header("Offset (element relative to the marker)")]
        [Tooltip("Position offset from the marker, in meters, in the marker's frame (+Z out of the face).")]
        public Vector3 positionOffset = Vector3.zero;
        [Tooltip("Rotation offset (Euler) from the marker.")]
        public Vector3 eulerOffset = Vector3.zero;

        [Header("Stability")]
        [Tooltip("Seconds of recent samples averaged for a steady pose. Higher = smoother, slower.")]
        public float poseAverageSeconds = 0.5f;
        [Tooltip("Minimum stable samples before the first snap, so one noisy frame can't misplace it.")]
        public int minSamples = 6;

        /// <summary>
        /// The marker ID currently (or most recently) driving the held pose, -1 until one is seen. When
        /// the marker is pulled away the last id is held, matching the held pose. Read by MovingMap and
        /// NavBallGlobe to select a physical scale.
        /// </summary>
        public int ActiveMarkerId { get; private set; } = -1;

        /// <summary>True while a fresh marker pose is being applied THIS frame (the tag is in view now),
        /// false once the pose is only being held (tag pulled away). Lets a CockpitElement tell "I am
        /// being positioned by my dev tag" from "I should ride the cockpit anchor instead".</summary>
        public bool Tracking { get; private set; }

        private bool _ready;
        private Transform _originXform;
        private WaitForEndOfFrame _eof;

        // The shared detectors this instance reads from, each with the subset of OUR ids it should
        // report and the (dictionary, length) key used to release it on destroy.
        private class UsedDetector
        {
            public MarkerDetector detector;
            public HashSet<int> ids;
            public ArucoType dict;
            public float length;
        }
        private readonly List<UsedDetector> _used = new List<UsedDetector>();

        private struct Sample { public Pose pose; public float t; public int id; }
        private readonly Queue<Sample> _samples = new Queue<Sample>();

        // ----- Shared, app-wide detector pool + once-per-frame pump (see class summary) -----

        private static MagicLeapMarkerUnderstandingFeature s_feature;

        private struct DetectorKey : IEquatable<DetectorKey>
        {
            public ArucoType dict;
            public float length;
            public bool Equals(DetectorKey o) => dict == o.dict && length.Equals(o.length);
            public override bool Equals(object o) => o is DetectorKey k && Equals(k);
            public override int GetHashCode() => ((int)dict * 397) ^ length.GetHashCode();
        }

        private class PooledDetector { public MarkerDetector detector; public int refCount; }
        private static readonly Dictionary<DetectorKey, PooledDetector> s_pool =
            new Dictionary<DetectorKey, PooledDetector>();
        private static int s_lastPumpFrame = -1;

        void Start()
        {
            var feature = OpenXRSettings.Instance != null
                ? OpenXRSettings.Instance.GetFeature<MagicLeapMarkerUnderstandingFeature>()
                : null;
            if (feature == null || !feature.enabled)
            {
                // No marker subsystem (Editor, or feature disabled). Leave the authored transform alone.
                Debug.LogWarning("[MarkerAnchor] Marker Understanding feature not available; staying put.");
                return;
            }
            s_feature = feature;

            // Resolve which tags place this element: the profile if assigned, else the inline fallback.
            ArucoType dict;
            List<MarkerSpec> specs = ResolveMarkers(out dict);
            if (specs.Count == 0)
            {
                Debug.LogError("[MarkerAnchor] No markers configured (no profile and no inline markers).");
                return;
            }

            // Group the accepted ids by printed length: one shared detector per distinct length.
            var byLength = new Dictionary<float, HashSet<int>>();
            foreach (var m in specs)
            {
                if (!byLength.TryGetValue(m.markerLengthMeters, out var set))
                {
                    set = new HashSet<int>();
                    byLength[m.markerLengthMeters] = set;
                }
                set.Add(m.markerId);
            }

            foreach (var kv in byLength)
            {
                MarkerDetector detector = Acquire(feature, dict, kv.Key);
                if (detector == null)
                {
                    Debug.LogError($"[MarkerAnchor] Could not acquire detector for length {kv.Key:F4}m.");
                    continue;
                }
                _used.Add(new UsedDetector { detector = detector, ids = kv.Value, dict = dict, length = kv.Key });
            }

            if (_used.Count == 0)
            {
                Debug.LogError("[MarkerAnchor] No detectors available.");
                return;
            }

            _ready = true;
            _eof = new WaitForEndOfFrame();
            StartCoroutine(DetectionLoop());
            Debug.Log($"[MarkerAnchor] Detecting {specs.Count} marker(s) in {dict} across {_used.Count} " +
                      $"shared detector(s) (pool size {s_pool.Count}).");
        }

        void OnDestroy()
        {
            foreach (var u in _used) Release(u.dict, u.length);
            _used.Clear();
            _ready = false;
        }

        // Pull (dictionary, id, length) from the assigned IMarkerSource, or fall back to the inline list.
        private List<MarkerSpec> ResolveMarkers(out ArucoType dict)
        {
            var specs = new List<MarkerSpec>();
            if (markerProfile is IMarkerSource src)
            {
                dict = src.MarkerDictionary;
                src.CollectMarkers(specs);
                return specs;
            }

            dict = dictionary;
            if (markers != null)
                foreach (var m in markers)
                    specs.Add(new MarkerSpec { markerId = m.markerId, markerLengthMeters = m.markerLengthMeters });
            return specs;
        }

        // After xrEndFrame so a pipeline stall can't delay frame submission (the "camera stuck" freeze).
        private IEnumerator DetectionLoop()
        {
            while (true)
            {
                yield return _eof;
                if (!_ready) continue;

                Pump();   // shared: UpdateMarkerDetectors() runs once per frame regardless of anchor count

                float now = Time.time;
                foreach (var u in _used)
                {
                    if (u.detector.Status != MarkerDetectorStatus.Ready) continue;
                    var data = u.detector.Data;
                    if (data == null) continue;

                    for (int i = 0; i < data.Count; i++)
                    {
                        var d = data[i];
                        if (!d.MarkerNumber.HasValue || !d.MarkerPose.HasValue) continue;
                        int id = (int)d.MarkerNumber.Value;
                        if (!u.ids.Contains(id)) continue;   // only OUR ids off this shared detector
                        PushSample(ToWorld(d.MarkerPose.Value), now, id);
                    }
                }
            }
        }

        void Update()
        {
            // Drop samples older than the averaging window. If none remain the marker is not currently
            // visible -- hold the last applied pose (the whole point of "set it then pull the marker").
            float cutoff = Time.time - Mathf.Max(0.001f, poseAverageSeconds);
            while (_samples.Count > 0 && _samples.Peek().t < cutoff) _samples.Dequeue();
            if (_samples.Count == 0) { Tracking = false; return; }

            // Only ever average ONE marker. Pick the freshest sample's id and average just its samples,
            // so two tags briefly in view never blend into a bad pose.
            int activeId = NewestSampleId();
            int n = AveragePose(_samples, activeId, out Pose markerPose);
            if (n < Mathf.Max(1, minSamples)) { Tracking = false; return; }

            ActiveMarkerId = activeId;
            Tracking = true;

            Vector3 posOff = positionOffset;
            Vector3 eulOff = eulerOffset;
            if (markerProfile is IMarkerSource src)
            {
                posOff = src.MarkerPositionOffset;
                eulOff = src.MarkerEulerOffset;
            }

            // Place this element at the marker, plus the configured offset in the marker's frame.
            transform.SetPositionAndRotation(
                markerPose.position + markerPose.rotation * posOff,
                markerPose.rotation * Quaternion.Euler(eulOff));
        }

        // ----- shared pool helpers -----

        private static MarkerDetector Acquire(MagicLeapMarkerUnderstandingFeature feature, ArucoType dict, float length)
        {
            var key = new DetectorKey { dict = dict, length = length };
            if (!s_pool.TryGetValue(key, out var pooled))
            {
                var settings = new MarkerDetectorSettings
                {
                    MarkerType = MarkerType.Aruco,
                    MarkerDetectorProfile = MarkerDetectorProfile.Default,
                    ArucoSettings = new ArucoSettings
                    {
                        ArucoType = dict,
                        ArucoLength = length,
                        EstimateArucoLength = false,
                    },
                };
                var detector = feature.CreateMarkerDetector(settings);
                if (detector == null) return null;
                pooled = new PooledDetector { detector = detector, refCount = 0 };
                s_pool[key] = pooled;
            }
            pooled.refCount++;
            return pooled.detector;
        }

        private static void Release(ArucoType dict, float length)
        {
            var key = new DetectorKey { dict = dict, length = length };
            if (!s_pool.TryGetValue(key, out var pooled)) return;
            pooled.refCount--;
            if (pooled.refCount <= 0)
            {
                if (s_feature != null && pooled.detector != null)
                    s_feature.DestroyMarkerDetector(pooled.detector);
                s_pool.Remove(key);
            }
        }

        // One UpdateMarkerDetectors() call drives EVERY pooled detector; guard so it runs once per frame
        // even though each anchor's coroutine calls it.
        private static void Pump()
        {
            if (s_feature == null) return;
            if (s_lastPumpFrame == Time.frameCount) return;
            s_lastPumpFrame = Time.frameCount;
            s_feature.UpdateMarkerDetectors();
        }

        void PushSample(Pose worldPose, float t, int id)
        {
            _samples.Enqueue(new Sample { pose = worldPose, t = t, id = id });
            while (_samples.Count > 240) _samples.Dequeue();
        }

        int NewestSampleId()
        {
            int id = ActiveMarkerId;
            float newest = float.NegativeInfinity;
            foreach (var s in _samples)
                if (s.t >= newest) { newest = s.t; id = s.id; }
            return id;
        }

        // ML2 reports marker poses in XR Origin tracking space; convert to world via the rig's
        // CameraFloorOffset (or the origin itself if no offset object is set).
        Pose ToWorld(Pose tracking)
        {
            if (_originXform == null)
            {
                var origin = FindObjectOfType<XROrigin>();
                if (origin == null) return tracking;
                _originXform = origin.CameraFloorOffsetObject != null
                    ? origin.CameraFloorOffsetObject.transform
                    : origin.transform;
            }
            return new Pose(_originXform.TransformPoint(tracking.position), _originXform.rotation * tracking.rotation);
        }

        // Average only the samples matching id. Returns how many were averaged (0 if none).
        static int AveragePose(Queue<Sample> samples, int id, out Pose result)
        {
            Vector3 sumPos = Vector3.zero;
            Vector4 sumQ = Vector4.zero;
            Quaternion first = Quaternion.identity;
            bool haveFirst = false;
            int n = 0;
            foreach (var s in samples)
            {
                if (s.id != id) continue;
                sumPos += s.pose.position;
                Quaternion r = s.pose.rotation;
                if (!haveFirst) { first = r; haveFirst = true; }
                if (Quaternion.Dot(first, r) < 0f) r = new Quaternion(-r.x, -r.y, -r.z, -r.w);
                sumQ += new Vector4(r.x, r.y, r.z, r.w);
                n++;
            }
            if (n == 0) { result = Pose.identity; return 0; }
            sumQ.Normalize();
            result = new Pose(sumPos / n, new Quaternion(sumQ.x, sumQ.y, sumQ.z, sumQ.w));
            return n;
        }
    }
}
