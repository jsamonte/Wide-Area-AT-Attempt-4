# TODO / Daily Status

Rolling status and handoff. **Read this first** at the start of a session. Update it at the end of each
working day: move finished items to "Done recently", reprioritize "Next up", keep "Known issues" current.
Operating rules live in `CLAUDE.md`; the why in `PROJECT_PLANS.md`; subsystem designs in the `*_ARCHITECTURE.md`
files.

_Last updated: 2026-07-15._

## TOMORROW'S FOCUS (set 2026-07-14 EOD). READ THIS FIRST

The through-line Thomas set: get EVERYTHING tunable and PLACEABLE on device so a setup can be saved to a
finalized state, THEN lock the trial workflow, THEN EEG. Comms/twin/PLUME are later. Do device-savable
placement + options before anything else.

**1. Make the trial BEGIN window a placeable element, like the dev windows (TOP TASK).**
   Today the `TrialFlowPanel` BEGIN prompt offsets from the 240/241 frame via an Inspector field only
   (`TRIAL_FLOW_ARCHITECTURE.md` section 6 admits this). Make it behave exactly like a `CockpitElement`:
   - Under **240 (Placement)** it is VISIBLE and MOVEABLE (nudged live from a dev window, same feel as
     placing the map / NavBall), and its pose is captured relative to the cockpit frame.
   - The **240 -> 241 swap SAVES/locks** its position (the `CockpitAnchor` already calls `DevSettingsStore.Save()`
     on that edge; ride it).
   - Under **241 (Deploy)** it spawns at the saved pose for the actual trial run.
   - Its placement (offset X/Y/Z, facing) becomes a `DevTunable` group so it exports/saves/loads like every
     other knob. This is the "no Inspector-only values" rule applied to the panel.

**2. Consolidate the dev windows onto 230.** Make 230 the single "overall" dev window that exposes the
   flight-path knobs (`FlightPlanProfile`) AND the study knobs (currently the 231 / `StudyProfile` window,
   gaze rules etc.) AND the new trial-BEGIN-window placement from task 1. DECISION for tomorrow: truly merge
   the windows into one, or keep them separate but add the trial-panel placement tunables? Confirm first what
   231 is today (it is the StudyProfile / gaze-rules window) before merging. Recommend: add the trial-panel
   placement to a window first (small, high value), decide the full 230/231 merge second (bigger, more churn,
   risks re-tuning what already works, see the "no churn" rule).

**3. Everything savable on device.** The point of 1 and 2 is that a full cockpit setup (element positions +
   trial panel position + options) can be placed once on device, Exported/Saved, and reloaded, so we reach a
   finalized setup without re-placing every session.

**Then, in order (not tomorrow unless 1-3 land):**
- **Finalize the trial workflow** (the 07-14 trial-flow batch still needs its device pass, see below).
- **EEG** (Tier 3 item 11): UDP receiver + cross-device sync markers. Starts after the trial workflow is locked.
- **Gaze over the cockpit** (physical-panel AOIs, Tier 3 item 10): Thomas's idea is to PLACE the AOI
  boxes/balls himself by timed gaze (look at an instrument for N seconds to drop/size its AOI), NOT a formal
  per-USER calibration. He sets it up once; participants do not calibrate for this. Cheaper and probably
  enough. Confirm it holds up.

**Backburner / reassess (do NOT sink time here now):**
- **Digital twin:** left mid-build (code-crops the room scan to the cockpit + fake-shaded, see the twin
  section below). Thomas will reassess tomorrow; do not over-invest. It is a setup aid, not a deliverable.
- **WiFi comms instead of USB:** Thomas is worried the USB cable moving mid-session causes issues. Moving the
  adb tunnel / dashboard to WiFi would decouple it. "Shouldn't be too terrible." Backburner until the setup
  and trial workflow are locked.
- **PLUME:** Thomas expects to use PLUME (Unity XR behavioral/physiological recording + replay) "in some
  way", most likely for the record/replay and EEG+gaze data path. Investigate what it buys us before building
  any custom recording. Ties to the trial-playback open question.

**Good enough, stop polishing:** the trial web dashboard. Thomas: "looks good enough with quite a bit for
debugging." No more work on it unless something is broken.

## NEW WORK QUEUED 2026-07-15 (not yet started)

- **Set the trial counts on `StudyProfile`:** `trialsPerBlock = 1`, `practiceTrials = 1`. One-line change, one
  source of truth. Waiting on Thomas's go, or do it with the next profile edit.
- **Route is too tight for the Cirrus. Make it LONGER / looser.** Each condition is a single flight now, so
  the route can be longer. Open work item; needs Thomas in the flight-path dev tooling.
- **Practice free-flight tail:** the practice flies the real route first (route familiarization), then a ~5 min
  UNSCORED free-flight window (climb, play with AR elements, crashing is fine). Design need: the practice
  needs a "free mode" tail that is not scored and does not require staying on-route or keeping HDG/VS engaged,
  unlike a real trial. Not built.
- **NavBall data-source "packages" (dev-window checkboxes). PLAN AGREED 2026-07-15, not built.**
  Let the operator pick which source drives the NavBall. Decided scope: three sources, ONE selector for the
  whole element (not per-readout).
  - Sources: **True physics** (what `FlightData` holds today, per its own header), **Instrument/indicated**
    (what the physical G1000 shows), **Flight Director commanded** (`FdPitchDeg`/`FdRollDeg`, already present).
  - `NavBallProfile` gets one `Source` enum field, surfaced on its dev window as three MUTUALLY EXCLUSIVE
    checkboxes. Each is a `DevTunable.B` whose get/set read+write the one enum field (checking one unchecks
    the others), so there is a single backing value that exports/saves like every other knob. One source of truth.
  - PLUMBING: instrument/indicated datarefs are NOT polled today, so add a second read-only set to
    `XP12Receiver` + `FlightData` (indicated airspeed, baro altitude, AHRS pitch/roll, indicated heading/HSI).
    Confirm exact names against `Assets/Reference/XPlane/XP12_DataRefs_12.1.3.csv` (v1 API) BEFORE wiring.
    Reads only; the X-Plane write guardrail is untouched. These are distinct real signals, not duplicates of
    the true values, so both legitimately live in `FlightData`.
  - Add a shared resolver (e.g. `NavBallSource.PitchDeg` / `.RollDeg` / ...) that the ~6 NavBall modules call
    instead of reading `FlightData` directly, switching on the selected source in ONE place.
  - OPEN WRINKLE (needs Thomas): FD only provides commanded pitch/roll, no heading/VS/AoA. Under the FD
    package, plan is the attitude globe shows the FD command and the readouts FD cannot supply fall back to
    true values. Confirm that fallback is acceptable.
  - Ties to [[study-represents-existing-instruments]]: instrument values are arguably the study-faithful source.
- **Map tracking mode (dev-window option). PLAN AGREED 2026-07-15, not built.** Build this FIRST (small,
  self-contained). Add a `MapProfile` enum tunable **Track Mode: North-up | Heading-up**.
  - NORTH-up = today's behavior, unchanged (terrain fixed, only the ownship symbol banks/moves).
  - HEADING-up = counter-rotate the map root by heading (reuse the smoothed track already in `MovingMap`,
    the `_smTrack` field) so the nose stays pointing up and the ownship symbol sits fixed.
  - One rotation branch in `MovingMap`, rides the existing dev window + export/save.

## Questions for the professor (keep this current, raise at the next meeting)

- **Trial recording + playback:** does he want trials RECORDED and PLAYED BACK with the physical cockpit or
  the digital twin in view? This decides whether we build a replay mode (and whether PLUME is the tool). The
  per-trial CSV already logs pose + gaze, so replay is feasible without new capture.
- **Gaze AOI calibration:** confirm that self-placed, timed-gaze physical-panel AOIs (no per-participant
  calibration) are scientifically acceptable for the "AR pulled gaze off the physical PFD" claim.
- **Study design still-open values:** weather as a factor (`study.weatherIsAFactor`) still open. RESOLVED
  2026-07-15: it is **1 practice + 1 flight per condition = 4 recorded trials** (`trialsPerBlock = 1`,
  `practiceTrials = 1`), NOT the ~20-flight 2x2. Practice is easy (fair weather, daytime) and has a free-flight
  tail (see the new-work block below).
- **EEG sync:** is the EEG rig's own clock plus a post-hoc alignment marker enough, or do we need live
  cross-device sync markers? Answer removes or keeps the EEG UDP receiver work.

## TRIAL FLOW: built 2026-07-14, all six device bugs FIXED IN CODE 2026-07-14, needs a device pass (START HERE)

A whole trial-flow front end was written on 2026-07-14 across a long session, then the six confirmed device
bugs were fixed later the same day (see the numbered list below, each marked DONE/written). None of the fixes
has been on device yet: build, deploy, and walk the six verifications. It is designed in
`TRIAL_FLOW_ARCHITECTURE.md` (read that first). The pieces:

- **New files:** `Study/SessionPlan.cs` (the derived, persisted counterbalanced session), `Study/TrialFlowPanel.cs`
  (the headset BEGIN prompt + in-trial panel), `Study/TrialWeatherCheck.cs` (read-only sim-vs-plan weather
  check), `Core/CockpitAlignCheck.cs` (the 240/241 snap-quality wireframe).
- **Changed:** `Study/TrialController.cs` (identity now derived from SessionPlan, frame lock on start),
  `Study/StudyProfile.cs` (session-design values: `weatherIsAFactor`, `trialsPerBlock`, `practiceTrials`),
  `Study/StatusSnapshot.cs` (session board JSON), `Study/DataLogger.cs` (derived-identity + weather header),
  `Core/CockpitAnchor.cs` (240->241 save, `Locked`, `ArElementsAllowed`, `WindowsSuppressed`),
  `Core/CockpitElement.cs` (`arOnly` AR gate), `Core/MarkerAnchor.cs` (`Freeze`, `ActiveMarkerLength`),
  `DevSettings/DevModeController.cs` (`WindowsSuppressed`), `Data/XP12Receiver.cs` + `Data/FlightData.cs`
  (three read-only weather/time datarefs), `Resources/dashboard_html.txt` (session board, dev-only
  download/delete), `Study/ExperimentServer.cs` (zip download, guarded delete, no-store on the page).

**FIXES WRITTEN 2026-07-14 (compiled in my head, NOT device-verified). Verify each on device:**

1. **The dashboard must ARM, never START. Now an explicit ARM button (Thomas asked for one).** DONE (written).
   Removed the "Start trial" button. Clicking a board slot now PICKS it (dashed outline, local only, nothing
   sent), and a big **ARM selected trial** button commits it (posts `/api/trial/arm`, solid blue). The
   participant starts from the headset BEGIN prompt. The `/api/trial/start` HTTP route is LEFT as a bring-up
   backdoor; nothing on the calm page hits it and `StartTrial()` keeps its guards. (`Resources/dashboard_html.txt`.)

2. **The BEGIN window shows under 240. It must show ONLY under 241 (Deploy).** DONE (written).
   `TrialFlowPanel.DrawReady` now gates on `CockpitAnchor.Active.Mode == CockpitMode.Deploy`, not `HasFrame`.
   Verify: 240 up = no prompt (setup); 241 up = prompt. (`Study/TrialFlowPanel.cs`.)

3. **The BEGIN window does not respond to the controller. REAL CAUSE FOUND on the 2nd pass.** DONE (written).
   The first fix (worldCamera + EventSystem, both kept, both correct and harmless) was NOT the cause. The
   actual bug: `TrialFlowPanel.DrawReady` runs EVERY frame while Idle and called `SetButtons(...)` every frame,
   which DESTROYS and recreates the BEGIN button GameObject each frame. A button that does not survive from
   pointer-down to pointer-up can never complete a click, so the ray hit it (went red) and nothing happened.
   Now the BEGIN button is built ONCE (rebuilt only when the weather-ok color flips), matching the dev window,
   which builds its buttons once. Verify: the BEGIN button reacts to the ray. (`Study/TrialFlowPanel.cs`.)

4. **AR gate is inconsistent: under 241, map + NavBall hide but the ownship symbol and flight path stay.**
   DONE (written), ZERO Inspector wiring. REAL STRUCTURE (2nd-pass scene parse): the ownship, range rings and
   the flight path (`FlightPlan` root: waypoints + path line) are SEPARATE top-level roots in `TerrainMap.unity`,
   NOT children of `TerrainAnchor`, so the `TerrainAnchor` `CockpitElement`/`ShowWhenAnchored` gate never
   touched them. And `RangeRings` is spawned at RUNTIME, so it cannot be Inspector-wired at all. Fix: a new
   `Core/ArContentGate` component (hides self-renderers AND direct children in a no-AR Deploy, off the one
   `CockpitAnchor.ArElementsAllowed` switch) that `OwnshipMarker`, `RangeRings`, `FlightPathLine` and
   `WaypointMarkers` SELF-ATTACH in code (`ArContentGate.Ensure`) in play mode. `FlightPathLine`'s LineRenderer
   is on its own root with no child, which is why the gate disables self-renderers too. The `ShowWhenAnchored`
   gate from the 1st pass is kept as belt-and-suspenders (redundant with `CockpitElement` on `TerrainAnchor`,
   harmless). Verify BOTH: armed no-AR under 241 hides EVERYTHING (terrain, ownship, path, waypoints, rings);
   setup / an armed AR trial shows them. (`Core/ArContentGate.cs` + the four map-overlay scripts.)

5. **Dashboard download/delete buttons still show with DEV off.** DONE (written). REAL bug, not caching: the
   stylesheet had only element-specific `.X.hidden` rules and NO generic `.hidden`, so `#fileactions`
   (`class="btns hidden"`) fell through to `.btns{display:flex}` and the buttons always showed. Added a global
   `.hidden { display:none !important; }`. Verify with DEV off after a hard refresh. (`Resources/dashboard_html.txt`.)

6. **Caching served stale pages.** Already handled: `no-store` on the HTML response (`ExperimentServer.cs`).
   A browser holding an old copy still needs one hard refresh / clear-site-data to escape it. If arming or the
   dev gating "does nothing", suspect a cached page FIRST.

**Not yet verified at all (written same session, no device pass):** the session board rendering, the derived
file naming, the weather header, the frame lock, `CockpitAlignCheck`, the practice-trial path, session
persistence across restart. Treat all of it as unproven.

**Open design decisions (not bugs), from the 2026-07-14 session:**
- Anchor the extended cockpit (and the BEGIN prompt) to the ArUco cockpit tag, or to the LiDAR digital twin
  of the sim room? The panel currently offsets from the 240/241 frame.
- Can the participant use the physical cockpit instruments in the AR condition? This decides whether the
  physical-panel AOI proxies (Tier 3 item 10) are essential or optional.
- Trial count: the 2x2 (weather as a factor) is 20 recorded flights per participant, ~2.7 h at an 8-min
  route. Decide `trialsPerBlock` and `weatherIsAFactor` on `StudyProfile` before running anyone.


## SCOPE IS FROZEN (2026-07-13)

**Time crunch. The build is feature-complete for the study.** From here the only work is: optimize, fix bugs,
and make the data trustworthy. Everything else moves to "Future work" at the bottom of this file, which is
written to be lifted more or less directly into the paper's future-work section.

The bar for taking on anything new: *does a participant's data get worse without it?* If no, it is future work.

## Where things stand

The map, the NavBall, the guidance chain and the trial server all run **on the ML2**. Terrain renders in
stereo, ArUco places the elements, the dev window tunes them on-device, the sim's flight director flies our
spline via the scoped AP-bug writes, eye tracking + gaze AOI record, and the headset hosts its own trial
dashboard that writes per-trial CSVs.

## Verify next: the 07-14 dashboard batch (WRITTEN, NOT COMPILED, NOT ON DEVICE)

Aimed at the colleague handoff and at run-day slack. Unity has not compiled any of it.

1. **Refusals now appear ON the dashboard.** Test it: press Start with no participant set. Before, the page
   said "Sent" and nothing happened, with the reason stranded in logcat. Now a red message box under the
   buttons says why. This is the batch's main bug fix. Every guard in `TrialController` routes through
   `Accept()` / `Refuse()`.
2. **Trial number is editable** in the Identify row (Idle only). This is the repair path after an app restart
   drops the counter back to 1 and silently mislabels a participant's fifth trial as their first.
3. **"Mark event"** writes `MARK:<text>` onto a real CSV row without stopping the trial. Check a marker lands
   in the `marker` column.
4. **Vitals card + two screaming banners**: battery under 15%, and head tracking lost. Tracking loss is also
   marked into the CSV (`TRACKING_LOST` / `TRACKING_REGAINED`) on the edges. To test tracking loss on device,
   cover the world cameras or face a blank wall. In the Editor there is no XR device, so the card should read
   "no XR device" and the banner must NOT fire: if it fires on a desk test, that is a bug (a false alarm is
   the alarm you learn to ignore).
5. **Redo button** now exists on the page for an endpoint that already worked.
6. `EXPERIMENT_SERVER_HANDOFF.md` was rewritten: its file list was missing `StudyProfile.cs` and `LogRing.cs`,
   and it did not mention that the Study scripts depend on `ARCockpit.DevSettings/.Core/.Data/.FlightPlan`.
   A colleague copying exactly what it listed would not have compiled.
7. **The deliverable is packaged in `Handoff/ExperimentServer/`** (24 scripts + metas, the dashboard, the
   profile asset, the handoff doc, and a START HERE README naming the exact blocks to delete per file). It
   lives OUTSIDE `Assets/` so Unity never compiles the copies. **It is a copy, so it goes stale**: if the live
   Study scripts change, re-copy before sending it. Zip that folder and send it.

## Verify next: the 07-13 data-exposure batch (WRITTEN, NOT COMPILED, NOT ON DEVICE)

This batch closes TODO items 2 and 6 (the data-handling review and the web dev mode). It touches the study
data path, so verify it before it records anything you care about. **Unity has not compiled any of it.**

0. **CREATE THE ASSET FIRST or nothing below works right:** Assets > Create > ARCockpit > Study Profile,
   put it in `Assets/Resources`, name it exactly `StudyProfile`. Without it the code falls back to in-memory
   defaults (safe, but nothing persists and the CSV header will warn).
1. **The Systems Inspector values are GONE from their components and now live on StudyProfile.** Confirm the
   new asset's values match what the scene held: `useTrialReset` OFF, `requireCalibratedEyes` ON,
   `gazeMinConfidence` 1, `gazeDwellToConfirm` 0.5, `gazeHitboxPadding` 1.15, `flushEveryRows` 60. The scene
   values were read off `Systems.unity` and used as the defaults, so they should already match.
2. **Print ArUco 231** (5x5_250, **3 in / 0.0762 m**, per the ones-digit size rule: x0 = 3.5 in, x1 = 3 in,
   x2 = 2.5 in). It pops the new study dev window. NOTE: the `StudyProfile.asset` you created serialized 231
   at 0.0889 instead of the code's 0.0762; fixed 2026-07-14. Confirm the Inspector reads 0.0762.
3. **Open a trial CSV and look at the top.** It should now carry `#` lines naming every `study.*` parameter.
   **Analysis scripts must now use `pd.read_csv(path, comment='#')`.** This is a format change.
4. **The dashboard has a DEV button.** Log stream with per-subsystem filter buttons (XP12 / FDCmd / AOI /
   TRIAL / SERVER / ...), a write-path panel, and the full tunable registry. Read-only by design.
5. **The flight-plan arm switch changed owner.** `FlightPlanSystem` no longer sets `commandArmed`: the
   profile (and the 230 window) owns it. So whether the writer arms is now whatever
   `FlightPlanProfile.commandArmed` says, NOT the Systems Inspector checkbox that used to win. Check the
   asset's value is what you want before flying.

## Verify next (written 2026-07-13, still unconfirmed)

The 07-13 batch went in with no Unity to test in. **Confirmed good on device since:** terrain smoothing seams
are gone, the flight path no longer draws outside the box, and the terrain warning still draws correctly with
forward bias on (the shader center split worked). What is left:

1. **NavBall gaze hitbox.** The readouts (speed, VS, heading, waypoint) sat OUTSIDE the gaze sphere, so a
   participant reading the AR airspeed logged as looking at NOTHING. Silent data loss, and it matches what
   Thomas saw on device. In the Editor: select the NavBall's `GazeTarget`, confirm the gizmo box now covers
   the globe AND the number stack. On device: look at the speed number, expect `[AOI] looking at navball`.
   **This one is study-critical: it is the difference between real gaze data and a hole in it.**
2. **The magenta cue** (was reading the sim's empty FMS: frozen heading, "+12+85" label). Confirm the arc
   moves and the label reads a real waypoint name. This CHANGES WHAT THE CUE MEANS when the rig's FMS is
   empty, so eyeball it before a trial. `navball.guidanceSource = 1` forces the old sim-GPS behavior.
3. **Magvar slew limit.** Watch `[FDCmd]`: the mag bug should no longer snap while the true command is steady.
4. **NavBall "random line at the edge."** Likely the cross-track needle or the roll deviation arc showing a
   stale value from the empty FMS; the guidance-source fix should cure it. If not: on the 220 window turn off
   `navball.showCrossTrack`, then `navball.showRollDeviation`, and see which one kills it.

## Open bugs

- **`TrialReset` is not in the deployed scene set** (found in the 07-13 scene audit, not yet a problem
  because the flag is off). It exists only in `FlightPlanRoute.unity`, the review scene. `Systems.unity` has
  none, so on device `FindObjectOfType<TrialReset>()` returns null. Consequences: the dashboard's "start pose
  captured" light can never turn green, and the moment anyone sets `study.useTrialReset` ON, **every trial
  Start is refused** with a misleading "no connected sim" error. Either add a `TrialReset` to `Systems` or
  leave the flag off and accept that the reposition is not available. Ties directly to item 7 (X-Plane
  situations), which may remove the need for `TrialReset` entirely.
- **Ownship shadow is not showing up properly** (Thomas, on device, 2026-07-13). `map.showOwnshipShadow` is one
  of the new legibility overlays and it went in the same untested batch as everything above. Unclear yet whether
  it does not draw at all, draws in the wrong place, or is hidden by the terrain. Things to check in
  `OwnshipMarker`, roughly in order of suspicion: (a) the shadow is drawn at the ownship's ground position but is
  it being Z-fought or buried by the terrain surface, i.e. does it need the same always-on-top treatment (or a
  small vertical offset) the symbol got; (b) does it respect `map.ownshipAlwaysOnTop` at all, since the symbol
  now renders through terrain and the shadow may not; (c) is it clipped by the box vignette like the flight path
  was; (d) is the profile toggle actually reaching the component (check the 210 window shows the knob and that
  it is ON). The stalk (`map.showOwnshipStalk`) connects the symbol to the shadow, so if the stalk draws and the
  shadow does not, that localizes it fast.

## Next up (priority order, under the scope freeze)

**Tier 1: data integrity. A trial is worthless if these are wrong, and they fail SILENTLY.**

1. **Derive the trial identity instead of typing it. THE BIGGEST HOLE.** Participant / condition / trial are
   typed into the dashboard and nothing checks them against reality, so a mistype silently mislabels a
   perfect-looking file. Intent: pull it from the loaded X-Plane situation. Decide at study prep.
2. ~~Data-handling review across the dev windows + the web server.~~ **DONE 2026-07-13, needs verifying (see
   the batch at the top).** What the audit actually found, all of it now fixed:
   - The study's gaze rules (confidence floor, dwell-to-confirm, hitbox padding) lived as Inspector fields
     and **shaped the recorded data while being unreachable on device and recorded nowhere.** They are now
     `StudyProfile`, tag 231, and stamped into every CSV header.
   - `FlightPlanSystem` **overwrote `FlightPlanProfile.commandArmed`** (and the route file, and the test
     sweep) from its own Inspector bools every launch, silently clobbering the 230 dev window and any saved
     override. Those scene fields are deleted; the profile is the only home.
   - **Every knob on `GuidanceCommandWriter` and `RouteGuidance` was unreachable, anywhere.** Both are
     `AddComponent`-ed at runtime, so they have no Inspector on the deployed build: the write rate, the VS
     clamp, the magvar slew limit, the test sweep and the status logging could not be changed on device OR in
     the Editor. All moved onto `FlightPlanProfile` and exposed on the 230 window.
   - `MapProfile.followAltitude` and the whole `FlightPlanProfile` terrain-conflict group (4 fields) are read
     by **nothing**. Left in place but marked NOT IMPLEMENTED and deliberately NOT given dev-window knobs: a
     control that does nothing is worse than no control. Wire them or delete them.
   - Map gained 5 missing knobs, flight plan gained 4. NavBall was already complete.
3. **HDG/VS engaged watchdog.** Read-only monitor of the AP mode state that flags loudly (log + on-screen) the
   instant HDG or VS disengages mid-trial, so a spoiled run is caught DURING the run, not at analysis. MONITOR
   only; auto re-engaging would be a new write and is out of scope.
4. **Re-test the "elements seen" check.** Suspected of confirming too eagerly (timing around element spawn).
   Readiness aid only, cannot corrupt a trial, but it can lie about what a participant sees.

**Tier 2: run-day robustness. What keeps a session from falling over with a participant in the chair.**

5. **ArUco backup tag (25x).** Escape hatch for when the website misbehaves. BYPASSES the normal guards (no
   participant requirement, no calibration gate) and therefore writes to its own `trials/aruco/` folder so the
   investigator can see the run came through the back door. Must show a confirmation on the ML2. Only fires
   when Idle, never while Recording.
6. ~~A dev mode on the web dashboard.~~ **DONE 2026-07-13, needs verifying.** `Study/LogRing.cs` (bounded
   ring on `logMessageReceivedThreaded`), `/api/logs` + `/api/logtags` + `/api/tunables`, and a DEV toggle on
   the dashboard with three read-only panes: the log stream with per-subsystem filter buttons (the adb
   `Select-String` replacement), the system + AP-write-path panel (including the exact gate blocking a write,
   and whether the receiver is in test mode), and the full live tunable registry. Read-only throughout: the
   headset's dev windows stay the only place a value is edited.
7. **X-Plane situations.** Settle whether a `.sit` can set up a trial cleanly. If so, `useTrialReset` (and
   maybe most of `TrialReset`) may not be needed. Currently OFF.
8. **Cockpit anchor (24x): the wiring IS done, only the device test is missing.** This item used to say the
   Inspector wiring never happened. That is stale: `Systems.unity` has a `CockpitAnchor`, and `CockpitElement`
   is on the map, NavBall and flight-plan scene roots. So what is left is just printing 240/241 and confirming
   Placement/Deploy on device. Only worth the time if item 10 (physical-panel AOI) stays in scope.

**Digital twin (cockpit scan): MID-BUILD, on the backburner. Thomas reassesses 2026-07-15.**

State as of 2026-07-14 EOD (do NOT sink more time here until Thomas re-scopes it):
- Mesh is IN the repo: `Assets/Reference/CockpitTwin/cockpit_scan.obj` (untextured, 21.5k verts / 36.6k faces).
  `CockpitTwin.unity` is built and in the SceneBootstrap list; `Core/CockpitTwin.cs` registers it to the
  240/241 frame, self-gates to 240 on device, and is dev-tunable (offset/rot/scale/opacity + crop box).
- **The scan is the whole ROOM (41 x 23 x 5 m), not just the cockpit, and it is untextured.** First device
  look was "a bunch of blue shapes." So `CockpitTwin` now CODE-CROPS to the densest region (auto-seed = the
  cockpit) and bakes fake vertex shading so it reads. NOT device-verified yet.
- **No clean re-export exists:** Lixel Cybercolor v1.13.1 cannot edit/crop and OBJ export is greyed out
  (XGRIDS feature gap, confirmed in-app, not a user error). PLY / LCC2 / USDZ are the only outputs. Fallback
  if the untextured code-crop still reads poorly: the COLORED point cloud `point_cloud.ply` (color aids
  recognition more than a gray shape).
- Remaining IF it stays in scope: confirm the crop lands on the cockpit on device (refine the crop box in the
  editor dev window), then invisible instrument colliders as physical-panel gaze AOIs (Tier 3 item 10). But
  see the new gaze idea in TOMORROW'S FOCUS: timed self-placed gaze AOIs may not need the twin at all.
- Open musing (not decided): overlay AR on the physical instruments and place AR elements over that. A study-
  design decision for the professor, not a technical blocker.

**Tier 3: study design, still undecided and NOT code yet.**

9. **Randomized events (anti-learning).** One flight plan, ~5 trials/setup, so participants will learn the
   route. Inject randomized maneuver prompts along it (BANK LEFT / PITCH UP / SLOW DOWN). Settle: the event
   set, whether an event perturbs the commanded bugs or is a prompt only, timing vs location triggering,
   per-trial seeding for repeatability, event logging. **Decide whether this is in scope at all before building
   any of it.** It is the largest remaining unbuilt thing on the list.
10. **Physical-panel AOI proxies** (invisible `GazeTarget`s over the PFD / MFD / airspeed / altimeter). This is
    what lets the paper say "AR pulled gaze off the physical PFD", which is close to the study's whole claim.
    Needs item 8 first. **If time forces a cut, cutting this costs the most scientifically: decide early.**
11. **EEG UDP receiver + cross-device sync markers.** Not started. Ask whether the EEG rig's own clock and a
    post-hoc alignment marker is enough, which would remove this entirely.

**Tier 4: element polish. Do only if the tiers above are clear.**

12. **NavBall information review** (Thomas, 2026-07-13, now that he has flight experience with it): revisit what
    is actually ON the NavBall and whether it hangs together. Ties directly to the next item.
13. **A "Sources" section in the NavBall dev window.** Rather than Thomas deciding alone what drives each
    element, expose the SOURCE of each cue as a dropdown so a tester can try a configuration in the headset,
    see what reads best, and Export it. `navball.guidanceSource` (Auto / SimGps / OurRoute) already proves the
    pattern; generalize it to the other cues (heading, pitch/roll, speed, VS) and give the section its own
    header. This turns "what should the NavBall show?" from an argument into an experiment. Cheap, because the
    tunable plumbing and Export/Save/Load already exist.
14. **Ownship symbol LOD** (3D model close in, flat chevron at wide scales), which is what every EFB does. The
    07-13 legibility overlays may already be enough. See also the shadow bug above.
15. **Bring the guidance lines onto the deployed map.** `GuidanceMapOverlay` (red cross-track tie-line, green
    commanded-heading vector) lives only in the `FlightPlanRoute` review scene.
16. **Flight plan revision.** MFDROUTE hugs terrain; refine the vertical profile so less of the path is
    floor-driven.
17. **Write `FLIGHTPLAN_SETUP.md`**: how to author/export an `.fms`, where it must live for both X-Plane
    (`Output/FMS plans/`) and Unity (`Assets/StreamingAssets/FlightPlans/`), how to load it into the G1000, and
    the session setup the overlays need (HSI source = GPS, FD on, adb tunnel up). Worth it only if a colleague
    will run the study without Thomas.

## Open questions

- **Trial playback with the cockpit / digital twin in view (NEW 2026-07-14, professor may want this).** The
  professor may want trials RECORDED and then PLAYED BACK with the physical cockpit or the digital twin in
  view, i.e. a review/replay mode. The per-trial CSV already logs aircraft pose and gaze, so a replay could
  drive the twin from the file without new capture. NOT specified yet: settle exactly what the professor wants
  (replay of what, against what, for whom) before building. Ties directly to the digital-twin work above.
- **Hardware-rig AP lock (UNRESOLVED, Thomas testing).** The sim is an **NFS Cirrus Commercial Cockpit**.
  Hardware AP panels usually run a plugin that writes `heading_mag` / `vertical_velocity` from the physical
  knob encoders every frame, which would clobber our PATCH the next frame (writes still return 200, but the FD
  follows the knob, not us). TEST: fly airborne with HDG+VS engaged, tick `sweepCommandWriter`, watch whether
  the sim's HDG bug visibly oscillates. 200s but no motion = hardware is locking it. Options then: use the
  hardware panel's own bug knobs, find non-hardware-driven bug datarefs, or run on a software-only seat.
  **This is the one open question that could still force a real change of plan, so settle it early.**
- **X-Plane WebSocket.** Nothing is blocked (REST works and the writer uses REST regardless), so under the
  scope freeze this is probably just future work. Recorded because the old conclusion in this file was WRONG:
  `Study/WebSocketProbe.cs` proved `ClientWebSocket` round-trips fine under IL2CPP on device, so the XP12 WS
  failure was the NETWORK (the tunnel or the sim), not the runtime. Delete `WebSocketProbe.cs` when you accept
  that this is not getting done.
- **"Content spawns far under the ArUco."** Reported before the terrain-build fix and never re-checked once
  terrain actually drew. May well be gone. Confirm next time the map is up.

## Known issues

- **Burst compile errors in the Editor** (`TypeInitializationException` at `BurstCompiler.Compile`). From the
  ML / XRI packages, appeared around the OpenXR downgrade. Not blocking (it builds). Workaround: Jobs > Burst >
  Enable Compilation off.
- **ArUco only works on device** (no marker subsystem in the Editor), so every ArUco change needs a build.

## Done recently

**2026-07-13 (part 4): device confirmations + scene cleanup.** Confirmed good on the ML2: terrain smoothing
seams are GONE, the flight path no longer draws outside the map box, and the terrain warning still draws
correctly with forward bias on (the split of `_TerrainMapCenter` / `_TerrainBoxCenter` did its job). The NavBall
no longer spawns at the origin before anchoring, which closes a bug that had been reported twice and never
chased. Removed the deprecated `MagicLeapCamera` component and other dead objects from the `Systems` scene.
`ADB_HELP.txt` rewritten as a run-day command card (safe-build rollback, dashboard tunnel, pulling trial CSVs).

**2026-07-13 (part 3): NavBall + map fixes, and everything is tunable on device.** Three real bugs fixed (the
NavBall gaze hitbox missing the readouts; the magenta cue reading the rig's empty FMS instead of our own route,
via a new `NavBallProfile.guidanceSource` Auto/SimGps/OurRoute; magvar spiking the FD mag bug, now slew-limited).
Map gained `plan.showAheadMeters` / `showBehindMeters` draw-range trimming and `map.forwardBiasFraction`
(track-up G1000 style), plus ownship legibility overlays (always-on-top, shadow, stalk, halo, auto-spawned range
rings; `ownshipScaleMultiplier` default dropped to 80). **Everything is now a dev-window knob**, including the
FULL flight-plan PID set, which was the long-standing blocker on tuning the FD from the headset. Dev window
sections now start collapsed. See the verify list above.

**2026-07-13 (part 2): the trial server is BUILT and running on device.** `Study/ExperimentServer.cs`
(`HttpListener` on `*:8080`, listener thread never touches a Unity API), `TrialController` state machine,
`DataLogger` 60-column CSV, the dashboard HTML. Files are born in `trials/_incomplete/` and only move to
`valid/` or `invalid/` when a human closes the trial out, so a crashed run can never be silently analyzed as a
good one. Verified over hotspot AND adb tunnel, phone AND PC browser. Design: `STUDY_ARCHITECTURE.md` 5.
Handoff: `EXPERIMENT_SERVER_HANDOFF.md`. Run day: `TRIAL_CHECKLIST.md`.

**2026-07-13 (part 1): eye tracking works, device-verified.** `Study/EyeTracker.cs` gives a real gaze ray,
per-eye poses, openness, pupil diameter, and the device's Fixation/Saccade/Pursuit/Blink classifier. Gaze-to-AOI
works (`[AOI] looking at map` / `navball`, dwell timing, confirmed-seen set). Three findings that cost real time
and are worth keeping:
- **Pupil diameter is in METERS** (a calibrated eye reads ~0.0034). The SDK headers do not say so.
- **Eye calibration is MANDATORY and FAILS SILENTLY**: uncalibrated gives confidence 0 and `Valid=false` WITH a
  `Success` result code, i.e. indistinguishable from a permission failure. Now a hard gate on the checklist.
- **Permission ORDER matters**: the native tracker gates its data streams by the permissions held AT CREATION
  TIME, and `PUPIL_SIZE` resolved ~4 s after `EYE_TRACKING`, so creating the tracker on the first grant came up
  permanently pupil-blind. `EyeTracker` now waits for every permission to settle before `CreateEyeTracker`.

**Earlier (07-06 to 07-10), condensed:** guidance runs end to end (`.fms` -> Catmull-Rom spline -> PID with
anti-windup -> scoped HDG/VS bug writes, confirmed PATCHing cleanly on device for a full 13-minute run);
terrain-clearance floor + gradient smoothing so the drawn and flown paths are one curve; the writer re-resolves
dataref IDs on reconnect/404 because X-Plane reassigns them on restart; `FlightPlanSystem` puts guidance in the
`Systems` scene; the map-spawn drift bug (an unrotated recenter offset) and the giant waypoint labels are fixed;
`TrialReset` repositions the aircraft by writing local OGL coords (lat/lon are read-only in this build); the dev
window, controller ray, and X-Plane v1/v2/v3 API all verified on device. Full history is in git; the designs are
in the `*_ARCHITECTURE.md` files.

## Stale files to deal with

- ~~`Trial_Mgmt_Plan.md`~~ **DELETED 2026-07-14** (it is in git history). Fully superseded by
  `EXPERIMENT_SERVER_HANDOFF.md`, and several of its recommendations were wrong on device.
- `Assets/Scripts/Study/WebSocketProbe.cs` - throwaway. Delete once the XP12 WebSocket question above is settled.

## Commands

**Device commands (adb, tunnels, logcat filters, pulling trial data, safe-build rollback) live in
`ADB_HELP.txt`**, which is the portable card that rides the USB drive. Do not duplicate them here: one source
of truth, same as the code rule. Only the two things that need a dev PC live below.

Headless build (Unity must be CLOSED, ~1-2 min incremental; output `Build/AugCogCockpit.x86_64.apk`):
```powershell
Unity.exe -batchmode -quit -projectPath . -buildTarget Android -executeMethod ARCockpit.EditorTools.BuildScript.BuildAndroid -logFile build.log
```

Verify a build packaged the x86-64 OpenXR loader (no headset needed; if this is missing the app renders flat):
```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::OpenRead($apk).Entries | ? { $_.FullName -eq "lib/x86_64/libUnityOpenXR.so" }
```

## Known gotchas (keep handy)

- **Do NOT bump `com.unity.xr.openxr` above ~1.13.x** while on ML SDK 2.6.0. 1.17.x drops ML2 x86-64 support and
  you get `library "UnityOpenXR" not found` on device (flat fallback render). This was the real cause of the old
  "only a red line" symptom. Verify with the loader check above.
- **One shared marker detector pool.** Never let two components each create detectors / call
  `UpdateMarkerDetectors()`; duplicate detectors at the same (dictionary, length) starve each other on device and
  the second element never places. The static pool in `MarkerAnchor` handles this; route any new marker user
  through it.
- **X-Plane reassigns every dataref numeric ID on restart.** Anything caching a write ID must invalidate on a
  reconnect edge and on a 404. The sim gets restarted between trials, so this is not theoretical.
- **Controller ray dead at the origin** = one of three things: the Magic Leap 2 Controller Interaction Profile is
  off (OpenXR > Android), the input actions were never enabled (needs `InputActionAutoEnabler`, no
  `InputActionManager` in the scenes), or two components fight over the controller transform (a Tracked Pose
  Driver + an Action-based controller with empty tracking actions, which writes zero). Diagnose with
  `XRInputDiagnostics`: `boundControls=0` = no binding; `read` changing but `worldPos` stuck = a transform fight.
  See `DEVSETTINGS_ARCHITECTURE.md` 8.
- **A world-space canvas parented to the head camera inherits the rig scale** and renders huge. Build it as a root
  object and head-lock it each frame (see `FpsReadout`). Also create the RectTransform + Canvas at
  `new GameObject(...)` time; adding the Canvas afterward resets localScale to 1.
- **Test Mode on the Systems `XP12Receiver`** drives `FlightData` with no sim (bring-up on device).
- New ML/OpenXR features need a manifest permission AND the feature enabled in OpenXR settings.
- The NavBall cues only mean anything with a real FD mode engaged in the sim: CDI=GPS, NAV (GPSS) lateral,
  FLC/ALT (or VNV) vertical, LVL OFF. A "frozen" cue was once just the FD sitting in ROL/PIT hold.

## Future work (for the paper)

Deliberately not built. Grouped so this lifts more or less straight into a future-work section: each item is
scoped, and says what it would BUY, which is what a reviewer wants rather than a feature list.

**More AR elements.** The architecture is per-element (an element is a prefab plus a profile plus an ArUco tag,
composed by additive scene loading), so the marginal cost of another element is low and the framework is the
contribution. Not built: traffic markers, an airspace ball, audio-cue visualization, control-surface vectors,
a HUD waterline + AoA, and a LiDAR-scanned digital twin of the cockpit. Each would extend the same
AR-vs-physical-instrument comparison to a different class of information (conflict, airspace structure, aural
alerting, control state), which is the natural way to widen the study rather than deepen it.

**World-locked vs head-locked presentation.** The elements are world-locked to the physical cockpit by ArUco.
The obvious counterfactual, and a real open question in AR cockpit design, is head-locked (HUD-style)
presentation of the same information. Because the elements read from one shared data source and are placed by
one anchor, this is a placement change rather than a rewrite, and it would isolate the cost of head movement
and re-accommodation from the cost of the information itself.

**Symbol level-of-detail.** The ownship symbol is a true-scale 3D aircraft with a scale multiplier, plus
legibility overlays (drawn through terrain, with a ground shadow, a stalk, a locator halo, and range rings). The
standard EFB solution is LOD: the 3D model up close, a flat chevron at wide map scales. Worth doing if the map
is ever used at more than the study's fixed range.

**Streaming instrument data over WebSocket.** The receiver polls X-Plane's REST API and dead-reckons between
fixes. X-Plane also offers a WebSocket stream (the sim pushes, no poll round-trip), which would cut latency and
jitter at the source instead of smoothing them client-side. We proved `ClientWebSocket` works on the ML2 under
IL2CPP; the remaining failure was in the network path, not the runtime.

**Adaptive / attention-driven presentation.** The most interesting thing the current build makes possible and
does not use: the system already knows, in real time, where the pilot is looking (gaze AOI), how they are
deviating from the intended path (cross-track and vertical error), and how hard the flight director is working
to correct it. Nothing closes that loop. An element that surfaces or suppresses itself based on measured
attention and measured error is the natural next study, and this build is the instrument that would let you
run it.

**Automatic trial identity.** Participant, condition, and trial number are entered by the researcher. Deriving
them from the loaded simulator situation would remove the last hand-typed field from the data path. (Listed
here only if it does not land before the study; it is currently item 1 on the live list above.)
