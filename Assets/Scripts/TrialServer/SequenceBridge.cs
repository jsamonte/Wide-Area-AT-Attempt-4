using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace TrialServer
{
    /// <summary>
    /// The one piece that touches the study. It turns the commands the <see cref="ExperimentServer"/> accepts
    /// into clicks on the study's existing SequenceManager buttons, and it tracks where the flow is so the
    /// dashboard can show it. NON-INVASIVE by design: it does not edit SequenceManager or EyeAndHeadTracker,
    /// it drives them through the same UnityEvents a physical button press would fire. That is why it can be
    /// dropped into any scene that has the study and just work.
    ///
    /// CONTROL MODEL (operator arms, participant starts). The dashboard never starts a trial. When a trial is
    /// queued (Ready) this bridge holds the study's own Start button non-interactable, so the participant
    /// cannot start early. The operator arms the trial from the dashboard (/api/trial/arm), which re-enables
    /// that button; the participant then starts the trial themselves on the device. The bridge notices the
    /// start by watching the tracker's trial clock and flips its phase to Recording.
    ///
    /// COMMAND HANDLING runs on the MAIN thread: the server enqueues inbound requests and drains them in
    /// Update, invoking OnCommand there. So everything in this class is main-thread and may freely touch Unity.
    ///
    /// THE FRAGILE POINTS are reflection, and both are guarded. (1) The pool and wireframe tables are two
    /// PRIVATE 4x4 arrays on SequenceManager, read by name for DISPLAY only. (2) Ending a trial early invokes
    /// SequenceManager's private OnTrialFinished() by name, which is the study's own end-of-trial path (pause
    /// recording, clear gems, advance, show the menu). If either name ever changes, that feature degrades with
    /// one loud warning and everything else keeps working. Public getters / a public EndTrialEarly() on
    /// SequenceManager would remove the reflection entirely if that trade is ever wanted.
    /// </summary>
    public class SequenceBridge : MonoBehaviour
    {
        const string Tag = "[BRIDGE]";

        [Tooltip("Soft trial time limit in minutes, shown on the dashboard as elapsed vs limit with an " +
                 "over-limit banner. Display only: nothing auto-ends a trial.")]
        [SerializeField] float trialLimitMinutes = 20f;

        SequenceManager _sm;
        EyeAndHeadTracker _trackerSubscribed;   // remembered so OnDestroy unsubscribes from the right one

        // Device-start detection. StartNewTrialRecording resets the trial clock, so while Armed a drop in
        // GetCurrentTrialTime (or a rise from exactly 0) means the participant pressed Start on the device.
        float _lastTrialTime;

        // The dashboard's trial clock. The tracker's own clock reads 0 while paused and counts paused time
        // back in on resume; the page instead FREEZES during a pause and excludes paused time after it.
        // Whether the trial rules should follow this clock or the tracker's is an open decision (see
        // TRIAL_SERVER.md); this is display only either way.
        float _pausedSeconds;
        float _pauseStartedAt;

        // Reflection into SequenceManager's private members, bound once. The two table fields are display
        // only; the method is the early-end path.
        FieldInfo _seqIndexField;
        FieldInfo _trialIndexField;
        FieldInfo _poolsField;
        FieldInfo _wireframesField;
        MethodInfo _onTrialFinishedMethod;
        bool _reflectionOk;
        bool _reflectionWarned;

        void Start()
        {
            // Reset the shared state in case static fields survived a domain-reload-disabled play session.
            ServerState.SelectedSequence = 0;
            ServerState.TrialNumber = 0;
            ServerState.Pool = 0;
            ServerState.Wireframe = false;
            ServerState.TimeOfDay = "";
            ServerState.Phase = TrialPhase.Menu;
            ServerState.TrialElapsed = 0f;
            ServerState.BadTrials.Clear();
            ServerState.TrialLimitSeconds = trialLimitMinutes * 60f;

            ExperimentServer.OnCommand += HandleCommand;
            TryBind();
        }

        void OnDestroy()
        {
            ExperimentServer.OnCommand -= HandleCommand;
            if (_trackerSubscribed != null)
                _trackerSubscribed.OnAllTargetsDestroyed.RemoveListener(OnTargetsCleared);
        }

        void Update()
        {
            // The study can be loaded additively after this component, so keep trying until it is found.
            if (_sm == null) { TryBind(); return; }

            EnforceArmGate();
            DetectDeviceStart();
            PublishTrialClock();
            RefreshTrialDisplay();
        }

        // ---- Binding ------------------------------------------------------------------------------------

        void TryBind()
        {
            // includeInactive: the study's SequenceManager deactivates its own GameObject during the tutorial
            // and between trials, so a plain FindObjectOfType would miss it at exactly the wrong moments.
            _sm = FindObjectOfType<SequenceManager>(true);
            if (_sm == null) return;

            Debug.Log($"{Tag} Bound to SequenceManager.");

            if (_sm.tracker != null)
            {
                _sm.tracker.OnAllTargetsDestroyed.AddListener(OnTargetsCleared);
                _trackerSubscribed = _sm.tracker;
            }
            else
            {
                Debug.LogWarning($"{Tag} SequenceManager has no tracker assigned; trial-end detection is off.");
            }

            BindReflection();
        }

        void BindReflection()
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
            var t = typeof(SequenceManager);
            _seqIndexField = t.GetField("currentSequenceIndex", F);
            _trialIndexField = t.GetField("currentTrialIndex", F);
            _poolsField = t.GetField("sequencePools", F);
            _wireframesField = t.GetField("sequenceWireframes", F);
            _onTrialFinishedMethod = t.GetMethod("OnTrialFinished", F);

            _reflectionOk = _seqIndexField != null && _trialIndexField != null &&
                            _poolsField != null && _wireframesField != null;

            if (!_reflectionOk && !_reflectionWarned)
            {
                _reflectionWarned = true;
                Debug.LogWarning($"{Tag} Could not bind SequenceManager's private fields " +
                    "(currentSequenceIndex/currentTrialIndex/sequencePools/sequenceWireframes). Pool, wireframe " +
                    "and time-of-day will not display. Core trial control (select/arm/mark) is unaffected. " +
                    "This usually means a field was renamed; add public getters to make it rock-solid.");
            }

            if (_onTrialFinishedMethod == null)
                Debug.LogWarning($"{Tag} Could not bind SequenceManager.OnTrialFinished(); the dashboard's " +
                    "End trial command will be refused. A rename did this; a public EndTrialEarly() fixes it.");
        }

        // ---- Arm gate + device-start detection ----------------------------------------------------------

        // While a trial is queued (Ready), hold the study's own Start button non-interactable so the
        // participant cannot start before the operator arms. Enforced every frame because the study's own
        // UpdateMenuForNextTrial re-enables the button whenever it shows the menu, and listener order between
        // the two scripts is not guaranteed. Arming (below) flips it back on once.
        void EnforceArmGate()
        {
            if (ServerState.Phase != TrialPhase.Ready) return;
            SetStartInteractable(false);
        }

        // The participant starts an armed trial on the device, not through us, so the phase has to be read off
        // the study rather than tracked forward. StartNewTrialRecording resets the tracker's trial clock: a
        // drop, or a rise from exactly 0 (paused between trials), means the trial just started.
        void DetectDeviceStart()
        {
            if (_sm.tracker == null) return;
            float t = _sm.tracker.GetCurrentTrialTime();

            if (ServerState.Phase == TrialPhase.Armed &&
                ((t > 0f && _lastTrialTime <= 0f) || t < _lastTrialTime - 1f))
            {
                ServerState.Phase = TrialPhase.Recording;
                _pausedSeconds = 0f;
                ServerState.Report($"Trial {ServerState.TrialNumber} started by the participant.");
            }

            _lastTrialTime = t;
        }

        // Freeze the page's trial clock across a pause. While Paused the last value simply stays; everywhere
        // else it is the tracker's clock minus the pause time already served this set.
        void PublishTrialClock()
        {
            if (ServerState.Phase == TrialPhase.Paused) return;
            float t = _sm.tracker != null ? _sm.tracker.GetCurrentTrialTime() : 0f;
            ServerState.TrialElapsed = Mathf.Max(0f, t - _pausedSeconds);
        }

        void SetStartInteractable(bool on)
        {
            if (_sm.startButton == null) return;
            var btn = _sm.startButton.GetComponent<Button>();
            if (btn == null) btn = _sm.startButton.GetComponentInParent<Button>(true);
            if (btn != null && btn.interactable != on) btn.interactable = on;
        }

        // ---- Live display -------------------------------------------------------------------------------

        // Pull the current/queued trial's pool + wireframe + time-of-day out of the study's private tables and
        // publish them to ServerState for the snapshot. Only meaningful while a numbered trial is queued or
        // running; cleared otherwise so the dashboard never shows a stale pool during the menu or tutorial.
        void RefreshTrialDisplay()
        {
            bool active = ServerState.Phase == TrialPhase.Ready || ServerState.Phase == TrialPhase.Armed ||
                          ServerState.Phase == TrialPhase.Recording || ServerState.Phase == TrialPhase.Paused;
            if (!active || !_reflectionOk)
            {
                if (!active) { ServerState.Pool = 0; ServerState.Wireframe = false; ServerState.TimeOfDay = ""; }
                return;
            }

            try
            {
                int seq = (int)_seqIndexField.GetValue(_sm);
                int trial = (int)_trialIndexField.GetValue(_sm);
                if (seq < 0 || seq > 3 || trial < 0 || trial > 3)
                {
                    ServerState.Pool = 0; ServerState.Wireframe = false; ServerState.TimeOfDay = "";
                    return;
                }

                var pools = (int[,])_poolsField.GetValue(_sm);
                var wires = (bool[,])_wireframesField.GetValue(_sm);
                ServerState.Pool = pools[seq, trial];
                ServerState.Wireframe = wires[seq, trial];
                // Trials 0-1 are Dusk, 2-3 are Night, per the study's sequence design.
                ServerState.TimeOfDay = trial < 2 ? "Dusk" : "Night";
            }
            catch (System.Exception e)
            {
                // A cast or bounds failure means the tables changed shape. Degrade the display, do not crash,
                // and stop trying so we do not spam the log every frame.
                _reflectionOk = false;
                if (!_reflectionWarned)
                {
                    _reflectionWarned = true;
                    Debug.LogWarning($"{Tag} Reading SequenceManager's private tables failed ({e.Message}). " +
                                     "Pool/wireframe/time-of-day display is now off; core control still works.");
                }
                ServerState.Pool = 0; ServerState.Wireframe = false; ServerState.TimeOfDay = "";
            }
        }

        // ---- Trial-end detection ------------------------------------------------------------------------

        // The study's tracker fires this when the last target in the current set is destroyed: once for the
        // tutorial and once per numbered trial. The manual End command routes through AdvanceFlow directly
        // (invoking OnTrialFinished by reflection does not re-fire this event).
        void OnTargetsCleared() => AdvanceFlow("complete");

        void AdvanceFlow(string how)
        {
            switch (ServerState.Phase)
            {
                case TrialPhase.Tutorial:
                    ServerState.TrialNumber = 1;
                    ServerState.Phase = TrialPhase.Ready;
                    ServerState.Report($"Tutorial {how}. Trial 1 is queued; arm it when ready.");
                    break;

                case TrialPhase.Recording:
                case TrialPhase.Paused:
                    if (ServerState.TrialNumber >= ServerState.TotalTrials)
                    {
                        ServerState.Phase = TrialPhase.Done;
                        ServerState.Report($"Trial {ServerState.TrialNumber} {how}. Sequence finished.");
                    }
                    else
                    {
                        int finished = ServerState.TrialNumber;
                        ServerState.TrialNumber = finished + 1;
                        ServerState.Phase = TrialPhase.Ready;
                        ServerState.Report($"Trial {finished} {how}. Trial {ServerState.TrialNumber} is queued; arm it when ready.");
                    }
                    break;

                // Menu / Ready / Armed / Done: a clear here is unexpected (no set should be up); ignore it.
            }
        }

        // ---- Commands (main thread) ---------------------------------------------------------------------

        void HandleCommand(string path, string body)
        {
            if (_sm == null)
            {
                ServerState.Report("No SequenceManager in the scene; command ignored.", "error");
                return;
            }

            switch (path)
            {
                case "/api/trial/sequence": HandleSequence(body); break;
                case "/api/trial/arm":      HandleArm();          break;
                case "/api/trial/start":    HandleStart();        break;   // bring-up backdoor; not on the page
                case "/api/trial/end":      HandleEnd();          break;
                case "/api/trial/pause":    HandlePause();        break;
                case "/api/trial/resume":   HandleResume();       break;
                case "/api/trial/bad":      HandleBad(body);      break;
                case "/api/trial/mark":     HandleMark(body);     break;
                default:
                    ServerState.Report($"Unknown command: {path}", "warn");
                    break;
            }
        }

        void HandleSequence(string body)
        {
            if (ServerState.Phase != TrialPhase.Menu)
            {
                ServerState.Report("A sequence is already selected. Restart the app to pick another.", "error");
                return;
            }

            if (!int.TryParse((body ?? "").Trim(), out int index) || index < 0 || index > 3)
            {
                ServerState.Report($"Sequence command needs an index 0-3; got \"{body}\".", "error");
                return;
            }

            if (_sm.sequenceButtons == null || index >= _sm.sequenceButtons.Length || _sm.sequenceButtons[index] == null)
            {
                ServerState.Report($"Sequence button {index} is not wired in the scene.", "error");
                return;
            }

            if (!ClickButton(_sm.sequenceButtons[index]))
            {
                ServerState.Report($"Sequence button {index} has no Button component to click.", "error");
                return;
            }

            ServerState.SelectedSequence = index + 1;
            ServerState.TrialNumber = 0;
            ServerState.Phase = TrialPhase.Tutorial;
            _pausedSeconds = 0f;
            ServerState.Report($"Sequence {index + 1} selected. The tutorial (4 practice gems) is running; " +
                               "recording starts with it by the study's design.");
        }

        // Arm the queued trial: re-enable the study's own Start button so the PARTICIPANT can start it on the
        // device. This is the only thing the dashboard's big button does; nothing here starts a trial.
        void HandleArm()
        {
            switch (ServerState.Phase)
            {
                case TrialPhase.Menu:
                    ServerState.Report("Select a sequence first.", "error");
                    return;
                case TrialPhase.Tutorial:
                    ServerState.Report("Tutorial in progress. Wait for it to finish before arming a trial.", "warn");
                    return;
                case TrialPhase.Armed:
                    ServerState.Report($"Trial {ServerState.TrialNumber} is already armed.", "warn");
                    return;
                case TrialPhase.Recording:
                case TrialPhase.Paused:
                    ServerState.Report("A trial is already running.", "warn");
                    return;
                case TrialPhase.Done:
                    ServerState.Report("The sequence is complete. Restart the app to run another.", "error");
                    return;
            }

            if (_sm.startButton == null)
            {
                ServerState.Report("Start button is not wired in the scene.", "error");
                return;
            }

            SetStartInteractable(true);
            ServerState.Phase = TrialPhase.Armed;
            ServerState.Report($"Trial {ServerState.TrialNumber} armed. The participant starts it on the device.");
        }

        // Bring-up backdoor only: starts an ARMED trial from HTTP by clicking the device button in code.
        // Nothing on the dashboard calls this; the participant is the one who starts trials.
        void HandleStart()
        {
            if (ServerState.Phase != TrialPhase.Armed)
            {
                ServerState.Report("Start is a backdoor and only works on an armed trial. Arm it first.", "error");
                return;
            }

            if (!ClickButton(_sm.startButton))
            {
                ServerState.Report("Start button has no Button component to click.", "error");
                return;
            }

            ServerState.Phase = TrialPhase.Recording;
            _pausedSeconds = 0f;
            ServerState.Report($"Trial {ServerState.TrialNumber} started via the backdoor.");
        }

        // End the current set early by invoking the study's own end-of-trial path (OnTrialFinished: pause
        // recording, clear spawned gems, advance the trial index, show the menu). Reflection because the
        // method is private; refused loudly if the binding failed. A MANUAL_END marker lands in the gaze JSON
        // first so the file itself says the set did not run to completion.
        void HandleEnd()
        {
            bool endable = ServerState.Phase == TrialPhase.Tutorial ||
                           ServerState.Phase == TrialPhase.Recording ||
                           ServerState.Phase == TrialPhase.Paused;
            if (!endable)
            {
                ServerState.Report("Nothing is running to end.", "error");
                return;
            }

            if (_onTrialFinishedMethod == null)
            {
                ServerState.Report("Cannot end early: SequenceManager.OnTrialFinished was not found " +
                                   "(renamed?). End the set on the device instead.", "error");
                return;
            }

            string what = ServerState.Phase == TrialPhase.Tutorial ? "Tutorial" : $"Trial {ServerState.TrialNumber}";

            // The study's end path assumes it is recording; a paused set is resumed for the handoff so its
            // marker and save land normally.
            if (ServerState.Phase == TrialPhase.Paused && _sm.tracker != null)
            {
                _pausedSeconds += Time.realtimeSinceStartup - _pauseStartedAt;
                _sm.tracker.ResumeRecording();
            }

            if (_sm.tracker != null)
                _sm.tracker.LogMarker($"MANUAL_END: {what} ended from the dashboard");

            try
            {
                _onTrialFinishedMethod.Invoke(_sm, null);
            }
            catch (System.Exception e)
            {
                ServerState.Report($"End failed inside the study's own end path: {e.Message}", "error");
                return;
            }

            // Invoking the method directly does not re-fire OnAllTargetsDestroyed, so advance our phase here.
            AdvanceFlow("ended from the dashboard");

            // Ending the tutorial early leaves its 4 practice gems in the scene: they are spawned directly,
            // not through the spawner that the end path clears. Say so rather than leaving it a mystery.
            if (what == "Tutorial")
                ServerState.Report("Tutorial ended from the dashboard; Trial 1 is queued. The practice gems " +
                                   "stay visible until the first trial spawns.", "warn");
        }

        void HandlePause()
        {
            if (ServerState.Phase != TrialPhase.Recording)
            {
                ServerState.Report("No recording trial to pause.", "error");
                return;
            }
            if (_sm.tracker == null)
            {
                ServerState.Report("No tracker to pause.", "error");
                return;
            }

            _sm.tracker.PauseRecording();
            ServerState.Phase = TrialPhase.Paused;
            _pauseStartedAt = Time.realtimeSinceStartup;
            ServerState.Report($"Trial {ServerState.TrialNumber} recording paused. Gems stay active; " +
                               "the page's trial clock is frozen.", "warn");
        }

        void HandleResume()
        {
            if (ServerState.Phase != TrialPhase.Paused)
            {
                ServerState.Report("No paused trial to resume.", "error");
                return;
            }
            if (_sm.tracker == null)
            {
                ServerState.Report("No tracker to resume.", "error");
                return;
            }

            _pausedSeconds += Time.realtimeSinceStartup - _pauseStartedAt;
            _sm.tracker.ResumeRecording();
            ServerState.Phase = TrialPhase.Recording;
            ServerState.Report($"Trial {ServerState.TrialNumber} recording resumed.");
        }

        // Flag a run invalid. The record that matters is the TRIAL_INVALID marker in the study's own gaze
        // JSON (one source of truth); ServerState.BadTrials only re-feeds the dashboard's list on reload.
        void HandleBad(string body)
        {
            int n;
            switch (ServerState.Phase)
            {
                case TrialPhase.Recording:
                case TrialPhase.Paused:
                    n = ServerState.TrialNumber;            // the one running right now
                    break;
                case TrialPhase.Ready:
                case TrialPhase.Armed:
                    n = ServerState.TrialNumber - 1;        // the one that just finished
                    break;
                case TrialPhase.Done:
                    n = ServerState.TrialNumber;            // the last one
                    break;
                default:
                    ServerState.Report("No trial to flag yet.", "error");
                    return;
            }

            if (n < 1)
            {
                ServerState.Report("No trial has run yet.", "error");
                return;
            }

            if (_sm.tracker == null)
            {
                ServerState.Report("No tracker to write the flag to.", "error");
                return;
            }

            string reason = (body ?? "").Trim();
            if (reason.Length == 0) reason = "no reason given";

            string entry = $"Trial {n}: {reason}";
            _sm.tracker.LogMarker($"TRIAL_INVALID: {entry}");
            ServerState.BadTrials.Add(entry);
            ServerState.Report($"Flagged BAD: {entry}", "warn");
        }

        void HandleMark(string body)
        {
            string text = (body ?? "").Trim();
            if (text.Length == 0)
            {
                ServerState.Report("Mark ignored: no text.", "warn");
                return;
            }

            if (_sm.tracker == null)
            {
                ServerState.Report("No tracker to mark; SequenceManager has none assigned.", "error");
                return;
            }

            // Lands the note in the study's own gaze JSON, not a second file, so the marker sits alongside the
            // data it annotates. This and the flags above are the only writes into the tracker's public API.
            _sm.tracker.LogMarker(text);
            ServerState.Report($"Marked: {text}");
        }

        // Invoke a button's onClick the same way a physical press would, routing through the study's own
        // guards. Works even when the button is hidden (SetActive false), which is exactly the state the flow
        // leaves them in. Mirrors how SequenceManager itself resolves the Button: on the object, or a parent.
        static bool ClickButton(GameObject go)
        {
            if (go == null) return false;
            var btn = go.GetComponent<Button>();
            if (btn == null) btn = go.GetComponentInParent<Button>(true);
            if (btn == null) return false;
            btn.onClick.Invoke();
            return true;
        }
    }
}
