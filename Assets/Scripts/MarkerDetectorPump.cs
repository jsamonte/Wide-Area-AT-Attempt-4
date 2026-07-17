// MarkerDetectorPump.cs
// Ensures UpdateMarkerDetectors() is called exactly ONCE per frame,
// at end-of-frame, no matter how many scripts share the same feature.
// Self-creating singleton — no scene setup required.

using System.Collections;
using UnityEngine;
using MagicLeap.OpenXR.Features.MarkerUnderstanding;

public class MarkerDetectorPump : MonoBehaviour
{
    // ---- Singleton ----
    private static MarkerDetectorPump _instance;

    public static MarkerDetectorPump Instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject("[MarkerDetectorPump]");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<MarkerDetectorPump>();
            }
            return _instance;
        }
    }

    // ---- State ----
    private MagicLeapMarkerUnderstandingFeature _feature;
    private int _lastPumpedFrame = -1;
    private bool _coroutineRunning = false;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    /// <summary>
    /// Called by any script that holds the feature reference.
    /// Safe to call multiple times — only the first non-null value is stored.
    /// </summary>
    public void Register(MagicLeapMarkerUnderstandingFeature feature)
    {
        if (_feature == null && feature != null)
        {
            _feature = feature;
            Debug.Log("[MarkerDetectorPump] Feature registered.");
        }
    }

    private void Update()
    {
        if (_feature == null) return;
        if (_feature.MarkerDetectors.Count == 0) return;
        if (_lastPumpedFrame == Time.frameCount) return;   // already scheduled this frame
        if (_coroutineRunning) return;

        _lastPumpedFrame = Time.frameCount;
        StartCoroutine(PumpEndOfFrame());
    }

    private IEnumerator PumpEndOfFrame()
    {
        _coroutineRunning = true;
        yield return new WaitForEndOfFrame();
        if (_feature != null && _feature.MarkerDetectors.Count > 0)
            _feature.UpdateMarkerDetectors();
        _coroutineRunning = false;
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }
}
