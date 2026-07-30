using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using MagicLeap.Android;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;
using Microsoft.MixedReality.WorldLocking.Core;
public class ArucoMarkerManager : MonoBehaviour
{
    public static ArucoMarkerManager Instance { get; private set; }

    [Header("=== General Settings ===")]
    [Tooltip("The root GameObject of the building/prefab that is being world-locked.")]
    [SerializeField] private GameObject buildingRoot;

    [Header("Global ArUco Detector Settings")]
    [SerializeField] private ArucoType arucoDictionary = ArucoType.Dictionary_5x5_250;
    [SerializeField] private float arucoPhysicalLengthMeters = 0.15f;
    [SerializeField] private bool estimateArucoLength = false;

    // Throttled detector profile. The Default profile analyses frames at full rate;
    // across the ~26 scans this component performs during alignment that drives CVIP
    // memory growth, and CVIP memory exhaustion is what segfaults pw_service later in
    // the session. We cut the NUMBER of frames analysed (FPS + analysis interval) but
    // deliberately keep per-frame precision (resolution, corner + edge refinement) so
    // marker pose accuracy -- and therefore elevErr -- is unaffected.
    // Set useThrottledProfile = false to restore the previous Default behaviour.
    [Header("Detector profile (CVIP memory mitigation)")]
    [SerializeField] private bool useThrottledProfile = true;
    [SerializeField] private MarkerDetectorFPS throttledFps = MarkerDetectorFPS.Low;
    [SerializeField] private MarkerDetectorFullAnalysisInterval throttledAnalysisInterval = MarkerDetectorFullAnalysisInterval.Medium;
    [SerializeField] private MarkerDetectorResolution throttledResolution = MarkerDetectorResolution.High;
    [SerializeField] private MarkerDetectorCornerRefineMethod throttledCornerRefinement = MarkerDetectorCornerRefineMethod.Contour;
    [SerializeField] private bool throttledEdgeRefinement = true;
    // World = multi-camera, wider coverage. Kept as the default because the space-pin
    // markers are spread around the building and detection reliability matters more
    // here than the small extra cost of the second camera.
    [SerializeField] private MarkerDetectorCamera throttledCamera = MarkerDetectorCamera.World;

    [Header("=== Controller Offset Adjustment ===")]
    // OFF by default, and it should stay off for a World-Locking build. Three reasons:
    //
    //  1. It fights WLT. The SpacePins align the model by correcting the camera, and each
    //     ArucoPinDriver locks against its pin's authored ModelingPoseGlobal (including the
    //     elevation lock). Moving buildingRoot after pins have locked means those authored
    //     poses no longer describe where the model actually is.
    //  2. It breaks static batching. A batched renderer's geometry is baked into world
    //     space; move the transform and the visuals stop tracking it while the colliders
    //     still move, so gaze raycasts hit geometry that isn't where it appears to be.
    //  3. It is reachable mid-trial. This component keeps running after the marker
    //     detector is destroyed, so a participant brushing the thumbstick could shift the
    //     entire world model during a recorded trial.
    //
    // Turn it on only for manual calibration work, in a build where the model is not
    // marked Batching Static.
    [SerializeField] private bool enableControllerAdjustment = false;
    [SerializeField] private float offsetAdjustSpeed = 0.8f;
    [SerializeField] private float rotationAdjustSpeed = 45f;
    [SerializeField] private float inputDeadzone = 0.12f;
    [SerializeField] private bool enableZAxisRotation = true;

    // Runtime state
    private MagicLeapMarkerUnderstandingFeature markerFeature;
    // The single detector THIS manager owns (drives the building's SpacePins).
    // Tracked so we can tear down ONLY the space-pin detector without touching
    // other detectors on the shared feature singleton (e.g. MapTracking's ID-88
    // detector), which must keep running after the space pins are disabled.
    private MarkerDetector _spacePinDetector;
    private bool hasInitializedDetector = false;
    private bool _alreadyDestroyed = false;

    private Dictionary<ulong, ArucoPinDriver> _pinDrivers = new Dictionary<ulong, ArucoPinDriver>();

    private List<InputDevice> rightHandDevices = new List<InputDevice>();
    private List<InputDevice> leftHandDevices = new List<InputDevice>();

    public Orienter SharedOrienter { get; private set; }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            var orienterObj = new GameObject("ArUcoOrienter");
            SharedOrienter = orienterObj.AddComponent<Orienter>();
        }
        else Destroy(gameObject);
    }

    public void RegisterDriver(ulong arucoID, ArucoPinDriver driver)
    {
        _pinDrivers[arucoID] = driver;
        Debug.Log($"[ArucoMarkerManager] Registered driver for ArUco ID {arucoID}");
    }

    public void UnregisterDriver(ulong arucoID)
    {
        if (_pinDrivers.ContainsKey(arucoID))
        {
            _pinDrivers.Remove(arucoID);
            Debug.Log($"[ArucoMarkerManager] Unregistered driver for ArUco ID {arucoID}");
        }
    }

    private IEnumerator Start()
    {
        yield return new WaitUntil(AreSubsystemsLoaded);

        markerFeature = OpenXRSettings.Instance.GetFeature<MagicLeapMarkerUnderstandingFeature>();
        if (markerFeature == null)
        {
            Debug.LogError("❌ Magic Leap Marker feature missing.");
            enabled = false;
            yield break;
        }

        MarkerDetectorPump.Instance.Register(markerFeature);
        Permissions.RequestPermission(Permissions.SpaceImportExport, OnSpacePermissionGranted, OnPermissionDenied);
        CreateMarkerDetector();

        // Only attach the grab rig when manual adjustment is actually wanted. Adding a
        // Rigidbody + XRGrabInteractable to the building root makes it movable, which is
        // exactly what static batching and the SpacePin alignment need it not to be.
        if (buildingRoot != null && enableControllerAdjustment)
        {
            SetupGrabInteraction(buildingRoot);
        }
    }

    private bool AreSubsystemsLoaded()
    {
        return XRGeneralSettings.Instance?.Manager?.activeLoader != null;
    }

    private void CreateMarkerDetector()
    {
        if (hasInitializedDetector || markerFeature == null) return;

        var settings = new MarkerDetectorSettings
        {
            MarkerDetectorProfile = useThrottledProfile
                ? MarkerDetectorProfile.Custom
                : MarkerDetectorProfile.Default,
            MarkerType = MarkerType.Aruco,
            ArucoSettings = new ArucoSettings
            {
                ArucoType = arucoDictionary,
                ArucoLength = arucoPhysicalLengthMeters,
                EstimateArucoLength = estimateArucoLength
            }
        };

        if (useThrottledProfile)
        {
            settings.CustomProfileSettings = new CustomProfileSettings
            {
                FPSHint = throttledFps,                         // fewer frames analysed
                AnalysisInterval = throttledAnalysisInterval,   // less frequent full analysis
                ResolutionHint = throttledResolution,           // kept high: pose accuracy
                CornerRefinement = throttledCornerRefinement,   // kept: sub-pixel corners
                UseEdgeRefinement = throttledEdgeRefinement,    // kept: pose accuracy
                CameraHint = throttledCamera
            };
        }

        _spacePinDetector = markerFeature.CreateMarkerDetector(settings);
        hasInitializedDetector = true;
        Debug.Log($"[ArucoMarkerManager] Space-pin detector created " +
                  $"(profile: {(useThrottledProfile ? $"Custom throttled — fps {throttledFps}, interval {throttledAnalysisInterval}, res {throttledResolution}, corners {throttledCornerRefinement}, cam {throttledCamera}" : "Default")}).");
    }

    private void OnSpacePermissionGranted(string permission) { }
    private void OnPermissionDenied(string permission) { }

    private void Update()
    {
        if (markerFeature == null || markerFeature.MarkerDetectors.Count == 0) return;

        float now = Time.time;

        // Collect detections and dispatch. Only read from OUR detector so that
        // other detectors on the shared feature (e.g. MapTracking's) are never
        // touched here -- and so that once _spacePinDetector is destroyed, the
        // space pins go silent while the map keeps tracking.
        if (_spacePinDetector != null)
        {
            foreach (var data in _spacePinDetector.Data)
            {
                if (data.MarkerPose == null || !data.MarkerNumber.HasValue) continue;
                ulong id = data.MarkerNumber.Value;

                Pose trackingPose = data.MarkerPose.Value;
                if (trackingPose.position.sqrMagnitude < 0.0001f) continue;

                if (_pinDrivers.TryGetValue(id, out var driver))
                {
                    driver.ReceiveMarkerPose(trackingPose, now);
                }
                else
                {
                    if (Time.frameCount % 60 == 0) // Log once per second roughly
                        Debug.LogWarning($"[ArucoMarkerManager] Detected ArUco {id} but no driver is registered for it.");
                }
            }
        }

        // Ordered so the disabled case costs one bool test: IsSharedInstanceGrabbed does a
        // GetComponent every frame, and HandleControllerOffsetAdjustment polls XR devices.
        if (enableControllerAdjustment && !IsSharedInstanceGrabbed())
            HandleControllerOffsetAdjustment();
    }

    void SetupGrabInteraction(GameObject instance)
    {
        if (instance == null) return;
        var rb = instance.GetComponent<Rigidbody>();
        if (rb == null) rb = instance.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        var grab = instance.GetComponent<XRGrabInteractable>();
        if (grab == null) grab = instance.AddComponent<XRGrabInteractable>();

        grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
        grab.trackPosition = true;
        grab.trackRotation = false;
        grab.throwOnDetach = false;
        grab.retainTransformParent = false;
        grab.smoothPosition = false;
        grab.smoothRotation = false;
        grab.useDynamicAttach = true;
        grab.matchAttachPosition = true;
        grab.matchAttachRotation = true;
        grab.snapToColliderVolume = false;
    }

    void LateUpdate()
    {
        // Checked first so the per-frame GetComponent and XR device queries below don't
        // run at all in a normal (non-calibration) session.
        if (!enableControllerAdjustment) return;
        if (buildingRoot == null) return;
        var grab = buildingRoot.GetComponent<XRGrabInteractable>();
        if (grab == null || !grab.isSelected || grab.interactorsSelecting.Count == 0) return;

        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightHandDevices);
        if (rightHandDevices.Count == 0) return;

        var dev = rightHandDevices[0];
        if (!dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis)) return;
        if (Mathf.Abs(axis.x) <= inputDeadzone && Mathf.Abs(axis.y) <= inputDeadzone) return;

        Vector3 pivot = grab.interactorsSelecting[0].transform.position;
        float rotSpeed = rotationAdjustSpeed * Time.deltaTime;

        if (Mathf.Abs(axis.x) > inputDeadzone && enableZAxisRotation)
            buildingRoot.transform.RotateAround(pivot, buildingRoot.transform.forward, axis.x * rotSpeed);
    }

    private bool IsSharedInstanceGrabbed()
    {
        if (buildingRoot == null) return false;
        var grab = buildingRoot.GetComponent<XRGrabInteractable>();
        return grab != null && grab.isSelected;
    }

    private void HandleControllerOffsetAdjustment()
    {
        if (buildingRoot == null) return;

        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightHandDevices);
        InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, leftHandDevices);
        float speed = offsetAdjustSpeed * Time.deltaTime;

        if (rightHandDevices.Count > 0 && rightHandDevices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 rAxis))
        {
            Vector3 pos = buildingRoot.transform.position;
            if (Mathf.Abs(rAxis.x) > inputDeadzone) pos.x += rAxis.x * speed;
            if (Mathf.Abs(rAxis.y) > inputDeadzone) pos.z += rAxis.y * speed;
            buildingRoot.transform.position = pos;
        }

        if (leftHandDevices.Count > 0 && leftHandDevices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 lAxis))
        {
            if (Mathf.Abs(lAxis.y) > inputDeadzone)
            {
                Vector3 pos = buildingRoot.transform.position;
                pos.y += lAxis.y * speed;
                buildingRoot.transform.position = pos;
            }
        }
    }

    public void DestroyMarkerTrackers()
    {
        // Destroy ONLY the space-pin detector. Previously this called
        // DestroyAllMarkerDetectors(), which also tore down MapTracking's detector
        // on the shared feature singleton -- killing the map. Destroying just our
        // own detector leaves any other detector (the map) running.
        if (markerFeature != null && _spacePinDetector != null && !_alreadyDestroyed)
        {
            markerFeature.DestroyMarkerDetector(_spacePinDetector);
            _spacePinDetector = null;
            _alreadyDestroyed = true;
            hasInitializedDetector = false;
            Debug.Log("[ArucoMarkerManager] Space-pin marker detector destroyed to save performance (map detector left intact).");
        }
    }

    private void OnDestroy()
    {
        if (markerFeature != null && _spacePinDetector != null && !_alreadyDestroyed)
        {
            markerFeature.DestroyMarkerDetector(_spacePinDetector);
            _spacePinDetector = null;
            _alreadyDestroyed = true;
            Debug.Log("[MarkerDet] space-pin detector destroyed");
        }
        else if (_alreadyDestroyed)
        {
            Debug.Log("[MarkerDet] already destroyed – skip");
        }
        hasInitializedDetector = false;
    }
}
