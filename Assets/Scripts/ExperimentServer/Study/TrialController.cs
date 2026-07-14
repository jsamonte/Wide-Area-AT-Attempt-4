using System;
using System.Collections;
using System.Text.RegularExpressions;
using UnityEngine;
using ARCockpit.Core;
using ARCockpit.DevSettings;

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
        bool RequireCalibratedEyes => Profile == null || Profile.requireCalibratedEyes;
        float ClearReadoutAfterSeconds => Profile != null ? Profile.clearReadoutAfterSeconds : 6f;

        [Header("State (read-only view)")]
        public string state = "Idle";
        public string participantId = "";
        public string condition = "";
        public int trialNumber = 1;

        const string Tag = "[TRIAL]";

        DataLogger _logger;
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

        // ---- Commands ------------------------------------------------------------------------------------

        void HandleCommand(string path, string body)
        {
            switch (path)
            {
                case "/api/trial/identify": Identify(body); break;
                case "/api/trial/start":    StartTrial();   break;
                case "/api/trial/stop":     StopFromWeb(body); break;
                case "/api/trial/pause":    PauseTrial();   break;
                case "/api/trial/resume":   ResumeTrial();  break;
                case "/api/trial/redo":     RedoTrial(NoteOf(body, "redo")); break;

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
            public string condition;
            public int trial;
        }

        public void Identify(string json)
        {
            if (TrialData.State != TrialState.Idle)
            {
                Refuse("Cannot change the participant or trial number mid-trial. Stop the trial first.");
                return;
            }

            try
            {
                var id = JsonUtility.FromJson<Identity>(json);
                if (id == null) { Refuse($"Identify: could not parse {json}"); return; }

                string newParticipant = string.IsNullOrEmpty(id.participant) ? TrialData.ParticipantId : Sanitize(id.participant);
                bool participantChanged = newParticipant != TrialData.ParticipantId;

                TrialData.ParticipantId = newParticipant;
                if (!string.IsNullOrEmpty(id.condition)) TrialData.Condition = Sanitize(id.condition);

                // A new person starts at trial 1 and starts UNCONFIRMED. Inheriting the last participant's
                // counter mislabels every file, and inheriting their confirmed-seen set shows a green "map
                // seen" light for someone who has never looked at it.
                if (participantChanged)
                {
                    TrialData.TrialNumber = 1;
                    TrialData.RedoPending = false;
                    AoiData.ClearConfirmed();
                    AoiData.ResetTotals();
                    ClearTrialReadout();
                    Debug.Log($"{Tag} New participant: trial counter reset to 1 and confirmed-seen cleared.");
                }

                // The explicit trial number always wins, and it is the ONLY way to repair the counter after a
                // crash. TrialData is static in-memory state, so an app restart mid-session silently drops the
                // counter back to 1: the next file for a participant on their fifth trial would be labeled
                // trial 1. The timestamp in the name keeps it from overwriting anything, so no data is lost,
                // but the LABEL is what analysis groups by, and a wrong label is a wrong result. Before this,
                // the field was honored here but nothing could send it, so the damage was unrepairable without
                // a rebuild. It is Idle-only (guarded above) because renumbering a running trial would move the
                // goalposts under an open file.
                if (id.trial > 0) TrialData.TrialNumber = id.trial;

                Accept($"Identified: {TrialData.ParticipantId} / {TrialData.Condition} / trial {TrialData.TrialNumber}" +
                       (TrialData.RedoPending ? " (redo pending)" : ""));
            }
            catch (Exception e) { Refuse($"Identify failed: {e.Message}"); }
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
            if (!TrialData.Identified)
            {
                Refuse("Start refused: enter a participant ID and condition, then press Set.");
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

            _logger.Mark("TRIAL_START");
            TrialData.State = TrialState.Recording;
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

            // The row count and duration stay up for a few seconds so you can actually read what you just
            // recorded, then the card clears. What it must not do is sit there under the next participant.
            _clearAt = Time.time + Mathf.Max(0f, ClearReadoutAfterSeconds);

            // An invalid trial does not consume its number: the next attempt IS this trial, retaken. A valid
            // one advances. That way trial 3 always means the third real trial, whatever happened on the way.
            if (valid) TrialData.TrialNumber++;
            else TrialData.RedoPending = true;

            string outcome = $"Stopped ({(valid ? "filed as VALID" : "filed as INVALID: " + note)}). " +
                             $"Next trial: {TrialData.TrialNumber}" + (TrialData.RedoPending ? " (redo)" : "");
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

        // The timestamp is what makes every file unique even if the participant, condition, and trial number
        // repeat (a redo of a redo, a mis-set counter). Sortable form, so a directory listing is in run order.
        string FileName()
        {
            string redo = TrialData.RedoPending ? "_redo" : "";
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            return $"{TrialData.ParticipantId}_{TrialData.Condition}_trial_{TrialData.TrialNumber}{redo}_{stamp}.csv";
        }

        /// <summary>A participant ID typed on a phone keyboard becomes a file name. Anything that is not
        /// safe in a file name becomes an underscore.</summary>
        static string Sanitize(string s) => Regex.Replace(s.Trim(), @"[^A-Za-z0-9_\-]", "_");
    }
}
