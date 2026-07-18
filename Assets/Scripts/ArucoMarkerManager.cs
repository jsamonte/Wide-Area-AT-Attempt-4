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

    [Header("=== Controller Offset Adjustment ===")]
    [SerializeField] private bool enableControllerAdjustment = true;
    [SerializeField] private float offsetAdjustSpeed = 0.8f;
    [SerializeField] private float rotationAdjustSpeed = 45f;
    [SerializeField] private float inputDeadzone = 0.12f;
    [SerializeField] private bool enableZAxisRotation = true;

    // Runtime state
    private MagicLeapMarkerUnderstandingFeature markerFeature;
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

        if (buildingRoot != null)
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
            MarkerDetectorProfile = MarkerDetectorProfile.Default,
            MarkerType = MarkerType.Aruco,
            ArucoSettings = new ArucoSettings
            {
                ArucoType = arucoDictionary,
                ArucoLength = arucoPhysicalLengthMeters,
                EstimateArucoLength = estimateArucoLength
            }
        };
        markerFeature.CreateMarkerDetector(settings);
        hasInitializedDetector = true;
    }

    private void OnSpacePermissionGranted(string permission) { }
    private void OnPermissionDenied(string permission) { }

    private void Update()
    {
        if (markerFeature == null || markerFeature.MarkerDetectors.Count == 0) return;

        float now = Time.time;

        // Collect detections and dispatch
        foreach (var detector in markerFeature.MarkerDetectors)
        {
            if (detector.Settings.MarkerType != MarkerType.Aruco) continue;
            foreach (var data in detector.Data)
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

        if (!IsSharedInstanceGrabbed() && enableControllerAdjustment)
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
        if (markerFeature != null && !_alreadyDestroyed) 
        {
            markerFeature.DestroyAllMarkerDetectors();
            _alreadyDestroyed = true;
            hasInitializedDetector = false;
            Debug.Log("[ArucoMarkerManager] Marker Detectors manually destroyed to save performance.");
        }
    }

    private void OnDestroy()
    {
        if (markerFeature != null && !_alreadyDestroyed) 
        {
            markerFeature.DestroyAllMarkerDetectors();
            _alreadyDestroyed = true;
            Debug.Log("[MarkerDet] destroyed");
        }
        else if (_alreadyDestroyed)
        {
            Debug.Log("[MarkerDet] already destroyed – skip");
        }
        hasInitializedDetector = false;
    }
}
