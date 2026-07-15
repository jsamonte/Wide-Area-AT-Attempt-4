using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ARCockpit.Study
{
    /// <summary>What has happened to one trial slot.</summary>
    public enum SlotStatus
    {
        Pending,    // not flown yet
        Running,    // recording right now
        Valid,      // flown and filed as good
        Invalid,    // flown and thrown away; it goes back to Pending but the attempt is remembered
    }

    /// <summary>
    /// One trial in a participant's session: which condition, which weather, where it sits in the order,
    /// and what happened to it. Serializable because the whole plan is persisted as JSON.
    /// </summary>
    [Serializable]
    public class TrialSlot
    {
        public int index;          // 0-based position in the whole session, practice included
        public int block;          // 1-based condition block; 0 for practice
        public int trialInBlock;   // 1-based within the block; 0 for practice
        public int number;         // 1-based RECORDED trial number (practice slots are 0). This is the "trial 6 of 20".
        public bool practice;

        public bool arOn;
        public string weather = "";   // "clear" / "rain", or "" when weather is not a factor

        public string status = nameof(SlotStatus.Pending);
        public string file = "";      // the CSV this slot produced (the last attempt)
        public string startedAt = "";
        public int attempts;          // how many times it has been flown, including the throwaways

        public SlotStatus Status
        {
            get => Enum.TryParse(status, out SlotStatus s) ? s : SlotStatus.Pending;
            set => status = value.ToString();
        }

        /// <summary>The condition code that goes in the file name and the CSV, e.g. "AR_RAIN" or "NOAR".
        /// Stable and machine-readable: analysis groups by this.</summary>
        public string Code =>
            (arOn ? "AR" : "NOAR") + (string.IsNullOrEmpty(weather) ? "" : "_" + weather.ToUpperInvariant());

        /// <summary>What the headset panel says out loud.</summary>
        public string Label =>
            (arOn ? "AR ON" : "AR OFF") +
            (string.IsNullOrEmpty(weather) ? "" : "  +  " + (weather == "rain" ? "HEAVY RAIN, NIGHT" : "CLEAR NIGHT"));
    }

    /// <summary>
    /// THE session: a participant's whole ordered list of trials, DERIVED from their participant number
    /// rather than typed, and persisted so it survives an app restart.
    ///
    /// WHY THIS EXISTS. Participant, condition and trial number used to be three hand-typed fields on the
    /// dashboard, checked against nothing. A typo produced a perfect-looking CSV with the wrong label on it,
    /// and a wrong label is a wrong result. On top of that, TrialData is static in-memory state, so an app
    /// restart mid-session silently dropped the trial counter back to 1 and wrote a participant's sixth
    /// trial as their first. Both failures are silent, which makes them the expensive kind.
    ///
    /// Now exactly ONE thing is typed (the participant ID, once). The counterbalance group falls out of the
    /// participant number, the group gives the block order, the blocks give the slots, and the slot gives
    /// the condition, the weather, and the number. The dashboard ARMS a slot; the headset flies it; the
    /// outcome is written back onto it. See TRIAL_FLOW_ARCHITECTURE.md.
    ///
    /// The design is a balanced Latin square across the 2x2 (AR x weather), or a simple AB/BA cross when
    /// weather is not a factor. Trials per block and practice count are StudyProfile values, NOT constants:
    /// the full 2x2 at 5 trials a block is 20 recorded flights per participant, which is more than a person
    /// will sit through, and shrinking it must be a number change rather than a rewrite.
    /// </summary>
    [Serializable]
    public class SessionPlan
    {
        public string participant = "";
        public int participantNumber;
        public int group;                       // 1..4 (or 1..2 without weather)
        public bool weatherIsAFactor;
        public int trialsPerBlock;
        public int practiceTrials;
        public List<TrialSlot> slots = new List<TrialSlot>();
        public int armedIndex = -1;             // -1 = nothing armed

        const string Tag = "[SESSION]";

        // ---- The one live plan ---------------------------------------------------------------------------

        public static SessionPlan Active { get; private set; }

        /// <summary>Fires whenever the plan changes (armed, completed, rebuilt) so the dashboard and the
        /// headset panel repaint without polling the disk.</summary>
        public static event Action OnChanged;

        static void Changed()
        {
            Active?.Save();
            OnChanged?.Invoke();
        }

        // ---- The counterbalance ------------------------------------------------------------------------
        //
        // Four conditions, four sequences: a balanced Latin square, so every condition appears in every
        // block position exactly once across the four groups, and every condition follows every other
        // exactly once. This is the table from the study design doc, transcribed literally.
        //
        //   A1 = AR + clear, A2 = AR + rain, B1 = no AR + clear, B2 = no AR + rain
        static readonly string[][] Square4 =
        {
            new[] { "A1", "A2", "B2", "B1" },
            new[] { "A2", "B1", "A1", "B2" },
            new[] { "B2", "A1", "B1", "A2" },
            new[] { "B1", "B2", "A2", "A1" },
        };

        // Weather held constant: a plain AB / BA cross.
        static readonly string[][] Square2 =
        {
            new[] { "A", "B" },
            new[] { "B", "A" },
        };

        // ---- Build -------------------------------------------------------------------------------------

        /// <summary>Load this participant's plan from disk, or build a fresh one. The ONLY way a plan is
        /// made: nothing else constructs slots.</summary>
        public static SessionPlan LoadOrCreate(string participant)
        {
            participant = Sanitize(participant);
            if (string.IsNullOrEmpty(participant))
            {
                Active = null;
                return null;
            }

            SessionPlan loaded = Load(participant);
            if (loaded != null && loaded.StillMatchesProfile())
            {
                Active = loaded;
                Debug.Log($"{Tag} Loaded {participant}: group {loaded.group}, " +
                          $"{loaded.CompletedCount}/{loaded.RecordedCount} trials done.");
                Active.ArmNextIfNothingArmed();
                return Active;
            }

            if (loaded != null)
            {
                // The profile changed under an existing participant (someone retuned trialsPerBlock). Rebuilding
                // would renumber trials that have already been flown, so the old plan is kept and the change is
                // shouted about instead of silently reshaping the data.
                Debug.LogWarning($"{Tag} {participant}'s saved plan does not match the current StudyProfile " +
                                 $"(trialsPerBlock / practiceTrials / weatherIsAFactor changed). KEEPING the saved " +
                                 $"plan: rebuilding it would renumber trials that are already flown. Finish this " +
                                 $"participant on their original plan, or delete their session file to start over.");
                Active = loaded;
                Active.ArmNextIfNothingArmed();
                return Active;
            }

            Active = Build(participant);
            Active.Save();
            Debug.Log($"{Tag} New plan for {participant}: group {Active.group}, {Active.RecordedCount} recorded " +
                      $"trials in {Active.BlockCount} blocks, plus {Active.practiceTrials} practice.");
            Active.ArmNextIfNothingArmed();
            OnChanged?.Invoke();
            return Active;
        }

        static SessionPlan Build(string participant)
        {
            StudyProfile p = StudyProfile.Active;

            var plan = new SessionPlan
            {
                participant = participant,
                participantNumber = NumberOf(participant),
                weatherIsAFactor = p.weatherIsAFactor,
                trialsPerBlock = Mathf.Max(1, p.trialsPerBlock),
                practiceTrials = Mathf.Max(0, p.practiceTrials),
            };

            string[][] square = plan.weatherIsAFactor ? Square4 : Square2;
            // Participant 1 is group 1. An unnumbered ID (someone typed "pilotBob") falls to group 1 rather
            // than crashing, and the log says so, because refusing to run a participant on run day over a
            // naming convention is worse than running them in a known group.
            int n = plan.participantNumber;
            if (n <= 0)
            {
                Debug.LogWarning($"{Tag} '{participant}' has no number in it, so the counterbalance group cannot " +
                                 $"be derived. Falling back to group 1. Name participants P01, P02, ... so the " +
                                 $"group is derived rather than guessed.");
                n = 1;
            }
            plan.group = ((n - 1) % square.Length) + 1;
            string[] order = square[plan.group - 1];

            int index = 0;
            int number = 0;

            // Practice first, and never counted. Its condition is the participant's FIRST block, so the
            // practice run rehearses the thing they are about to do rather than something else.
            for (int i = 0; i < plan.practiceTrials; i++)
            {
                var slot = FromCode(order[0], plan.weatherIsAFactor);
                slot.index = index++;
                slot.practice = true;
                slot.block = 0;
                slot.trialInBlock = 0;
                slot.number = 0;
                plan.slots.Add(slot);
            }

            for (int b = 0; b < order.Length; b++)
            {
                for (int t = 0; t < plan.trialsPerBlock; t++)
                {
                    var slot = FromCode(order[b], plan.weatherIsAFactor);
                    slot.index = index++;
                    slot.practice = false;
                    slot.block = b + 1;
                    slot.trialInBlock = t + 1;
                    slot.number = ++number;
                    plan.slots.Add(slot);
                }
            }

            return plan;
        }

        static TrialSlot FromCode(string code, bool weatherIsAFactor)
        {
            // A* = AR on, B* = AR off. The trailing 1 = clear, 2 = rain (ignored when weather is not a factor).
            bool ar = code.StartsWith("A", StringComparison.Ordinal);
            string weather = "";
            if (weatherIsAFactor) weather = code.EndsWith("2", StringComparison.Ordinal) ? "rain" : "clear";
            return new TrialSlot { arOn = ar, weather = weather };
        }

        bool StillMatchesProfile()
        {
            StudyProfile p = StudyProfile.Active;
            return weatherIsAFactor == p.weatherIsAFactor
                && trialsPerBlock == Mathf.Max(1, p.trialsPerBlock)
                && practiceTrials == Mathf.Max(0, p.practiceTrials);
        }

        // ---- Query -------------------------------------------------------------------------------------

        public int BlockCount => weatherIsAFactor ? 4 : 2;
        public int RecordedCount => slots.FindAll(s => !s.practice).Count;
        public int CompletedCount => slots.FindAll(s => !s.practice && s.Status == SlotStatus.Valid).Count;

        public TrialSlot Armed => (armedIndex >= 0 && armedIndex < slots.Count) ? slots[armedIndex] : null;

        public TrialSlot NextPending()
        {
            foreach (TrialSlot s in slots)
                if (s.Status == SlotStatus.Pending) return s;
            return null;
        }

        // ---- Commands ----------------------------------------------------------------------------------

        /// <summary>Arm a slot: it becomes the trial the headset will fly next. Refused for a slot that is
        /// already flown and valid, because re-flying it would silently produce two files claiming to be the
        /// same trial. (Redo a bad trial instead: it is Invalid, so it is Pending again.)</summary>
        public bool Arm(int index, out string why)
        {
            if (index < 0 || index >= slots.Count) { why = $"There is no slot {index}."; return false; }

            TrialSlot s = slots[index];
            if (s.Status == SlotStatus.Valid)
            {
                why = $"Trial {s.number} is already done and filed as valid. Arming it again would write a " +
                      "second file claiming to be the same trial. Clear it first if you really mean to re-fly it.";
                return false;
            }

            armedIndex = index;
            why = "";
            Changed();
            return true;
        }

        /// <summary>Arm the first pending slot. The normal path: a valid stop calls this, so the investigator
        /// clicks nothing between trials WITHIN a block. A block boundary is deliberately not crossed
        /// automatically: that is where the washout break goes.</summary>
        public bool ArmNextInBlock()
        {
            TrialSlot done = Armed;
            TrialSlot next = NextPending();
            if (next == null) { armedIndex = -1; Changed(); return false; }

            // Do not step over a block boundary on our own. A human decides when the break is over.
            if (done != null && !done.practice && !next.practice && next.block != done.block)
            {
                armedIndex = -1;
                Changed();
                Debug.Log($"{Tag} Block {done.block} complete. The next block is NOT armed automatically: " +
                          $"take the washout break, then arm trial {next.number} on the board.");
                return false;
            }

            armedIndex = next.index;
            Changed();
            return true;
        }

        void ArmNextIfNothingArmed()
        {
            if (Armed == null) { TrialSlot n = NextPending(); if (n != null) armedIndex = n.index; }
        }

        /// <summary>The armed slot is now recording.</summary>
        public void MarkRunning(string file)
        {
            TrialSlot s = Armed;
            if (s == null) return;
            s.Status = SlotStatus.Running;
            s.file = file;
            s.startedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            s.attempts++;
            Changed();
        }

        /// <summary>The armed slot finished. Valid files it and moves on; invalid puts it back to Pending so
        /// it still has to be flown, while remembering that an attempt happened.</summary>
        public void MarkComplete(bool valid)
        {
            TrialSlot s = Armed;
            if (s == null) return;

            s.Status = valid ? SlotStatus.Valid : SlotStatus.Invalid;
            Changed();

            if (valid) ArmNextInBlock();
            else
            {
                // An invalid trial is still owed. It goes back to Pending and stays armed, so pressing BEGIN
                // again simply re-flies it. attempts remembers that it was thrown away once.
                s.Status = SlotStatus.Pending;
                Changed();
            }
        }

        /// <summary>Force a slot back to Pending. The escape hatch for "we have to re-fly trial 4".</summary>
        public void Clear(int index)
        {
            if (index < 0 || index >= slots.Count) return;
            slots[index].Status = SlotStatus.Pending;
            Changed();
        }

        // ---- Persistence -------------------------------------------------------------------------------
        //
        // On disk per participant, rewritten on every status change. This is what makes an app restart
        // mid-session survivable: the plan, not the in-memory counter, is the source of truth.

        public static string Dir => Path.Combine(Application.persistentDataPath, "sessions");
        static string PathFor(string participant) => Path.Combine(Dir, participant + ".json");

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(PathFor(participant), JsonUtility.ToJson(this, true));
            }
            catch (Exception e)
            {
                // Not fatal: the trial can still be flown and the CSV still lands. But the plan will not
                // survive a restart, and that is exactly the failure this class exists to prevent, so it is
                // an error and not a warning.
                Debug.LogError($"{Tag} Could not save the session plan for {participant}: {e.Message}. The trial " +
                               $"will still record, but an app restart will lose which trials are done.");
            }
        }

        static SessionPlan Load(string participant)
        {
            try
            {
                string path = PathFor(participant);
                if (!File.Exists(path)) return null;
                var plan = JsonUtility.FromJson<SessionPlan>(File.ReadAllText(path));
                if (plan == null || plan.slots == null || plan.slots.Count == 0) return null;

                // A slot left Running is a crash mid-trial: the app died with the file open. It is not Valid
                // and it is not a deliberate throwaway, so it goes back to Pending and gets flown again.
                foreach (TrialSlot s in plan.slots)
                    if (s.Status == SlotStatus.Running) s.Status = SlotStatus.Pending;

                return plan;
            }
            catch (Exception e)
            {
                Debug.LogError($"{Tag} Could not read the session plan for {participant}: {e.Message}");
                return null;
            }
        }

        // ---- Naming ------------------------------------------------------------------------------------

        /// <summary>Pull the number out of a participant ID: "P07" -> 7, "p7" -> 7, "pilot12" -> 12. 0 when
        /// there is no number in it at all.</summary>
        public static int NumberOf(string participant)
        {
            Match m = Regex.Match(participant ?? "", @"\d+");
            return m.Success && int.TryParse(m.Value, out int n) ? n : 0;
        }

        public static string Sanitize(string s) => Regex.Replace((s ?? "").Trim(), @"[^A-Za-z0-9_\-]", "_");
    }
}
