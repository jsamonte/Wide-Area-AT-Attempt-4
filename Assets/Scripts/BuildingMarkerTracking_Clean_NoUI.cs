// %BANNER_BEGIN%
// ---------------------------------------------------------------------
// %COPYRIGHT_BEGIN%
// Copyright (c) (2024) Magic Leap, Inc. All Rights Reserved.
// %COPYRIGHT_END%
// ---------------------------------------------------------------------
// %BANNER_END%
//
// FIXED VERSION (CLEANED - NO UI): Addresses lag + per-frame blinking/jitter after spawn.
// Root cause: 
//   1. Marker poses from MagicLeapMarkerUnderstandingFeature are RELATIVE to XROrigin.CameraFloorOffsetObject (not world space).
//      Original direct assignment caused incorrect placement, jumps, and perceived lag/blink as tracking/origin updated.
//   2. Intermittent zero/invalid MarkerPose or MarkerLength data from the OpenXR feature (known community issue, especially with Estimate=false).
//      Caused snaps to origin or skipped updates that looked like blinking.
//   3. UI status spam every frame + reliance on potentially bad data.MarkerLength for scale.
//
// Changes made (UI removal edition):
// - ALL UI components removed (Dropdowns, Toggles, Sliders, Texts, Buttons, listeners, callback methods).
//   This makes the script fully standalone and scene-independent. No more null-reference issues from missing UI elements.
// - Status/feedback now uses clean Debug.Log transitions (acquire/lost) instead of per-frame UI text spam.
// - Core tracking, permanence, XROrigin world-space transform, validation, and offset/scale/rotation logic preserved 100%.
// - Hardcoded ArUco setup (target ID + size) remains — just set targetArucoID and arucoLength in inspector or code.
// - Auto-finds XROrigin if not assigned.
// - Fallback to direct pose if no XROrigin (for compatibility).
// - DestroyMarkerTrackers made public so you can call it from other scripts or a key press if needed.
//
// Usage (now much simpler):
// 1. Replace your old BuildingMarkerTracking1.cs with this cleaned version (or copy contents into your script).
// 2. In Inspector on the GameObject with this script: assign your "1st Building 3 Wireframe.prefab" (or desired custom prefab) to customMarkerPrefab.
// 3. Set targetArucoID to YOUR printed marker's ID (default 88).
// 4. Tune offsetX/Y/Z (meters, marker-local) and scaleMultiplier (start with 5-20 for building to look reasonable size next to 15cm marker).
// 5. Test rotationOffset (270 on X is common; try -90, 0, or 180 if model lies flat/wrong way).
// 6. Ensure good even lighting on the physical ArUco marker for reliable detection.
// 7. Make sure your scene has an XR Origin (with CameraFloorOffsetObject) and the Magic Leap Marker Understanding feature is enabled.
// 8. Add MARKER_TRACKING permission in Magic Leap Manifest Settings.
// 9. Build & run on Magic Leap 2. The prefab should now STICK stably to the real marker (or last known pose) without jumping, blinking, or UI dependencies.
//
// If still issues: 
// - Confirm MagicLeapMarkerUnderstandingFeature enabled in Project Settings > XR Plug-in Management > OpenXR.
// - Add MARKER_TRACKING permission.
// - Check device logs for pose/detector warnings.
// - Try EstimateArucoLength = true in CreateHardcoded for potentially more stable data (slight accuracy trade-off).
// - Your scene must have an XR Origin for best world-space results.

using UnityEngine;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;
using Unity.XR.CoreUtils;   // For XROrigin

namespace MagicLeap.Examples
{
    public class BuildingMarkerTracking_Clean_NoUI : MonoBehaviour
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

        [SerializeField, Tooltip("Rotation offset to apply to the prefab")]
        private float offset = 270f;

        private Vector3 rotationOffset = new Vector3(270f, 0f, 0f);
        private MagicLeapMarkerUnderstandingFeature markerFeature;
        private MarkerDetectorSettings markerDetectorSettings;
        private GameObject currentCustomInstance;   // tracks your spawned prefab
        private bool hasEverBeenSeen = false;       // permanence (last known pose)
        private float arucoLength = 0.15f;          // configured marker size (reliable for scale)
        private bool wasVisibleLastFrame = false;   // for clean transition logging only

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

            if (markerFeature.MarkerDetectors.Count == 0)
            {
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
                        currentCustomInstance.SetActive(true);
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

                    // Occasional position log (every ~0.5s) to avoid console spam
                    if (Time.frameCount % 30 == 0)
                    {
                        Debug.Log($"Tracking ID {data.MarkerNumber} at {pose.position}");
                    }
                }
            }

            // PERMANENCE: Once seen with good data, the prefab STAYS visible at the LAST known good position/rotation/scale
            if (currentCustomInstance != null)
            {
                currentCustomInstance.SetActive(true);
            }

            // Clean transition logging (no per-frame spam)
            if (currentlyVisible && !wasVisibleLastFrame)
            {
                Debug.Log($"✅ Marker {targetArucoID} ACQUIRED — prefab now tracking stably.");
                wasVisibleLastFrame = true;
            }
            else if (!currentlyVisible && wasVisibleLastFrame && hasEverBeenSeen)
            {
                Debug.Log($"🟡 Marker {targetArucoID} LOST or bad data — prefab remains at LAST KNOWN pose (permanent).");
                wasVisibleLastFrame = false;
            }
        }

        void OnDestroy()
        {
            DestroyMarkerTrackers();
        }

        /// <summary>
        /// Public method to destroy the current prefab instance and all marker detectors.
        /// Call this from another script or Input if you want a reset key.
        /// </summary>
        public void DestroyMarkerTrackers()
        {
            if (currentCustomInstance != null)
            {
                Destroy(currentCustomInstance);
                currentCustomInstance = null;
            }
            hasEverBeenSeen = false;
            wasVisibleLastFrame = false;

            if (markerFeature != null)
            {
                markerFeature.DestroyAllMarkerDetectors();
            }
        }
    }
}
