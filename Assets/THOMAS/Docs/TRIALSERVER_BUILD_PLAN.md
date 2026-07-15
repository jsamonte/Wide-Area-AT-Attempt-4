# TrialServer Build Plan (fresh-session handoff)

Read this first. It carries every decision and every fact needed to build the trial-management web server into
this project WITHOUT re-reading the codebase. Written 2026-07-15 at the recon to build boundary, right before a
context clear. Thomas's working rules are in `Assets/THOMAS/Docs/CLAUDE.md` (his own project's manual, referenced
for HOW he likes to work; the flight specifics in it do NOT apply here). His TODO style is in `TODO.md` beside it.

## What this is
Thomas is a guest in a colleague's Unity Magic Leap 2 project (`Wide-Area-AT-Attempt-4`). He is porting his own
trial-management web server into it so the colleague can (1) DEBUG his study live from a phone as he preps for
trials and (2) RUN the trials from that dashboard. The colleague's study is a visual-search AR task: participants
destroy "gems" by eye dwell.

## Locked decisions
- **Lean WRAP, not replace.** The colleague's `SequenceManager` stays the trial driver; his `EyeAndHeadTracker`
  (JSON logger) stays the single source of data. The server is a remote control + live monitor only. Do NOT add a
  second logger, second eye tracker, or second counterbalance. That would violate his one-source-of-truth rule.
- **Namespace `TrialServer`** (was `ARCockpit.*`).
- **Leave his real `Trial 2.unity` ALONE.** (Thomas's explicit choice, and an auto-safety classifier also blocks
  editing it.) All scene work happens in an isolated COPY under `Assets/THOMAS/Scenes/`. The old in-project server
  folder `Assets/Scripts/ExperimentServer/` (ARCockpit namespace, wired into the real Trial 2) is left in place as
  cruft so his scene does not break; Thomas removes it himself later. Do NOT delete it, do NOT touch real Trial 2.
- **Command approach A (non-invasive):** the bridge drives `SequenceManager` by invoking its existing UI Buttons'
  `onClick` on the main thread. No edits to his scripts.
- **Deliverable that STAYS:** `Assets/Scripts/TrialServer/` (code + prefab + `TRIAL_SERVER.md` README) and the
  reusable prefab. **Scratch that gets DELETED at the end:** `Assets/THOMAS/` and `Implement/`. The one doc Thomas
  wants to live permanently in his project is a README for the server architecture + trial workflow, placed next
  to the code as `Assets/Scripts/TrialServer/TRIAL_SERVER.md`.

## His study + data model (so you do not re-read his code)
- **`SequenceManager.cs`** (the current driver, in Trial 2). Flow: scan ArUco markers -> operator picks one of 4
  Sequences -> a Tutorial phase (4 gems in a cross) -> 4 researcher-approved trials -> done. Conditions per trial
  come from two private 4x4 tables: `sequencePools[seq,trial]` (pool 1-4) and `sequenceWireframes[seq,trial]`
  (wireframe overlay on/off). Trials 0-1 are "Dusk", 2-3 are "Night". Public fields you can read/use:
  `spawner`, `tracker`, `sequenceButtons[]` (4), `sequenceButtonTexts[]`, `startButton`, `startButtonText`,
  `mainInstructionsObject`, `tutorialTargetPrefab`, `pool1..4_9Baseline`. PRIVATE (not readable without
  reflection): `currentSequenceIndex`, `currentTrialIndex`, `sequenceComplete`, `isTutorialPhase`, the two tables.
  Buttons: `sequenceButtons[i]` picks sequence i (wired in his Start()); `startButton` starts/approves the current
  trial. Invoking `Button.onClick.Invoke()` works even when the button is hidden (SetActive false) and routes
  through his own guards, so it is a safe remote click.
- **`EyeAndHeadTracker.cs`** (his JSON logger; `Instance` singleton). PUBLIC API to read non-invasively:
  `GetTargetsDestroyed() -> int`, `GetCurrentTrialTime() -> float` (0 when not recording),
  `GetRemainingTargetPositions() -> List<Vector3>` (use `.Count` for targets remaining), the public UnityEvent
  `OnAllTargetsDestroyed` (subscribe to detect trial end), and `LogMarker(string)` (writes a marker into his JSON;
  use this for the dashboard "Mark event" so notes land in HIS data, not a second file). PRIVATE: `currentTrialName`,
  `isRecording`, `participantId`, `condition`. Recording state can be inferred from `GetCurrentTrialTime() > 0`.
- **Eye-tracking liveness:** `GazeInputManager.Instance != null && GazeInputManager.Instance.EyeTrackingPermissionGranted`.
  Also `GazeInputManager.Instance.GazePosition` / `.GazeRotation` if you want a live gaze hit later.
- **`RandomSpawner.cs`:** spawns the gems; `PoolSelection` enum {Pool1..4}; not needed by the server directly.

## The 5 files to build in `Assets/Scripts/TrialServer/` (namespace `TrialServer`)
Base them on the pristine copies in `Implement/ExperimentServer/Assets/Scripts/Study/` (or the in-project
`Assets/Scripts/ExperimentServer/Study/`, same content). Keep the HTTP threading contract EXACTLY (it is
load-bearing: listener thread touches NO Unity API; main thread publishes a volatile snapshot string; inbound
commands enqueue and return 202, drained in Update on the main thread).

1. **`ExperimentServer.cs`** (adapt the existing 440-line core). Remove its coupling to the cut classes:
   - `StudyProfile` -> replace `SnapshotHz`/`serverLogRequests`/`devLogBufferLines` with plain serialized fields
     `snapshotHz` (default 10), `logRequests` (default false), `logBufferLines` (default 800).
   - `DataLogger.TrialsDir` -> point the file endpoints at `Application.persistentDataPath` and list `*.json`
     (his gaze session files), cached in Awake. This lets the operator download his session data from a browser.
   - Remove the `/api/tunables` + `/api/logtags` tunables machinery and all `DevSettingsStore` references. Keep
     `/api/logs` + the DEV log panel (that is `LogRing`, high value for debugging). Keep `OnCommand` + the
     `/api/trial/*` command queue.
2. **`LogRing.cs`** reuse verbatim, only change the namespace. Self-contained (a bounded ring on
   `Application.logMessageReceivedThreaded`). `ExperimentServer` installs/pumps it.
3. **`ServerState.cs`** a lean static holder (replaces the flight `TrialData.cs`): the safe main-thread ->
   HTTP-thread hand-off plus the operator-feedback channel. Fields the bridge writes on the main thread and
   StatusSnapshot reads: e.g. `SelectedSequence` (1-4, 0 = none), `TrialNumber`, `TotalTrials`, `Pool`,
   `Wireframe` (bool), `TimeOfDay` (string), `Phase` (Menu/Tutorial/Recording/Done), plus `LastMessage`,
   `LastMessageLevel` ("ok"/"warn"/"error"), `LastMessageAt`, and a `Report(msg, level)`. Keep the "report the
   OUTCOME of a command, not just its delivery" property: every command result updates LastMessage.
4. **`StatusSnapshot.cs`** new. `static Build(ip, port)` returns the JSON the dashboard polls. Read on the MAIN
   thread only (ExperimentServer already calls it from Update). Blocks: `server` (ip/port), `ready` (eye-tracking
   permission granted, tracker present, GazeInputManager present), `trial` (from ServerState: sequence, trial num,
   pool, wireframe, time-of-day, phase), `live` (targets remaining via
   `EyeAndHeadTracker.Instance.GetRemainingTargetPositions().Count`, destroyed via `GetTargetsDestroyed()`, trial
   time via `GetCurrentTrialTime()`), and `msg` (LastMessage + level + age). Include your own small JSON string
   helpers (F for floats invariant-culture, Esc for strings); do not pull a JSON lib.
5. **`SequenceBridge.cs`** new MonoBehaviour. In Start: `FindObjectOfType<SequenceManager>()` (cache it),
   subscribe to `ExperimentServer.OnCommand` and to `sequenceManager.tracker.OnAllTargetsDestroyed`. Command
   handler (runs on main thread via the queue) parses `/api/trial/...`:
   - `/api/trial/sequence` body = index 0-3 -> `sm.sequenceButtons[i].GetComponent<Button>().onClick.Invoke()`,
     set `ServerState.SelectedSequence`, `Report("Sequence i+1 selected")`.
   - `/api/trial/start` -> `sm.startButton...onClick.Invoke()`, `Report("Trial started")`.
   - `/api/trial/mark` body = text -> `sm.tracker.LogMarker(text)`, `Report("Marked: text")`.
   Track `TrialNumber`/phase itself (it commanded the flow); increment on `OnAllTargetsDestroyed`. For pool /
   wireframe / time-of-day display, read `SequenceManager`'s private tables via GUARDED reflection (try/catch,
   log a LOUD warning ONCE if it fails, never fail silently). This is the one fragile point: if his field names
   change it stops showing pool/wireframe (but not the core control). If Thomas later wants it rock-solid, offer 3
   public getters on SequenceManager (edits his file, he declined that for now).
   Refuse commands with a clear `Report(reason, "error")` when `sm == null` or the flow is Done.

## Second batch (after the 5 scripts compile)
- **`Assets/Scripts/TrialServer/Resources/dashboard_html.txt`** rebuilt from his existing dashboard. KEEP his
  trial/readiness/banner logic, the `visibilitychange` re-sync, and the screen wake-lock re-take. REPLACE the
  flight cards with: a 4-button Sequence selector, the current trial line (Trial N of M, Pool, Wireframe on/off,
  Dusk/Night), targets remaining/destroyed + trial time, an "Approve / Start trial" button, a "Mark event" box,
  the LastMessage outcome area, and a DEV toggle showing the LogRing stream + the downloadable session JSON list.
  One self-contained HTML/CSS/JS TextAsset, no framework. Resources (not StreamingAssets): on Android
  StreamingAssets in the APK reads empty.
- **Prefab `Assets/Scripts/TrialServer/TrialServer.prefab`:** one GameObject "TrialServer" with `ExperimentServer`
  + `SequenceBridge`. The bridge self-finds `SequenceManager`, so this is a true one-drag add to any scene. That
  is the "future scenes need only minor changes" property Thomas wants.
- **Working scene `Assets/THOMAS/Scenes/TrialServer.unity`:** duplicate the real `Assets/EyeGaze/Assets/Scenes/Trial 2.unity`
  (copy the .unity; give it a fresh .meta guid). In the COPY only, remove the OLD `Server` object and add the new
  prefab. The old Server is one root GameObject `&110390440` named "Server" with components `&110390441`
  (TrialController), `&110390442` (DataLogger), `&110390443` (ExperimentServer), Transform `&110390444`. To excise
  from the copy: delete the YAML block from `--- !u!1 &110390440` up to (not including) `--- !u!1 &173726637`;
  remove the PLUME `SceneGuidRegistry` entry pairs (`- object: {fileID: X}` + its `guid:` line) for fileIDs
  110390440/441/442/443/444; remove the SceneRoots line `- {fileID: 110390444}`. (A tested Python approach for
  this is in the git history of this session; it was run against the real scene and blocked by the classifier, so
  run it against the COPY instead.)
- **`INTERNET` permission:** confirm `<uses-permission android:name="android.permission.INTERNET" />` is in
  `Assets/Plugins/Android/AndroidManifest.xml`; add if missing.
- **`TRIAL_SERVER.md`:** the one permanent README (architecture + trial workflow) next to the code.

## Constraints / gotchas
- **You own serialized-file editing** (scenes/prefabs/.meta directly, Force Text). **Never build or deploy or adb**
  install: that is Thomas's step. A change is "written, not verified" until he builds it.
- URP only (ML2 is Android mobile-class). No `UnityEditor` API in runtime scripts. Must run in Editor without a
  device. No em dashes anywhere. American English only. One source of truth (no duplicated values).
- **End every response with Thomas's "Context Status" block** (format in `CLAUDE.md`, drop lines that do not earn
  their place; be blunt in Pace; put by-hand action items in "Your TODO", not prose).
- **LCCSDK:** `com.xgrids.lccsdk` is a local `file:` dependency at `C:\Users\yujnkm\Documents\LCCSDK(v2.0.14)\`,
  outside the repo. It is restored and the project compiles. Do not delete the `Assets/Samples/LCC SDK/` scripts.
- `GazeInputManager` is the colleague's eye-input singleton (referenced by `EyeAndHeadTracker`); assume it exists.

## Execution order
1. Create `Assets/Scripts/TrialServer/` and write the 5 scripts (namespace `TrialServer`). Tell Thomas to build.
2. After it compiles clean: `dashboard_html.txt`, then the prefab, then the duplicated+cleaned scene, then the
   manifest check, then `TRIAL_SERVER.md`.
3. Thomas builds + deploys and drives the dashboard from a phone; iterate against real device behavior.
