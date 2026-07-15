namespace TrialServer
{
    /// <summary>Where the wrapped study is in its flow, as the server understands it. The bridge OWNS this:
    /// it drives the flow by clicking the study's buttons, so it always knows which phase it just moved into.
    /// It is not read back out of SequenceManager (that state is private), it is tracked forward.</summary>
    public enum TrialPhase
    {
        Menu,        // nothing selected yet; the operator picks one of the four sequences
        Tutorial,    // the four-gem cross is up; no numbered trial is recording
        Ready,       // a trial is queued and waiting for the researcher to approve/start it
        Recording,   // a numbered trial is live
        Done         // the sequence's four trials are finished
    }

    /// <summary>
    /// The one main-thread to HTTP-thread hand-off for the trial server, plus the operator-feedback channel.
    /// Same one-writer static-holder contract the flight project used: the SequenceBridge is the only writer,
    /// StatusSnapshot (on the main thread) reads, and the finished JSON string is what crosses to the
    /// listener thread. Nothing here is a second copy of the study's data: SequenceManager stays the trial
    /// driver and EyeAndHeadTracker stays the single data source. These fields are just what the server
    /// COMMANDED plus what it needs to display, held in one place so the snapshot has one thing to read.
    /// </summary>
    public static class ServerState
    {
        /// <summary>1-4 once a sequence is picked, 0 while still on the menu.</summary>
        public static int SelectedSequence;

        /// <summary>1-based trial number the study is on (queued or recording). 0 before the first trial.</summary>
        public static int TrialNumber;

        /// <summary>Total numbered trials in a sequence. His study is a fixed four.</summary>
        public static int TotalTrials = 4;

        /// <summary>Pool 1-4 for the current/queued trial, 0 when unknown (reflection into his private table
        /// failed, or no trial is queued). Populated by the bridge via guarded reflection.</summary>
        public static int Pool;

        /// <summary>Whether the current/queued trial has the wireframe overlay on. From his private table.</summary>
        public static bool Wireframe;

        /// <summary>"Dusk" or "Night" for the current/queued trial (trials 0-1 Dusk, 2-3 Night), or "" if
        /// unknown.</summary>
        public static string TimeOfDay = "";

        /// <summary>Where the flow is. The bridge sets this as it drives the study.</summary>
        public static TrialPhase Phase = TrialPhase.Menu;

        // ---- Operator feedback -------------------------------------------------------------------------
        //
        // WHY THIS EXISTS. A 202 from the socket only means "the bytes were accepted", not "the trial
        // machinery ran". A command can be refused on the main thread (no SequenceManager in the scene, the
        // sequence is already done, the wrong phase) and the operator, watching a phone with no PC attached,
        // would otherwise see a confirmation and nothing happen. This channel reports the OUTCOME of every
        // command, accepted or refused, and the dashboard renders the latest one under the buttons.

        /// <summary>The outcome of the most recent command: what happened, or why it was refused.</summary>
        public static string LastMessage = "";

        /// <summary>"ok", "warn" or "error". Drives the color on the dashboard.</summary>
        public static string LastMessageLevel = "ok";

        /// <summary>Time.time when LastMessage was set, so the dashboard can age it out. A stale reason
        /// sitting under a button you pressed ten minutes ago reads as a fresh refusal.</summary>
        public static float LastMessageAt = -999f;

        /// <summary>Report a command outcome. One call so the log line and the on-page message cannot drift
        /// out of step. Called on the main thread by the bridge.</summary>
        public static void Report(string message, string level = "ok")
        {
            LastMessage = message;
            LastMessageLevel = level;
            LastMessageAt = UnityEngine.Time.time;
        }
    }
}
