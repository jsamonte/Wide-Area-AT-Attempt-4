// %BANNER_BEGIN%
// ---------------------------------------------------------------------
// %COPYRIGHT_BEGIN%
// Copyright (c) (2024) Magic Leap, Inc. All Rights Reserved.
// %COPYRIGHT_END%
// ---------------------------------------------------------------------
// %BANNER_END%
//
// FIXED VERSION: Addresses lag + per-frame blinking/jitter after spawn.
// Root cause: 
//   1. Marker poses from MagicLeapMarkerUnderstandingFeature are RELATIVE to XROrigin.CameraFloorOffsetObject (not world space).
//      Original direct assignment caused incorrect placement, jumps, and perceived lag/blink as tracking/origin updated.
//   2. Intermittent zero/invalid MarkerPose or MarkerLength data from the OpenXR feature (known community issue, especially with Estimate=false).
//      Caused snaps to origin or skipped updates that looked like blinking.
//   3. UI status spam every frame + reliance on potentially bad data.MarkerLength for scale.
//
// Changes made:
// - Added XROrigin support + correct world-space transform (TransformPoint + rotation compose) matching official Magic Leap examples.
// - Added robust validation: skip frames with near-zero pose (invalid data).
// - Use configured arucoLength for scale (reliable, independent of data.MarkerLength which can be 0).
// - Permanence logic preserved/enhanced: only updates on GOOD data; keeps last good pose/scale/rot when marker lost or bad data.
// - Kept all your custom offset/scaleMultiplier/rotationOffset/permanence/target ID logic.
// - Auto-finds XROrigin if not assigned (add to scene if missing).
// - Fallback to direct pose if no XROrigin (for compatibility).
// - Minor: guard feature null in Update; use var pose for clarity.
//
// Usage:
// 1. Replace your BuildingMarkerTracking1.cs with this file (or copy contents).
// 2. In Inspector on the GameObject with this script: assign your "1st Building 3 Wireframe.prefab" (or desired custom prefab) to customMarkerPrefab.
// 3. Set targetArucoID to YOUR printed marker's ID (default 88).
// 4. Tune offsetX/Y/Z (meters, marker-local) and scaleMultiplier (start with 5-20 for building to look reasonable size next to 15cm marker).
// 5. Test rotationOffset (270 on X is common; try -90, 0, or 180 if model lies flat/wrong way).
// 6. Ensure good even lighting on the physical ArUco marker for reliable detection.
// 7. The UI elements (dropdowns etc.) are preserved for compatibility with your scene.
// 8. Build & run on Magic Leap 2. The prefab should now STICK stably to the real marker (or last known pose) without jumping or blinking.
//
// If still issues: 
// - Confirm MagicLeapMarkerUnderstandingFeature enabled in Project Settings > XR Plug-in Management > OpenXR.
// - Add MARKER_TRACKING permission in Magic Leap Manifest Settings.
// - Check device logs for pose/ detector warnings.
// - Try EstimateArucoLength = true in CreateHardcoded for potentially more stable data (at cost of slight accuracy).
// - Your scene likely already has an XR Origin — this script auto-finds it.

using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;
using Unity.XR.CoreUtils;   // For XROrigin

namespace MagicLeap.Examples
{
    public class BuildingMarkerTracking1 : MonoBehaviour
    {
        [Header("=== YOUR CUSTOM PREFAB SETTINGS ===")]
        [SerializeField, Tooltip("Your own prefab that should appear on your personal ArUco marker")]
        private GameObject customMarkerPrefab;

        [SerializeField, Tooltip("The exact ArUco ID number printed on your marker (e.g. 42)")]
        private ulong targetArucoID = 88;   // ← CHANGE THIS TO YOUR MARKER'S ID

        [Header("Prefab Position Offset (marker local space, meters)")]
        [SerializeField, Tooltip("Left (negative) / Right (positive) — along marker's X axis")]
        private float offsetX = 0f;
        [SerializeField, Tooltip("Down (negative) / Up (positive) — along marker's Y axis (keep 0 for most cases)")]
        private float offsetY = 0f;
        [SerializeField, Tooltip("Backward (negative) / Forward (positive) — along marker's Z axis")]
        private float offsetZ = 0f;

        [Header("Prefab Scale (relative to marker)")]
        [SerializeField, Tooltip("Multiplier for the prefab's size. 1.0 = exact size of the physical ArUco marker. Increase this value to make your prefab larger (e.g. 5-20 for a building model). This fixes the 'prefab size ignored' issue.")]
        private float scaleMultiplier = 1.0f;

        [Header("XR Origin (for correct world-space marker poses)")]
        [SerializeField, Tooltip("XR Origin component (usually auto-found). Required for accurate placement relative to real world. Add an XR Origin to your scene if missing.")]
        private XROrigin xrOrigin;

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
        private float arucoLength = 0.15f;          // configured marker size (reliable for scale)

        void OnValidate()
        {
            if (xrOrigin == null)
            {
                xrOrigin = FindAnyObjectByType<XROrigin>();
            }
        }

        void Start()
        {
            markerFeature = OpenXRSettings.Instance.GetFeature<MagicLeapMarkerUnderstandingFeature>();

            if (markerFeature == null)
            {
                Debug.LogError("❌ MagicLeapMarkerUnderstandingFeature not found! Make sure it's enabled in XR Plug-in Management → OpenXR.");
                return;
            }

            if (xrOrigin == null)
            {
                xrOrigin = FindAnyObjectByType<XROrigin>();
                if (xrOrigin == null)
                {
                    Debug.LogWarning("⚠️ No XROrigin found in scene. Marker poses will use direct assignment (may cause incorrect placement). Add an XR Origin for best results.");
                }
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
            arucoLength = 0.15f;   // meters — must match real physical size of your printed ArUco
            markerDetectorSettings.ArucoSettings.ArucoLength = arucoLength;

            markerDetectorSettings.ArucoSettings.EstimateArucoLength = false;

            markerFeature.CreateMarkerDetector(markerDetectorSettings);

            Debug.Log($"✅ Personal ArUco tracker created (target ID = {targetArucoID}, size = {arucoLength * 1000f} mm)");
        }

        void Update()
        {
            if (markerFeature == null)
            {
                return;
            }

            var sb = new StringBuilder($"Marker Detectors Created: {markerFeature.MarkerDetectors.Count}");
            destroyAllButton.interactable = markerFeature.MarkerDetectors.Count > 0;

            if (markerFeature.MarkerDetectors.Count == 0)
            {
                statusTextDisplay.text = sb.ToString();
                return;
            }

            markerFeature.UpdateMarkerDetectors();

            bool currentlyVisible = false;   // only true this frame if GOOD marker data seen right now

            foreach (var markerDetector in markerFeature.MarkerDetectors)
            {
                if (markerDetector.Settings.MarkerType != MarkerType.Aruco)
                    continue;

                for (int i = 0; i < markerDetector.Data.Count; i++)
                {
                    var data = markerDetector.Data[i];

                    if (data.MarkerPose == null || data.MarkerNumber != targetArucoID)
                        continue;

                    Pose pose = data.MarkerPose.Value;

                    // === KEY FIX: Skip invalid/zero poses sometimes returned by the OpenXR feature ===
                    // This prevents snapping the prefab to origin or causing per-frame jumps/blinks.
                    if (pose.position.sqrMagnitude < 0.0001f)
                    {
                        continue;
                    }

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
                        // === KEY FIX: Transform relative pose to world space using XROrigin ===
                        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
                            ? xrOrigin.CameraFloorOffsetObject.transform
                            : null;

                        Vector3 markerWorldPos;
                        Quaternion markerWorldRot;

                        if (originT != null)
                        {
                            markerWorldPos = originT.TransformPoint(pose.position);
                            markerWorldRot = originT.rotation * pose.rotation;
                        }
                        else
                        {
                            // Fallback (old behavior) if no XR Origin — may be incorrect but prevents total failure
                            markerWorldPos = pose.position;
                            markerWorldRot = pose.rotation;
                        }

                        // Apply user-controlled offset in marker local space (now in world)
                        Vector3 markerLocalOffset = new Vector3(offsetX, offsetY, offsetZ);
                        Vector3 worldOffset = markerWorldRot * markerLocalOffset;
                        Vector3 finalPosition = markerWorldPos + worldOffset;

                        Quaternion rotOffsetQuat = Quaternion.Euler(rotationOffset);
                        Quaternion finalRotation = markerWorldRot * rotOffsetQuat;

                        currentCustomInstance.transform.SetPositionAndRotation(finalPosition, finalRotation);

                        // Reliable scale using configured length (ignores potentially bad data.MarkerLength)
                        currentCustomInstance.transform.localScale = Vector3.one * arucoLength * scaleMultiplier;
                    }

                    sb.AppendLine($"\nTracking ID {data.MarkerNumber} at {pose.position}");
                }
            }

            // PERMANENCE: Once seen with good data, the prefab STAYS visible at the LAST known good position/rotation/scale
            if (currentCustomInstance != null)
            {
                currentCustomInstance.SetActive(true);   // never hide again after first good detection
            }

            // Helpful status feedback
            if (hasEverBeenSeen && !currentlyVisible)
            {
                sb.AppendLine($"\n🟡 LAST SEEN POSITION (ArUco ID {targetArucoID} no longer visible or bad data)");
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