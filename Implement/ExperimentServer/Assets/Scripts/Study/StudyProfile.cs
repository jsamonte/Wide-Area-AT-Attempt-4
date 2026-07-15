using System.Collections.Generic;
using UnityEngine;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;
using ARCockpit.Core;
using ARCockpit.DevSettings;
using ARCockpit.Terrain;   // MarkerSizePreset (the shared id + printed length + DevMode row), defined with MapProfile

namespace ARCockpit.Study
{
    /// <summary>
    /// THE one place the study's own parameters live, mirroring MapProfile / NavBallProfile /
    /// FlightPlanProfile. It owns the values that decide HOW THE DATA IS COLLECTED: the gaze-to-AOI
    /// attribution rules, the recording gates, the logger's flush cadence, and the dev-server settings.
    ///
    /// Why this asset exists at all. Every one of these numbers used to be an Inspector field on a scene
    /// component in Systems.unity, which meant three bad things at once:
    ///
    ///   1. They were UNREACHABLE ON DEVICE. Changing the gaze dwell threshold meant opening Unity and
    ///      rebuilding the APK, which is exactly the loop the dev-window system exists to kill.
    ///   2. They were UNRECORDED. Nothing wrote them into the trial file, so two CSVs collected weeks apart
    ///      under different thresholds are not comparable, and nothing in the data says so. That is a
    ///      silent data-integrity hole, which is the worst kind.
    ///   3. The gaze hitbox padding lived once PER GazeTarget, so the map and the NavBall could silently
    ///      disagree about how generously a look counts as a look.
    ///
    /// Now they live here once, they are tunable from the 231 dev window on the headset, they ride
    /// Export / Save / Load like every other knob, and DataLogger stamps them into every trial's header so
    /// a file always says what rules produced it.
    ///
    /// Setup (once): Assets > Create > ARCockpit > Study Profile, put it in a Resources folder, name it
    /// exactly "StudyProfile". Every study script auto-loads it by that name, so nothing is dragged into a
    /// slot.
    ///
    /// A NOTE ON WHAT IS TUNABLE MID-SESSION. Some of these only take effect at startup (the eye tracker
    /// gates its data streams by the permissions held when it is created; the web server binds its port
    /// once). Those are marked "next launch" in their help text rather than hidden, because a value you
    /// cannot see is how you end up debugging the wrong thing.
    /// </summary>
    [CreateAssetMenu(menuName = "ARCockpit/Study Profile", fileName = "StudyProfile")]
    public class StudyProfile : ScriptableObject, IMarkerSource, IDevModeSource, IDevTunableSource
    {
        [Header("Gaze to AOI (HOW A LOOK IS COUNTED: these decide the data)")]
        [Tooltip("How far the gaze ray is cast, in meters. It only has to reach the elements in front of " +
                 "the participant, so this is generous.")]
        public float gazeMaxDistanceMeters = 10f;

        [Tooltip("Minimum gaze confidence the tracker must report for a sample to be attributed to an AOI " +
                 "at all. 0 accepts everything (including a blink), 2 is the tracker's high-confidence " +
                 "value. 1 is the working default: it rejects garbage without throwing away real looks.")]
        [Range(0, 2)] public int gazeMinConfidence = 1;

        [Tooltip("How long the participant must hold an AOI before it counts as CONFIRMED SEEN, in seconds. " +
                 "This is what the readiness check ('has this participant actually seen the NavBall?') is " +
                 "built on. Too low confirms a glance across it; too high never confirms.")]
        public float gazeDwellToConfirmSeconds = 0.5f;

        [Tooltip("Grow every AOI hitbox by this factor. Gaze carries real angular error (about a degree even " +
                 "when calibrated), so a hitbox exactly the size of the visual UNDER-counts looks at its " +
                 "edges. One value for the whole study, so the map and the NavBall are judged by the same " +
                 "rule: a per-element padding would silently bias the comparison between them.")]
        [Range(1f, 2f)] public float gazeHitboxPadding = 1.15f;

        [Tooltip("Seconds between [AOI] log lines. Logging only, it does not touch the recorded data.")]
        public float gazeLogIntervalSeconds = 3f;

        [Header("Session design (SessionPlan derives the whole trial order from these)")]
        [Tooltip("Is weather a FACTOR (a second independent variable) or a constant? ON gives the 2x2 " +
                 "design: 4 conditions (AR/no-AR x clear/rain), 4 blocks, ordered by a balanced Latin " +
                 "square. OFF holds the weather constant and gives 2 conditions (AR / no AR) in 2 blocks. " +
                 "CHANGING THIS CHANGES THE STUDY: it does not renumber a participant who has already " +
                 "started (their saved plan wins), so set it before you run anyone.")]
        public bool weatherIsAFactor = true;

        [Tooltip("Recorded trials per condition block. The 2x2 design at 5 trials a block is 20 recorded " +
                 "flights per participant, and at an 8-minute route that is over 2.5 hours of flying before " +
                 "breaks. Pick a number a human will actually sit through.")]
        [Range(1, 10)] public int trialsPerBlock = 5;

        [Tooltip("Unrecorded practice runs at the start of a session, flown in the participant's FIRST " +
                 "condition. They get a file (under trials/practice/) but never count toward the plan. " +
                 "Their job is to burn off the novelty effect, which otherwise lands entirely on trial 1.")]
        [Range(0, 3)] public int practiceTrials = 1;

        [Header("Trial gates (what a trial refuses to do)")]
        [Tooltip("Reposition the aircraft to the captured start pose when a trial starts. Needs a TrialReset " +
                 "in the loaded scenes AND a connected sim; with it on and either missing, Start is refused " +
                 "outright. Off records from wherever the aircraft currently is.")]
        public bool useTrialReset = false;

        [Tooltip("Refuse to record when the eye tracker is running but has NEVER reported high confidence, " +
                 "which means the participant was never calibrated. Uncalibrated eyes return a healthy-looking " +
                 "Success with confidence 0, so the file would look perfect and be worthless. LEAVE THIS ON " +
                 "for a real participant: it is the gate that stops a silently-dead trial.")]
        public bool requireCalibratedEyes = true;

        [Tooltip("Seconds a finished trial's readout stays on the dashboard before it clears.")]
        public float clearReadoutAfterSeconds = 6f;

        [Header("Logging")]
        [Tooltip("Flush the trial CSV to disk every this many rows. Lower loses less data if the app dies " +
                 "mid-trial; higher touches the filesystem less. At 60 rows and ~60 fps that is about a " +
                 "second of data at risk.")]
        public int flushEveryRows = 60;

        [Header("Eye tracker (takes effect NEXT LAUNCH)")]
        [Tooltip("Ask for pupil diameter. NEXT LAUNCH ONLY: the native tracker gates its data streams by the " +
                 "permissions it holds AT CREATION TIME, so this cannot be turned on mid-session. Off means " +
                 "no pupil data in the CSV.")]
        public bool requestPupilSize = true;

        [Tooltip("Seconds between [Eye] status log lines.")]
        public float eyeLogIntervalSeconds = 5f;

        [Header("Web dashboard + dev log")]
        [Tooltip("How often the main thread rebuilds the status blob the dashboard reads. The dashboard polls " +
                 "at 2 Hz, so there is nothing to gain above about 10.")]
        [Range(1f, 30f)] public float serverSnapshotHz = 10f;

        [Tooltip("Log every HTTP request the dashboard makes. Noisy; bring-up only.")]
        public bool serverLogRequests = false;

        [Tooltip("How many log lines the dev-mode ring buffer keeps in memory for the dashboard's log view. " +
                 "It is a RING: the oldest line is dropped when it is full, so this is a memory bound, not a " +
                 "limit on how long you can watch.")]
        [Range(100, 5000)] public int devLogBufferLines = 800;

        [Header("Markers (the 23x dev-tag block; the study window is 231)")]
        [Tooltip("Dictionary the study dev tag was generated from. Same 5x5_250 as every other tag, so it " +
                 "shares the existing detector pool.")]
        public ArucoType markerDictionary = ArucoType.Dictionary_5x5_250;

        [Tooltip("The study dev tag: 231, printed at 3 in (0.0762 m), which pops this tuning window. It sits " +
                 "in the 23x block next to the flight plan's 230 (the two profiles each map only their own id, " +
                 "so they never collide). The tag places no visual: it exists only to drive the window. The " +
                 "printed length must match the paper, or the pose distance is wrong.")]
        public MarkerSizePreset[] markerSizePresets = new MarkerSizePreset[]
        {
            new MarkerSizePreset { markerId = 231, markerLengthMeters = 0.0762f, mode = DevMode.Window },
        };

        // IMarkerSource: the MarkerAnchor reads the id / printed length / dictionary from here.
        public ArucoType MarkerDictionary => markerDictionary;
        public void CollectMarkers(List<MarkerSpec> into)
        {
            if (markerSizePresets == null) return;
            foreach (var p in markerSizePresets)
                if (p != null)
                    into.Add(new MarkerSpec { markerId = p.markerId, markerLengthMeters = p.markerLengthMeters });
        }

        // The window is a floating panel, not a placed element, so there is nothing to nudge off the tag.
        public Vector3 MarkerPositionOffset => Vector3.zero;
        public Vector3 MarkerEulerOffset => Vector3.zero;

        // IDevModeSource: which dev mode the sighted tag selects.
        public DevMode? FindDevMode(int markerId)
        {
            if (markerSizePresets == null) return null;
            foreach (var p in markerSizePresets)
                if (p != null && p.markerId == markerId) return p.mode;
            return null;
        }

        // IDevTunableSource: every knob above, bound straight to its field so the value still lives once.
        // Keys are stable: they are what Export / Save / Load and the CSV header write.
        public void CollectTunables(List<DevTunable> into)
        {
            into.Add(DevTunable.F("study.gazeMaxDistance", "Gaze reach (m)", 1f, 30f,
                () => gazeMaxDistanceMeters, v => gazeMaxDistanceMeters = v,
                "How far the gaze ray is cast. It only has to reach the elements in front of you.")
                .In("Gaze to AOI"));
            into.Add(DevTunable.I("study.gazeMinConfidence", "Min gaze confidence", 0, 2,
                () => gazeMinConfidence, v => gazeMinConfidence = v,
                "How sure the tracker must be before a look counts. 0 accepts everything including blinks, 2 accepts only high confidence. 1 is the working default. THIS CHANGES THE RECORDED DATA.")
                .In("Gaze to AOI"));
            into.Add(DevTunable.F("study.gazeDwellToConfirm", "Dwell to confirm seen (s)", 0.05f, 3f,
                () => gazeDwellToConfirmSeconds, v => gazeDwellToConfirmSeconds = v,
                "How long you must hold an element before it counts as CONFIRMED SEEN. Too low and a glance across it confirms; too high and it never does. THIS CHANGES THE RECORDED DATA.")
                .In("Gaze to AOI"));
            into.Add(DevTunable.F("study.gazeHitboxPadding", "Hitbox padding (x)", 1f, 2f,
                () => gazeHitboxPadding, v => gazeHitboxPadding = v,
                "Grows every AOI hitbox by this factor, because gaze carries about a degree of real error even when calibrated. One value for the whole study, so every element is judged by the same rule. THIS CHANGES THE RECORDED DATA.")
                .In("Gaze to AOI"));
            into.Add(DevTunable.F("study.gazeLogInterval", "[AOI] log interval (s)", 0.5f, 20f,
                () => gazeLogIntervalSeconds, v => gazeLogIntervalSeconds = v,
                "Seconds between [AOI] log lines. Logging only; it does not affect the recorded data.")
                .In("Gaze to AOI"));

            into.Add(DevTunable.B("study.weatherIsAFactor", "Weather is a factor", () => weatherIsAFactor, v => weatherIsAFactor = v,
                "ON: the 2x2 design (AR/no-AR x clear/rain), 4 blocks, balanced Latin square. OFF: weather held constant, 2 blocks. THIS IS THE STUDY DESIGN. A participant who has already started keeps their saved plan, so set this before you run anyone.")
                .In("Session design"));
            into.Add(DevTunable.I("study.trialsPerBlock", "Trials per block", 1, 10,
                () => trialsPerBlock, v => trialsPerBlock = v,
                "Recorded trials in each condition block. 4 blocks x 5 = 20 flights per participant, which at an 8-minute route is over 2.5 hours before breaks. Pick a number a human will sit through.")
                .In("Session design"));
            into.Add(DevTunable.I("study.practiceTrials", "Practice trials", 0, 3,
                () => practiceTrials, v => practiceTrials = v,
                "Unrecorded practice runs at the start of a session, flown in the participant's first condition. They are filed under trials/practice/ and never counted. They exist to burn off the novelty effect, which otherwise lands entirely on trial 1.")
                .In("Session design"));

            into.Add(DevTunable.B("study.useTrialReset", "Reposition on trial start", () => useTrialReset, v => useTrialReset = v,
                "Snap the aircraft to the captured start pose when a trial starts. Needs a TrialReset in the loaded scenes AND a connected sim: with this ON and either one missing, Start is REFUSED. Off records from wherever the aircraft is.")
                .In("Trial gates"));
            into.Add(DevTunable.B("study.requireCalibratedEyes", "Require calibrated eyes", () => requireCalibratedEyes, v => requireCalibratedEyes = v,
                "Refuse to record if the eye tracker has never reported high confidence, which means the participant was never calibrated. Uncalibrated eyes still return a healthy-looking Success, so the file would look perfect and be worthless. LEAVE THIS ON for a real participant.")
                .In("Trial gates"));
            into.Add(DevTunable.F("study.clearReadoutAfter", "Clear readout after (s)", 1f, 60f,
                () => clearReadoutAfterSeconds, v => clearReadoutAfterSeconds = v,
                "How long a finished trial's numbers stay on the dashboard before clearing.")
                .In("Trial gates"));

            into.Add(DevTunable.I("study.flushEveryRows", "CSV flush every (rows)", 1, 600,
                () => flushEveryRows, v => flushEveryRows = v,
                "How often the trial file is flushed to disk. Lower loses less if the app dies mid-trial; higher touches the filesystem less. 60 rows is about a second of data at risk.")
                .In("Logging"));

            into.Add(DevTunable.B("study.requestPupilSize", "Request pupil size", () => requestPupilSize, v => requestPupilSize = v,
                "Ask the tracker for pupil diameter. TAKES EFFECT NEXT LAUNCH ONLY: the native tracker locks its data streams to the permissions it holds when it is created, so this cannot be turned on mid-session.")
                .In("Eye tracker"));
            into.Add(DevTunable.F("study.eyeLogInterval", "[Eye] log interval (s)", 0.5f, 30f,
                () => eyeLogIntervalSeconds, v => eyeLogIntervalSeconds = v,
                "Seconds between [Eye] status log lines.")
                .In("Eye tracker"));

            into.Add(DevTunable.F("study.serverSnapshotHz", "Dashboard update rate (Hz)", 1f, 30f,
                () => serverSnapshotHz, v => serverSnapshotHz = v,
                "How often the headset rebuilds the status the dashboard reads. The dashboard polls at 2 Hz, so above about 10 you are just doing work nobody sees.")
                .In("Web dashboard"));
            into.Add(DevTunable.B("study.serverLogRequests", "Log HTTP requests", () => serverLogRequests, v => serverLogRequests = v,
                "Log every request the dashboard makes. Very noisy; bring-up only.")
                .In("Web dashboard"));
            into.Add(DevTunable.I("study.devLogBufferLines", "Dev log buffer (lines)", 100, 5000,
                () => devLogBufferLines, v => devLogBufferLines = v,
                "How many log lines are kept in memory for the dashboard's DEV log view. It is a ring buffer, so the oldest line drops when it fills: this bounds memory, it does not limit how long you can watch.")
                .In("Web dashboard"));
        }

        // Auto-loaded singleton: the asset named "StudyProfile" in any Resources folder, same as the others.
        // Falls back to an in-memory default with a warning so the study scripts still run before the asset
        // is created (the defaults here match what the scene components used to hold).
        private static StudyProfile _active;
        public static StudyProfile Active
        {
            get
            {
                if (_active == null) _active = Resources.Load<StudyProfile>("StudyProfile");
                if (_active == null)
                {
                    Debug.LogWarning("[StudyProfile] No 'StudyProfile' asset found in a Resources folder. " +
                                     "Create one: Assets > Create > ARCockpit > Study Profile, put it in " +
                                     "Assets/Resources, name it 'StudyProfile'. Using defaults for now.");
                    _active = CreateInstance<StudyProfile>();
                }
                return _active;
            }
        }
    }
}
