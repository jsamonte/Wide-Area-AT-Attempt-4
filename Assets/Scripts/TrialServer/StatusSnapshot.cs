using System.Globalization;
using System.Text;
using UnityEngine;

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
    /// come from ServerState (what the server commanded); the live fields come straight off his
    /// EyeAndHeadTracker singleton, which stays the single source of truth for the data.
    /// </summary>
    public static class StatusSnapshot
    {
        static readonly StringBuilder Sb = new StringBuilder(1024);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Build(string ip, int port)
        {
            // Readiness: can he actually collect eye data right now? All three are his own components, read on
            // the main thread. GazeInputManager and EyeAndHeadTracker are singletons; a null means the object
            // is not in the scene, which is itself the thing the operator needs to see before a trial.
            var gaze = GazeInputManager.Instance;
            bool gazePresent = gaze != null;
            bool eyePermission = gazePresent && gaze.EyeTrackingPermissionGranted;

            var tracker = EyeAndHeadTracker.Instance;
            bool trackerPresent = tracker != null;

            // Live trial readout, straight off his tracker. GetRemainingTargetPositions allocates a list, but
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
              .Append('}');

            Sb.Append(",\"trial\":{")
              .Append("\"sequence\":").Append(ServerState.SelectedSequence)
              .Append(",\"number\":").Append(ServerState.TrialNumber)
              .Append(",\"total\":").Append(ServerState.TotalTrials)
              .Append(",\"pool\":").Append(ServerState.Pool)
              .Append(",\"wireframe\":").Append(B(ServerState.Wireframe))
              .Append(",\"timeOfDay\":\"").Append(Esc(ServerState.TimeOfDay)).Append('"')
              .Append(",\"phase\":\"").Append(ServerState.Phase.ToString()).Append('"')
              .Append('}');

            Sb.Append(",\"live\":{")
              .Append("\"targetsRemaining\":").Append(targetsRemaining)
              .Append(",\"destroyed\":").Append(destroyed)
              .Append(",\"trialTime\":").Append(F(trialTime))
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
