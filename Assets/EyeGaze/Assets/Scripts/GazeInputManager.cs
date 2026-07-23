using System.Collections.Generic;
using LearnXR.Core;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.MagicLeap;
using UnityEngine.XR.OpenXR.Features.Interactions;
using InputDevice = UnityEngine.XR.InputDevice;
using Logger = LearnXR.Core.Logger;

public class GazeInputManager : Singleton<GazeInputManager>
{
    private InputDevice eyeTrackingDevice;
    private List<InputDevice> inputDeviceList = new List<InputDevice>();
    public bool EyeTrackingPermissionGranted { get; private set; }
    public Vector3 GazePosition { get; private set; }
    public Quaternion GazeRotation { get; private set; }
    
    void Start()
    {
        MagicLeap.Android.Permissions.RequestPermission(MagicLeap.Android.Permissions.EyeTracking, OnPermissionGranted, 
            OnPermissionDenied, OnPermissionDenied);
    }

    private void Update()
    {
        if (!EyeTrackingPermissionGranted) return;
       
        if (!eyeTrackingDevice.isValid)
        {
            InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.EyeTracking, inputDeviceList);
            if (inputDeviceList.Count > 0)
            {
                eyeTrackingDevice = inputDeviceList[0];
            }

            if (!eyeTrackingDevice.isValid)
            {
                // Logger.Instance.LogWarning($"Unable to get eye tracking information");
                return;
            }
        }
        
        bool hasData = eyeTrackingDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked);
        hasData &= eyeTrackingDevice.TryGetFeatureValue(EyeTrackingUsages.gazePosition, out Vector3 position);
        hasData &= eyeTrackingDevice.TryGetFeatureValue(EyeTrackingUsages.gazeRotation, out Quaternion rotation);

        if (isTracked && hasData)
        {
            GazePosition = position;
            GazeRotation = rotation;
        }
    }

    private void OnPermissionDenied(string permission)
    {
        Logger.Instance.LogError($"Eye tracking permission denied.");

        // CRIT, not just ERROR: a denied eye permission means the recorded gaze is empty while everything else
        // looks healthy, which is the exact "worthless file that passed" failure. This line (unlike the
        // LearnXR Logger call above) goes through Debug.LogError, so LogRing catches it: it drives the red CRIT
        // row on the dashboard punch list and lands in the on-disk run log for review afterward.
        Debug.LogError("[GAZE:CRIT] Eye tracking permission denied: recorded gaze data will be empty.");
    }

    private void OnPermissionGranted(string permission)
    {
        EyeTrackingPermissionGranted = true;

        // INFO through Debug.Log so the run log records the permission OUTCOME either way. A run log that shows
        // neither a grant nor a denial means the request callback never fired at all, which is its own finding.
        Debug.Log("[GAZE] Eye tracking permission granted.");
    }
}