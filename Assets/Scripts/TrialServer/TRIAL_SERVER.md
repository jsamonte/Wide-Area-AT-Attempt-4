# TRIAL SERVER

A small web server that turns the Magic Leap 2 into a remote control and live monitor for the visual-search
trial study. Open a browser on a phone (same network, or a PC through an adb tunnel) and you can see what the
participant is doing and drive the trials, without standing at the headset.

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
  trial number, pool, wireframe, time of day, phase, and the last command outcome.
- **`StatusSnapshot.cs`** builds the dashboard's status JSON on the main thread.
- **`LogRing.cs`** is a bounded, thread-safe ring of the app's own log lines, so the dashboard's dev view can
  show what you would otherwise only get from `adb logcat`.
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

The server is not on the data path. His gaze JSON is written by his tracker on the main thread; if HTTP
stalls, the recording is untouched and the dashboard simply shows a stale number.

## The trial workflow

The study is one sequence of a tutorial plus four numbered trials. The dashboard drives it through these
phases (tracked by the bridge, since it commands the flow):

1. **Menu** - pick one of the four sequences. The four buttons lock after the first pick, because
   `SequenceManager` only accepts a sequence once. Restart the app to choose another.
2. **Tutorial** - four gems in a cross appear and run on their own. No numbered trial is recording.
3. **Ready** - a numbered trial is queued and waiting for the researcher. The Start button lights and shows
   the trial number, time of day (Dusk for trials 1 to 2, Night for 3 to 4), and pool.
4. **Recording** - the trial is live. Targets remaining, targets destroyed, and trial time update live.
5. Back to **Ready** for the next trial, until all four are done, then **Done**.

"Mark" writes a note straight into his gaze JSON (via `EyeAndHeadTracker.LogMarker`), so the annotation sits
with the data it describes rather than in a second file.

## The one fragile point

Pool, wireframe, and time of day are read from two **private** 4x4 tables on `SequenceManager` by reflection,
because they are the only way to display them without editing his script. This is guarded: if he ever renames
those fields (`currentSequenceIndex`, `currentTrialIndex`, `sequencePools`, `sequenceWireframes`), the
pool/wireframe/time-of-day readout goes blank and a loud warning fires once, but the core control (select,
start, mark) keeps working because it never touches those fields. To make it rock-solid, add three public
getters to `SequenceManager` and read those instead.

## API

- `GET /` - the dashboard page.
- `GET /api/status` - the status JSON the dashboard polls (server, ready, trial, live, msg, logLines).
- `GET /api/logs?since=<cursor>&tag=<TAG>&max=<n>` - the dev log ring.
- `GET /api/files` - list his gaze session JSON files in the persistent data directory.
- `GET /api/file?name=<rel>` - download one session file. `GET /api/files/zip` - all of them, zipped.
- `POST /api/trial/sequence` - body is the sequence index 0 to 3.
- `POST /api/trial/start` - approve/start the queued trial.
- `POST /api/trial/mark` - body is the marker text.

Command bodies are raw (the index `2`, or the marker text), not JSON.
