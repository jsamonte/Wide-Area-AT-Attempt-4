# Trial Flow Architecture

How a participant gets from "sitting in the cockpit" to "a valid trial CSV on disk", and who is allowed to
decide what.

> **STATUS 2026-07-14: written; six device bugs FIXED IN CODE, not yet device-verified.** This describes the
> intended design. The six confirmed bugs from the first device pass have all been fixed (dashboard ARMs not
> STARTs; BEGIN prompt only under 241/Deploy; BEGIN prompt binds worldCamera and swaps in XRUIInputModule so
> it takes controller input; the AR gate now lives in `ShowWhenAnchored` too so ownship + flight path hide in
> a no-AR trial; the dashboard's dev-only buttons hide because a missing global `.hidden` CSS rule was added).
> See `TODO.md` under "TRIAL FLOW" for the per-bug verification steps. Build and walk them before trusting a run.

This is the front end that was missing. The machinery underneath already existed: `TrialController` is the
state machine, `DataLogger` writes the 60-column CSV, `ExperimentServer` hosts the dashboard,
`CockpitAnchor` / `CockpitElement` place the elements. What did not exist was a way for the person in the
headset to run a trial without an investigator typing into a phone, and a way for the trial's IDENTITY
(participant, condition, weather, trial number) to be DERIVED rather than typed.

Related docs: `STUDY_ARCHITECTURE.md` (eye tracking, gaze AOI, the CSV, the server),
`DEVSETTINGS_ARCHITECTURE.md` (the dev windows, the tunable system, the cockpit anchor),
`TRIAL_CHECKLIST.md` (the run-day procedure).

## 1. The problem this solves

The old path: the investigator opens the dashboard on a phone, types a participant ID, types a condition
string, types a trial number, presses Start. Four hand-entered fields, none of them checked against
anything. A typo produces a perfect-looking file with the wrong label on it, and a wrong label is a wrong
result. `TODO.md` calls this "the biggest hole", and it is.

Worse, `TrialData` is static in-memory state. An app restart mid-session silently drops the trial counter
back to 1, so a participant's sixth trial is written as their first.

And there was no physical condition. `CockpitAnchor` Deploy (241) spawns EVERY `CockpitElement`
unconditionally, so "physical only" meant an investigator remembering not to show a tag. That is not a
condition, it is a hope.

## 2. The design in one paragraph

The **session plan is derived from the participant number**, not typed. A participant is assigned a
counterbalance group by their number; that group gives an order of condition blocks; each block is N
trials. The result is a list of SLOTS, each of which knows its condition (AR on/off), its weather, its
index, and its status. The dashboard shows the slots as a board and ARMS one. The headset then runs
exactly that armed slot: the cockpit tag deploys the elements the armed condition calls for, a panel asks
the participant to confirm and press BEGIN, and stopping the trial writes the outcome back onto the slot.
Nothing about the trial's identity is ever typed except the participant ID, once.

## 3. The counterbalance (`SessionPlan`)

Two factors, both within-subjects:

- **AR**: on (physical cockpit + AR elements) or off (physical cockpit only). The participant wears the ML2
  in BOTH, so eye tracking is recorded in both and the headset is not a confound.
- **Weather**: clear night vs heavy rain night. OPTIONAL: see `study.weatherIsAFactor` below.

With weather as a factor there are four conditions (`AR_CLEAR`, `AR_RAIN`, `NOAR_CLEAR`, `NOAR_RAIN`) and
the block order comes from a **balanced Latin square**, so every condition appears in every slot position
equally often and every condition follows every other exactly once:

| Group | Participants | Block 1 | Block 2 | Block 3 | Block 4 |
|---|---|---|---|---|---|
| 1 | P01, P05, P09, ... | AR_CLEAR | AR_RAIN | NOAR_RAIN | NOAR_CLEAR |
| 2 | P02, P06, P10, ... | AR_RAIN | NOAR_CLEAR | AR_CLEAR | NOAR_RAIN |
| 3 | P03, P07, P11, ... | NOAR_RAIN | AR_CLEAR | NOAR_CLEAR | AR_RAIN |
| 4 | P04, P08, P12, ... | NOAR_CLEAR | NOAR_RAIN | AR_RAIN | AR_CLEAR |

Group = `((participantNumber - 1) % 4) + 1`. With weather NOT a factor there are two conditions and two
groups (`AR` first, or `NOAR` first), which is the same code with a smaller table.

**The trial count is a value, not a constant.** `study.trialsPerBlock` and `study.practiceTrials` live on
`StudyProfile` and are tunable from the 231 window. This matters: the full 2x2 design is 4 blocks x 5
trials = 20 recorded trials, and at an 8-minute route that is over 2.5 hours of flying per participant
before breaks. That number has to come down, and when it does it must be a value change, not a rewrite.

**Practice trials are slot index 0..(practiceTrials-1)**, marked `practice`. They record (so the file
exists if you want it) but they carry `_practice_` in the file name instead of a trial number, and are
never counted toward the plan. The name matters more than a folder would: the file gets copied off the
headset and analyzed somewhere the plan is not, and a practice run that reads as trial 1 is a
novelty-effect run sitting in the middle of the data.

### Persistence

The plan for a participant is written to `persistentDataPath/sessions/<participant>.json` on every status
change. On launch, `SessionPlan.LoadOrCreate(participant)` restores it. This is what kills the
restart-loses-the-counter bug: the plan, not `TrialData.TrialNumber`, is the source of truth, and
`TrialData` becomes a mirror of the armed slot.

Slot status is one of `Pending`, `Running`, `Valid`, `Invalid`. An Invalid slot goes back to Pending (it
still has to be flown) but keeps a record of the attempt, so the board shows "this one was redone" rather
than pretending it never happened.

## 4. The cockpit tags, and the setup ritual

The tags do not change (`240` = Placement, `241` = Deploy) but the ritual around them is now explicit.

**Setup (the tester, before any participant):**
1. Put the `241` tag at its permanent home. It belongs OUT of the normal scan pattern (the armrest is the
   intended spot) so it cannot catch the world cameras mid-flight and re-snap the frame.
2. Put `240` over the same physical spot. The two tags MUST share a physical reference point, or a pose
   captured under 240 deploys somewhere else under 241.
3. With `240` up, position each element with its own dev tag (210 map, 220 NavBall). `CockpitElement`
   captures each element's pose relative to the cockpit frame continuously while its dev tag tracks.
4. Swap `240` for `241`. **The Placement -> Deploy transition calls `DevSettingsStore.Save()`**, so the
   captured positions are persisted to disk at exactly the moment the ritual says "save". Then the
   elements deploy at those positions.

**Run day (the participant):** they look at `241`, the elements deploy, the trial panel appears.

### The alignment check

An ArUco snap can go wrong quietly: a bad pose puts the whole cockpit half a meter off and everything
still LOOKS like a cockpit. So while a cockpit tag is tracking, `CockpitAlignCheck` draws a wireframe
square at the pose the anchor believes, at the tag's exact printed edge length, plus a short stick along
its +Z normal and a tick on one corner.

If the virtual square sits ON the printed border of the real tag, the snap is good. If it floats, or is
rotated, or is visibly the wrong size, it is not, and you look again. The tag is its own reference object:
a separate printed alignment box would just be a second thing to line up.

Re-scanning is free and safe: `MarkerAnchor` re-averages continuously while the tag is in view, so looking
at 241 again simply re-snaps. Nothing is destroyed by a bad snap that a better one does not fix.

### The frame lock

The corollary: a re-snap DURING a trial would move every AR element under a recording participant, and
nothing in the file would say so. So `CockpitAnchor.Locked` is set when a trial starts and cleared when it
stops. While locked, marker updates do not move the frame. Adjust freely while Idle; frozen while
Recording.

## 5. The armed slot gates the deploy

This is what makes the physical condition real.

`CockpitElement` gains `arOnly` (true for the map and the NavBall). Under Deploy:

- `arOnly` elements spawn ONLY when the armed slot has AR on.
- Everything else (the cockpit frame itself, the physical-panel AOI proxies, the trial panel) spawns in
  both conditions.

So the physical condition is not "the investigator did not show a tag". It is a state the app is in, it is
recorded in the file, and the elements cannot appear by accident.

## 6. The headset panel (`TrialFlowPanel`)

A small world-space prompt driven by the controller ray. The trial is ARMED on the dashboard; the headset's
only job is to let the participant BEGIN it and to give a mid-flight escape hatch. So it is deliberately
compact: one line of who/what and a BEGIN button, with the weather line appearing only when the sim
disagrees with the plan. The first version was a full card and read as invasive, which for someone about to
fly is exactly wrong.

**Placement is cockpit-frame-relative, with a head-locked fallback.** Once a cockpit tag has been seen, the
prompt sits at a fixed offset from the frame (`trialpanel.off*`, a dev tunable), so it sits in a spot in the
cockpit the participant can look toward rather than floating with their eyes. Only its facing tracks the
head, so it stays readable without wandering. Before any frame exists it head-locks as a fallback, but BEGIN
is not offered until the frame exists anyway (without it, nothing is placed and the AR gate is not applied).

The offset is set in the Inspector / export for now, NOT nudged live on a dev window: precise placement is
still being defined and may re-anchor to the sim-room digital twin instead of the cockpit tag. That is an
open decision, recorded in the doc rather than hidden behind an Inspector-only field.

> **PLANNED (2026-07-14, top task for 2026-07-15): make the BEGIN window a placeable element like the dev
> windows.** The Inspector-only offset above violates the "everything tunable on device" rule and should go.
> Target behavior, mirroring `CockpitElement`:
> - Under **240 (Placement)** the panel is visible and MOVEABLE, nudged live from a dev window (same feel as
>   placing the map / NavBall), capturing its pose relative to the cockpit frame.
> - The **240 -> 241 swap saves/locks** the pose (ride the existing `CockpitAnchor` save-on-that-edge).
> - Under **241 (Deploy)** it spawns at the saved pose for the trial run.
> - Placement (offset X/Y/Z, facing) becomes a `DevTunable` group so it Exports/Saves/Loads like every other
>   knob, surfaced on the consolidated **230** dev window (which is also planned to gather the flight-path
>   `FlightPlanProfile` knobs and the study `StudyProfile` knobs, currently the 231 window). OPEN: whether to
>   truly merge 230/231 into one window or just add the panel's placement tunables to a window. Do the small
>   placement win first; weigh the full merge against the "no churn / do not re-tune what works" rule.

**Nothing shows during the flight.** Holding the controller menu button ~1 s summons a compact
Pause / Finish / Redo panel, and opening it writes `PANEL_OPEN` into the CSV, because in a no-AR trial the
panel is AR the participant was not meant to see.

**Idle, with a slot armed:** the panel shows who, what, and which:

```
  P07   -   TRIAL 6 of 20   -   BLOCK 2
  AR ON  +  HEAVY RAIN, NIGHT
  Sim reports: 1.2 sm vis, overcast, 21:40 local     [matches]
  [ B E G I N ]                        [ not this one ]
```

BEGIN runs a 3-second countdown, then calls `TrialController.StartTrial()`, then despawns. In the physical
condition, that leaves nothing AR in front of the participant, which is the entire point.

**Recording:** the panel is gone. Holding the controller menu button for ~1 s reopens a compact panel with
Pause / End (good) / End (redo); End asks to confirm, so a fumbled thumb cannot kill a run.

Opening the panel mid-trial **writes `PANEL_OPEN` into the CSV**. In the physical condition, the panel is
AR shown to a participant who is supposed to be seeing none, so that fact belongs in the data rather than
in someone's memory.

## 7. The sim read-back (verification, not control)

The weather label is chosen by the plan, but X-Plane is set up by a human. Those can disagree, and a file
labeled RAIN that was flown in CLEAR is poison.

So at BEGIN the panel reads the sim's actual visibility, cloud coverage and local time, shows them next to
the expected condition, and stamps BOTH the expected and the observed values into the CSV header. A
mismatch turns the line amber and says so; it does NOT block, because a blocked BEGIN with a participant in
the chair is worse than a flagged one.

This is READ ONLY. It adds no write path. The X-Plane write guardrail (`CLAUDE.md`) is untouched by
everything in this document.

## 8. The dashboard: the session board

The typed condition and trial-number fields are replaced by a board: one cell per slot, grouped by block.

- grey = pending
- blue = armed (the next trial)
- green = valid, done
- amber = invalid / was redone

Clicking a pending cell arms it (Idle only). A valid stop auto-arms the next pending cell, so the normal
path is zero clicks: the investigator watches, and the participant drives.

The participant ID is still typed, once, at the start of a session. It is the one field a headset menu
cannot produce cleanly, and it is the input from which everything else is derived.

## 9. What is deliberately NOT here

- **No new X-Plane writes.** Weather is read, never set.
- **No auto-advancing between BLOCKS without a human.** A block boundary is where the washout break goes.
  The board arms the next slot within a block; crossing into a new block requires a click, so nobody
  accidentally runs 20 trials back to back.
- **No deletion.** An invalid trial's file is kept, exactly as it is today. The board records that it
  happened.
