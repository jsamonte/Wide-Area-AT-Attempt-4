using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using ARCockpit.DevSettings;

namespace ARCockpit.Study
{
    /// <summary>
    /// Writes the per-trial CSV. ONE row per frame carrying everything: time, head pose, gaze, eye state,
    /// AOI, FlightData, GuidanceData. One file, one clock, so the EEG alignment is a single join rather
    /// than a reconciliation of three files that each drifted differently.
    ///
    /// This class does NOT own the trial state and cannot start or stop itself: <see cref="TrialController"/>
    /// drives it. That split is deliberate. A file writer that can also reposition the aircraft is how a
    /// reset ends up firing mid-trial.
    ///
    /// Timestamps come from a MONOTONIC clock, and the row count is a fact rather than an assumption. The
    /// plan called for "60 Hz polling", but a dropped frame gives a 59-row second, and the analysis must
    /// never have to trust the frame counter. Every row carries its own time.
    /// </summary>
    public class DataLogger : MonoBehaviour
    {
        [Tooltip("Leave empty to use the auto-loaded Resources/StudyProfile.")]
        public StudyProfile profileOverride;

        // The flush cadence lives on StudyProfile: it is a data-safety parameter (how much of a trial is at
        // risk if the app dies mid-run), so it belongs with the other study knobs, tunable from the 231
        // window and stamped into the CSV header.
        StudyProfile Profile => profileOverride != null ? profileOverride : StudyProfile.Active;
        int FlushEveryRows => Profile != null ? Profile.flushEveryRows : 60;

        [Tooltip("Head pose source. Leave empty to use the main camera (the XR rig's head).")]
        public Transform head;

        const string Tag = "[LOG]";

        StreamWriter _writer;
        double _startTime;
        DateTime _startedUtc;
        int _rowsSinceFlush;
        string _pendingMarker = "";

        public bool IsOpen => _writer != null;
        public string CurrentPath { get; private set; } = "";

        /// <summary>Everything the study records lives under one directory, so the end-of-session pull is a
        /// single folder rather than a hunt through persistentDataPath alongside dev_overrides.json.</summary>
        public static string TrialsDir => Path.Combine(Application.persistentDataPath, "trials");
        static string IncompleteDir => Path.Combine(TrialsDir, "_incomplete");

        /// <summary>Every column, in order. Kept in one place so the header and the row can never drift
        /// apart, which is the classic way a CSV ends up silently shifted by one column.</summary>
        static readonly string[] Columns =
        {
            "t_sec", "unix_ms", "marker",
            "head_x", "head_y", "head_z", "head_pitch", "head_yaw", "head_roll",
            "eye_tracking", "gaze_valid", "gaze_conf",
            "gaze_ox", "gaze_oy", "gaze_oz", "gaze_dx", "gaze_dy", "gaze_dz",
            "fix_valid", "fix_x", "fix_y", "fix_z",
            "open_l", "open_r",
            "pupil_l_mm", "pupil_r_mm",
            "behavior", "behavior_amp_deg", "behavior_vel_dps", "behavior_dur_ms",
            "aoi", "aoi_dwell_s", "aoi_hit_dist_m"
        };

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        readonly StringBuilder _row = new StringBuilder(1024);

        void Awake()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
        }

        // ---- Lifecycle (TrialController is the only caller) --------------------------------------------

        public bool Open(string fileName)
        {
            Close();

            try
            {
                Directory.CreateDirectory(IncompleteDir);
                CurrentPath = Path.Combine(IncompleteDir, fileName);

                // Never overwrite. If a file with this name exists, the run that produced it is data we do
                // not have permission to destroy, so we suffix rather than clobber. The timestamp in the name
                // makes this all but impossible; this is the backstop.
                if (File.Exists(CurrentPath))
                {
                    string alt = Path.GetFileNameWithoutExtension(fileName) + "_dup" + DateTime.Now.ToString("HHmmss") + ".csv";
                    Debug.LogWarning($"{Tag} {fileName} already exists. Writing {alt} instead; nothing was overwritten.");
                    CurrentPath = Path.Combine(IncompleteDir, alt);
                }

                _writer = new StreamWriter(CurrentPath, false, Encoding.UTF8);
                WriteParamHeader(_writer);
                _writer.WriteLine(string.Join(",", Columns));
                _writer.Flush();

                _startTime = Time.realtimeSinceStartupAsDouble;
                _startedUtc = DateTime.UtcNow;
                _rowsSinceFlush = 0;
                TrialData.RowsWritten = 0;
                TrialData.ElapsedSeconds = 0f;
                TrialData.CurrentFile = Path.GetFileName(CurrentPath);

                Debug.Log($"{Tag} Opened {CurrentPath}");
                return true;
            }
            catch (Exception e)
            {
                _writer = null;
                CurrentPath = "";
                Debug.LogError($"{Tag} Could not open {fileName}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stamp the STUDY PARAMETERS into the top of the file, as '#' comment lines above the column header.
        ///
        /// Why this exists. The gaze attribution rules (the confidence floor, the dwell that counts as seen,
        /// the hitbox padding) decide what the numbers in this file MEAN. Two trials recorded weeks apart
        /// under different values are not comparable, and until now nothing in the data said so: you would
        /// have had to remember. A trial file should be self-describing, so it carries its own rules.
        ///
        /// Every study.* tunable is written, whatever it is, by walking the registry rather than a hand-kept
        /// list here. A hand-kept list is a second place the knobs live, and it would silently fall behind the
        /// day someone adds one.
        ///
        /// FORMAT NOTE, and it matters for analysis: these lines start with '#', which is a comment to most
        /// CSV readers but is NOT part of the CSV standard. In pandas, read them with
        /// pd.read_csv(path, comment='#'). A reader that does not skip comments will treat the first '#' line
        /// as the header row.
        /// </summary>
        static void WriteParamHeader(StreamWriter w)
        {
            w.WriteLine("# AR Cockpit trial. Rows below the column header are the data.");
            w.WriteLine($"# recorded_utc = {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
            w.WriteLine($"# app_version = {Application.version}");
            w.WriteLine("# --- study parameters in force for THIS trial (from StudyProfile + any dev override) ---");

            int written = 0;
            foreach (DevTunable t in DevSettingsStore.Tunables)
            {
                if (t == null || t.Key == null || !t.Key.StartsWith("study.", StringComparison.Ordinal)) continue;
                string mark = DevSettingsStore.IsOverridden(t.Key) ? "  (overridden on device)" : "";
                w.WriteLine($"# {t.Key} = {t.Serialize()}{mark}");
                written++;
            }

            // The study knobs register when TrialController's dev anchor comes up. If none are here, the
            // StudyProfile asset is missing (the code fell back to in-memory defaults), and the file cannot
            // say what rules produced it. Say so IN the file rather than leaving a silent gap.
            if (written == 0)
                w.WriteLine("# WARNING: no study parameters registered. Is Resources/StudyProfile missing?");
        }

        /// <summary>
        /// Close the file and FILE it: valid trials land in trials/valid, spoiled ones in trials/invalid.
        /// Nothing is ever deleted. A trial you cut short is still evidence, and whether to use it is a
        /// decision for analysis, not for a button pressed under time pressure on run day.
        ///
        /// Files are BORN in trials/_incomplete and only move on a deliberate close. So if the app dies or
        /// the battery goes mid-trial, the partial file stays in _incomplete and says exactly what it is.
        /// A crashed run can never be mistaken for a good one.
        /// </summary>
        public void Close(bool? valid = null, string note = "")
        {
            if (_writer == null) return;

            string from = CurrentPath;
            int rows = TrialData.RowsWritten;
            float seconds = TrialData.ElapsedSeconds;

            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception e) { Debug.LogError($"{Tag} Error closing {from}: {e.Message}"); }
            _writer = null;

            if (valid == null)
            {
                // Not classified (app quit, OnDestroy). Leave it in _incomplete where it belongs.
                Debug.LogWarning($"{Tag} Closed WITHOUT a verdict, left in _incomplete: {Path.GetFileName(from)} ({rows} rows)");
                return;
            }

            string destDir = Path.Combine(TrialsDir, valid.Value ? "valid" : "invalid");
            try
            {
                Directory.CreateDirectory(destDir);
                string dest = Path.Combine(destDir, Path.GetFileName(from));
                if (File.Exists(dest)) File.Delete(dest);   // only possible if we just wrote it; the name carries a timestamp
                File.Move(from, dest);
                CurrentPath = dest;
                Debug.Log($"{Tag} Filed as {(valid.Value ? "VALID" : "INVALID")}: {dest} ({rows} rows, {seconds:F1}s)");
            }
            catch (Exception e)
            {
                Debug.LogError($"{Tag} Could not file {from}: {e.Message}. The data is safe where it is.");
            }

            AppendIndex(valid.Value, note, rows, seconds);
        }

        /// <summary>One line per trial in a session-wide index, so the run is readable without opening 40
        /// CSVs: who, which condition, which trial, valid or not, why not, how long, where the file went.
        /// This is the thing you will actually want three months later.</summary>
        void AppendIndex(bool valid, string note, int rows, float seconds)
        {
            try
            {
                string path = Path.Combine(TrialsDir, "trials_index.csv");
                bool exists = File.Exists(path);
                using (var w = new StreamWriter(path, true, Encoding.UTF8))
                {
                    if (!exists)
                        w.WriteLine("started_utc,ended_utc,participant,condition,trial,redo,valid,note,rows,duration_s,file");
                    w.WriteLine(string.Join(",", new[]
                    {
                        _startedUtc.ToString("yyyy-MM-dd HH:mm:ss", Inv),
                        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", Inv),
                        Esc(TrialData.ParticipantId),
                        Esc(TrialData.Condition),
                        TrialData.TrialNumber.ToString(Inv),
                        Bit(TrialData.RedoPending),
                        Bit(valid),
                        Esc(note),
                        rows.ToString(Inv),
                        seconds.ToString("F1", Inv),
                        Esc((valid ? "valid/" : "invalid/") + Path.GetFileName(CurrentPath))
                    }));
                }
            }
            catch (Exception e) { Debug.LogWarning($"{Tag} Could not append the trial index: {e.Message}"); }
        }

        /// <summary>Stamp the next row with an event string (TRIAL_START, TRACKING_LOST, ...). The marker
        /// rides an ordinary data row rather than a line of its own, so the marker is anchored to the exact
        /// sample it happened on and nothing has to be interpolated afterward.</summary>
        public void Mark(string marker)
        {
            _pendingMarker = marker;
            Debug.Log($"{Tag} MARKER: {marker}");
        }

        // ---- The row -----------------------------------------------------------------------------------

        /// <summary>Called once per frame by TrialController while Recording. Paused writes nothing.</summary>
        public void WriteRow()
        {
            if (_writer == null) return;

            float t = (float)(Time.realtimeSinceStartupAsDouble - _startTime);
            TrialData.ElapsedSeconds = t;

            // The XR rig's camera may not exist yet at Awake (additive scene loading). Without this retry the
            // head pose would be all zeros for the whole session and nobody would notice until analysis.
            if (head == null && Camera.main != null) head = Camera.main.transform;

            Vector3 hp = head != null ? head.position : Vector3.zero;
            Vector3 he = head != null ? head.rotation.eulerAngles : Vector3.zero;

            _row.Clear();
            A(F(t, 4));
            A(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(Inv));
            A(Esc(_pendingMarker));
            _pendingMarker = "";

            A(F(hp.x, 4)); A(F(hp.y, 4)); A(F(hp.z, 4));
            A(F(he.x, 2)); A(F(he.y, 2)); A(F(he.z, 2));

            A(Bit(EyeData.Tracking));
            A(Bit(EyeData.GazeValid));
            A(EyeData.GazeConfidence.ToString(Inv));

            A(F(EyeData.GazeOrigin.x, 4)); A(F(EyeData.GazeOrigin.y, 4)); A(F(EyeData.GazeOrigin.z, 4));
            A(F(EyeData.GazeDirection.x, 4)); A(F(EyeData.GazeDirection.y, 4)); A(F(EyeData.GazeDirection.z, 4));

            A(Bit(EyeData.FixationValid));
            A(EyeData.FixationValid ? F(EyeData.FixationPoint.x, 4) : "");
            A(EyeData.FixationValid ? F(EyeData.FixationPoint.y, 4) : "");
            A(EyeData.FixationValid ? F(EyeData.FixationPoint.z, 4) : "");

            A(EyeData.LeftGeometryValid ? F(EyeData.LeftOpenness, 3) : "");
            A(EyeData.RightGeometryValid ? F(EyeData.RightOpenness, 3) : "");

            // Meters on the wire, millimeters in the file: pupillometry is published in mm, and a column of
            // 0.0034 invites somebody to "fix" it later with a factor nobody wrote down.
            A(EyeData.LeftPupilValid ? F(EyeData.LeftPupilDiameter * 1000f, 3) : "");
            A(EyeData.RightPupilValid ? F(EyeData.RightPupilDiameter * 1000f, 3) : "");

            A(EyeData.BehaviorValid ? Esc(EyeData.Behavior) : "");
            A(EyeData.BehaviorValid ? F(EyeData.BehaviorAmplitude, 3) : "");
            // The device reports velocity as NaN during a fixation. An empty cell is the truth; a zero is a
            // lie that would quietly drag down any mean saccade velocity computed later.
            A(EyeData.BehaviorValid ? F(EyeData.BehaviorVelocity, 2) : "");
            A(EyeData.BehaviorValid ? F(EyeData.BehaviorDurationNs / 1e6f, 2) : "");

            A(Esc(AoiData.Current));
            A(F(AoiData.DwellSeconds, 3));
            A(string.IsNullOrEmpty(AoiData.Current) ? "" : F(AoiData.HitDistance, 3), last: true);

            try
            {
                _writer.WriteLine(_row.ToString());
                TrialData.RowsWritten++;

                if (++_rowsSinceFlush >= Mathf.Max(1, FlushEveryRows))
                {
                    _writer.Flush();
                    _rowsSinceFlush = 0;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"{Tag} Write failed, closing the file to save what we have: {e.Message}");
                Close();
            }
        }

        void A(string value, bool last = false)
        {
            _row.Append(value);
            if (!last) _row.Append(',');
        }

        // NaN would be written literally and break a naive parser. Empty is honest: the value was not
        // measured. Never substitute a zero for a missing measurement.
        static string F(float v, int decimals)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "";
            return v.ToString("F" + decimals, Inv);
        }

        static string Bit(bool v) => v ? "1" : "0";

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.IndexOf(',') >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        // A crash or a quit must not cost the buffer.
        void OnApplicationPause(bool paused) { if (paused && _writer != null) { try { _writer.Flush(); } catch { } } }
        void OnApplicationQuit() => Close();
        void OnDestroy() => Close();
    }
}
