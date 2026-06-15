// WireframeAlignment.cs
// trackRotation = false + Manual Z rotation via right thumbstick horizontal

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using MagicLeap.Android;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;
using MagicLeap.OpenXR.Features.SpatialAnchors;
using MagicLeap.OpenXR.Features.LocalizationMaps;
using MagicLeap.OpenXR.Subsystems;
using Unity.XR.CoreUtils;

public class WireframeAlignment : MonoBehaviour
{
    [Header("=== ARUCO → PREFAB MAPPINGS ===")]
    [SerializeField] private List<ArucoPrefabMapping> arucoMappings = new List<ArucoPrefabMapping>();

    [Header("Global ArUco Detector Settings")]
    [SerializeField] private ArucoType arucoDictionary = ArucoType.Dictionary_5x5_250;
    [SerializeField] private float arucoPhysicalLengthMeters = 0.15f;
    [SerializeField] private bool estimateArucoLength = false;

    [Header("XR Origin")]
    [SerializeField] private XROrigin xrOrigin;

    [Header("=== Debug Alignment Info Text ===")]
    [SerializeField] private bool showAlignmentInfoText = true;
    [SerializeField] private float textHeightAboveMarker = 0.12f;
    [SerializeField] private Color textColor = Color.cyan;
    [SerializeField] private float textWorldScale = 0.022f;
    [SerializeField] private int textFontSize = 72;

    [Header("=== Controller Offset Adjustment ===")]
    [SerializeField] private bool enableControllerAdjustment = true;
    [SerializeField] private float offsetAdjustSpeed = 0.8f;
    [SerializeField] private float inputDeadzone = 0.12f;

    [Header("=== Grab Behavior ===")]
    [SerializeField] private float scaleAdjustSpeed = 0.6f;
    [SerializeField] private float rotationSpeed = 90f; // degrees per second for Z rotation

    // Persistence
    private Dictionary<string, ulong> anchorMapPosIdToArucoID = new Dictionary<string, ulong>();
    private const string PREFS_KEY = "ArucoToSpatialAnchorMappings";

    private MagicLeapMarkerUnderstandingFeature markerFeature;
    private MagicLeapSpatialAnchorsFeature spatialAnchorsFeature;
    private MagicLeapSpatialAnchorsStorageFeature storageFeature;
    private MLXrAnchorSubsystem activeSubsystem;

    private Dictionary<ulong, ARAnchor> createdAnchorsByArucoID = new Dictionary<ulong, ARAnchor>();
    private List<ARAnchor> localAnchors = new List<ARAnchor>();
    private List<ARAnchor> storedAnchors = new List<ARAnchor>();

    private bool permissionGranted = false;
    private bool hasInitializedDetector = false;

    private ulong lastSeenArucoID = 0;

    private Dictionary<ulong, Pose> lastDetectedMarkerPoses = new Dictionary<ulong, Pose>();
    private GameObject alignmentInfoTextObj;
    private TextMesh alignmentInfoTextMesh;
    private Camera mainCamera;

    private List<InputDevice> rightHandDevices = new List<InputDevice>();
    private List<InputDevice> leftHandDevices = new List<InputDevice>();

    [Serializable]
    public class ArucoPrefabMapping
    {
        public ulong arucoID;
        public GameObject prefab;
        public float offsetX = 0f;
        public float offsetY = 0f;
        public float offsetZ = 0f;
        public float scaleMultiplier = 1.0f;
        public Vector3 rotationOffset = new Vector3(270f, 0f, 0f);
    }

    private void OnValidate()
    {
        if (xrOrigin == null)
            xrOrigin = FindAnyObjectByType<XROrigin>();
    }

    private IEnumerator Start()
    {
        yield return new WaitUntil(AreSubsystemsLoaded);

        markerFeature = OpenXRSettings.Instance.GetFeature<MagicLeapMarkerUnderstandingFeature>();
        spatialAnchorsFeature = OpenXRSettings.Instance.GetFeature<MagicLeapSpatialAnchorsFeature>();
        storageFeature = OpenXRSettings.Instance.GetFeature<MagicLeapSpatialAnchorsStorageFeature>();

        if (markerFeature == null || spatialAnchorsFeature == null || storageFeature == null)
        {
            Debug.LogError("❌ Required Magic Leap features missing.");
            enabled = false;
            yield break;
        }

        if (xrOrigin == null)
            xrOrigin = FindAnyObjectByType<XROrigin>();

        if (mainCamera == null)
        {
            mainCamera = (xrOrigin != null && xrOrigin.Camera != null) ? xrOrigin.Camera : Camera.main;
        }

        LoadAnchorMappings();

        Permissions.RequestPermission(Permissions.SpaceImportExport, OnSpacePermissionGranted, OnPermissionDenied);

        if (storageFeature != null)
            storageFeature.OnQueryComplete += OnQueryComplete;

        CreateMarkerDetector();
        InitializeAlignmentText();
    }

    private bool AreSubsystemsLoaded()
    {
        if (XRGeneralSettings.Instance?.Manager?.activeLoader == null) return false;
        activeSubsystem = XRGeneralSettings.Instance.Manager.activeLoader.GetLoadedSubsystem<XRAnchorSubsystem>() as MLXrAnchorSubsystem;
        return activeSubsystem != null;
    }

    private void LoadAnchorMappings()
    {
        if (PlayerPrefs.HasKey(PREFS_KEY))
        {
            string json = PlayerPrefs.GetString(PREFS_KEY);
            var wrapper = JsonUtility.FromJson<AnchorMappingWrapper>(json);
            if (wrapper?.mappings != null)
                anchorMapPosIdToArucoID = wrapper.mappings.ToDictionary(m => m.mapPosId, m => m.arucoID);
        }
    }

    private void SaveAnchorMappings()
    {
        var list = anchorMapPosIdToArucoID.Select(kvp => new AnchorMapping { mapPosId = kvp.Key, arucoID = kvp.Value }).ToList();
        var wrapper = new AnchorMappingWrapper { mappings = list };
        PlayerPrefs.SetString(PREFS_KEY, JsonUtility.ToJson(wrapper));
        PlayerPrefs.Save();
    }

    [Serializable] private class AnchorMapping { public string mapPosId; public ulong arucoID; }
    [Serializable] private class AnchorMappingWrapper { public List<AnchorMapping> mappings; }

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

    private void OnSpacePermissionGranted(string permission)
    {
        permissionGranted = true;
        QueryExistingAnchors();
    }

    private void OnPermissionDenied(string permission) { permissionGranted = false; }

    void Update()
    {
        if (markerFeature == null || markerFeature.MarkerDetectors.Count == 0) return;

        markerFeature.UpdateMarkerDetectors();

        foreach (var detector in markerFeature.MarkerDetectors)
        {
            if (detector.Settings.MarkerType != MarkerType.Aruco) continue;

            foreach (var data in detector.Data)
            {
                if (data.MarkerPose == null || !data.MarkerNumber.HasValue) continue;

                ulong id = data.MarkerNumber.Value;
                Pose pose = data.MarkerPose.Value;
                if (pose.position.sqrMagnitude < 0.0001f) continue;

                var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == id);
                if (mapping == null || mapping.prefab == null) continue;

                lastSeenArucoID = id;
                lastDetectedMarkerPoses[id] = pose;

                if (createdAnchorsByArucoID.TryGetValue(id, out ARAnchor existing) && existing != null)
                {
                    UpdateInstanceTransform(existing.gameObject, mapping, pose);
                    continue;
                }

                if (HasAnchorForArucoID(id)) continue;

                CreateAndPublishAnchorFromMarker(id, mapping, pose);
            }
        }

        UpdateStoredAnchorTransforms();
        EnforceSingleActivePrefab();
        UpdateAlignmentInfoText();

        if (enableControllerAdjustment && !IsAnyActiveObjectGrabbed())
            HandleControllerOffsetAdjustment();
    }

    private bool IsAnyActiveObjectGrabbed()
    {
        if (lastSeenArucoID == 0) return false;

        if (createdAnchorsByArucoID.TryGetValue(lastSeenArucoID, out ARAnchor created) && created != null)
        {
            var grab = created.GetComponent<XRGrabInteractable>();
            return grab != null && grab.isSelected;
        }

        foreach (var anchor in storedAnchors)
        {
            if (anchor != null && anchor.transform.childCount > 0)
            {
                var child = anchor.transform.GetChild(0).gameObject;
                var grab = child.GetComponent<XRGrabInteractable>();
                if (grab != null && grab.isSelected) return true;
            }
        }
        return false;
    }

    private void HandleControllerOffsetAdjustment()
    {
        if (lastSeenArucoID == 0) return;

        var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == lastSeenArucoID);
        if (mapping == null) return;

        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightHandDevices);
        InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, leftHandDevices);

        float dt = Time.deltaTime;
        float speed = offsetAdjustSpeed * dt;
        bool changed = false;

        if (rightHandDevices.Count > 0)
        {
            var dev = rightHandDevices[0];
            if (dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis))
            {
                if (Mathf.Abs(axis.x) > inputDeadzone) { mapping.offsetX += axis.x * speed; changed = true; }
                if (Mathf.Abs(axis.y) > inputDeadzone) { mapping.offsetZ += axis.y * speed; changed = true; }
            }
        }

        if (leftHandDevices.Count > 0)
        {
            var dev = leftHandDevices[0];
            if (dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis))
            {
                if (Mathf.Abs(axis.y) > inputDeadzone) { mapping.offsetY += axis.y * speed; changed = true; }
            }
        }

        if (changed)
            ForceUpdateActivePrefabTransform(mapping);
    }

    private void ForceUpdateActivePrefabTransform(ArucoPrefabMapping mapping)
    {
        if (!lastDetectedMarkerPoses.TryGetValue(lastSeenArucoID, out Pose markerPose)) return;

        if (createdAnchorsByArucoID.TryGetValue(lastSeenArucoID, out ARAnchor created) && created != null && created.gameObject != null)
        {
            UpdateInstanceTransform(created.gameObject, mapping, markerPose);
            return;
        }

        if (activeSubsystem == null) return;

        foreach (ARAnchor anchor in storedAnchors)
        {
            if (anchor == null || anchor.gameObject == null) continue;
            string mapId = activeSubsystem.GetAnchorMapPositionId(anchor);
            if (string.IsNullOrEmpty(mapId)) continue;
            if (!anchorMapPosIdToArucoID.TryGetValue(mapId, out ulong mapped) || mapped != lastSeenArucoID) continue;

            if (anchor.transform.childCount > 0)
            {
                GameObject child = anchor.transform.GetChild(0).gameObject;
                UpdateInstanceTransformRelativeToAnchor(child, mapping, anchor);
            }
            break;
        }
    }

    private bool HasAnchorForArucoID(ulong arucoID)
    {
        if (createdAnchorsByArucoID.TryGetValue(arucoID, out ARAnchor ca) && ca != null && ca.gameObject != null)
            return true;

        if (activeSubsystem == null) return false;

        foreach (ARAnchor sa in storedAnchors.ToList())
        {
            if (sa == null) continue;
            string mapId = activeSubsystem.GetAnchorMapPositionId(sa);
            if (!string.IsNullOrEmpty(mapId) && anchorMapPosIdToArucoID.TryGetValue(mapId, out ulong mapped) && mapped == arucoID)
                return true;
        }
        return false;
    }

    private void EnforceSingleActivePrefab()
    {
        if (lastSeenArucoID == 0) return;

        foreach (var kvp in createdAnchorsByArucoID)
        {
            if (kvp.Value != null && kvp.Value.gameObject != null)
                kvp.Value.gameObject.SetActive(kvp.Key == lastSeenArucoID);
        }

        if (activeSubsystem != null)
        {
            foreach (ARAnchor anchor in storedAnchors.ToList())
            {
                if (anchor == null || anchor.gameObject == null) continue;
                string mapPosId = activeSubsystem.GetAnchorMapPositionId(anchor);
                bool shouldShow = false;
                if (!string.IsNullOrEmpty(mapPosId) && anchorMapPosIdToArucoID.TryGetValue(mapPosId, out ulong mappedAruco))
                    shouldShow = (mappedAruco == lastSeenArucoID);
                anchor.gameObject.SetActive(shouldShow);
            }
        }
    }

    private void CreateAndPublishAnchorFromMarker(ulong arucoID, ArucoPrefabMapping mapping, Pose markerRelativePose)
    {
        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
            ? xrOrigin.CameraFloorOffsetObject.transform : null;

        Vector3 worldPos = originT != null ? originT.TransformPoint(markerRelativePose.position) : markerRelativePose.position;
        Quaternion worldRot = originT != null ? originT.rotation * markerRelativePose.rotation : markerRelativePose.rotation;

        GameObject instance = Instantiate(mapping.prefab, worldPos, worldRot);
        instance.SetActive(true);

        SetupGrabInteraction(instance);

        ARAnchor arAnchor = instance.AddComponent<ARAnchor>();
        var rend = instance.GetComponent<MeshRenderer>();
        if (rend != null) rend.material.color = Color.grey;

        createdAnchorsByArucoID[arucoID] = arAnchor;
        localAnchors.Add(arAnchor);

        UpdateInstanceTransform(instance, mapping, markerRelativePose);
        PublishSingleAnchor(arAnchor);
    }

    private void UpdateInstanceTransform(GameObject instance, ArucoPrefabMapping mapping, Pose markerRelativePose)
    {
        if (instance == null) return;

        var grab = instance.GetComponent<XRGrabInteractable>();
        if (grab != null && grab.isSelected) return;

        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
            ? xrOrigin.CameraFloorOffsetObject.transform : null;

        Vector3 worldPos = originT != null ? originT.TransformPoint(markerRelativePose.position) : markerRelativePose.position;
        Quaternion worldRot = originT != null ? originT.rotation * markerRelativePose.rotation : markerRelativePose.rotation;

        Vector3 localOffset = new Vector3(mapping.offsetX, mapping.offsetY, mapping.offsetZ);
        Vector3 finalPos = worldPos + (worldRot * localOffset);
        Quaternion finalRot = worldRot * Quaternion.Euler(mapping.rotationOffset);

        instance.transform.SetPositionAndRotation(finalPos, finalRot);
        instance.transform.localScale = Vector3.one * arucoPhysicalLengthMeters * mapping.scaleMultiplier;
    }

    private void PublishSingleAnchor(ARAnchor anchor)
    {
        if (!permissionGranted || storageFeature == null || anchor?.trackingState != TrackingState.Tracking) return;
        storageFeature.PublishSpatialAnchorsToStorage(new List<ARAnchor> { anchor }, 0);
    }

    private void OnQueryComplete(List<string> anchorMapPositionIds)
    {
        Debug.Log($"[Persistence] OnQueryComplete received {anchorMapPositionIds.Count} anchor IDs from storage.");

        List<string> tracked = new List<string>();

        foreach (ARAnchor stored in storedAnchors.ToList())
        {
            string id = activeSubsystem?.GetAnchorMapPositionId(stored);
            if (!string.IsNullOrEmpty(id))
            {
                tracked.Add(id);
                if (!anchorMapPositionIds.Contains(id))
                {
                    Debug.Log($"[Persistence] Removing expired anchor: {id}");
                    Destroy(stored.gameObject);
                    storedAnchors.Remove(stored);
                }
            }
        }

        var newAnchors = anchorMapPositionIds.Except(tracked).ToList();
        if (newAnchors.Count > 0)
        {
            Debug.Log($"[Persistence] Creating {newAnchors.Count} anchors from storage...");
            storageFeature.CreateSpatialAnchorsFromStorage(newAnchors);
        }
    }

    private void OnAnchorsChanged(ARAnchorsChangedEventArgs args)
    {
        foreach (ARAnchor anchor in args.added)
        {
            if (activeSubsystem != null && activeSubsystem.IsStoredAnchor(anchor))
            {
                storedAnchors.Add(anchor);

                string mapPosId = activeSubsystem.GetAnchorMapPositionId(anchor);
                if (!string.IsNullOrEmpty(mapPosId) && anchorMapPosIdToArucoID.TryGetValue(mapPosId, out ulong savedArucoID))
                {
                    var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == savedArucoID);
                    if (mapping != null && mapping.prefab != null)
                    {
                        GameObject instance = Instantiate(mapping.prefab, anchor.transform.position, anchor.transform.rotation);
                        SetupGrabInteraction(instance);
                        instance.transform.SetParent(anchor.transform);
                        UpdateInstanceTransformRelativeToAnchor(instance, mapping, anchor);
                        Debug.Log($"[Persistence] ✅ Restored prefab for ArUco {savedArucoID}");
                        anchor.gameObject.SetActive(false);
                    }
                }
            }
        }

        foreach (ARAnchor anchor in args.updated)
        {
            if (activeSubsystem != null && activeSubsystem.IsStoredAnchor(anchor) && localAnchors.Contains(anchor))
            {
                string mapPosId = activeSubsystem.GetAnchorMapPositionId(anchor);
                if (!string.IsNullOrEmpty(mapPosId))
                {
                    ulong arucoID = createdAnchorsByArucoID.FirstOrDefault(kvp => kvp.Value == anchor).Key;
                    if (arucoID != 0)
                    {
                        anchorMapPosIdToArucoID[mapPosId] = arucoID;
                        SaveAnchorMappings();
                        Debug.Log($"[Persistence] Saved mapping for new anchor (ArUco {arucoID})");
                    }
                }

                var rend = anchor.GetComponent<MeshRenderer>();
                if (rend != null) rend.material.color = Color.white;

                storedAnchors.Add(anchor);
                localAnchors.Remove(anchor);
            }
        }

        foreach (ARAnchor anchor in args.removed)
            storedAnchors.Remove(anchor);
    }

    private void UpdateInstanceTransformRelativeToAnchor(GameObject instance, ArucoPrefabMapping mapping, ARAnchor anchor)
    {
        if (instance == null || anchor == null) return;

        var grab = instance.GetComponent<XRGrabInteractable>();
        if (grab != null && grab.isSelected) return;

        Vector3 localOffset = new Vector3(mapping.offsetX, mapping.offsetY, mapping.offsetZ);
        Vector3 finalPos = anchor.transform.position + (anchor.transform.rotation * localOffset);
        Quaternion finalRot = anchor.transform.rotation * Quaternion.Euler(mapping.rotationOffset);

        instance.transform.SetPositionAndRotation(finalPos, finalRot);
        instance.transform.localScale = Vector3.one * arucoPhysicalLengthMeters * mapping.scaleMultiplier;
    }

    private void UpdateStoredAnchorTransforms()
    {
        if (activeSubsystem == null) return;
        foreach (var anchor in storedAnchors)
            if (anchor != null)
            {
                var p = activeSubsystem.GetAnchorPose(anchor);
                anchor.transform.SetPositionAndRotation(p.position, p.rotation);
            }
    }

    public void QueryExistingAnchors()
    {
        if (storageFeature != null && xrOrigin != null)
            storageFeature.QueryStoredSpatialAnchors(xrOrigin.transform.position, 20f);
    }

    public void DestroyAll()
    {
        foreach (var a in localAnchors) if (a) Destroy(a.gameObject);
        foreach (var a in storedAnchors) if (a) Destroy(a.gameObject);
        localAnchors.Clear();
        storedAnchors.Clear();
        createdAnchorsByArucoID.Clear();
        lastDetectedMarkerPoses.Clear();
        lastSeenArucoID = 0;

        if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);

        if (markerFeature != null) markerFeature.DestroyAllMarkerDetectors();
        hasInitializedDetector = false;
    }

    private void OnDestroy()
    {
        if (storageFeature != null) storageFeature.OnQueryComplete -= OnQueryComplete;
        DestroyAll();
    }

    // ==================== Grab Setup ====================

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

        // NEW: preserve current pose/offset relative to controller on grab
        grab.useDynamicAttach = true;
        grab.matchAttachPosition = true;
        grab.matchAttachRotation = true;
        grab.snapToColliderVolume = false; // don't re-center on the collider either



        grab.selectExited.AddListener(OnGrabReleased);
    }

    private void OnGrabReleased(SelectExitEventArgs args)
    {
        if (lastSeenArucoID == 0) return;

        var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == lastSeenArucoID);
        if (mapping == null) return;

        if (!lastDetectedMarkerPoses.TryGetValue(lastSeenArucoID, out Pose markerRelPose)) return;

        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
            ? xrOrigin.CameraFloorOffsetObject.transform : null;

        Vector3 markerWorldPos = originT != null ? originT.TransformPoint(markerRelPose.position) : markerRelPose.position;
        Quaternion markerWorldRot = originT != null ? originT.rotation * markerRelPose.rotation : markerRelPose.rotation;

        var releasedObject = args.interactableObject?.transform;
        if (releasedObject == null) return;

        // Position
        Vector3 newLocalOffset = Quaternion.Inverse(markerWorldRot) * (releasedObject.position - markerWorldPos);
        mapping.offsetX = newLocalOffset.x;
        mapping.offsetY = newLocalOffset.y;
        mapping.offsetZ = newLocalOffset.z;

        // Rotation
        Quaternion newLocalRot = Quaternion.Inverse(markerWorldRot) * releasedObject.rotation;
        mapping.rotationOffset = newLocalRot.eulerAngles;

        UpdateAlignmentInfoText();
    }

    // ==================== Manual Z Rotation + Scale while grabbed ====================

    void LateUpdate()
    {
        if (lastSeenArucoID == 0) return;

        var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == lastSeenArucoID);
        if (mapping == null) return;

        GameObject grabbedObject = null;

        if (createdAnchorsByArucoID.TryGetValue(lastSeenArucoID, out ARAnchor created) && created != null)
        {
            var grab = created.GetComponent<XRGrabInteractable>();
            if (grab != null && grab.isSelected) grabbedObject = created.gameObject;
        }
        else
        {
            foreach (var anchor in storedAnchors)
            {
                if (anchor != null && anchor.transform.childCount > 0)
                {
                    var child = anchor.transform.GetChild(0).gameObject;
                    var grab = child.GetComponent<XRGrabInteractable>();
                    if (grab != null && grab.isSelected)
                    {
                        grabbedObject = child;
                        break;
                    }
                }
            }
        }

        if (grabbedObject != null)
        {
            InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightHandDevices);
            if (rightHandDevices.Count > 0)
            {
                var dev = rightHandDevices[0];
                if (dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis))
                {
                    // Horizontal = Z rotation (roll)
                    if (Mathf.Abs(axis.x) > 0.1f)
                    {
                        float zDelta = axis.x * rotationSpeed * Time.deltaTime;
                        grabbedObject.transform.Rotate(0, 0, zDelta, Space.Self);
                    }

                    // Vertical = Scale
                    if (Mathf.Abs(axis.y) > 0.1f)
                    {
                        mapping.scaleMultiplier += axis.y * scaleAdjustSpeed * Time.deltaTime;
                        mapping.scaleMultiplier = Mathf.Max(0.1f, mapping.scaleMultiplier);
                        grabbedObject.transform.localScale = Vector3.one * arucoPhysicalLengthMeters * mapping.scaleMultiplier;
                    }
                }
            }
        }
    }

    private void InitializeAlignmentText()
    {
        if (!showAlignmentInfoText) return;
        if (alignmentInfoTextObj != null) return;

        alignmentInfoTextObj = new GameObject("ArUcoAlignmentInfoText");
        alignmentInfoTextObj.transform.SetParent(null);

        alignmentInfoTextMesh = alignmentInfoTextObj.AddComponent<TextMesh>();
        alignmentInfoTextMesh.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        alignmentInfoTextMesh.fontSize = textFontSize;
        alignmentInfoTextMesh.color = textColor;
        alignmentInfoTextMesh.anchor = TextAnchor.MiddleCenter;
        alignmentInfoTextMesh.alignment = TextAlignment.Center;

        alignmentInfoTextObj.SetActive(false);
    }

    private void UpdateAlignmentInfoText()
    {
        if (!showAlignmentInfoText || alignmentInfoTextMesh == null || lastSeenArucoID == 0)
        {
            if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
            return;
        }

        var mapping = arucoMappings.FirstOrDefault(m => m.arucoID == lastSeenArucoID);
        if (mapping == null || mapping.prefab == null)
        {
            if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
            return;
        }

        if (!lastDetectedMarkerPoses.TryGetValue(lastSeenArucoID, out Pose relPose))
        {
            if (alignmentInfoTextObj != null) alignmentInfoTextObj.SetActive(false);
            return;
        }

        Transform originT = (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
            ? xrOrigin.CameraFloorOffsetObject.transform : null;

        Vector3 markerWorldPos = originT != null ? originT.TransformPoint(relPose.position) : relPose.position;
        Quaternion markerWorldRot = originT != null ? originT.rotation * relPose.rotation : relPose.rotation;

        Vector3 textPos = markerWorldPos + Vector3.up * textHeightAboveMarker;
        alignmentInfoTextObj.transform.position = textPos;

        if (mainCamera != null)
        {
            Vector3 toCamera = mainCamera.transform.position - textPos;
            if (toCamera.sqrMagnitude > 0.0001f)
            {
                alignmentInfoTextObj.transform.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
            }
        }
        else
        {
            alignmentInfoTextObj.transform.rotation = markerWorldRot;
        }

        alignmentInfoTextObj.transform.localScale = Vector3.one * textWorldScale;

        alignmentInfoTextMesh.text =
            $"ArUco {lastSeenArucoID}\n" +
            $"Offset   X: {mapping.offsetX:F3}   Y: {mapping.offsetY:F3}   Z: {mapping.offsetZ:F3}\n" +
            $"Rotation X: {mapping.rotationOffset.x:F1}°  Y: {mapping.rotationOffset.y:F1}°  Z: {mapping.rotationOffset.z:F1}°\n" +
            $"Scale: {mapping.scaleMultiplier:F2}x";

        alignmentInfoTextObj.SetActive(true);
    }
}