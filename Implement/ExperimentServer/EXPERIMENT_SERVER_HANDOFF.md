# Experiment server: handoff guide

A Magic Leap 2 app that hosts its own web dashboard, so the researcher can drive an experiment and watch
what the participant is experiencing from a phone or a laptop browser. Written for the AR Cockpit flight
study, but the server, the trial state machine, and the CSV logger are deliberately independent of it.

This document is written for someone dropping these files into a DIFFERENT Unity project. It says what to
copy, what to cut, **why things are the way they are**, and what does not work well. It is also written to
be read by an AI assistant on your behalf: every non-obvious decision carries its reasoning inline, so you
can hand this file to a model and ask "what do I have to change for my project" and get a real answer.

Read section 7 before you trust it with real data.

Verified on device: Magic Leap 2, Unity 2022.3 LTS, URP, IL2CPP, Android x86-64.

_Last updated: 2026-07-14._

---

## 1. What it does

The headset runs a small HTTP server. Point any browser on the same network at it and you get a page that
shows:

- **Vitals**: battery, and whether head tracking is alive.
- **Readiness**: is eye tracking live, are permissions granted, has the participant been calibrated, is the
  data source connected, has the participant actually LOOKED at each element yet.
- **Live telemetry**: what the participant is looking at right now, and the state of whatever your app is
  measuring. The point is that the researcher can see the streams are ALIVE and not frozen, without asking
  the participant.
- **Trial controls**: set participant / condition / trial number, start, stop (good or bad), pause, resume,
  redo, and mark an event mid-trial.
- **A dev panel** (hidden by default): the device log stream with per-subsystem filter buttons, a system
  panel, and the full live tunable registry. This is what replaces `adb logcat` when there is no PC.
- **The recorded files**, downloadable from the page.

Data is written to a CSV on the headset at frame rate. **The dashboard is never on the data path.** If HTTP
stalls, the recording is untouched and the page just shows a stale number. That single property is what
makes everything else here safe to be sloppy about.

## 2. Files to copy

Everything lives in `Assets/Scripts/Study/` plus two assets.

| File | What it is | Can you cut it? |
|---|---|---|
| `ExperimentServer.cs` | The HTTP server. Threading, routing, file download. | No. This is the core. |
| `StatusSnapshot.cs` | Builds the dashboard's JSON on the main thread. | No, but you WILL edit it (section 5). |
| `TrialData.cs` | Static holder: state, participant, condition, trial number, row count, last message. | No. |
| `TrialController.cs` | The state machine. Idle / Recording / Paused, file naming, guardrails, markers. | No. |
| `DataLogger.cs` | Writes the CSV. Flush, file filing, the trial index. | No, but you WILL edit the columns. |
| `StudyProfile.cs` | The ScriptableObject holding every tunable parameter, and the CSV's `#` header. | No. See the warning below. |
| `LogRing.cs` | Bounded log ring buffer feeding the dev panel's log stream. | Yes, if you drop the dev panel. |
| `Resources/dashboard_html.txt` | The whole dashboard: vanilla HTML, CSS, JS in one file. | No, but edit freely. |
| `Resources/StudyProfile.asset` | The profile instance. **Must be named exactly `StudyProfile`.** | No. |
| `EyeTracker.cs`, `EyeData.cs` | Magic Leap eye tracking, the one writer plus its static holder. | Yes, if you do not need eyes. |
| `GazeAoi.cs`, `GazeTarget.cs`, `IGazeBoundsSource.cs` | Which object is the participant looking at, and for how long. | Yes, if you do not need gaze AOI. |

### The dependency you will hit immediately

**These files are NOT self-contained.** They `using` five other namespaces from this project:

| Namespace | What is used | What to do |
|---|---|---|
| `ARCockpit.DevSettings` | `DevSettingsStore`, `DevTunable`, `DevMode`, `IDevTunableSource`. `StudyProfile` implements the tunable interface; the CSV header and the dev panel's tunable list both walk the registry. | Copy the `DevSettings` folder too, or strip it (see below). |
| `ARCockpit.Core` | `MarkerAnchor`, `IMarkerSource` (the ArUco tag that pops the on-device settings window). | Copy, or strip. |
| `ARCockpit.Data` | `FlightData`, `XP12Receiver` (this project's telemetry). | Replace with YOUR data source. |
| `ARCockpit.FlightPlan` | `GuidanceData`, `GuidanceCommandWriter`, `TrialReset`. | Delete these blocks. |
| `ARCockpit.Terrain` | `MapProfile`, only for the gaze hitbox bounds. | Delete with the gaze files if unused. |

**The minimum viable strip**, if you want the server and logger and nothing else: delete `StudyProfile.cs`,
replace `Profile`-derived values in `TrialController` / `DataLogger` / `ExperimentServer` with plain
`[SerializeField]` fields, and delete the `DevSettingsStore` blocks in `StatusSnapshot.BuildTunables()`,
`DataLogger.WriteParamHeader()` and `ExperimentServer` (the `/api/tunables` route and `_tunablesDirty`).
That costs you the on-device tuning and the self-describing CSV header, and it is a perfectly reasonable
trade if your project has its own config system. Keep everything else.

**Namespace** is `ARCockpit.Study`. Rename it to yours.

**Setup**: put `ExperimentServer` and `TrialController` on one persistent GameObject in your bootstrap
scene. `DataLogger` is added automatically (`[RequireComponent]`). Create the `StudyProfile` asset in
`Resources`. That is the entire installation.

## 3. The design, and why

### The threading contract (the thing that will crash you if you ignore it)

`HttpListener` serves requests on a BACKGROUND thread. Touching almost any Unity API from there, including
something as innocent as `Application.persistentDataPath` or `Time.time`, throws or hard-crashes. So:

- **Outbound**: the MAIN thread builds the status JSON in `Update()` and publishes it to a `volatile string`.
  The listener thread only ever hands out that finished string. It never reads Unity state.
- **Inbound**: a request pushes a command onto a `ConcurrentQueue` and immediately returns **202 Accepted**.
  `Update()` drains the queue and executes on the main thread.
- Anything the listener thread needs from Unity (the data directory, the HTML, profile flags) is cached in
  `Awake()` or mirrored into a `volatile` field each frame. `_logRequests` exists for exactly this reason:
  reading `StudyProfile.Active` off-thread would be a crash.

A consequence worth internalizing: **202 means "heard you", not "done".** See the next section, because
that consequence bit us.

### Why the dashboard reports the OUTCOME of a command, not just its delivery

This is the most important lesson in the file, and it cost a real bug.

Every guard in `TrialController` refuses a command with a good, specific message: "the eye tracker has never
reported high confidence, run the ML2 eye calibration", "useTrialReset is ON but there is no TrialReset in
any loaded scene". Those messages went to `Debug.LogError`. Meanwhile the HTTP layer had already returned
202, and the dashboard printed "Sent /api/trial/start."

So on run day, the operator pressed Start, got a cheerful confirmation, watched nothing happen, and the
reason why was sitting in `adb logcat` **on a headset with no PC plugged into it.** The reasons were always
right. They were simply written somewhere nobody could read at the moment they mattered.

The fix is `TrialData.Report(message, level)`. The controller routes EVERY command outcome through exactly
one of two helpers, `Accept()` or `Refuse()`, each of which logs AND publishes to the status blob, so a
refusal cannot be logged without also reaching the page. The dashboard renders the latest message under the
buttons and ages it out after 20 seconds.

**If you take one thing from this document: an operator UI that confirms delivery instead of outcome is
worse than one that confirms nothing**, because it actively teaches the operator that the thing worked.

### Why the state machine is separate from the logger

The logger writes rows. The controller decides when a trial exists. They are separate because in this
project a trial start also REPOSITIONS the aircraft in the simulator, and a file writer that can also
reposition an aircraft is how a reset ends up firing in the middle of a run. Your project may have an
equivalent side effect (loading a scene, resetting a task). Keep the split.

### Why files are born in `_incomplete/`

```
persistentDataPath/trials/
  _incomplete/        <- every file starts here
  valid/              <- moved here on "Stop: GOOD"
  invalid/            <- moved here on "Stop: BAD", with your reason
  trials_index.csv    <- one line per trial: who, condition, trial, valid, note, rows, duration, file
```

A file only MOVES out of `_incomplete/` when a human deliberately closes the trial out. So if the app
crashes or the battery dies mid-trial, the partial file stays in `_incomplete/` and says exactly what it is.
**A crashed run can never be silently analyzed as a good one.** This is the single most valuable thing in
the design and it costs almost nothing.

Nothing is ever deleted. A trial you cut short is still evidence; whether to use it is a decision for
analysis, not for a button pressed under time pressure on run day. There is deliberately **no "clear logs"
endpoint and no delete button**: a delete control on a phone, next to the buttons you mash between
participants, is how you lose a morning of data to a fat thumb. If you ever need bulk deletion, do it over
`adb`, off the device, with the files already backed up.

### Why "Stop: GOOD" and "Stop: BAD" instead of one Stop

Stopping is the one moment where a wrong default is expensive. "It stopped, I will sort it out later" does
not survive contact with twenty participants. The verdict is captured while you still remember why.

An INVALID trial **does not consume its number**: the next attempt is that same trial, retaken. So trial 3
always means the third real trial, whatever happened on the way.

### Why the trial number is editable, and why that is not a loophole

`TrialData` is static in-memory state. If the app restarts mid-session (a crash, a battery swap, a
force-stop), the counter silently drops back to 1, and the next file for a participant on their fifth trial
is labeled `trial_1`. The timestamp in the file name means nothing is overwritten and no data is lost, but
**the label is what analysis groups by**, and a wrong label is a wrong result.

So the dashboard's Identify row has a trial-number box. It is honored only from Idle, so it cannot renumber
a running trial out from under an open file. It is the only way to repair the counter without a rebuild.

### Why there is a "Mark event" button

Before it, an operator who noticed something go slightly wrong mid-trial had exactly two options: kill the
run, or remember it. Killing a run over something that might not have mattered is expensive; remembering it
does not survive twenty participants.

"Mark event" writes a `MARK:<your text>` onto a real data row and lets the trial keep recording. The moment
is now IN the data instead of in someone's memory, so the question of whether it mattered is decided later,
at analysis, with the trace in front of you. The machinery (`DataLogger.Mark`) existed all along; it was
simply not reachable from the page.

### Why battery and tracking loss get a screaming red banner, and nothing else does

These two are the failures that **leave every other light on the page green.**

- **Battery dies**: the CSV just stops, mid-trial, and the file is left in `_incomplete/`.
- **Head tracking drops** (blank sky, direct sun, a featureless wall): the AR content swims or freezes, so
  what the participant SEES is no longer what the study thinks it is showing them, and their gaze during
  that window means nothing. Meanwhile eye tracking still reports, the data link is still up, and rows go
  down at frame rate. Nothing else on the page would tell you.

Tracking loss is also written into the CSV on both edges (`TRACKING_LOST` / `TRACKING_REGAINED`), because a
banner nobody was looking at is not evidence. At analysis, weeks later, you can cut the window out.

The loudness is the design, and it is why **nothing else is allowed to use the `.critical` CSS class**. An
alarm that fires often is an alarm you learn to ignore. Tracking loss requires half a second of continuous
loss before it fires (one dropped frame is not a failure), and it never fires when there is no XR device at
all, so a desk test in the Editor does not train you to dismiss it.

### Why the dashboard HTML is in `Resources`, not `StreamingAssets`

Most guides say StreamingAssets. **On Android that is a trap.** StreamingAssets lives inside the compressed
APK and `File.ReadAllText` on it returns nothing. It works perfectly in the Editor and serves a blank page on
the device. A `TextAsset` in `Resources` is compiled in and readable from memory on both.

### Why `HttpListener` and not a package

It is in the standard library. No DLL, no third-party dependency, no package manager surprises. It was
tested on the ML2 under IL2CPP with a throwaway probe before anything was built on it, and it binds and
serves correctly. `System.Net.WebSockets.ClientWebSocket` also works there, if you need it later.

## 4. The dev panel (read-only)

Hidden behind a DEV toggle; the state persists in `localStorage`. On run day the page looks exactly as it
always did and costs exactly what it always did (the log stream is not polled with DEV off).

- **Log stream** (`/api/logs`, `/api/logtags`). `LogRing` is a bounded ring hooked to
  `Application.logMessageReceivedThreaded`, so it captures every `Debug.Log` in the process from any thread.
  It parses the `[TAG]` prefix each subsystem writes and turns those into filter buttons: this is
  `adb logcat -s Unity | Select-String "[MyTag]"`, from a phone. **That is the whole point.** On run day
  there is no PC tethered to the headset, so the normal way to answer "what is it actually doing" does not
  exist. The client polls with a monotonic cursor (`?since=N`), so lines are never fetched twice or skipped,
  and a wrapped ring reports how many lines were **dropped** rather than leaving a silent hole.
- **System panel**. Frame rate, the data link, and this project's autopilot write path.
- **Tunables** (`/api/tunables`). The whole live `DevSettingsStore` registry: key, label, section, current
  value, authored default, and whether it has been overridden. This is the honest answer to "is everything
  exposed?": if a value is not in this list, it is not reachable on device.

**Everything in the dev panel is read-only, deliberately.** The headset's own dev windows stay the single
place a value is edited, so there is one editing path rather than two that can disagree. See section 8 if
you want to change that, and read the argument there before you do.

## 5. What you must customize

**`StatusSnapshot.Build()`** is flight-specific. It reads `FlightData` and `GuidanceData` (this project's
telemetry holders). Replace those blocks with your own state. **Keep** the `trial`, `ready` and `vitals`
blocks: those are not flight-specific and they are the ones doing the safety work.

**`DataLogger.Columns`** plus the matching `WriteRow()` body. The columns array and the row are written in
the same order in one file precisely so they cannot drift apart, which is the classic way a CSV ends up
silently shifted by one column. If you add a column, add it in both places.

**`dashboard_html.txt`**: the cards are plain HTML. Delete the Aircraft and Guidance cards, add yours. The
JS reads the JSON keys directly.

**`StudyProfile`** (the asset in `Resources`, not the script): the trial gates and gaze rules.
- `useTrialReset`: turn this **OFF**. It calls into this project's X-Plane repositioning. With it off, Start
  simply opens the file and records.
- `requireCalibratedEyes`: leave ON if you use eye tracking, off otherwise. See section 7.
- `flushEveryRows`: how much of a trial is at risk in a crash. 60 rows is about a second.

**`ExperimentServer.port`**: 8080 by default.

> **Do not put these values back into Inspector fields on the scene components.** They were there once. It
> meant a dwell-threshold change cost a full APK rebuild, and, far worse, it meant **nothing recorded what
> the rules had been**. Two sessions collected weeks apart under different gaze thresholds are not
> comparable, and nothing in the data said so. `DataLogger.WriteParamHeader` now stamps every parameter into
> the CSV as `#` comment lines, so a trial file is self-describing.
>
> **Analysis note:** those `#` lines are a convention, not part of the CSV standard. Read the files with
> `pd.read_csv(path, comment='#')`. A reader that does not skip comments takes the first `#` line as the
> header row.

## 6. Network access

The server binds `http://*:8080/`, which is EVERY interface. Both of these work with no code change:

- **Phone / laptop on the same network** (wifi, hotspot, travel router): browse to the headset's IP. The app
  logs it at startup, and the dashboard shows it in the header.
  ```
  adb logcat -d -s Unity | Select-String "\[SERVER\]"
  [SERVER] Listening on port 8080. Dashboard: http://192.168.137.46:8080/
  ```
- **Tethered over USB**, no network at all:
  ```
  adb forward tcp:8080 tcp:8080
  ```
  then open `http://localhost:8080/` on the PC.

Requires `<uses-permission android:name="android.permission.INTERNET" />` in the manifest. That is all.

Multiple browsers can watch at once. Only one should be driving the buttons.

## 7. What does not work well (read this)

- **Eye calibration fails SILENTLY.** With uncalibrated eyes the Magic Leap API returns a **Success** result
  code, gaze confidence 0, and pupil data flagged invalid. It is indistinguishable from a permission failure
  by the values alone, and it produces a log file that is perfectly well formed and worthless. There is no
  way to recover it afterward. Hence: the dashboard shows a red banner, and `TrialController` REFUSES to
  record (`requireCalibratedEyes`). Run the headset's eye calibration on every participant.
- **Permission ordering matters.** The native eye tracker gates its data streams by the permissions held AT
  CREATION TIME. If you create the tracker as soon as the first permission is granted, and a second one
  (pupil size) resolves a moment later, the tracker comes up permanently blind to pupil data for that whole
  session. `EyeTracker` waits for every requested permission to settle before creating anything.
- **Button presses feel laggy**, and partly always will. The command runs on the next Unity frame, and a
  cellular hotspot adds real round-trip time. **Do not chase this.** The dashboard is not on the data path.
  Confirm actions by reading the outcome message, not by the button feeling responsive.
- **"Prefix already in use" on startup** almost always means a SECOND listener inside the same process (a
  leftover probe, or a duplicate server component across additively loaded scenes). Force-stopping the app
  does not help, because the conflict is inside the running app.
- **Phones freeze background JavaScript** when the screen locks. The dashboard handles this (it re-syncs on
  `visibilitychange`, and re-takes the screen wake lock, which the browser silently drops while hidden), but
  if you rewrite the polling, remember it, or the page will falsely report that the headset died the moment
  you unlock your phone.
- **The trial numbering is only as good as the Identify step.** Set participant and condition BEFORE the
  first trial. Start refuses without them, on purpose: a file that cannot be matched to a human being is
  unrecoverable. And if the app restarted, check the trial number.
- **`SystemInfo.batteryLevel` can return -1** on platforms that will not report it. The dashboard shows "not
  reported" rather than a fake 0%, and the low-battery banner never fires in that case. If your device
  reports nothing, that banner is simply not available to you.

## 8. Deliberately not built, and why

These were proposed and rejected. If you want them, the reasoning against is here so you can decide whether
it applies to you, rather than rediscovering it.

- **Writing tunables FROM the dashboard** (a `POST /api/tunables`). The single most tempting addition, and
  the strongest argument for it is real: for an untethered outdoor study, tuning AR placement from a phone
  without wearing the headset would be genuinely useful. It is not built because the current rule is that
  values are edited in exactly one place (the on-device dev window), and two editing paths that can fire at
  different times is precisely how a value ends up with two homes that disagree. **If you build it anyway**:
  route it through the same `ConcurrentQueue` as the trial commands (never touch `DevSettingsStore` from the
  listener thread), gate it behind a flag that is OFF by default, and **refuse it while Recording**, because
  a phone tap that changes a study parameter mid-trial silently changes what the trial measured.
- **Bulk file deletion.** See section 3. Not an oversight.
- **Voice commands.** The researcher is not the one wearing the headset, so hands-free control solves a
  problem nobody has.
- **Bluetooth keyboard triggers.** The dashboard is the primary control and a physical ArUco marker is the
  planned backup; a third input path is maintenance for no gain.
- **A "download all logs" zip.** Files are listed and downloaded individually. The authoritative pull at the
  end of a session is still `adb pull` of the whole directory, which is more reliable than a zip built on a
  headset.
- **A hardware heartbeat monitor** (an ESP32 with an LED, fed by a UDP heartbeat from the app, so a physical
  light goes red when the session dies). A genuinely nice idea for a long untethered walk where nobody wants
  to stare at a phone. Not built: it needs hardware, and the screaming banner plus the wake-lock button
  covers the same failure for the cost of nothing.

## 9. API reference

| Endpoint | Method | What |
|---|---|---|
| `/` | GET | The dashboard page. |
| `/api/status` (or `/api/telemetry`) | GET | The whole status blob: trial, vitals, readiness, eye, aoi, flight, guidance, dev. |
| `/api/files` | GET | Every CSV under `trials/`, with its folder, so `_incomplete` can be called out. |
| `/api/file?name=<rel>` | GET | Download one. Path-traversal guarded: resolves and requires the result to stay inside the trials directory. |
| `/api/ping` | GET | `ok`. |
| `/api/logs?since=<n>&tag=<T>&max=<n>` | GET | Ring-buffer log lines since a cursor. |
| `/api/logtags` | GET | The `[TAG]`s actually seen this session. |
| `/api/tunables` | GET | The live dev-settings registry. |
| `/api/trial/identify` | POST | `{participant, condition, trial?}`. Idle only. |
| `/api/trial/start` | POST | Idle only, requires identity, requires calibrated eyes. |
| `/api/trial/stop` | POST | `{valid, note}`. Files the trial under `valid/` or `invalid/`. |
| `/api/trial/pause` / `/resume` | POST | The file stays open; no rows while paused. |
| `/api/trial/redo` | POST | `{note}`. Abandons a running trial as invalid, or arms the redo flag from Idle. |
| `/api/trial/mark` | POST | `{note}`. Writes `MARK:<note>` on a real row. Recording only. |
| `/api/trial/capture` | POST | Captures the start pose (this project's sim reposition). Idle only. |

All `/api/trial/*` return **202 Accepted** on receipt. The result appears in `status.trial.message` /
`messageLevel` / `messageAge` a frame later. That is the confirmation. The 202 is not.
