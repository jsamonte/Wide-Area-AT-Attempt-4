// %BANNER_BEGIN%
// ---------------------------------------------------------------------
// %COPYRIGHT_BEGIN%
// Copyright (c) (2024) Magic Leap, Inc. All Rights Reserved.
// %COPYRIGHT_END%
// ---------------------------------------------------------------------
// %BANNER_END%

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;

namespace MagicLeap.Examples
{
    public class PersonalMarkerTracking1 : MonoBehaviour
    {
        [Header("=== YOUR CUSTOM PREFAB SETTINGS ===")]
        [SerializeField, Tooltip("Your own prefab that should appear on your personal ArUco marker")]
        private GameObject customMarkerPrefab;

        [SerializeField, Tooltip("The exact ArUco ID number printed on your marker (e.g. 42)")]
        private ulong targetArucoID = 88;   // ← CHANGE THIS TO YOUR MARKER'S ID

        [Header("Existing UI (keep as-is)")]
        [SerializeField] private Dropdown profileDropdown;
        [SerializeField] private Dropdown markerDetectorTypeDropdown;
        [SerializeField] private Text markerTypeText;
        [SerializeField] private Dropdown arucoTypeDropdown;
        [SerializeField] private Dropdown aprilTagTypeDropdown;
        [SerializeField] private Toggle estimateLength;
        [SerializeField] private Slider markerLength;
        [SerializeField] private Text markerLengethText;
        [SerializeField] private Dropdown FPSHintDropdown;
        [SerializeField] private Dropdown resolutionHintDropdown;
        [SerializeField] private Dropdown cameraHintDropdown;
        [SerializeField] private Dropdown cornerRefinementDropdown;
        [SerializeField] private Dropdown analysisIntervalDropdown;
        [SerializeField] private Toggle useEdgeRefinement;
        [SerializeField] private Text statusTextDisplay;
        [SerializeField] private Button destroyAllButton;

        private MagicLeapMarkerUnderstandingFeature markerFeature;
        private MarkerDetectorSettings markerDetectorSettings;
        private GameObject currentCustomInstance;   // tracks your spawned prefab

        void Start()
        {
            markerFeature = OpenXRSettings.Instance.GetFeature<MagicLeapMarkerUnderstandingFeature>();
            
            if (markerFeature == null)
            {
                Debug.LogError("❌ MagicLeapMarkerUnderstandingFeature not found! Make sure it's enabled in XR Plug-in Management → OpenXR.");
                return;
            }

            // === HARDCODED PERSONAL ARUCO TRACKER (exactly what you asked for) ===
            CreateHardcodedPersonalArucoTracker();

            // Keep the destroy button working
            destroyAllButton.onClick.AddListener(DestroyMarkerTrackers);
            destroyAllButton.interactable = false;

            // Optional: still allow manual creation via UI if you want both
            markerDetectorTypeDropdown.onValueChanged.AddListener(OnMarkerDetectorDropdownChanged);
        }

        private void CreateHardcodedPersonalArucoTracker()
        {
            // Fresh settings every time (required)
            markerDetectorSettings = new MarkerDetectorSettings();

            markerDetectorSettings.MarkerDetectorProfile = MarkerDetectorProfile.Default;   // Fast & reliable. Change to .Accuracy / .SmallTargets / .LargeFOV if needed
            markerDetectorSettings.MarkerType = MarkerType.Aruco;

            // === CHANGE THESE TWO LINES TO MATCH YOUR PRINTED MARKER ===
            markerDetectorSettings.ArucoSettings.ArucoType = ArucoType.Dictionary_5x5_250;   // Supports ID 88 (use Dictionary_5x5_100 or Dictionary_6x6_250 if you prefer)
            markerDetectorSettings.ArucoSettings.ArucoLength = 0.15f;                        // Your marker size in METERS (example = 15 cm). MUST match real size if EstimateArucoLength = false

            markerDetectorSettings.ArucoSettings.EstimateArucoLength = false;   // Set true only if you don't know exact size

            // Create the tracker
            markerFeature.CreateMarkerDetector(markerDetectorSettings);

            Debug.Log($"✅ Personal ArUco tracker created (target ID = {targetArucoID}, size = {markerDetectorSettings.ArucoSettings.ArucoLength * 1000f} mm)");
        }
        void Update()
        {
            var sb = new StringBuilder($"Marker Detectors Created: {markerFeature.MarkerDetectors.Count}");
            destroyAllButton.interactable = markerFeature.MarkerDetectors.Count > 0;

            if (markerFeature.MarkerDetectors.Count == 0)
            {
                statusTextDisplay.text = sb.ToString();
                return;
            }

            markerFeature.UpdateMarkerDetectors();

            foreach (var markerDetector in markerFeature.MarkerDetectors)
            {
                // Only care about ArUco detectors
                if (markerDetector.Settings.MarkerType != MarkerType.Aruco)
                    continue;

                for (int i = 0; i < markerDetector.Data.Count; i++)
                {
                    var data = markerDetector.Data[i];

                    // === THIS IS THE KEY PART ===
                    if (data.MarkerPose != null && data.MarkerNumber == targetArucoID)
                    {
                        if (currentCustomInstance == null && customMarkerPrefab != null)
                        {
                            currentCustomInstance = Instantiate(customMarkerPrefab);
                        }

                        if (currentCustomInstance != null)
                        {
                            // Move your prefab to the exact real-world position & rotation of the marker
                            currentCustomInstance.transform.SetPositionAndRotation(
                                data.MarkerPose.Value.position,
                                data.MarkerPose.Value.rotation);

                            // Optional: scale the prefab to match the real marker size
                            float scale = data.MarkerLength;
                            currentCustomInstance.transform.localScale = Vector3.one * scale;
                        }
                    }
                }
            }

            statusTextDisplay.text = sb.ToString();
        }

        void OnDestroy()
        {
            destroyAllButton.onClick.RemoveAllListeners();
            markerDetectorTypeDropdown.onValueChanged.RemoveAllListeners();
            DestroyMarkerTrackers();
        }

        private void DestroyMarkerTrackers()
        {
            if (currentCustomInstance != null)
            {
                Destroy(currentCustomInstance);
                currentCustomInstance = null;
            }
            markerFeature.DestroyAllMarkerDetectors();
        }

        // The rest of your existing UI methods (unchanged)
        public void OnSliderChanged(float value) => markerLengethText.text = $"{(int)value} mm";
                public void OnMarkerDetectorDropdownChanged(int idx)
        {
            switch ((MarkerType)idx)
            {
                case MarkerType.Aruco:
                    markerTypeText.text = "ArUco:";
                    aprilTagTypeDropdown.gameObject.SetActive(false);
                    arucoTypeDropdown.gameObject.SetActive(true);
                    break;
                case MarkerType.AprilTag:
                    markerTypeText.text = "April Tag:";
                    arucoTypeDropdown.gameObject.SetActive(false);
                    aprilTagTypeDropdown.gameObject.SetActive(true);
                    break;
                default:
                    arucoTypeDropdown.gameObject.SetActive(false);
                    aprilTagTypeDropdown.gameObject.SetActive(false);
                    markerTypeText.text = "";
                    break;
            }
        }
        public void OnMarkerProfileChanged(int idx)
        {
            bool active = (MarkerDetectorProfile)idx == MarkerDetectorProfile.Custom;
            FPSHintDropdown.gameObject.SetActive(active);
            resolutionHintDropdown.gameObject.SetActive(active);
            cameraHintDropdown.gameObject.SetActive(active);
            cornerRefinementDropdown.gameObject.SetActive(active);
            analysisIntervalDropdown.gameObject.SetActive(active);
            useEdgeRefinement.gameObject.SetActive(active);
        }
        public void OnCreateMarkerDetector()
        {
            markerDetectorSettings = new MarkerDetectorSettings();   // ← safe to do every time

            markerDetectorSettings.MarkerDetectorProfile = (MarkerDetectorProfile)profileDropdown.value;
            markerDetectorSettings.MarkerDetectorProfile = (MarkerDetectorProfile)profileDropdown.value;
            markerDetectorSettings.MarkerType = (MarkerType)markerDetectorTypeDropdown.value;

            if (markerDetectorSettings.MarkerDetectorProfile == MarkerDetectorProfile.Custom)
            {
                // set custom settings
                markerDetectorSettings.CustomProfileSettings.FPSHint = (MarkerDetectorFPS)FPSHintDropdown.value;
                markerDetectorSettings.CustomProfileSettings.ResolutionHint = (MarkerDetectorResolution)resolutionHintDropdown.value;
                markerDetectorSettings.CustomProfileSettings.CameraHint = (MarkerDetectorCamera)cameraHintDropdown.value;
                markerDetectorSettings.CustomProfileSettings.CornerRefinement = (MarkerDetectorCornerRefineMethod)cornerRefinementDropdown.value;
                markerDetectorSettings.CustomProfileSettings.AnalysisInterval = (MarkerDetectorFullAnalysisInterval)analysisIntervalDropdown.value;
                markerDetectorSettings.CustomProfileSettings.UseEdgeRefinement = useEdgeRefinement.isOn;
            }

            switch ((MarkerType)markerDetectorTypeDropdown.value)
            {
                case MarkerType.Aruco:
                    markerDetectorSettings.ArucoSettings.ArucoType = (ArucoType)arucoTypeDropdown.value;
                    markerDetectorSettings.ArucoSettings.ArucoLength = markerLength.value / 1000f;
                    markerDetectorSettings.ArucoSettings.EstimateArucoLength = estimateLength.isOn;
                    break;
                case MarkerType.AprilTag:
                    markerDetectorSettings.AprilTagSettings.AprilTagType = (AprilTagType)aprilTagTypeDropdown.value;
                    markerDetectorSettings.AprilTagSettings.AprilTagLength = markerLength.value / 1000f;
                    markerDetectorSettings.AprilTagSettings.EstimateAprilTagLength = estimateLength.isOn;
                    break;
                case MarkerType.QR:
                    markerDetectorSettings.QRSettings.QRLength = markerLength.value / 1000f;
                    markerDetectorSettings.QRSettings.EstimateQRLength = estimateLength.isOn;
                    break;
            }

            markerFeature.CreateMarkerDetector(markerDetectorSettings);
        }
    }
}