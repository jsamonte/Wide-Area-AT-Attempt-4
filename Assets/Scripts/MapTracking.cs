// MapTracking.cs
// Cleaned-up, zero-UI version of PersonalMarkerTracking1.cs
// All settings exposed in the Inspector. No dropdowns, buttons, or text fields required.
// Drop this component on a GameObject in your scene and configure via Inspector.

using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;
using MagicLeap.Android;
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

        [Header("XR Configuration")]
        [SerializeField, Tooltip("Required to convert Tracking Space to World Space. Will auto-find if left empty.")]
        private XROrigin xrOrigin;

        [Header("Transform Correction")]
        [SerializeField, Tooltip("Rotation offset applied to the prefab so it sits correctly on the marker")]
        private Vector3 rotationOffset = new Vector3(270f, 0f, 0f);

        [Header("Smoothing")]
        [SerializeField, Tooltip("How smoothly the map follows the marker. Lower = smoother/delayed, Higher = faster/jittery.")]
        private float followSpeed = 12f;

        private MagicLeapMarkerUnderstandingFeature markerFeature;
        private GameObject currentCustomInstance;
        private bool markerVisible = false;
        
        private Vector3 targetPosition;
        private Quaternion targetRotation;
        private bool hasInitialPose = false;
        private bool permissionGranted = false;

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

            // Request permissions exactly like Prototype3
            Permissions.RequestPermission(Permissions.SpaceImportExport, OnPermissionGranted, OnPermissionDenied);

            CreateMarkerDetector();
        }

        private void OnPermissionGranted(string permission)
        {
            permissionGranted = true;
        }

        private void OnPermissionDenied(string permission)
        {
            Debug.LogError($"[MapTracking] Permission denied: {permission}");
        }

        private void CreateMarkerDetector()
        {
            if (markerFeature == null) return;

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
            if (markerFeature == null || markerFeature.MarkerDetectors.Count == 0)
                return;

            // UpdateMarkerDetectors() is handled once per frame by MarkerDetectorPump.
            markerVisible = false;

            // 2. Loop through tracking data
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

                    // Convert the raw pose from ML Tracking Space to Unity World Space!
                    Pose worldPose = ToWorld(data.MarkerPose.Value);

                    // 3. Spawn once, then just move it — never re-instantiate every frame
                    if (currentCustomInstance == null && customMarkerPrefab != null)
                    {
                        currentCustomInstance = Instantiate(customMarkerPrefab);
                        currentCustomInstance.SetActive(true);
                    }

                    if (currentCustomInstance != null)
                    {
                        Quaternion offsetRot = Quaternion.Euler(rotationOffset);
                        targetPosition = worldPose.position;
                        targetRotation = worldPose.rotation * offsetRot;

                        if (!hasInitialPose)
                        {
                            currentCustomInstance.transform.SetPositionAndRotation(targetPosition, targetRotation);
                            hasInitialPose = true;
                        }
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

            // NOTE: Do NOT call DestroyAllMarkerDetectors() here.
            // Prototype8.cs registers its own detectors on the same feature singleton.
            // Calling DestroyAllMarkerDetectors() from MapTracking would silently
            // destroy Prototype8's detectors. Prototype8.DestroyAll() handles
            // cleanup of all detectors when it is destroyed.
        }
    }
}
