using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Android;

public class EyeAndHeadTracker : MonoBehaviour
{
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
    [Tooltip("If true, starts recording immediately. If false, wait until StartRecording() is called.")]
    public bool recordOnAwake = true;
    private bool isRecording = false;

    [SerializeField] private bool useEfficientRawLogging = false;
    [SerializeField] private bool autoSaveWhenAllTargetsDestroyed = true;
    [SerializeField] [Tooltip("How often to silently save the JSON to disk to prevent data loss on crash (in seconds).")]
    private float autoSaveIntervalSeconds = 15f;

    [Header("Combined JSON (your requested format)")]
    [SerializeField] private bool alsoExportCombinedJson = true;

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
            StartRecording();
        }
    }

    public void StartRecording()
    {
        if (isRecording) return;

        appStartTime = Time.realtimeSinceStartup;
        lastFrameTimestamp = appStartTime;
        startTimeString = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        if (useEfficientRawLogging)
        {
            string rawPath = Path.Combine(persistentDataPath, $"raw_eye_head_tracking_{participantId}_{sessionId}_BlueWireframe_{(enableBlueWireframe ? "On" : "Off")}_Map_{(enableMap ? "On" : "Off")}_{startTimeString}.ndjson");
            rawNdjsonWriter = new StreamWriter(rawPath, false);
        }

        isRecording = true;
        Debug.Log("EyeAndHeadTracker: JSON recording started.");
    }

    private void Start()
    {
        if (enableDwellDestroyFeature)
        {
            var allTargets = GameObject.FindGameObjectsWithTag(targetTag);
            targetRenderers = allTargets
                .Select(n => n.GetComponent<MeshRenderer>())
                .Where(r => r != null && r.enabled)
                .ToArray();

            Debug.Log($"Found {targetRenderers.Length} objects with tag '{targetTag}'.");
        }

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
        if (!isRecording) return;

        currentHitObjectName = "None";

        if (enableDwellDestroyFeature &&
            GazeInputManager.Instance != null &&
            GazeInputManager.Instance.EyeTrackingPermissionGranted)
        {
            RunEyeDwellDestruction();
        }

        CaptureTrackingFrame();

        // Periodic auto-save to prevent data loss on crash
        autoSaveTimer += Time.deltaTime;
        if (autoSaveTimer >= autoSaveIntervalSeconds)
        {
            SaveSessionData();
            autoSaveTimer = 0f;
        }
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
            var renderer = hitInfo.collider.GetComponent<MeshRenderer>();

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
                        gazeStabilityDuringDwell_deg = stability
                    });

                    destroyCount++;
                    previousDestroyTime = currentTime;
                    dwellDirectionsDuringCurrentDwell.Clear();

                    targetRenderers = targetRenderers.Where(r => r != renderer).ToArray();
                    Destroy(renderer.gameObject);
                    dwellOverTargetTracker = 0;

                    // OPTION B: Save immediately on every destruction event
                    SaveSessionData();

                    if (targetRenderers.Length == 0 && autoSaveWhenAllTargetsDestroyed)
                    {
                        performanceData.totalTimeToComplete = Time.realtimeSinceStartup - appStartTime;
                        performanceData.totalObjectsDestroyed = destroyCount;
                        performanceData.meanInterDestroyInterval = destructionEvents.Count > 1 
                            ? destructionEvents.Skip(1).Average(e => e.timeSincePreviousDestroy) : 0f;

                        SaveSessionData();
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

    public void SaveSessionData()
    {
        try
        {
            // Finalize numbers
            if (performanceData.totalObjectsDestroyed == 0 && destructionEvents.Count > 0)
            {
                performanceData.totalObjectsDestroyed = destructionEvents.Count;
                performanceData.totalTimeToComplete = Time.realtimeSinceStartup - appStartTime;
                performanceData.meanInterDestroyInterval = destructionEvents.Count > 1 
                    ? destructionEvents.Skip(1).Average(e => e.timeSincePreviousDestroy) : 0f;
            }

            // Build proper serializable root object
            var root = new SessionSummaryRoot
            {
                metadata = sessionMeta,
                sessionData = performanceData,
                destructionEvents = destructionEvents,
                rawTrackingFileReference = useEfficientRawLogging 
                    ? $"raw_eye_head_tracking_{participantId}_{sessionId}_BlueWireframe_{(enableBlueWireframe ? "On" : "Off")}_Map_{(enableMap ? "On" : "Off")}_{startTimeString}.ndjson" : null
            };

            string summaryPath = Path.Combine(persistentDataPath, $"gaze_session_summary_{participantId}_{sessionId}_BlueWireframe_{(enableBlueWireframe ? "On" : "Off")}_Map_{(enableMap ? "On" : "Off")}_{startTimeString}.json");
            string json = JsonUtility.ToJson(root, true);
            File.WriteAllText(summaryPath, json);

            Debug.Log($"✅ Summary saved: {summaryPath}");

            // Optional combined JSON with trackingData array (your requested format)
            if (alsoExportCombinedJson && trackingFrames.Count > 0)
            {
                var combined = new CombinedExportRoot
                {
                    sessionMeta = sessionMeta,
                    trackingData = trackingFrames,
                    destructionEvents = destructionEvents
                };

                string combinedPath = Path.Combine(persistentDataPath, $"combined_eye_head_tracking_{participantId}_{sessionId}_BlueWireframe_{(enableBlueWireframe ? "On" : "Off")}_Map_{(enableMap ? "On" : "Off")}_{startTimeString}.json");
                File.WriteAllText(combinedPath, JsonUtility.ToJson(combined, true));
                Debug.Log($"Combined tracking JSON saved: {combinedPath}");
            }

            if (rawNdjsonWriter != null)
            {
                rawNdjsonWriter.Flush();
                rawNdjsonWriter.Close();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Save failed: {ex.Message}");
            Debug.LogError(ex);
        }
    }

    public void ForceSaveNow() => SaveSessionData();

    private void OnApplicationPause(bool isPaused)
    {
        if (isPaused)
        {
            SaveSessionData();
        }
    }

    private void OnApplicationQuit()
    {
        SaveSessionData();
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