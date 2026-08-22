using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Events;

public class EyeAndHeadTracker : MonoBehaviour
{
    public static EyeAndHeadTracker Instance { get; private set; }

    [Header("Session Configuration")]
    [SerializeField] private string participantId = "P002";
    [SerializeField] private string sessionId = "S001";
    [SerializeField] private string condition = "wideAreaAR_navigation_v1";
    [SerializeField] private string environment = "Unity_Wide_Area_AR";


    [Header("Eye Dwell Destruction")]
    [SerializeField] private string targetTag = "DwellDestroyTarget";
    [SerializeField] private LayerMask layersToIncludeWithRay;
    [SerializeField] [Range(1.0f, 10.0f)] private float minDwellTimeOverTarget = 4.0f;
    [SerializeField] private bool enableDwellDestroyFeature = true;
    [Tooltip("If true, casts an extra unmasked ray each frame to log exactly what the eye hits (name + layer). Off by default to save per-frame CPU/heat on Magic Leap.")]
    [SerializeField] private bool logUnmaskedEyeHit = false;

    [Header("Gaze Object Logging (targets, recall objects, any collider)")]
    [Tooltip("Records what the eye ray actually lands on — gems, recall objects, walls, props, anything with a collider — into the JSON summary and the raw NDJSON stream.")]
    [SerializeField] private bool enableGazeObjectLogging = true;
    [Tooltip("Which layers the gaze-logging ray may hit. Leave as Everything so untagged props and world geometry are captured too.")]
    [SerializeField] private LayerMask gazeLogLayers = ~0;
    [Tooltip("Maximum distance the gaze-logging ray travels, in metres.")]
    [SerializeField] private float gazeLogMaxDistance = 50f;
    [Tooltip("Sample the gaze-logging ray every N frames. 1 = every frame; raise it to cut CPU/heat on Magic Leap.")]
    [SerializeField] [Range(1, 10)] private int gazeLogSampleEveryNFrames = 1;
    [Tooltip("Looks shorter than this still count toward the dwell totals, but are not written out as individual look events.")]
    [SerializeField] private float minLookDurationToLogSeconds = 0.15f;
    [Tooltip("Stream every completed look event, as it happens, to its own append-only gaze_events_*.ndjson file. Independent of the raw per-frame stream. This is the crash-safe home for per-look data: lines are written once and never rewritten.")]
    [SerializeField] private bool logGazeEventsToNdjson = true;
    [Tooltip("ALSO embed the individual look events in the JSON summary. Off by default: the summary is rewritten in full on every autosave, so a growing event list there costs a main-thread hitch every few seconds. The aggregate percentages are always included either way.")]
    [SerializeField] private bool includeLookEventsInSummary = false;
    [Tooltip("Safety cap on how many individual look events are kept in memory for the JSON summary. Only applies when the option above is on; the aggregate dwell totals, percentages and the NDJSON event stream are unaffected by this cap.")]
    [SerializeField] private int maxStoredLookEvents = 1500;

    [Header("Data Quality")]
    [Tooltip("Trials whose valid-gaze percentage falls below this are flagged in the summary's dataQuality " +
             "notes. Set it to the SAME number as the exclusion criterion in your protocol, or the flag stops " +
             "meaning anything. 50 was calibrated on outdoor pilot data (31 Jul 2026: 62.1% and 70.4%); " +
             "indoor sessions run around 89%, so a lower bar is not laxness, it is what ambient IR outdoors " +
             "actually permits.")]
    [SerializeField] [Range(0f, 100f)] private float gazeValidityScreeningPercent = 50f;

    [Header("Logging")]
    [Tooltip("If true, starts recording immediately. If false, wait until ResumeRecording() is called.")]
    public bool recordOnAwake = true;
    private bool isRecording = false;
    private bool hasInitializedRecording = false;

    [Header("Events")]
    public UnityEvent OnAllTargetsDestroyed = new UnityEvent();

    [SerializeField] private bool useEfficientRawLogging = false;
    [SerializeField] private bool autoSaveWhenAllTargetsDestroyed = true;
    [SerializeField] [Tooltip("How often to silently save the JSON summary and flush the raw NDJSON buffer to disk (seconds). Balances crash-safety against IO frequency: a hard platform reboot fires neither OnApplicationQuit nor OnApplicationPause, but each target-destruction event is saved immediately anyway, so this only bounds raw per-frame pose loss.")]
    private float autoSaveIntervalSeconds = 10f;

    [Header("Optional Objects Tracking")]
    [SerializeField] private GameObject blueWireframeObject;
    [SerializeField] private GameObject mapGameObject;
    [SerializeField] private bool enableBlueWireframe = true;
    [SerializeField] private bool enableMap = true;

    // ==================== SERIALIZABLE CLASSES ====================

    [System.Serializable]
    public class SessionMeta
    {
        public string sessionId;
        public string participantId;
        public string condition;
        public string device = "Magic Leap 2";
        public string environment;

        // MEASURED from this trial's actual frame intervals, not a nominal figure. Gaze is sampled on the
        // render loop, so this is the rate the data was really captured at. This field used to be a hardcoded
        // 90 while the files it described were recorded at ~59 Hz.
        public float measuredSampleRateHz;
        public int targetFrameRateSetting;

        // What the per-eye fields in the raw stream actually contain. The Magic Leap OpenXR path in use here
        // (EyeTrackingUsages.gazePosition/gazeRotation) returns ONE combined gaze pose: there is no separate
        // left/right stream and no pupillometry. leftEye/rightEye are that combined pose offset by half an
        // assumed IPD, so they must not be analyzed as independent eyes, and pupilDiameterMm/openness are
        // written as -1 (unavailable) rather than as plausible-looking constants.
        public string eyeDataSource = "MagicLeap combined gaze; per-eye fields derived from it, no pupillometry";
    }

    [System.Serializable]
    public class TrackingFrame
    {
        public int frameId;
        public string timestamp;
        public float deltaTimeMs;
        public HeadTransformData headTransform;
        public EyeTrackingData eyeTracking;
        public string eyeRaycastHitObject;
        public string gazeHitCategory;
        public string gazeHitTag;
        public string gazeHitLayer;
        public float gazeHitDistanceMeters;
    }

    [System.Serializable] public class HeadTransformData { public Vector3Data position; public QuaternionData rotation; }
    [System.Serializable] public class Vector3Data { public float x, y, z; }
    [System.Serializable] public class QuaternionData { public float x, y, z, w; }

    [System.Serializable]
    public class EyeTrackingData
    {
        public EyeData leftEye;
        public EyeData rightEye;
        public CombinedGazeData combinedGaze;
    }

    [System.Serializable]
    public class EyeData
    {
        public bool isValid;
        public Vector3Data gazeOrigin;
        public Vector3Data gazeDirection;
        public float pupilDiameterMm;
        public float openness;
    }

    [System.Serializable]
    public class CombinedGazeData
    {
        public bool isValid;
        public Vector3Data gazeDirection;
    }

    [System.Serializable]
    public class DestructionEvent
    {
        public int destroyOrder;
        public string objectName;
        public float timeSinceAppStart;
        public float timeSincePreviousDestroy;
        public Vector3Data headPositionAtDestroy;
        public Vector3Data headRotationEulerAtDestroy;
        public Vector3Data targetPositionAtDestroy;
        public float target_X;
        public float target_Y;
        public float target_Z;
        public float gazeStabilityDuringDwell_deg;
        public string notes;
    }

    /// <summary>
    /// One continuous look at a single object: from the frame the gaze ray landed on it
    /// until the frame it left. Written to the JSON summary and (optionally) the NDJSON stream.
    /// </summary>
    [System.Serializable]
    public class GazeLookEvent
    {
        public int lookOrder;
        public string objectName;
        public string category;          // "Target", "RecallObject", "Other", or whatever GazeLoggableObject says
        public string objectTag;
        public string layer;
        public int objectInstanceId;
        public float startTime;          // seconds since this trial's recording started
        public float endTime;
        public float durationSeconds;
        public int sampleCount;
        public float meanDistanceMeters;
        public float gazeStability_deg;
        public Vector3Data objectPosition;
    }

    /// <summary>Total dwell accumulated on one object across the whole trial.</summary>
    [System.Serializable]
    public class GazeObjectStat
    {
        public string objectName;
        public string category;
        public int objectInstanceId;
        public float totalDwellSeconds;
        public int lookCount;
        public float meanLookDurationSeconds;
        public float longestLookSeconds;
        public float firstLookTime = -1f;
        public float percentOfTrackedTime;    // share of all time the gaze ray was sampled
        public float percentOfTimeOnObjects;  // share of the time the gaze was on *something*
    }

    /// <summary>The "what did they actually look at, and for how much of the trial" rollup.</summary>
    [System.Serializable]
    public class GazeAttentionSummary
    {
        public float totalTrackedSeconds;
        public float totalTimeOnObjectsSeconds;
        public float totalTimeOnNothingSeconds;
        public float percentTimeOnObjects;
        public float percentTimeOnNothing;
        // These four count different things and used to be reported in a way that made them look
        // contradictory (distinctObjectsLookedAt could exceed totalLookEvents, which reads as impossible).
        // totalLookEvents  : looks long enough to clear minLookDurationToLogSeconds and reach the event stream.
        // totalLookSegments: EVERY contiguous look, including the sub-threshold ones that still add dwell time.
        // distinctObjectsLookedAt   : objects that accumulated any dwell at all.
        // distinctObjectsInLookEvents: objects that produced at least one logged look event.
        public int totalLookEvents;
        public int totalLookSegments;
        public int distinctObjectsLookedAt;
        public int distinctObjectsInLookEvents;
        public List<GazeObjectStat> perCategory = new List<GazeObjectStat>();
        public List<GazeObjectStat> perObject = new List<GazeObjectStat>();
    }

    [System.Serializable]
    public class SessionPerformanceData
    {
        public float timeToFirstDestroy;
        public float totalTimeToComplete;
        public int totalObjectsDestroyed;
        public float meanInterDestroyInterval;
        public DataQualityInfo dataQuality;
    }

    [System.Serializable]
    public class DataQualityInfo
    {
        // Share of gaze samples where the tracker returned a FRESH pose. This is the field the preregistered
        // exclusion criterion is read from. It sat at 0 on every file ever recorded because nothing assigned
        // it; a trial cannot be screened without it.
        public float estimatedValidGazeSamplesPercent;
        public int validGazeSamples;
        public int totalGazeSamples;

        // Share of valid samples where the gaze ray hit any collider. A trial that is 100% valid but near-0%
        // hits usually means the ray is mis-transformed, not that the participant stared at empty sky.
        public float gazeRaycastHitPercent;

        // Frame pacing, reported as a condition check. Gaze is sampled on the render loop, so a condition that
        // costs frame rate also costs gaze samples -- the pilot's wireframe trials ran ~0.6 Hz slower than the
        // zero-wireframe ones. That bias is why the primary outcome is a proportion of TIME, not of frames.
        public float measuredFrameRateHz;
        public float meanFrameIntervalMs;
        public float longestFrameIntervalMs;
        public int framesOver50ms;
        public float percentFramesOver50ms;

        public string notes;
    }

    /// <summary>
    /// The preregistered outcomes, computed here so the analysis never re-derives them from the raw stream
    /// and the numbers in the file are the numbers that get analyzed.
    ///
    /// Primary: share of valid tracked time the gaze rested on non-target scene geometry (category "Other").
    /// Time-weighted -- a sum of per-sample dt, not a frame count -- so it is immune to the frame-rate
    /// difference between the wireframe and zero-wireframe conditions.
    ///
    /// Secondary: how many DISTINCT pieces of scenery were fixated per minute of tracked time. The raw
    /// distinct-object count is uninterpretable alone because trials differ in length.
    ///
    /// The navigation map is scored under its own category and is NOT part of "Other". It is a
    /// head-locked UI panel, not part of the environment, and it dominated the Other total in P001
    /// (33.9 s of 35.3 s in trial 1, 46.5 s of 56.0 s in trial 2). Leaving it in makes the primary DV
    /// read "time spent consulting the map", which is a different construct and moves in the opposite
    /// direction: looking AT the map is time spent NOT looking at the world.
    /// </summary>
    [System.Serializable]
    public class PrimaryOutcomeData
    {
        public float proportionTimeOnOtherScenery;   // primary DV, 0..1
        public float percentTimeOnOtherScenery;      // same number, readable
        public float secondsOnOtherScenery;
        public float trackedSeconds;

        public int distinctOtherObjectsFixated;
        public float distinctOtherObjectsPerMinute;  // secondary DV

        // Carried alongside so the primary can be checked against the broader measure that barely moved in
        // the pilot: the signal is specifically in Other, not in on-object time overall.
        public float percentTimeOnTargets;
        public float percentTimeOnRecallObjects;

        // Map consultation, reported separately rather than discarded: how much a participant leaned on
        // the map is a plausible covariate for how much of the environment they looked at.
        public float secondsOnMap;
        public float percentTimeOnMap;
    }

    [System.Serializable]
    public class SessionSummaryRoot
    {
        public SessionMeta metadata;
        public SessionPerformanceData sessionData;
        public PrimaryOutcomeData primaryOutcome;
        public List<DestructionEvent> destructionEvents = new List<DestructionEvent>();
        public GazeAttentionSummary gazeAttention;
        public List<GazeLookEvent> gazeLookEvents = new List<GazeLookEvent>();
        public string rawTrackingFileReference;
        public string gazeEventsFileReference;
    }

    [System.Serializable]
    public class CombinedExportRoot
    {
        public SessionMeta sessionMeta;
        public List<TrackingFrame> trackingData = new List<TrackingFrame>();
        public List<DestructionEvent> destructionEvents = new List<DestructionEvent>();
    }

    // ==================== PRIVATE STATE ====================

    private SessionMeta sessionMeta;
    private List<DestructionEvent> destructionEvents = new List<DestructionEvent>();
    private SessionPerformanceData performanceData = new SessionPerformanceData();
    private string currentTrialName = "Tutorial";

    private MeshRenderer[] targetRenderers;
    private float dwellOverTargetTracker;
    private int destroyCount = 0;
    private float appStartTime;
    private float previousDestroyTime;
    private int currentFrameId = 0;
    private float lastFrameTimestamp;
    private List<Vector3> dwellDirectionsDuringCurrentDwell = new List<Vector3>();
    private string currentHitObjectName = "None";
    private float autoSaveTimer = 0f;

    // ---- Gaze object logging state ----
    /// <summary>Cached, per-collider resolution of "which logical object is this and what is it called".</summary>
    private class GazeObjectInfo
    {
        public int objectId;
        public string name;
        public string category;
        public string objectTag;
        public string layer;
        public Transform transform;
    }

    private readonly Dictionary<int, GazeObjectInfo> gazeInfoByColliderId = new Dictionary<int, GazeObjectInfo>();
    private readonly Dictionary<int, GazeObjectStat> gazeStatsByObjectId = new Dictionary<int, GazeObjectStat>();
    private readonly List<GazeLookEvent> gazeLookEvents = new List<GazeLookEvent>();
    private readonly List<Vector3> currentLookDirections = new List<Vector3>();
    private const int MAX_LOOK_DIRECTION_SAMPLES = 900; // ~30 s at 30 fps, then we stop growing the list

    private GazeObjectInfo currentGazeInfo;
    private float currentLookStartTime;
    private int currentLookSampleCount;
    private float currentLookDistanceSum;
    private Vector3 currentLookObjectPosition;

    private float gazeTrackedSeconds;
    private float gazeOnObjectSeconds;
    private float gazeOnNothingSeconds;
    private float lastGazeSampleTime = -1f;
    private int gazeSampleFrameCounter;
    private int gazeLookEventCounter;
    private int gazeLookSegmentCounter;
    private readonly HashSet<int> objectsWithLoggedLookEvents = new HashSet<int>();

    // ---- Data-quality counters (feed DataQualityInfo) ----
    private int gazeSamplesTotal;   // every time SampleGazeObjects ran
    private int gazeSamplesValid;   // ...and the tracker had a fresh pose for us
    private int gazeSamplesWithHit; // ...and the gaze ray landed on a collider

    // ---- Frame pacing counters (feed DataQualityInfo + metadata.measuredSampleRateHz) ----
    private int frameIntervalCount;
    private float frameIntervalSumMs;
    private float frameIntervalMaxMs;
    private int framesOver50ms;

    /// <summary>Sentinel for a per-eye metric the Magic Leap gaze path does not expose at all.</summary>
    private const float EyeMetricUnavailable = -1f;

    private string currentHitCategory = "None";
    private string currentHitTag = "None";
    private string currentHitLayer = "None";
    private float currentHitDistance = 0f;

    private readonly StringBuilder gazeEventStringBuilder = new StringBuilder(512);
    private readonly ConcurrentQueue<string> gazeEventQueue = new ConcurrentQueue<string>();
    private StreamWriter gazeEventWriter;
    private string gazeEventsFileName;
    private static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;

    private readonly int fillProgressProperty = Shader.PropertyToID("_FillProgress");
    private const float MIN_FILL_RANGE = -0.6f;
    private const float MAX_FILL_RANGE = 0.6f;

    private string persistentDataPath;
    private StreamWriter rawNdjsonWriter;
    private string startTimeString;

    // Extra filename component describing the trial's condition, e.g. "Pool3_Seed300". Supplied by the study
    // through StartNewTrialRecording so the pool and the spawn seed are readable from a DIRECTORY LISTING and
    // not only from inside the file. Empty for sets that have no pool, like the tutorial.
    private string conditionTag = "";

    // ==================== incomplete_ / completed_ NAMING ====================
    //
    // Every file starts life as "incomplete_..." and is renamed to "completed_..." only once the set has
    // ended and all three handles are closed. A hard platform reboot or a flat battery mid-trial fires
    // NEITHER OnApplicationQuit NOR OnApplicationPause, so the prefix is the only trustworthy signal that a
    // file was closed properly: anything still named incomplete_ on the headset is truncated data.
    private const string IncompletePrefix = "incomplete_";
    private const string CompletedPrefix = "completed_";

    /// <summary>
    /// Category string for the navigation map. Public because MapTracking stamps it onto the map instance
    /// at spawn time, and the two must not be allowed to disagree via a copied string literal: if they do,
    /// the map silently falls through to "Other" again and the primary DV goes back to being wrong.
    /// </summary>
    public const string MapCategory = "Map";

    // Flips the prefix once the set is finalized, and stops later saves recreating an incomplete_ copy.
    private bool trialFilesFinalized;
    private string FilePrefix => trialFilesFinalized ? CompletedPrefix : IncompletePrefix;

    // The in-flight background summary write. SaveSessionData is fire-and-forget, so finalization has to
    // WAIT on it: a write that lands after the rename would recreate the incomplete_ summary alongside the
    // completed_ one, and the newest file on disk would then be the wrong one.
    private System.Threading.Tasks.Task _summaryWriteTask;

    private ConcurrentQueue<string> rawLogQueue = new ConcurrentQueue<string>();
    private bool isRawLoggingThreadRunning = false;
    private object rawWriterLock = new object();

    private StringBuilder ndjsonStringBuilder = new StringBuilder(512);
    private TrackingFrame currentTrackingFrame = new TrackingFrame
    {
        headTransform = new HeadTransformData { position = new Vector3Data(), rotation = new QuaternionData() },
        eyeTracking = new EyeTrackingData
        {
            leftEye = new EyeData { gazeOrigin = new Vector3Data(), gazeDirection = new Vector3Data() },
            rightEye = new EyeData { gazeOrigin = new Vector3Data(), gazeDirection = new Vector3Data() },
            combinedGaze = new CombinedGazeData { gazeDirection = new Vector3Data() }
        }
    };

    // ==================== LIFECYCLE ====================

    private void Awake()
    {
        Instance = this;

        // Cap the framerate to 30fps to reduce heat generation on Magic Leap.
        // Set here as well as TrialManager/SequenceManager so it can't be missed if
        // this tracker runs in a scene without them.
        Application.targetFrameRate = 30;

        persistentDataPath = Application.persistentDataPath;
        Directory.CreateDirectory(persistentDataPath);

        if (blueWireframeObject != null) blueWireframeObject.SetActive(enableBlueWireframe);
        if (mapGameObject != null) mapGameObject.SetActive(enableMap);

        sessionMeta = new SessionMeta
        {
            sessionId = sessionId,
            participantId = participantId,
            condition = condition,
            environment = environment
        };

        if (Application.platform == RuntimePlatform.Android)
            RequestWritePermission();

        if (recordOnAwake)
        {
            ResumeRecording();
        }
    }

    public void ResumeRecording()
    {
        if (isRecording) return;

        if (!hasInitializedRecording)
        {
            appStartTime = Time.realtimeSinceStartup;
            lastFrameTimestamp = appStartTime;
            startTimeString = DateTime.Now.ToString("MM_dd_HH_mm_ss");

            if (useEfficientRawLogging)
            {
                string rawPath = Path.Combine(persistentDataPath, BuildFileName("raw_eye_head_tracking", "ndjson"));
                lock (rawWriterLock)
                {
                    rawNdjsonWriter = new StreamWriter(rawPath, true);
                }

                EnsureRawLoggingThread();
            }

            OpenGazeEventWriter();
            hasInitializedRecording = true;
        }
        else
        {
            // Just resuming, update timestamp so we don't log a massive delta time gap
            lastFrameTimestamp = Time.realtimeSinceStartup;
        }

        // Don't let the paused interval count as dwell time on the first sample back.
        lastGazeSampleTime = -1f;

        isRecording = true;
        Debug.Log("EyeAndHeadTracker: JSON recording RESUMED.");
    }

    public int GetTargetsDestroyed() => destroyCount;
    
    public float GetCurrentTrialTime() => isRecording ? (Time.realtimeSinceStartup - appStartTime) : 0f;
    
    public List<Vector3> GetRemainingTargetPositions()
    {
        if (targetRenderers == null) return new List<Vector3>();
        return targetRenderers.Where(r => r != null).Select(r => r.transform.position).ToList();
    }

    public void PauseRecording()
    {
        if (!isRecording) return;
        isRecording = false;
        // Close the look in progress so it makes it into the summary rather than being lost.
        CloseCurrentLook(Time.realtimeSinceStartup);
        SetCurrentHitFields(null, 0f);
        SaveSessionData(); // Force save to disk whenever we pause to ensure data safety
        Debug.Log("EyeAndHeadTracker: JSON recording PAUSED.");
    }

    /// <summary>
    /// Builds every output filename from ONE place. All three streams have to agree, because the JSON summary
    /// stores the other two filenames as cross-references (rawTrackingFileReference / gazeEventsFileReference)
    /// -- assembling them separately is exactly how a summary ends up pointing at a raw file that does not
    /// exist under that name.
    ///
    /// Shape: {incomplete_|completed_}{prefix}_{participant}_{session}_{trial}[_{condition}]_{MM_dd_HH_mm_ss}.{ext}
    /// e.g.   incomplete_gaze_session_summary_P002_S001_Trial_3_Pool4_Seed400_07_29_14_32_05.json
    ///        completed_gaze_session_summary_P002_S001_Trial_3_Pool4_Seed400_07_29_14_32_05.json
    /// </summary>
    private string BuildFileName(string prefix, string extension)
    {
        string condition = string.IsNullOrEmpty(conditionTag) ? "" : $"_{conditionTag}";
        return $"{FilePrefix}{prefix}_{participantId}_{sessionId}_{currentTrialName}{condition}_{startTimeString}.{extension}";
    }

    /// <summary>Keeps a caller-supplied tag safe to paste into a filename: letters, digits, underscore and
    /// dash survive; spaces and dots become underscores; anything else is dropped.</summary>
    private static string SanitizeForFileName(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return "";

        var sb = new System.Text.StringBuilder(tag.Length);
        foreach (char c in tag)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            else if (c == ' ' || c == '.') sb.Append('_');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Closes this set's three files and renames them from incomplete_ to completed_. Call it when a set has
    /// genuinely ENDED (every target destroyed, or ended deliberately from the dashboard) -- never on a
    /// mid-trial pause, which goes on writing to the same files after it resumes.
    ///
    /// The ordering here IS the method; each step exists because the obvious order is broken:
    ///   1. wait out the fire-and-forget background summary write, or it lands after the rename and
    ///      recreates the incomplete_ summary;
    ///   2. close all three handles, because a file open for writing cannot be renamed;
    ///   3. rename the two ndjson streams while the prefix still reads incomplete_;
    ///   4. flip the prefix and rewrite the summary SYNCHRONOUSLY, so the filenames it embeds
    ///      (rawTrackingFileReference / gazeEventsFileReference) name files that exist;
    ///   5. delete the incomplete_ summary the autosaves left behind.
    ///
    /// Idempotent, and a no-op if nothing was ever opened. Never throws: a rename that fails leaves the data
    /// in place under its incomplete_ name, which is far better than losing the trial.
    /// </summary>
    public void FinalizeTrialFiles()
    {
        if (trialFilesFinalized || !hasInitializedRecording) return;

        isRecording = false;

        // 1. Let the in-flight background summary write settle before touching any filename.
        try
        {
            _summaryWriteTask?.Wait(5000);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"EyeAndHeadTracker: background summary write did not settle before " +
                             $"finalizing {currentTrialName}: {ex.Message}");
        }
        _summaryWriteTask = null;

        // 2. Release every handle. The raw writer is shared with RawLoggingThreadLoop, which null-checks it
        // under rawWriterLock, so nulling it here turns any stray queued line into a no-op rather than a
        // write into a renamed file.
        CloseCurrentLook(Time.realtimeSinceStartup);
        CloseGazeEventWriter();

        if (rawNdjsonWriter != null)
        {
            lock (rawWriterLock)
            {
                rawNdjsonWriter.Flush();
                rawNdjsonWriter.Close();
                rawNdjsonWriter = null;
            }
        }

        // 3. Names as they are on disk right now (still incomplete_).
        string rawOld = BuildFileName("raw_eye_head_tracking", "ndjson");
        string summaryOld = BuildFileName("gaze_session_summary", "json");
        string eventsOld = gazeEventsFileName;

        // 4. Flip the prefix; every BuildFileName call from here returns the completed_ name.
        trialFilesFinalized = true;

        if (useEfficientRawLogging)
            TryRenameInDataPath(rawOld, BuildFileName("raw_eye_head_tracking", "ndjson"));

        if (!string.IsNullOrEmpty(eventsOld))
        {
            string eventsNew = BuildFileName("gaze_events", "ndjson");
            // Only update the cross-reference if the rename actually happened, so the summary never names
            // a file that is not there.
            if (TryRenameInDataPath(eventsOld, eventsNew)) gazeEventsFileName = eventsNew;
        }

        // 5. Rewrite the summary under its completed_ name, synchronously so it cannot race the rename.
        SaveSessionData(true);
        TryDeleteInDataPath(summaryOld);

        Debug.Log($"EyeAndHeadTracker: {currentTrialName} finalized; files renamed to {CompletedPrefix}*.");
    }

    /// <summary>Renames a file inside persistentDataPath. Returns whether it moved. Never throws.</summary>
    private bool TryRenameInDataPath(string oldFileName, string newFileName)
    {
        try
        {
            string oldPath = Path.Combine(persistentDataPath, oldFileName);
            string newPath = Path.Combine(persistentDataPath, newFileName);

            if (!File.Exists(oldPath)) return false;
            // File.Move throws if the destination exists, which it can after a re-run at the same timestamp.
            if (File.Exists(newPath)) File.Delete(newPath);

            File.Move(oldPath, newPath);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"EyeAndHeadTracker: could not rename {oldFileName} -> {newFileName}: " +
                           $"{ex.Message}. The data is intact under its {IncompletePrefix}name.");
            return false;
        }
    }

    private void TryDeleteInDataPath(string fileName)
    {
        try
        {
            string path = Path.Combine(persistentDataPath, fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"EyeAndHeadTracker: could not remove the superseded {fileName}: {ex.Message}");
        }
    }

    /// <summary>Starts a new trial's recording with no condition in the filenames. Used for sets that have
    /// no pool or seed, like the tutorial.</summary>
    public void StartNewTrialRecording(string trialName) => StartNewTrialRecording(trialName, null);

    /// <summary>
    /// Starts a new trial's recording. <paramref name="trialConditionTag"/> is folded into all three of this
    /// trial's output filenames (e.g. "Pool3_Seed300"), so which pool and which spawn seed produced the data
    /// is visible without opening anything. Pass null or empty for sets with no pool.
    /// </summary>
    public void StartNewTrialRecording(string trialName, string trialConditionTag)
    {
        // Save old data before clearing. Note this runs while currentTrialName/conditionTag still describe
        // the OUTGOING trial, so its files close under its own name -- do not reorder these.
        if (destructionEvents.Count > 0)
        {
            SaveSessionData();
        }

        // A new set starts life incomplete_ again. Must come after the save above, which belongs to the
        // OUTGOING set (and is a no-op if that set was already finalized).
        trialFilesFinalized = false;
        _summaryWriteTask = null;

        conditionTag = SanitizeForFileName(trialConditionTag);
        currentTrialName = trialName;
        destructionEvents.Clear();
        destroyCount = 0;
        performanceData = new SessionPerformanceData();
        ResetGazeLogging();

        // Per TRIAL, not per app run. This counter used to run continuously across every trial in a session
        // (trial 2 opening at frameId 26734), so frameId could not be used to index into a trial's own stream
        // and silently reset to 0 whenever the app was relaunched mid-session.
        currentFrameId = 0;

        appStartTime = Time.realtimeSinceStartup;
        lastFrameTimestamp = appStartTime;
        startTimeString = DateTime.Now.ToString("MM_dd_HH_mm_ss");

        if (rawNdjsonWriter != null)
        {
            lock (rawWriterLock)
            {
                rawNdjsonWriter.Flush();
                rawNdjsonWriter.Close();
                rawNdjsonWriter = null;
            }
        }

        // ResetGazeLogging above closed the look in progress, which may still be sitting in
        // the queue. Drain it into the OUTGOING trial's file before swapping writers.
        CloseGazeEventWriter();

        if (useEfficientRawLogging)
        {
            string rawPath = Path.Combine(persistentDataPath, BuildFileName("raw_eye_head_tracking", "ndjson"));
            lock (rawWriterLock)
            {
                rawNdjsonWriter = new StreamWriter(rawPath, true);
            }

            EnsureRawLoggingThread();
        }

        OpenGazeEventWriter();

        hasInitializedRecording = true;
        isRecording = true;
        Debug.Log($"EyeAndHeadTracker: JSON recording started for {currentTrialName}.");
    }

    public void RefreshTargetList()
    {
        if (enableDwellDestroyFeature)
        {
            var allTargets = GameObject.FindGameObjectsWithTag(targetTag);
            targetRenderers = allTargets
                .Select(n => n.GetComponentInChildren<MeshRenderer>())
                .Where(r => r != null && r.enabled)
                .ToArray();

            Debug.Log($"RefreshTargetList: Found {targetRenderers.Length} objects with tag '{targetTag}'.");
        }
    }

    private void Start()
    {
        RefreshTargetList();

        // Diagnostic: Verify GazeInputManager is properly set up in the scene
        if (GazeInputManager.Instance == null)
        {
            Debug.LogError("⚠️ GazeInputManager.Instance is NULL! Eye dwell will NOT work. " +
                "Make sure a GameObject with GazeInputManager is in the scene.");
        }
        else
        {
            Debug.Log($"✅ GazeInputManager found. Permission granted: {GazeInputManager.Instance.EyeTrackingPermissionGranted}");
        }
    }

    private void RequestWritePermission()
    {
        if (!Permission.HasUserAuthorizedPermission(Permission.ExternalStorageWrite))
            Permission.RequestUserPermission(Permission.ExternalStorageWrite);
    }

    private void Update()
    {
        // When gaze object logging is on, the hit fields are owned by SampleGazeObjects() and
        // stay sticky between samples (they may be throttled to every Nth frame).
        if (!enableGazeObjectLogging) currentHitObjectName = "None";

        // Gated on isRecording so this doesn't keep raycasting/querying the eye-tracking
        // hardware every frame during cooldown/menu screens between trials, when
        // SequenceManager has paused recording specifically to let the device cool down.
        if (isRecording &&
            enableDwellDestroyFeature &&
            GazeInputManager.Instance != null &&
            GazeInputManager.Instance.EyeTrackingPermissionGranted)
        {
            RunEyeDwellDestruction();
        }

        if (isRecording)
        {
            TrackFramePacing();

            if (enableGazeObjectLogging) SampleGazeObjects();

            CaptureTrackingFrame();

            // Periodic auto-save to prevent data loss on crash
            autoSaveTimer += Time.deltaTime;
            if (autoSaveTimer >= autoSaveIntervalSeconds)
            {
                SaveSessionData();
                autoSaveTimer = 0f;
            }
        }
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            CloseCurrentLook(Time.realtimeSinceStartup);
            SaveSessionData();

            // Backgrounding is where Android is most likely to kill us, so push the queued
            // events out synchronously rather than trusting the writer thread to get a slice.
            DrainGazeEventQueue();
            if (gazeEventWriter != null)
            {
                lock (rawWriterLock) { gazeEventWriter.Flush(); }
            }

            Debug.Log("EyeAndHeadTracker: App paused, data saved safely.");
        }
        else
        {
            lastGazeSampleTime = -1f; // the backgrounded interval isn't dwell time
        }
    }

    private void OnApplicationQuit()
    {
        CloseCurrentLook(Time.realtimeSinceStartup);
        SaveSessionData();
        CloseGazeEventWriter();
        Debug.Log("EyeAndHeadTracker: App quit, data saved safely.");
    }

    // ==================== TRACKING ====================

    private void CaptureTrackingFrame()
    {
        // Everything computed here (UTC timestamp string, head sampling, eye sampling)
        // feeds ONLY the raw NDJSON stream. When raw logging is off there is no consumer,
        // so skip the whole per-frame body to save CPU/GC — and therefore heat — on ML2.
        if (!useEfficientRawLogging) return;

        float now = Time.realtimeSinceStartup;
        float dtMs = (lastFrameTimestamp == 0f) ? 0f : (now - lastFrameTimestamp) * 1000f;
        lastFrameTimestamp = now;

        var head = Camera.main != null ? Camera.main.transform : transform;

        // 1. Update reusable object (Solution A)
        currentTrackingFrame.frameId = currentFrameId++;
        currentTrackingFrame.timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        currentTrackingFrame.deltaTimeMs = dtMs;
        
        currentTrackingFrame.headTransform.position.x = head.position.x;
        currentTrackingFrame.headTransform.position.y = head.position.y;
        currentTrackingFrame.headTransform.position.z = head.position.z;
        
        currentTrackingFrame.headTransform.rotation.x = head.rotation.x;
        currentTrackingFrame.headTransform.rotation.y = head.rotation.y;
        currentTrackingFrame.headTransform.rotation.z = head.rotation.z;
        currentTrackingFrame.headTransform.rotation.w = head.rotation.w;
        
        UpdateEyeTrackingData(currentTrackingFrame.eyeTracking);
        currentTrackingFrame.eyeRaycastHitObject = currentHitObjectName;
        currentTrackingFrame.gazeHitCategory = currentHitCategory;
        currentTrackingFrame.gazeHitTag = currentHitTag;
        currentTrackingFrame.gazeHitLayer = currentHitLayer;
        currentTrackingFrame.gazeHitDistanceMeters = currentHitDistance;

        if (useEfficientRawLogging && rawNdjsonWriter != null)
        {
            // 2. Zero-allocation string building (Solution B)
            var f = currentTrackingFrame;
            var h = f.headTransform;
            var e = f.eyeTracking;

            ndjsonStringBuilder.Clear();
            ndjsonStringBuilder.Append("{\"type\":\"frame\",\"frameId\":").Append(f.frameId)
                .Append(",\"timestamp\":\"").Append(f.timestamp)
                .Append("\",\"deltaTimeMs\":").Append(f.deltaTimeMs.ToString("F3", Inv))
                .Append(",\"headTransform\":{\"position\":{\"x\":").Append(h.position.x.ToString("F4", Inv)).Append(",\"y\":").Append(h.position.y.ToString("F4", Inv)).Append(",\"z\":").Append(h.position.z.ToString("F4", Inv))
                .Append("},\"rotation\":{\"x\":").Append(h.rotation.x.ToString("F4", Inv)).Append(",\"y\":").Append(h.rotation.y.ToString("F4", Inv)).Append(",\"z\":").Append(h.rotation.z.ToString("F4", Inv)).Append(",\"w\":").Append(h.rotation.w.ToString("F4", Inv))
                .Append("}},\"eyeTracking\":{\"leftEye\":{\"isValid\":").Append(e.leftEye.isValid ? "true" : "false")
                .Append(",\"gazeOrigin\":{\"x\":").Append(e.leftEye.gazeOrigin.x.ToString("F4", Inv)).Append(",\"y\":").Append(e.leftEye.gazeOrigin.y.ToString("F4", Inv)).Append(",\"z\":").Append(e.leftEye.gazeOrigin.z.ToString("F4", Inv))
                .Append("},\"gazeDirection\":{\"x\":").Append(e.leftEye.gazeDirection.x.ToString("F4", Inv)).Append(",\"y\":").Append(e.leftEye.gazeDirection.y.ToString("F4", Inv)).Append(",\"z\":").Append(e.leftEye.gazeDirection.z.ToString("F4", Inv))
                .Append("},\"pupilDiameterMm\":").Append(e.leftEye.pupilDiameterMm.ToString("F2", Inv))
                .Append(",\"openness\":").Append(e.leftEye.openness.ToString("F2", Inv))
                .Append("},\"rightEye\":{\"isValid\":").Append(e.rightEye.isValid ? "true" : "false")
                .Append(",\"gazeOrigin\":{\"x\":").Append(e.rightEye.gazeOrigin.x.ToString("F4", Inv)).Append(",\"y\":").Append(e.rightEye.gazeOrigin.y.ToString("F4", Inv)).Append(",\"z\":").Append(e.rightEye.gazeOrigin.z.ToString("F4", Inv))
                .Append("},\"gazeDirection\":{\"x\":").Append(e.rightEye.gazeDirection.x.ToString("F4", Inv)).Append(",\"y\":").Append(e.rightEye.gazeDirection.y.ToString("F4", Inv)).Append(",\"z\":").Append(e.rightEye.gazeDirection.z.ToString("F4", Inv))
                .Append("},\"pupilDiameterMm\":").Append(e.rightEye.pupilDiameterMm.ToString("F2", Inv))
                .Append(",\"openness\":").Append(e.rightEye.openness.ToString("F2", Inv))
                .Append("},\"combinedGaze\":{\"isValid\":").Append(e.combinedGaze.isValid ? "true" : "false")
                .Append(",\"gazeDirection\":{\"x\":").Append(e.combinedGaze.gazeDirection.x.ToString("F4", Inv)).Append(",\"y\":").Append(e.combinedGaze.gazeDirection.y.ToString("F4", Inv)).Append(",\"z\":").Append(e.combinedGaze.gazeDirection.z.ToString("F4", Inv))
                .Append("}}},\"eyeRaycastHitObject\":\"").Append(EscapeJson(f.eyeRaycastHitObject))
                .Append("\",\"gazeHitCategory\":\"").Append(EscapeJson(f.gazeHitCategory))
                .Append("\",\"gazeHitTag\":\"").Append(EscapeJson(f.gazeHitTag))
                .Append("\",\"gazeHitLayer\":\"").Append(EscapeJson(f.gazeHitLayer))
                .Append("\",\"gazeHitDistanceMeters\":").Append(f.gazeHitDistanceMeters.ToString("F3", Inv))
                .Append("}");
            rawLogQueue.Enqueue(ndjsonStringBuilder.ToString());
        }
        else if (!useEfficientRawLogging)
        {
            // Debug.LogWarning("EyeAndHeadTracker: useEfficientRawLogging is false, but in-memory logging has been removed. Please enable useEfficientRawLogging!");
        }
    }

    private void UpdateEyeTrackingData(EyeTrackingData data)
    {
        if (GazeInputManager.Instance == null)
        {
            data.leftEye.isValid = false;
            data.rightEye.isValid = false;
            data.combinedGaze.isValid = false;
            return;
        }

        var gazePos = GazeInputManager.Instance.GazePosition;
        var gazeRot = GazeInputManager.Instance.GazeRotation;

        // The SDK reports the gaze pose in TRACKING space; headTransform in the same logged row is in
        // WORLD space. Through P001 this block wrote the raw pose, so gazeOrigin sat a median 104 m (trial
        // 1) and 177 m (trial 2) from the head position it was supposed to originate at, and no offline
        // consumer could relate the two. The dwell data was never affected -- the raycasts go through
        // TryGetGazeRay, which converts -- but the logged vectors were unusable. Same conversion, one place.
        TrackingToWorld(ref gazePos, ref gazeRot);

        Vector3 gazeDir = gazeRot * Vector3.forward;

        // isValid now means "the tracker returned a fresh sample this frame", not "the participant granted
        // permission at startup". The old meaning was a session-long constant, which is exactly why every
        // file recorded 100% validity on every channel. Frames where this is false carry the LAST GOOD pose
        // and must be treated as missing data downstream.
        bool isValid = GazeInputManager.Instance.EyeTrackingPermissionGranted &&
                       GazeInputManager.Instance.IsGazeTracked;

        // The left/right blocks are the combined gaze pose offset by half an assumed 64 mm IPD. They are
        // kept so the schema stays stable, but they are DERIVED -- the Magic Leap path exposes one combined
        // gaze and no per-eye stream, so treating them as two eyes would be measuring the constant 0.064.
        // pupilDiameterMm/openness are unavailable entirely; they were previously written as 3.4/3.5 and
        // 0.95/0.96, constants that read as genuine measurements across all 100k logged frames.
        data.leftEye.isValid = isValid;
        data.leftEye.gazeOrigin.x = gazePos.x - 0.032f;
        data.leftEye.gazeOrigin.y = gazePos.y;
        data.leftEye.gazeOrigin.z = gazePos.z;
        data.leftEye.gazeDirection.x = gazeDir.x;
        data.leftEye.gazeDirection.y = gazeDir.y;
        data.leftEye.gazeDirection.z = gazeDir.z;
        data.leftEye.pupilDiameterMm = EyeMetricUnavailable;
        data.leftEye.openness = EyeMetricUnavailable;

        data.rightEye.isValid = isValid;
        data.rightEye.gazeOrigin.x = gazePos.x + 0.032f;
        data.rightEye.gazeOrigin.y = gazePos.y;
        data.rightEye.gazeOrigin.z = gazePos.z;
        data.rightEye.gazeDirection.x = gazeDir.x;
        data.rightEye.gazeDirection.y = gazeDir.y;
        data.rightEye.gazeDirection.z = gazeDir.z;
        data.rightEye.pupilDiameterMm = EyeMetricUnavailable;
        data.rightEye.openness = EyeMetricUnavailable;

        data.combinedGaze.isValid = isValid;
        data.combinedGaze.gazeDirection.x = gazeDir.x;
        data.combinedGaze.gazeDirection.y = gazeDir.y;
        data.combinedGaze.gazeDirection.z = gazeDir.z;
    }

    // ==================== GAZE DESTRUCTION ====================

    /// <summary>
    /// Resolves the eye gaze ray into world space (the raw pose is in tracking space).
    /// Returns false when eye tracking isn't available this frame.
    /// </summary>
    private bool TryGetGazeRay(out Vector3 gazePosition, out Quaternion gazeRotation)
        => TryGetGazeRay(out gazePosition, out gazeRotation, out _);

    /// <summary>
    /// As above, and additionally reports whether the pose is a FRESH sample from the tracker
    /// (<paramref name="isFresh"/>) or the last good one being held through a blink or a tracking drop.
    ///
    /// The distinction is the whole point: GazeInputManager keeps serving the previous pose when tracking
    /// lapses, so a caller that cannot tell the difference will happily raycast a stale ray and bank the
    /// result as dwell time on whatever it happened to be aimed at. Every consumer here now either skips
    /// stale frames or counts them as missing data.
    /// </summary>
    private bool TryGetGazeRay(out Vector3 gazePosition, out Quaternion gazeRotation, out bool isFresh)
    {
        gazePosition = Vector3.zero;
        gazeRotation = Quaternion.identity;
        isFresh = false;

        if (GazeInputManager.Instance == null || !GazeInputManager.Instance.EyeTrackingPermissionGranted)
            return false;

        gazePosition = GazeInputManager.Instance.GazePosition;
        gazeRotation = GazeInputManager.Instance.GazeRotation;
        isFresh = GazeInputManager.Instance.IsGazeTracked;

        TrackingToWorld(ref gazePosition, ref gazeRotation);

        return true;
    }

    /// <summary>
    /// Converts a gaze pose from XR tracking space into world space, in place.
    ///
    /// Factored out so the raycast path and the logging path cannot disagree about which space they are
    /// in. They did disagree through P001, and the symptom -- a gazeOrigin 100+ m from the head position
    /// recorded on the same frame -- is only visible if you go looking for it in the raw file.
    /// A no-op when there is no tracking origin, which is what the Editor sees.
    /// </summary>
    private static void TrackingToWorld(ref Vector3 position, ref Quaternion rotation)
    {
        var cam = Camera.main;
        if (cam == null || cam.transform.parent == null) return;

        var trackingOrigin = cam.transform.parent;
        position = trackingOrigin.TransformPoint(position);
        rotation = trackingOrigin.rotation * rotation;
    }

    private void RunEyeDwellDestruction()
    {
        // Stale poses are skipped rather than raycast: returning early neither advances nor resets the dwell
        // timer, so a blink coasts through instead of either banking free progress on a frozen ray or
        // cancelling a dwell the participant is still holding.
        if (!TryGetGazeRay(out Vector3 gazePosition, out Quaternion gazeRotation, out bool isFresh) || !isFresh)
            return;

        // DEBUG: Record what the eye is actually hitting (ignoring layers) for the JSON log.
        // Gated off by default: this extra unmasked, infinite-distance raycast runs every
        // frame against ALL colliders and needlessly adds CPU/heat on Magic Leap.
        // Redundant once gaze object logging is on — that already records every collider hit.
        if (logUnmaskedEyeHit && !enableGazeObjectLogging &&
            Physics.Raycast(gazePosition, gazeRotation * Vector3.forward, out RaycastHit debugHit, Mathf.Infinity))
        {
            currentHitObjectName = debugHit.collider.name + " (Layer: " + LayerMask.LayerToName(debugHit.collider.gameObject.layer) + ")";
        }

        if (Physics.Raycast(gazePosition, gazeRotation * Vector3.forward, out RaycastHit hitInfo, Mathf.Infinity, layersToIncludeWithRay))
        {
            Transform current = hitInfo.collider.transform;
            MeshRenderer renderer = null;
            while (current != null)
            {
                if (current.CompareTag(targetTag))
                {
                    renderer = current.GetComponentInChildren<MeshRenderer>();
                    break;
                }
                current = current.parent;
            }

            // Fallback just in case
            if (renderer == null)
                renderer = hitInfo.collider.GetComponentInChildren<MeshRenderer>();

            if (renderer != null && targetRenderers != null && targetRenderers.Contains(renderer))
            {
                dwellOverTargetTracker += Time.deltaTime;
                dwellDirectionsDuringCurrentDwell.Add(gazeRotation * Vector3.forward);

                float progress = dwellOverTargetTracker / minDwellTimeOverTarget;
                float fillAmount = ConvertPercentageToRange(progress, renderer);
                renderer.material.SetFloat(fillProgressProperty, fillAmount);
                ClearAllFillings(renderer.gameObject);

                if (dwellOverTargetTracker >= minDwellTimeOverTarget)
                {
                    float currentTime = Time.realtimeSinceStartup;

                    if (isRecording)
                    {
                        float timeSinceStart = currentTime - appStartTime;
                        float timeSincePrev = (destroyCount == 0) ? 0f : (currentTime - previousDestroyTime);

                        if (destroyCount == 0)
                            performanceData.timeToFirstDestroy = timeSinceStart;

                        var head = Camera.main != null ? Camera.main.transform : transform;
                        Vector3 headPos = head.position;
                        Vector3 headEuler = head.rotation.eulerAngles;
                        Vector3 targetPos = renderer.transform.position;
                        float stability = CalculateGazeStability(dwellDirectionsDuringCurrentDwell);

                        destructionEvents.Add(new DestructionEvent
                        {
                            destroyOrder = destroyCount + 1,
                            objectName = renderer.gameObject.name,
                            timeSinceAppStart = timeSinceStart,
                            timeSincePreviousDestroy = timeSincePrev,
                            headPositionAtDestroy = new Vector3Data { x = headPos.x, y = headPos.y, z = headPos.z },
                            headRotationEulerAtDestroy = new Vector3Data { x = headEuler.x, y = headEuler.y, z = headEuler.z },
                            targetPositionAtDestroy = new Vector3Data { x = targetPos.x, y = targetPos.y, z = targetPos.z },
                            target_X = targetPos.x,
                            target_Y = targetPos.y,
                            target_Z = targetPos.z,
                            gazeStabilityDuringDwell_deg = stability
                        });

                        destroyCount++;
                        previousDestroyTime = currentTime;
                    }

                    dwellDirectionsDuringCurrentDwell.Clear();

                    targetRenderers = targetRenderers.Where(r => r != renderer).ToArray();
                    Destroy(renderer.gameObject);
                    dwellOverTargetTracker = 0;

                    if (isRecording)
                    {
                        SaveSessionData();
                    }

                    if (targetRenderers.Length == 0)
                    {
                        if (isRecording && autoSaveWhenAllTargetsDestroyed)
                        {
                            performanceData.totalTimeToComplete = Time.realtimeSinceStartup - appStartTime;
                            performanceData.totalObjectsDestroyed = CountRealDestroys();
                            performanceData.meanInterDestroyInterval = ComputeMeanInterDestroyInterval();

                            SaveSessionData();
                        }
                        
                        // Fire the event to notify TrialManager
                        OnAllTargetsDestroyed?.Invoke();
                    }
                }
            }
            else
            {
                dwellOverTargetTracker = 0;
                dwellDirectionsDuringCurrentDwell.Clear();
                ClearAllFillings();
            }
        }
        else
        {
            dwellOverTargetTracker = 0;
            dwellDirectionsDuringCurrentDwell.Clear();
            ClearAllFillings();
        }
    }

    private float CalculateGazeStability(List<Vector3> directions)
    {
        if (directions.Count < 2) return 0f;
        Vector3 avg = Vector3.zero;
        foreach (var d in directions) avg += d;
        avg.Normalize();

        float sumSq = 0f;
        foreach (var d in directions)
            sumSq += Vector3.Angle(d, avg) * Vector3.Angle(d, avg);
        return Mathf.Sqrt(sumSq / directions.Count);
    }

    private float ConvertPercentageToRange(float percentage, Renderer renderer)
    {
        if (renderer == null) return -1000f; // Safely hide it far below
        float minY = renderer.bounds.min.y;
        float maxY = renderer.bounds.max.y;
        // Add 5% padding so it completely clears/fills at the edges
        float padding = (maxY - minY) * 0.05f;
        return Mathf.Lerp(minY - padding, maxY + padding, percentage);
    }

    private void ClearAllFillings(GameObject exclude = null)
    {
        if (targetRenderers == null) return;
        foreach (var r in targetRenderers)
        {
            if (r != null && r.gameObject != exclude)
            {
                float zero = ConvertPercentageToRange(0, r);
                r.material.SetFloat(fillProgressProperty, zero);
            }
        }
    }

    // ==================== GAZE OBJECT LOGGING ====================

    /// <summary>
    /// Casts the eye ray against every collider on <see cref="gazeLogLayers"/> and accumulates
    /// how long the participant looked at each object — gems, recall objects, and any other
    /// collider in the scene. Feeds both the per-frame NDJSON stream and the JSON summary.
    /// </summary>
    private void SampleGazeObjects()
    {
        gazeSampleFrameCounter++;
        if (gazeLogSampleEveryNFrames > 1 && (gazeSampleFrameCounter % gazeLogSampleEveryNFrames) != 0)
            return;

        float now = Time.realtimeSinceStartup;
        float dt = (lastGazeSampleTime < 0f) ? 0f : now - lastGazeSampleTime;
        // Discard implausible gaps (first sample, app resume, level load) so they don't
        // inflate the dwell totals the percentages are computed from.
        if (dt < 0f || dt > 1f) dt = 0f;
        lastGazeSampleTime = now;

        gazeSamplesTotal++;

        // A stale pose is missing data, not a look. Raycasting it would keep banking dwell time on whatever
        // the last good ray happened to point at -- through every blink, for the whole trial.
        if (!TryGetGazeRay(out Vector3 gazePosition, out Quaternion gazeRotation, out bool isFresh) || !isFresh)
        {
            CloseCurrentLook(now);
            SetCurrentHitFields(null, 0f);
            return;
        }

        gazeSamplesValid++;

        Vector3 gazeDirection = gazeRotation * Vector3.forward;
        gazeTrackedSeconds += dt;

        GazeObjectInfo hitObject = null;
        float hitDistance = 0f;
        if (Physics.Raycast(gazePosition, gazeDirection, out RaycastHit hit, gazeLogMaxDistance, gazeLogLayers))
        {
            hitObject = ResolveGazeObject(hit.collider);
            hitDistance = hit.distance;
            gazeSamplesWithHit++;
        }

        if (hitObject == null)
        {
            gazeOnNothingSeconds += dt;
            CloseCurrentLook(now);
            SetCurrentHitFields(null, 0f);
            return;
        }

        gazeOnObjectSeconds += dt;

        if (currentGazeInfo == null || currentGazeInfo.objectId != hitObject.objectId)
        {
            CloseCurrentLook(now);
            BeginLook(hitObject, now);
        }

        currentLookSampleCount++;
        currentLookDistanceSum += hitDistance;
        if (currentLookDirections.Count < MAX_LOOK_DIRECTION_SAMPLES)
            currentLookDirections.Add(gazeDirection);
        if (hitObject.transform != null)
            currentLookObjectPosition = hitObject.transform.position;

        // Dwell accrues per sample; longestLookSeconds is recorded when the look CLOSES, in CloseCurrentLook.
        // Updating it here recorded 0 for any look that only ever received one sample, because BeginLook
        // stamps currentLookStartTime on that same frame -- which is how a category ended up reporting a
        // longest look of 0.0 alongside a non-zero mean.
        var stat = GetOrCreateStat(hitObject);
        stat.totalDwellSeconds += dt;

        SetCurrentHitFields(hitObject, hitDistance);
    }

    private void SetCurrentHitFields(GazeObjectInfo info, float distance)
    {
        currentHitObjectName = info != null ? info.name : "None";
        currentHitCategory = info != null ? info.category : "None";
        currentHitTag = info != null ? info.objectTag : "None";
        currentHitLayer = info != null ? info.layer : "None";
        currentHitDistance = distance;
    }

    /// <summary>
    /// Works out which logical object a collider belongs to, and how it should be labelled.
    /// Priority: an explicit <see cref="GazeLoggableObject"/> anywhere up the hierarchy (that is
    /// how spawned gems and recall objects identify themselves), then the dwell-destroy tag,
    /// then a plain "Other" fallback so untagged colliders are still counted.
    /// Cached per collider because this runs every sampled frame.
    /// </summary>
    private GazeObjectInfo ResolveGazeObject(Collider col)
    {
        if (col == null) return null;

        int colliderId = col.GetInstanceID();
        if (gazeInfoByColliderId.TryGetValue(colliderId, out var cached) && cached.transform != null)
            return cached;

        // Entries for destroyed objects are never looked up again, so drop the whole cache
        // once it grows past anything a single trial could plausibly need.
        if (gazeInfoByColliderId.Count > 4096) gazeInfoByColliderId.Clear();

        Transform root = col.transform;
        string category = null;
        string name = null;

        var loggable = col.GetComponentInParent<GazeLoggableObject>();
        if (loggable != null)
        {
            root = loggable.transform;
            category = loggable.category;
            name = loggable.ResolvedName;
        }
        else
        {
            // Fall back to the dwell-destroy tag so gems are still classified as targets
            // even if nobody attached a GazeLoggableObject to them.
            Transform t = col.transform;
            while (t != null)
            {
                if (t.CompareTag(targetTag)) { root = t; category = "Target"; break; }
                t = t.parent;
            }
        }

        var info = new GazeObjectInfo
        {
            objectId = root.gameObject.GetInstanceID(),
            name = string.IsNullOrEmpty(name) ? root.gameObject.name : name,
            category = string.IsNullOrEmpty(category) ? "Other" : category,
            objectTag = root.gameObject.tag,
            layer = LayerMask.LayerToName(root.gameObject.layer),
            transform = root
        };

        gazeInfoByColliderId[colliderId] = info;
        return info;
    }

    private GazeObjectStat GetOrCreateStat(GazeObjectInfo info)
    {
        if (gazeStatsByObjectId.TryGetValue(info.objectId, out var stat)) return stat;

        stat = new GazeObjectStat
        {
            objectName = info.name,
            category = info.category,
            objectInstanceId = info.objectId
        };
        gazeStatsByObjectId[info.objectId] = stat;
        return stat;
    }

    private void BeginLook(GazeObjectInfo info, float now)
    {
        currentGazeInfo = info;
        currentLookStartTime = now;
        currentLookSampleCount = 0;
        currentLookDistanceSum = 0f;
        currentLookDirections.Clear();
        currentLookObjectPosition = info.transform != null ? info.transform.position : Vector3.zero;

        gazeLookSegmentCounter++;

        var stat = GetOrCreateStat(info);
        stat.lookCount++;
        if (stat.firstLookTime < 0f)
            stat.firstLookTime = now - appStartTime;
    }

    /// <summary>
    /// Ends the look currently in progress and, if it lasted long enough to be meaningful,
    /// writes it out as a look event. Dwell time itself was already accumulated per sample,
    /// so discarding a short look never loses time from the percentages.
    /// </summary>
    private void CloseCurrentLook(float now)
    {
        if (currentGazeInfo == null) return;

        float duration = Mathf.Max(0f, now - currentLookStartTime);

        // The completed look's real duration -- the only place longestLookSeconds can be measured correctly.
        var closingStat = GetOrCreateStat(currentGazeInfo);
        if (duration > closingStat.longestLookSeconds) closingStat.longestLookSeconds = duration;

        if (duration >= minLookDurationToLogSeconds)
        {
            objectsWithLoggedLookEvents.Add(currentGazeInfo.objectId);
            var ev = new GazeLookEvent
            {
                lookOrder = ++gazeLookEventCounter,
                objectName = currentGazeInfo.name,
                category = currentGazeInfo.category,
                objectTag = currentGazeInfo.objectTag,
                layer = currentGazeInfo.layer,
                objectInstanceId = currentGazeInfo.objectId,
                startTime = currentLookStartTime - appStartTime,
                endTime = now - appStartTime,
                durationSeconds = duration,
                sampleCount = currentLookSampleCount,
                meanDistanceMeters = currentLookSampleCount > 0 ? currentLookDistanceSum / currentLookSampleCount : 0f,
                gazeStability_deg = CalculateGazeStability(currentLookDirections),
                objectPosition = new Vector3Data
                {
                    x = currentLookObjectPosition.x,
                    y = currentLookObjectPosition.y,
                    z = currentLookObjectPosition.z
                }
            };

            // Append-only stream: written once, never rewritten, so it survives a hard kill.
            if (gazeEventWriter != null)
                EnqueueLookEventNdjson(ev);

            // The in-memory copy only exists to be embedded in the summary, which is
            // rewritten in full on every autosave -- hence opt-in, and capped.
            if (includeLookEventsInSummary && gazeLookEvents.Count < maxStoredLookEvents)
                gazeLookEvents.Add(ev);
        }

        currentGazeInfo = null;
        currentLookSampleCount = 0;
        currentLookDistanceSum = 0f;
        currentLookDirections.Clear();
    }

    /// <summary>
    /// Opens the per-trial, append-only look-event stream. Deliberately independent of
    /// <see cref="useEfficientRawLogging"/>: look events are cheap (a handful per second at
    /// most) and this is the only place they are stored crash-safely, since the JSON summary
    /// is rewritten wholesale on every autosave rather than appended to.
    /// </summary>
    private void OpenGazeEventWriter()
    {
        if (!enableGazeObjectLogging || !logGazeEventsToNdjson) return;

        try
        {
            gazeEventsFileName = BuildFileName("gaze_events", "ndjson");
            string path = Path.Combine(persistentDataPath, gazeEventsFileName);

            lock (rawWriterLock)
            {
                gazeEventWriter = new StreamWriter(path, true);
            }

            EnsureRawLoggingThread();
            Debug.Log($"EyeAndHeadTracker: streaming look events to {gazeEventsFileName}");
        }
        catch (Exception ex)
        {
            gazeEventWriter = null;
            gazeEventsFileName = null;
            Debug.LogError($"EyeAndHeadTracker: could not open gaze event stream: {ex.Message}");
        }
    }

    /// <summary>
    /// Drains anything still queued and closes the look-event stream. Draining synchronously
    /// matters here: the background writer would otherwise find a null writer and silently
    /// drop the last few events of a trial.
    /// </summary>
    private void CloseGazeEventWriter()
    {
        if (gazeEventWriter == null) return;

        DrainGazeEventQueue();

        lock (rawWriterLock)
        {
            gazeEventWriter.Flush();
            gazeEventWriter.Close();
            gazeEventWriter = null;
        }
    }

    private void DrainGazeEventQueue()
    {
        while (gazeEventQueue.TryDequeue(out string line))
        {
            lock (rawWriterLock)
            {
                if (gazeEventWriter != null) gazeEventWriter.WriteLine(line);
            }
        }
    }

    private void EnsureRawLoggingThread()
    {
        if (isRawLoggingThreadRunning) return;
        isRawLoggingThreadRunning = true;
        System.Threading.Tasks.Task.Run(RawLoggingThreadLoop);
    }

    private void EnqueueLookEventNdjson(GazeLookEvent ev)
    {
        gazeEventStringBuilder.Clear();
        gazeEventStringBuilder.Append("{\"type\":\"gazeLookEvent\",\"lookOrder\":").Append(ev.lookOrder)
            .Append(",\"timestamp\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv))
            .Append("\",\"objectName\":\"").Append(EscapeJson(ev.objectName))
            .Append("\",\"category\":\"").Append(EscapeJson(ev.category))
            .Append("\",\"objectTag\":\"").Append(EscapeJson(ev.objectTag))
            .Append("\",\"layer\":\"").Append(EscapeJson(ev.layer))
            .Append("\",\"objectInstanceId\":").Append(ev.objectInstanceId)
            .Append(",\"startTime\":").Append(ev.startTime.ToString("F3", Inv))
            .Append(",\"endTime\":").Append(ev.endTime.ToString("F3", Inv))
            .Append(",\"durationSeconds\":").Append(ev.durationSeconds.ToString("F3", Inv))
            .Append(",\"sampleCount\":").Append(ev.sampleCount)
            .Append(",\"meanDistanceMeters\":").Append(ev.meanDistanceMeters.ToString("F3", Inv))
            .Append(",\"gazeStability_deg\":").Append(ev.gazeStability_deg.ToString("F3", Inv))
            .Append(",\"objectPosition\":{\"x\":").Append(ev.objectPosition.x.ToString("F4", Inv))
            .Append(",\"y\":").Append(ev.objectPosition.y.ToString("F4", Inv))
            .Append(",\"z\":").Append(ev.objectPosition.z.ToString("F4", Inv))
            .Append("}}");

        gazeEventQueue.Enqueue(gazeEventStringBuilder.ToString());
    }

    /// <summary>
    /// Builds the "what did they look at, and for what share of the trial" rollup that goes
    /// into the session summary JSON. Percentages are computed here so the JSON is directly
    /// readable without any post-processing.
    /// </summary>
    private GazeAttentionSummary BuildGazeAttentionSummary()
    {
        var summary = new GazeAttentionSummary
        {
            totalTrackedSeconds = gazeTrackedSeconds,
            totalTimeOnObjectsSeconds = gazeOnObjectSeconds,
            totalTimeOnNothingSeconds = gazeOnNothingSeconds,
            percentTimeOnObjects = Percent(gazeOnObjectSeconds, gazeTrackedSeconds),
            percentTimeOnNothing = Percent(gazeOnNothingSeconds, gazeTrackedSeconds),
            totalLookEvents = gazeLookEventCounter,
            totalLookSegments = gazeLookSegmentCounter,
            distinctObjectsLookedAt = gazeStatsByObjectId.Count,
            distinctObjectsInLookEvents = objectsWithLoggedLookEvents.Count
        };

        // Fold the look currently in progress into its object's longest-look, so a summary written mid-trial
        // by an autosave doesn't understate a look that simply hasn't ended yet.
        if (currentGazeInfo != null &&
            gazeStatsByObjectId.TryGetValue(currentGazeInfo.objectId, out var openStat))
        {
            float openDuration = Mathf.Max(0f, Time.realtimeSinceStartup - currentLookStartTime);
            if (openDuration > openStat.longestLookSeconds) openStat.longestLookSeconds = openDuration;
        }

        foreach (var stat in gazeStatsByObjectId.Values)
        {
            summary.perObject.Add(new GazeObjectStat
            {
                objectName = stat.objectName,
                category = stat.category,
                objectInstanceId = stat.objectInstanceId,
                totalDwellSeconds = stat.totalDwellSeconds,
                lookCount = stat.lookCount,
                meanLookDurationSeconds = stat.lookCount > 0 ? stat.totalDwellSeconds / stat.lookCount : 0f,
                longestLookSeconds = stat.longestLookSeconds,
                firstLookTime = stat.firstLookTime,
                percentOfTrackedTime = Percent(stat.totalDwellSeconds, gazeTrackedSeconds),
                percentOfTimeOnObjects = Percent(stat.totalDwellSeconds, gazeOnObjectSeconds)
            });
        }
        summary.perObject.Sort((a, b) => b.totalDwellSeconds.CompareTo(a.totalDwellSeconds));

        // Roll the per-object numbers up per category (Target / RecallObject / Other / custom).
        var byCategory = new Dictionary<string, GazeObjectStat>();
        foreach (var stat in gazeStatsByObjectId.Values)
        {
            if (!byCategory.TryGetValue(stat.category, out var cat))
            {
                cat = new GazeObjectStat { objectName = stat.category, category = stat.category, firstLookTime = -1f };
                byCategory[stat.category] = cat;
            }
            cat.totalDwellSeconds += stat.totalDwellSeconds;
            cat.lookCount += stat.lookCount;
            if (stat.longestLookSeconds > cat.longestLookSeconds) cat.longestLookSeconds = stat.longestLookSeconds;
            if (stat.firstLookTime >= 0f && (cat.firstLookTime < 0f || stat.firstLookTime < cat.firstLookTime))
                cat.firstLookTime = stat.firstLookTime;
        }

        foreach (var cat in byCategory.Values)
        {
            cat.meanLookDurationSeconds = cat.lookCount > 0 ? cat.totalDwellSeconds / cat.lookCount : 0f;
            cat.percentOfTrackedTime = Percent(cat.totalDwellSeconds, gazeTrackedSeconds);
            cat.percentOfTimeOnObjects = Percent(cat.totalDwellSeconds, gazeOnObjectSeconds);
            summary.perCategory.Add(cat);
        }
        summary.perCategory.Sort((a, b) => b.totalDwellSeconds.CompareTo(a.totalDwellSeconds));

        return summary;
    }

    private static float Percent(float part, float whole) => whole > 0f ? (part / whole) * 100f : 0f;

    /// <summary>
    /// Computes the preregistered primary and secondary outcomes. Everything here is derived from the
    /// time-weighted dwell totals (sums of per-sample dt), never from frame counts, so a condition that
    /// costs frame rate cannot move the outcome by itself.
    /// </summary>
    private PrimaryOutcomeData BuildPrimaryOutcome()
    {
        float otherSeconds = 0f, targetSeconds = 0f, recallSeconds = 0f, mapSeconds = 0f;
        int distinctOther = 0;

        foreach (var stat in gazeStatsByObjectId.Values)
        {
            switch (stat.category)
            {
                case "Target":
                    targetSeconds += stat.totalDwellSeconds;
                    break;
                case "RecallObject":
                    recallSeconds += stat.totalDwellSeconds;
                    break;
                case MapCategory:
                    // Carved out explicitly rather than left to the default arm. The map is a UI panel
                    // the participant consults, not scenery they noticed, and it is a single object with
                    // very long dwells -- it swamped the primary DV in P001.
                    mapSeconds += stat.totalDwellSeconds;
                    break;
                default: // "Other" and any other custom category: non-target scene geometry
                    otherSeconds += stat.totalDwellSeconds;
                    if (stat.totalDwellSeconds > 0f) distinctOther++;
                    break;
            }
        }

        float trackedMinutes = gazeTrackedSeconds / 60f;

        return new PrimaryOutcomeData
        {
            proportionTimeOnOtherScenery = gazeTrackedSeconds > 0f ? otherSeconds / gazeTrackedSeconds : 0f,
            percentTimeOnOtherScenery = Percent(otherSeconds, gazeTrackedSeconds),
            secondsOnOtherScenery = otherSeconds,
            trackedSeconds = gazeTrackedSeconds,
            distinctOtherObjectsFixated = distinctOther,
            distinctOtherObjectsPerMinute = trackedMinutes > 0f ? distinctOther / trackedMinutes : 0f,
            percentTimeOnTargets = Percent(targetSeconds, gazeTrackedSeconds),
            percentTimeOnRecallObjects = Percent(recallSeconds, gazeTrackedSeconds),
            secondsOnMap = mapSeconds,
            percentTimeOnMap = Percent(mapSeconds, gazeTrackedSeconds)
        };
    }

    /// <summary>
    /// Builds the data-quality block the exclusion criterion is screened on. Nothing here is cosmetic:
    /// with no valid-sample percentage there is no objective, preregisterable basis for dropping a trial.
    /// </summary>
    private DataQualityInfo BuildDataQualityInfo()
    {
        float meanIntervalMs = frameIntervalCount > 0 ? frameIntervalSumMs / frameIntervalCount : 0f;

        return new DataQualityInfo
        {
            estimatedValidGazeSamplesPercent = Percent(gazeSamplesValid, gazeSamplesTotal),
            validGazeSamples = gazeSamplesValid,
            totalGazeSamples = gazeSamplesTotal,
            gazeRaycastHitPercent = Percent(gazeSamplesWithHit, gazeSamplesValid),
            measuredFrameRateHz = meanIntervalMs > 0f ? 1000f / meanIntervalMs : 0f,
            meanFrameIntervalMs = meanIntervalMs,
            longestFrameIntervalMs = frameIntervalMaxMs,
            framesOver50ms = framesOver50ms,
            percentFramesOver50ms = Percent(framesOver50ms, frameIntervalCount),
            notes = BuildDataQualityNotes()
        };
    }

    /// <summary>
    /// Flags the failure modes that otherwise pass silently -- a trial with no gaze, a ray that never hits
    /// anything, a denied permission -- so they are visible in the file itself rather than only in a log.
    /// </summary>
    private string BuildDataQualityNotes()
    {
        var notes = new List<string>();

        if (gazeSamplesTotal == 0)
        {
            notes.Add("No gaze samples recorded.");
        }
        else
        {
            float validPercent = Percent(gazeSamplesValid, gazeSamplesTotal);
            if (validPercent < gazeValidityScreeningPercent)
                notes.Add($"Valid gaze {validPercent:F1}% is below the {gazeValidityScreeningPercent:F0}% screening threshold.");
            if (gazeSamplesValid > 0 && Percent(gazeSamplesWithHit, gazeSamplesValid) < 1f)
                notes.Add("Gaze ray almost never hit a collider; check the ray transform and gazeLogLayers.");
        }

        if (GazeInputManager.Instance == null)
            notes.Add("GazeInputManager missing from the scene.");
        else if (!GazeInputManager.Instance.EyeTrackingPermissionGranted)
            notes.Add("Eye tracking permission not granted.");

        return notes.Count == 0 ? "OK" : string.Join(" ", notes);
    }

    /// <summary>
    /// Accumulates frame-interval statistics for the trial. Reported because gaze is sampled on the render
    /// loop: if a condition costs frame rate it also costs gaze samples, and that difference would otherwise
    /// be invisible while sitting directly on the primary outcome.
    /// </summary>
    private void TrackFramePacing()
    {
        float ms = Time.unscaledDeltaTime * 1000f;
        if (ms <= 0f || ms > 1000f) return; // ignore the first frame and scene-load hitches

        frameIntervalCount++;
        frameIntervalSumMs += ms;
        if (ms > frameIntervalMaxMs) frameIntervalMaxMs = ms;
        if (ms > 50f) framesOver50ms++;
    }

    /// <summary>
    /// Gems destroyed this trial. Markers live in the same list with destroyOrder -1, so counting the list
    /// itself is what made every completed trial report 22 destructions for 20 gems.
    /// </summary>
    private int CountRealDestroys()
    {
        int n = 0;
        foreach (var e in destructionEvents)
            if (e.destroyOrder > 0) n++;
        return n;
    }

    /// <summary>
    /// Mean gap between consecutive gem destructions. Excludes markers, and excludes the first destroy --
    /// its timeSincePreviousDestroy is 0 by definition, not a real interval. With 20 gems this averages the
    /// 19 real intervals; the old code averaged 21 values, two of which were markers sitting at zero, which
    /// pulled the reported mean about 10% low on every trial.
    /// </summary>
    private float ComputeMeanInterDestroyInterval()
    {
        float sum = 0f;
        int n = 0;
        foreach (var e in destructionEvents)
        {
            if (e.destroyOrder <= 1) continue; // skip markers (-1) and the first destroy (1)
            sum += e.timeSincePreviousDestroy;
            n++;
        }
        return n > 0 ? sum / n : 0f;
    }

    private void ResetGazeLogging()
    {
        CloseCurrentLook(Time.realtimeSinceStartup);
        gazeInfoByColliderId.Clear();
        gazeStatsByObjectId.Clear();
        gazeLookEvents.Clear();
        currentLookDirections.Clear();
        currentGazeInfo = null;
        objectsWithLoggedLookEvents.Clear();
        gazeTrackedSeconds = 0f;
        gazeOnObjectSeconds = 0f;
        gazeOnNothingSeconds = 0f;
        lastGazeSampleTime = -1f;
        gazeSampleFrameCounter = 0;
        gazeLookEventCounter = 0;
        gazeLookSegmentCounter = 0;

        gazeSamplesTotal = 0;
        gazeSamplesValid = 0;
        gazeSamplesWithHit = 0;

        frameIntervalCount = 0;
        frameIntervalSumMs = 0f;
        frameIntervalMaxMs = 0f;
        framesOver50ms = 0;

        SetCurrentHitFields(null, 0f);
    }

    /// <summary>Minimal JSON string escaping for object names written into the NDJSON stream.</summary>
    private static string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.IndexOf('"') < 0 && value.IndexOf('\\') < 0 && value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0 && value.IndexOf('\t') < 0)
            return value;

        return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    // ==================== SAVE ====================

    public void LogMarker(string message)
    {
        // We will hijack the DestructionEvent list to store our text markers 
        // so they show up clearly in your final JSON without changing your data structure!
        destructionEvents.Add(new DestructionEvent
        {
            destroyOrder = -1, // -1 indicates this is a text marker, not a real target
            objectName = $"MARKER: {message}",
            timeSinceAppStart = Time.realtimeSinceStartup - appStartTime,
            notes = message
        });
        Debug.Log($"JSON MARKER: {message}");
    }

    public void SaveSessionData() => SaveSessionData(false);

    /// <summary>
    /// Writes the JSON summary. <paramref name="synchronous"/> is used only by
    /// <see cref="FinalizeTrialFiles"/>, which must not leave a write in flight while it renames files.
    /// </summary>
    private void SaveSessionData(bool synchronous)
    {
        // Once a set is finalized its files are closed and renamed, so the routine autosave callers
        // (OnDestroy, OnApplicationQuit, the next trial's StartNewTrialRecording) must not write again --
        // they would re-emit a summary for a set that is already sealed.
        if (trialFilesFinalized && !synchronous) return;

        try
        {
            // Finalize numbers (MAIN THREAD)
            if (destructionEvents.Count > 0)
            {
                // Both of these must filter out the markers that share this list -- see the helpers.
                performanceData.totalObjectsDestroyed = CountRealDestroys();
                performanceData.totalTimeToComplete = Time.realtimeSinceStartup - appStartTime;
                performanceData.meanInterDestroyInterval = ComputeMeanInterDestroyInterval();
            }

            var dataQuality = BuildDataQualityInfo();

            // Copy data for background thread to prevent race conditions
            var metaCopy = new SessionMeta
            {
                sessionId = sessionMeta.sessionId,
                participantId = sessionMeta.participantId,
                condition = sessionMeta.condition,
                device = sessionMeta.device,
                environment = sessionMeta.environment,
                measuredSampleRateHz = dataQuality.measuredFrameRateHz,
                targetFrameRateSetting = Application.targetFrameRate,
                eyeDataSource = sessionMeta.eyeDataSource
            };

            var perfCopy = new SessionPerformanceData
            {
                timeToFirstDestroy = performanceData.timeToFirstDestroy,
                totalTimeToComplete = performanceData.totalTimeToComplete,
                totalObjectsDestroyed = performanceData.totalObjectsDestroyed,
                meanInterDestroyInterval = performanceData.meanInterDestroyInterval,
                dataQuality = dataQuality
            };

            var destructionCopy = new List<DestructionEvent>(destructionEvents);
            var rawFileRef = useEfficientRawLogging
                ? BuildFileName("raw_eye_head_tracking", "ndjson") : null;

            string summaryPath = Path.Combine(persistentDataPath, BuildFileName("gaze_session_summary", "json"));

            // 1. Save Summary (Main Thread serialization)
            var root = new SessionSummaryRoot
            {
                metadata = metaCopy,
                sessionData = perfCopy,
                primaryOutcome = BuildPrimaryOutcome(),
                destructionEvents = destructionCopy,
                gazeAttention = BuildGazeAttentionSummary(),
                gazeLookEvents = includeLookEventsInSummary
                    ? new List<GazeLookEvent>(gazeLookEvents)
                    : new List<GazeLookEvent>(),
                rawTrackingFileReference = rawFileRef,
                gazeEventsFileReference = gazeEventsFileName
            };
            string json = JsonUtility.ToJson(root, true);

            // The finalize path writes on the calling thread: it is about to rename these files, so it
            // cannot leave the write racing behind it on the thread pool.
            if (synchronous)
            {
                WriteTextAtomically(summaryPath, json);
                Debug.Log($"✅ Summary saved (final): {summaryPath}");
                return;
            }

            // Fire and forget background thread. Kept in _summaryWriteTask so FinalizeTrialFiles can wait.
            _summaryWriteTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    WriteTextAtomically(summaryPath, json);
                    Debug.Log($"✅ Summary saved in background: {summaryPath}");

                    if (rawNdjsonWriter != null)
                    {
                        lock (rawWriterLock)
                        {
                            rawNdjsonWriter.Flush();
                        }
                    }

                    // Push the look-event stream to disk on the same cadence, so a hard
                    // kill loses at most one autosave interval of events.
                    if (gazeEventWriter != null)
                    {
                        lock (rawWriterLock)
                        {
                            gazeEventWriter.Flush();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Background save failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Debug.LogError($"Save failed: {ex.Message}");
            Debug.LogError(ex);
        }
    }

    public void ForceSaveNow() => SaveSessionData();

    private static readonly object _fileLock = new object();

    private void WriteTextAtomically(string path, string content)
    {
        lock (_fileLock)
        {
            File.WriteAllText(path, content);
        }
    }

    private void OnDestroy()
    {
        CloseCurrentLook(Time.realtimeSinceStartup);
        SaveSessionData();

        // Close the event stream before stopping the writer thread, so the drain happens
        // while the writer is still open.
        CloseGazeEventWriter();

        isRawLoggingThreadRunning = false;

        if (rawNdjsonWriter != null)
        {
            lock (rawWriterLock)
            {
                rawNdjsonWriter.Flush();
                rawNdjsonWriter.Close();
                rawNdjsonWriter = null;
            }
        }
    }

    private void RawLoggingThreadLoop()
    {
        while (isRawLoggingThreadRunning)
        {
            if (rawLogQueue.TryDequeue(out string logLine))
            {
                lock (rawWriterLock)
                {
                    if (rawNdjsonWriter != null)
                    {
                        rawNdjsonWriter.WriteLine(logLine);
                    }
                }
            }
            else if (gazeEventQueue.TryDequeue(out string eventLine))
            {
                lock (rawWriterLock)
                {
                    if (gazeEventWriter != null)
                    {
                        gazeEventWriter.WriteLine(eventLine);
                    }
                }
            }
            else
            {
                Thread.Sleep(5); // Prevent 100% CPU usage
            }
        }

        // Drain both queues before exiting
        while (rawLogQueue.TryDequeue(out string logLine))
        {
            lock (rawWriterLock)
            {
                if (rawNdjsonWriter != null)
                {
                    rawNdjsonWriter.WriteLine(logLine);
                }
            }
        }

        while (gazeEventQueue.TryDequeue(out string eventLine))
        {
            lock (rawWriterLock)
            {
                if (gazeEventWriter != null)
                {
                    gazeEventWriter.WriteLine(eventLine);
                }
            }
        }
    }
}