# TRIAL SERVER

A small web server that turns the Magic Leap 2 into a remote control and live monitor for the visual-search
trial study. Open a browser on a phone (same network, or a PC through an adb tunnel) and the operator can see
what the participant is doing and drive the trials, without standing at the headset.

It is a **wrapper, not a replacement**. `SequenceManager` stays the trial driver and `EyeAndHeadTracker` stays
the single source of the recorded data. This server only observes and issues commands. It adds no second
logger, no second eye tracker, no second copy of any value.

## The one-drag install

`TrialServer.prefab` is one GameObject with two components. Drop it into any scene that has the study
(`SequenceManager` + `EyeAndHeadTracker` + `GazeInputManager`) and it works: the bridge self-finds
`SequenceManager` at runtime, so nothing needs wiring in the Inspector. Remove the scene's old `Server`
object (the previous TrialController + DataLogger + ExperimentServer) so two servers do not fight over the
port.

The `INTERNET` permission is required and is in `Assets/Plugins/Android/AndroidManifest.xml`.

## Access

    adb forward tcp:8080 tcp:8080
    # then open http://localhost:8080/ in a browser

Or, if the headset has a wifi address, browse to `http://<headset-ip>:8080/` directly (the address is printed
to the log at startup as `[SERVER] Listening ...`).

## The files

- **`ExperimentServer.cs`** owns the socket, the status snapshot, and the command queue. It serves the
  dashboard, the status JSON, the dev log, and the gaze session files. It is the only networking piece.
- **`SequenceBridge.cs`** is the only piece that touches the study. It turns the commands the server accepts
  into clicks on `SequenceManager`'s existing buttons, and tracks the flow phase so the dashboard can show it.
- **`ServerState.cs`** is the one static holder the bridge writes and the snapshot reads: selected sequence,
  trial number, pool, wireframe, time of day, phase, the trial time limit, the bad-trial flags, and the last
  command outcome.
- **`StatusSnapshot.cs`** builds the dashboard's status JSON on the main thread.
- **`LogRing.cs`** is a bounded, thread-safe ring of the app's own log lines, so the dashboard's dev view can
  show what would otherwise only come from `adb logcat`.
- **`Resources/trialserver_dashboard.txt`** is the dashboard, one self-contained HTML/CSS/JS TextAsset. The
  resource name is deliberately unique so it cannot collide with the old flight dashboard's `dashboard_html`.

## The threading contract (load-bearing)

`HttpListener` callbacks run on a background thread, and touching any Unity API from there crashes. So:

- The listener thread touches **nothing** of Unity's.
- Outbound: the main thread builds the status JSON in `Update` and publishes it to a volatile string. The
  listener thread only hands out that finished string.
- Inbound: a request enqueues a command and returns **202 Accepted** immediately. `Update` drains the queue
  and runs the command on the main thread. A 202 means "heard you", not "done". The dashboard confirms by
  watching the phase change, which is also what proves the trial machinery actually ran.

The server is not on the data path. The gaze JSON is written by the study's tracker on the main thread; if
HTTP stalls, the recording is untouched and the dashboard simply shows a stale number.

## The trial workflow (operator arms, participant starts)

The study is one sequence of a tutorial plus four numbered trials. The dashboard drives it through these
phases (tracked by the bridge, since it commands the flow):

1. **Menu** - pick one of the four sequences. The four buttons lock after the first pick, because
   `SequenceManager` only accepts a sequence once. Restart the app to choose another.
2. **Tutorial** - picking a sequence starts the study's tutorial IMMEDIATELY: four practice gems in a cross,
   and the tracker starts recording with it. That is the study's own design, and the dashboard shows it as
   `TUTORIAL (not a trial)` so it is never mistaken for one.
3. **Ready** - a numbered trial is queued. The bridge holds the device's Start button **disabled** here, so
   the participant cannot start before the operator approves.
4. **Armed** - the operator pressed "Arm trial" on the dashboard, which re-enables the device Start button.
   The PARTICIPANT starts the trial on the device; the dashboard never starts one. The bridge notices the
   start by watching the tracker's trial clock reset.
5. **Recording** - the trial is live. Targets remaining, targets destroyed, and trial time vs the time limit
   update live. **Pause / Resume** drive the tracker's own `PauseRecording` / `ResumeRecording`. The page's
   trial clock FREEZES while paused and excludes paused time afterward (device-verified). **OPEN DECISION**:
   the tracker's own internal clock (the timestamps in the gaze JSON) does NOT exclude pauses, it measures
   from the trial's start moment. So after a pause the page and the JSON disagree about elapsed time. Decide
   which clock the trial rules (the 20-minute limit) should follow; the page currently excludes paused time.
   **End trial** ends the set early through the study's own end-of-trial path, after writing a `MANUAL_END:`
   marker into the gaze JSON.
6. Back to **Ready** for the next trial, until all four are done, then **Done**.

Other controls:

- **Mark** writes a note straight into the gaze JSON (via `EyeAndHeadTracker.LogMarker`), so the annotation
  sits with the data it describes rather than in a second file.
- **Flag BAD** writes a `TRIAL_INVALID: Trial N: <reason>` marker into the gaze JSON. While a trial runs the
  flag names that trial; between trials it names the one that just finished. The JSON is the authoritative
  record; the dashboard's list is only the session tally.
- The **trial time limit** (default 20 min, `trialLimitMinutes` on the `SequenceBridge` component) is display
  only: over the limit the dashboard shows a red banner, and the operator decides whether to end the trial.
  Nothing auto-ends.

## The fragile points (both guarded, both reflection)

1. Pool, wireframe, and time of day are read from two **private** 4x4 tables on `SequenceManager`, because
   that is the only way to display them without editing the study's script. If those fields are ever renamed
   (`currentSequenceIndex`, `currentTrialIndex`, `sequencePools`, `sequenceWireframes`), the condition readout
   goes blank and one loud warning fires; control is unaffected.
2. **End trial** invokes the private `SequenceManager.OnTrialFinished()` by name, which is the study's own
   end-of-trial path (pause recording, clear the spawned gems, advance, show the menu). If it is renamed, the
   End command is refused with a clear message; everything else keeps working. Ending the TUTORIAL early
   leaves its four practice gems visible (they are spawned directly, not through the spawner the end path
   clears); they go away when the first trial spawns.

Public getters / a public `EndTrialEarly()` on `SequenceManager` would remove both reflections if that trade
is ever wanted.

## API

- `GET /` - the dashboard page.
- `GET /api/status` - the status JSON the dashboard polls (server, ready, trial, live, badTrials, msg, logLines).
- `GET /api/logs?since=<cursor>&tag=<TAG>&max=<n>` - the dev log ring.
- `GET /api/files` - list the gaze session JSON files in the persistent data directory.
- `GET /api/file?name=<rel>` - download one session file. `GET /api/files/zip` - all of them, zipped.
- `POST /api/trial/sequence` - body is the sequence index 0 to 3. Starts the tutorial by the study's design.
- `POST /api/trial/arm` - approve the queued trial (re-enables the device Start button).
- `POST /api/trial/start` - bring-up backdoor: starts an ARMED trial from HTTP. Nothing on the page calls it.
- `POST /api/trial/end` - end the running set early (writes `MANUAL_END:` first).
- `POST /api/trial/pause` / `POST /api/trial/resume` - pause/resume the tracker's recording.
- `POST /api/trial/bad` - body is the reason; flags the current/last trial invalid (`TRIAL_INVALID:` marker).
- `POST /api/trial/mark` - body is the marker text.

Command bodies are raw (the index `2`, or the marker text), not JSON.

## Notes for the study's maintainer

Findings from the first device test that live in the study's own code, not in this server:

- **Eye tracking permission is the whole game, and its denial looks healthy.** `GazeInputManager` requests
  `EYE_TRACKING` in its `Start()`; until the popup is accepted, `EyeTrackingPermissionGranted` stays false and
  both dwell-destroy scripts (`GazeDestroyFeature`, `GazeDestroyTimed`) return without doing anything. The app
  still runs, the UI still responds, so "gems will not destroy" plus "dashboard says eye permission DENIED"
  are the same single fact. Two popups appear on first launch: the photos/videos one is the tracker's
  STORAGE write permission (`EyeAndHeadTracker.RequestWritePermission`), and the eye tracking one comes
  separately. Accept both before running anything. The dashboard now shows a red banner while the eye
  permission is missing.
- **Prerequisites for gaze hitting gems**: the eye permission above, the gems on the layer named by
  `RandomSpawner.targetLayer` with colliders, and the scene's ArUco markers scanned so the content is placed
  where the participant actually is. If dwell-destroy does nothing, check those in that order (the dashboard's
  Readiness card covers the first).
- **The wireframe baselines show before the first trial.** Nothing turns the four `pool*_9Baseline` renderers
  off at startup; they are first set on the first Start click (`SequenceManager.OnStartButtonClicked`). So
  whatever the scene saved is visible during the menu and tutorial. One-line fix if unwanted: disable all four
  in `SequenceManager.Start()`.
- **Getting a debug line onto the dashboard**: log it with a bracketed tag, e.g.
  `Debug.Log("[SPAWN] pool 3 placed");`. The dashboard's DEV view shows the stream and builds a filter button
  per tag automatically, so `[SPAWN]` becomes a one-tap filter. This replaces `adb logcat` on run day.
- **Eye confidence / calibration signals are not surfaced yet.** The snapshot's `ready` block only reports
  permission and component presence. If a "calibration worked" signal is wanted on the dashboard,
  `GazeInputManager` (or the tracker) must expose a confidence value publicly; then it is one line each in
  `StatusSnapshot.Build` and the dashboard's Readiness card.

### Findings from the second device test (2026-07-16)

- **Gems can be destroyed from ANY distance; there is no range cap.** The dwell raycast in
  `EyeAndHeadTracker.RunEyeDwellDestruction` is `Physics.Raycast(..., Mathf.Infinity, layersToIncludeWithRay)`.
  If the intended rule is ~3 m, add a serialized `maxDwellDistanceMeters` on `EyeAndHeadTracker` and either
  pass it as the raycast length or reject hits with `hitInfo.distance > max`. (Note the two OLD scripts,
  `GazeDestroyFeature` / `GazeDestroyTimed`, used a 10 m cap, but both auto-disable themselves in Awake as
  obsolete; the live logic is the tracker's, and it has no cap.)
- **Dwell feels unreliable, and distance makes it worse.** Three compounding causes in the current logic:
  1. The dwell timer resets to ZERO on any single frame off the target. There is no grace window, so one
     frame of eye jitter or a blink restarts the whole 4-second dwell.
  2. `GazeInputManager.Update` keeps the LAST gaze pose whenever the device reports not-tracked, rather than
     invalidating it. During a tracking dropout the ray silently sticks at a stale direction.
  3. No range cap (above) plus small targets: at several meters a gem subtends a tiny angle, so normal gaze
     noise constantly exits the collider and trips cause 1. Capping the range and/or adding a short
     off-target grace (e.g. hold the timer for 0.15 s before resetting) would make dwell far more forgiving.
- **Tutorial gems would not destroy (trial gems did).** Not conclusively diagnosed; ranked suspects, all in
  the study's code, since the tutorial spawn path (`SequenceManager.StartTutorialPhase`) differs from the
  trial path (`RandomSpawner.SpawnAndConfigure`):
  1. Layer on the ROOT only: both paths set `layer` on the root GameObject, which does NOT propagate to
     children. If `tutorialTargetPrefab`'s collider sits on a child whose authored layer is not in the
     tracker's `layersToIncludeWithRay`, the dwell ray never hits it. Check whether the assigned tutorial
     prefab is the same prefab (or an identically layered one) as `RandomSpawner`'s gem.
  2. The tracker's `RefreshTargetList` keeps only objects whose tag is `DwellDestroyTarget` AND whose
     `MeshRenderer` is enabled at that moment. The tutorial spawn logs
     `RefreshTargetList: Found N objects` right after spawning; if it says 0 (or 4 plus leftovers), that is
     the answer. That log line is visible from the dashboard's DEV stream.
  3. The per-frame `eyeRaycastHitObject` field in the gaze JSON records what the eye ray actually hit,
     ignoring layers. During a failed tutorial dwell it shows whether the ray reaches the gem at all (a layer
     problem) or hits it without destroying (a target-list problem).
- **The sequence flow itself is worth a design conversation.** Picking a sequence immediately starts the
  tutorial and its recording; there is no separate "everything is scanned and ready, now begin" step, and a
  mis-tap on the sequence buttons cannot be undone without restarting the app. A better-defined setup phase
  (select, confirm readiness, then explicitly begin the tutorial) would make run-day operation safer. To be
  discussed; not changed here because it is the study's flow, not the server's.
