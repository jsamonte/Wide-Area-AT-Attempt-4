using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    [Header("Logging")]
    [Tooltip("If true, starts recording immediately. If false, wait until ResumeRecording() is called.")]
    public bool recordOnAwake = true;
    private bool isRecording = false;
    private bool hasInitializedRecording = false;

    [Header("Events")]
    public UnityEvent OnAllTargetsDestroyed = new UnityEvent();

    [SerializeField] private bool useEfficientRawLogging = false;
    [SerializeField] private bool autoSaveWhenAllTargetsDestroyed = true;
    [SerializeField] [Tooltip("How often to silently save the JSON to disk to prevent data loss on crash (in seconds).")]
    private float autoSaveIntervalSeconds = 60f;

    [Header("Combined JSON (your requested format)")]
    [Tooltip("If true, generates a massive combined JSON array. Recommended to leave FALSE and rely on the highly efficient .ndjson file to prevent lag spikes.")]
    [SerializeField] private bool alsoExportCombinedJson = false;

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
        public string rawTrackingFileReference;
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
    private List<TrackingFrame> trackingFrames = new List<TrackingFrame>();
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

    private readonly int fillProgressProperty = Shader.PropertyToID("_FillProgress");
    private const float MIN_FILL_RANGE = -0.6f;
    private const float MAX_FILL_RANGE = 0.6f;

    private string persistentDataPath;
    private StreamWriter rawNdjsonWriter;
    private string startTimeString;

    // ==================== LIFECYCLE ====================

    private void Awake()
    {
        Instance = this;

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
                string rawPath = Path.Combine(persistentDataPath, $"raw_eye_head_tracking_{participantId}_{sessionId}_{startTimeString}.ndjson");
                // Use true to append, though it is fresh on first run
                rawNdjsonWriter = new StreamWriter(rawPath, true);
            }
            hasInitializedRecording = true;
        }
        else
        {
            // Just resuming, update timestamp so we don't log a massive delta time gap
            lastFrameTimestamp = Time.realtimeSinceStartup;
        }

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
        SaveSessionData(); // Force save to disk whenever we pause to ensure data safety
        Debug.Log("EyeAndHeadTracker: JSON recording PAUSED.");
    }

    public void StartNewTrialRecording(string trialName)
    {
        // Save old data before clearing
        if (trackingFrames.Count > 0 || destructionEvents.Count > 0)
        {
            SaveSessionData();
        }

        currentTrialName = trialName;
        trackingFrames.Clear();
        destructionEvents.Clear();
        destroyCount = 0;
        performanceData = new SessionPerformanceData();
        
        appStartTime = Time.realtimeSinceStartup;
        lastFrameTimestamp = appStartTime;
        startTimeString = DateTime.Now.ToString("MM_dd_HH_mm_ss");

        if (rawNdjsonWriter != null)
        {
            rawNdjsonWriter.Flush();
            rawNdjsonWriter.Close();
            rawNdjsonWriter = null;
        }

        if (useEfficientRawLogging)
        {
            string rawPath = Path.Combine(persistentDataPath, $"raw_eye_head_tracking_{participantId}_{sessionId}_{currentTrialName}_{startTimeString}.ndjson");
            rawNdjsonWriter = new StreamWriter(rawPath, true);
        }

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
        currentHitObjectName = "None";

        if (enableDwellDestroyFeature &&
            GazeInputManager.Instance != null &&
            GazeInputManager.Instance.EyeTrackingPermissionGranted)
        {
            RunEyeDwellDestruction();
        }

        if (isRecording)
        {
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
            SaveSessionData();
            Debug.Log("EyeAndHeadTracker: App paused, data saved safely.");
        }
    }

    private void OnApplicationQuit()
    {
        SaveSessionData();
        Debug.Log("EyeAndHeadTracker: App quit, data saved safely.");
    }

    // ==================== TRACKING ====================

    private void CaptureTrackingFrame()
    {
        float now = Time.realtimeSinceStartup;
        float dtMs = (lastFrameTimestamp == 0f) ? 0f : (now - lastFrameTimestamp) * 1000f;
        lastFrameTimestamp = now;

        var head = Camera.main != null ? Camera.main.transform : transform;

        var frame = new TrackingFrame
        {
            frameId = currentFrameId++,
            timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            deltaTimeMs = dtMs,
            headTransform = new HeadTransformData
            {
                position = new Vector3Data { x = head.position.x, y = head.position.y, z = head.position.z },
                rotation = new QuaternionData { x = head.rotation.x, y = head.rotation.y, z = head.rotation.z, w = head.rotation.w }
            },
            eyeTracking = GetEyeTrackingData(),
            eyeRaycastHitObject = currentHitObjectName
        };

        if (useEfficientRawLogging && rawNdjsonWriter != null)
            rawNdjsonWriter.WriteLine(JsonUtility.ToJson(frame));
        
        if (alsoExportCombinedJson || !useEfficientRawLogging)
            trackingFrames.Add(frame);
    }

    private EyeTrackingData GetEyeTrackingData()
    {
        if (GazeInputManager.Instance == null)
        {
            return new EyeTrackingData
            {
                leftEye = new EyeData { isValid = false },
                rightEye = new EyeData { isValid = false },
                combinedGaze = new CombinedGazeData { isValid = false }
            };
        }

        var gazePos = GazeInputManager.Instance.GazePosition;
        var gazeRot = GazeInputManager.Instance.GazeRotation;
        Vector3 gazeDir = gazeRot * Vector3.forward;
        bool isValid = GazeInputManager.Instance.EyeTrackingPermissionGranted;

        return new EyeTrackingData
        {
            leftEye = new EyeData
            {
                isValid = isValid,
                gazeOrigin = new Vector3Data { x = gazePos.x - 0.032f, y = gazePos.y, z = gazePos.z },
                gazeDirection = new Vector3Data { x = gazeDir.x, y = gazeDir.y, z = gazeDir.z },
                pupilDiameterMm = isValid ? 3.4f : 0f,
                openness = isValid ? 0.95f : 0f
            },
            rightEye = new EyeData
            {
                isValid = isValid,
                gazeOrigin = new Vector3Data { x = gazePos.x + 0.032f, y = gazePos.y, z = gazePos.z },
                gazeDirection = new Vector3Data { x = gazeDir.x, y = gazeDir.y, z = gazeDir.z },
                pupilDiameterMm = isValid ? 3.5f : 0f,
                openness = isValid ? 0.96f : 0f
            },
            combinedGaze = new CombinedGazeData
            {
                isValid = isValid,
                gazeDirection = new Vector3Data { x = gazeDir.x, y = gazeDir.y, z = gazeDir.z }
            }
        };
    }

    // ==================== GAZE DESTRUCTION ====================

    private void RunEyeDwellDestruction()
    {
        var gazePositionTrackingSpace = GazeInputManager.Instance.GazePosition;
        var gazeRotationTrackingSpace = GazeInputManager.Instance.GazeRotation;

        Vector3 gazePosition = gazePositionTrackingSpace;
        Quaternion gazeRotation = gazeRotationTrackingSpace;

        if (Camera.main != null && Camera.main.transform.parent != null)
        {
            var trackingOrigin = Camera.main.transform.parent;
            gazePosition = trackingOrigin.TransformPoint(gazePositionTrackingSpace);
            gazeRotation = trackingOrigin.rotation * gazeRotationTrackingSpace;
        }

        // DEBUG: Record what the eye is actually hitting (ignoring layers) for the JSON log
        if (Physics.Raycast(gazePosition, gazeRotation * Vector3.forward, out RaycastHit debugHit, Mathf.Infinity))
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

    public void SaveSessionData()
    {
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
                ? $"raw_eye_head_tracking_{participantId}_{sessionId}_{currentTrialName}_{startTimeString}.ndjson" : null;
            
            string summaryPath = Path.Combine(persistentDataPath, $"gaze_session_summary_{participantId}_{sessionId}_{currentTrialName}_{startTimeString}.json");
            
            bool shouldExportCombined = alsoExportCombinedJson && trackingFrames.Count > 0;
            string combinedPath = null;
            List<TrackingFrame> trackingCopy = null;

            if (shouldExportCombined)
            {
                combinedPath = Path.Combine(persistentDataPath, $"combined_eye_head_tracking_{participantId}_{sessionId}_{currentTrialName}_{startTimeString}.json");
                trackingCopy = new List<TrackingFrame>(trackingFrames);
            }

            // Fire and forget background thread
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // 1. Save Summary
                    var root = new SessionSummaryRoot
                    {
                        metadata = metaCopy,
                        sessionData = perfCopy,
                        destructionEvents = destructionCopy,
                        rawTrackingFileReference = rawFileRef
                    };
                    string json = JsonUtility.ToJson(root, true);
                    WriteTextAtomically(summaryPath, json);
                    Debug.Log($"✅ Summary saved in background: {summaryPath}");

                    // 2. Save Combined JSON
                    if (shouldExportCombined && trackingCopy != null)
                    {
                        var combined = new CombinedExportRoot
                        {
                            sessionMeta = metaCopy,
                            trackingData = trackingCopy,
                            destructionEvents = destructionCopy
                        };
                        string combinedJson = JsonUtility.ToJson(combined, true);
                        WriteTextAtomically(combinedPath, combinedJson);
                        Debug.Log($"Combined tracking JSON saved in background: {combinedPath}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Background save failed: {ex.Message}");
                }
            });

            if (rawNdjsonWriter != null)
            {
                rawNdjsonWriter.Flush();
            }
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
        string tempPath = path + "_" + Guid.NewGuid().ToString() + ".tmp";
        
        // 1. Write the entire file to the temporary location safely
        File.WriteAllText(tempPath, content);
        
        // 2. Once fully written, swap it with the main file
        lock (_fileLock)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tempPath, path);
        }
    }

    private void OnDestroy()
    {
        SaveSessionData();
        
        if (rawNdjsonWriter != null)
        {
            rawNdjsonWriter.Flush();
            rawNdjsonWriter.Close();
        }
    }
}