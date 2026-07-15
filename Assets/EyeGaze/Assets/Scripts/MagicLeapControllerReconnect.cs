using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LearnXR.EyeGaze
{
    /// <summary>
    /// Forces Unity's XR Input system to re-initialize and fetch the active input device 
    /// if the Magic Leap 2 controller wakes up from standby or if the application resumes.
    /// </summary>
    public class MagicLeapControllerReconnect : MonoBehaviour
    {
        [Tooltip("Assign the XR Controller GameObjects here (e.g., RightHand Controller, Game Controller).")]
        public GameObject[] controllerObjects;

        private void OnEnable()
        {
            // Listen for device changes in the new Input System
            InputSystem.onDeviceChange += OnDeviceChange;
        }

        private void OnDisable()
        {
            InputSystem.onDeviceChange -= OnDeviceChange;
        }

        private void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (change == InputDeviceChange.Added || change == InputDeviceChange.Reconnected)
            {
                Debug.Log($"[MagicLeapControllerReconnect] Device reconnected/added: {device.name}");
                
                // Refresh controllers to force XR Interaction Toolkit to re-bind
                StartCoroutine(RefreshControllersRoutine());
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
            {
                Debug.Log("[MagicLeapControllerReconnect] Application gained focus, checking/refreshing controllers...");
                // Wait a frame to ensure all other systems have resumed before refreshing
                StartCoroutine(RefreshControllersRoutine());
            }
        }
        
        private void OnApplicationPause(bool isPaused)
        {
            if (!isPaused)
            {
                Debug.Log("[MagicLeapControllerReconnect] Application unpaused, checking/refreshing controllers...");
                StartCoroutine(RefreshControllersRoutine());
            }
        }

        /// <summary>
        /// Disables and then re-enables the controller GameObjects. 
        /// This triggers OnDisable/OnEnable on their components (like ActionBasedController),
        /// forcing them to fetch the active device and re-bind.
        /// </summary>
        private IEnumerator RefreshControllersRoutine()
        {
            // Give the OpenXR Subsystem a short moment to realize the device is back
            yield return new WaitForSeconds(0.2f);
            
            if (controllerObjects == null || controllerObjects.Length == 0)
            {
                Debug.LogWarning("[MagicLeapControllerReconnect] No controller objects assigned to refresh.");
                yield break;
            }

            foreach (var controllerObj in controllerObjects)
            {
                if (controllerObj != null && controllerObj.activeSelf)
                {
                    controllerObj.SetActive(false);
                }
            }

            // Wait another frame to ensure they are fully disabled
            yield return null;

            foreach (var controllerObj in controllerObjects)
            {
                if (controllerObj != null)
                {
                    controllerObj.SetActive(true);
                }
            }
            
            Debug.Log("[MagicLeapControllerReconnect] Controllers refreshed.");
        }
    }
}
