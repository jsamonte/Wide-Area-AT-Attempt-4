using System;
using System.Collections;
using UnityEngine;
using ARCockpit.Core;
using ARCockpit.Data;
using ARCockpit.DevSettings;
using ARCockpit.FlightPlan;

namespace ARCockpit.Study
{
    /// <summary>
    /// The trial state machine, and the ONLY thing allowed to start a trial. It owns the Idle / Recording /
    /// Paused state, the file naming, and the one guardrail that matters: a trial reset repositions the
    /// aircraft, so it may fire ONLY from Idle. Never mid-trial.
    ///
    /// Commands arrive from <see cref="ExperimentServer"/> (already marshalled onto the main thread) and
    /// later from the ArUco backup tag. Both call the same methods, so there is one code path to trust
    /// rather than two that drift.
    ///
    ///     adb logcat -d -s Unity | Select-String "\[TRIAL\]"
    /// </summary>
    [RequireComponent(typeof(DataLogger))]
    public class TrialController : MonoBehaviour
    {
        [Tooltip("Leave empty to use the auto-loaded Resources/StudyProfile.")]
        public StudyProfile profileOverride;

        // The trial GATES live on StudyProfile: they are study parameters (they decide what a trial refuses
        // to do), they must be reachable on the headset from the 231 window, and DataLogger stamps them into
        // every trial's CSV header so a file records the rules it was collected under. They were Inspector
        // fields here, which meant a run-day change needed Unity and a rebuild, and left no trace in the data.
        StudyProfile Profile => profileOverride != null ? profileOverride : StudyProfile.Active;
        bool UseTrialReset => Profile != null && Profile.useTrialReset;
        bool RequireCalibratedEyes => Profile == null || Profile.requireCalibratedEyes;
        float ClearReadoutAfterSeconds => Profile != null ? Profile.clearReadoutAfterSeconds : 6f;

        [Header("State (read-only view)")]
        public string state = "Idle";
        public string participantId = "";
        public string condition = "";
        public int trialNumber = 1;

        const string Tag = "[TRIAL]";

        /// <summary>Read by the DataLogger for its xp_connected column, so the logger does not need its own
        /// receiver reference.</summary>
        public static bool XPlaneConnected;

        DataLogger _logger;
        TrialReset _reset;
        XP12Receiver _receiver;
        CockpitAnchor _cockpit;
        bool _starting;
        float _clearAt = -1f;
        bool _trackingWasLost;

        void Awake()
        {
            _logger = GetComponent<DataLogger>();

            // Spawn the study dev window's anchor (ArUco 231), exactly as FlightPlanSystem spawns the flight
            // plan's 230. It is a bare handle with no visual: a MarkerAnchor that catches the tag and a
            // DevModeController that pops the window carrying the study knobs (gaze attribution, the trial
            // gates, the logger, the dashboard). The anchor's profile is assigned BEFORE the controller is
            // added, because the controller's Awake reads it off the anchor.
            if (GameObject.Find("StudyDevAnchor") == null)
            {
                var go = new GameObject("StudyDevAnchor");
                go.transform.SetParent(transform, false);
                var anchor = go.AddComponent<MarkerAnchor>();
                anchor.markerProfile = StudyProfile.Active;
                go.AddComponent<DevModeController>();
            }
        }

        void OnEnable() => ExperimentServer.OnCommand += HandleCommand;
        void OnDisable() => ExperimentServer.OnCommand -= HandleCommand;

        void Update()
        {
            if (_receiver == null) _receiver = FindObjectOfType<XP12Receiver>();
            if (_reset == null) _reset = FindObjectOfType<TrialReset>();
            if (_cockpit == null) _cockpit = CockpitAnchor.Active;
            XPlaneConnected = _receiver != null && _receiver.Connected;
            TrialData.UseTrialReset = UseTrialReset;

            PublishArmedSlot();

            // The calibration gate depends on this having run. Do not leave it to the web server: with the
            // server disabled, Start would refuse every trial for a calibration failure that never happened.
            // Refresh is frame-guarded, so calling it here as well is free.
            StatusSnapshot.Refresh(Time.unscaledDeltaTime);

            // The one thing this component does every frame: while Recording, a row goes down.
            if (TrialData.State == TrialState.Recording) _logger.WriteRow();

            WatchHeadTracking();

            if (_clearAt > 0f && Time.time >= _clearAt) ClearTrialReadout();

            // Mirror into the Inspector for a glance in the Editor. TrialData stays the source of truth.
            state = TrialData.State.ToString();
            participantId = TrialData.ParticipantId;
            condition = TrialData.Condition;
            trialNumber = TrialData.TrialNumber;
        }

        /// <summary>
        /// Stamp head-tracking loss into the CSV, on the edges only.
        ///
        /// WHY. When the headset loses tracking the AR elements swim or freeze, so what the participant SEES is
        /// no longer what the study thinks it is showing them, and their gaze during that window means nothing.
        /// But every other signal stays healthy: eye tracking still reports, the sim link is still up, rows are
        /// still going down at frame rate. Nothing in the file would say the segment was junk. A dashboard
        /// banner alone does not fix that, because at analysis time, weeks later, nobody remembers what the
        /// dashboard was showing. So it goes in the data, on both edges, and analysis can cut the window out.
        ///
        /// Edges only, deliberately: a marker on every frame of a ten-second dropout is ten seconds of noise.
        /// </summary>
        void WatchHeadTracking()
        {
            bool lost = StatusSnapshot.TrackingLost;
            if (lost == _trackingWasLost) return;
            _trackingWasLost = lost;

            if (lost)
            {
                Debug.LogWarning($"{Tag} HEAD TRACKING LOST. The AR elements are not where the participant thinks.");
                TrialData.Report("Head tracking LOST. The AR elements are unreliable until it recovers.", "error");
            }
            else
            {
                Debug.Log($"{Tag} Head tracking recovered.");
                TrialData.Report("Head tracking recovered.", "warn");
            }

            // Only into the file if there IS a file. Outside a trial the dashboard banner is the whole story.
            if (TrialData.State != TrialState.Idle)
            {
                _logger.Mark(lost ? "TRACKING_LOST" : "TRACKING_REGAINED");
                _logger.WriteRow();
            }
        }

        /// <summary>
        /// Push the armed slot's condition out to the two places that act on it: the AR gate (which decides
        /// whether 241 is allowed to deploy the map and the NavBall at all) and TrialData (which the file
        /// name, the CSV and the dashboard read).
        ///
        /// This is what makes the physical condition a STATE OF THE APP rather than an investigator
        /// remembering not to show a tag. It runs every frame while Idle so the elements appear and disappear
        /// as the board arms a different slot, and is FROZEN during a trial: changing the condition under a
        /// recording participant would make the file's own label a lie.
        /// </summary>
        void PublishArmedSlot()
        {
            if (TrialData.State != TrialState.Idle) return;

            TrialSlot slot = SessionPlan.Active?.Armed;
            if (slot == null)
            {
                // No participant, or nothing armed: this is SETUP, not a trial. Show the AR elements. The
                // physical-condition blackout is a property of an armed no-AR trial, not the default state:
                // making "nothing armed" hide everything meant 241 wiped the map and NavBall during placement,
                // before any participant existed, which is exactly what went wrong on device.
                CockpitAnchor.ArElementsAllowed = true;
                TrialData.Condition = "";
                return;
            }

            // An armed slot IS a condition. Now the gate bites: a no-AR trial hides the AR elements, whatever
            // tags are shown.
            CockpitAnchor.ArElementsAllowed = slot.arOn;
            TrialData.Condition = slot.Code;
            TrialData.TrialNumber = slot.number;
        }

        // ---- Commands ------------------------------------------------------------------------------------

        void HandleCommand(string path, string body)
        {
            switch (path)
            {
                case "/api/trial/identify": Identify(body); break;
                case "/api/trial/arm":      ArmSlot(SlotOf(body)); break;
                case "/api/trial/start":    StartTrial();   break;
                case "/api/trial/stop":     StopFromWeb(body); break;
                case "/api/trial/pause":    PauseTrial();   break;
                case "/api/trial/resume":   ResumeTrial();  break;
                case "/api/trial/redo":     RedoTrial(NoteOf(body, "redo")); break;
                case "/api/trial/capture":  CapturePose();  break;
                case "/api/trial/mark":     MarkEvent(NoteOf(body, "")); break;
                default: Refuse($"Unknown command {path}"); break;
            }
        }

        // ---- Operator feedback ---------------------------------------------------------------------------
        //
        // Every command outcome goes through exactly one of these two, so a refusal can never be logged
        // without also reaching the dashboard. Before this, the guards below spoke only to logcat, which on
        // run day is a PC that is not plugged in: the operator pressed Start, the page said "Sent", the state
        // never changed, and the reason was unreachable. The reasons were always good. They were just invisible.

        /// <summary>The command did what was asked.</summary>
        void Accept(string message)
        {
            Debug.Log($"{Tag} {message}");
            TrialData.Report(message, "ok");
        }

        /// <summary>The command was REFUSED, and this is why. Always an error in the log (it is a thing the
        /// operator asked for that did not happen) and always shown on the page.</summary>
        void Refuse(string message)
        {
            Debug.LogError($"{Tag} {message}");
            TrialData.Report(message, "error");
        }

        void StopFromWeb(string body)
        {
            var v = Parse(body);
            // Default to VALID only when the dashboard says nothing. The dashboard always says.
            StopTrial(v == null || v.valid, v != null ? v.note : "");
        }

        static string NoteOf(string body, string fallback)
        {
            var v = Parse(body);
            return v != null && !string.IsNullOrEmpty(v.note) ? v.note : fallback;
        }

        static Verdict Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<Verdict>(json); }
            catch { return null; }
        }

        [Serializable]
        class Verdict
        {
            public bool valid = true;
            public string note = "";
        }

        [Serializable]
        class Identity
        {
            public string participant;
        }

        [Serializable]
        class SlotRef
        {
            public int slot = -1;
        }

        static int SlotOf(string body)
        {
            if (string.IsNullOrEmpty(body)) return -1;
            try { var s = JsonUtility.FromJson<SlotRef>(body); return s != null ? s.slot : -1; }
            catch { return -1; }
        }

        /// <summary>
        /// Set the participant, which BUILDS OR LOADS THEIR WHOLE SESSION. This is now the only hand-typed
        /// input in the entire data path.
        ///
        /// It used to take a condition string and a trial number as well, both typed, both unchecked against
        /// anything, which is how a perfect-looking file ends up with the wrong label on it. Those are gone:
        /// the condition and the number come from the armed slot of the SessionPlan, which is derived from
        /// the participant number by the counterbalance. See SessionPlan and TRIAL_FLOW_ARCHITECTURE.md.
        /// </summary>
        public void Identify(string json)
        {
            if (TrialData.State != TrialState.Idle)
            {
                Refuse("Cannot change the participant mid-trial. Stop the trial first.");
                return;
            }

            try
            {
                var id = JsonUtility.FromJson<Identity>(json);
                if (id == null) { Refuse($"Identify: could not parse {json}"); return; }

                string newParticipant = string.IsNullOrEmpty(id.participant)
                    ? TrialData.ParticipantId
                    : SessionPlan.Sanitize(id.participant);

                if (string.IsNullOrEmpty(newParticipant))
                {
                    Refuse("Identify: no participant ID. Everything else in a trial's identity is derived from " +
                           "this one field, so it cannot be empty.");
                    return;
                }

                bool participantChanged = newParticipant != TrialData.ParticipantId;
                TrialData.ParticipantId = newParticipant;

                // A new person starts UNCONFIRMED. Inheriting the last participant's confirmed-seen set shows
                // a green "map seen" light for someone who has never looked at it.
                if (participantChanged)
                {
                    TrialData.RedoPending = false;
                    AoiData.ClearConfirmed();
                    AoiData.ResetTotals();
                    ClearTrialReadout();
                }

                SessionPlan plan = SessionPlan.LoadOrCreate(newParticipant);
                if (plan == null)
                {
                    Refuse($"Identify: could not build a session plan for '{newParticipant}'.");
                    return;
                }

                TrialSlot armed = plan.Armed;
                string where = armed == null
                    ? "every trial is done"
                    : (armed.practice ? "PRACTICE" : $"trial {armed.number} of {plan.RecordedCount}") +
                      $", {armed.Label}";

                Accept($"{plan.participant}: counterbalance group {plan.group}, " +
                       $"{plan.CompletedCount}/{plan.RecordedCount} done. Next up: {where}.");
            }
            catch (Exception e) { Refuse($"Identify failed: {e.Message}"); }
        }

        /// <summary>Arm a slot from the dashboard board. Idle only: arming mid-trial would change the
        /// condition under an open file.</summary>
        public void ArmSlot(int index)
        {
            if (TrialData.State != TrialState.Idle)
            {
                Refuse("Cannot arm a different trial while one is running. Stop it first.");
                return;
            }
            if (SessionPlan.Active == null)
            {
                Refuse("Arm refused: no participant is set, so there is no session plan to arm a trial in.");
                return;
            }

            if (!SessionPlan.Active.Arm(index, out string why)) { Refuse(why); return; }

            TrialSlot s = SessionPlan.Active.Armed;
            Accept($"Armed {(s.practice ? "PRACTICE" : "trial " + s.number)}: {s.Label}. " +
                   $"Show the cockpit tag (241) to deploy and begin.");
        }

        [ContextMenu("Start Trial")]
        public void StartTrial()
        {
            if (_starting) { Refuse("Already starting."); return; }

            if (TrialData.State != TrialState.Idle)
            {
                Refuse($"Start refused: state is {TrialData.State}, not Idle. Stop the current trial first.");
                return;
            }

            // No participant means a file called "__trial_1.csv" that cannot be matched to a human being
            // afterward. That is unrecoverable, so it is a hard block, not a warning.
            if (SessionPlan.Active == null)
            {
                Refuse("Start refused: no participant is set. Enter the participant ID on the dashboard and " +
                       "press Set; the condition and the trial number are derived from it.");
                return;
            }

            // Nothing armed means nothing to fly. This happens at a block boundary on purpose (the plan does
            // not step over one by itself: that is where the washout break goes), and at the end of a session.
            if (SessionPlan.Active.Armed == null)
            {
                TrialSlot next = SessionPlan.Active.NextPending();
                Refuse(next == null
                    ? $"Start refused: {SessionPlan.Active.participant} has finished every trial in their plan."
                    : $"Start refused: no trial is armed. The next one is trial {next.number} (block {next.block}), " +
                      $"and a new block is never armed automatically: take the break, then arm it on the board.");
                return;
            }

            // The calibration gate. See STUDY_ARCHITECTURE: uncalibrated eyes return Success with confidence
            // 0 and invalid pupils, so the file looks perfect and the gaze data in it is garbage.
            if (RequireCalibratedEyes && EyeData.Tracking && !StatusSnapshot.SawHighConfidence)
            {
                Refuse("Start refused: the eye tracker has never reported high confidence. Run the ML2 eye " +
                       "calibration on this participant. (Override with study.requireCalibratedEyes = false " +
                       "in the 231 window, and only for a deliberate no-eye-tracking run.)");
                return;
            }

            StartCoroutine(StartRoutine());
        }

        IEnumerator StartRoutine()
        {
            _starting = true;
            _clearAt = -1f;   // cancel the pending clear: it would otherwise fire mid-trial and zero the live row count

            if (UseTrialReset)
            {
                // Two DIFFERENT failures, named separately. They used to share one "no connected sim" message,
                // which sends you to check the network when the real problem is that no TrialReset exists in
                // the loaded scenes at all (it lives in the FlightPlanRoute review scene, not in Systems).
                if (_reset == null)
                {
                    Refuse("Start refused: study.useTrialReset is ON but there is no TrialReset component in any " +
                           "loaded scene, so there is nothing to reposition the aircraft. Add one to Systems, or " +
                           "turn the flag off in the 231 dev window.");
                    _starting = false;
                    yield break;
                }
                if (!XPlaneConnected)
                {
                    Refuse("Start refused: study.useTrialReset is ON but X-Plane is not connected, so the aircraft " +
                           "cannot be repositioned. Connect the sim, or turn the flag off in the 231 dev window " +
                           "for a no-sim run.");
                    _starting = false;
                    yield break;
                }

                TrialData.Report("Repositioning to the trial start pose...", "warn");
                Debug.Log($"{Tag} Repositioning to the trial start pose...");
                _reset.BeginTrial();

                // Wait for the reposition to finish. Opening the file first would record the snap itself as
                // if it were flight: a spike in every channel at t=0 that looks like real data.
                yield return null;
                float timeout = 20f;
                while (_reset.Busy && timeout > 0f) { timeout -= Time.deltaTime; yield return null; }
                if (_reset.Busy)
                {
                    Debug.LogWarning($"{Tag} Reposition did not finish in time; starting anyway.");
                    TrialData.Report("The reposition did not finish within 20 s. Starting anyway: check the " +
                                     "aircraft is actually where you expect before you trust this trial.", "warn");
                }
            }

            // Dwell totals belong to exactly one trial. Confirmed (can the participant SEE each element) is
            // a property of the setup, so AoiData deliberately keeps it across the reset.
            AoiData.ResetTotals();

            string file = FileName();
            if (!_logger.Open(file))
            {
                Refuse($"Start refused: could not open {file} for writing. The trials directory may be " +
                       "unwritable or the storage full.");
                _starting = false;
                yield break;
            }

            // Freeze the cockpit frame for the duration. The 241 tag lives on the armrest, out of the normal
            // scan pattern, but if it catches the world cameras mid-flight the frame re-snaps and EVERY AR
            // element moves under a recording participant, with nothing in the file to say so. Adjust freely
            // while Idle; frozen while Recording. See MarkerAnchor.Freeze.
            if (_cockpit == null) _cockpit = CockpitAnchor.Active;
            if (_cockpit != null) _cockpit.Locked = true;

            _logger.Mark("TRIAL_START");
            TrialData.State = TrialState.Recording;
            SessionPlan.Active?.MarkRunning(file);
            _starting = false;

            Accept($"RECORDING {file}");
        }

        /// <summary>Stop and FILE the trial. valid = true files it under trials/valid, false under
        /// trials/invalid with your note. Both are kept: nothing is ever deleted.</summary>
        public void StopTrial(bool valid = true, string note = "")
        {
            if (TrialData.State == TrialState.Idle) { Refuse("Stop ignored: no trial is running."); return; }

            _logger.Mark(valid ? "TRIAL_STOP" : "TRIAL_STOP_INVALID");
            _logger.WriteRow();          // the marker rides a real row, so it lands on a real sample
            _logger.Close(valid, note);

            TrialData.State = TrialState.Idle;
            TrialData.RedoPending = false;

            // The frame is adjustable again: the participant can re-scan 241 and re-snap before the next run.
            if (_cockpit != null) _cockpit.Locked = false;

            // The row count and duration stay up for a few seconds so you can actually read what you just
            // recorded, then the card clears. What it must not do is sit there under the next participant.
            _clearAt = Time.time + Mathf.Max(0f, ClearReadoutAfterSeconds);

            // The plan owns the trial number now. An invalid trial does not consume its slot: it goes back to
            // Pending and stays armed, so the next start simply re-flies it. A valid one arms the next slot
            // (within the block; a block boundary waits for a human). That way trial 3 always means the third
            // real trial, whatever happened on the way, and it survives an app restart, which the old
            // in-memory counter did not.
            if (!valid) TrialData.RedoPending = true;
            SessionPlan.Active?.MarkComplete(valid);

            TrialSlot next = SessionPlan.Active?.Armed;
            string upNext = next == null
                ? "nothing armed (block finished, or the session is done)"
                : (next.practice ? "PRACTICE" : $"trial {next.number}") + ", " + next.Label;

            string outcome = $"Stopped ({(valid ? "filed as VALID" : "filed as INVALID: " + note)}). " +
                             $"Next: {upNext}.";
            Debug.Log($"{Tag} {outcome}");
            // A bad trial is not an error (it was a deliberate call by the operator), but it is not routine
            // either, so it reads amber rather than green: the page should look different after you have just
            // thrown a run away.
            TrialData.Report(outcome, valid ? "ok" : "warn");
        }

        [ContextMenu("Stop Trial (valid)")]
        public void StopValid() => StopTrial(true);

        [ContextMenu("Stop Trial (invalid)")]
        public void StopInvalid() => StopTrial(false, "marked invalid in the Editor");

        /// <summary>Abandon the current trial: file it as invalid and flag the next start as a redo. This is
        /// the same thing as an invalid stop, named for what it is used for on run day.</summary>
        [ContextMenu("Redo Trial")]
        public void RedoTrial(string note = "redo")
        {
            if (TrialData.State != TrialState.Idle)
            {
                StopTrial(false, note);
                return;
            }

            TrialData.RedoPending = true;
            Accept($"Redo armed. The next start writes trial {TrialData.TrialNumber} flagged _redo.");
        }

        [ContextMenu("Pause Trial")]
        public void PauseTrial()
        {
            if (TrialData.State != TrialState.Recording)
            {
                Refuse($"Pause ignored: state is {TrialData.State}, not Recording.");
                return;
            }
            _logger.Mark("PAUSE");
            _logger.WriteRow();
            TrialData.State = TrialState.Paused;
            TrialData.Report("Paused. The file stays open and no rows are written.", "warn");
            Debug.Log($"{Tag} Paused (the file stays open; no rows are written).");
        }

        [ContextMenu("Resume Trial")]
        public void ResumeTrial()
        {
            if (TrialData.State != TrialState.Paused)
            {
                Refuse($"Resume ignored: state is {TrialData.State}, not Paused.");
                return;
            }
            TrialData.State = TrialState.Recording;
            _logger.Mark("RESUME");
            Accept("Resumed.");
        }

        /// <summary>
        /// Stamp an EVENT MARKER onto the running trial: "the autopilot dropped out here", "the participant
        /// coughed", "the sim stuttered". Writes a real row carrying MARK:&lt;text&gt;, so it lands on a real
        /// sample and analysis can find the moment by timestamp.
        ///
        /// WHY THIS EXISTS. Before it, an operator who noticed something go slightly wrong mid-trial had
        /// exactly two options: kill the run, or remember it. Killing a run over something that may not have
        /// mattered is expensive, and remembering it does not survive twenty participants. This is the cheap
        /// middle option, and it is why the marker machinery (DataLogger.Mark) was there all along and simply
        /// unreachable: the trial keeps recording, and the moment is now IN the data rather than in someone's
        /// memory, so the decision about whether it mattered can be made later, at analysis, with the trace in
        /// front of you.
        ///
        /// Recording only, on purpose. A marker with no open file has nowhere to go, and silently accepting one
        /// would teach the operator that pressing it always works.
        /// </summary>
        public void MarkEvent(string text)
        {
            if (TrialData.State == TrialState.Idle)
            {
                Refuse("Marker ignored: no trial is running, so there is no file to write it to.");
                return;
            }

            // DataLogger.Esc already quotes a cell containing a comma, so commas are safe and are left alone.
            // Newlines are NOT handled there, and a newline in the marker column would split one sample across
            // two CSV rows, which is silent corruption of every column after it. So they are stripped here.
            string safe = string.IsNullOrEmpty(text)
                ? "EVENT"
                : text.Replace("\r", " ").Replace("\n", " ").Trim();

            _logger.Mark("MARK:" + safe);
            _logger.WriteRow();   // the marker rides a real sample, exactly as TRIAL_START and PAUSE do
            Accept($"Marked: {safe}");
        }

        /// <summary>Capture the aircraft's current pose as the trial start point. Idle only: this is a setup
        /// action, and doing it mid-trial would move the goalposts under a running recording.</summary>
        [ContextMenu("Capture Start Pose")]
        public void CapturePose()
        {
            if (TrialData.State != TrialState.Idle)
            {
                Refuse("Capture refused: the start pose can only be captured while Idle. It is a setup action, " +
                       "and moving the goalposts under a running trial is exactly what it must not do.");
                return;
            }
            if (_reset == null)
            {
                Refuse("Capture refused: there is no TrialReset component in any loaded scene. It lives in the " +
                       "FlightPlanRoute review scene, not in Systems.");
                return;
            }
            _reset.Capture();
            Accept("Start pose captured from the aircraft's current position.");
        }

        /// <summary>Blank the finished-trial numbers on the dashboard. Idle with a file name, an elapsed
        /// time, and a row count from a trial that ended ten minutes ago is a card that lies about what is
        /// happening right now.</summary>
        void ClearTrialReadout()
        {
            TrialData.CurrentFile = "";
            TrialData.ElapsedSeconds = 0f;
            TrialData.RowsWritten = 0;
            _clearAt = -1f;
        }

        // ---- Naming --------------------------------------------------------------------------------------

        // Every field here now comes from the armed slot rather than a typed box. The timestamp still makes
        // each file unique even when the rest repeats (a redo of a redo), and its sortable form means a
        // directory listing is in run order.
        string FileName()
        {
            TrialSlot s = SessionPlan.Active?.Armed;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            if (s == null)   // should be unreachable: StartTrial refuses without an armed slot
                return $"{TrialData.ParticipantId}_UNARMED_{stamp}.csv";

            // Practice is labeled in the file name, not just in the plan, because the file gets copied off the
            // headset and analyzed somewhere the plan is not. A practice run that reads as trial 1 is a
            // novelty-effect run in the middle of the data.
            if (s.practice)
                return $"{TrialData.ParticipantId}_{s.Code}_practice_{stamp}.csv";

            // attempts is incremented by MarkRunning, which runs AFTER this, so a slot being flown for the
            // first time reads 0 here and a re-fly of a thrown-away trial reads 1 or more.
            string redo = s.attempts > 0 ? "_redo" : "";
            return $"{TrialData.ParticipantId}_{s.Code}_trial_{s.number}{redo}_{stamp}.csv";
        }
    }
}
