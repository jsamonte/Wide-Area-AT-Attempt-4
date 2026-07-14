using UnityEngine;

namespace ARCockpit.Study
{
    /// <summary>
    /// Current eye state, written by exactly one <see cref="EyeTracker"/> and read by everything else
    /// (the trial DataLogger, AOI dwell, any gaze-driven visual). Same contract as
    /// <see cref="ARCockpit.Data.FlightData"/>: one writer, static holder, consumers do not care where
    /// the data came from.
    ///
    /// Everything here is REAL device data or it is marked invalid. Nothing is synthesized. If the eye
    /// tracker is not running (Editor, permission denied, no ML2) then Tracking is false and the fields
    /// hold their last values, so a logger must write the Tracking / *Valid flags alongside the numbers
    /// and treat them as authoritative.
    ///
    /// Poses are in WORLD space (the tracker converts out of the XR tracking space), so a gaze raycast
    /// works directly against scene colliders.
    /// </summary>
    public static class EyeData
    {
        /// <summary>True while the eye tracker is created, permitted, and returning data this frame.</summary>
        public static bool Tracking;

        /// <summary>True if the eye-tracking permission was granted. False means the participant denied it.</summary>
        public static bool PermissionGranted;

        /// <summary>True if the pupil-size permission was granted (a separate gate from eye tracking).</summary>
        public static bool PupilPermissionGranted;

        // Combined gaze ray, world space. This is the one to raycast for AOI hits.
        public static bool GazeValid;
        public static Vector3 GazeOrigin;
        public static Vector3 GazeDirection;
        public static int GazeConfidence;       // 0 low, 1 medium, 2 high

        // Per-eye rays, world space. Useful for vergence / depth-of-fixation analysis offline.
        public static bool LeftPoseValid;
        public static Vector3 LeftOrigin;
        public static Vector3 LeftDirection;
        public static int LeftConfidence;

        public static bool RightPoseValid;
        public static Vector3 RightOrigin;
        public static Vector3 RightDirection;
        public static int RightConfidence;

        // Fixation pose: where the device thinks the eyes are converged, world space.
        public static bool FixationValid;
        public static Vector3 FixationPoint;
        public static int FixationConfidence;

        // Eye openness, 0 closed to 1 fully open. A blink shows up here as well as in Behavior.
        public static bool LeftGeometryValid;
        public static float LeftOpenness;
        public static bool RightGeometryValid;
        public static float RightOpenness;

        // Pupil diameter, in METERS (confirmed on device 2026-07-13: a calibrated eye reads ~0.0034, i.e.
        // 3.4 mm, a normal diameter). The SDK headers do not document the unit, so this was settled by
        // reading the live magnitude. Multiply by 1000 for the millimeters that pupillometry papers use.
        // Requires the PUPIL_SIZE permission AND a calibrated headset: uncalibrated eyes return
        // Valid = false with a Success result code, which looks identical to a permission failure.
        public static bool LeftPupilValid;
        public static float LeftPupilDiameter;
        public static bool RightPupilValid;
        public static float RightPupilDiameter;

        // Device gaze-behavior classifier: Fixation, Saccade, Pursuit, Blink, EyesClosed, Unknown.
        // This is a real gift for the EEG work: saccade onset and fixation duration come free.
        public static bool BehaviorValid;
        public static string Behavior = "";
        public static float BehaviorAmplitude;   // degrees
        public static float BehaviorDirection;   // degrees
        public static float BehaviorVelocity;    // degrees per second
        public static long BehaviorOnsetTimeNs;
        public static ulong BehaviorDurationNs;

        /// <summary>Device timestamp of the latest gaze pose, nanoseconds on the OpenXR clock.</summary>
        public static long DeviceTimeNs;

        /// <summary>Clears the live flags. Called when the tracker stops so nothing reads stale poses as live.</summary>
        public static void MarkNotTracking()
        {
            Tracking = false;
            GazeValid = false;
            LeftPoseValid = false;
            RightPoseValid = false;
            FixationValid = false;
            LeftGeometryValid = false;
            RightGeometryValid = false;
            LeftPupilValid = false;
            RightPupilValid = false;
            BehaviorValid = false;
        }
    }
}
