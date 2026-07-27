using UnityEngine;
using TMPro;
using System.Collections;
using System.Collections.Generic;

public class SequenceManager : MonoBehaviour
{
    [Header("Script References")]
    public RandomSpawner spawner;
    public EyeAndHeadTracker tracker;
    [Tooltip("Drag the MapTracking object here. Its detector is started at trial start and stopped when all targets are destroyed, so the map only runs (and draws power/heat) during a trial.")]
    public MagicLeap.Examples.MapTracking mapTracking;
    [Tooltip("Drag the Gem prefab here to spawn during the Tutorial Phase")]
    public GameObject tutorialTargetPrefab;

    [Header("UI References")]
    public GameObject mainInstructionsObject;
    
    [Header("Sequence Buttons")]
    [Tooltip("Drag your 4 Sequence Buttons here.")]
    public GameObject[] sequenceButtons; 

    [Tooltip("Drag the Text child of each sequence button into this array.")]
    public GameObject[] sequenceButtonTexts;

    [Header("Start Button")]
    [Tooltip("Drag your dedicated Start Trial button here.")]
    public GameObject startButton;
    [Tooltip("Drag the Text child of the Start button here.")]
    public GameObject startButtonText;

    [Header("Pool 9Baseline Objects")]
    public GameObject pool1_9Baseline;
    public GameObject pool2_9Baseline;
    public GameObject pool3_9Baseline;
    public GameObject pool4_9Baseline;

    [Header("Blue Wireframe (12Baseline)")]
    [Tooltip("Drag the blue wireframe GameObject (12Baseline) here. It is enabled/disabled " +
             "each trial depending on whether that trial should show the wireframe.")]
    public GameObject blueWireframe12Baseline;

    [Header("Thermal / Frame Rate")]
    [Tooltip("Frame rate during active gameplay (tutorial + trials). Capped at 30 to reduce heat on Magic Leap.")]
    [SerializeField] private int trialFrameRate = 30;
    [Tooltip("Frame rate while a menu / wait screen is showing. Lower = less heat while idle between trials.")]
    [SerializeField] private int menuFrameRate = 30;
    [Tooltip("Minimum forced cooldown (seconds) before the Start button becomes clickable between trials, letting the compute pack shed heat. Set 0 to disable.")]
    [SerializeField] private float minCooldownBetweenTrialsSeconds = 20f;

    [Header("Heartbeat / Diagnostics")]
    [Tooltip("While a trial (or the tutorial) is running, a heartbeat line is written to the device log (logcat) at this interval. It is deliberately NOT written to the trial JSON: logcat survives a mid-trial reboot (the JSON copy would be buffered in RAM and lost on the very reboot we're trying to diagnose), and it keeps the experimental data clean. Pull it with 'adb logcat' or a bugreport and grep for [HEARTBEAT]. Set 0 to disable.")]
    [SerializeField] private float heartbeatIntervalSeconds = 30f;

    // Internal State
    private int currentSequenceIndex = -1;
    private int currentTrialIndex = 0;
    private bool sequenceComplete = false;
    private bool isTutorialPhase = false;
    private Coroutine _cooldownRoutine;
    private System.Threading.CancellationTokenSource _heartbeatCts;

    // Hardcoded Sequences based on your prompt
    // True = Wireframe ON. False = Wireframe OFF.
    // Index 0,1 are Dusk. Index 2,3 are Night.
    private int[,] sequencePools = new int[4, 4] {
        { 1, 2, 3, 4 }, // Sequence 1
        { 2, 1, 3, 4 }, // Sequence 2
        { 1, 2, 4, 3 }, // Sequence 3
        { 2, 1, 4, 3 }  // Sequence 4
    };

    private bool[,] sequenceWireframes = new bool[4, 4] {
        { false, true, false, true }, // Sequence 1
        { true, false, false, true }, // Sequence 2
        { false, true, true, false }, // Sequence 3
        { true, false, true, false }  // Sequence 4
    };

    private void Start()
    {
        if (spawner != null) spawner.spawnOnAwake = false;
        if (tracker != null) tracker.recordOnAwake = false;
        if (tracker != null) tracker.OnAllTargetsDestroyed.AddListener(OnTrialFinished);

        // Fallback so the map still works if the Inspector reference wasn't wired.
        if (mapTracking == null) mapTracking = FindObjectOfType<MagicLeap.Examples.MapTracking>();

        // Set up the unified button clicks in code and initialize their names
        for (int i = 0; i < sequenceButtons.Length; i++)
        {
            if (sequenceButtons[i] != null)
            {
                int index = i; // local copy for closure
                
                // Name the buttons "Sequence 1", "Sequence 2", etc.
                SetButtonText(index, $"Sequence {index + 1}");

                var btn = sequenceButtons[i].GetComponent<UnityEngine.UI.Button>();
                if (btn == null) btn = sequenceButtons[i].GetComponentInParent<UnityEngine.UI.Button>(true);

                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(() => SelectSequence(index));
                }
                else
                {
                    Debug.LogWarning($"SequenceManager: Could not find Button component for Sequence Button {i}.");
                }
            }
        }

        // Set up Start button
        if (startButton != null)
        {
            var sBtn = startButton.GetComponent<UnityEngine.UI.Button>();
            if (sBtn == null) sBtn = startButton.GetComponentInParent<UnityEngine.UI.Button>(true);
            if (sBtn != null)
            {
                sBtn.onClick.RemoveAllListeners();
                sBtn.onClick.AddListener(() => OnStartButtonClicked());
            }
            startButton.SetActive(false); // Hide until needed
        }

        SetFrameRate(menuFrameRate); // idle menu: run cool
        ShowMenu("Please scan all ArUco Markers and Select a Sequence to start.");
    }

    /// <summary>Central place to change the render cap. Hard-capped at 30 fps to keep
    /// heat down on Magic Leap regardless of any higher value serialized in the Inspector.</summary>
    private const int MaxFrameRate = 30;
    private void SetFrameRate(int fps)
    {
        Application.targetFrameRate = Mathf.Min(fps, MaxFrameRate);
    }

    private void OnUnifiedButtonClicked(int buttonIndex)
    {
        if (currentSequenceIndex == -1)
        {
            // 1. FIRST CLICK: We are picking a sequence
            SelectSequence(buttonIndex);
        }
        else
        {
            // 2. SUBSEQUENT CLICKS: Only Button 0 is visible and it acts as the Start button
            if (buttonIndex == 0)
            {
                OnStartButtonClicked();
            }
        }
    }

    private void SelectSequence(int index)
    {
        if (currentSequenceIndex != -1) return; // Prevent double-fire on sequence selection

        currentSequenceIndex = index;
        currentTrialIndex = 0;
        sequenceComplete = false;

        // Hide ALL sequence buttons
        for (int i = 0; i < sequenceButtons.Length; i++)
        {
            if (sequenceButtons[i] != null)
            {
                var btn = sequenceButtons[i].GetComponent<UnityEngine.UI.Button>();
                if (btn == null) btn = sequenceButtons[i].GetComponentInParent<UnityEngine.UI.Button>(true);
                
                if (btn != null) btn.gameObject.SetActive(false);
                else sequenceButtons[i].SetActive(false); // Fallback
            }
        }
        
        // Ensure start button is hidden
        if (startButton != null) startButton.SetActive(false);

        // Start Tutorial Phase instead of going straight to the first trial
        StartTutorialPhase();
    }

    private void StartTutorialPhase()
    {
        isTutorialPhase = true;
        gameObject.SetActive(false); // Hide HUD during tutorial
        SetFrameRate(trialFrameRate); // active gameplay

        // Space pins have served their purpose (building is locked) — tear down the
        // space-pin detector now so it isn't running during the tutorial or trials.
        // Also defensively stop map tracking: the tutorial never uses it, so if a
        // prior trial's cleanup somehow failed to stop it, this guarantees neither
        // CV detector is running during the tutorial.
        if (ArucoMarkerManager.Instance != null)
            ArucoMarkerManager.Instance.DestroyMarkerTrackers();
        if (mapTracking != null)
            mapTracking.StopTracking();

        if (tutorialTargetPrefab != null)
        {
            Transform userTransform = Camera.main != null ? Camera.main.transform : transform;
            Vector3 userPos = userTransform.position;

            // Spawn 4 targets in a cross pattern around the user at 1.5m
            Vector3[] offsets = new Vector3[]
            {
                new Vector3(0, 0, 1.5f),   // Front
                new Vector3(0, 0, -1.5f),  // Back
                new Vector3(1.5f, 0, 0),   // Right
                new Vector3(-1.5f, 0, 0)   // Left
            };

            foreach (var offset in offsets)
            {
                Vector3 spawnPos = userPos + (userTransform.rotation * offset);
                spawnPos.y = userPos.y; // Keep at eye level

                GameObject t = Instantiate(tutorialTargetPrefab, spawnPos, Quaternion.identity);
                t.transform.LookAt(userPos);
                t.transform.Rotate(90f, 0f, 0f, Space.Self); 
                
                t.tag = "DwellDestroyTarget";

                // Label it for the gaze logger so it reports a clean prefab name, not "(Clone)".
                var loggable = t.GetComponent<GazeLoggableObject>();
                if (loggable == null) loggable = t.AddComponent<GazeLoggableObject>();
                loggable.category = "Target";
                if (string.IsNullOrEmpty(loggable.displayName)) loggable.displayName = tutorialTargetPrefab.name;


                // Force into the layer the tracker uses (grabbed from spawner if possible)
                if (spawner != null)
                {
                    int layerId = LayerMask.NameToLayer(spawner.targetLayer);
                    if (layerId > -1) t.layer = layerId;
                }
            }
        }
        else
        {
            Debug.LogError("SequenceManager: Cannot start tutorial phase because tutorialTargetPrefab is missing!");
            isTutorialPhase = false;
            UpdateMenuForNextTrial();
            return;
        }

        if (tracker != null)
        {
            tracker.RefreshTargetList();
            tracker.StartNewTrialRecording("Tutorial");
        }

        StartHeartbeat("Tutorial");
    }

    private void UpdateMenuForNextTrial()
    {
        gameObject.SetActive(true); // Show HUD
        SetFrameRate(menuFrameRate); // idle menu: run cool and let the device cool down

        if (currentTrialIndex >= 4)
        {
            sequenceComplete = true;
            ShowMenu($"Sequence {currentSequenceIndex + 1} Complete!\nPlease close the application or restart.");
            if (startButton != null) startButton.SetActive(false);
            if (tracker != null) tracker.PauseRecording();
            return;
        }

        string timeOfDay = (currentTrialIndex < 2) ? "Dusk" : "Night";
        int poolNum = sequencePools[currentSequenceIndex, currentTrialIndex];

        if (currentTrialIndex == 0)
        {
            ShowMenu($"Sequence {currentSequenceIndex + 1} Selected.\n\nPlease wait until the researcher approves Trial 1 ({timeOfDay} - Pool {poolNum}).");
        }
        else
        {
            ShowMenu($"Trial {currentTrialIndex} Complete!\n\nPlease wait until the researcher approves the next trial:\nTrial {currentTrialIndex + 1} ({timeOfDay} - Pool {poolNum}).");
        }

        if (startButton != null)
        {
            startButton.SetActive(true);
            SetButtonTextSingle(startButtonText, startButton, $"Start Trial {currentTrialIndex + 1}");

            // Enforce a minimum cooldown so the compute pack can shed heat before the
            // next trial. The button stays greyed out with a countdown, then enables.
            if (_cooldownRoutine != null) StopCoroutine(_cooldownRoutine);
            if (minCooldownBetweenTrialsSeconds > 0f)
                _cooldownRoutine = StartCoroutine(CooldownThenEnableStart(currentTrialIndex + 1));
            else
                SetButtonInteractable(startButton, true);
        }
    }

    /// <summary>
    /// Keep the Start button disabled for <see cref="minCooldownBetweenTrialsSeconds"/>,
    /// showing a countdown, then re-enable it. Gives the device guaranteed cool-down time
    /// between trials on top of the researcher-paced approval.
    /// </summary>
    private IEnumerator CooldownThenEnableStart(int trialNumberForLabel)
    {
        SetButtonInteractable(startButton, false);
        float remaining = minCooldownBetweenTrialsSeconds;
        while (remaining > 0f)
        {
            SetButtonTextSingle(startButtonText, startButton, $"Cooldown… {Mathf.CeilToInt(remaining)}s");
            yield return new WaitForSeconds(1f);
            remaining -= 1f;
        }
        SetButtonTextSingle(startButtonText, startButton, $"Start Trial {trialNumberForLabel}");
        SetButtonInteractable(startButton, true);
        _cooldownRoutine = null;
    }

    private void OnStartButtonClicked()
    {
        if (sequenceComplete || currentSequenceIndex == -1) return;
        if (!gameObject.activeSelf) return; // Anti-double-fire guard!

        // If the cooldown is still counting down the button is non-interactive, so
        // clicks can't reach here. Stop any stray cooldown coroutine just in case.
        if (_cooldownRoutine != null) { StopCoroutine(_cooldownRoutine); _cooldownRoutine = null; }

        gameObject.SetActive(false); // Hide HUD
        SetFrameRate(trialFrameRate); // active gameplay

        // 1. Force detector state to a known-good baseline BEFORE anything else runs,
        // so that if spawner/wireframe logic below throws partway through, the CV
        // pipeline is never left with two detectors running at once (the biggest
        // CV/heat win, and a likely contributor to CVIP watchdog reboots under
        // sustained load). Stop map tracking first, then destroy the space-pin
        // detector, then start map tracking -- all three calls are idempotent, so
        // this is safe no matter what state we entered this method in.
        if (mapTracking != null) mapTracking.StopTracking();
        if (ArucoMarkerManager.Instance != null) ArucoMarkerManager.Instance.DestroyMarkerTrackers();
        if (mapTracking != null) mapTracking.StartTracking();

        int poolNum = sequencePools[currentSequenceIndex, currentTrialIndex];
        bool useWireframe = sequenceWireframes[currentSequenceIndex, currentTrialIndex];
        string timeOfDay = (currentTrialIndex < 2) ? "Dusk" : "Night";

        // 2. Clear old objects and spawn new ones
        if (spawner != null)
        {
            spawner.DestroyAllSpawnedObjects();
            spawner.selectedPool = (RandomSpawner.PoolSelection)(poolNum - 1);
            spawner.SpawnObjects();
        }

        // 3. Set Wireframes
        SetWireframeActive(pool1_9Baseline, false);
        SetWireframeActive(pool2_9Baseline, false);
        SetWireframeActive(pool3_9Baseline, false);
        SetWireframeActive(pool4_9Baseline, false);

        if (poolNum == 1) SetWireframeActive(pool1_9Baseline, useWireframe);
        if (poolNum == 2) SetWireframeActive(pool2_9Baseline, useWireframe);
        if (poolNum == 3) SetWireframeActive(pool3_9Baseline, useWireframe);
        if (poolNum == 4) SetWireframeActive(pool4_9Baseline, useWireframe);

        // Blue wireframe (12Baseline): enable/disable the whole GameObject to match
        // whether this trial should show the wireframe.
        if (blueWireframe12Baseline != null) blueWireframe12Baseline.SetActive(useWireframe);

        // 4. Start JSON Tracker and log marker
        if (tracker != null)
        {
            tracker.RefreshTargetList();
            tracker.StartNewTrialRecording($"Trial_{currentTrialIndex + 1}");
            string wireframeText = useWireframe ? "Wireframe" : "Zero Wireframe";
            tracker.LogMarker($"Trial {currentTrialIndex + 1} ({timeOfDay}): Pool {poolNum} + {wireframeText}");
        }

        StartHeartbeat($"Trial_{currentTrialIndex + 1}");
    }

    private void OnTrialFinished()
    {
        StopHeartbeat();

        if (isTutorialPhase)
        {
            isTutorialPhase = false;
            UpdateMenuForNextTrial(); // Show the menu for Trial 1
            return;
        }

        if (tracker != null)
        {
            tracker.LogMarker($"Trial {currentTrialIndex + 1} Ended.");
            tracker.PauseRecording();
        }

        // All targets destroyed -> stop the map detector to drop camera/CV load and heat.
        if (mapTracking != null) mapTracking.StopTracking();

        if (spawner != null) spawner.DestroyAllSpawnedObjects();

        currentTrialIndex++;
        UpdateMenuForNextTrial();
    }

    /// <summary>
    /// Starts (or restarts) a periodic heartbeat while a trial/tutorial is active.
    /// Each tick is written to the device log (logcat) via Debug.Log -- NOT to the
    /// trial JSON. logcat is streamed to the OS in real time, so the last heartbeat
    /// before a reboot/hang survives (an adb bugreport or 'adb logcat' shows it with a
    /// wall-clock timestamp), whereas the JSON summary is only flushed at trial end and
    /// would be lost on a mid-trial crash. Keeping it out of the JSON also stops it
    /// polluting the experimental destructionEvents data.
    ///
    /// Runs on a background Task, NOT a coroutine: this component lives on the HUD
    /// Canvas, which StartTutorialPhase()/OnStartButtonClicked() deactivate (via
    /// gameObject.SetActive(false)) for the entire trial -- and a coroutine cannot run
    /// on an inactive GameObject. A Task is independent of GameObject active state, and
    /// Debug.Log is thread-safe (the tracker already logs from background threads).
    /// </summary>
    private void StartHeartbeat(string label)
    {
        StopHeartbeat();
        if (heartbeatIntervalSeconds <= 0f) return;

        _heartbeatCts = new System.Threading.CancellationTokenSource();
        var token = _heartbeatCts.Token;
        int intervalMs = Mathf.RoundToInt(heartbeatIntervalSeconds * 1000f);

        System.Threading.Tasks.Task.Run(async () =>
        {
            float elapsed = 0f;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await System.Threading.Tasks.Task.Delay(intervalMs, token);
                    elapsed += heartbeatIntervalSeconds;
                    // logcat only. Grep for [HEARTBEAT] in an adb logcat / bugreport to
                    // find the last one before a reboot and thus which trial was active.
                    Debug.Log($"[HEARTBEAT] {label} still running, elapsed {elapsed:F0}s");
                }
            }
            catch (System.OperationCanceledException) { /* normal stop */ }
        }, token);
    }

    private void StopHeartbeat()
    {
        if (_heartbeatCts != null)
        {
            _heartbeatCts.Cancel();
            _heartbeatCts.Dispose();
            _heartbeatCts = null;
        }
    }

    private void OnDestroy()
    {
        // Ensure the heartbeat task doesn't outlive this component (e.g. on app quit).
        StopHeartbeat();
    }

    private void SetWireframeActive(GameObject baselineObj, bool active)
    {
        if (baselineObj != null)
        {
            MeshRenderer renderer = baselineObj.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.enabled = active;
        }
    }

    private void ShowMenu(string msg)
    {
        gameObject.SetActive(true);
        if (mainInstructionsObject != null)
        {
            var tmp = mainInstructionsObject.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (tmp != null) { tmp.text = msg; return; }

            var legacy = mainInstructionsObject.GetComponentInChildren<UnityEngine.UI.Text>(true);
            if (legacy != null) { legacy.text = msg; return; }
        }
    }

    private void SetButtonText(int index, string msg)
    {
        if (index < 0 || index >= sequenceButtons.Length) return;

        GameObject textObj = null;
        
        // Use explicit reference if provided, otherwise fallback to finding it dynamically
        if (sequenceButtonTexts != null && sequenceButtonTexts.Length > index && sequenceButtonTexts[index] != null)
        {
            textObj = sequenceButtonTexts[index];
        }
        else if (sequenceButtons != null && sequenceButtons.Length > index && sequenceButtons[index] != null)
        {
            textObj = sequenceButtons[index];
        }
        
        if (textObj == null) return;

        var tmp = textObj.GetComponent<TMPro.TMP_Text>();
        if (tmp == null) tmp = textObj.GetComponentInChildren<TMPro.TMP_Text>(true);
        if (tmp != null) { tmp.text = msg; return; }

        var legacy = textObj.GetComponent<UnityEngine.UI.Text>();
        if (legacy == null) legacy = textObj.GetComponentInChildren<UnityEngine.UI.Text>(true);
        if (legacy != null) { legacy.text = msg; return; }
    }

    private void SetButtonInteractable(GameObject buttonObj, bool interactable)
    {
        if (buttonObj == null) return;
        var btn = buttonObj.GetComponent<UnityEngine.UI.Button>();
        if (btn == null) btn = buttonObj.GetComponentInParent<UnityEngine.UI.Button>(true);
        if (btn != null) btn.interactable = interactable;
    }

    private void SetButtonTextSingle(GameObject textObj, GameObject fallbackObj, string msg)
    {
        GameObject target = textObj != null ? textObj : fallbackObj;
        if (target == null) return;

        var tmp = target.GetComponent<TMPro.TMP_Text>();
        if (tmp == null) tmp = target.GetComponentInChildren<TMPro.TMP_Text>(true);
        if (tmp != null) { tmp.text = msg; return; }

        var legacy = target.GetComponent<UnityEngine.UI.Text>();
        if (legacy == null) legacy = target.GetComponentInChildren<UnityEngine.UI.Text>(true);
        if (legacy != null) { legacy.text = msg; return; }
    }
}
