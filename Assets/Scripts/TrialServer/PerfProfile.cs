using UnityEngine;

namespace TrialServer
{
    /// <summary>
    /// THE one home for the performance monitor's thresholds. Every number the monitor compares against
    /// lives here so it is editable in one place, and PerfMonitor reads it rather than hardcoding.
    ///
    /// This is a STRIPPED version of the AR Cockpit project's PerfProfile. That project routes each field
    /// through its on-device dev-settings registry (a DevTunable per value) so the thresholds are tunable
    /// from the headset and ride its Export / Save / Load. The trial server has no such registry, so this
    /// version is a plain ScriptableObject: edit the values in the Inspector on the asset. If you later add
    /// a tunable system, this is the class to wire into it (make it implement your IDevTunableSource).
    ///
    /// ON BY DEFAULT? NO. <see cref="enabledAtStartup"/> ships FALSE, and that is deliberate. Telemetry is a
    /// diagnostic tool, not a study instrument: during a recorded trial the only thing it can do is take
    /// frame time away from the elements the participant is looking at. So the monitor idles until someone
    /// turns it on from the dashboard's DEV panel, which is the workflow that matters (you turn it on when
    /// you are asking a performance question, and off again before you record).
    ///
    /// The counters that need a DEVELOPMENT build to report anything (draw calls, SetPass, GC alloc per
    /// frame) say "n/a" in a release build rather than reporting zero. A zero that means "not measured"
    /// looks exactly like a zero that means "nothing drawn", and that has cost people entire afternoons.
    ///
    /// Setup (once): Assets > Create > TrialServer > Perf Profile, put it in a Resources folder, name it
    /// exactly "PerfProfile". <see cref="PerfMonitor"/> auto-loads it by that name.
    /// </summary>
    [CreateAssetMenu(menuName = "TrialServer/Perf Profile", fileName = "PerfProfile")]
    public class PerfProfile : ScriptableObject
    {
        [Header("Master switch")]
        [Tooltip("Start sampling as soon as the app launches. OFF is the shipped default: telemetry costs " +
                 "frame time and a recorded trial should not be paying for it. TAKES EFFECT NEXT LAUNCH: " +
                 "the live on/off is owned by PerfMonitor and is driven from the dashboard's DEV panel.")]
        public bool enabledAtStartup = false;

        [Header("Frame budget (what counts as a slow frame)")]
        [Tooltip("Target frame time in milliseconds. 16.7 is 60 Hz. Frames slower than this count as over " +
                 "budget, and the dashboard's perf chip goes amber when too many of them are.")]
        [Range(4f, 50f)] public float targetFrameMs = 16.7f;

        [Tooltip("A frame slower than this many times the window MEDIAN counts as a hitch. Relative to the " +
                 "median rather than the budget on purpose: a hitch is a stall against how the app is " +
                 "currently running, so stalls still register on a session that is slow overall.")]
        [Range(1.5f, 10f)] public float hitchMultiplier = 2f;

        [Tooltip("Percentage of frames allowed over budget before the perf chip goes amber. Some overrun is " +
                 "normal; a sustained third of frames is not.")]
        [Range(1f, 90f)] public float overBudgetWarnPct = 20f;

        [Tooltip("Seconds of frame history kept for the percentile window. Longer smooths out a momentary " +
                 "stall; shorter reacts to one. 10 s is about 600 frames at 60 Hz.")]
        [Range(2f, 60f)] public float sampleWindowSeconds = 10f;

        [Header("Sampling cadence")]
        [Tooltip("Seconds between the EXPENSIVE reads: /proc memory, free disk space, thermal status, XR " +
                 "compositor stats. These are the ones that would actually cost something per frame, which " +
                 "is why they are on their own timer.")]
        [Range(0.25f, 10f)] public float slowPollSeconds = 1f;

        [Tooltip("Seconds between [PERF] log lines while the monitor is running. The log line is what makes " +
                 "a captured session reviewable afterward, rather than only being watchable live. 0 = never.")]
        [Range(0f, 60f)] public float logIntervalSeconds = 5f;

        [Header("Alarm thresholds")]
        [Tooltip("Battery percentage below which the perf chip goes red.")]
        [Range(1f, 50f)] public float batteryWarnPct = 15f;

        [Tooltip("Free space on the app's storage, in megabytes, below which this is treated as CRITICAL. " +
                 "Running out of disk mid-trial is silent and unrecoverable, which is why it earns a CRIT " +
                 "rather than a warning.")]
        [Range(50f, 5000f)] public float freeSpaceCritMb = 300f;

        [Tooltip("Android thermal status at or above which the device is considered to be throttling. " +
                 "0 NONE, 1 LIGHT, 2 MODERATE, 3 SEVERE, 4 CRITICAL, 5 EMERGENCY, 6 SHUTDOWN. Throttling " +
                 "explains slow frames that otherwise look like a code regression, which is the single most " +
                 "misleading failure this whole module exists to name. This is the one to watch outdoors.")]
        [Range(1, 6)] public int thermalWarnLevel = 2;

        // Auto-loaded singleton: the asset named "PerfProfile" in any Resources folder. Falls back to an
        // in-memory default so the monitor still runs before the asset exists, with a warning rather than a
        // silent second set of values.
        static PerfProfile _active;
        public static PerfProfile Active
        {
            get
            {
                if (_active == null) _active = Resources.Load<PerfProfile>("PerfProfile");
                if (_active == null)
                {
                    Debug.LogWarning("[PerfProfile] No 'PerfProfile' asset found in a Resources folder. " +
                                     "Create one: Assets > Create > TrialServer > Perf Profile, put it in " +
                                     "Assets/Resources, name it 'PerfProfile'. Using defaults for now.");
                    _active = CreateInstance<PerfProfile>();
                }
                return _active;
            }
        }
    }
}
