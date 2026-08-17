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
    
    // ==================== GROUP / PART BUTTONS ====================
    //
    // A participant is assigned to a GROUP (1-4), which fixes their pool and wireframe schedule for all
    // four trials -- see the counterbalancing tables below.
    //
    // A session is half a group. PART 1 is Trials 1 & 2 (the brighter half, labelled Dusk) and runs the
    // tutorial first; PART 2 is Trials 3 & 4 (the darker half, labelled Night) and SKIPS the tutorial,
    // because the headset is rebooted between the two parts and the same participant has already done the
    // tutorial in Part 1.
    //
    // The two parts are authored as two separate arrays rather than one array of eight so the
    // Inspector says which is which. Laid out in the scene as two columns: Part 1 on the LEFT,
    // Part 2 on the RIGHT, Group 1-4 top to bottom in each.

    // The serialized field names still say "sequence" only because renaming them would break the existing
    // scene wiring; "sequence" here means GROUP everywhere in the protocol. Do not rename these without
    // adding a FormerlySerializedAs for each -- lines further down record what happened last time button
    // bindings went stale.
    //
    // FormerlySerializedAs: these two were "sequenceButtons"/"sequenceButtonTexts" back when a session ran a
    // whole group. The old four buttons ARE the Part 1 column, so the attribute migrates the existing
    // scene wiring into Part 1 instead of silently emptying it and leaving a menu with no working buttons.
    [Header("Group Buttons — PART 1  (Trials 1 & 2 · Dusk · runs the tutorial)")]
    [Tooltip("LEFT column, top to bottom: Group 1, 2, 3, 4.\n\n" +
             "Picking one of these runs the 4-gem tutorial, then Trials 1 and 2 (the brighter half).")]
    [UnityEngine.Serialization.FormerlySerializedAs("sequenceButtons")]
    public GameObject[] sequenceButtonsPart1 = new GameObject[SequencesPerPart];

    [Tooltip("The Text child of each PART 1 button, same order. Optional — if left empty the button " +
             "itself is searched for a TMP_Text child.")]
    [UnityEngine.Serialization.FormerlySerializedAs("sequenceButtonTexts")]
    public GameObject[] sequenceButtonTextsPart1 = new GameObject[SequencesPerPart];

    [Header("Group Buttons — PART 2  (Trials 3 & 4 · Night · NO tutorial)")]
    [Tooltip("RIGHT column, top to bottom: Group 1, 2, 3, 4.\n\n" +
             "Part 2 is run after rebooting the headset, so picking one of these SKIPS the tutorial " +
             "and goes straight to the wait screen for Trial 3 (the darker half).")]
    public GameObject[] sequenceButtonsPart2 = new GameObject[SequencesPerPart];

    [Tooltip("The Text child of each PART 2 button, same order. Optional — if left empty the button " +
             "itself is searched for a TMP_Text child.")]
    public GameObject[] sequenceButtonTextsPart2 = new GameObject[SequencesPerPart];

    [Header("Start Button")]
    [Tooltip("Drag your dedicated Start Trial button here.")]
    public GameObject startButton;
    [Tooltip("Drag the Text child of the Start button here.")]
    public GameObject startButtonText;

    // ==================== SINGLE-TRIAL RECOVERY ====================
    //
    // A rescue path for re-running ONE trial after a crash, a lost map, or a battery death partway
    // through a part. It is entirely separate from the normal group/part flow: it runs no tutorial, does
    // not advance to a second trial, and ends the session as soon as its one trial finishes.
    //
    // Flow: scan markers -> Wireframe or No Wireframe -> Pool 1-4 -> one trial -> done.
    //
    // Recovery trials record under the trial name "Trial_Recovery" rather than a number, so a re-run can
    // never be mistaken for a clean trial during analysis. The operator records which trial it replaced.
    //
    // Nothing here executes unless one of the two buttons below is pressed, and both are hidden the
    // moment a normal group/part button is used.
    [Header("Single-Trial Recovery (optional)")]
    [Tooltip("Button that starts a recovery trial WITH the wireframe. Leave empty to disable recovery mode.")]
    public GameObject recoveryWireframeButton;
    [Tooltip("The Text child of the recovery Wireframe button. Optional.")]
    public GameObject recoveryWireframeButtonText;
    [Tooltip("Button that starts a recovery trial WITHOUT the wireframe. Leave empty to disable recovery mode.")]
    public GameObject recoveryNoWireframeButton;
    [Tooltip("The Text child of the recovery No Wireframe button. Optional.")]
    public GameObject recoveryNoWireframeButtonText;

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

    /// <summary>How many groups there are (1-4). One button per group, per part.</summary>
    public const int SequencesPerPart = 4;

    /// <summary>How many trials each part runs. Part 1 = trial indices 0-1, Part 2 = trial indices 2-3.</summary>
    public const int TrialsPerPart = 2;

    // Internal State
    private int currentSequenceIndex = -1;
    private int currentPartIndex = 0;      // 0 = Part 1 (trials 0-1), 1 = Part 2 (trials 2-3)
    private int currentTrialIndex = 0;     // ABSOLUTE index 0-3 into the group tables, never re-based per part
    private bool sequenceComplete = false;
    private bool isTutorialPhase = false;

    // ---- Single-trial recovery state (all false/0 during a normal session) ----
    private bool recoveryAwaitingPool = false; // the four left buttons are showing as Pool 1-4
    private bool recoveryMode = false;         // a recovery trial has been configured
    private bool recoveryTrialDone = false;    // its one trial has finished
    private bool recoveryWireframe = false;
    private int recoveryPool = 0;              // 1-4
    private Coroutine _cooldownRoutine;
    private System.Threading.CancellationTokenSource _heartbeatCts;

    // Flattened view of the two authored button arrays, built once in Awake: index 0-3 = Part 1
    // Group 1-4, index 4-7 = Part 2 Group 1-4. Everything else in this class -- and the trial
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

    // ==================== COUNTERBALANCING TABLES ====================
    //
    // Group 1-4 x trial 0-3. Participants are assigned to a group in equal numbers (6 each at n=24), and a
    // group fully determines which pool and which wireframe state every one of that participant's four
    // trials gets.
    //
    // The single property these tables exist to guarantee: EACH POOL APPEARS TWICE WITH THE WIREFRAME ON
    // AND TWICE WITH IT OFF across the four groups. The previous tables did not have this -- Pool 1 was
    // zero-wireframe in all four sequences and Pool 2 was wireframe in all four -- which meant any
    // difference in the scenery density or layout of those two pools was indistinguishable from an effect
    // of the wireframe itself. That confound is what made the pilot's headline result uninterpretable.
    //
    // Also balanced: each group sees each pool exactly once; each pool appears once in each trial position;
    // and each trial position is two wireframe / two zero-wireframe across the four groups.
    //
    // The strict ON/OFF alternation within each group is deliberate, not an oversight. Ambient light falls
    // monotonically through a session (see TimeOfDayForTrial), so alternating samples both wireframe states
    // evenly across that gradient for every participant. Blocking them (OFF OFF ON ON) would put every
    // wireframe trial in the darker half of the session. Groups 1/3 and 2/4 run opposite phases so the
    // small residual imbalance cancels across the sample.
    //
    //   Group 1:  Pool 1 Zero | Pool 2 Wire | Pool 3 Zero | Pool 4 Wire
    //   Group 2:  Pool 2 Wire | Pool 1 Zero | Pool 4 Wire | Pool 3 Zero
    //   Group 3:  Pool 4 Zero | Pool 3 Wire | Pool 2 Zero | Pool 1 Wire
    //   Group 4:  Pool 3 Wire | Pool 4 Zero | Pool 1 Wire | Pool 2 Zero
    //
    // Lighting is NOT a factor in these tables and must not be added to them: it is real daylight fading
    // outside, so it cannot be assigned, only observed. Trials 1-2 are simply the brighter half of the
    // session and 3-4 the darker half. See TimeOfDayForTrial.
    private int[,] groupPools = new int[4, 4] {
        { 1, 2, 3, 4 }, // Group 1
        { 2, 1, 4, 3 }, // Group 2
        { 4, 3, 2, 1 }, // Group 3
        { 3, 4, 1, 2 }  // Group 4
    };

    // True = Wireframe ON. False = Wireframe OFF.
    private bool[,] groupWireframes = new bool[4, 4] {
        { false, true,  false, true  }, // Group 1
        { true,  false, true,  false }, // Group 2
        { false, true,  false, true  }, // Group 3
        { true,  false, true,  false }  // Group 4
    };

    // ==================== PUBLIC READ-ONLY API ====================
    //
    // For the trial server's SequenceBridge, which used to reach in here by reflection to display the
    // pool/wireframe/time-of-day and to end a trial early. Reflection broke silently on a rename and could
    // not survive the part change at all (a bare trial index no longer tells you which part you are in),
    // so the few things it needs are exposed properly instead. All observation, except EndTrialEarly.

    /// <summary>0-3 once a group is picked, -1 while still on the menu.</summary>
    public int SelectedSequenceIndex => currentSequenceIndex;

    /// <summary>0 = Part 1 (Trials 1-2, Dusk), 1 = Part 2 (Trials 3-4, Night). Meaningless until picked.</summary>
    public int SelectedPartIndex => currentPartIndex;

    /// <summary>Absolute index (0-3) of the running or queued trial.</summary>
    public int CurrentTrialIndex => currentTrialIndex;

    /// <summary>1-based number of the selected part's FIRST trial: 1 for Part 1, 3 for Part 2.</summary>
    public int FirstTrialNumber => PartFirstTrialIndex + 1;

    /// <summary>1-based number of the selected part's LAST trial: 2 for Part 1, 4 for Part 2.</summary>
    public int LastTrialNumber => PartEndTrialIndex;

    /// <summary>Number of group/part buttons in the flattened index space: 0-3 = Part 1 Group 1-4,
    /// 4-7 = Part 2 Group 1-4.</summary>
    public int SequenceButtonCount => _allButtons != null ? _allButtons.Length : 0;

    /// <summary>The button at a flattened index (see <see cref="SequenceButtonCount"/>), or null if the
    /// index is out of range or that slot was never wired in the Inspector.</summary>
    public GameObject GetSequenceButton(int flatIndex) =>
        (_allButtons != null && flatIndex >= 0 && flatIndex < _allButtons.Length) ? _allButtons[flatIndex] : null;

    /// <summary>Pool number (1-4) for an absolute trial index, or 0 if the group/index is out of range.</summary>
    public int GetPoolForTrial(int sequenceIndex, int trialIndex) =>
        InTableRange(sequenceIndex, trialIndex) ? groupPools[sequenceIndex, trialIndex] : 0;

    /// <summary>Whether the given trial shows the wireframe. False if the group/index is out of range.</summary>
    public bool GetWireframeForTrial(int sequenceIndex, int trialIndex) =>
        InTableRange(sequenceIndex, trialIndex) && groupWireframes[sequenceIndex, trialIndex];

    private static bool InTableRange(int sequenceIndex, int trialIndex) =>
        sequenceIndex >= 0 && sequenceIndex < SequencesPerPart &&
        trialIndex >= 0 && trialIndex < SequencesPerPart;

    /// <summary>
    /// "Dusk" for trials 1-2, "Night" for trials 3-4.
    ///
    /// DESCRIPTIVE ONLY -- this is not an experimental factor and must not be treated as one. Nothing in the
    /// application controls the lighting: it is real daylight fading outside while the session runs, so it
    /// cannot be assigned to a trial, only observed. It is therefore inseparable from trial order, from
    /// fatigue, and from how long the participant took to get there (a slower participant reaches Trial 3
    /// later, and so in darker conditions). Positional is the honest encoding precisely because the real
    /// variable is positional: Part 1 is simply the brighter half of the session and Part 2 the darker half.
    ///
    /// Record actual illuminance and wall-clock time per trial if you want to say anything about light, and
    /// keep it out of the primary model -- because session length drives it, adjusting for it can introduce
    /// bias rather than remove it.
    /// </summary>
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

    /// <summary>
    /// Switches off every Inspector-authored OnClick entry on a button, so only the listener this class
    /// adds in code can run.
    ///
    /// onClick.RemoveAllListeners() clears ONLY runtime listeners; persistent (Inspector) calls survive it
    /// AND are invoked first. Every sequence button in the scene carried a stale SelectSequence(...) binding
    /// left over from an earlier layout -- "Seq 4 - Part 1" was bound to 2, "Seq 3 - Part 1" to 1, both
    /// Part 2 columns to Part 1 indices, and even Start Trial to 3. Because SelectSequence ignores a second
    /// selection once currentSequenceIndex is set, the stale call won every race and the correct one that
    /// this class wires silently no-opped: picking Sequence 4 Part 1 actually ran Sequence 3 Part 1, and
    /// only Sequence 1 looked right (its stale argument happened to be 0).
    ///
    /// Disabling them here rather than deleting them in the scene keeps the fix from being undone the next
    /// time someone re-authors these buttons in the Inspector.
    /// </summary>
    private static void DisablePersistentClicks(UnityEngine.UI.Button btn)
    {
        for (int p = btn.onClick.GetPersistentEventCount() - 1; p >= 0; p--)
        {
            if (btn.onClick.GetPersistentTarget(p) == null) continue;

            Debug.LogWarning($"SequenceManager: disabling stale Inspector OnClick " +
                             $"'{btn.onClick.GetPersistentMethodName(p)}' on '{btn.name}'. " +
                             "Click bindings are wired in code; remove it from the Inspector.");

            btn.onClick.SetPersistentListenerState(p, UnityEngine.Events.UnityEventCallState.Off);
        }
    }

    private void Start()
    {
        if (spawner != null) spawner.spawnOnAwake = false;
        if (tracker != null) tracker.recordOnAwake = false;
        if (tracker != null) tracker.OnAllTargetsDestroyed.AddListener(OnTrialFinished);

        // Fallback so the map still works if the Inspector reference wasn't wired.
        if (mapTracking == null) mapTracking = FindObjectOfType<MagicLeap.Examples.MapTracking>();

        // Wire every group/part button in code and label its face, so the Inspector never has to carry
        // an OnClick binding that could drift out of step with the array order.
        for (int i = 0; i < _allButtons.Length; i++)
        {
            if (_allButtons[i] == null) continue;

            int index = i; // local copy for closure
            int seqNumber = (i % SequencesPerPart) + 1;
            int partNumber = (i / SequencesPerPart) + 1;

            // Name the buttons "Group 1 - Part 1", "Group 1 - Part 2", etc.
            SetButtonText(index, $"Group {seqNumber} - Part {partNumber}");

            var btn = _allButtons[i].GetComponent<UnityEngine.UI.Button>();
            if (btn == null) btn = _allButtons[i].GetComponentInParent<UnityEngine.UI.Button>(true);

            if (btn != null)
            {
                DisablePersistentClicks(btn);
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => SelectSequence(index));
            }
            else
            {
                Debug.LogWarning($"SequenceManager: Could not find Button component for " +
                                 $"Group {seqNumber} Part {partNumber} (flat index {i}).");
            }
        }

        // Set up Start button
        if (startButton != null)
        {
            var sBtn = startButton.GetComponent<UnityEngine.UI.Button>();
            if (sBtn == null) sBtn = startButton.GetComponentInParent<UnityEngine.UI.Button>(true);
            if (sBtn != null)
            {
                DisablePersistentClicks(sBtn);
                sBtn.onClick.RemoveAllListeners();
                sBtn.onClick.AddListener(() => OnStartButtonClicked());
            }
            startButton.SetActive(false); // Hide until needed
        }

        WireRecoveryButtons();

        SetFrameRate(menuFrameRate); // idle menu: run cool

        // Build the wireframe cache now, while we're already on the menu, so the one-off
        // hierarchy walk doesn't land in the middle of a trial transition.
        CacheBlueWireframeChildren();

        ShowMenu("Please scan all ArUco Markers, then select a Group and Part to start.\n" +
                 "Part 1 = Trials 1 & 2 (Dusk).   Part 2 = Trials 3 & 4 (Night).");
    }

    /// <summary>Central place to change the render cap. The ceiling exists so a stray
    /// Inspector value cannot drive the device past what it can sustain thermally; it is
    /// NOT meant to override a deliberate setting. Raised from 30 to 60 so that
    /// trialFrameRate = 60 takes effect: rows are logged once per frame, so this constant
    /// determines the study's sampling rate. Menu screens still idle at menuFrameRate to
    /// shed heat between trials.</summary>
    private const int MaxFrameRate = 60;
    private void SetFrameRate(int fps)
    {
        Application.targetFrameRate = Mathf.Min(fps, MaxFrameRate);
    }

    /// <summary>
    /// Handles a click on any of the eight group/part buttons. <paramref name="flatIndex"/> is the
    /// flattened index: 0-3 = Part 1 Group 1-4, 4-7 = Part 2 Group 1-4.
    /// </summary>
    private void SelectSequence(int flatIndex)
    {
        // Recovery mode borrows these same four buttons as a Pool 1-4 picker rather than adding four more
        // GameObjects to the scene. Inert during a normal session: recoveryAwaitingPool is only ever true
        // between a recovery button press and a pool being chosen.
        if (recoveryAwaitingPool)
        {
            SelectRecoveryPool((flatIndex % SequencesPerPart) + 1);
            return;
        }

        if (currentSequenceIndex != -1) return; // Prevent double-fire on group selection

        HideRecoveryButtons(); // a normal group/part was chosen; recovery is no longer reachable

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
            Debug.Log($"SequenceManager: Group {currentSequenceIndex + 1} Part 2 selected; " +
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

    // ==================== SINGLE-TRIAL RECOVERY ====================

    /// <summary>Binds the two recovery buttons and labels their faces. A null button simply disables
    /// recovery mode, so a scene that has not authored them behaves exactly as before.</summary>
    private void WireRecoveryButtons()
    {
        BindRecoveryButton(recoveryWireframeButton, recoveryWireframeButtonText, "Recover: WIREFRAME", true);
        BindRecoveryButton(recoveryNoWireframeButton, recoveryNoWireframeButtonText, "Recover: NO Wireframe", false);
    }

    private void BindRecoveryButton(GameObject buttonObj, GameObject textObj, string label, bool useWireframe)
    {
        if (buttonObj == null) return;

        SetButtonTextSingle(textObj, buttonObj, label);

        var btn = buttonObj.GetComponent<UnityEngine.UI.Button>();
        if (btn == null) btn = buttonObj.GetComponentInParent<UnityEngine.UI.Button>(true);
        if (btn == null)
        {
            Debug.LogWarning($"SequenceManager: recovery button '{label}' has no Button component; recovery via that button is unavailable.");
            return;
        }

        DisablePersistentClicks(btn);
        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(() => SelectRecoveryWireframe(useWireframe));
    }

    private void HideRecoveryButtons()
    {
        if (recoveryWireframeButton != null) recoveryWireframeButton.SetActive(false);
        if (recoveryNoWireframeButton != null) recoveryNoWireframeButton.SetActive(false);
    }

    /// <summary>
    /// Step 1 of recovery: the wireframe state is chosen, and the four Part 1 buttons are relabelled as a
    /// Pool 1-4 picker. Refuses to start once a normal group/part is running, so a stray press mid-session
    /// cannot derail a real trial.
    /// </summary>
    private void SelectRecoveryWireframe(bool useWireframe)
    {
        if (currentSequenceIndex != -1 || recoveryMode || recoveryAwaitingPool) return;

        recoveryWireframe = useWireframe;
        recoveryAwaitingPool = true;

        HideRecoveryButtons();
        if (startButton != null) startButton.SetActive(false);

        // Markers are scanned by this point, exactly as in the normal path.
        TearDownScanningDetectors();

        // Hide the Part 2 column; relabel the Part 1 column as the pool picker.
        for (int i = 0; i < _allButtons.Length; i++)
        {
            if (_allButtons[i] == null) continue;

            var btn = _allButtons[i].GetComponent<UnityEngine.UI.Button>();
            if (btn == null) btn = _allButtons[i].GetComponentInParent<UnityEngine.UI.Button>(true);
            var go = btn != null ? btn.gameObject : _allButtons[i];

            if (i < SequencesPerPart)
            {
                go.SetActive(true);
                SetButtonText(i, $"Pool {i + 1}");
            }
            else
            {
                go.SetActive(false);
            }
        }

        ShowMenu($"RECOVERY — {(useWireframe ? "Wireframe" : "Zero Wireframe")}.\n" +
                 "Now choose the pool for the trial you are re-running.");

        Debug.Log($"SequenceManager: recovery mode armed ({(useWireframe ? "Wireframe" : "Zero Wireframe")}); awaiting pool.");
    }

    /// <summary>
    /// Step 2 of recovery: the pool is chosen and a single trial is queued. currentSequenceIndex is set to
    /// 0 purely to satisfy the "something is selected" guards elsewhere -- no group table is ever read in
    /// recovery mode, so the value is not used to look anything up.
    /// </summary>
    private void SelectRecoveryPool(int poolNumber)
    {
        if (!recoveryAwaitingPool) return;

        recoveryAwaitingPool = false;
        recoveryMode = true;
        recoveryTrialDone = false;
        recoveryPool = Mathf.Clamp(poolNumber, 1, SequencesPerPart);

        currentSequenceIndex = 0;
        currentPartIndex = 0;
        currentTrialIndex = 0;
        sequenceComplete = false;
        isTutorialPhase = false;

        HideAllSequenceButtons();

        Debug.Log($"SequenceManager: recovery trial queued — Pool {recoveryPool}, " +
                  $"{(recoveryWireframe ? "Wireframe" : "Zero Wireframe")}. No tutorial; ends after this trial.");

        UpdateMenuForNextTrial();
    }

    /// <summary>The wait screen and the completion screen for a recovery trial. Replaces the normal
    /// per-part menu entirely, because there is no part, no trial numbering and no next trial.</summary>
    private void UpdateMenuForRecoveryTrial()
    {
        gameObject.SetActive(true);
        SetFrameRate(menuFrameRate);

        string label = $"Pool {recoveryPool} + {(recoveryWireframe ? "Wireframe" : "Zero Wireframe")}";

        if (recoveryTrialDone)
        {
            sequenceComplete = true;
            ShowMenu($"Recovery trial complete ({label}).\n" +
                     "You may now remove the headset. Thank you!\n" +
                     "Please close the application.");
            if (startButton != null) startButton.SetActive(false);
            if (tracker != null) tracker.PauseRecording();
            return;
        }

        ShowMenu($"RECOVERY trial ready: {label}.\n\nPlease wait until the researcher approves the trial.");

        if (startButton != null)
        {
            startButton.SetActive(true);
            SetButtonTextSingle(startButtonText, startButton, "Start Recovery Trial");

            // No cooldown here: recovery follows a crash or a restart, so the device has already been idle.
            if (_cooldownRoutine != null) { StopCoroutine(_cooldownRoutine); _cooldownRoutine = null; }
            SetButtonInteractable(startButton, true);
        }
    }

    /// <summary>
    /// Brings down both CV detectors. Space pins have served their purpose once a group is picked
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
        if (recoveryMode) { UpdateMenuForRecoveryTrial(); return; }

        gameObject.SetActive(true); // Show HUD
        SetFrameRate(menuFrameRate); // idle menu: run cool and let the device cool down

        // The headset cue is spelled out on every between-trial screen, because a participant left to guess
        // whether to take the device off will do it at the wrong moment: mid-part removal costs the eye
        // calibration and the fit, and sitting through the reboot wearing it is uncomfortable for nothing.
        // The two branches below happen to line up exactly with the two cues -- a part ends after Trials 2
        // and 4 (headset off), and the only mid-part screens are after Trials 1 and 3 (headset stays on).
        if (currentTrialIndex >= PartEndTrialIndex)
        {
            sequenceComplete = true;
            string whatNext = (currentPartIndex == 0)
                ? "Please reboot the headset before running Part 2."
                : "Please close the application.";
            ShowMenu($"Group {currentSequenceIndex + 1} Part {currentPartIndex + 1} Complete!\n" +
                     "You may now remove the headset. Thank you!\n" +
                     $"{whatNext}");
            if (startButton != null) startButton.SetActive(false);
            if (tracker != null) tracker.PauseRecording();
            return;
        }

        string timeOfDay = TimeOfDayForTrial(currentTrialIndex);
        int poolNum = groupPools[currentSequenceIndex, currentTrialIndex];

        if (currentTrialIndex == PartFirstTrialIndex)
        {
            ShowMenu($"Group {currentSequenceIndex + 1} Part {currentPartIndex + 1} Selected.\n\nPlease wait until the researcher approves Trial {currentTrialIndex + 1} ({timeOfDay} - Pool {poolNum}).");
        }
        else
        {
            ShowMenu($"Trial {currentTrialIndex} Complete!\n\nPlease keep the headset on until instructed otherwise.\n\nPlease wait until the researcher approves the next trial:\nTrial {currentTrialIndex + 1} ({timeOfDay} - Pool {poolNum}).");
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

        // Recovery reads its pool and wireframe state from the operator's two button presses; the group
        // tables are never consulted, because a recovery run has no group and no trial position.
        int poolNum = recoveryMode ? recoveryPool : groupPools[currentSequenceIndex, currentTrialIndex];
        bool useWireframe = recoveryMode ? recoveryWireframe : groupWireframes[currentSequenceIndex, currentTrialIndex];
        string timeOfDay = TimeOfDayForTrial(currentTrialIndex);

        // 2. Clear old objects and spawn new ones
        if (spawner != null)
        {
            spawner.DestroyAllSpawnedObjects();
            spawner.selectedPool = (RandomSpawner.PoolSelection)(poolNum - 1);
            spawner.SpawnObjects();
        }

        // 3. Set Wireframes
        // The blue wireframe is the only thing the wireframe condition drives. There used to
        // be four per-pool "9Baseline" slots toggled alongside it, but no per-pool geometry
        // exists in this scene (pools are prefab lists spawned by RandomSpawner), so all four
        // had been pointed at 13Baseline itself -- which turned the solid building renderer on
        // during every wireframe trial. The slots are gone; do not reintroduce them.
        SetBlueWireframeVisible(useWireframe);

        // 4. Start JSON Tracker and log marker
        // "Trial_Recovery" rather than a number, deliberately: a re-run trial follows a crashed attempt at
        // the same pool, so the participant has partial prior exposure to that layout. Naming it distinctly
        // means it can never be silently analysed as a clean trial.
        string trialName = recoveryMode ? "Trial_Recovery" : $"Trial_{currentTrialIndex + 1}";

        if (tracker != null)
        {
            tracker.RefreshTargetList();
            tracker.StartNewTrialRecording(trialName, BuildConditionTag(poolNum));
            string wireframeText = useWireframe ? "Wireframe" : "Zero Wireframe";
            tracker.LogMarker(recoveryMode
                ? $"RECOVERY trial: Pool {poolNum} + {wireframeText}"
                : $"Trial {currentTrialIndex + 1} ({timeOfDay}): Pool {poolNum} + {wireframeText}");
        }

        StartHeartbeat(trialName);
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

        if (recoveryMode)
        {
            if (tracker != null)
            {
                tracker.LogMarker("Recovery trial ended.");
                tracker.PauseRecording();
                tracker.FinalizeTrialFiles();
            }

            if (mapTracking != null) mapTracking.StopTracking();
            if (spawner != null) spawner.DestroyAllSpawnedObjects();

            // One trial only: do not advance currentTrialIndex, do not queue anything else.
            recoveryTrialDone = true;
            UpdateMenuForNextTrial();
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

    /// <summary>Sets the face text of a group/part button by FLATTENED index (0-3 = Part 1
    /// Group 1-4, 4-7 = Part 2 Group 1-4).</summary>
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
