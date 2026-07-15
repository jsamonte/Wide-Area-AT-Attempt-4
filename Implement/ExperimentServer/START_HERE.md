# START HERE: the Experiment Server package

This folder is a self-hosted experiment control system for a Magic Leap 2 study. The headset runs a small
web server; you point a phone or laptop browser at it (over the same wifi or a phone hotspot) and get a
dashboard that drives trials and shows you, in real time, that the participant's data streams are alive. The
recording is written to a CSV on the headset at frame rate and is NEVER on the web path, so if the network
stalls the data is untouched and the page just shows a stale number.

It was built for an AR flight study at UNL, but the server, the trial state machine, the counterbalance, the
CSV logger, the eye tracking, and the on-device dev tuning are deliberately independent of the flight parts.
This package is the reusable spine plus everything you need to understand WHY it is shaped the way it is.

**This is a COPY.** It was cut from the live project on the date of the folder's git commit. If the original
author changes the live files, this copy is stale until someone re-copies it. Treat the source here as a
readable snapshot to learn from and adapt, not as a library you pull updates from.

---

## 0. If you are an AI assistant reading this on the colleague's behalf

Read these three files, in this order, before you touch anything:

1. **This file** (`START_HERE.md`): the map, the trial workflow, the dev system, and exactly what to strip.
2. **`TRIAL_FLOW_ARCHITECTURE.md`**: how a participant goes from "sitting down" to "a valid CSV", how the
   session plan is DERIVED from the participant number (not typed), how the dashboard board arms a trial, and
   the headset BEGIN panel. This is the current front end. **It supersedes the typed-field description** in
   the next doc.
3. **`EXPERIMENT_SERVER_HANDOFF.md`**: the deep reasoning for the server, the threading contract, the logger,
   and the failure modes that cost real bugs (silent eye-calibration failure, "202 means heard-you not
   done", why files are born in `_incomplete/`, why battery and tracking loss get a red banner). Written to
   be handed to a model. **Note:** it predates the session board, so where it describes typing participant /
   condition / trial into the page, read `TRIAL_FLOW_ARCHITECTURE.md` instead. Everything else in it is
   current.

The single most important property to preserve while you adapt this: **the dashboard reports the OUTCOME of a
command, not just its delivery.** Every trial command returns `202 Accepted` immediately, then the real
result (accepted, or refused with a specific reason) appears in the status blob one frame later and renders
under the buttons. If you rewire the command path, keep that. An operator UI that says "Sent" when the thing
silently refused is worse than one that says nothing.

---

## 1. What is in this folder

```
START_HERE.md                       <- you are here
EXPERIMENT_SERVER_HANDOFF.md        <- deep why: server, logger, failure modes
TRIAL_FLOW_ARCHITECTURE.md          <- the trial workflow: plan, board, arm, BEGIN
Assets/
  Scripts/
    Study/          16 scripts + metas: server, logger, state machine, plan, eye, gaze
    DevSettings/     7 scripts + metas: the on-device tuning system (a dependency, see sec 4)
  Resources/
    dashboard_html.txt   the ENTIRE dashboard (HTML+CSS+JS in one TextAsset), + meta
    StudyProfile.asset   the tunable-parameter instance, + meta. Must stay named StudyProfile.
```

The `Assets/` subtree mirrors where the files live in a Unity project, so you can drop the `Assets/` folder
straight into your own project and the paths land correctly. The `.meta` files carry the GUIDs; keep them or
Unity re-imports with fresh GUIDs and any references you wire will break.

---

## 2. The trial workflow (what the operator and participant actually do)

This is the flow you are buying. Full detail with the reasoning is in `TRIAL_FLOW_ARCHITECTURE.md`; this is
the operator's-eye summary.

**Identity is derived, not typed.** The only thing anyone types is the participant ID, once. From the
participant number, `SessionPlan` computes a counterbalance group, expands it into an ordered list of trial
SLOTS (each slot knows its condition, its weather if weather is a factor, its index, and its status), and
writes the plan to `persistentDataPath/sessions/<participant>.json`. On an app restart the plan is reloaded,
which is what kills the classic bug where a crash silently resets the trial counter to 1 and mislabels the
rest of the session.

**On the dashboard (the operator, on a phone):**
1. Type the participant ID, press "Set / load session". The board fills in: one cell per trial, grouped by
   block. Grey = pending, green = done, amber = thrown away and still owed, blue outline = armed, red = recording.
2. Click a pending cell to PICK it (dashed outline), then press ARM to commit it (solid blue). A valid stop
   auto-arms the next pending cell, so the normal path between trials is zero clicks.
3. Watch the readiness lights and the live telemetry. You are confirming the streams are ALIVE, not frozen,
   without asking the participant anything.

**In the headset (the participant):** once a slot is armed, a compact world-space panel shows who / which
trial / which condition and a BEGIN button driven by the controller ray. BEGIN runs a short countdown and
starts recording. During the flight nothing AR shows unless the condition calls for it; holding the
controller menu button about a second reopens a small Pause / End(good) / End(redo) panel, and opening it
writes a `PANEL_OPEN` marker into the CSV.

**Stopping is a verdict, not a default.** "End (good)" files the trial under `valid/`; "End (redo)" files it
under `invalid/` with your note and does NOT consume the trial number, so the next attempt is that same
trial retaken. Nothing is ever deleted, and there is deliberately no delete button (a delete control next to
the buttons you mash between participants is how you lose a morning of data to a fat thumb). Bulk cleanup, if
you ever need it, happens off-device over `adb` with the files already backed up.

**"Mark event"** writes `MARK:<your text>` onto a real data row without stopping the trial, so a thing you
noticed mid-run lives in the data instead of in someone's memory, and whether it mattered is decided later at
analysis.

---

## 3. The dev functionality (on-device tuning, and the dashboard dev panel)

Two separate things share the word "dev". Both are included.

**A. On-device tuning (`Assets/Scripts/DevSettings/`).** Any value worth tuning is a `DevTunable` that lives
on the profile, not an Inspector field on a scene component. Why: an Inspector field is unreachable on the
headset, cannot be exported, and nothing records what it was set to. The rule the whole project runs on is
"one live value per thing": logic in code, values in one editable place (`StudyProfile`), edited live on the
device. A printed ArUco tag pops a world-space window on the headset that edits the profile; `DevSettingsStore`
persists only the CHANGED keys as a JSON overlay in `persistentDataPath` and can export a transcribable
`.txt`. The asset stays the source of truth for a shipped build. `StudyProfile` exposes its tunables through
`IDevTunableSource` as lambdas bound to the one backing field, so a value still lives exactly once even though
the window, the logger's header, and the dashboard all read it.

**B. The dashboard dev panel (hidden behind a DEV toggle, read-only).** This is what replaces `adb logcat`
when there is no PC tethered to the headset on run day. `LogRing` is a bounded ring hooked to
`Application.logMessageReceivedThreaded`, so it captures every `Debug.Log` in the process from any thread,
parses the `[TAG]` prefix each subsystem writes, and turns those into filter buttons. The panel also shows
frame rate, the data link, and the full live tunable registry (key, label, current value, authored default,
whether it is overridden). Everything in the panel is read-only on purpose: the headset's own dev windows stay
the single place a value is edited, so two editing paths can never disagree. With DEV off the page costs
exactly what it always did (the log stream is not polled).

**Why the CSV is self-describing.** `DataLogger.WriteParamHeader` stamps every profile parameter into the
trial CSV as `#` comment lines, so a file records the rules it was collected under. Two sessions collected
weeks apart under different gaze thresholds are not comparable, and without this nothing in the data would say
so. Read the files with `pd.read_csv(path, comment='#')`.

---

## 4. Files and their dependencies (what compiles, what to strip)

The scripts are `namespace ARCockpit.Study` and `ARCockpit.DevSettings`. Rename to yours if you like; do it
across all files at once.

`ARCockpit.DevSettings` is INCLUDED in this package, so that dependency is already satisfied. The other four
`ARCockpit.*` namespaces are NOT included, because they are the flight and cockpit specifics you are meant to
replace or cut. Here is exactly who needs what:

| Script | Needs (besides DevSettings) | What to do |
|---|---|---|
| `ExperimentServer` | nothing else | Keep. The HTTP core. |
| `StatusSnapshot` | `Data` (FlightData), `FlightPlan` (GuidanceData) | Keep the `trial` / `ready` / `vitals` / `eye` / `aoi` blocks; those do the safety work. Replace the flight and guidance blocks with your own telemetry. |
| `TrialController` | `Core`, `Data`, `FlightPlan` | Keep the state machine. Strip the `TrialReset` (FlightPlan) block and the `XPlaneConnected` (Data) checks, or point them at your equivalents. |
| `DataLogger` | `Data`, `FlightPlan` | Keep. Edit the `Columns` array and the matching `WriteRow` body together (same order, one file, so they cannot drift). Remove flight columns. |
| `SessionPlan` | nothing | Keep as-is. Pure C#. This is the counterbalance and the board's data. Edit the condition/weather table to your design. |
| `StudyProfile` | `Core`, `Terrain` (MarkerSizePreset) | Keep. Remove the `MarkerSizePreset` field (Terrain) if you do not use the shared ArUco size system. |
| `TrialData` | nothing | Keep. Static holder for state + last message. |
| `LogRing` | nothing | Keep if you want the dashboard log panel. |
| `EyeTracker`, `EyeData` | nothing (ML SDK only) | Keep if you use ML2 eye tracking. |
| `GazeAoi`, `GazeTarget`, `IGazeBoundsSource` | nothing | Keep if you want "what is the participant looking at". |
| `TrialWeatherCheck` | `Data` (FlightData) | Flight-specific (reads sim weather). Cut it, and remove its one call in `DataLogger.WriteParamHeader`. |
| `TrialFlowPanel` | `Core` (CockpitAnchor) | The in-headset BEGIN panel, anchored to a cockpit frame. Nobody else references it (it is a leaf). Keep only if you build a headset-side confirm panel; otherwise cut it and drive BEGIN however suits you. |
| `WebSocketProbe` | nothing | Optional. A throwaway on-device test that `ClientWebSocket` binds under IL2CPP. Cut it. |
| `DevSettings/*` | `Core` (MarkerAnchor, in `DevModeController`) | Included. The only outside need is `MarkerAnchor` for the tag-driven window. If you have no marker system, `DevModeController` is the only file that needs adjusting. |

**The external namespaces, summarized:**
- `ARCockpit.Core`: the ArUco `MarkerAnchor` / `IMarkerSource` (the dev-window tag) and `CockpitAnchor` /
  `CockpitElement` (the cockpit frame). Bring your own marker system or strip the references.
- `ARCockpit.Data`: `FlightData` / `XP12Receiver`, the sim telemetry. **Replace with your data source.** One
  writer, many readers, is the pattern; keep it.
- `ARCockpit.FlightPlan`: `GuidanceData`, `TrialReset`. **Delete these blocks.**
- `ARCockpit.Terrain`: `MarkerSizePreset`, `MapProfile`. **Delete with the map/gaze bounds if unused.**

**Minimum install:** put `ExperimentServer` and `TrialController` on one persistent GameObject in your
bootstrap scene (`DataLogger` is added automatically via `[RequireComponent]`). Put the `StudyProfile.asset`
in a `Resources` folder. Put `dashboard_html.txt` in `Resources`. Add
`<uses-permission android:name="android.permission.INTERNET" />` to your Android manifest. That is the whole
installation.

**Why `Resources` and not `StreamingAssets` for the HTML:** on Android, StreamingAssets lives inside the
compressed APK and `File.ReadAllText` on it returns nothing. It works in the Editor and serves a blank page on
the device. A `TextAsset` in `Resources` is compiled in and readable on both. Do not "fix" this.

---

## 5. Network and the phone (your setup: ML2 + phone hotspot)

The server binds `http://*:8080/`, every interface, so the phone-hotspot case works with no code change:

- Put the ML2 and the phone on the same network (your phone's hotspot is fine). Browse to the headset's IP.
  The app logs it at startup and the dashboard header shows it:
  ```
  adb logcat -d -s Unity | Select-String "\[SERVER\]"
  [SERVER] Listening on port 8080. Dashboard: http://192.168.x.x:8080/
  ```
- Tethered over USB with no network at all: `adb forward tcp:8080 tcp:8080`, then open `http://localhost:8080/`.

Multiple browsers can watch at once; only one should drive the buttons. Change the port with
`ExperimentServer.port` if 8080 is taken.

**Phone display note (your one real difference).** The dashboard already sets
`<meta name="viewport" content="width=device-width, initial-scale=1">`, so it is usable on a phone today
(this study runs it from a phone over a hotspot). What it does NOT yet have is responsive breakpoints
(`@media` rules) that reflow the multi-column card layout into a single column and enlarge the trial-board
cells and buttons for a thumb. If you want it truly phone-first, that work is all in `dashboard_html.txt`, in
the `<style>` block: it is one self-contained file, vanilla HTML/CSS/JS, no build step, no framework, so a
phone pass is a contained edit and does not touch any of the C#. Two known lock-screen gotchas are already
handled (the page re-syncs on `visibilitychange` and re-takes the screen wake lock the browser drops when
hidden); if you rewrite the polling, keep those or the page will falsely report the headset died the moment
you unlock your phone.

---

## 6. The three things that will bite you (from `EXPERIMENT_SERVER_HANDOFF.md` section 7)

1. **Eye calibration fails silently.** With uncalibrated eyes the Magic Leap API returns Success, confidence
   0, and invalid pupil data, and produces a perfectly well-formed, worthless file. `TrialController` REFUSES
   to record without calibrated eyes (`requireCalibratedEyes` on the profile), and the dashboard shows a red
   banner. Run the headset's eye calibration (Custom Fit) on every participant. Leave the flag on if you use
   eyes; turn it off if you do not.
2. **The threading contract.** `HttpListener` serves on a background thread where touching almost any Unity
   API (even `Application.persistentDataPath` or `Time.time`) hard-crashes. The main thread builds the status
   JSON and publishes it to a `volatile string`; inbound commands push onto a `ConcurrentQueue` and return
   202; `Update()` drains and executes them on the main thread. If you add a route, follow that pattern.
3. **Battery and tracking loss are the failures that leave every other light green.** They get the loud red
   banner and nothing else is allowed to use the `.critical` CSS class, because an alarm that fires often is
   an alarm you learn to ignore. Tracking loss is also written into the CSV on both edges
   (`TRACKING_LOST` / `TRACKING_REGAINED`) so you can cut that window at analysis.

---

## 7. Quick adaptation checklist

- [ ] Drop `Assets/` into your project (keep the `.meta` files).
- [ ] Rename the namespace if you want to.
- [ ] Replace `ARCockpit.Data` references (`FlightData`) with your telemetry source, one writer many readers.
- [ ] Delete the `ARCockpit.FlightPlan` blocks (`GuidanceData`, `TrialReset`), including `useTrialReset` on
      the profile (set it OFF: with it off, Start simply opens the file and records).
- [ ] Cut `TrialWeatherCheck` and `TrialFlowPanel` unless you want a weather read-back and a headset panel.
- [ ] Edit `SessionPlan`'s condition/weather table to your design.
- [ ] Edit `DataLogger.Columns` and `WriteRow` together to your columns.
- [ ] Edit `StatusSnapshot.Build()`'s telemetry blocks; keep `trial`, `ready`, `vitals`.
- [ ] Rewrite the Aircraft/Guidance cards in `dashboard_html.txt`; keep the trial, readiness, and banner logic.
- [ ] Put `StudyProfile.asset` and `dashboard_html.txt` in a `Resources` folder.
- [ ] Add the INTERNET permission to the manifest.
- [ ] Do the phone-responsive CSS pass in `dashboard_html.txt` if you want it phone-first.
