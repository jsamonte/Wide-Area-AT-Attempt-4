using System;
using MagicLeap.Android;
using MagicLeap.OpenXR.Features.EyeTracker;
using UnityEngine;
using UnityEngine.XR.OpenXR;

namespace ARCockpit.Study
{
    /// <summary>
    /// The one writer of <see cref="EyeData"/>. Owns the Magic Leap 2 eye tracker (XR_ML_eye_tracker via
    /// MagicLeapEyeTrackerFeature), requests the two permissions it needs, and pushes real per-eye data
    /// into the static holder each frame. Put ONE of these on the Bootstrap object in Systems.
    ///
    /// This uses the ML eye tracker feature rather than the plain OpenXR eye-gaze interaction profile
    /// because the study needs more than a gaze ray: per-eye poses with confidence, eye openness, pupil
    /// diameter, and the device's own gaze-behavior classifier (fixation / saccade / pursuit / blink with
    /// onset and duration). The interaction profile gives only the combined ray.
    ///
    /// SETUP (all three or you get nothing):
    ///   1. OpenXR > Android > enable "Magic Leap 2 Eye Tracker".
    ///   2. Manifest: com.magicleap.permission.EYE_TRACKING and com.magicleap.permission.PUPIL_SIZE.
    ///      Pupil size is a SEPARATE gate; without it the pupil fields stay invalid but the rest works.
    ///   3. The participant grants both at the runtime prompt on first launch.
    ///
    /// In the Editor the ML permission helper auto-grants and the feature is not running, so this no-ops
    /// cleanly: EyeData.Tracking stays false and nothing downstream breaks. It never hard-requires a device.
    ///
    ///     adb logcat -d -s Unity | Select-String "\[EYE\]"
    /// </summary>
    public class EyeTracker : MonoBehaviour
    {
        [Tooltip("Leave empty to use the auto-loaded Resources/StudyProfile.")]
        public StudyProfile profileOverride;

        // Both knobs live on StudyProfile so they are one value, visible on the 231 window and stamped into
        // the trial header. requestPupilSize is read ONCE, in Awake, before the tracker is created: the native
        // tracker locks its data streams to the permissions it holds at creation time, so changing it later in
        // the session cannot do anything. Reading it live would be a knob that lies about taking effect.
        StudyProfile Profile => profileOverride != null ? profileOverride : StudyProfile.Active;
        bool RequestPupilSize => Profile == null || Profile.requestPupilSize;
        float LogInterval => Profile != null ? Profile.eyeLogIntervalSeconds : 5f;

        [Tooltip("The XR tracking-space transform that eye poses are relative to. Leave empty to " +
                 "auto-resolve to the head camera's parent (the XR Origin's camera offset).")]
        public Transform trackingSpace;

        MagicLeapEyeTrackerFeature _feature;
        bool _created;
        bool _failed;
        float _logTimer;

        // The native tracker gates its data streams by the permissions held AT CREATION TIME. The pupil
        // permission resolves asynchronously and can land seconds after eye tracking does, so creating
        // the tracker as soon as EYE_TRACKING lands comes up permanently pupil-blind. We wait for every
        // permission we asked for to settle (granted or denied) before creating.
        bool _eyeSettled;
        bool _pupilSettled;
        bool _resultsLogged;

        void Start()
        {
            _feature = OpenXRSettings.Instance != null
                ? OpenXRSettings.Instance.GetFeature<MagicLeapEyeTrackerFeature>()
                : null;

            if (_feature == null || !_feature.enabled)
            {
                Debug.LogWarning("[EYE] Magic Leap 2 Eye Tracker OpenXR feature is not enabled " +
                                 "(OpenXR > Android). No eye data this run.");
                _failed = true;
                return;
            }

            Permissions.RequestPermission(
                Permissions.EyeTracking,
                _ => { EyeData.PermissionGranted = true; _eyeSettled = true; Debug.Log("[EYE] EYE_TRACKING granted."); },
                p => { _failed = true; _eyeSettled = true; Debug.LogError($"[EYE] {p} DENIED. No eye data this run."); },
                p => { _failed = true; _eyeSettled = true; Debug.LogError($"[EYE] {p} denied permanently. Clear app permissions to re-prompt."); });

            if (RequestPupilSize)
            {
                Permissions.RequestPermission(
                    Permissions.PupilSize,
                    _ => { EyeData.PupilPermissionGranted = true; _pupilSettled = true; Debug.Log("[EYE] PUPIL_SIZE granted."); },
                    p => { _pupilSettled = true; Debug.LogWarning($"[EYE] {p} denied. Pupil diameter will log as invalid; the rest still works."); },
                    p => { _pupilSettled = true; Debug.LogWarning($"[EYE] {p} denied permanently. Pupil diameter will log as invalid."); });
            }
            else
            {
                _pupilSettled = true;
            }
        }

        void Update()
        {
            if (_failed || !_eyeSettled || !_pupilSettled) return;

            if (!_created)
            {
                // The tracker can only be created once the OpenXR session exists, so we try each frame
                // after the permission lands rather than assuming Start is late enough.
                try
                {
                    _feature.CreateEyeTracker();
                    _created = true;
                    Debug.Log("[EYE] Eye tracker created.");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    Debug.LogError($"[EYE] CreateEyeTracker failed: {ex.GetType().Name}: {ex.Message}");
                    return;
                }
            }

            try
            {
                Sample();
            }
            catch (Exception ex)
            {
                _failed = true;
                EyeData.MarkNotTracking();
                Debug.LogError($"[EYE] Sampling failed, stopping: {ex.GetType().Name}: {ex.Message}");
            }
        }

        void Sample()
        {
            Transform space = ResolveTrackingSpace();

            PosesData poses = _feature.GetPosesData();
            WritePose(poses.GazePose, space, ref EyeData.GazeValid, ref EyeData.GazeOrigin,
                      ref EyeData.GazeDirection, ref EyeData.GazeConfidence);
            WritePose(poses.LeftPose, space, ref EyeData.LeftPoseValid, ref EyeData.LeftOrigin,
                      ref EyeData.LeftDirection, ref EyeData.LeftConfidence);
            WritePose(poses.RightPose, space, ref EyeData.RightPoseValid, ref EyeData.RightOrigin,
                      ref EyeData.RightDirection, ref EyeData.RightConfidence);

            EyeData.FixationValid = poses.FixationPose.Valid;
            if (poses.FixationPose.Valid)
            {
                EyeData.FixationPoint = space != null
                    ? space.TransformPoint(poses.FixationPose.Pose.position)
                    : poses.FixationPose.Pose.position;
                EyeData.FixationConfidence = (int)poses.FixationPose.Confidence;
            }

            EyeData.DeviceTimeNs = poses.GazePose.Time;

            GeometricData[] geo = _feature.GetGeometricData();
            for (int i = 0; i < geo.Length; i++)
            {
                if (geo[i].Eye == Eye.Left)
                {
                    EyeData.LeftGeometryValid = geo[i].Valid;
                    if (geo[i].Valid) EyeData.LeftOpenness = geo[i].EyeOpenness;
                }
                else
                {
                    EyeData.RightGeometryValid = geo[i].Valid;
                    if (geo[i].Valid) EyeData.RightOpenness = geo[i].EyeOpenness;
                }
            }

            if (EyeData.PupilPermissionGranted)
            {
                PupilData[] pupils = _feature.GetPupilData();

                // One-shot: dump the raw XrResult codes. If pupil comes back invalid, this separates a
                // permission/creation-order problem (an error result) from an uncalibrated-eyes problem
                // (a success result carrying Valid=false), which we cannot tell apart from the values.
                if (!_resultsLogged && pupils.Length > 0)
                {
                    _resultsLogged = true;
                    Debug.Log($"[EYE] result codes: poses={poses.Result} geometric={geo[0].Result} " +
                              $"pupil={pupils[0].Result} pupilValid L/R={pupils[0].Valid}/{pupils[1].Valid}");
                }

                for (int i = 0; i < pupils.Length; i++)
                {
                    if (pupils[i].Eye == Eye.Left)
                    {
                        EyeData.LeftPupilValid = pupils[i].Valid;
                        if (pupils[i].Valid) EyeData.LeftPupilDiameter = pupils[i].PupilDiameter;
                    }
                    else
                    {
                        EyeData.RightPupilValid = pupils[i].Valid;
                        if (pupils[i].Valid) EyeData.RightPupilDiameter = pupils[i].PupilDiameter;
                    }
                }
            }

            GazeBehavior behavior = _feature.GetGazeBehavior();
            EyeData.BehaviorValid = behavior.Valid;
            if (behavior.Valid)
            {
                EyeData.Behavior = behavior.GazeBehaviorType.ToString();
                EyeData.BehaviorOnsetTimeNs = behavior.OnsetTime;
                EyeData.BehaviorDurationNs = behavior.Duration;
                if (behavior.MetaData.Valid)
                {
                    EyeData.BehaviorAmplitude = behavior.MetaData.Amplitude;
                    EyeData.BehaviorDirection = behavior.MetaData.Direction;
                    EyeData.BehaviorVelocity = behavior.MetaData.Velocity;
                }
            }

            EyeData.Tracking = EyeData.GazeValid;

            Heartbeat();
        }

        static void WritePose(PoseData src, Transform space, ref bool valid, ref Vector3 origin,
                              ref Vector3 direction, ref int confidence)
        {
            valid = src.Valid;
            if (!src.Valid) return;

            // Eye poses come back in the OpenXR app space, which is the XR rig's tracking space, not
            // world. Push them through the rig transform so a gaze raycast hits scene colliders.
            if (space != null)
            {
                origin = space.TransformPoint(src.Pose.position);
                direction = space.TransformDirection(src.Pose.rotation * Vector3.forward);
            }
            else
            {
                origin = src.Pose.position;
                direction = src.Pose.rotation * Vector3.forward;
            }
            confidence = (int)src.Confidence;
        }

        Transform ResolveTrackingSpace()
        {
            if (trackingSpace != null) return trackingSpace;

            var cam = Camera.main;
            if (cam == null) return null;

            // The head camera's parent is the XR Origin's camera offset, which is the transform that
            // carries the rig's world placement. Poses are relative to it.
            trackingSpace = cam.transform.parent != null ? cam.transform.parent : cam.transform.root;
            Debug.Log($"[EYE] Tracking space resolved to '{trackingSpace.name}'.");
            return trackingSpace;
        }

        void Heartbeat()
        {
            if (LogInterval <= 0f) return;
            _logTimer += Time.unscaledDeltaTime;
            if (_logTimer < LogInterval) return;
            _logTimer = 0f;

            // Two things to eyeball on device. (1) With the head still and the eyes looking straight
            // ahead, gaze dir should roughly match head fwd; if it is wildly off, the tracking-space
            // conversion is wrong. (2) The pupil magnitude settles the units question: ~0.003 means
            // meters, ~3.0 means millimeters. EyeData documents this as unconfirmed until we read it here.
            var cam = Camera.main;
            Vector3 fwd = cam != null ? cam.transform.forward : Vector3.zero;
            float dot = EyeData.GazeValid && cam != null ? Vector3.Dot(EyeData.GazeDirection.normalized, fwd) : 0f;

            Debug.Log($"[EYE] tracking={EyeData.Tracking} gazeValid={EyeData.GazeValid} conf={EyeData.GazeConfidence} " +
                      $"dir={EyeData.GazeDirection:F2} (dot with head fwd {dot:F2}) " +
                      $"open L/R={EyeData.LeftOpenness:F2}/{EyeData.RightOpenness:F2} " +
                      $"pupil L/R={EyeData.LeftPupilDiameter:F4}/{EyeData.RightPupilDiameter:F4} " +
                      $"(valid {EyeData.LeftPupilValid}/{EyeData.RightPupilValid}) " +
                      $"behavior={EyeData.Behavior} vel={EyeData.BehaviorVelocity:F1}");
        }

        void OnDestroy()
        {
            if (_created)
            {
                try { _feature.DestroyEyeTracker(); } catch { }
                _created = false;
            }
            EyeData.MarkNotTracking();
        }
    }
}
