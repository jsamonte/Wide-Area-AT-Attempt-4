using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using ARCockpit.Data;
using ARCockpit.DevSettings;
using ARCockpit.FlightPlan;

namespace ARCockpit.Study
{
    /// <summary>
    /// Builds the dashboard's JSON status blob. Called ONLY from the main thread (by
    /// <see cref="ExperimentServer"/> in Update); the server hands the finished string to its background
    /// listener thread. That is the whole reason this is a separate class: every Unity API call, every
    /// FindObjectOfType, every static-holder read happens here, on the main thread, and the HTTP thread
    /// never touches Unity at all. Reading FlightData from the listener thread would be a hard crash.
    ///
    /// The status blob is a read-only OBSERVATION. Nothing in this file writes to the sim, the trial, or
    /// the logger. A stalled or slow dashboard therefore cannot affect the recorded data.
    /// </summary>
    public static class StatusSnapshot
    {
        // Cached scene refs. Refreshed on a timer because elements are spawned by ArUco tags mid-session,
        // so a ref grabbed at startup would miss whatever appeared later.
        static XP12Receiver _receiver;
        static TrialReset _trialReset;
        static GuidanceCommandWriter _writer;
        static GazeTarget[] _gazeTargets = new GazeTarget[0];
        static float _refreshTimer;

        // Smoothed frame time for the dev view. The headset's frame rate is the first thing to look at when
        // "the map feels bad", and it is the one number you cannot get from a log line after the fact.
        static float _smoothedDt = 1f / 60f;

        // Sticky calibration evidence: gaze confidence dips to 0 on every blink, so "we have seen high
        // confidence at least once this session" is the honest signal, not the instantaneous value.
        static bool _sawHighConfidence;
        static bool _sawValidPupil;

        // ---- Vitals ----------------------------------------------------------------------------------------
        //
        // The headset's own health, which nothing else was watching. This matters most for the case this module
        // is being handed to a colleague for: an UNTETHERED, outdoor session, where there is no PC to check and
        // the researcher is not the one wearing the device. Two things kill a session there, both silently:
        //
        //   - The battery dies. The CSV simply stops, mid-trial, and the last file sits in _incomplete/.
        //   - Head tracking drops (featureless sky, direct sun, a blank wall). The AR elements swim or freeze
        //     and the participant sees garbage, while every OTHER light on the dashboard stays green, because
        //     eye tracking, the data link and the logger are all still perfectly healthy. There is no way to
        //     infer this from the numbers already on the page, which is precisely why it needs its own signal.
        //
        // Tracking is read through the stock XR input API (the center-eye node's isTracked), not a Magic Leap
        // one, so this compiles and behaves on any XR target the colleague might use. In the Editor with no XR
        // device there is no node at all, and _xrPresent stays false: the dashboard then says "no XR device"
        // rather than screaming that tracking is lost, because a false alarm is the alarm you learn to ignore.

        static bool _xrPresent;
        static bool _headTracked;
        static float _trackingLostFor;   // seconds of continuous loss, so a single dropped frame is not an alarm
        static float _vitalsTimer;

        static readonly List<InputDevice> XrDevices = new List<InputDevice>();

        static readonly StringBuilder Sb = new StringBuilder(2048);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Has the tracker EVER reported high gaze confidence this session? The TrialController
        /// gates recording on this, because uncalibrated eyes produce a healthy-looking, worthless file.</summary>
        public static bool SawHighConfidence => _sawHighConfidence;

        /// <summary>Head tracking has been lost for long enough to be real (half a second), on a headset that
        /// actually exists. The TrialController marks this into the running CSV so the spoiled segment is IN
        /// the data rather than only on a dashboard nobody was watching at the time.</summary>
        public static bool TrackingLost => _xrPresent && _trackingLostFor >= 0.5f;

        static int _lastRefreshFrame = -1;

        /// <summary>Safe to call from more than one place in a frame: it runs once. Both the server and the
        /// TrialController call it, because the sticky calibration evidence below is a RECORDING gate, not a
        /// dashboard nicety. If only the server refreshed it, turning the web server off would leave
        /// SawHighConfidence permanently false and block every trial with a calibration error that is not true.
        /// </summary>
        public static void Refresh(float deltaTime)
        {
            if (_lastRefreshFrame == Time.frameCount) return;
            _lastRefreshFrame = Time.frameCount;

            if (deltaTime > 0f) _smoothedDt = Mathf.Lerp(_smoothedDt, deltaTime, 0.1f);

            _refreshTimer -= deltaTime;
            if (_refreshTimer <= 0f)
            {
                _refreshTimer = 2f;
                if (_receiver == null) _receiver = Object.FindObjectOfType<XP12Receiver>();
                if (_trialReset == null) _trialReset = Object.FindObjectOfType<TrialReset>();
                // The writer is AddComponent-ed by FlightPlanSystem, so it may not exist on the first frames.
                if (_writer == null) _writer = Object.FindObjectOfType<GuidanceCommandWriter>();
                _gazeTargets = Object.FindObjectsOfType<GazeTarget>();
            }

            if (EyeData.Tracking && EyeData.GazeConfidence >= 2) _sawHighConfidence = true;
            if (EyeData.LeftPupilValid || EyeData.RightPupilValid) _sawValidPupil = true;

            RefreshVitals(deltaTime);
        }

        /// <summary>Head tracking, once every quarter second. Polling the XR device list every frame is
        /// pointless: tracking loss lasts seconds, not milliseconds, and this runs during a recording.</summary>
        static void RefreshVitals(float deltaTime)
        {
            _vitalsTimer -= deltaTime;
            if (_vitalsTimer > 0f) return;
            _vitalsTimer = 0.25f;

            InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.HeadMounted, XrDevices);
            _xrPresent = XrDevices.Count > 0;

            bool tracked = false;
            if (_xrPresent && XrDevices[0].TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked))
                tracked = isTracked;

            _headTracked = tracked;

            // Only accumulate loss when there IS a headset to lose tracking on. In the Editor, or on a device
            // where XR never came up at all, "not tracked" is not a fault to shout about: it is a different
            // situation entirely, and conflating them would put a red banner on every Editor test run.
            if (_xrPresent && !tracked) _trackingLostFor += 0.25f;
            else _trackingLostFor = 0f;
        }

        /// <summary>
        /// The session board: every slot in the participant's plan, with its condition and its status. This is
        /// what replaced the three typed fields (participant / condition / trial number) on the dashboard.
        /// The investigator now clicks a cell instead of typing a label, which is the difference between a
        /// mislabel being impossible and a mislabel being one keystroke away.
        /// </summary>
        static void AppendSession()
        {
            SessionPlan plan = SessionPlan.Active;
            if (plan == null) { Sb.Append(",\"session\":null"); return; }

            Sb.Append(",\"session\":{")
              .Append("\"participant\":\"").Append(Esc(plan.participant)).Append('"')
              .Append(",\"group\":").Append(plan.group)
              .Append(",\"weatherIsAFactor\":").Append(B(plan.weatherIsAFactor))
              .Append(",\"done\":").Append(plan.CompletedCount)
              .Append(",\"total\":").Append(plan.RecordedCount)
              .Append(",\"armed\":").Append(plan.armedIndex)
              .Append(",\"slots\":[");

            for (int i = 0; i < plan.slots.Count; i++)
            {
                TrialSlot s = plan.slots[i];
                if (i > 0) Sb.Append(',');
                Sb.Append("{\"index\":").Append(s.index)
                  .Append(",\"block\":").Append(s.block)
                  .Append(",\"number\":").Append(s.number)
                  .Append(",\"practice\":").Append(B(s.practice))
                  .Append(",\"arOn\":").Append(B(s.arOn))
                  .Append(",\"weather\":\"").Append(Esc(s.weather)).Append('"')
                  .Append(",\"code\":\"").Append(Esc(s.Code)).Append('"')
                  .Append(",\"status\":\"").Append(Esc(s.status)).Append('"')
                  .Append(",\"attempts\":").Append(s.attempts)
                  .Append(",\"file\":\"").Append(Esc(s.file)).Append('"')
                  .Append('}');
            }

            Sb.Append("]}");
        }

        public static string Build(string ip, int port)
        {
            bool xplane = _receiver != null && _receiver.Connected;
            bool posed = _trialReset != null && _trialReset.captured;

            // Which elements SHOULD the participant be able to see? Whatever GazeTargets exist in the scene
            // right now. Never a hardcoded list: the elements are spawned by tags and renamed as the study
            // evolves, and a hardcoded list would quietly report a missing element as "all good".
            var seen = new List<string>();
            var unseen = new List<string>();
            foreach (var t in _gazeTargets)
            {
                if (t == null || string.IsNullOrEmpty(t.aoiKey)) continue;
                if (AoiData.Confirmed.Contains(t.aoiKey)) { if (!seen.Contains(t.aoiKey)) seen.Add(t.aoiKey); }
                else if (!unseen.Contains(t.aoiKey)) unseen.Add(t.aoiKey);
            }

            // The calibration trap, surfaced. Uncalibrated eyes return a Success result code with confidence
            // 0 and invalid pupils, which looks exactly like healthy-but-idle. If the tracker is running and
            // we have NEVER seen high confidence, that is the single most likely explanation, and it is worth
            // shouting about because the resulting data is unrecoverable.
            bool calibrationSuspect = EyeData.Tracking && !_sawHighConfidence;

            Sb.Clear();
            Sb.Append('{');

            Sb.Append("\"time\":").Append(F(Time.time));
            Sb.Append(",\"ip\":\"").Append(Esc(ip)).Append("\",\"port\":").Append(port);

            Sb.Append(",\"trial\":{")
              .Append("\"state\":\"").Append(TrialData.State).Append('"')
              .Append(",\"participant\":\"").Append(Esc(TrialData.ParticipantId)).Append('"')
              .Append(",\"condition\":\"").Append(Esc(TrialData.Condition)).Append('"')
              .Append(",\"number\":").Append(TrialData.TrialNumber)
              .Append(",\"redoPending\":").Append(B(TrialData.RedoPending))
              .Append(",\"file\":\"").Append(Esc(TrialData.CurrentFile)).Append('"')
              .Append(",\"elapsed\":").Append(F(TrialData.ElapsedSeconds))
              .Append(",\"rows\":").Append(TrialData.RowsWritten)
              .Append(",\"identified\":").Append(B(TrialData.Identified))
              // The outcome of the last command, accepted or refused. The dashboard renders this under the
              // buttons. Age is sent rather than a timestamp so the client does not have to reconcile two
              // clocks: it just fades the message out once it is old.
              .Append(",\"message\":\"").Append(Esc(TrialData.LastMessage)).Append('"')
              .Append(",\"messageLevel\":\"").Append(Esc(TrialData.LastMessageLevel)).Append('"')
              .Append(",\"messageAge\":").Append(F(TrialData.LastMessageAt < -900f ? 9999f : Time.time - TrialData.LastMessageAt))
              .Append('}');

            AppendSession();

            // Headset vitals. See the block comment at the top of this file for why these two, and only these
            // two: they are the failures that leave every other light on the page green.
            float battery = SystemInfo.batteryLevel;   // 0..1, or -1 where the platform will not say
            Sb.Append(",\"vitals\":{")
              .Append("\"batteryPct\":").Append(battery < 0f ? "null" : F(battery * 100f, 0))
              .Append(",\"charging\":").Append(B(SystemInfo.batteryStatus == BatteryStatus.Charging ||
                                                 SystemInfo.batteryStatus == BatteryStatus.Full))
              .Append(",\"xrPresent\":").Append(B(_xrPresent))
              .Append(",\"headTracked\":").Append(B(_headTracked))
              // The SAME property the TrialController marks the CSV from, deliberately: if the banner and the
              // CSV marker could disagree about what "lost" means, the page would be telling you one thing and
              // the data another.
              .Append(",\"trackingLost\":").Append(B(TrackingLost))
              .Append(",\"trackingLostFor\":").Append(F(_trackingLostFor, 1))
              .Append('}');

            Sb.Append(",\"ready\":{")
              .Append("\"eyeTracking\":").Append(B(EyeData.Tracking))
              .Append(",\"eyePermission\":").Append(B(EyeData.PermissionGranted))
              .Append(",\"pupilPermission\":").Append(B(EyeData.PupilPermissionGranted))
              .Append(",\"sawHighConfidence\":").Append(B(_sawHighConfidence))
              .Append(",\"sawValidPupil\":").Append(B(_sawValidPupil))
              .Append(",\"calibrationSuspect\":").Append(B(calibrationSuspect))
              .Append(",\"xplane\":").Append(B(xplane))
              .Append(",\"guidance\":").Append(B(GuidanceData.Valid))
              .Append(",\"useTrialReset\":").Append(B(TrialData.UseTrialReset))
              .Append(",\"startPoseCaptured\":").Append(B(posed))
              .Append(",\"seen\":").Append(Arr(seen))
              .Append(",\"unseen\":").Append(Arr(unseen))
              .Append('}');

            Sb.Append(",\"eye\":{")
              .Append("\"gazeValid\":").Append(B(EyeData.GazeValid))
              .Append(",\"confidence\":").Append(EyeData.GazeConfidence)
              .Append(",\"leftOpenness\":").Append(F(EyeData.LeftOpenness))
              .Append(",\"rightOpenness\":").Append(F(EyeData.RightOpenness))
              // Meters on the device, millimeters for a human. The dashboard should never show 0.0034.
              .Append(",\"leftPupilMm\":").Append(EyeData.LeftPupilValid ? F(EyeData.LeftPupilDiameter * 1000f) : "null")
              .Append(",\"rightPupilMm\":").Append(EyeData.RightPupilValid ? F(EyeData.RightPupilDiameter * 1000f) : "null")
              .Append(",\"behavior\":\"").Append(Esc(EyeData.BehaviorValid ? EyeData.Behavior : "")).Append('"')
              .Append('}');

            Sb.Append(",\"aoi\":{")
              .Append("\"current\":\"").Append(Esc(AoiData.Current)).Append('"')
              .Append(",\"dwell\":").Append(F(AoiData.DwellSeconds))
              .Append(",\"totals\":").Append(DwellMap())
              .Append('}');

            Sb.Append(",\"flight\":{")
              .Append("\"connected\":").Append(B(xplane))
              .Append(",\"pitch\":").Append(F(FlightData.PitchDeg))
              .Append(",\"roll\":").Append(F(FlightData.RollDeg))
              .Append(",\"headingTrue\":").Append(F(FlightData.TrueHeadingDeg))
              .Append(",\"track\":").Append(F(FlightData.TrackDeg))
              .Append(",\"groundspeedKt\":").Append(F(FlightData.GroundspeedKt))
              .Append(",\"verticalSpeedFpm\":").Append(F(FlightData.VerticalSpeedFpm))
              .Append(",\"altitudeM\":").Append(F(FlightData.AltitudeMeters))
              .Append(",\"lat\":").Append(F(FlightData.LatitudeDeg, 5))
              .Append(",\"lon\":").Append(F(FlightData.LongitudeDeg, 5))
              .Append(",\"fdMode\":").Append(FlightData.FdMode)
              .Append(",\"hsiSource\":").Append(FlightData.HsiSourceSelect)
              .Append(",\"gpsNavId\":\"").Append(Esc(FlightData.GpsNavId)).Append('"')
              .Append('}');

            Sb.Append(",\"guidance\":{")
              .Append("\"valid\":").Append(B(GuidanceData.Valid))
              .Append(",\"crossTrackM\":").Append(F(GuidanceData.CrossTrackErrorMeters))
              .Append(",\"verticalDevM\":").Append(F(GuidanceData.VerticalDeviationMeters))
              .Append(",\"progress\":").Append(F(GuidanceData.TrackProgress01, 3))
              .Append(",\"commandedHeading\":").Append(F(GuidanceData.CommandedHeadingDeg))
              .Append(",\"commandedVsFpm\":").Append(F(GuidanceData.CommandedVerticalSpeedFpm))
              .Append('}');

            // The DEV block: everything the run-day dashboard does NOT need, and everything you would
            // otherwise open a terminal for. It is always in the payload (a few hundred bytes at 2 Hz is
            // nothing) and the dashboard simply hides it until DEV is switched on, so turning DEV on never
            // has to renegotiate anything with the headset. Strictly an OBSERVATION: nothing here writes.
            Sb.Append(",\"dev\":{");

            Sb.Append("\"fps\":").Append(F(_smoothedDt > 1e-5f ? 1f / _smoothedDt : 0f, 1));
            Sb.Append(",\"frameMs\":").Append(F(_smoothedDt * 1000f, 1));

            // The X-Plane link, as the receiver sees it. testMode is here because a test-mode receiver
            // produces a perfectly healthy-looking dashboard from slider values, which is exactly the kind of
            // thing that should be impossible to miss.
            Sb.Append(",\"xp\":{")
              .Append("\"connected\":").Append(B(xplane))
              .Append(",\"testMode\":").Append(B(_receiver != null && _receiver.testMode))
              .Append(",\"api\":\"").Append(Esc(_receiver != null ? _receiver.apiVersion : "")).Append('"')
              .Append(",\"url\":\"").Append(Esc(_receiver != null ? _receiver.ApiBaseUrl : "")).Append('"')
              .Append('}');

            // The AP write path. On run day the question is always the same ("is it actually commanding the
            // sim, and if not, why not"), and the writer already answers it precisely: BlockReason is the same
            // named gate the [FDCmd] log prints. Surfacing it here is what removes the logcat step.
            Sb.Append(",\"write\":{");
            if (_writer == null)
            {
                Sb.Append("\"present\":false");
            }
            else
            {
                Sb.Append("\"present\":true")
                  .Append(",\"armed\":").Append(B(_writer.Armed))
                  .Append(",\"writing\":").Append(B(_writer.Writing))
                  .Append(",\"blocked\":\"").Append(Esc(_writer.BlockReason ?? "")).Append('"')
                  .Append(",\"testSweep\":").Append(B(_writer.TestSweep))
                  .Append(",\"writeHeading\":").Append(B(_writer.WriteHeading))
                  .Append(",\"writeVs\":").Append(B(_writer.WriteVerticalSpeed))
                  .Append(",\"magCmd\":").Append(F(_writer.LastMagCommandDeg))
                  .Append(",\"trueCmd\":").Append(F(_writer.LastTrueCommandDeg))
                  .Append(",\"vsCmd\":").Append(F(_writer.LastVsCommandFpm))
                  .Append(",\"headingId\":").Append(_writer.HeadingDatarefId)
                  .Append(",\"vsId\":").Append(_writer.VsDatarefId);
            }
            Sb.Append('}');

            // The raw flight values the receiver is publishing, beyond the handful the normal view shows.
            Sb.Append(",\"flightRaw\":{")
              .Append("\"aoaDeg\":").Append(F(FlightData.AngleOfAttackDeg))
              .Append(",\"fpaDeg\":").Append(F(FlightData.FlightPathAngleDeg))
              .Append(",\"magHeading\":").Append(F(FlightData.MagneticHeadingDeg))
              .Append(",\"fdPitch\":").Append(F(FlightData.FdPitchDeg))
              .Append(",\"fdRoll\":").Append(F(FlightData.FdRollDeg))
              .Append(",\"gpsRelBearing\":").Append(F(FlightData.GpsRelativeBearingDeg))
              .Append(",\"gpsXtkNm\":").Append(F(FlightData.GpsCrossTrackNm, 3))
              .Append('}');

            // The dev-settings override layer. A count here is the fastest way to answer "am I looking at the
            // Unity-authored build, or at a tuned overlay?", which has burned time more than once.
            Sb.Append(",\"tunables\":{")
              .Append("\"count\":").Append(DevSettingsStore.Tunables.Count)
              .Append(",\"overridden\":").Append(OverriddenCount())
              .Append('}');

            Sb.Append(",\"logLines\":").Append(LogRing.Total);

            Sb.Append('}');   // dev

            Sb.Append('}');
            return Sb.ToString();
        }

        static int OverriddenCount()
        {
            int n = 0;
            foreach (DevTunable t in DevSettingsStore.Tunables)
                if (t != null && DevSettingsStore.IsOverridden(t.Key)) n++;
            return n;
        }

        /// <summary>
        /// Every registered dev tunable, as JSON, for the dashboard's read-only DEV list: key, label, section,
        /// kind, range, the CURRENT value, the Unity-authored default, and whether it has been overridden.
        ///
        /// This is the honest answer to "is everything actually exposed?". Rather than trusting a doc that
        /// says the knobs are all reachable, the dashboard shows you the live registry: if a value is missing
        /// from this list, it is not tunable on device, full stop.
        ///
        /// MAIN THREAD ONLY. The get delegates read fields on ScriptableObjects, so like everything else in
        /// this class it is built here and handed to the listener thread as a finished string. Read-only by
        /// design: the dashboard displays these and cannot set them (the headset's dev window is the one place
        /// a value is edited, so there is a single editing path to trust).
        /// </summary>
        public static string BuildTunables()
        {
            var sb = new StringBuilder(8192);
            sb.Append("{\"tunables\":[");
            bool first = true;
            foreach (DevTunable t in DevSettingsStore.Tunables)
            {
                if (t == null) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"key\":\"").Append(Esc(t.Key))
                  .Append("\",\"label\":\"").Append(Esc(t.Label))
                  .Append("\",\"section\":\"").Append(Esc(t.Section ?? ""))
                  .Append("\",\"kind\":\"").Append(t.Type.ToString().ToLowerInvariant())
                  .Append("\",\"value\":\"").Append(Esc(t.Serialize()))
                  .Append("\",\"default\":\"").Append(Esc(DevSettingsStore.DefaultOf(t.Key) ?? ""))
                  .Append("\",\"overridden\":").Append(B(DevSettingsStore.IsOverridden(t.Key)))
                  .Append(",\"min\":").Append(F(t.Min, 4))
                  .Append(",\"max\":").Append(F(t.Max, 4))
                  .Append(",\"help\":\"").Append(Esc(t.Help ?? "")).Append("\"}");
            }
            return sb.Append("]}").ToString();
        }

        static string DwellMap()
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var kv in AoiData.TotalDwell)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(Esc(kv.Key)).Append("\":").Append(F(kv.Value));
            }
            return sb.Append('}').ToString();
        }

        static string Arr(List<string> items)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(items[i])).Append('"');
            }
            return sb.Append(']').ToString();
        }

        // NaN and Infinity are not legal JSON and will break the dashboard's parse outright. They do occur:
        // the device reports behavior velocity as NaN during a fixation. Emit null and let the client show
        // a blank rather than poisoning the whole payload.
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
