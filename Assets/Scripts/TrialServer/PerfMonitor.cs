using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.XR;

namespace TrialServer
{
    /// <summary>
    /// THE performance monitor: one owner for every frame-time, memory, thermal, storage and compositor
    /// number the app reports. Nothing else should keep its own frame-rate accumulator, because two would
    /// quietly disagree about how fast the app is running.
    ///
    /// WHY IT SPAWNS ITSELF. It is created by a RuntimeInitializeOnLoadMethod rather than dropped into a
    /// scene, for two reasons: additive scene loading makes duplicate components easy (and two monitors
    /// would double every counter read), and a value that the dashboard needs must not depend on which
    /// scenes happen to be loaded. So there is nothing to wire in the Inspector; it just comes up.
    ///
    /// OFF BY DEFAULT, AND THAT IS THE POINT. A recorded trial should not be paying frame time to measure
    /// itself. In the OFF state this component does one boolean test per frame: no ProfilerRecorders exist,
    /// no /proc file is read, no JNI call is made, no statistics are computed. It is switched ON from the
    /// dashboard's DEV panel when you are actually asking a performance question. The live on/off is owned
    /// HERE, as a single field, and <see cref="PerfProfile.enabledAtStartup"/> only seeds it at launch.
    ///
    /// EVERY COUNTER CAN SAY "I DO NOT KNOW". Most of these are best-effort on an Android XR device: the
    /// compositor stats may not be published by the ML2 runtime, the profiler recorders are meaningless
    /// outside a development build, and thermal status needs API 29. Each one therefore reports null rather
    /// than zero when it is unavailable, and the dashboard prints "n/a". A zero that means "not measured"
    /// is indistinguishable from a zero that means "nothing happened", and that ambiguity is how you end up
    /// debugging a counter instead of the app.
    ///
    /// MAIN THREAD ONLY. Everything here touches Unity APIs. The web server publishes the finished JSON
    /// string to its listener thread, exactly like every other part of the status blob.
    /// </summary>
    public class PerfMonitor : MonoBehaviour
    {
        const string Tag = "[PERF]";

        public static PerfMonitor Instance { get; private set; }

        // ---- The one live on/off ---------------------------------------------------------------------

        bool _enabled;
        /// <summary>Is the monitor sampling RIGHT NOW? The single live answer; the profile only seeds it.</summary>
        public static bool Sampling => Instance != null && Instance._enabled;

        // ---- Always-on (free) ------------------------------------------------------------------------
        //
        // One lerp per frame, costing nothing, so the dashboard always has a frame time to show even with
        // the monitor off. Without this, turning telemetry off would blank the one number you look at first
        // when someone says "it feels bad", which would just teach everyone to leave it on.

        float _smoothedMs = 1000f / 60f;
        /// <summary>Smoothed frame time in ms. Always valid, monitor on or off.</summary>
        public static float SmoothedFrameMs => Instance != null ? Instance._smoothedMs : 1000f / 60f;
        /// <summary>Smoothed frame rate. Always valid, monitor on or off.</summary>
        public static float SmoothedFps
        {
            get { float ms = SmoothedFrameMs; return ms > 1e-4f ? 1000f / ms : 0f; }
        }

        // ---- Sampled only while enabled --------------------------------------------------------------

        PerfStats _stats;
        PerfStats.Summary _summary;

        float _slowTimer;
        float _logTimer;
        float _enabledAt;

        // Profiler recorders. These only report anything in a DEVELOPMENT build; in a release build they
        // come back invalid and every value below stays null. Created on enable and disposed on disable,
        // so an off monitor is not holding any profiler machinery open.
        ProfilerRecorder _drawCalls, _setPass, _gcAlloc, _tris, _verts;
        bool _recordersUp;

        // Memory
        long _totalAllocated, _totalReserved, _monoUsed;
        long _rssBytes, _rssPeakBytes;
        bool _rssAvailable = true;

        // Storage
        long _freeBytes = -1;

        // Thermal (Android PowerManager). Cached, and permanently given up on after one failure so a
        // device without it does not pay for a throwing JNI call every second.
        AndroidJavaObject _powerManager;
        bool _thermalAvailable = true;
        int _thermalStatus = -1;
        int _thermalPeak = -1;

        // Battery. The run-day question is not "what percent", it is "does this headset survive four more
        // trials", so the useful numbers are the drain RATE and the projection off it.
        float _batteryPct = -1f;
        float _batteryBaselinePct = -1f;
        float _batteryBaselineTime = -1f;
        float _drainPctPerHour;
        float _hoursRemaining = -1f;

        // XR compositor. On a headset the compositor holds a steady presented frame rate by reprojecting,
        // so the app's own frame rate LIES about what the participant sees. Dropped frames are the truth.
        XRDisplaySubsystem _display;
        bool _xrStatsAvailable;
        float _gpuAppMs = -1f, _gpuCompositorMs = -1f, _droppedFrames = -1f, _presentedFrames = -1f;

        // Subsystem timing. Keep the call sites few (your known frame hogs) because this is the number that
        // answers "how many elements can be up at once". Kept as a plain smoothed ms per named slot.
        const int MaxSlots = 4;
        readonly string[] _slotNames = new string[MaxSlots];
        readonly float[] _slotMs = new float[MaxSlots];
        int _slotCount;

        // ---- Lifecycle -------------------------------------------------------------------------------

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (Instance != null) return;
            var go = new GameObject("PerfMonitor");
            DontDestroyOnLoad(go);
            go.AddComponent<PerfMonitor>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;

            PerfProfile cfg = PerfProfile.Active;

            // Size the ring once, from the window length at the target rate. Reallocating it on a live edit
            // would throw away the history you were looking at, so it is fixed for the session.
            int capacity = Mathf.Clamp(Mathf.CeilToInt(cfg.sampleWindowSeconds * 1000f / Mathf.Max(1f, cfg.targetFrameMs)), 64, 4000);
            _stats = new PerfStats(capacity);

            if (cfg.enabledAtStartup) SetEnabled(true, "profile");
        }

        void OnDestroy()
        {
            if (Instance == this) { TearDownRecorders(); Instance = null; }
        }

        /// <summary>
        /// Turn sampling on or off. This is what the dashboard's DEV toggle calls (marshalled onto the main
        /// thread by ExperimentServer, never from the listener thread). The transition is logged with its
        /// source, because "who turned this on" is exactly the kind of thing nobody remembers an hour later.
        /// </summary>
        public static void SetEnabled(bool on, string source)
        {
            if (Instance == null) return;
            if (Instance._enabled == on) return;
            Instance._enabled = on;

            if (on)
            {
                Instance._stats.Clear();
                Instance._enabledAt = Time.realtimeSinceStartup;
                Instance._slowTimer = 0f;
                Instance._logTimer = 0f;
                Instance._batteryBaselinePct = -1f;
                Instance._thermalPeak = -1;
                Instance._rssPeakBytes = 0;
                Instance.SetUpRecorders();
                Debug.Log($"{Tag} Sampling ON (by {source}). Development build: {Debug.isDebugBuild}. " +
                          "Draw calls and GC alloc only report in a development build.");
            }
            else
            {
                Instance.TearDownRecorders();
                Debug.Log($"{Tag} Sampling OFF (by {source}). {Instance.SummaryLine()}");
            }
        }

        void SetUpRecorders()
        {
            if (_recordersUp) return;
            _recordersUp = true;
            try
            {
                _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
                _setPass   = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
                _tris      = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
                _verts     = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
                _gcAlloc   = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{Tag} Profiler recorders unavailable: {e.Message}. Render counters will read n/a.");
            }
        }

        void TearDownRecorders()
        {
            if (!_recordersUp) return;
            _recordersUp = false;
            SafeDispose(ref _drawCalls);
            SafeDispose(ref _setPass);
            SafeDispose(ref _tris);
            SafeDispose(ref _verts);
            SafeDispose(ref _gcAlloc);
        }

        static void SafeDispose(ref ProfilerRecorder r)
        {
            try { if (r.Valid) r.Dispose(); } catch { /* a recorder that never started is not an error */ }
        }

        // ---- The frame loop --------------------------------------------------------------------------

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            float ms = dt * 1000f;

            // The always-on part: one lerp. This runs whether or not sampling is enabled.
            if (dt > 0f) _smoothedMs = Mathf.Lerp(_smoothedMs, ms, 0.1f);

            if (!_enabled) return;   // <- the entire cost of an OFF monitor is this test

            PerfProfile cfg = PerfProfile.Active;
            _stats.Add(ms, cfg.targetFrameMs);

            _slowTimer -= dt;
            if (_slowTimer <= 0f)
            {
                _slowTimer = Mathf.Max(0.25f, cfg.slowPollSeconds);
                SlowPoll(cfg);
                _summary = _stats.Compute(cfg.targetFrameMs, cfg.hitchMultiplier);
            }

            if (cfg.logIntervalSeconds > 0f)
            {
                _logTimer -= dt;
                if (_logTimer <= 0f)
                {
                    _logTimer = cfg.logIntervalSeconds;
                    EmitLogLine(cfg);
                }
            }
        }

        /// <summary>The expensive reads, on their own timer. Every one of them is wrapped: a device that
        /// will not answer must leave the value null, not throw into the frame loop.</summary>
        void SlowPoll(PerfProfile cfg)
        {
            // Managed / native memory. These work in a release build, unlike the profiler recorders.
            _totalAllocated = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            _totalReserved  = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong();
            _monoUsed       = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();

            ReadRss();
            ReadFreeSpace();
            ReadThermal(cfg);
            ReadBattery();
            ReadXrStats();
        }

        // System.Diagnostics.Process is unreliable on IL2CPP/Android, so read the kernel's own answer.
        // PEAK matters more than current: a steady climb across a session points at a leak, and that is a
        // different diagnosis from thermal throttling with completely different fixes.
        void ReadRss()
        {
            if (!_rssAvailable) return;
            try
            {
                if (!File.Exists("/proc/self/status")) { _rssAvailable = false; return; }
                foreach (string line in File.ReadAllLines("/proc/self/status"))
                {
                    if (!line.StartsWith("VmRSS:", StringComparison.Ordinal)) continue;
                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[1], out long kb))
                    {
                        _rssBytes = kb * 1024L;
                        if (_rssBytes > _rssPeakBytes) _rssPeakBytes = _rssBytes;
                    }
                    return;
                }
                _rssAvailable = false;   // no VmRSS line on this platform; stop looking
            }
            catch { _rssAvailable = false; }
        }

        void ReadFreeSpace()
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Application.persistentDataPath));
                _freeBytes = drive.AvailableFreeSpace;
            }
            catch { _freeBytes = -1; }
        }

        // Android thermal status (API 29+). This is the long-session killer and, more importantly, the
        // explanation for slow frames that otherwise look exactly like a code regression.
        void ReadThermal(PerfProfile cfg)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!_thermalAvailable) return;
            try
            {
                if (_powerManager == null)
                {
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    {
                        _powerManager = activity.Call<AndroidJavaObject>("getSystemService", "power");
                    }
                    if (_powerManager == null) { _thermalAvailable = false; return; }
                }

                _thermalStatus = _powerManager.Call<int>("getCurrentThermalStatus");
                if (_thermalStatus > _thermalPeak) _thermalPeak = _thermalStatus;
            }
            catch (Exception e)
            {
                _thermalAvailable = false;
                _thermalStatus = -1;
                Debug.LogWarning($"{Tag} Thermal status unavailable ({e.Message}). Needs API 29+; reporting n/a.");
            }
#else
            _thermalAvailable = false;
            _thermalStatus = -1;
#endif
        }

        void ReadBattery()
        {
            float level = SystemInfo.batteryLevel;      // 0..1, or -1 where the platform will not say
            _batteryPct = level < 0f ? -1f : level * 100f;

            bool charging = SystemInfo.batteryStatus == BatteryStatus.Charging ||
                            SystemInfo.batteryStatus == BatteryStatus.Full;

            // Charging invalidates a drain baseline, so restart it rather than reporting a negative drain.
            if (_batteryPct < 0f || charging)
            {
                _batteryBaselinePct = -1f;
                _drainPctPerHour = 0f;
                _hoursRemaining = -1f;
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (_batteryBaselinePct < 0f) { _batteryBaselinePct = _batteryPct; _batteryBaselineTime = now; return; }

            float hours = (now - _batteryBaselineTime) / 3600f;
            // Below a few minutes the reported level has not moved enough to divide by, and the result is
            // noise dressed up as a projection. Say nothing rather than say something wrong.
            if (hours < 0.05f) return;

            _drainPctPerHour = (_batteryBaselinePct - _batteryPct) / hours;
            _hoursRemaining = _drainPctPerHour > 0.5f ? _batteryPct / _drainPctPerHour : -1f;
        }

        // The compositor's view, which is the one that matters on a headset: it holds a steady presented
        // rate by reprojecting stale frames, so app frame rate alone can look fine while the participant is
        // seeing judder. Whether the ML2 runtime publishes these at all is a DEVICE question we cannot
        // answer here, which is why availability is reported explicitly.
        void ReadXrStats()
        {
            if (_display == null || !_display.running)
            {
                var displays = new System.Collections.Generic.List<XRDisplaySubsystem>();
                SubsystemManager.GetSubsystems(displays);
                _display = displays.Count > 0 ? displays[0] : null;
            }
            if (_display == null) { _xrStatsAvailable = false; return; }

            bool any = false;
            any |= TryStat("GPUAppTime", ref _gpuAppMs);
            any |= TryStat("GPUCompositorTime", ref _gpuCompositorMs);
            any |= TryStat("droppedFrameCount", ref _droppedFrames);
            any |= TryStat("framePresentCount", ref _presentedFrames);
            _xrStatsAvailable = any;
        }

        bool TryStat(string name, ref float into)
        {
            try
            {
                if (UnityEngine.XR.Provider.XRStats.TryGetStat(_display, name, out float v)) { into = v; return true; }
            }
            catch { /* a runtime that does not publish this stat is not an error worth logging every second */ }
            into = -1f;
            return false;
        }

        // ---- Subsystem timing ------------------------------------------------------------------------

        /// <summary>
        /// Report how long a named subsystem took this frame, in milliseconds. Call it from your known frame
        /// hogs (a marker pump, a mesh rebuild, a heavy line-renderer update): the number it produces is the
        /// one that answers "how many of these can be up at once".
        ///
        /// A no-op while the monitor is off, so the call sites cost a static bool test and nothing else.
        /// Example:
        ///   var sw = System.Diagnostics.Stopwatch.StartNew();
        ///   ... work ...
        ///   PerfMonitor.ReportSubsystemMs("markers", (float)sw.Elapsed.TotalMilliseconds);
        /// </summary>
        public static void ReportSubsystemMs(string name, float ms)
        {
            PerfMonitor m = Instance;
            if (m == null || !m._enabled || string.IsNullOrEmpty(name)) return;

            for (int i = 0; i < m._slotCount; i++)
            {
                if (m._slotNames[i] != name) continue;
                m._slotMs[i] = Mathf.Lerp(m._slotMs[i], ms, 0.2f);
                return;
            }
            if (m._slotCount >= MaxSlots) return;
            m._slotNames[m._slotCount] = name;
            m._slotMs[m._slotCount] = ms;
            m._slotCount++;
        }

        // ---- Reporting -------------------------------------------------------------------------------

        void EmitLogLine(PerfProfile cfg)
        {
            PerfStats.Summary s = _summary;

            // CRIT is reserved for "this trial's data is compromised", per the LogRing contract. Storage
            // about to run out qualifies: the file stops mid-trial, silently and unrecoverably. Thermal at or
            // past the warn level does not compromise the DATA, so it stays a warning however alarming it is.
            bool storageCrit = _freeBytes >= 0 && _freeBytes < (long)(cfg.freeSpaceCritMb * 1024f * 1024f);
            string level = storageCrit ? "[PERF:CRIT]" : (Throttling(cfg) ? "[PERF:WARN]" : "[PERF]");

            var sb = new StringBuilder(256);
            sb.Append(level).Append(' ')
              .Append(N(s.MeanFps, 1)).Append(" fps mean, ")
              .Append(N(s.OnePctLowFps, 1)).Append(" fps 1% low | ms p50/p95/p99 ")
              .Append(N(s.P50Ms, 1)).Append('/').Append(N(s.P95Ms, 1)).Append('/').Append(N(s.P99Ms, 1))
              .Append(" worst ").Append(N(s.WorstMs, 1))
              .Append(" | ").Append(N(s.OverBudgetPct, 0)).Append("% over budget, ")
              .Append(s.Hitches).Append(" hitch(es)");

            if (_rssBytes > 0) sb.Append(" | rss ").Append(Mb(_rssBytes)).Append(" MB (peak ").Append(Mb(_rssPeakBytes)).Append(')');
            if (_thermalStatus >= 0) sb.Append(" | thermal ").Append(_thermalStatus);
            if (_batteryPct >= 0f)
            {
                sb.Append(" | batt ").Append(N(_batteryPct, 0)).Append('%');
                if (_drainPctPerHour > 0.5f) sb.Append(" (-").Append(N(_drainPctPerHour, 1)).Append("%/h)");
            }
            if (_freeBytes >= 0) sb.Append(" | free ").Append(Mb(_freeBytes)).Append(" MB");
            if (_droppedFrames >= 0f) sb.Append(" | dropped ").Append(N(_droppedFrames, 0));
            for (int i = 0; i < _slotCount; i++)
                sb.Append(" | ").Append(_slotNames[i]).Append(' ').Append(N(_slotMs[i], 2)).Append(" ms");

            if (storageCrit) sb.Append("  STORAGE NEARLY FULL: a session file will stop mid-write.");

            Debug.Log(sb.ToString());
        }

        bool Throttling(PerfProfile cfg) => _thermalStatus >= 0 && _thermalStatus >= cfg.thermalWarnLevel;

        /// <summary>
        /// A one-line session summary, for a log line printed when sampling is turned off (and anywhere else
        /// you want to stamp the performance a session was collected under). A file that records the
        /// performance it was collected under is the difference between "that run felt bad" and a number you
        /// can compare across conditions.
        ///
        /// Returns a line saying so plainly when the monitor was never running, rather than a row of zeros
        /// that would read as a perfectly smooth session.
        /// </summary>
        public string SummaryLine()
        {
            if (_stats == null || _stats.TotalFrames == 0)
                return "perf = not sampled (monitor off)";

            PerfProfile cfg = PerfProfile.Active;
            PerfStats.Summary s = _stats.Compute(cfg.targetFrameMs, cfg.hitchMultiplier);
            var sb = new StringBuilder(200);
            sb.Append("perf = ").Append(N(s.MeanFps, 1)).Append(" fps mean, ")
              .Append(N(s.OnePctLowFps, 1)).Append(" fps 1% low, p95 ").Append(N(s.P95Ms, 1)).Append(" ms, worst ")
              .Append(N(_stats.WorstEverMs, 1)).Append(" ms, ")
              .Append(_stats.FramesOverBudget).Append('/').Append(_stats.TotalFrames).Append(" frames over ")
              .Append(N(cfg.targetFrameMs, 1)).Append(" ms");
            if (_rssPeakBytes > 0) sb.Append(", peak rss ").Append(Mb(_rssPeakBytes)).Append(" MB");
            if (_thermalPeak >= 0) sb.Append(", peak thermal ").Append(_thermalPeak);
            return sb.ToString();
        }

        /// <summary>The same summary for whoever holds no reference to the instance.</summary>
        public static string Summary() => Instance != null ? Instance.SummaryLine() : "perf = not sampled (no monitor)";

        /// <summary>
        /// Append the dashboard's <c>"perf"</c> block. MAIN THREAD ONLY, like every other part of the status
        /// blob: StatusSnapshot builds the string and the web server hands the finished text to its listener
        /// thread.
        ///
        /// With the monitor off this emits four fields and stops. That is deliberate: an off monitor should
        /// produce an obviously-empty card rather than a full card of stale numbers from whenever it was
        /// last on, which is the kind of thing that gets read as live and acted on.
        /// </summary>
        public static void AppendJson(StringBuilder sb)
        {
            sb.Append("\"on\":").Append(Sampling ? "true" : "false");
            sb.Append(",\"fps\":").Append(N(SmoothedFps, 1));
            sb.Append(",\"frameMs\":").Append(N(SmoothedFrameMs, 1));

            PerfMonitor m = Instance;
            if (m == null || !m._enabled)
            {
                sb.Append(",\"devBuild\":").Append(Debug.isDebugBuild ? "true" : "false");
                return;
            }

            PerfProfile cfg = PerfProfile.Active;
            PerfStats.Summary s = m._summary;

            sb.Append(",\"devBuild\":").Append(Debug.isDebugBuild ? "true" : "false")
              .Append(",\"forS\":").Append(N(Time.realtimeSinceStartup - m._enabledAt, 0))
              .Append(",\"budgetMs\":").Append(N(cfg.targetFrameMs, 1))
              .Append(",\"meanFps\":").Append(N(s.MeanFps, 1))
              .Append(",\"lowFps\":").Append(N(s.OnePctLowFps, 1))
              .Append(",\"p50\":").Append(N(s.P50Ms, 1))
              .Append(",\"p95\":").Append(N(s.P95Ms, 1))
              .Append(",\"p99\":").Append(N(s.P99Ms, 1))
              .Append(",\"worstMs\":").Append(N(s.WorstMs, 1))
              .Append(",\"overPct\":").Append(N(s.OverBudgetPct, 1))
              .Append(",\"overBudgetWarnPct\":").Append(N(cfg.overBudgetWarnPct, 1))
              .Append(",\"hitches\":").Append(s.Hitches)
              .Append(",\"samples\":").Append(s.Count)
              .Append(",\"totalFrames\":").Append(m._stats.TotalFrames);

            // Memory. allocated/reserved/mono report in any build; gcPerFrame needs a development build.
            sb.Append(",\"allocMb\":").Append(Mb(m._totalAllocated))
              .Append(",\"reservedMb\":").Append(Mb(m._totalReserved))
              .Append(",\"monoMb\":").Append(Mb(m._monoUsed))
              .Append(",\"rssMb\":").Append(m._rssBytes > 0 ? Mb(m._rssBytes) : "null")
              .Append(",\"rssPeakMb\":").Append(m._rssPeakBytes > 0 ? Mb(m._rssPeakBytes) : "null")
              .Append(",\"gcPerFrame\":").Append(Rec(m._gcAlloc));

            // Render counters: development build only, null otherwise.
            sb.Append(",\"drawCalls\":").Append(Rec(m._drawCalls))
              .Append(",\"setPass\":").Append(Rec(m._setPass))
              .Append(",\"tris\":").Append(Rec(m._tris))
              .Append(",\"verts\":").Append(Rec(m._verts));

            // Thermal + power.
            sb.Append(",\"thermal\":").Append(m._thermalStatus >= 0 ? m._thermalStatus.ToString(CultureInfo.InvariantCulture) : "null")
              .Append(",\"thermalPeak\":").Append(m._thermalPeak >= 0 ? m._thermalPeak.ToString(CultureInfo.InvariantCulture) : "null")
              .Append(",\"thermalWarn\":").Append(cfg.thermalWarnLevel)
              .Append(",\"batteryPct\":").Append(m._batteryPct >= 0f ? N(m._batteryPct, 0) : "null")
              .Append(",\"drainPctPerHour\":").Append(m._drainPctPerHour > 0.5f ? N(m._drainPctPerHour, 1) : "null")
              .Append(",\"hoursLeft\":").Append(m._hoursRemaining > 0f ? N(m._hoursRemaining, 1) : "null");

            // Storage.
            sb.Append(",\"freeMb\":").Append(m._freeBytes >= 0 ? Mb(m._freeBytes) : "null")
              .Append(",\"freeCritMb\":").Append(N(cfg.freeSpaceCritMb, 0));

            // XR compositor. availability is reported separately from the values, because "the runtime does
            // not publish this" and "the value is zero" are completely different findings.
            sb.Append(",\"xrStats\":").Append(m._xrStatsAvailable ? "true" : "false")
              .Append(",\"gpuAppMs\":").Append(m._gpuAppMs >= 0f ? N(m._gpuAppMs, 2) : "null")
              .Append(",\"gpuCompMs\":").Append(m._gpuCompositorMs >= 0f ? N(m._gpuCompositorMs, 2) : "null")
              .Append(",\"dropped\":").Append(m._droppedFrames >= 0f ? N(m._droppedFrames, 0) : "null")
              .Append(",\"presented\":").Append(m._presentedFrames >= 0f ? N(m._presentedFrames, 0) : "null");

            sb.Append(",\"subsystems\":{");
            for (int i = 0; i < m._slotCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(m._slotNames[i]).Append("\":").Append(N(m._slotMs[i], 3));
            }
            sb.Append('}');
        }

        // A recorder that never started, or a release build, reports invalid. Emit null, never 0.
        static string Rec(ProfilerRecorder r)
            => r.Valid ? r.LastValue.ToString(CultureInfo.InvariantCulture) : "null";

        static string Mb(long bytes) => (bytes / (1024f * 1024f)).ToString("F1", CultureInfo.InvariantCulture);

        static string N(float v, int decimals)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
            return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }
    }
}
