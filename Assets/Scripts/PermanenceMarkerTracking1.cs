// %BANNER_BEGIN%
// ---------------------------------------------------------------------
// %COPYRIGHT_BEGIN%
// Copyright (c) (2024) Magic Leap, Inc. All Rights Reserved.
// %COPYRIGHT_END%
// ---------------------------------------------------------------------
// %BANNER_END%

using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;

namespace MagicLeap.Examples
{
    public class PermanenceMarkerTracking1 : MonoBehaviour
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

        private Vector3 rotationOffset = new Vector3(270f, 0f, 0f);
        private MagicLeapMarkerUnderstandingFeature markerFeature;
        private MarkerDetectorSettings markerDetectorSettings;
        private GameObject currentCustomInstance;   // tracks your spawned prefab
        private bool hasEverBeenSeen = false;       // NEW: tracks permanence (last known pose)

        void Start()
        {
            markerFeature = OpenXRSettings.Instance.GetFeature<MagicLeapMarkerUnderstandingFeature>();

            if (markerFeature == null)
            {
                Debug.LogError("❌ MagicLeapMarkerUnderstandingFeature not found! Make sure it's enabled in XR Plug-in Management → OpenXR.");
                return;
            }

            CreateHardcodedPersonalArucoTracker();

            destroyAllButton.onClick.AddListener(DestroyMarkerTrackers);
            destroyAllButton.interactable = false;

            markerDetectorTypeDropdown.onValueChanged.AddListener(OnMarkerDetectorDropdownChanged);
        }

        private void CreateHardcodedPersonalArucoTracker()
        {
            markerDetectorSettings = new MarkerDetectorSettings();

            markerDetectorSettings.MarkerDetectorProfile = MarkerDetectorProfile.Default;
            markerDetectorSettings.MarkerType = MarkerType.Aruco;

            // === CHANGE THESE TWO LINES TO MATCH YOUR PRINTED MARKER ===
            markerDetectorSettings.ArucoSettings.ArucoType = ArucoType.Dictionary_5x5_250;
            markerDetectorSettings.ArucoSettings.ArucoLength = 0.15f;   // meters — must match real size

            markerDetectorSettings.ArucoSettings.EstimateArucoLength = false;

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

            bool currentlyVisible = false;   // NEW: only true this frame if marker is seen right now

            foreach (var markerDetector in markerFeature.MarkerDetectors)
            {
                if (markerDetector.Settings.MarkerType != MarkerType.Aruco)
                    continue;

                for (int i = 0; i < markerDetector.Data.Count; i++)
                {
                    var data = markerDetector.Data[i];

                    if (data.MarkerPose == null || data.MarkerNumber != targetArucoID)
                        continue;

                    currentlyVisible = true;
                    hasEverBeenSeen = true;

                    // Spawn once, then just move it — never re-instantiate every frame
                    if (currentCustomInstance == null && customMarkerPrefab != null)
                    {
                        currentCustomInstance = Instantiate(customMarkerPrefab);
                        currentCustomInstance.SetActive(true);   // start visible
                    }

                    if (currentCustomInstance != null)
                    {
                        // Update to the LIVE pose only while the marker is visible
                        Quaternion offset = Quaternion.Euler(rotationOffset);
                        currentCustomInstance.transform.SetPositionAndRotation(
                            data.MarkerPose.Value.position,
                            data.MarkerPose.Value.rotation * offset);

                        // Guarded scale (only apply when valid — prevents degenerate transform)
                        float reportedLength = data.MarkerLength;
                        if (reportedLength > 0.001f)
                        {
                            currentCustomInstance.transform.localScale = Vector3.one * reportedLength;
                        }
                    }

                    sb.AppendLine($"\nTracking ID {data.MarkerNumber} at {data.MarkerPose.Value.position}");
                }
            }

            // PERMANENCE: Once seen, the prefab STAYS visible at the LAST known position/rotation/scale
            if (currentCustomInstance != null)
            {
                currentCustomInstance.SetActive(true);   // never hide again after first detection
            }

            // Helpful status feedback
            if (hasEverBeenSeen && !currentlyVisible)
            {
                sb.AppendLine($"\n🟡 LAST SEEN POSITION (ArUco ID {targetArucoID} no longer visible)");
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
            hasEverBeenSeen = false;
            markerFeature.DestroyAllMarkerDetectors();
        }

        // ── UI helpers (unchanged) ────────────────────────────────────────────

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
            markerDetectorSettings = new MarkerDetectorSettings();   // required — it's a struct

            markerDetectorSettings.MarkerDetectorProfile = (MarkerDetectorProfile)profileDropdown.value;
            markerDetectorSettings.MarkerType = (MarkerType)markerDetectorTypeDropdown.value;

            if (markerDetectorSettings.MarkerDetectorProfile == MarkerDetectorProfile.Custom)
            {
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