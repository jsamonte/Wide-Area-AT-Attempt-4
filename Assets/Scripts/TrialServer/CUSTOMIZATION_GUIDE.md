# TRIAL SERVER: customization and decisions guide

This is the "what can I change, and what do I have to decide" companion to `TRIAL_SERVER.md` (how the server
works), `TRIAL_SERVER_UPDATE.md` (the debug/perf pack, in the delivery folder), and `VERIFY_ON_DEVICE.md`
(the on-device test punch list). Read those for mechanism; read THIS to tune it to your study and your
headset.

It is written to be scanned by Claude. Point Claude at this whole `TrialServer/` folder and ask it to walk
you through a section. Nothing here requires a device to change; only the VERIFY steps do. Every value below
lives in exactly ONE place (a serialized field or an asset), so there is no second copy to keep in sync.

Suggested prompts for Claude, once it can see this folder:
- "Using CUSTOMIZATION_GUIDE.md, list every value I can change and its current default, and tell me which
  ones I should set for a 90 Hz headset."
- "I want a new health chip for X. Walk me through the StatusSnapshot + dashboard edit per the Extending
  section."
- "Which of the Open Decisions still are not decided in this repo? Check the code, not just the doc."

---

## 1. The whole config surface, in one table

Everything you can tune without writing code. Three homes: the `ExperimentServer` component, the
`SequenceBridge` component (both on the `TrialServer.prefab`), and the `PerfProfile` asset.

### 1a. `ExperimentServer` component (on `TrialServer.prefab`)

| Field | Default | What it does | When it takes effect |
|---|---|---|---|
| `enableWebServer` | true | Master switch. Off = no socket at all (the log file still writes). | Next launch |
| `port` | 8080 | Port the dashboard serves on (`http://<headset-ip>:<port>/`). | Next launch |
| `snapshotHz` | 10 | How many times/sec the main thread rebuilds the status JSON. A few Hz is plenty; not on the data path. | Live |
| `logRequests` | false | Log every HTTP request to the console. Noisy; only for chasing a routing problem. | Live |
| `logBufferLines` | 800 | Lines held in the in-memory log ring (what the DEV stream and Download-log serve). Bigger = more history, more memory. | Next launch |
| `logToFile` | true | Write the whole run to `persistentDataPath/logs/run_<time>.log`, flushed per line. This is the outdoor / no-network capture. Independent of the web server. | Next launch |

### 1b. `SequenceBridge` component (on `TrialServer.prefab`)

| Field | Default | What it does |
|---|---|---|
| `trialLimitMinutes` | 20 | Soft trial time limit. DISPLAY ONLY: over the limit the dashboard shows a red banner and the operator decides. Nothing auto-ends a trial. |

### 1c. `PerfProfile` asset (`Resources/PerfProfile.asset`)

The performance monitor's thresholds. One asset, auto-loaded by name. If the asset is missing, the monitor
runs on these same defaults and logs one warning. Full reasoning is in `PerfProfile.cs`.

| Field | Default | What it does |
|---|---|---|
| `enabledAtStartup` | false | Start sampling at launch. OFF ships on purpose: telemetry costs frame time and a recorded trial should not pay for it. The live on/off is driven from the dashboard DEV panel; this only seeds it. |
| `targetFrameMs` | 16.7 | Frame budget in ms. **16.7 = 60 Hz. Set this to your headset's real refresh** (e.g. 8.33 for 120 Hz). Frames slower than this count as over budget. |
| `hitchMultiplier` | 2 | A frame slower than this many times the window median counts as a hitch. Relative to the median so stalls still register on a slow session. |
| `overBudgetWarnPct` | 20 | Percent of frames over budget before the perf chip goes amber. |
| `sampleWindowSeconds` | 10 | Seconds of frame history in the percentile window (~600 frames at 60 Hz). Longer smooths; shorter reacts. |
| `slowPollSeconds` | 1 | Seconds between the EXPENSIVE reads (memory, disk, thermal, XR stats). Their own timer so they are not per-frame. |
| `logIntervalSeconds` | 5 | Seconds between `[PERF]` log lines while sampling. This is what makes a captured run reviewable after the fact. 0 = never. |
| `batteryWarnPct` | 15 | Battery percent below which the perf chip goes red. |
| `freeSpaceCritMb` | 300 | Free storage (MB) below which it is CRITICAL. Running out mid-trial is silent and unrecoverable, hence CRIT not warn. |
| `thermalWarnLevel` | 2 | Android thermal status at/above which the device is throttling (0 NONE, 1 LIGHT, 2 MODERATE, 3 SEVERE, 4 CRITICAL, 5 EMERGENCY, 6 SHUTDOWN). The one to watch outdoors. |

---

## 2. The decisions you actually have to make

These are choices with no safe default; someone has to pick. Ordered by how much they cost to get wrong.

### 2a. Development build vs release build (affects which perf counters exist)
- **Development build**: draw calls, SetPass calls, triangle/vertex counts, and GC-allocated-per-frame all
  report. These come from Unity's ProfilerRecorders, which only publish in a development build.
- **Release build**: those read "n/a" (never a fake zero). Everything else (frame time, memory, storage,
  thermal, battery, dropped frames) still reports.
- **Decide**: run a development build while chasing a rendering/GC regression; ship release for real trials.
  You do not have to choose once and for all; it is per build.

### 2b. Which clock rules the trial time limit (an OPEN decision from TRIAL_SERVER.md)
- The dashboard's trial clock FREEZES during a pause and excludes paused time. The tracker's own clock (the
  timestamps in the gaze JSON) does NOT: it measures from the trial's start moment.
- So after any pause, the page and the JSON disagree about elapsed time. Decide which one the 20-minute limit
  should follow. The page currently excludes paused time. This is a study-rules call, not a server bug.

### 2c. What "target frame time" is for your headset
- `PerfProfile.targetFrameMs` defaults to 60 Hz (16.7 ms). If your ML2 config runs at a different rate, set
  it, or every over-budget/hitch number is measured against the wrong bar. This is the single most impactful
  perf value.

### 2d. How aggressive the alarm thresholds are
- `thermalWarnLevel`, `freeSpaceCritMb`, `batteryWarnPct` decide when a chip turns amber/red. Defaults are
  conservative. Outdoors in sun, thermal is the one to watch; if you get constant amber thermal chips that
  are not actually hurting frames, raise the level. If you record long sessions, raise `freeSpaceCritMb` so
  you are warned with more headroom.

### 2e. Log file on or off, and ring size
- `logToFile` on (default) means every run leaves a full `.log` on disk to hand to an AI afterward. Turn it
  off only if storage is genuinely tight (the files are small text). `logBufferLines` only affects the LIVE
  DEV stream and the Download-log button, not the file, which captures everything regardless.

### 2f. Where to mark CRIT (this is yours to place; see section 4c)
- CRIT means "this session's data is compromised", not "an exception happened". Only YOU know your compromise
  sites. Eye-permission-denied is already marked (`GazeInputManager`). Storage-about-to-run-out is marked
  automatically by the perf monitor. Anything else that silently ruins a recording is a candidate.

### 2g. Study-code decisions the first device tests surfaced (in the study, NOT the server)
These live in the study's own scripts; the server only made them visible. From `TRIAL_SERVER.md`:
- **Dwell range cap.** `EyeAndHeadTracker.RunEyeDwellDestruction` raycasts to `Mathf.Infinity`: gems destroy
  from any distance. If the rule is ~3 m, add a `maxDwellDistanceMeters` and clamp.
- **Dwell grace window.** The dwell timer resets to zero on a single off-target frame (a blink restarts the
  whole 4 s). A short 0.15 s grace would make it forgiving.
- **Stale gaze during tracking dropout.** `GazeInputManager.Update` keeps the last pose when not tracked
  rather than invalidating it, so the ray sticks. (The dashboard's new Head-tracking chip now at least tells
  you WHEN tracking is lost.)
- **Sequence flow / setup phase.** Picking a sequence immediately starts the tutorial and its recording, with
  no "confirm ready, then begin" step and no undo on a mis-tap. A defined setup phase would be safer.
- **Wireframe baselines visible before the first trial.** Nothing disables the four `pool*_9Baseline`
  renderers until the first Start; one line in `SequenceManager.Start()` fixes it if unwanted.

### 2h. Keep the two reflections, or add public getters
- The bridge reads `SequenceManager`'s private tables (pool/wireframe/time-of-day) and calls its private
  `OnTrialFinished()` by reflection, so the study script did not have to change. If those fields/methods are
  renamed, the readout blanks (one warning) or End is refused (clear message); control otherwise survives.
- **Decide**: add public getters and a public `EndTrialEarly()` to `SequenceManager` to remove both
  reflections, OR keep the study script untouched and accept the fragility. Trade is stability vs not editing
  the study.

---

## 3. Customizing the dashboard (the web page)

The whole page is one self-contained TextAsset: `Resources/trialserver_dashboard.txt` (HTML + CSS + JS, no
external files, because Android serves nothing from StreamingAssets). Edit it as text; no Unity needed.

### 3a. Colors / theme
All colors are CSS variables at the top (`:root`). Change these and everything follows:
```
--bg    page background        --ok    green (healthy)
--card  card background         --bad   red (problem to fix)
--line  borders                 --warn  amber (a caveat)
--text  main text               --rec   recording indicator
--dim   secondary text          --sel   selection blue
```

### 3b. What shows where
- **Calm page (always visible):** the punch list (only non-green findings), the trial controls, the readiness
  card, the health bar.
- **DEV panel (hidden until toggled):** the live log stream with per-tag and per-severity filters, the
  Download-log button, the session files list, and the Performance card.
- The punch list and health bar are both built from ONE function, `computeHealth(d)`. Add a chip there once
  and it appears in both, and they can never disagree.

---

## 4. Extending it (the three common additions)

### 4a. Add a health chip (a new thing the punch list watches)
Two edits, both text:
1. In `StatusSnapshot.Build`, read the value on the main thread and append it to the `ready` (or `vitals`)
   block as a JSON field.
2. In the dashboard's `computeHealth(d)`, push a chip: `{ cls: 'ok'|'warn'|'bad', label, fix }`. The `fix`
   string is the plain-language action shown on the punch list.
The Head-tracking chip added in this pack is a worked example of exactly this; copy its shape.

### 4b. Add a tunable value
- A value the server owns: add a serialized field to `ExperimentServer` or `SequenceBridge` (like
  `logBufferLines`). One home, editable in the Inspector.
- A perf threshold: add a field to `PerfProfile` and read `PerfProfile.Active.yourField` in `PerfMonitor`.
- Do NOT add a value in two places (a script default AND an Inspector field AND an asset). One home only;
  that is the rule the whole design is built on.

### 4c. Mark a CRIT site
No special API. Put the level in the existing bracket tag:
```csharp
Debug.LogError("[BRIDGE:CRIT] Eye tracking permission denied: gaze data will be empty.");
```
`LogRing` parses `[TAG:CRIT]` into tag + level. It drives the red CRIT punch-list row and lands in the run
log. Reserve CRIT for compromised session data. Everything logged with a `[TAG]` prefix already becomes a
one-tap DEV filter, so tag your new log lines.

### 4d. (Optional) time a subsystem you suspect is a frame hog
`PerfMonitor.ReportSubsystemMs("markers", ms)` records a named per-frame cost that shows on the Performance
card. It is a no-op while the monitor is off, so the call site costs a bool test. Nothing calls it yet; add
it around a marker pump or a mesh rebuild when you want to know "how many of these can be up at once".

---

## 5. What to leave alone (the load-bearing parts)

- **The threading contract.** The HTTP listener thread must never touch a Unity API (it crashes). The main
  thread builds the JSON in `Update` and publishes a string; the listener only hands out that string. Inbound
  commands enqueue and return 202, drained on the main thread. If you add an endpoint, follow this exactly.
- **The server is not on the data path.** The gaze JSON is written by the study's tracker; if HTTP stalls,
  the recording is untouched and the page just shows a stale number. Keep it that way: do not move any data
  write into the server.
- **One source of truth.** `SequenceManager` stays the trial driver, `EyeAndHeadTracker` the single data
  source. This server observes and commands; it adds no second logger, eye tracker, or copy of any value.

---

## 6. Files in this folder, at a glance

- `TRIAL_SERVER.md` - how the server works (install, workflow, API, fragile points, device-test findings).
- `CUSTOMIZATION_GUIDE.md` - this file (what to change, what to decide).
- `VERIFY_ON_DEVICE.md` - the on-device test punch list for the current unverified batch.
- `ExperimentServer.cs` - socket, snapshot, command queue, file endpoints, log-file toggle.
- `SequenceBridge.cs` - the only piece that touches the study (drives the flow, reads it by reflection).
- `StatusSnapshot.cs` - builds the status JSON on the main thread.
- `LogRing.cs` - the log ring, severity, and the run-log file sink.
- `ServerState.cs` - the one static holder the bridge writes and the snapshot reads.
- `PerfMonitor.cs` / `PerfStats.cs` / `PerfProfile.cs` - the performance monitor, its math, its thresholds.
- `Resources/trialserver_dashboard.txt` - the dashboard page.
- `Resources/PerfProfile.asset` - the perf thresholds asset.
