using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.MagicLeap;
using TMPro;
using Logger = LearnXR.Core.Logger;

public class WristTracker : MonoBehaviour
{
    [SerializeField]
    private GameObject wristPrefabForKeypoint;
    
    [SerializeField]
    [Range(0, 1.0f)]
    private float wristVisibilityConfidence = 0.5f;

    [SerializeField] private bool wristNamesVisibility = true;

    [SerializeField] private bool verboseWristLog = false;
    
    private InputDevice leftHandDevice;
    private InputDevice rightHandDevice;
    
    private Dictionary<string, GameObject> wristIndicators = new();
    
    private enum HandSide
    {
        LeftHand,
        RightHand
    }
    
    void Start()
    {
        if (MLPermissions.CheckPermission(MLPermission.HandTracking).IsOk)
        {
            Logger.Instance.LogInfo($"MLPermission for hand tracking was auto granted");
            InputSubsystem.Extensions.MLHandTracking.StartTracking();
        }
    }

    private void Update()
    {
        if (!leftHandDevice.isValid || !rightHandDevice.isValid)
        {
            leftHandDevice =
                InputSubsystem.Utils.FindMagicLeapDevice(InputDeviceCharacteristics.HandTracking |
                                                         InputDeviceCharacteristics.Left);
            rightHandDevice =
                InputSubsystem.Utils.FindMagicLeapDevice(InputDeviceCharacteristics.HandTracking |
                                                         InputDeviceCharacteristics.Right);
        }
        
        if (leftHandDevice.isValid)
        {
            if (verboseWristLog)
                DisplayFeatures("LeftHand", leftHandDevice);
            BuildWristVisualizer(HandSide.LeftHand);
        }

        if (rightHandDevice.isValid)
        {
            if (verboseWristLog)
                DisplayFeatures("RightHand", rightHandDevice);
            BuildWristVisualizer(HandSide.RightHand);
        }
    }

    private void DisplayFeatures(string handDeviceName, InputDevice device)
    {
        var features = new List<InputFeatureUsage>();
        if (device.TryGetFeatureUsages(features))
        {
            foreach (var feature in features)
            {
                if (verboseWristLog)
                    Logger.Instance.LogInfo($"{handDeviceName}: {feature.name}");
            }
        }
    }

    private void BuildWristVisualizer(HandSide handSide)
    {
        InputDevice device = handSide == HandSide.LeftHand ? leftHandDevice : rightHandDevice;
        
        device.TryGetFeatureValue(InputSubsystem.Extensions.DeviceFeatureUsages.Hand.Confidence, out float confidence);
        if (confidence >= wristVisibilityConfidence)
        {
            if (device.TryGetFeatureValue(CommonUsages.handData, out Hand hand))
            {
                if (hand.TryGetRootBone(out Bone wristBone))
                {
                    wristBone.TryGetPosition(out Vector3 wristPosition);
                    wristBone.TryGetRotation(out Quaternion wristRotation);

                    string wristKey = $"{handSide}_Wrist";
                    GameObject wristVisualizer = null;
                    
                    if (!wristIndicators.ContainsKey(wristKey))
                    {
                        if (wristPrefabForKeypoint != null)
                        {
                            wristVisualizer = Instantiate(wristPrefabForKeypoint, transform, true);
                            wristIndicators.Add(wristKey, wristVisualizer);
                        }
                        else
                        {
                            if (verboseWristLog)
                                Logger.Instance.LogInfo("No wristPrefabForKeypoint assigned - wrist visualizer not created");
                            return;
                        }
                    }
                    
                    wristVisualizer = wristIndicators[wristKey];
                    var wristVisualizerInfo = wristVisualizer.GetComponentInChildren<TextMeshPro>(true);
                    if (wristVisualizerInfo != null)
                    {
                        wristVisualizerInfo.text = handSide == HandSide.LeftHand ? "Left Wrist" : "Right Wrist";
                        wristVisualizerInfo.gameObject.SetActive(wristNamesVisibility);
                    }
                    wristVisualizer.transform.localPosition = wristPosition;
                    wristVisualizer.transform.localRotation = wristRotation;
                }
            }
        }
        else
        {
            if (verboseWristLog)
                Logger.Instance.LogInfo($"Wrist visibility confidence not met for {handSide}");
        }
    }
}
