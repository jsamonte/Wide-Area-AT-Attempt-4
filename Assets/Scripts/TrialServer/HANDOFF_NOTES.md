# TRIAL SERVER: handoff notes and TODOs

Read this alongside `TRIAL_SERVER.md` (how it works), `CUSTOMIZATION_GUIDE.md` (what to change),
and `VERIFY_ON_DEVICE.md` (what to test on the headset). This file is the "state of the world" and the
to-do list: what is wired vs not, what to clean up, and the open work, in priority order.

Everything below was checked against the repo on 2026-07-22. Point Claude at this `TrialServer/` folder and
ask it to re-verify any item before acting; the repo may have moved on.

---

## 0. Integration state: the new server IS live in the shipping scene

Checked in `Assets/EyeGaze/Assets/Scenes/Application.unity` (the one enabled build scene):

- An active root GameObject named **"TrialServer"** carries the new `ExperimentServer` + `SequenceBridge`,
  both enabled, `enableWebServer: 1`, port 8080. **The new server runs.** So the dashboard, run-log file,
  perf card, and punch-list chips are all live once built (subject to the device verification in
  `VERIFY_ON_DEVICE.md`).
- The OLD server (`ARCockpit.Study.ExperimentServer` + `TrialController` + `DataLogger`) is still in the same
  scene, but on a GameObject that is **disabled** (`m_IsActive: 0`). It does not run, so there is no port
  8080 conflict. It is dead weight to be removed (section 1), not an active problem.

Two things to know about how it was placed:
- **The prefab was unpacked.** The components sit in the scene directly; `TrialServer.prefab` is not
  referenced by the scene. So editing the prefab asset will NOT update the scene copy. If you want the prefab
  to be the source again, re-create the scene object from it (or just keep editing the scene instance and
  ignore the prefab).
- **`logToFile` is not stored in the scene** (the scene was saved before that field existed), so it takes the
  code default, which is ON. If you want it explicitly recorded, open the ExperimentServer component in the
  Inspector once and re-save the scene.

Confirm the scene still has the study alongside it: `SequenceManager` + `EyeAndHeadTracker` +
`GazeInputManager`. The new server OBSERVES those; `EyeAndHeadTracker` stays the single data writer, not the
old `DataLogger`.

---

## 1. Cleaning up the old server (safe, once the scene swap above is done)

The old server lives in `Assets/Scripts/ExperimentServer/Study/` (`namespace ARCockpit.Study`). It is fully
superseded by this `TrialServer`. I checked the whole repo: **no live code references any of it.** The only
references are:
- `Application.unity` and `Trial 2.unity` place the old ExperimentServer + TrialController + DataLogger
  (removed in step 0 above).
- `Assets/Resources/StudyProfile.asset` is an instance of the old `StudyProfile.cs`, and is used only by the
  old server (no live script loads it). `RandomSpawner.cs` looks like a reference but is only the word
  "EyeTracker" inside a tooltip string, not a real dependency.

**Safe deletion order:**
1. Remove the old (disabled) Server GameObject from `Application.unity` and `Trial 2.unity` FIRST. If you
   delete the scripts while the object is still there, the scenes get harmless-but-messy "missing script"
   slots. The object is already disabled, so removing it changes no runtime behavior.
2. Then delete the whole folder `Assets/Scripts/ExperimentServer/Study/` (all 12 `.cs` + their `.meta`), and
   `Assets/Resources/StudyProfile.asset` (+ its `.meta`). The other old scripts (EyeData, EyeTracker, GazeAoi,
   GazeTarget, IGazeBoundsSource, the old LogRing, the old StatusSnapshot, TrialData) are private
   dependencies of that server and are referenced by nothing else.
3. Rebuild. If it compiles and `Application.unity` opens without missing-script warnings, the old server is
   gone cleanly.

DO NOT delete the folder before step 1, and re-run the reference check first (a `grep` for each script's
GUID across `*.unity *.prefab *.asset`) in case the repo changed. There is a second, unrelated folder,
`Assets/Scripts/ExperimentServer/DevSettings/`, that is NOT part of this and should be left alone unless you
have separately confirmed it is dead.

---

## 2. Things worth knowing on run day (verified in this repo)

- **Two permission popups on first launch, in this order:** the photos/videos one is the tracker's STORAGE
  write permission (`EyeAndHeadTracker.RequestWritePermission`); the eye-tracking one comes separately
  (`GazeInputManager` requests it in `Start()`). Accept BOTH before running anything. A denied eye permission
  now fires a red CRIT (`[GAZE:CRIT]`) on the punch list and into the run log, but the fix is still "accept
  the popup".
- **A denied eye permission looks healthy.** The app runs, the UI responds, gems just never destroy and the
  gaze data is empty. The dashboard's red "Eyes: permission DENIED" chip is the tell. This is the single most
  expensive failure to miss.
- **Getting your own debug lines on the dashboard:** log with a bracketed tag, e.g.
  `Debug.Log("[SPAWN] pool 3 placed")`. It becomes a one-tap filter in the DEV log and lands in the run log.
  Add `:CRIT` / `:WARN` / `:ERROR` in the bracket to set severity (`[SPAWN:WARN] ...`).
- **Pulling a run's log with no network (the outdoor case):** with `logToFile` on (default), the whole run is
  at `persistentDataPath/logs/run_<time>.log`. Pull it with adb:
  ```
  adb shell ls -l /sdcard/Android/data/<package>/files/logs/
  adb pull  /sdcard/Android/data/<package>/files/logs/
  ```
  Or, back on a network, it shows in the dashboard DEV files list and downloads as text. Hand that file to an
  AI to read the run.
- **The perf card is device-unverified on ML2.** Frame time / memory / storage / battery should be solid;
  the XR compositor stats (GPU/dropped frames) and Android thermal are the open questions (see
  `VERIFY_ON_DEVICE.md` section 4). Turn sampling OFF before recording a real trial.

---

## 2a. Logs without the web server: how detailed, and what to add

**Retrieval:** yes, with `logToFile` on (default) the whole run is at `persistentDataPath/logs/run_<time>.log`,
pulled with `adb pull` (no network, no dashboard needed). **One caveat:** the file is created by the
TrialServer object's `ExperimentServer.Awake`, so it only writes **in scenes that contain that object**. A
placement/test scene without TrialServer produces no file. Fix options: drop the TrialServer object into that
scene too, or add the universal logger below.

**Detail:** the file captures EVERYTHING logged via `Debug.Log/Warning/Error`, from any thread. So it is
exactly as detailed as the code's own logging, no more. Right now the placement/marker/wireframe code is
lightly logged and inconsistently tagged (mixed `[Persistence]`, `[Console]`, and untagged lines), so a
pulled run will be thin exactly where a placement rebuild needs it.

**Two ways to make it useful (both code-only, no device):**

1. **Universal logger.** A tiny self-spawning component (the pattern `PerfMonitor` already uses) that starts
   the log file and pumps the clock on its own, so EVERY scene/run writes a log regardless of the web server.
   One design decision: where its on/off + buffer size live, to keep a single config home (today those are
   fields on `ExperimentServer`).

2. **Add tagged log points to the placement code.** All in COLD paths (never per-frame `Update`, which would
   flood the file). For `WireframeAlignment.cs` specifically, the high-value points are:
   - Marker acquired / lost (in the acquire path and `EvictStaleMarkers`): `[WIRE] marker 3 acquired` /
     `[WIRE] marker 3 lost`.
   - Anchor created (`CreateAndPublishAnchorFromMarker`): `[WIRE] anchor created for ArUco 3 at (x,y,z)`.
   - **Grab-release offset edit (`OnGrabReleased`): log the final offset / rotation / scale per marker.** This
     is the standout for a MANUAL placement rebuild: every nudge the operator makes gets recorded, so the run
     log becomes the record of "what did I end up placing it at", transcribable back into the Inspector or
     diffable if it drifts.
   - Space-import permission denied (`OnPermissionDenied`, currently silent): `[WIRE:WARN] SpaceImportExport
     denied: anchors will not persist across sessions`.
   - Tag the existing features-missing error and unify the mixed tags onto one (e.g. `[WIRE]`), so it is a
     single one-tap dashboard filter.
   The same shape applies to the marker-tracking and spatial-anchor scripts if those are also in the rebuild.

---

## 3. Open TODOs, priority order

### Verify (the new server is already live in the scene)
- [ ] Build `Application.unity` and confirm the new dashboard comes up (the new server is placed and active;
      see section 0). Then walk `VERIFY_ON_DEVICE.md`.

### Cleanup (no device needed)
- [ ] Remove the old, disabled Server GameObject from `Application.unity` (+ `Trial 2.unity`), then delete
      `Assets/Scripts/ExperimentServer/Study/` and `Assets/Resources/StudyProfile.asset` (section 1).
- [ ] Once the pack is confirmed working, delete the delivery copy `THOMAS/TrialServer_Update/` (it is a
      duplicate of the applied files now; keeping it invites drift).

### Queued code work (written but unverified, or proposed)
- [ ] Verify the current unverified batch on device: the debug pack, the run-log file, the eye-permission
      CRIT, the head-tracking chip. Full steps in `VERIFY_ON_DEVICE.md`.
- [ ] (Proposed) Bridge reflection WARN: `SequenceBridge` reads the study's private tables by reflection; a
      miss silently makes the dashboard's Pool/Wireframe readout guess. A one-time WARN would surface it.
- [ ] (Proposed) Universal logger so every scene/run writes a log file without the web server (section 2a).
- [ ] (Proposed) Tagged log points in `WireframeAlignment.cs` and the marker/anchor scripts, especially the
      grab-release offset record (section 2a).

### Study-code decisions (in the study, not this server; from device findings)
- [ ] Dwell range cap: `EyeAndHeadTracker.RunEyeDwellDestruction` raycasts to infinity (gems destroy from any
      distance). Add a `maxDwellDistanceMeters` if the rule is ~3 m.
- [ ] Dwell grace window: the dwell timer resets on a single off-target frame (a blink restarts the 4 s). A
      ~0.15 s grace would make it forgiving.
- [ ] Decide which clock rules the trial time limit (page excludes paused time; the gaze JSON does not). See
      `CUSTOMIZATION_GUIDE.md` section 2b.
- [ ] Decide on a setup/confirm phase before the tutorial auto-starts, and whether to disable the wireframe
      baselines until the first trial. See `TRIAL_SERVER.md` device findings.

---

## 4. What each doc in this folder is for
- `TRIAL_SERVER.md` - how the server works (install, workflow, API, fragile reflection points, device findings).
- `CUSTOMIZATION_GUIDE.md` - every value you can change and the choices you have to make.
- `VERIFY_ON_DEVICE.md` - the on-device test punch list for the current unverified batch.
- `HANDOFF_NOTES.md` - this file: integration state, cleanup, and the TODO list.
