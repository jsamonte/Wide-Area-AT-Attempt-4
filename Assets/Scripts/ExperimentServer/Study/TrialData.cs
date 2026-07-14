namespace ARCockpit.Study
{
    /// <summary>Where the trial machinery is right now. Idle is the ONLY state in which a trial reset is
    /// allowed to fire: repositioning the aircraft mid-trial would ruin the run and the data with it.</summary>
    public enum TrialState
    {
        Idle,
        Recording,
        Paused
    }

    /// <summary>
    /// Current trial bookkeeping. Same one-writer static-holder contract as FlightData / EyeData / AoiData:
    /// the TrialController (next drop) is the only writer, the logger and the web dashboard read.
    ///
    /// It exists NOW, ahead of the controller, so the dashboard's JSON shape is settled and the controller
    /// slots in without reshaping the client. Until the controller lands these hold their defaults and the
    /// dashboard honestly reports "Idle, nothing recorded".
    /// </summary>
    public static class TrialData
    {
        public static TrialState State = TrialState.Idle;

        /// <summary>Set once per participant, before the first trial. Locked into every file name.</summary>
        public static string ParticipantId = "";

        /// <summary>The condition string, e.g. "physical" or "physical_ar". Also part of the file name.</summary>
        public static string Condition = "";

        /// <summary>1-based. Incremented on a successful Stop.</summary>
        public static int TrialNumber = 1;

        /// <summary>True when the NEXT trial is a redo of a spoiled one, which flags its file name rather
        /// than overwriting the spoiled file. Nothing is ever silently replaced.</summary>
        public static bool RedoPending;

        /// <summary>File name of the trial currently open, empty when Idle. Shown on the dashboard so the
        /// researcher can see the data is landing where they think it is.</summary>
        public static string CurrentFile = "";

        /// <summary>Seconds the current trial has been recording (excludes paused time).</summary>
        public static float ElapsedSeconds;

        /// <summary>Rows written to the current file. The dashboard's proof that data is actually flowing:
        /// a stream that is present but frozen is the failure mode this number exists to expose.</summary>
        public static int RowsWritten;

        /// <summary>Mirrors TrialController.useTrialReset, so the dashboard knows whether the captured start
        /// pose actually matters. A readiness light that is red for something you deliberately switched off
        /// is worse than no light: it teaches you to ignore the panel.</summary>
        public static bool UseTrialReset = true;

        /// <summary>Whether the participant + condition have been entered. Start is blocked until they are,
        /// because a file named "_trial_1.csv" with no participant is unrecoverable after the fact.</summary>
        public static bool Identified => !string.IsNullOrEmpty(ParticipantId) && !string.IsNullOrEmpty(Condition);

        // ---- Operator feedback -------------------------------------------------------------------------
        //
        // WHY THIS EXISTS. Every guard in TrialController used to refuse a command with a Debug.LogError and
        // nothing else. That message went to logcat, on a headset with no PC attached to it on run day, while
        // the dashboard cheerfully printed "Sent /api/trial/start" because a 202 only means the socket
        // accepted the bytes. So the operator saw a confirmation, watched nothing happen, and had no way to
        // find out why. The refusal reasons were already good ("the eye tracker has never reported high
        // confidence", "useTrialReset is ON but there is no TrialReset in any loaded scene"); they were simply
        // written somewhere nobody on run day could read.
        //
        // This is the channel that puts them on the page. The controller reports the outcome of EVERY command,
        // accepted or refused, and the dashboard renders the latest one. It is deliberately a single latest
        // message rather than a list: the operator needs to know what just happened to the button they pressed,
        // and a scrolling history is what the DEV log pane is for.

        /// <summary>The outcome of the most recent command: what happened, or why it was refused.</summary>
        public static string LastMessage = "";

        /// <summary>"ok", "warn" or "error". Drives the color on the dashboard.</summary>
        public static string LastMessageLevel = "ok";

        /// <summary>Time.time when LastMessage was set, so the dashboard can age it out. A stale reason
        /// sitting under a button you pressed ten minutes ago reads as a fresh refusal.</summary>
        public static float LastMessageAt = -999f;

        /// <summary>Called by TrialController for every command outcome. Logs AND surfaces, so there is one
        /// call rather than two that can drift out of step (the classic way a refusal ends up logged but not
        /// shown, or shown but not logged).</summary>
        public static void Report(string message, string level = "ok")
        {
            LastMessage = message;
            LastMessageLevel = level;
            LastMessageAt = UnityEngine.Time.time;
        }
    }
}
