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
    
    // ==================== SEQUENCE / PART BUTTONS ====================
    //
    // A session is half a sequence. PART 1 is Trials 1 & 2 (both Dusk) and runs the tutorial first;
    // PART 2 is Trials 3 & 4 (both Night) and SKIPS the tutorial, because the headset is rebooted
    // between the two parts and the same participant has already done the tutorial in Part 1.
    //
    // The two parts are authored as two separate arrays rather than one array of eight so the
    // Inspector says which is which. Laid out in the scene as two columns: Part 1 on the LEFT,
    // Part 2 on the RIGHT, Sequence 1-4 top to bottom in each.

    // FormerlySerializedAs: these two were "sequenceButtons"/"sequenceButtonTexts" back when a session ran a
    // whole sequence. The old four buttons ARE the Part 1 column, so the attribute migrates the existing
    // scene wiring into Part 1 instead of silently emptying it and leaving a menu with no working buttons.
    [Header("Sequence Buttons — PART 1  (Trials 1 & 2 · Dusk · runs the tutorial)")]
    [Tooltip("LEFT column, top to bottom: Sequence 1, 2, 3, 4.\n\n" +
             "Picking one of these runs the 4-gem tutorial, then Trials 1 and 2 (both Dusk).")]
    [UnityEngine.Serialization.FormerlySerializedAs("sequenceButtons")]
    public GameObject[] sequenceButtonsPart1 = new GameObject[SequencesPerPart];

    [Tooltip("The Text child of each PART 1 button, same order. Optional — if left empty the button " +
             "itself is searched for a TMP_Text child.")]
    [UnityEngine.Serialization.FormerlySerializedAs("sequenceButtonTexts")]
    public GameObject[] sequenceButtonTextsPart1 = new GameObject[SequencesPerPart];

    [Header("Sequence Buttons — PART 2  (Trials 3 & 4 · Night · NO tutorial)")]
    [Tooltip("RIGHT column, top to bottom: Sequence 1, 2, 3, 4.\n\n" +
             "Part 2 is run after rebooting the headset, so picking one of these SKIPS the tutorial " +
             "and goes straight to the wait screen for Trial 3 (both trials are Night).")]
    public GameObject[] sequenceButtonsPart2 = new GameObject[SequencesPerPart];

    [Tooltip("The Text child of each PART 2 button, same order. Optional — if left empty the button " +
             "itself is searched for a TMP_Text child.")]
    public GameObject[] sequenceButtonTextsPart2 = new GameObject[SequencesPerPart];

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

    /// <summary>How many sequences there are (1-4). One button per sequence, per part.</summary>
    public const int SequencesPerPart = 4;

    /// <summary>How many trials each part runs. Part 1 = trial indices 0-1, Part 2 = trial indices 2-3.</summary>
    public const int TrialsPerPart = 2;

    // Internal State
    private int currentSequenceIndex = -1;
    private int currentPartIndex = 0;      // 0 = Part 1 (trials 0-1), 1 = Part 2 (trials 2-3)
    private int currentTrialIndex = 0;     // ABSOLUTE index 0-3 into the sequence tables, never re-based per part
    private bool sequenceComplete = false;
    private bool isTutorialPhase = false;
    private Coroutine _cooldownRoutine;
    private System.Threading.CancellationTokenSource _heartbeatCts;

    // Flattened view of the two authored button arrays, built once in Awake: index 0-3 = Part 1
    // Sequence 1-4, index 4-7 = Part 2 Sequence 1-4. Everything else in this class -- and the trial
    // server's SequenceBridge -- works in that one index space, so the two-array split stays purely an
    // authoring convenience.
    private GameObject[] _allButtons;
    private GameObject[] _allButtonTexts;

    // First (inclusive) and last (exclusive) ABSOLUTE trial index of the selected part. A part is just a
    // window over the 4x4 tables below, which is why nothing here renumbers trials: Part 2's trials stay
    // "Trial 3"/"Trial 4" in the UI, in the markers, and in the recording filenames, so its data can never
    // collide with Part 1's.
    private int PartFirstTrialIndex => currentPartIndex * TrialsPerPart;
    private int PartEndTrialIndex => PartFirstTrialIndex + TrialsPerPart;

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

    // ==================== PUBLIC READ-ONLY API ====================
    //
    // For the trial server's SequenceBridge, which used to reach in here by reflection to display the
    // pool/wireframe/time-of-day and to end a trial early. Reflection broke silently on a rename and could
    // not survive the part change at all (a bare trial index no longer tells you which part you are in),
    // so the few things it needs are exposed properly instead. All observation, except EndTrialEarly.

    /// <summary>0-3 once a sequence is picked, -1 while still on the menu.</summary>
    public int SelectedSequenceIndex => currentSequenceIndex;

    /// <summary>0 = Part 1 (Trials 1-2, Dusk), 1 = Part 2 (Trials 3-4, Night). Meaningless until picked.</summary>
    public int SelectedPartIndex => currentPartIndex;

    /// <summary>Absolute index (0-3) of the running or queued trial.</summary>
    public int CurrentTrialIndex => currentTrialIndex;

    /// <summary>1-based number of the selected part's FIRST trial: 1 for Part 1, 3 for Part 2.</summary>
    public int FirstTrialNumber => PartFirstTrialIndex + 1;

    /// <summary>1-based number of the selected part's LAST trial: 2 for Part 1, 4 for Part 2.</summary>
    public int LastTrialNumber => PartEndTrialIndex;

    /// <summary>Number of sequence/part buttons in the flattened index space: 0-3 = Part 1 Sequence 1-4,
    /// 4-7 = Part 2 Sequence 1-4.</summary>
    public int SequenceButtonCount => _allButtons != null ? _allButtons.Length : 0;

    /// <summary>The button at a flattened index (see <see cref="SequenceButtonCount"/>), or null if the
    /// index is out of range or that slot was never wired in the Inspector.</summary>
    public GameObject GetSequenceButton(int flatIndex) =>
        (_allButtons != null && flatIndex >= 0 && flatIndex < _allButtons.Length) ? _allButtons[flatIndex] : null;

    /// <summary>Pool number (1-4) for an absolute trial index, or 0 if the sequence/index is out of range.</summary>
    public int GetPoolForTrial(int sequenceIndex, int trialIndex) =>
        InTableRange(sequenceIndex, trialIndex) ? sequencePools[sequenceIndex, trialIndex] : 0;

    /// <summary>Whether the given trial shows the wireframe. False if the sequence/index is out of range.</summary>
    public bool GetWireframeForTrial(int sequenceIndex, int trialIndex) =>
        InTableRange(sequenceIndex, trialIndex) && sequenceWireframes[sequenceIndex, trialIndex];

    private static bool InTableRange(int sequenceIndex, int trialIndex) =>
        sequenceIndex >= 0 && sequenceIndex < SequencesPerPart &&
        trialIndex >= 0 && trialIndex < SequencesPerPart;

    /// <summary>"Dusk" for trials 1-2, "Night" for trials 3-4. The part boundary and the lighting boundary
    /// are the same split by design: Part 1 is the Dusk session, Part 2 the Night session.</summary>
    public static string TimeOfDayForTrial(int trialIndex) => trialIndex < TrialsPerPart ? "Dusk" : "Night";

    /// <summary>Ends the running trial through this class's own end-of-trial path (pause recording, clear
    /// gems, advance, show the menu) exactly as if the last target had been destroyed. The trial server's
    /// "End trial" command is the only caller.</summary>
    public void EndTrialEarly() => OnTrialFinished();

    private void Awake()
    {
        // Built in Awake, not Start: the trial server's SequenceBridge can bind to this component before
        // our Start runs, and it indexes buttons through GetSequenceButton.
        BuildCombinedButtonArrays();
    }

    /// <summary>Flattens the two authored per-part arrays into one 0-7 index space. Missing or short arrays
    /// simply leave null slots, which every consumer already skips.</summary>
    private void BuildCombinedButtonArrays()
    {
        _allButtons = new GameObject[SequencesPerPart * 2];
        _allButtonTexts = new GameObject[SequencesPerPart * 2];

        for (int s = 0; s < SequencesPerPart; s++)
        {
            _allButtons[s] = ElementOrNull(sequenceButtonsPart1, s);
            _allButtons[SequencesPerPart + s] = ElementOrNull(sequenceButtonsPart2, s);
            _allButtonTexts[s] = ElementOrNull(sequenceButtonTextsPart1, s);
            _allButtonTexts[SequencesPerPart + s] = ElementOrNull(sequenceButtonTextsPart2, s);
        }
    }

    private static GameObject ElementOrNull(GameObject[] array, int index) =>
        (array != null && index >= 0 && index < array.Length) ? array[index] : null;

    private void Start()
    {
        if (spawner != null) spawner.spawnOnAwake = false;
        if (tracker != null) tracker.recordOnAwake = false;
        if (tracker != null) tracker.OnAllTargetsDestroyed.AddListener(OnTrialFinished);

        // Fallback so the map still works if the Inspector reference wasn't wired.
        if (mapTracking == null) mapTracking = FindObjectOfType<MagicLeap.Examples.MapTracking>();

        // Wire every sequence/part button in code and label its face, so the Inspector never has to carry
        // an OnClick binding that could drift out of step with the array order.
        for (int i = 0; i < _allButtons.Length; i++)
        {
            if (_allButtons[i] == null) continue;

            int index = i; // local copy for closure
            int seqNumber = (i % SequencesPerPart) + 1;
            int partNumber = (i / SequencesPerPart) + 1;

            // Name the buttons "Seq 1 - Part 1", "Seq 1 - Part 2", etc.
            SetButtonText(index, $"Seq {seqNumber} - Part {partNumber}");

            var btn = _allButtons[i].GetComponent<UnityEngine.UI.Button>();
            if (btn == null) btn = _allButtons[i].GetComponentInParent<UnityEngine.UI.Button>(true);

            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => SelectSequence(index));
            }
            else
            {
                Debug.LogWarning($"SequenceManager: Could not find Button component for " +
                                 $"Sequence {seqNumber} Part {partNumber} (flat index {i}).");
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

        // Build the wireframe cache now, while we're already on the menu, so the one-off
        // hierarchy walk doesn't land in the middle of a trial transition.
        CacheBlueWireframeChildren();

        ShowMenu("Please scan all ArUco Markers, then select a Sequence and Part to start.\n" +
                 "Part 1 = Trials 1 & 2 (Dusk).   Part 2 = Trials 3 & 4 (Night).");
    }

    /// <summary>Central place to change the render cap. Hard-capped at 30 fps to keep
    /// heat down on Magic Leap regardless of any higher value serialized in the Inspector.</summary>
    private const int MaxFrameRate = 30;
    private void SetFrameRate(int fps)
    {
        Application.targetFrameRate = Mathf.Min(fps, MaxFrameRate);
    }

    /// <summary>
    /// Handles a click on any of the eight sequence/part buttons. <paramref name="flatIndex"/> is the
    /// flattened index: 0-3 = Part 1 Sequence 1-4, 4-7 = Part 2 Sequence 1-4.
    /// </summary>
    private void SelectSequence(int flatIndex)
    {
        if (currentSequenceIndex != -1) return; // Prevent double-fire on sequence selection

        currentSequenceIndex = flatIndex % SequencesPerPart;
        currentPartIndex = flatIndex / SequencesPerPart;
        currentTrialIndex = PartFirstTrialIndex; // Part 1 starts at trial 0, Part 2 at trial 2
        sequenceComplete = false;

        HideAllSequenceButtons();

        // Ensure start button is hidden
        if (startButton != null) startButton.SetActive(false);

        // The markers have been scanned and the building is locked, so both CV detectors come down HERE,
        // at selection, rather than inside the tutorial. Part 2 skips the tutorial, and leaving the
        // space-pin detector running through its wait screen and the start cooldown is exactly the
        // CV/heat load the rest of this class works to avoid.
        TearDownScanningDetectors();

        if (currentPartIndex == 0)
        {
            // Part 1: tutorial first, then Trials 1 and 2.
            StartTutorialPhase();
        }
        else
        {
            // Part 2 runs after a headset reboot, on a participant who already did the tutorial in
            // Part 1, so it goes straight to the wait screen for Trial 3. Nothing else needs priming:
            // EyeAndHeadTracker.StartNewTrialRecording opens its own writers and starts its own clock,
            // so the first numbered trial bootstraps recording on its own.
            Debug.Log($"SequenceManager: Sequence {currentSequenceIndex + 1} Part 2 selected; " +
                      "skipping the tutorial and queueing Trial 3.");
            UpdateMenuForNextTrial();
        }
    }

    private void HideAllSequenceButtons()
    {
        for (int i = 0; i < _allButtons.Length; i++)
        {
            if (_allButtons[i] == null) continue;

            var btn = _allButtons[i].GetComponent<UnityEngine.UI.Button>();
            if (btn == null) btn = _allButtons[i].GetComponentInParent<UnityEngine.UI.Button>(true);

            if (btn != null) btn.gameObject.SetActive(false);
            else _allButtons[i].SetActive(false); // Fallback
        }
    }

    /// <summary>
    /// Brings down both CV detectors. Space pins have served their purpose once a sequence is picked
    /// (the building is locked), and map tracking is stopped defensively in case a prior trial's cleanup
    /// failed — together that guarantees neither detector is running while we sit on a menu or in the
    /// tutorial. Both calls are idempotent, so this is safe to call from any state.
    /// </summary>
    private void TearDownScanningDetectors()
    {
        if (ArucoMarkerManager.Instance != null)
            ArucoMarkerManager.Instance.DestroyMarkerTrackers();
        if (mapTracking != null)
            mapTracking.StopTracking();
    }

    private void StartTutorialPhase()
    {
        isTutorialPhase = true;
        gameObject.SetActive(false); // Hide HUD during tutorial
        SetFrameRate(trialFrameRate); // active gameplay

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

        if (currentTrialIndex >= PartEndTrialIndex)
        {
            sequenceComplete = true;
            string whatNext = (currentPartIndex == 0)
                ? "Please reboot the headset before running Part 2."
                : "Please close the application.";
            ShowMenu($"Sequence {currentSequenceIndex + 1} Part {currentPartIndex + 1} Complete!\n{whatNext}");
            if (startButton != null) startButton.SetActive(false);
            if (tracker != null) tracker.PauseRecording();
            return;
        }

        string timeOfDay = TimeOfDayForTrial(currentTrialIndex);
        int poolNum = sequencePools[currentSequenceIndex, currentTrialIndex];

        if (currentTrialIndex == PartFirstTrialIndex)
        {
            ShowMenu($"Sequence {currentSequenceIndex + 1} Part {currentPartIndex + 1} Selected.\n\nPlease wait until the researcher approves Trial {currentTrialIndex + 1} ({timeOfDay} - Pool {poolNum}).");
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
        string timeOfDay = TimeOfDayForTrial(currentTrialIndex);

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

        // Blue wireframe (12Baseline): show/hide for this trial.
        SetBlueWireframeVisible(useWireframe);

        // 4. Start JSON Tracker and log marker
        if (tracker != null)
        {
            tracker.RefreshTargetList();
            tracker.StartNewTrialRecording($"Trial_{currentTrialIndex + 1}", BuildConditionTag(poolNum));
            string wireframeText = useWireframe ? "Wireframe" : "Zero Wireframe";
            tracker.LogMarker($"Trial {currentTrialIndex + 1} ({timeOfDay}): Pool {poolNum} + {wireframeText}");
        }

        StartHeartbeat($"Trial_{currentTrialIndex + 1}");
    }

    /// <summary>
    /// The condition fragment folded into this trial's output filenames: "Pool3_Seed300", or
    /// "Pool3_SeedRandom" when the spawner has fixed seeds switched off and the layout is therefore not
    /// reproducible (better than printing a seed that was never applied). Read off the spawner AFTER
    /// selectedPool has been set, so it always describes the layout that was actually spawned.
    /// </summary>
    private string BuildConditionTag(int poolNum)
    {
        if (spawner == null) return $"Pool{poolNum}";

        int? seed = spawner.ActiveSeed;
        return seed.HasValue ? $"Pool{poolNum}_Seed{seed.Value}" : $"Pool{poolNum}_SeedRandom";
    }

    private void OnTrialFinished()
    {
        StopHeartbeat();

        if (isTutorialPhase)
        {
            isTutorialPhase = false;

            // Seal the tutorial's files here rather than letting the next trial's
            // StartNewTrialRecording close them: that path would leave them named incomplete_
            // forever, even though the tutorial ran to completion.
            if (tracker != null)
            {
                tracker.LogMarker("Tutorial Ended.");
                tracker.PauseRecording();
                tracker.FinalizeTrialFiles();
            }

            UpdateMenuForNextTrial(); // Show the menu for Trial 1
            return;
        }

        if (tracker != null)
        {
            tracker.LogMarker($"Trial {currentTrialIndex + 1} Ended.");
            tracker.PauseRecording();

            // Every target was destroyed (or the researcher ended the set deliberately), so the files
            // are complete: close them and rename incomplete_ -> completed_.
            tracker.FinalizeTrialFiles();
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

    // ==================== BLUE WIREFRAME VISIBILITY ====================
    //
    // The blue wireframe subtree holds ~330 SplineExtrude components. SetActive() on the
    // root runs OnEnable/OnDisable on every one of them, and SplineExtrude.OnEnable calls
    // Rebuild() unconditionally: SplineMesh.Extrude + AutosmoothNormals + collider
    // assignment, per component, all in the same frame -- with OnDisable destroying every
    // generated mesh on the way out. That is a multi-second main-thread stall at every
    // trial transition and a plausible ANR kill on device.
    //
    // Toggling the renderers and colliders directly is visually and physically identical
    // (an inactive GameObject renders nothing and has no physics presence either) without
    // ever waking the extruders. The generated meshes are built once, at load, and kept.

    private MeshRenderer[] _blueWireframeRenderers;
    private Collider[] _blueWireframeColliders;
    private bool _blueWireframeCached;

    private void CacheBlueWireframeChildren()
    {
        if (_blueWireframeCached || blueWireframe12Baseline == null) return;

        // The root has to stay active from here on, since visibility is now driven by the
        // child components rather than by the GameObject. If it was authored inactive this
        // is the single rebuild we pay -- once, on the menu, instead of once per trial.
        if (!blueWireframe12Baseline.activeSelf)
            blueWireframe12Baseline.SetActive(true);

        // Include inactive children so the cache is complete whatever state we start in.
        _blueWireframeRenderers = blueWireframe12Baseline.GetComponentsInChildren<MeshRenderer>(true);
        _blueWireframeColliders = blueWireframe12Baseline.GetComponentsInChildren<Collider>(true);
        _blueWireframeCached = true;

        Debug.Log($"SequenceManager: cached {_blueWireframeRenderers.Length} blue wireframe renderer(s) " +
                  $"and {_blueWireframeColliders.Length} collider(s).");
    }

    private void SetBlueWireframeVisible(bool visible)
    {
        if (blueWireframe12Baseline == null) return;

        CacheBlueWireframeChildren();

        for (int i = 0; i < _blueWireframeRenderers.Length; i++)
        {
            if (_blueWireframeRenderers[i] != null)
                _blueWireframeRenderers[i].enabled = visible;
        }

        // Colliders must follow the renderers. Under the old SetActive() the whole subtree
        // left the physics scene when hidden; if they stayed on, the "no wireframe" trials
        // would still have ~2,300 invisible capsules intercepting gaze rays -- blocking gem
        // dwell with nothing on screen to explain why.
        for (int i = 0; i < _blueWireframeColliders.Length; i++)
        {
            if (_blueWireframeColliders[i] != null)
                _blueWireframeColliders[i].enabled = visible;
        }
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

    /// <summary>Sets the face text of a sequence/part button by FLATTENED index (0-3 = Part 1
    /// Sequence 1-4, 4-7 = Part 2 Sequence 1-4).</summary>
    private void SetButtonText(int index, string msg)
    {
        if (_allButtons == null || index < 0 || index >= _allButtons.Length) return;

        // Use explicit reference if provided, otherwise fallback to finding it dynamically
        GameObject textObj = _allButtonTexts[index] != null ? _allButtonTexts[index] : _allButtons[index];

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
