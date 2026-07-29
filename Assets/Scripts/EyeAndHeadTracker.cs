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
        public int samplingRateHz = 90;
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
        public int totalLookEvents;
        public int distinctObjectsLookedAt;
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
        public float estimatedValidGazeSamplesPercent;
        public string notes;
    }

    [System.Serializable]
    public class SessionSummaryRoot
    {
        public SessionMeta metadata;
        public SessionPerformanceData sessionData;
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
        Vector3 gazeDir = gazeRot * Vector3.forward;
        bool isValid = GazeInputManager.Instance.EyeTrackingPermissionGranted;

        data.leftEye.isValid = isValid;
        data.leftEye.gazeOrigin.x = gazePos.x - 0.032f;
        data.leftEye.gazeOrigin.y = gazePos.y;
        data.leftEye.gazeOrigin.z = gazePos.z;
        data.leftEye.gazeDirection.x = gazeDir.x;
        data.leftEye.gazeDirection.y = gazeDir.y;
        data.leftEye.gazeDirection.z = gazeDir.z;
        data.leftEye.pupilDiameterMm = isValid ? 3.4f : 0f;
        data.leftEye.openness = isValid ? 0.95f : 0f;

        data.rightEye.isValid = isValid;
        data.rightEye.gazeOrigin.x = gazePos.x + 0.032f;
        data.rightEye.gazeOrigin.y = gazePos.y;
        data.rightEye.gazeOrigin.z = gazePos.z;
        data.rightEye.gazeDirection.x = gazeDir.x;
        data.rightEye.gazeDirection.y = gazeDir.y;
        data.rightEye.gazeDirection.z = gazeDir.z;
        data.rightEye.pupilDiameterMm = isValid ? 3.5f : 0f;
        data.rightEye.openness = isValid ? 0.96f : 0f;

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
    {
        gazePosition = Vector3.zero;
        gazeRotation = Quaternion.identity;

        if (GazeInputManager.Instance == null || !GazeInputManager.Instance.EyeTrackingPermissionGranted)
            return false;

        gazePosition = GazeInputManager.Instance.GazePosition;
        gazeRotation = GazeInputManager.Instance.GazeRotation;

        if (Camera.main != null && Camera.main.transform.parent != null)
        {
            var trackingOrigin = Camera.main.transform.parent;
            gazePosition = trackingOrigin.TransformPoint(gazePosition);
            gazeRotation = trackingOrigin.rotation * gazeRotation;
        }

        return true;
    }

    private void RunEyeDwellDestruction()
    {
        if (!TryGetGazeRay(out Vector3 gazePosition, out Quaternion gazeRotation))
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
                            performanceData.totalObjectsDestroyed = destroyCount;
                            performanceData.meanInterDestroyInterval = destructionEvents.Count > 1 
                                ? destructionEvents.Skip(1).Average(e => e.timeSincePreviousDestroy) : 0f;

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

        if (!TryGetGazeRay(out Vector3 gazePosition, out Quaternion gazeRotation))
        {
            CloseCurrentLook(now);
            SetCurrentHitFields(null, 0f);
            return;
        }

        Vector3 gazeDirection = gazeRotation * Vector3.forward;
        gazeTrackedSeconds += dt;

        GazeObjectInfo hitObject = null;
        float hitDistance = 0f;
        if (Physics.Raycast(gazePosition, gazeDirection, out RaycastHit hit, gazeLogMaxDistance, gazeLogLayers))
        {
            hitObject = ResolveGazeObject(hit.collider);
            hitDistance = hit.distance;
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

        var stat = GetOrCreateStat(hitObject);
        stat.totalDwellSeconds += dt;
        float currentLookDuration = now - currentLookStartTime;
        if (currentLookDuration > stat.longestLookSeconds)
            stat.longestLookSeconds = currentLookDuration;

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

        if (duration >= minLookDurationToLogSeconds)
        {
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
            distinctObjectsLookedAt = gazeStatsByObjectId.Count
        };

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

    private void ResetGazeLogging()
    {
        CloseCurrentLook(Time.realtimeSinceStartup);
        gazeInfoByColliderId.Clear();
        gazeStatsByObjectId.Clear();
        gazeLookEvents.Clear();
        currentLookDirections.Clear();
        currentGazeInfo = null;
        gazeTrackedSeconds = 0f;
        gazeOnObjectSeconds = 0f;
        gazeOnNothingSeconds = 0f;
        lastGazeSampleTime = -1f;
        gazeSampleFrameCounter = 0;
        gazeLookEventCounter = 0;
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
                performanceData.totalObjectsDestroyed = destructionEvents.Count;
                performanceData.totalTimeToComplete = Time.realtimeSinceStartup - appStartTime;
                performanceData.meanInterDestroyInterval = destructionEvents.Count > 1 
                    ? destructionEvents.Skip(1).Average(e => e.timeSincePreviousDestroy) : 0f;
            }

            // Copy data for background thread to prevent race conditions
            var metaCopy = new SessionMeta
            {
                sessionId = sessionMeta.sessionId,
                participantId = sessionMeta.participantId,
                condition = sessionMeta.condition,
                device = sessionMeta.device,
                environment = sessionMeta.environment,
                samplingRateHz = sessionMeta.samplingRateHz
            };

            var perfCopy = new SessionPerformanceData
            {
                timeToFirstDestroy = performanceData.timeToFirstDestroy,
                totalTimeToComplete = performanceData.totalTimeToComplete,
                totalObjectsDestroyed = performanceData.totalObjectsDestroyed,
                meanInterDestroyInterval = performanceData.meanInterDestroyInterval,
                dataQuality = performanceData.dataQuality
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