# Methodology Notes — Updated

Revision of the original methodology outline. Everything from the original is kept
(reordered slightly, corrected in a few places). Sections marked **[NEW]** did not
exist in the original notes and cover work done since. Sections marked **[REVISED]**
change or supersede something the original said.

The storytelling arc is unchanged and still the right one: *a photoreal scan is not an
aligned scan, an aligned scan is not a stable scan, and a stable scan is not one that
survives an hour outdoors on a head-mounted device.* Each section below is a failure
that forced the next design decision.

---

## 1. Getting the digital twin

*(Original notes, essentially unchanged — this section was already good.)*

- Xgrids portal cam used to scan the wide-area setting for the digital twin that
  becomes the blue wireframe.
- Scanned in the evening or early morning to avoid strong shadows and other lighting
  artifacts that degrade scan quality.
- **Attempt 1:** scan the site in portions and stitch the models together. Stitching
  proved too cumbersome.
- **Attempt 2:** went well until moving too quickly for two or three seconds caused
  sensor drift, rendering the entire scan unusable.
- **Attempt 3:** going well, but the scan autosaved and stopped when battery hit 10%,
  which the portal cam treats as too low to continue.
- **Attempt 4:** succeeded. This is the twin used for the blue wireframe.
- A few undesirable artifacts were cleaned in Blender before wireframe conversion.

**[NEW] What happened to the splat afterwards.** The Gaussian-splat twin is *not*
shipped to the device. `GsplatPlumeHelper` destroys the `GsplatRenderer` and disables
the generated point-cloud mesh in non-Editor builds. The splat survives only as an
authoring and alignment reference inside the Unity Editor — it is what the traced
geometry is traced *against*, and what alignment is visually verified against. This is
worth stating explicitly in the paper: the photoreal twin is a **development artifact**,
not a runtime asset. Nothing photoreal is ever rendered to the participant.

---

## 2. Rendering the wireframe: four generations

**[REVISED]** The original notes end at "followed a tutorial to build a wireframe
shader." That was generation 3. There is now a generation 4.

### Gen 1 — third-party wireframe asset
- Conflicted with the Magic Leap rendering path. Abandoned.

### Gen 2 — Blender-baked wireframe geometry
- First the plain Blender *Wireframe* modifier.
- Then *Limited Dissolve* followed by *Wireframe*, to cut edge count.
- Still lagged on device. Baked wireframe geometry multiplies triangle count by the
  edge count of the source mesh — exactly the wrong direction for a mobile XR GPU.

### Gen 3 — custom wireframe shader
- Followed a tutorial to build a wireframe shader; this became the shipped solution for
  a while.
- **Consequence worth reporting:** once the Blender-generated wireframe object was
  dropped, a mesh had to be re-applied to the replacement object to retain the grab,
  rotate and scale interactions, which had been riding on the old object's collider.
- Also in this era: the previous Unity scene was built on a template that required a
  UI. Removing the UI introduced lag; the fix was to rewrite the code so it did not
  depend on the UI existing.

### Gen 4 — **[NEW]** Semantic Mesh: traced low-poly surfaces + procedural grid shader

The shader fixed *how* lines are drawn but not *what* is drawn: it still ran over the
full-density scan mesh (~125,000 ft² of raw geometry). The final system replaces the
scan geometry itself.

- **Custom Unity Editor tool** (`Assets/SemanticMesh/Editor/SurfaceTracerWindow.cs`).
  The author clicks the corners of a region in the Scene view; clicks raycast against
  the high-res scan, and ear-clipping triangulation (`PolygonTriangulator.cs`) bridges
  the points into one flat low-poly surface. Clicked heights are preserved, so ramps and
  stairs keep their true slope — the surface *drapes* rather than flattening.
- **Regions carry meaning, not just geometry.** Each traced surface gets a
  `SemanticSurface` component holding a category: Walkable, Stairs, Grass, Building,
  Hazard, Obstacle, and Generic. Enum values are stable integers so serialized scene
  data never re-maps if categories are added later.
- **`Generic` is a deliberate escape hatch**, not an oversight. It is a real traced,
  draped, occluded surface with no per-zone semantics, so a messy space can be labeled
  quickly without adjudicating walkable-vs-grass on every region.
- **One palette, one source of truth.** `SurfacePalette.asset` holds each category's
  grid color and cell size; surfaces pull it at runtime through a
  `MaterialPropertyBlock`, so a value never lives in two places.
- **The grid is computed, not built.** `SemanticGrid.shader` generates the grid
  mathematically in the fragment shader from UVs expressed in meters — no
  LineRenderers, no line geometry, no per-line draw calls. Space between lines is
  transparent so it composites naturally in AR.
- **Distance-based LOD.** Line density and thickness fall off with distance from
  `_WorldSpaceCameraPos`, so near stairs read densely and far buildings read sparsely.
  This matters for a wide-area setting specifically: a fixed-density grid is either
  unreadably sparse up close or aliased into noise at distance.
- **Ground vs. labels are treated differently, on purpose.** The ground (streets,
  sidewalks, dirt) uses a draped shrinkwrap mesh cleaned in Blender with a single
  `M_GroundGrid` material that projects the grid **top-down from world X/Z**. That
  projection needs no UVs at all, so swapping in a re-cleaned mesh requires zero
  re-authoring. Vertical faces are discarded in-shader by a world-normal threshold
  (`_Up_Threshold`), which is what makes a rough Blender cleanup pass acceptable —
  leftover wall fragments and scan spikes cull themselves.
- **Known limit, stated honestly:** top-down projection smears on vertical walls. Walls
  are therefore traced with the tool (whose UV-based grid handles verticals cleanly)
  rather than shrinkwrapped. A triplanar shader would remove this split but was not
  built.
- **Readability is judged on device, not on the monitor.** Thin bright lines read very
  differently through AR passthrough than on a desktop display. All grid tuning was
  finalized on the ML2.

---

## 3. Marker tracking: Unity → Vuforia → ArUco

*(Original notes, plus one addition.)*

- Unity AR image tracking — difficulties on Magic Leap 2.
- Vuforia image tracking — difficulties on Magic Leap 2.
- Settled on **ArUco markers** (5×5_250 dictionary).
  - First based on the official Magic Leap 2 templates.
  - Then a colleague's ArUco code, integrated into the existing project.

**[NEW] Marker parameters, for reproducibility:** `Dictionary_5x5_250`; physical marker
length declared explicitly (0.15 m for the space-pin markers, 0.1016 m for the map
marker) with `estimateArucoLength = false`. Letting the SDK estimate marker length is
unreliable at these sizes and injects scale error directly into the alignment.

**[NEW] The frame-freeze bug (`ArucoTrackerSync`).** Several components independently
called `UpdateMarkerDetectors()` in the same frame. This blocks the ML2 OS perception
pipeline and freezes the Unity main thread — observed and reported at the time as the
"camera stuck" bug. Fixed by a static once-per-frame gate that guarantees the detector
feature is pumped exactly once per frame no matter how many components ask, plus a check
for redundant detector instances. This is a good concrete example of an integration
failure that looks like a performance bug.

---

## 4. Alignment, phase 1: the manual-offset era

*(Original notes, kept as the narrative of what was tried and why it failed. This whole
phase is superseded by §5 — say so explicitly in the paper, it is a stronger story than
presenting it as the final method.)*

- **Going in and out.** The ML2 overheats in direct sun, so sessions alternated inside
  and outside hoping the device would shed heat. Updating the transform offset by
  educated guess, indoors, between trips, took far too long.
- **Moving the entire prefab.** A script was written to move the offset with the
  controller so changes could be seen live instead of guessed. But a slight hand tilt
  rotated the entire map with no reliable way to recover the correct orientation.
- **Clamping rotation.** Locking rotation entirely prevented finding the correct
  orientation — the ArUco marker would have had to be placed at exactly one fixed
  orientation. Compromise: lock X and Y, allow Z, since Z varies with marker placement
  around the site.
- **Z rotation on the horizontal thumbstick.** Grab moved the positional offset; the
  left/right thumbstick rotated Z.
  - Rotation was far too fast — it overshot repeatedly, oscillating past the target.
    Fixed by exposing rotation speed as an Inspector field.
  - The prefab rotates about the center of the 3D mesh, not the point selected. On a
    site-scale model this means **every Z change requires re-fixing the positional
    offset immediately afterward** — the two adjustments are coupled, which is what
    made manual alignment so slow.
- **Scale on the vertical thumbstick.**
  - Original method: a single strand of duct tape measuring a staircase edge, matching
    the wireframe's staircase to the tape. Failed for two reasons.
    1. The code at the time let the wireframe shift with viewing angle on the marker,
       so there was no stable thing to measure against.
    2. Matching one edge does not match the world. When the right edge of the staircase
       lined up, the left edge fell short — scale too small. When the whole staircase
       matched, walking far enough revealed the wireframe trees sitting closer to the
       marker than the real ones — scale still too small. **Local agreement does not
       imply global agreement, and this is the core argument for the multi-marker method
       in §5.**
  - Every scale change still required going back inside.
  - Fix: vertical thumbstick drives scale live, horizontal keeps Z rotation. This worked
    well, especially over longer distances.
  - **Scale was updated at least 6–7 times over the course of alignment.**
- **X and Y rotation.** An ideal scan would be parallel to the ground. This one is not,
  in more than one way: the site has significant slope, and even the apparently flat
  areas carry a rotational offset. The thumbstick mapping was reassigned to X-axis
  rotation (horizontal) and Y-axis rotation (vertical).
- Until this point the markers sat at draft positions, because moving a marker after
  calibration invalidates the offset. Final placement: floor cleaned with a toothbrush
  and a rug for adhesion, permanent stickers applied, then the final offsets found.
- **Marker jitter fixes.** Early versions re-placed the wireframe every time a user
  looked at the marker, changed viewing angle, or changed distance.
  - A colleague's code was integrated to stabilize this.
  - A **dwell-time countdown** was added so the marker is processed once, correctly,
    rather than continuously. This stopped the wireframe jumping on every glance,
    although occasional completely-wrong spawns remained — which is what motivated §5.

**[NEW] Persistence.** Marker anchors moved off `PlayerPrefs` and into a JSON file store
(`WireframeMarkerAnchorStore`, mirroring `RoomAnchorCalibrationStore`), with a one-time
automatic migration of existing PlayerPrefs data. PlayerPrefs is not a defensible place
to keep calibration state for a study — it is opaque, size-limited, and cannot be pulled
off the device for inspection or archived with the run.

---

## 5. **[NEW]** Alignment, phase 2: World Locking Tools and a marker network

This is the single biggest methodological change since the original notes, and it should
probably be the centerpiece of the alignment section.

### The shift in premise
Phase 1 tried to compute *one* rigid transform from *one* marker and hold it. At
site scale that is the wrong model: the residual error grows with distance from the
marker (exactly what the staircase/tree observation in §4 demonstrated), and any single
detection error moves the entire world.

Phase 2 instead uses **Microsoft World Locking Tools (WLT)** with a network of ArUco
markers. Rather than the model snapping to a marker, each marker **pins a known model
point to a known physical point**, and WLT corrects the *camera* to satisfy all pins
simultaneously. Markers are scanned once before trials begin; the digital twin is then
aligned to the physical environment by the whole pin network.

### The build
- **19 `ArucoPinDriver` instances in the scene**: 18 space-pin markers distributed
  around the site (IDs 2, 3, 4, 8, 20, 22, 30, 33, 40, 44, 48, 80, 82, 84 ×2, 200, 202,
  222) plus ID **88** which drives the navigation map, not the alignment.
- Each driver requires a `SpacePinOrientable`, so WLT derives the pin's orientation
  itself from the pin network rather than trusting a single marker's measured rotation —
  which directly addresses the phase-1 problem where marker rotation error tilted the
  world.

### Making a detection trustworthy before it is allowed to move the world
A single noisy frame must never be able to place a pin. Each driver gates on:
- **Pose averaging** over a rolling window (`poseAverageSeconds = 0.25`).
- **Dwell** (`requiredDwellSeconds = 2.0`) — the marker must be held, not glimpsed.
- **Minimum sample count** before a relocalize is accepted
  (`minSamplesForRelocalize = 2`).
- **Visible-hold** (`visibleHoldSeconds = 0.75`) so intermittent detector dropouts do
  not cause flicker or re-placement.
- **Gaze gate** — the marker must be within `maxGazeAngleDegrees = 25°` of the head
  forward vector.
- **Head-stillness gate** — head translation under 0.20 m/s and rotation under 20°/s.
  Poses measured while the head is moving are systematically worse.

### Elevation lock
`lockElevation` mathematically pins the perceived marker height so WLT can never raise
or lower the floor. Without it, a marker measured slightly low drags the entire ground
plane down — and on a sloped site, floor height error is the most visible and most
disorienting failure mode.

### Height diagnostics
Every pin lock appends the authored / detected / placed world height to
`aruco_pin_height_diag.txt` in persistent storage. This file survives being offline and
survives a reboot, so it can be pulled over adb after an outdoor session. This exists
because outdoor sessions have no network, no console, and no second chance — the
"markers scan lower than authored" problem could not be diagnosed any other way.

### A performance detail worth one sentence
The head-motion delta is identical for all 18 drivers, so it is sampled once per frame
into statics and each driver compares it against its own thresholds. Previously all 18
recomputed the same delta independently.

### Why controller adjustment is now disabled
`enableControllerAdjustment` is off by default in WLT builds, for three reasons that are
each worth stating:
1. **It fights WLT.** Pins align the model by correcting the camera against authored
   `ModelingPoseGlobal` values. Moving the model root after pins have locked means those
   authored poses no longer describe where the model is.
2. **It breaks static batching.** A batched renderer's geometry is baked into world
   space. Move the transform and the *visuals* stop tracking it while the *colliders*
   still move — so gaze raycasts hit geometry that is not where it appears. In a
   gaze-dependent study this silently corrupts the dependent variable.
3. **It is reachable mid-trial.** The component keeps running after the marker detector
   is destroyed, so a participant brushing the thumbstick could shift the world model
   during a recorded trial.

---

## 6. **[REVISED]** Scaling — the three-stage account

The original notes' "6–7 scale updates by thumbstick" is now stage 1 of three. Present
it as a progression:

1. **Manual.** Hand controller adjusts the twin's size live (§4). Fast to iterate,
   impossible to verify globally.
2. **Satellite imagery.** The twin's footprint is registered against overhead imagery of
   the real site. Gives a globally-correct scale that no local measurement could.
3. **Multi-marker space pins.** With markers at known, surveyed positions, scale is no
   longer a single global multiplier applied by eye — it falls out of the pin network.
   This is what finally resolves the staircase-vs-trees contradiction from §4: a single
   multiplier cannot satisfy both near and far agreement if there is any residual skew,
   and the pin network does not require it to.

---

## 7. Spawning

- Targets spawn only within an authored **walkable area** (colliders / planes),
  with a configurable height offset above the surface.
- **Seeded randomness.** Each of the four pools has a fixed seed (100 / 200 / 300 /
  400) so a layout that works can be reproduced exactly. Randomness gives layout
  variety across pools; the seed gives reproducibility across participants. Every
  participant assigned to the same pool sees the identical layout.
- 20 targets per trial by default.
- Each pool also carries its own **recall object** list, spawned alongside the targets.

---

## 8. Occlusion

- An occlusion object hides targets and recall objects that sit behind real geometry,
  so the participant cannot see through buildings.
- Built from the 3D scan as a baseline, then **decimated** to reduce complexity.
- Mesh that occluded nothing was deleted outright.
- Buildings without a clean mesh were replaced with **cubes matching their footprint** —
  a building only needs to be the right *shape* to occlude correctly, not the right
  *surface*.
- **[NEW]** Implemented as a depth-only material (`DepthOccluder.shader`) — it writes
  depth and renders no color, so the occluder is invisible while still hiding everything
  behind it. Its effect is only fully visible on device.

---

## 9. **[REVISED + NEW]** Stability: crashes, and what actually caused them

The original notes list the WLT/subsystem disabling. The root cause found later is more
specific and more publishable.

### Root cause: CVIP memory exhaustion
The ML2's perception pipeline (CVIP) accumulates memory across marker-detection scans.
Alignment performs roughly **26 marker scans** per session. That growth is what
eventually segfaults `pw_service` — the crash presents much later than its cause, which
is why it took so long to find.

### Mitigation 1 — throttle *frame count*, not *precision*
`ArucoMarkerManager` uses a deliberately-tuned throttled detector profile:
- Reduced: `MarkerDetectorFPS.Low`, `FullAnalysisInterval.Medium` — fewer frames
  analyzed.
- **Deliberately kept at full quality**: `Resolution.High`, `CornerRefineMethod.Contour`,
  edge refinement on, `Camera.World` (multi-camera, wider coverage for markers spread
  around the site).

The distinction is the point: cutting *how often* the pipeline analyzes reduces memory
growth and heat, while cutting *per-frame precision* would degrade marker pose accuracy
and therefore elevation error. Only the first was cut.

### Mitigation 2 — detector lifecycle
Never run two detectors at once. At trial start, in order and idempotently: stop map
tracking → destroy the space-pin detector → start map tracking. Doing this *before* any
spawner or wireframe logic runs means an exception partway through can never leave two
detectors live. The map's ID-88 detector is torn down independently of the space-pin
detector so one cannot kill the other.

### Mitigation 3 — the map detector only runs during a trial
`MapTracking` starts with `startTrackingOnStart = false` and uses its own low-power
profile (Low FPS / Medium analysis / Low resolution) — a map that smooths its own
position does not need a high-rate detector. The camera and CV pipeline stay off, and
cool, whenever the map is not needed.

### Mitigation 4 — subsystems WLT cannot coexist with
WLT uses the Frozen World engine, which conflicts with several ML2 subsystems. Disabled:
- Meshing subsystem
- Localization maps
- Spatial anchor subsystem
- Spatial anchor storage
- **Hand tracking** — separately implicated by the ML2's own crash reports.

Max local anchors set to **128**.

### Mitigation 5 — split the session
The study runs as two parts with a **headset reboot between them**:
- **Part 1** — Trials 1 & 2 (labeled *Dusk*), preceded by the tutorial.
- **Part 2** — Trials 3 & 4 (labeled *Night*), tutorial skipped since the same
  participant already did it.
- Markers are re-scanned at the start of each part.

The reboot is what actually clears accumulated CVIP memory. Splitting the study is not a
protocol convenience — it is the crash mitigation.

---

## 10. **[NEW]** Thermal management

Distinct from crashing, though the original notes mix them. The ML2 compute pack
throttles and eventually shuts down under sustained outdoor load, and every measure
below buys thermal headroom:

- **Frame rate capped at 30 fps** during trials *and* menus. Halving frame rate is the
  single largest heat reduction available.
- **Render scale 0.65**, MSAA off, shadows off (an outdoor AR application has no use for
  rendered shadows).
- **Gaussian splat destroyed on device builds** — the photoreal twin never renders in a
  build (§1).
- **Automated wireframe replaced with the hand-traced low-poly one** (§2) — fewer
  polygons and better environmental accuracy at the same time.
- **Static flags** applied to non-moving geometry for batching. (See §5 for the
  batching/collider hazard this introduces.)
- **Map camera throttled to 1 frame in 6** (`MapCameraThrottle`), giving ~5 map updates
  per second at the 30 fps cap. The reasoning matters: the map camera draws the scene a
  *second* time into a small RenderTexture. At 256×256 the pixel cost is trivial, but
  the **CPU cost — culling and submitting every renderer — is a full duplicate of the
  main camera pass, every frame**. Throttling is implemented by toggling
  `Camera.enabled` rather than calling `Camera.Render()`, because under URP explicit
  `Render()` calls are not the supported path.
- **Forced 20-second cooldown between trials** before the Start button becomes
  clickable, letting the compute pack shed heat while the operator is doing paperwork
  anyway.
- **Low-power marker detector profiles** (§9).

---

## 11. **[NEW]** Experimental control: the on-device trial server

The researcher cannot stand at the headset in a wide-area outdoor setting. The ML2
therefore hosts its own control surface.

- An HTTP server on the headset (port 8080). The researcher opens a browser — phone on
  the same network, or a PC through an adb tunnel — and both **sees** what the
  participant is doing and **drives** the trials remotely.
- It is a **wrapper, not a replacement**: `SequenceManager` remains the trial driver and
  `EyeAndHeadTracker` remains the single data source. A `SequenceBridge` translates
  incoming commands into presses on the study's existing buttons.
- **The threading contract is the design.** `HttpListener` callbacks run on a background
  thread where touching any Unity API throws or crashes. So: the main thread builds a
  JSON status snapshot in `Update` and publishes it to a volatile field; the listener
  thread only ever hands out that finished string. Inbound commands are enqueued and
  return **202 Accepted** immediately, then drained and executed on the main thread. The
  HTTP response means *"heard you"*, not *"done"* — the dashboard confirms by watching
  the status change, which also proves the trial machinery actually ran rather than
  merely acknowledging.
- **The server is not on the data path.** Gaze data is written on the main thread by the
  tracker; the server is a read-only observer. If HTTP stalls, the recording is
  untouched and the dashboard just shows a stale number.
- **Run logs are written to file regardless of the server**, so an outdoor session with
  no network is still fully captured for later review.

### Performance telemetry
`PerfMonitor` spawns itself via `RuntimeInitializeOnLoadMethod` (so additive scene
loading cannot create duplicate counters) and is **off by default** — a recorded trial
should not pay frame time to measure itself. It is switched on from the dashboard only
when someone is actually asking a performance question. Every counter reports **null,
never zero**, when unavailable: a zero meaning "not measured" is indistinguishable from
a zero meaning "nothing happened."

---

## 12. **[NEW]** Study design and counterbalancing

This section did not exist in the original notes and is the part a reviewer will read
most carefully.

- **Independent variable:** wireframe ON / OFF, within-subject, 2 trials each.
- **Nuisance factor:** pool 1–4 (the object layout), counterbalanced.
- **4 groups × 4 trials.** Group assignment fully determines every trial's pool and
  wireframe state. Equal allocation (6 per group at n = 24).

| Group | Trial 1 | Trial 2 | Trial 3 | Trial 4 |
|---|---|---|---|---|
| 1 | Pool 1 · OFF | Pool 2 · ON | Pool 3 · OFF | Pool 4 · ON |
| 2 | Pool 2 · ON | Pool 1 · OFF | Pool 4 · ON | Pool 3 · OFF |
| 3 | Pool 4 · OFF | Pool 3 · ON | Pool 2 · OFF | Pool 1 · ON |
| 4 | Pool 3 · ON | Pool 4 · OFF | Pool 1 · ON | Pool 2 · OFF |

### The confound this design exists to fix — report this
The **previous** tables had Pool 1 with the wireframe off in all four sequences and Pool
2 with it on in all four. Pool was therefore perfectly confounded with condition: any
difference in the scenery density or layout of those pools was indistinguishable from an
effect of the wireframe. **That confound is what made the pilot's headline result
uninterpretable.** The current tables guarantee each pool appears **twice with the
wireframe and twice without** across groups; each group sees each pool exactly once;
each pool appears once in each trial position; and each trial position is two ON / two
OFF across groups.

### Why ON/OFF strictly alternates within a group
Deliberate, not an oversight. Ambient light falls monotonically through an evening
session, so alternating samples both wireframe states evenly across that gradient for
every participant. Blocking (OFF OFF ON ON) would place every wireframe trial in the
darker half of the session. Groups 1/3 and 2/4 run opposite phases so the small residual
imbalance cancels across the sample.

### Lighting is observed, not assigned
Daylight cannot be randomized, so it is **not a factor in the tables** and must not be
added to them. Trials 1–2 are simply the brighter half of the session (*Dusk*) and 3–4
the darker half (*Night*). Report it as a measured covariate, never as a manipulation.

### Trial numbering and recovery
- Trials keep their absolute numbers across the part split — Part 2's trials are "Trial
  3" and "Trial 4" in the UI, in the markers, and in the recording filenames, so Part 2
  data can never collide with Part 1's.
- **Single-trial recovery path** for a crash, lost map, or battery death partway
  through: scan markers → pick wireframe state → pick pool → run one trial → done. No
  tutorial, no advance. Recovery trials record under the name **`Trial_Recovery`**
  rather than a number, so a re-run can never be silently analyzed as a clean trial —
  the participant has partial prior exposure to that layout. The operator records which
  trial it replaced.

---

## 13. **[NEW]** Measurement and data collection

- `EyeAndHeadTracker` records eye and head data at ~60 Hz, writing both a **JSON
  summary** and a **raw NDJSON stream**.
- **Gaze object logging.** The eye ray is logged against whatever it actually lands on —
  targets, recall objects, walls, props, any collider — producing per-AOI dwell totals.
  Sampling rate, ray distance (50 m) and layer mask are all tunable to trade fidelity
  against heat.
- **Look events are streamed append-only** to their own `gaze_events_*.ndjson`. This is
  the crash-safe home for per-look data: lines are written once and never rewritten. By
  contrast the JSON summary is rewritten *in full* on every autosave, so embedding a
  growing event list there costs a main-thread hitch every few seconds — it is therefore
  off by default. Aggregate percentages appear in the summary either way.
- **Task mechanic:** targets are destroyed by eye dwell (4.0 s over the target).
- **Heartbeat to logcat**, every 30 s during a trial, deliberately *not* into the trial
  JSON: logcat survives a mid-trial reboot, whereas the JSON copy would be buffered in
  RAM and lost in exactly the event being diagnosed. It also keeps the experimental data
  clean.

### Gaze data quality outdoors — report this number
The valid-gaze screening threshold is **50%**, and the reason is empirical: outdoor
pilot sessions (31 Jul 2026) returned 62.1% and 70.4% valid gaze, while indoor sessions
run around **89%**. Ambient infrared outdoors is the limiting factor. A 50% floor is not
laxness — it is what outdoor eye tracking on this hardware actually permits, and the
gap between 89% and ~65% is itself a finding worth reporting for anyone planning
outdoor XR eye-tracking work.

---

## 14. **[NEW]** Analysis pipeline

Raw headset output → analysis-ready tables with no manual steps (R, tidyverse). Every
path and threshold lives in one config file; nothing downstream hard-codes a number. The
pipeline is validated end-to-end on **simulated** data (`run_selftest.R`) before real
data is touched, and writes `session_info.txt` (package versions) on every run.

### Three reduction decisions that need to be in the paper
1. **Time is summed from inter-row intervals, never counted in rows.** Frames drop; a
   59-row second is not 59/60 of a second.
2. **Pauses and stalls are excised.** Timestamps run on wall clock but no rows are
   written while a trial is paused, so a pause appears as one enormous inter-row
   interval. Counting it would credit the participant with minutes of dwell on whatever
   they happened to be looking at when the operator hit pause. Intervals above **0.2 s**
   are treated as unobserved: excluded from every duration, and they break any dwell or
   eye event spanning them.
3. **Missing means missing.** The logger writes an empty cell — never a zero, never NaN
   — for anything not measured. Saccade peak velocity is empty during every fixation;
   read as 0 it would drag the study-wide mean toward zero. Column types are declared
   rather than guessed.

### Exclusion policy (CONSORT-style flow generated automatically)
> Trials flagged invalid by the operator during the session were excluded. Where a
> spoiled trial was re-run, the retake replaced it (an invalid trial does not consume
> its trial number, so the retake occupies the same design cell). Files stranded in
> `_incomplete/` — recordings interrupted by an application crash or quit, which never
> reached a classified close — were excluded. Trials in which more than 20% of trial
> time lacked usable gaze (tracking active, gaze valid, and confidence at or above the
> floor recorded in that trial's own file header) were excluded from gaze analyses but
> **retained for survey and recall analyses**, since eye-tracker quality is
> uninformative about whether a participant completed a questionnaire.

### Preregistered analysis decisions
Fixed before looking at real data: the 20% gaze-loss threshold, the 0.10 s look-merge
window, the 0.10 s minimum look duration.

### Surveys and recall
- Instruments: NASA-TLX, subjective clutter, MEC-SPQ (presence), SBSOD (spatial
  ability), SART (situation awareness), SSQ (simulator sickness), wireframe impressions,
  brightness.
- Scoring decisions worth stating: **TLX Performance is not reverse-coded** (the item is
  already worded higher-is-worse); **SBSOD reverses 8 of 15 items**; brightness items are
  bipolar and scored as deviation from the midpoint.
- **Post-study measures (SART, SSQ, wireframe impressions) are collected once per
  participant and therefore cannot enter the condition model.** Stating this prevents an
  obvious reviewer objection.
- Recall is scored with signal detection theory: **d′, criterion, A′**, plus an
  item-level GLMM. The **loglinear correction is applied to every trial, not only ceiling
  ones**, which is the defensible choice.

### Limitation to state explicitly: pupil diameter
`pupil_mean_mm` is computed because it is cheap and conventionally cited as a workload
proxy. **It tracks scene luminance far more strongly than cognitive effort.** The
conditions here differ in exactly how much they light up the display — an AR wireframe
overlay is additive light — so a pupil difference between conditions cannot be
interpreted as a workload difference. **Raw TLX is the workload measure.** Report the
pupil data if at all only with this caveat attached.

---

## Outstanding before modelling

Carry these as an explicit checklist, not as prose:

- [ ] **Recall answer key** — `item_label` and `present` for all 120 rows.
- [ ] **Recall transcriptions** — paper sheets typed into `recall_responses.csv`.
- [ ] **NDJSON reduction** — the gaze reduction currently targets the disabled
      `DataLogger` CSV path; the live writer is `EyeAndHeadTracker`. This must be
      retargeted before any real reduction run.
- [ ] Confirm the condition strings in the analysis config match what the operator
      actually typed during sessions.

---

## Suggested narrative spine for the paper

Six failures, each forcing the next design decision. This is the storytelling arc:

1. **You cannot scan a large outdoor space in one pass.** → four attempts, three modes
   of failure (stitching, drift, battery).
2. **You cannot render a photoreal scan on a head-mounted device.** → four generations
   of wireframe, ending in traced semantic geometry and a procedural grid shader.
3. **You cannot align a site-scale model from one marker.** → local agreement is not
   global agreement (the staircase and the trees), forcing a marker network and WLT.
4. **You cannot trust a single detection.** → dwell, pose averaging, gaze and head
   stillness gates, elevation lock.
5. **You cannot run perception continuously for an hour outdoors.** → CVIP exhaustion,
   detector lifecycle, throttled profiles, thermal budget, and a session split with a
   reboot in the middle.
6. **You cannot interpret a result from a confounded design.** → the pool/condition
   confound that made the pilot uninterpretable, and the counterbalancing that fixes it.
