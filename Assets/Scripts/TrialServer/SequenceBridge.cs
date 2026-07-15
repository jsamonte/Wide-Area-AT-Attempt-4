using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace TrialServer
{
    /// <summary>
    /// The one piece that touches his study. It turns the commands the <see cref="ExperimentServer"/> accepts
    /// into clicks on his existing SequenceManager buttons, and it tracks where the flow is so the dashboard
    /// can show it. NON-INVASIVE by design: it does not edit SequenceManager or EyeAndHeadTracker, it drives
    /// them through the same UnityEvents a physical button press would fire. That is why it can be dropped
    /// into any scene that has his study and just work.
    ///
    /// COMMAND HANDLING runs on the MAIN thread: the server enqueues inbound requests and drains them in
    /// Update, invoking OnCommand there. So everything in this class is main-thread and may freely touch Unity.
    ///
    /// THE ONE FRAGILE POINT is reflection. His pool and wireframe are in two PRIVATE 4x4 tables on
    /// SequenceManager, so to DISPLAY them we read those fields by name. This is guarded: if the names ever
    /// change, the pool/wireframe/time-of-day readout goes blank and a loud warning fires ONCE, but the core
    /// control (select, start, mark) keeps working because it never touches those fields. If Thomas wants this
    /// rock-solid, three public getters on SequenceManager remove the reflection entirely.
    /// </summary>
    public class SequenceBridge : MonoBehaviour
    {
        const string Tag = "[BRIDGE]";

        SequenceManager _sm;
        EyeAndHeadTracker _trackerSubscribed;   // remembered so OnDestroy unsubscribes from the right one

        // Reflection into SequenceManager's private state, bound once. Only used for the DISPLAY of
        // pool/wireframe/time-of-day; never for control.
        FieldInfo _seqIndexField;
        FieldInfo _trialIndexField;
        FieldInfo _poolsField;
        FieldInfo _wireframesField;
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

            RefreshTrialDisplay();
        }

        // ---- Binding ------------------------------------------------------------------------------------

        void TryBind()
        {
            // includeInactive: his SequenceManager deactivates its own GameObject during the tutorial and
            // between trials, so a plain FindObjectOfType would miss it at exactly the wrong moments.
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

            _reflectionOk = _seqIndexField != null && _trialIndexField != null &&
                            _poolsField != null && _wireframesField != null;

            if (!_reflectionOk && !_reflectionWarned)
            {
                _reflectionWarned = true;
                Debug.LogWarning($"{Tag} Could not bind SequenceManager's private fields " +
                    "(currentSequenceIndex/currentTrialIndex/sequencePools/sequenceWireframes). Pool, wireframe " +
                    "and time-of-day will not display. Core trial control (select/start/mark) is unaffected. " +
                    "This usually means a field was renamed; add public getters to make it rock-solid.");
            }
        }

        // ---- Live display -------------------------------------------------------------------------------

        // Pull the current/queued trial's pool + wireframe + time-of-day out of his private tables and publish
        // them to ServerState for the snapshot. Only meaningful while a numbered trial is queued or running;
        // cleared otherwise so the dashboard never shows a stale pool during the menu or tutorial.
        void RefreshTrialDisplay()
        {
            bool active = ServerState.Phase == TrialPhase.Ready || ServerState.Phase == TrialPhase.Recording;
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
                // Trials 0-1 are Dusk, 2-3 are Night, per his sequence design.
                ServerState.TimeOfDay = trial < 2 ? "Dusk" : "Night";
            }
            catch (System.Exception e)
            {
                // A cast or bounds failure means his tables changed shape. Degrade the display, do not crash,
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

        // His tracker fires this when the last target in the current set is destroyed. It fires once for the
        // tutorial and once per numbered trial. We advance our own phase off it, because he commands the flow
        // and thus always knows what "the last set cleared" means at this point.
        void OnTargetsCleared()
        {
            switch (ServerState.Phase)
            {
                case TrialPhase.Tutorial:
                    ServerState.TrialNumber = 1;
                    ServerState.Phase = TrialPhase.Ready;
                    ServerState.Report("Tutorial complete. Trial 1 is ready to start.");
                    break;

                case TrialPhase.Recording:
                    if (ServerState.TrialNumber >= ServerState.TotalTrials)
                    {
                        ServerState.Phase = TrialPhase.Done;
                        ServerState.Report($"Trial {ServerState.TrialNumber} complete. Sequence finished.");
                    }
                    else
                    {
                        int finished = ServerState.TrialNumber;
                        ServerState.TrialNumber = finished + 1;
                        ServerState.Phase = TrialPhase.Ready;
                        ServerState.Report($"Trial {finished} complete. Trial {ServerState.TrialNumber} is ready to start.");
                    }
                    break;

                // Menu / Ready / Done: a clear here is unexpected (no set should be recording); ignore it.
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
                case "/api/trial/start":    HandleStart();        break;
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
            ServerState.Report($"Sequence {index + 1} selected. Tutorial started.");
        }

        void HandleStart()
        {
            switch (ServerState.Phase)
            {
                case TrialPhase.Menu:
                    ServerState.Report("Select a sequence first.", "error");
                    return;
                case TrialPhase.Tutorial:
                    ServerState.Report("Tutorial in progress. Wait for it to finish before starting a trial.", "warn");
                    return;
                case TrialPhase.Recording:
                    ServerState.Report("A trial is already recording.", "warn");
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

            if (!ClickButton(_sm.startButton))
            {
                ServerState.Report("Start button has no Button component to click.", "error");
                return;
            }

            ServerState.Phase = TrialPhase.Recording;
            ServerState.Report($"Trial {ServerState.TrialNumber} started.");
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

            // Lands the note in HIS gaze JSON, not a second file, so the marker sits alongside the data it
            // annotates. This is the one place we call into his tracker's public API to write.
            _sm.tracker.LogMarker(text);
            ServerState.Report($"Marked: {text}");
        }

        // Invoke a button's onClick the same way a physical press would, routing through his own guards. Works
        // even when the button is hidden (SetActive false), which is exactly the state his flow leaves them in.
        // Mirrors how SequenceManager itself resolves the Button: on the object, or on a parent.
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
