using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace TrialServer
{
    /// <summary>
    /// Builds the dashboard's JSON status blob. Called ONLY from the main thread (by
    /// <see cref="ExperimentServer"/> in Update); the server hands the finished string to its background
    /// listener thread. That is the whole reason this is a separate class: every Unity API call, every
    /// singleton read, happens here, on the main thread, and the HTTP thread never touches Unity at all.
    /// Reading EyeAndHeadTracker or GazeInputManager from the listener thread would be a hard crash.
    ///
    /// The status blob is a read-only OBSERVATION. Nothing in this file writes to the study, the trial, or
    /// the logger. A stalled or slow dashboard therefore cannot affect the recorded data. The trial fields
    /// come from ServerState (what the server commanded); the live fields come straight off the study's
    /// EyeAndHeadTracker singleton, which stays the single source of truth for the data.
    /// </summary>
    public static class StatusSnapshot
    {
        static readonly StringBuilder Sb = new StringBuilder(1024);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Build(string ip, int port)
        {
            // Readiness: can the study actually collect eye data right now? All three are the study's own
            // components, read on the main thread. GazeInputManager and EyeAndHeadTracker are singletons; a
            // null means the object is not in the scene, which is itself the thing to see before a trial.
            var gaze = GazeInputManager.Instance;
            bool gazePresent = gaze != null;
            bool eyePermission = gazePresent && gaze.EyeTrackingPermissionGranted;

            var tracker = EyeAndHeadTracker.Instance;
            bool trackerPresent = tracker != null;

            // Head tracking. Outdoors against a featureless sky the ML2 loses the head pose mid-trial, which
            // quietly corrupts every head-relative number while the app itself looks fine. Tri-state on
            // purpose: true/false when an HMD is present, null when there is NO head device at all (the Editor,
            // the App Simulator), so the dashboard omits the chip off-device instead of crying "tracking lost"
            // on every desk run. Read here on the main thread, like every other Unity call in this file.
            bool? headTracked = null;
            InputDevice headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (headDevice.isValid && headDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool isHeadTracked))
                headTracked = isHeadTracked;

            // Live trial readout, straight off the study's tracker. GetRemainingTargetPositions allocates a list, but
            // this runs at the snapshot rate (a few Hz), not per frame, so it is not on any hot path. When the
            // tracker is absent these stay at rest values rather than throwing.
            int targetsRemaining = trackerPresent ? tracker.GetRemainingTargetPositions().Count : 0;
            int destroyed = trackerPresent ? tracker.GetTargetsDestroyed() : 0;
            float trialTime = trackerPresent ? tracker.GetCurrentTrialTime() : 0f;

            Sb.Clear();
            Sb.Append('{');

            Sb.Append("\"time\":").Append(F(Time.time));

            Sb.Append(",\"server\":{")
              .Append("\"ip\":\"").Append(Esc(ip)).Append("\",\"port\":").Append(port)
              .Append('}');

            // Everything green here means a trial can be recorded. Kept deliberately small: these are the
            // failures that otherwise look fine (no eye permission produces a healthy-looking but worthless
            // file; a missing tracker means nothing is being written at all).
            Sb.Append(",\"ready\":{")
              .Append("\"eyePermission\":").Append(B(eyePermission))
              .Append(",\"gazeManager\":").Append(B(gazePresent))
              .Append(",\"tracker\":").Append(B(trackerPresent))
              .Append(",\"headTracked\":").Append(headTracked.HasValue ? B(headTracked.Value) : "null")
              .Append('}');

            // Headset vitals. Battery is the one that leaves every other light on the page green while it kills
            // a session: if the headset dies mid-trial the gaze file simply stops. SystemInfo.batteryLevel is
            // 0..1, or -1 where the platform will not report it; the dashboard says "not reported" rather than
            // a fake 0%.
            float battery = SystemInfo.batteryLevel;
            bool charging = SystemInfo.batteryStatus == BatteryStatus.Charging ||
                            SystemInfo.batteryStatus == BatteryStatus.Full;
            Sb.Append(",\"vitals\":{")
              .Append("\"batteryPct\":").Append(battery < 0f ? "null" : F(battery * 100f, 0))
              .Append(",\"charging\":").Append(B(charging))
              .Append('}');

            // CRIT log lines, at TOP level, because the run-day punch list is on the calm page and the calm
            // page never polls the log stream. A CRIT line means some subsystem already knows this session's
            // data is compromised (eye permission denied, storage about to run out); without this the operator
            // would only find out by opening DEV and reading the log.
            LogRing.CritSummary(out int critCount, out string latestCrit);
            Sb.Append(",\"crit\":{")
              .Append("\"count\":").Append(critCount)
              .Append(",\"latest\":\"").Append(Esc(latestCrit)).Append('"')
              .Append('}');

            Sb.Append(",\"trial\":{")
              .Append("\"sequence\":").Append(ServerState.SelectedSequence)
              .Append(",\"number\":").Append(ServerState.TrialNumber)
              .Append(",\"total\":").Append(ServerState.TotalTrials)
              .Append(",\"pool\":").Append(ServerState.Pool)
              .Append(",\"wireframe\":").Append(B(ServerState.Wireframe))
              .Append(",\"timeOfDay\":\"").Append(Esc(ServerState.TimeOfDay)).Append('"')
              .Append(",\"phase\":\"").Append(ServerState.Phase.ToString()).Append('"')
              .Append(",\"limit\":").Append(F(ServerState.TrialLimitSeconds, 0))
              .Append('}');

            // Runs the operator flagged invalid. The authoritative record is the TRIAL_INVALID marker in the
            // study's gaze JSON; this array only keeps the dashboard's list alive across a page reload.
            Sb.Append(",\"badTrials\":[");
            for (int i = 0; i < ServerState.BadTrials.Count; i++)
            {
                if (i > 0) Sb.Append(',');
                Sb.Append('"').Append(Esc(ServerState.BadTrials[i])).Append('"');
            }
            Sb.Append(']');

            // trialTime is the BRIDGE's clock (frozen across a pause), not the tracker's raw one: the raw
            // clock reads 0 while paused, which blanked the page's timer at exactly the moment the operator
            // is deciding whether to end the trial. `recording` stays the tracker's raw truth.
            Sb.Append(",\"live\":{")
              .Append("\"targetsRemaining\":").Append(targetsRemaining)
              .Append(",\"destroyed\":").Append(destroyed)
              .Append(",\"trialTime\":").Append(F(ServerState.TrialElapsed))
              .Append(",\"recording\":").Append(B(trialTime > 0f))
              .Append('}');

            // The outcome of the last command, accepted or refused. Age is sent rather than a timestamp so the
            // client does not have to reconcile two clocks: it just fades the message out once it is old.
            Sb.Append(",\"msg\":{")
              .Append("\"text\":\"").Append(Esc(ServerState.LastMessage)).Append('"')
              .Append(",\"level\":\"").Append(Esc(ServerState.LastMessageLevel)).Append('"')
              .Append(",\"age\":").Append(F(ServerState.LastMessageAt < -900f ? 9999f : Time.time - ServerState.LastMessageAt))
              .Append('}');

            Sb.Append(",\"logLines\":").Append(LogRing.Total);

            // The DEV block: everything the run-day dashboard does NOT need, and everything you would otherwise
            // open a terminal for. It is always in the payload (a few hundred bytes at a few Hz is nothing) and
            // the dashboard hides it until DEV is switched on. Strictly an OBSERVATION: nothing here writes.
            //
            // The performance block is nearly empty until the monitor is switched on from the dashboard: an OFF
            // monitor must not leave a full card of numbers from whenever it last ran, because a stale card
            // gets read as live and acted on. PerfMonitor writes its own JSON so the numbers and their
            // formatting stay next to the code that produced them.
            Sb.Append(",\"dev\":{\"perf\":{");
            PerfMonitor.AppendJson(Sb);
            Sb.Append("}}");

            Sb.Append('}');
            return Sb.ToString();
        }

        // NaN and Infinity are not legal JSON and will break the dashboard's parse outright. Emit null and let
        // the client show a blank rather than poisoning the whole payload.
        static string F(float v, int decimals = 2)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
            return v.ToString("F" + decimals, Inv);
        }

        static string B(bool v) => v ? "true" : "false";

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
        }
    }
}
