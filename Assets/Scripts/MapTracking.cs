// MapTracking.cs
// Cleaned-up, zero-UI version of PersonalMarkerTracking1.cs
// All settings exposed in the Inspector. No dropdowns, buttons, or text fields required.
// Drop this component on a GameObject in your scene and configure via Inspector.

using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;

namespace MagicLeap.Examples
{
    public class MapTracking : MonoBehaviour
    {
        [Header("=== YOUR CUSTOM PREFAB SETTINGS ===")]
        [SerializeField, Tooltip("Your own prefab that should appear on the ArUco marker")]
        private GameObject customMarkerPrefab;

        [SerializeField, Tooltip("The exact ArUco ID number printed on your marker (e.g. 88)")]
        private ulong targetArucoID = 88;

        [Header("Marker Detector Settings (Inspector only - no UI needed)")]
        [SerializeField] private MarkerDetectorProfile profile = MarkerDetectorProfile.Default;
        [SerializeField] private ArucoType arucoType = ArucoType.Dictionary_5x5_250;
        [SerializeField, Tooltip("Physical size of the printed ArUco marker in meters")]
        private float arucoLengthMeters = 0.15f;
        [SerializeField] private bool estimateArucoLength = false;

        [Header("Transform Correction")]
        [SerializeField, Tooltip("Rotation offset applied to the prefab so it sits correctly on the marker")]
        private Vector3 rotationOffset = new Vector3(270f, 0f, 0f);

        private MagicLeapMarkerUnderstandingFeature markerFeature;
        private GameObject currentCustomInstance;
        private bool markerVisible = false;
        private WaitForEndOfFrame _waitForEndOfFrame;

        void Start()
        {
            markerFeature = OpenXRSettings.Instance.GetFeature<MagicLeapMarkerUnderstandingFeature>();

            if (markerFeature == null)
            {
                Debug.LogError("❌ MagicLeapMarkerUnderstandingFeature not found!\n" +
                               "Go to Edit → Project Settings → XR Plug-in Management → OpenXR and enable 'Magic Leap Marker Understanding' feature.");
                enabled = false;
                return;
            }

            CreateMarkerDetector();

            _waitForEndOfFrame = new WaitForEndOfFrame();
            StartCoroutine(DetectionLoop());
        }

        private void CreateMarkerDetector()
        {
            if (markerFeature == null) return;

            if (ArucoTrackerSync.GlobalDetectorExists(markerFeature, MarkerType.Aruco))
            {
                Debug.Log("[Console] MapTracking: Detector already exists globally. Skipping duplicate creation.");
                return;
            }

            var settings = new MarkerDetectorSettings
            {
                MarkerDetectorProfile = profile,
                MarkerType = MarkerType.Aruco,
                ArucoSettings = new ArucoSettings
                {
                    ArucoType = arucoType,
                    ArucoLength = arucoLengthMeters,
                    EstimateArucoLength = estimateArucoLength
                }
            };

            markerFeature.CreateMarkerDetector(settings);

            Debug.Log($"✅ MapTracking ready — tracking ArUco ID {targetArucoID} " +
                      $"(Dictionary: {arucoType}, Size: {arucoLengthMeters * 1000f} mm)");
        }

        void Update()
        {
            // Hide (but don't destroy) the prefab when the marker leaves view
            if (currentCustomInstance != null)
            {
                currentCustomInstance.SetActive(markerVisible);
            }
        }

        private IEnumerator DetectionLoop()
        {
            while (true)
            {
                yield return _waitForEndOfFrame;

                if (markerFeature == null || markerFeature.MarkerDetectors.Count == 0)
                    continue;

                ArucoTrackerSync.UpdateDetectorsOncePerFrame(markerFeature);

                markerVisible = false;

                foreach (var markerDetector in markerFeature.MarkerDetectors)
                {
                    if (markerDetector.Settings.MarkerType != MarkerType.Aruco)
                        continue;

                    for (int i = 0; i < markerDetector.Data.Count; i++)
                    {
                        var data = markerDetector.Data[i];

                        if (data.MarkerPose == null || data.MarkerNumber != targetArucoID)
                            continue;

                        markerVisible = true;

                        // Spawn once, then just move it — never re-instantiate every frame
                        if (currentCustomInstance == null && customMarkerPrefab != null)
                        {
                            currentCustomInstance = Instantiate(customMarkerPrefab);
                            currentCustomInstance.SetActive(true);
                        }

                        if (currentCustomInstance != null)
                        {
                            Quaternion offsetRot = Quaternion.Euler(rotationOffset);
                            currentCustomInstance.transform.SetPositionAndRotation(
                                data.MarkerPose.Value.position,
                                data.MarkerPose.Value.rotation * offsetRot);
                        }
                    }
                }
            }
        }

        void OnDestroy()
        {
            DestroyMarkerTrackers();
        }

        /// <summary>
        /// Public method so you can call this from other scripts or a UI button if you ever add one later.
        /// </summary>
        public void DestroyMarkerTrackers()
        {
            if (currentCustomInstance != null)
            {
                Destroy(currentCustomInstance);
                currentCustomInstance = null;
            }

            // Removed markerFeature.DestroyAllMarkerDetectors() to prevent destroying 
            // detectors that might be used by other scripts (like WireframeAlignment).
        }
    }
}
