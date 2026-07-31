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

        // The OpenXR input layer re-registers the controller in BURSTS, not on a timer: an outdoor session
        // logged 200 "device added" events with 157 of the gaps under one second, 78 of them in the first
        // minute alone. Without this guard each event started its own RefreshControllersRoutine, so dozens
        // of them interleaved -- one disabling the controller objects while another was mid-flight. That
        // races XR Interaction Toolkit's own registration list into an ArgumentOutOfRangeException on
        // RemoveAt, after which something in XRI dereferences null EVERY FRAME for the rest of the session
        // (24,724 NullReferenceExceptions in one 26-minute run).
        //
        // One refresh at a time is all that was ever needed: the routine re-binds every controller in
        // controllerObjects, so a second concurrent pass has nothing left to do.
        private bool refreshInFlight;

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
            // Guarded here rather than at the call sites so every entry point -- device change, focus
            // regained, unpause -- is covered by the one check.
            if (refreshInFlight) yield break;
            refreshInFlight = true;

            // try/finally, not a bare reset at the end: if this object is disabled or destroyed mid-refresh
            // the coroutine is torn down early, and without the finally the flag would latch true and no
            // controller would ever re-bind again for the rest of the session.
            try
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
            finally
            {
                refreshInFlight = false;
            }
        }
    }
}
