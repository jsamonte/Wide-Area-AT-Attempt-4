# AR Cockpit Elements

Unity 2022.3 LTS project building augmented reality flight instrument elements for an EEG study at
the University of Nebraska-Lincoln. This file is the operating manual: how to work with me (Thomas),
the hard technical guardrails, and the coding conventions. It is the one doc auto-loaded every
session, so it stays lean and rule-focused.

- **Current status / where we left off:** see `TODO.md` (rolling daily handoff; read it first, update
  it at the end of each session).
- **What we are building and why:** see `PROJECT_PLANS.md` (study, cockpit concept, elements,
  planned features, ML2 deployment, X-Plane datarefs).
- **The authoritative dataref/command lists:** in `Assets/Reference/XPlane/`. Trust
  `XP12_DataRefs_12.1.3.csv` for the DEPLOYED sim (X-Plane 12.1.3, v1 API only); `XP12_DataRefs_official.csv`
  is the newer 12.4.x superset. Columns `dataref, type, writable, units, description`. Confirm any dataref,
  especially its `writable` flag, before wiring it. Commands are the sibling `XP12_Commands_*.csv`. The
  used/planned subset is annotated in `PROJECT_PLANS.md`.
- **The terrain map subsystem:** see `TERRAIN_ARCHITECTURE.md` (the in-depth design).
- **The NavBall element:** see `NAVBALL_ARCHITECTURE.md` (the in-depth design).
- **The flight path guidance (route spline, deviation, FD command output):** see
  `GUIDANCE_ARCHITECTURE.md` (spline, guidance engine, the scoped AP-bug writer, overlays, review scene).
- **The in-app dev settings (on-device tuning):** see `DEVSETTINGS_ARCHITECTURE.md` (the window, the
  dev-mode markers, export/load, and the controller-input requirements).
- **The study's own parameters** (gaze attribution rules, trial gates, logger, dashboard) live on
  `StudyProfile` (Resources), tuned on device via ArUco **231**, and are stamped into every trial CSV's
  `#` header so a file records the rules it was collected under.
- **The study data capture (eye tracking, gaze AOI, trial CSV, the experiment web server):** see
  `STUDY_ARCHITECTURE.md`, with the run-day procedure in `TRIAL_CHECKLIST.md`. The standalone guide for
  handing the server to a colleague is `EXPERIMENT_SERVER_HANDOFF.md`, which carries the REASONING for each
  decision, because a colleague (or his AI) has to be able to tell what is load-bearing from what is
  incidental. **The packaged deliverable is `Handoff/ExperimentServer/`** (outside `Assets/` on purpose, so
  Unity never compiles the copies): scripts + metas + the dashboard + the profile asset + a START HERE README.
  It is a COPY. If you change the live files under `Assets/Scripts/Study/`, the handoff folder is stale until
  someone re-copies it, so re-copy or say so out loud.
- **Grabbing logs off the ML2:** see `DEBUGGING.md` (adb, the `[APITEST]` startup self-test, filtered
  live viewing per subsystem tag, and capturing a session to a txt file for later review).

## How I like to work

Read this first. It is the difference between help and frustration.

- **You own the Unity Editor work, not me.** I do not want to live in the Editor: assigning components,
  building scenes, wiring Inspector fields is more error-prone for me than for you, and it is annoying with
  AR. Scenes and prefabs are Force Text, so author and wire them yourself by editing the serialized
  `.unity` / `.prefab` / `.asset` / `.meta` files directly. This works fine whether Unity is open or closed;
  just do it. Do not hand me a numbered list of Editor steps as the deliverable, and do not ask me to close
  Unity to make an edit. If a task genuinely cannot be done in text (something only the Editor can generate),
  say so and tell me the single smallest thing to click, rather than defaulting to "here are the steps."
- **NEVER build or deploy. That is mine.** Do not run a batchmode/headless build, and do not `adb install` /
  launch. Do not ask to, and do not ask me to close Unity so you can. You make the code and scene/asset
  changes; I build and put it on the headset; then we talk about what I see on device. A change is not "done"
  because it compiles in your head, but getting it TO device is my step, not yours.

- **Plan before code.** For anything non-trivial, formalize the approach and get my sign-off before
  editing files. Do not start "fucking around with code" before the plan is agreed. Use plan mode
  or lay out the steps and trade-offs first.
- **Ask clarifying questions up front.** If something is ambiguous or a decision is genuinely mine
  (region, scale, study design, which approach), ask before building, not after. Batch the questions,
  give a recommendation with each, and wait for answers. I would much rather answer three questions
  than unwind a wrong build.
- **Recommend, do not survey.** When you ask me to choose, lead with the option you would pick and
  why. Do not dump an exhaustive menu with no opinion.
- **One source of truth, always.** I hate values that live in two places and drift (the Inspector
  vs the script default is the classic). Logic lives in code; values live in one editable place (a
  config asset like `MapProfile`, or one static holder like `FlightData`). When you add tunables,
  consolidate them, do not scatter them across components.
- **Everything is exposed, and the device is where I work.** I do not want to live in the Editor
  playing with values. Any value that is worth tuning must be reachable and editable LIVE on the ML2
  (a `DevTunable` in that element's dev window) and visible on the web dashboard. A knob that only
  exists as an Inspector field on a scene component is a bug: it cannot be tuned on device, it cannot
  be exported, and nothing records what it was. When you add a field, add its tunable in the same
  commit, or say out loud why it is not tunable.
- **No second state of data.** There is ONE live value per thing. Not an Inspector copy and an asset
  copy, not a script default and a profile default, not a value the dev window edits and a different
  one the logger reads. If you catch yourself writing a second home for a value, stop: fold it into
  the existing one. This is the failure mode that has cost the most time on this project (the gaze
  hitbox, the magenta cue), and it fails silently.
- **No churn, no resetting.** Keep field names and serialized data stable so I never have to re-tune
  what already worked. Do not rename MonoBehaviours or reshuffle fields without a reason that is
  worth the breakage, and tell me when something will reset.
- **Be honest about what is verified.** You cannot run Unity here. Say so. Distinguish "this
  compiles in my head" from "this is tested." Report failures plainly with the output.
- **Docs are named in ALL CAPS.** Every project-level document at the repo root is
  `SCREAMING_SNAKE_CASE` with the extension in lower case: `TODO.md`, `STUDY_ARCHITECTURE.md`,
  `ADB_HELP.txt`. No `Trial_Mgmt_Plan.md`, no `adb_help.txt`. It makes the docs sort together and
  visually separate from code and Unity folders. If you create or rename a doc, use caps, and update
  every reference to it in the other docs in the same edit. (Files Unity or git own keep their
  required names: `.gitignore`, `.gitattributes`, `*.meta`, anything under `Assets/`.)
- **Style:** concise and direct. No em dashes anywhere in code, comments, or docs.
- **American English only.** Never British spelling. It is `color` not `colour`, `meter` not `metre`,
  `behavior` not `behaviour`, `center` not `centre`, `gray` not `grey`. This applies everywhere: code,
  comments, docs, UI strings, commit messages. No exceptions.
- **Never use an em dash** (the long dash character, wider than a hyphen) anywhere, in anything. Use a
  comma, a colon, or two separate sentences instead.

## Claude_Temp (the scratch drop box)

`Claude_Temp/` at the repo root is where Thomas drops things for you to use: log dumps, screenshots,
reference files, notes, output from other tools. Check it when you need context; it is not auto-loaded.

**Your responsibility for it:** when a file in there has served its purpose (the task is done, or the
finding has been written into the real docs), either DELETE it or tell Thomas it is stale and ask. Never
let it silently accumulate. Do not delete something you did not finish acting on, and never delete anything
that looks like the only copy of real data (a trial CSV, a capture, a recording): raise it instead.

Nothing in `Claude_Temp/` is a source of truth. It is raw input; the docs are the record.

## Critical constraints (guardrails, never violate)

- **URP, not HDRP.** The Magic Leap 2 is an Android-based, mobile-class device. HDRP does not deploy
  to it. No HDRP shaders, materials, volumes, or HDRP-only APIs. Custom shaders target URP.
- **X-Plane access is read-only, with TWO sanctioned write paths.** Default is observe-only: GET datarefs
  / read subscriptions, never PATCH. The two allowed write paths are:
  1. **In-flight guidance** (`FlightPlan/GuidanceCommandWriter`): writes ONLY the autopilot heading bug and
     vertical-speed bug so the sim's own flight director flies our spline. Off unless `armed`, guarded to a
     live connection; never touches the flight model, the G1000 config, or any other dataref. Do not broaden
     it: no new in-flight writes, no mode-engage spam beyond arming, no writing the flight model.
  2. **Trial reset** (`FlightPlan/TrialReset`): a BETWEEN-TRIALS reposition that snaps the aircraft to a
     captured start pose and forces a fixed trial baseline (pose + velocity, AP/FD on, HDG+VS armed, engines,
     electrical, parking brake), pausing the sim around the batch. Thomas authorized this write expansion for
     trial setup. It is a setup tool, not an in-flight one: it must never fire during a trial, and its writes
     stay scoped to reposition + baseline. Do not extend it to touch the flight model beyond position/velocity.
- **Must run in the Editor without an ML2.** Use test mode (slider-driven `FlightData`) and the
  Magic Leap App Simulator. Never write code that hard-requires device presence to function.
- **No `UnityEditor` API in runtime scripts.** Gameplay scripts live in the runtime asmdef
  (`Assets/Scripts/ARCockpit.Runtime.asmdef`), so `EditorApplication`, `UnityEditor.*`, etc. will
  not compile there. For editor previews use `[ExecuteAlways]` plus a dirty-flag rebuild. Genuine
  editor-only code goes in a separate editor asmdef.

## Unity setup

- Unity 2022.3.62f3 LTS, Universal Render Pipeline.
- XR target: OpenXR + Magic Leap (the legacy Magic Leap XR plugin and the Oculus plugin are both
  removed). ML SDK is the local tgz; build is x86-64 only, IL2CPP, Vulkan.
- **Do not bump `com.unity.xr.openxr` above ~1.13.x** while on ML SDK 2.6.0. 1.17.x drops ML2 x86-64
  support, the loader is left out of the build, and OpenXR fails on device (flat render). This was the
  real cause of the old "only a red line" symptom. See `TODO.md` / `PROJECT_PLANS.md`.
- New ML/OpenXR features need both the OpenXR feature enabled AND a manifest permission in
  `Assets/Plugins/Android/AndroidManifest.xml` (e.g. `com.magicleap.permission.MARKER_TRACKING`).
- **Controller / UI input needs three things or the ray sits dead at the origin:** (1) the
  **Magic Leap 2 Controller Interaction Profile** enabled under OpenXR > Android > Interaction Profiles;
  (2) input actions actually enabled at runtime (`Core/InputActionAutoEnabler` does this, since no scene
  has an `InputActionManager`); (3) ONE component owning the controller transform (do not run a Tracked
  Pose Driver and an Action-based controller with empty tracking actions at once, the empty one writes
  zero and pins the ray to the origin). See `DEVSETTINGS_ARCHITECTURE.md` section 8.
- **ArUco printed size is set by the ones digit, always.** `x0` = 3.5 in (0.0889 m), `x1` = 3 in
  (0.0762 m), `x2` = 2.5 in (0.0635 m). This holds for every tag block. It is separate from what the
  digit MEANS (for 21x/22x it also picks the `DevMode`; for the 23x dev windows 230 and 231 are two
  different profiles both in `DevMode.Window`, differing only in size). A wrong `markerLengthMeters`
  is not cosmetic: the length is a pose input, so declaring a 3 in tag as 3.5 in anchors the element
  about 17% too far away.
- Dev cameras (`OrbitCameraController`, `FreeCamera`, `ChaseCamera`) are editor-only conveniences,
  not shipped to device. Only the XR rig should own the tagged MainCamera at runtime.

## Architecture rules

- **Namespacing and assembly.** All scripts under `namespace ARCockpit` (sub-namespaces like
  `ARCockpit.Terrain`, `ARCockpit.Data`) in the single runtime assembly
  `Assets/Scripts/ARCockpit.Runtime.asmdef`. Add asmdef references as new package deps appear.
- **One shared data source.** `FlightData` is a static current-state holder. Exactly one
  `XP12Receiver` writes it; every visual module reads it. Modules never open their own X-Plane
  connection. Enforce a single receiver instance (additive loading makes duplicates easy).
- **One shared marker detector pool.** ArUco detectors and the per-frame `UpdateMarkerDetectors()` pump
  are static and shared across all `MarkerAnchor`s (one detector per dictionary+length). Never create
  detectors or pump per component: duplicate detectors at the same settings starve each other on device
  and the pump stalls the perception pipeline. Marker IDs/sizes/dictionary come from the element profile
  via `IMarkerSource`, not duplicated on the scene component.
- **Element = prefab.** Each AR element (NavBall, terrain MiniMap, AirspaceBall, etc.) is a
  self-contained prefab. The prefab is the unit you reuse and combine; style variants are prefab
  variants.
- **Per-element dev scenes, compose by additive loading.** Each element is authored in its own
  disposable scene. A `Systems` scene holds the persistent objects (the one `XP12Receiver`, the
  camera / XR rig); at runtime load it first, then additively load the element scenes you want.
  Avoid multiple cameras / audio listeners across additive scenes.
- **Every tunable is a `DevTunable`, and the profile owns it.** A value that a scene component holds in
  an Inspector field is unreachable on device, unexportable, and unrecorded. Worse, several components
  (`GuidanceCommandWriter`, `RouteGuidance`) are `AddComponent`-ed at runtime, so an Inspector field on
  them cannot be edited ANYWHERE. Put the value on the profile and have the component read it. Never have
  a scene component write a profile value at startup: that silently clobbers the dev window and the saved
  override layer (`FlightPlanSystem` used to do exactly this to `commandArmed`).
- **Config as a single asset.** Subsystem tunables live on one ScriptableObject auto-loaded from
  `Resources` (the terrain uses `MapProfile`). This is the "one source of truth" rule in practice:
  scripts read the asset, nothing duplicates its values, and edits to the asset persist through Play.
- **Dev settings overlay (on-device tuning).** A live, marker-driven dev window
  (`Assets/Scripts/DevSettings/`) tunes the profiles on the ML2. The repurposed ones-digit of each tag
  block picks a `DevMode`: `x0` = pop the window + apply saved overrides, `x1` = apply overrides silently,
  `x2` = pure Unity-authored values. So map 210/211/212 and NavBall 220/221/222 now select dev mode, NOT
  size (size became a normal tunable). Profiles expose tunables via `IDevTunableSource` (lambdas bound to
  the one field, so values still live once); `DevSettingsStore` keeps only the changed keys, persists a
  JSON overlay in `persistentDataPath`, and exports a transcribable `.txt`. The window edits the profile,
  fires `OnChanged`, and the element rebuilds. The overlay is a dev tool; the asset stays the source of
  truth for a final build (transcribe the export, ship `x2`).
- **Cockpit anchor (24x, `Core/CockpitAnchor` + `Core/CockpitElement`).** One master ArUco snaps the whole
  extended cockpit: `240` = Placement (a positional reference; while it is up you position each element with
  its own 210/220 tag and its pose relative to the 240 frame is captured), `241` = Deploy (spawn each
  element at its saved offset, skip unsaved). Positions ride the dev-settings Export/Save/Load as tunables
  (`cockpit.{element}.*`). See `DEVSETTINGS_ARCHITECTURE.md` section 7a.

## Coding conventions

- No `UnityEditor` API in runtime scripts; editor preview is `[ExecuteAlways]` + dirty-flag rebuild,
  not `OnValidate` + `EditorApplication.delayCall`.
- URP color via the LineRenderer `startColor` / `endColor`, not `_UnlitColor` (an HDRP-era path).
- One writer to `FlightData` (the receiver). Modules read only.
- Guard live / network code with `Application.isPlaying` so edit-mode previews never open a
  connection or poll.
- `Mathf.LerpAngle` for heading/track smoothing (0/360 wrap), `Mathf.Clamp01` for normalizing.
- Prefer `sharedMaterial` over `material` to avoid leaking material instances.
- No em dashes in generated text, comments, or docs.

## Materials (URP)

- HUD LineRenderers use `NavBallLine.mat` (Sprites/Default: unlit, vertex-colored, URP-OK), loaded
  from Resources by `HudPrimitives`. Color comes from `startColor` / `endColor`.
- The terrain uses a custom URP shader (`ARCockpit/TerrainBand`) created at runtime; its look is
  driven entirely by `MapProfile` globals (see `TERRAIN_ARCHITECTURE.md`).

## Source control

- Git with Git LFS. Track large binaries: heightmap RAW (`*.raw`), point clouds (`.ply`), meshes
  (`.fbx`), textures, audio. LFS rules live in `.gitattributes`; add new binary types there BEFORE
  the first commit of those files (otherwise they bake into history and need `git lfs migrate`).
- Editor > Asset Serialization = Force Text (scenes/prefabs diffable and mergeable).
- Commit `.meta` files; never ignore them or references break. The `.gitignore` is the standard
  Unity template and is correct as-is.

## Task Feedback & Context Management

At the end of every response, append a short "Context Status" block. Evaluate the current state of
the workflow, ask if anything else is needed for this specific task, and recommend the next command.

**Be honest in the Pace line, especially when the honest answer is unwelcome.** Its whole purpose is to
interrupt the vibe-coding loop: the failure mode is a long stretch of plausible-looking edits that never
got verified on device and did not move the study forward. So judge progress against the STUDY, not
against lines of code. If we have burned an hour re-deriving something, gone three rounds on one bug, or
are polishing something that is already good enough, say so plainly and recommend stopping or cutting
scope. "This is taking longer than it should and here is what I would cut" is the most useful thing you
can say. Do not pad it to sound productive.

**Never bury an action item for me inside a paragraph.** If I have to do something (add a component in the
Inspector, delete a script, run a command, tune a value), it goes in the Your TODO list, not in the prose.
A wall of text with a task hidden in the third sentence is how things get missed. If there is nothing for
me to do, say "nothing" and mean it. Keep the whole block short.

**Carry unfinished recommendations forward.** If you recommended something that should have been done and it
was not (updating `TODO.md`, setting a value, running a command), keep surfacing it in the block on later
turns until it is actually done or I explicitly wave it off. Do not silently drop a should-have-done item
just because the conversation moved on. The classic failure is recommending "update TODO.md" three times,
never doing it, and letting it evaporate.

**Do not pad it, and do not repeat yourself.** These lines are a TEMPLATE, not a checklist to fill in every
time. OMIT any line that has nothing real to say (no "Usage check: not needed yet" filler). Cut words that
carry no information. Do NOT restate above the block what belongs in it, and do NOT put in it a summary of
what you already said above: if it is worth saying once, say it once, in the block. The TODO line is for
things I must do BY HAND, not a recap of what you just did or what you already told me in the reply.

Use this format, dropping any line that does not earn its place:

**Context Status:**
* **Done this turn:** [One sentence. What actually changed, and whether it is device-verified or just written.]
* **Your TODO:** [Short numbered list of what I must do by hand: Inspector wiring, deletions, values to set.
  Say "nothing" if there is nothing. Follow with any shell/adb commands I will need, in a code block.]
* **Pace:** [How long this task has been running and whether that is reasonable. Flag loops, rework,
  scope creep, or polish on a solved problem. Be blunt.]
* **Task State:** [Mid-task / Wrapping up / Task Complete]
* **Next Steps:** [Ask a specific question about whether we should move on or if you need more help with the current file/issue]
* **Usage check:** [Remind me to run `/context` (or `/cost`) or just tell you my usage, so you can gauge
  whether to keep going, compact, or wrap. Only nag when it is actually worth checking (a long session, a
  lot of file reads, or nearing a natural stopping point); say "not needed yet" early on rather than
  reflexively asking every turn.]
* **Reset check:** [When I have been heads-down for a while or we just cleared a hard task, suggest I step
  away briefly to think and plan: a quick walk, a chapter of a book, look out a window. Only when it fits;
  do not tack a break onto every single reply or it becomes noise. Say nothing here if we are mid-flow.]
* **Recommended Command:**
  * [Recommend NO COMMAND if we are mid-task and need the history]
  * [Recommend `/compact` if the conversation is getting long but we need to retain the current explicit instructions]
  * [Recommend updating `TODO.md` and then running `/clear` to start fresh if the current task is complete]
  * [Recommend `/cost` if we have been running a long session and need a budget check]
