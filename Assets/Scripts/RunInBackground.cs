using UnityEngine;

public class RunInBackground : MonoBehaviour {
    private void Awake() {
        Application.runInBackground = true;
        Debug.Log("[RunInBG] enabled");
    }
}
