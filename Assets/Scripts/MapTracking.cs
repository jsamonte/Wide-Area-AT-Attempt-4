// MapTracking.cs
// Cleaned-up, zero-UI version of PersonalMarkerTracking1.cs
// All settings exposed in the Inspector. No dropdowns, buttons, or text fields required.
// Drop this component on a GameObject in your scene and configure via Inspector.

using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;
using Unity.XR.CoreUtils;

namespace MagicLeap.Examples
{
    public class MapTracking : MonoBehaviour
    {
        [Header("=== YOUR CUSTOM PREFAB SETTINGS ===")]
        [SerializeField, Tooltip("Your own prefab that should appear on the ArUco marker")]
        private GameObject customMarkerPrefab;

        [SerializeField, Tooltip("The exact ArUco ID number printed on your marker (e.g. 88)")]
        private ulong targetArucoID = 88;

        [Header("Marker Detector Settings")]
        [SerializeField] private MarkerDetectorProfile profile = MarkerDetectorProfile.Default;
        [SerializeField] private ArucoType arucoType = ArucoType.Dictionary_5x5_250;
        
        [Tooltip("Physical size of the printed ArUco marker in meters (Default: 0.1016)")]
        [SerializeField] private float arucoLengthMeters = 0.1016f;
        
        [Tooltip("Leave unchecked to prevent tracking issues on ML2 with small markers")]
        [SerializeField] private bool estimateArucoLength = false;

        [Header("Low-Power Detection (reduces heat)")]
        [Tooltip("If true, ignore the Profile above and build a lightweight Custom profile (lower FPS / less frequent full analysis) to cut camera+CV load and heat. Recommended for the map, which only needs to locate one marker.")]
        [SerializeField] private bool useLowPowerProfile = true;
        [Tooltip("Frames per second the detector analyzes. Low is plenty for a map that follows via smoothing.")]
        [SerializeField] private MarkerDetectorFPS lowPowerFps = MarkerDetectorFPS.Low;
        [Tooltip("How often the detector does a full (expensive) analysis pass. Medium keeps re-acquisition quick without running every frame.")]
        [SerializeField] private MarkerDetectorFullAnalysisInterval lowPowerAnalysisInterval = MarkerDetectorFullAnalysisInterval.Medium;
        [Tooltip("Resolution hint. Low reduces load (mainly affects QR/UPC/EAN, harmless for ArUco).")]
        [SerializeField] private MarkerDetectorResolution lowPowerResolution = MarkerDetectorResolution.Low;

        [Header("Detector Lifecycle")]
        [Tooltip("If false, no detector is created until StartTracking() is called (e.g. by SequenceManager when a trial starts). Keeps the camera/CV pipeline off — and heat down — while it isn't needed.")]
        [SerializeField] private bool startTrackingOnStart = false;

        [Header("XR Configuration")]
        [SerializeField, Tooltip("Required to convert Tracking Space to World Space. Will auto-find if left empty.")]
        private XROrigin xrOrigin;

        [Header("Transform Correction")]
        [SerializeField, Tooltip("Rotation offset applied to the prefab so it sits correctly on the marker")]
        private Vector3 rotationOffset = new Vector3(270f, 0f, 0f);

        [SerializeField, Tooltip("Position offset in meters applied after the rotation offset. Use X to shift the map sideways from the marker, Y up/down, Z forward/back.")]
        private Vector3 positionOffset = Vector3.zero;

        [SerializeField, Tooltip("If true, the position offset follows the map's own orientation (X is always 'sideways' relative to the marker). If false, the offset is applied in world axes.")]
        private bool offsetInMarkerSpace = true;

        [Header("Smoothing")]
        [SerializeField, Tooltip("How smoothly the map follows the marker. Lower = smoother/delayed, Higher = faster/jittery.")]
        private float followSpeed = 12f;

        private MagicLeapMarkerUnderstandingFeature markerFeature;
        // The single detector THIS component owns. Tracked so StopTracking() can
        // destroy ONLY the map detector via DestroyMarkerDetector(), never the
        // space-pin or other detectors that share the feature singleton.
        private MarkerDetector _detector;
        private GameObject currentCustomInstance;
        private bool markerVisible = false;
        
        private Vector3 targetPosition;
        private Quaternion targetRotation;
        private bool hasInitialPose = false;

        private void OnValidate()
        {
            if (xrOrigin == null)
                xrOrigin = FindAnyObjectByType<XROrigin>();
        }

        void Start()
        {
            if (xrOrigin == null)
                xrOrigin = FindAnyObjectByType<XROrigin>();

            markerFeature = OpenXRSettings.Instance.GetFeature<MagicLeapMarkerUnderstandingFeature>();

            if (markerFeature == null)
            {
                Debug.LogError("❌ MagicLeapMarkerUnderstandingFeature not found! Go to Project Settings -> OpenXR.");
                enabled = false;
                return;
            }

            // Register with the shared per-frame pump so UpdateMarkerDetectors() is
            // called exactly once per frame (at end-of-frame) across all scripts.
            MarkerDetectorPump.Instance.Register(markerFeature);

            // No permission request here. This used to ask for SPACE_IMPORT_EXPORT (copied from
            // Prototype3), but that permission is not declared in AndroidManifest.xml and nothing in
            // this component ever read the result -- so it only ever logged an ERROR on every launch.
            // Marker tracking runs on MARKER_TRACKING, which IS declared and granted.

            // Do NOT create the detector here by default. It is created on demand
            // via StartTracking() (e.g. when a trial begins) so the camera/CV
            // pipeline -- and its heat -- stays off until the map is actually needed.
            if (startTrackingOnStart)
                StartTracking();
        }

        /// <summary>
        /// Create the map's marker detector if it isn't already running. Idempotent.
        /// Call this when the map is actually needed (e.g. at trial start) so the
        /// camera/CV pipeline only runs during that window.
        /// </summary>
        public void StartTracking()
        {
            if (markerFeature == null) return;
            if (_detector != null) return; // already tracking

            var settings = new MarkerDetectorSettings
            {
                MarkerDetectorProfile = useLowPowerProfile ? MarkerDetectorProfile.Custom : profile,
                MarkerType = MarkerType.Aruco,
                ArucoSettings = new ArucoSettings
                {
                    ArucoType = arucoType,
                    ArucoLength = arucoLengthMeters,
                    EstimateArucoLength = estimateArucoLength
                }
            };

            if (useLowPowerProfile)
            {
                settings.CustomProfileSettings = new CustomProfileSettings
                {
                    FPSHint = lowPowerFps,
                    ResolutionHint = lowPowerResolution,
                    CameraHint = MarkerDetectorCamera.RGB,             // single camera = less load than multi-cam World
                    CornerRefinement = MarkerDetectorCornerRefineMethod.None, // fastest; map placement doesn't need sub-pixel corners
                    AnalysisInterval = lowPowerAnalysisInterval,
                    UseEdgeRefinement = false
                };
            }

            _detector = markerFeature.CreateMarkerDetector(settings);

            Debug.Log($"✅ MapTracking started — tracking ArUco ID {targetArucoID} " +
                      $"(Dictionary: {arucoType}, Size: {arucoLengthMeters * 1000f} mm, " +
                      $"Profile: {(useLowPowerProfile ? "Custom/LowPower" : profile.ToString())})");
        }

        /// <summary>
        /// Destroy ONLY this component's detector (leaving other detectors on the
        /// shared feature untouched) and hide the spawned map. Call when the map is
        /// no longer needed (e.g. once all trial targets are destroyed) to drop the
        /// camera/CV load and reduce heat.
        /// </summary>
        public void StopTracking()
        {
            if (markerFeature != null && _detector != null)
            {
                markerFeature.DestroyMarkerDetector(_detector);
                Debug.Log("[MapTracking] Map detector destroyed to save power (other detectors left intact).");
            }
            _detector = null;

            if (currentCustomInstance != null)
                currentCustomInstance.SetActive(false);
            markerVisible = false;
        }

        void Update()
        {
            // Only read from OUR detector. If it hasn't been started (or has been
            // stopped), there's nothing to do -- and we never touch other detectors.
            if (_detector == null)
                return;

            // UpdateMarkerDetectors() is handled once per frame by MarkerDetectorPump.
            markerVisible = false;

            // 2. Loop through this detector's tracking data
            for (int i = 0; i < _detector.Data.Count; i++)
            {
                var data = _detector.Data[i];

                if (data.MarkerPose == null || data.MarkerNumber != targetArucoID)
                    continue;

                markerVisible = true;

                // Convert the raw pose from ML Tracking Space to Unity World Space!
                Pose worldPose = ToWorld(data.MarkerPose.Value);

                // 3. Spawn once, then just move it — never re-instantiate every frame
                if (currentCustomInstance == null && customMarkerPrefab != null)
                {
                    currentCustomInstance = Instantiate(customMarkerPrefab);
                    currentCustomInstance.SetActive(true);
                    LabelForGazeLogging(currentCustomInstance);
                }

                if (currentCustomInstance != null)
                {
                    Quaternion offsetRot = Quaternion.Euler(rotationOffset);
                    targetRotation = worldPose.rotation * offsetRot;
                    targetPosition = worldPose.position +
                        (offsetInMarkerSpace ? targetRotation * positionOffset : positionOffset);

                    if (!hasInitialPose)
                    {
                        currentCustomInstance.transform.SetPositionAndRotation(targetPosition, targetRotation);
                        hasInitialPose = true;
                    }
                }
            }

            // 4. Smooth movement
            if (currentCustomInstance != null)
            {
                currentCustomInstance.SetActive(markerVisible);

                if (markerVisible && hasInitialPose)
                {
                    currentCustomInstance.transform.position = Vector3.Lerp(
                        currentCustomInstance.transform.position, 
                        targetPosition, 
                        Time.deltaTime * followSpeed);
                        
                    currentCustomInstance.transform.rotation = Quaternion.Slerp(
                        currentCustomInstance.transform.rotation, 
                        targetRotation, 
                        Time.deltaTime * followSpeed);
                }
            }
        }

        /// <summary>
        /// Stamps the spawned map with the gaze category the analysis expects.
        ///
        /// Done in code rather than left on the prefab on purpose. The map is the only UI panel in the
        /// scene that the gaze ray can hit, it accumulates very long dwells, and until P001 it was
        /// classified as "Other" -- i.e. as scenery -- which made it 96% of the primary DV in trial 1.
        /// A missing component on a prefab is invisible until the data comes back; this is not.
        /// The category is forced even if the prefab already carries a GazeLoggableObject -- the whole
        /// point is that it cannot be mislabelled. Only the display name defers to the prefab.
        /// </summary>
        private static void LabelForGazeLogging(GameObject mapInstance)
        {
            if (mapInstance == null) return;

            // The root specifically, not GetComponentInChildren: the tracker resolves a collider by
            // walking UP with GetComponentInParent, so a label sitting on a sibling child would never
            // be found by the map's own collider.
            var loggable = mapInstance.GetComponent<GazeLoggableObject>();
            if (loggable == null) loggable = mapInstance.AddComponent<GazeLoggableObject>();

            loggable.category = EyeAndHeadTracker.MapCategory;
            if (string.IsNullOrEmpty(loggable.displayName)) loggable.displayName = "NavigationMap";
        }

        private Pose ToWorld(Pose tracking)
        {
            Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null) ? xrOrigin.CameraFloorOffsetObject.transform : null;
            if (originT == null) return tracking;
            
            return new Pose(
                originT.TransformPoint(tracking.position), 
                originT.rotation * tracking.rotation
            );
        }

        void OnDestroy()
        {
            if (currentCustomInstance != null)
            {
                Destroy(currentCustomInstance);
                currentCustomInstance = null;
            }

            // Destroy ONLY our own detector. Never call DestroyAllMarkerDetectors()
            // here: other components (space pins, Prototype8, etc.) register their
            // own detectors on the same feature singleton and must not be torn down.
            if (markerFeature != null && _detector != null)
            {
                markerFeature.DestroyMarkerDetector(_detector);
                _detector = null;
            }
        }
    }
}
