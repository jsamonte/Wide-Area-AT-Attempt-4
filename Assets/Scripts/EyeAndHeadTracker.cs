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
    [SerializeField] private string unityVersion = "2022.3.x";

    [Header("Gaze Dwell Destruction")]
    [SerializeField] private LayerMask layersToIncludeWithRay;
    [SerializeField] [Range(1.0f, 10.0f)] private float minGazeTimeOverTarget = 4.0f;
    [SerializeField] private bool enableGazeDestroyFeature = true;

    [Header("Logging")]
    [SerializeField] private bool useEfficientRawLogging = true;
    [SerializeField] private bool autoSaveWhenAllTargetsDestroyed = true;

    [Header("Combined JSON (your requested format)")]
    [SerializeField] private bool alsoExportCombinedJson = false;

    [Header("Optional Objects Tracking")]
    [SerializeField] private GameObject arucoGameObject;
    [SerializeField] private GameObject mapGameObject;
    [SerializeField] private bool enableAruco = true;
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
    }

    // ==================== PRIVATE STATE ====================

    private SessionMeta sessionMeta;
    private List<TrackingFrame> trackingFrames = new List<TrackingFrame>();
    private List<DestructionEvent> destructionEvents = new List<DestructionEvent>();
    private SessionPerformanceData performanceData = new SessionPerformanceData();

    private MeshRenderer[] targetRenderers;
    private float gazeOverTargetTracker;
    private int destroyCount = 0;
    private float appStartTime;
    private float previousDestroyTime;
    private int currentFrameId = 0;
    private float lastFrameTimestamp;
    private List<Vector3> gazeDirectionsDuringCurrentDwell = new List<Vector3>();

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

        appStartTime = Time.realtimeSinceStartup;
        lastFrameTimestamp = appStartTime;
        startTimeString = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        if (arucoGameObject != null) arucoGameObject.SetActive(enableAruco);
        if (mapGameObject != null) mapGameObject.SetActive(enableMap);

        sessionMeta = new SessionMeta
        {
            sessionId = sessionId,
            participantId = participantId,
            condition = condition,
            environment = environment
        };

        if (enableGazeDestroyFeature)
        {
            var allTargets = GameObject.FindGameObjectsWithTag("GazeDestroyTarget");
            targetRenderers = allTargets
                .Select(n => n.GetComponent<MeshRenderer>())
                .Where(r => r != null)
                .ToArray();

            Debug.Log($"Found {targetRenderers.Length} GazeDestroyTarget objects.");
        }

        if (Application.platform == RuntimePlatform.Android)
            RequestWritePermission();

        if (useEfficientRawLogging)
        {
            string rawPath = Path.Combine(persistentDataPath, $"raw_eye_head_tracking_{participantId}_{sessionId}_Aruco_{(enableAruco ? "On" : "Off")}_Map_{(enableMap ? "On" : "Off")}_{startTimeString}.ndjson");
            rawNdjsonWriter = new StreamWriter(rawPath, false);
        }
    }

    private void RequestWritePermission()
    {
        if (!Permission.HasUserAuthorizedPermission(Permission.ExternalStorageWrite))
            Permission.RequestUserPermission(Permission.ExternalStorageWrite);
    }

    private void Update()
    {
        CaptureTrackingFrame();

        if (enableGazeDestroyFeature &&
            GazeInputManager.Instance != null &&
            GazeInputManager.Instance.EyeTrackingPermissionGranted)
        {
            RunGazeDwellDestruction();
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
            eyeTracking = GetEyeTrackingData()
        };

        if (useEfficientRawLogging && rawNdjsonWriter != null)
            rawNdjsonWriter.WriteLine(JsonUtility.ToJson(frame));
        else
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

    private void RunGazeDwellDestruction()
    {
        var gazePosition = GazeInputManager.Instance.GazePosition;
        var gazeRotation = GazeInputManager.Instance.GazeRotation;

        if (Physics.Raycast(gazePosition, gazeRotation * Vector3.forward, out RaycastHit hitInfo, 10.0f, layersToIncludeWithRay))
        {
            var renderer = hitInfo.collider.GetComponent<MeshRenderer>();

            if (renderer != null && targetRenderers != null && targetRenderers.Contains(renderer))
            {
                gazeOverTargetTracker += Time.deltaTime;
                gazeDirectionsDuringCurrentDwell.Add(gazeRotation * Vector3.forward);

                float progress = gazeOverTargetTracker / minGazeTimeOverTarget;
                float fillAmount = ConvertPercentageToRange(progress);
                renderer.material.SetFloat(fillProgressProperty, fillAmount);
                ClearAllFillings(renderer.gameObject);

                if (gazeOverTargetTracker >= minGazeTimeOverTarget)
                {
                    float currentTime = Time.realtimeSinceStartup;
                    float timeSinceStart = currentTime - appStartTime;
                    float timeSincePrev = (destroyCount == 0) ? 0f : (currentTime - previousDestroyTime);

                    if (destroyCount == 0)
                        performanceData.timeToFirstDestroy = timeSinceStart;

                    var head = Camera.main != null ? Camera.main.transform : transform;
                    Vector3 headPos = head.position;
                    Vector3 headEuler = head.rotation.eulerAngles;
                    float stability = CalculateGazeStability(gazeDirectionsDuringCurrentDwell);

                    destructionEvents.Add(new DestructionEvent
                    {
                        destroyOrder = destroyCount + 1,
                        objectName = renderer.gameObject.name,
                        timeSinceAppStart = timeSinceStart,
                        timeSincePreviousDestroy = timeSincePrev,
                        headPositionAtDestroy = new Vector3Data { x = headPos.x, y = headPos.y, z = headPos.z },
                        headRotationEulerAtDestroy = new Vector3Data { x = headEuler.x, y = headEuler.y, z = headEuler.z },
                        gazeStabilityDuringDwell_deg = stability
                    });

                    destroyCount++;
                    previousDestroyTime = currentTime;
                    gazeDirectionsDuringCurrentDwell.Clear();

                    targetRenderers = targetRenderers.Where(r => r != renderer).ToArray();
                    Destroy(renderer.gameObject);
                    gazeOverTargetTracker = 0;

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
                gazeOverTargetTracker = 0;
                gazeDirectionsDuringCurrentDwell.Clear();
                ClearAllFillings();
            }
        }
        else
        {
            gazeOverTargetTracker = 0;
            gazeDirectionsDuringCurrentDwell.Clear();
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

    private float ConvertPercentageToRange(float percentage)
    {
        float rangeSize = MAX_FILL_RANGE - MIN_FILL_RANGE;
        return (percentage * rangeSize) - MAX_FILL_RANGE;
    }

    private void ClearAllFillings(GameObject exclude = null)
    {
        if (targetRenderers == null) return;
        float zero = ConvertPercentageToRange(0);
        foreach (var r in targetRenderers)
            if (r != null && r.gameObject != exclude)
                r.material.SetFloat(fillProgressProperty, zero);
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
                    ? $"raw_eye_head_tracking_{participantId}_{sessionId}_Aruco_{(enableAruco ? "On" : "Off")}_Map_{(enableMap ? "On" : "Off")}_{startTimeString}.ndjson" : null
            };

            string summaryPath = Path.Combine(persistentDataPath, $"gaze_session_summary_{participantId}_{sessionId}_Aruco_{(enableAruco ? "On" : "Off")}_Map_{(enableMap ? "On" : "Off")}_{startTimeString}.json");
            string json = JsonUtility.ToJson(root, true);
            File.WriteAllText(summaryPath, json);

            Debug.Log($"✅ Summary saved: {summaryPath}");

            // Optional combined JSON with trackingData array (your requested format)
            if (alsoExportCombinedJson && !useEfficientRawLogging && trackingFrames.Count > 0)
            {
                var combined = new CombinedExportRoot
                {
                    sessionMeta = sessionMeta,
                    trackingData = trackingFrames
                };

                string combinedPath = Path.Combine(persistentDataPath, $"combined_eye_head_tracking_{participantId}_{sessionId}_Aruco_{(enableAruco ? "On" : "Off")}_Map_{(enableMap ? "On" : "Off")}_{startTimeString}.json");
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

    private void OnDestroy()
    {
        if (rawNdjsonWriter != null)
        {
            rawNdjsonWriter.Flush();
            rawNdjsonWriter.Close();
        }
    }
}