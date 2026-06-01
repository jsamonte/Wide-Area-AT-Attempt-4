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
    public class PersonalMarkerTrackingExample : MonoBehaviour
    {
        [Header("=== YOUR CUSTOM PREFAB SETTINGS ===")]
        [SerializeField, Tooltip("Your own prefab that should appear on your personal ArUco marker")]
        private GameObject customMarkerPrefab;

        [SerializeField, Tooltip("The exact ArUco ID number printed on your marker (e.g. 42)")]
        private ulong targetArucoID = 42;   // ← CHANGE THIS TO YOUR MARKER'S ID

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
            markerDetectorTypeDropdown.onValueChanged.AddListener(OnMarkerDetectorDropdownChanged);
            destroyAllButton.onClick.AddListener(DestroyMarkerTrackers);
            destroyAllButton.interactable = false;
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
        public void OnMarkerDetectorDropdownChanged(int idx) { /* keep your existing code */ }
        public void OnMarkerProfileChanged(int idx) { /* keep your existing code */ }
        public void OnCreateMarkerDetector() { /* keep your existing code */ }
    }
}