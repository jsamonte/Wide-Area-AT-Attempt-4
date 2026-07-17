using UnityEngine;

public class XRSessionWatcher : MonoBehaviour {
    private void OnApplicationFocus(bool hasFocus) {
        Debug.Log($"[AppFocus] {hasFocus}");
    }

    private void OnApplicationPause(bool pauseStatus) {
        Debug.Log($"[AppPause] {pauseStatus}");
    }
}
