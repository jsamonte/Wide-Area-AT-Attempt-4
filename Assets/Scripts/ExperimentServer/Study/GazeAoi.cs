using System.Collections.Generic;
using UnityEngine;

namespace ARCockpit.Study
{
    /// <summary>
    /// Attributes the live gaze ray to whatever <see cref="GazeTarget"/> it lands on, and keeps the dwell
    /// bookkeeping. This is the measure the study actually turns on: not "where did the eyes point" but
    /// "which instrument was the pilot reading, and for how long". The AR-versus-physical comparison is a
    /// statement about these numbers.
    ///
    /// One of these lives beside the <see cref="EyeTracker"/> on the Bootstrap object. It raycasts once
    /// per frame, picks the nearest GazeTarget hit (ignoring every other collider in the scene), and
    /// writes <see cref="AoiData"/>. It is read-only with respect to everything else: it never moves,
    /// hides, or otherwise touches the elements it is measuring.
    ///
    ///     adb logcat -d -s Unity | Select-String "\[AOI\]"
    /// </summary>
    public class GazeAoi : MonoBehaviour
    {
        [Tooltip("Leave empty to use the auto-loaded Resources/StudyProfile.")]
        public StudyProfile profileOverride;

        // The attribution rules live on StudyProfile, not here. They decide HOW A LOOK IS COUNTED, so they
        // are study parameters, not scene dressing: on the profile they are tunable from the 231 window on
        // device and DataLogger stamps them into every trial's header, so a file always says what rules
        // produced it. As Inspector fields they were reachable only in the Editor and recorded nowhere.
        StudyProfile Profile => profileOverride != null ? profileOverride : StudyProfile.Active;
        float MaxDistance => Profile != null ? Profile.gazeMaxDistanceMeters : 10f;
        int MinConfidence => Profile != null ? Profile.gazeMinConfidence : 1;
        float DwellToConfirm => Profile != null ? Profile.gazeDwellToConfirmSeconds : 0.5f;
        float LogInterval => Profile != null ? Profile.gazeLogIntervalSeconds : 3f;

        readonly RaycastHit[] _hits = new RaycastHit[16];
        float _logTimer;

        void Update()
        {
            if (!EyeData.Tracking || !EyeData.GazeValid || EyeData.GazeConfidence < MinConfidence)
            {
                AoiData.Clear();
                Heartbeat();
                return;
            }

            GazeTarget nearest = null;
            Vector3 hitPoint = Vector3.zero;
            float nearestDistance = float.MaxValue;

            // RaycastAll rather than Raycast: the gaze ray can pass through terrain chunks or UI colliders
            // on its way to an element, and we care only about GazeTargets. Non-alloc, called every frame.
            int count = Physics.RaycastNonAlloc(EyeData.GazeOrigin, EyeData.GazeDirection.normalized,
                                                _hits, MaxDistance, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < count; i++)
            {
                var target = _hits[i].collider.GetComponent<GazeTarget>();
                if (target == null) continue;
                if (_hits[i].distance >= nearestDistance) continue;

                nearest = target;
                nearestDistance = _hits[i].distance;
                hitPoint = _hits[i].point;
            }

            AoiData.Set(nearest != null ? nearest.aoiKey : "", hitPoint, nearestDistance, Time.deltaTime, DwellToConfirm);
            Heartbeat();
        }

        void Heartbeat()
        {
            if (LogInterval <= 0f) return;
            _logTimer += Time.unscaledDeltaTime;
            if (_logTimer < LogInterval) return;
            _logTimer = 0f;

            string looking = string.IsNullOrEmpty(AoiData.Current) ? "(nothing)" : AoiData.Current;
            Debug.Log($"[AOI] looking at {looking} for {AoiData.DwellSeconds:F1} s " +
                      $"(gaze conf {EyeData.GazeConfidence}) | seen so far: {AoiData.SeenSummary()}");
        }
    }

    /// <summary>
    /// Current gaze attribution, written by <see cref="GazeAoi"/>. Same one-writer static-holder contract
    /// as EyeData and FlightData. The trial logger writes Current and DwellSeconds on every row; the web
    /// dashboard reads Confirmed to show the researcher that the participant has actually looked at each
    /// AR element (the per-participant "can you see it" check).
    /// </summary>
    public static class AoiData
    {
        /// <summary>AOI key currently under gaze, or empty for none.</summary>
        public static string Current = "";

        /// <summary>Seconds of continuous dwell on Current. Resets the moment gaze leaves.</summary>
        public static float DwellSeconds;

        /// <summary>World point where the gaze ray met the target, and its distance in meters.</summary>
        public static Vector3 HitPoint;
        public static float HitDistance;

        /// <summary>Total dwell per AOI since the last Reset, in seconds. The headline study measure.</summary>
        public static readonly Dictionary<string, float> TotalDwell = new Dictionary<string, float>();

        /// <summary>How many times gaze entered each AOI since the last Reset. Transitions matter as much
        /// as totals: lots of short looks at an instrument reads differently from one long one.</summary>
        public static readonly Dictionary<string, int> EntryCount = new Dictionary<string, int>();

        /// <summary>AOIs that have been dwelled on long enough to count as genuinely seen. This is what
        /// the dashboard's readiness check reads: it proves the participant can actually see each element
        /// where it is placed, before the trial starts.</summary>
        public static readonly HashSet<string> Confirmed = new HashSet<string>();

        internal static void Set(string key, Vector3 point, float distance, float deltaTime, float dwellToConfirm)
        {
            if (string.IsNullOrEmpty(key))
            {
                Current = "";
                DwellSeconds = 0f;
                return;
            }

            if (key != Current)
            {
                Current = key;
                DwellSeconds = 0f;
                EntryCount.TryGetValue(key, out int entries);
                EntryCount[key] = entries + 1;
            }

            DwellSeconds += deltaTime;
            HitPoint = point;
            HitDistance = distance;

            TotalDwell.TryGetValue(key, out float total);
            TotalDwell[key] = total + deltaTime;

            if (DwellSeconds >= dwellToConfirm) Confirmed.Add(key);
        }

        internal static void Clear()
        {
            Current = "";
            DwellSeconds = 0f;
        }

        /// <summary>Wipe the accumulators. The trial state machine calls this at the start of each trial,
        /// so the dwell totals belong to exactly one trial. Confirmed is kept: whether the participant can
        /// see the elements is a property of the setup, not of a trial.</summary>
        public static void ResetTotals()
        {
            TotalDwell.Clear();
            EntryCount.Clear();
        }

        /// <summary>Wipe the confirmed-seen set. Called when the PARTICIPANT changes, and only then.
        /// "Can they see the element" is a fact about one person's eyes and one placement of the elements;
        /// carrying it into the next participant would show a green light for someone who has never looked
        /// at the map. A readiness check that inherits the last person's answer is worse than none.</summary>
        public static void ClearConfirmed()
        {
            Confirmed.Clear();
        }

        public static string SeenSummary()
        {
            if (Confirmed.Count == 0) return "nothing yet";
            return string.Join(", ", Confirmed);
        }
    }
}
