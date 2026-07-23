# TRIAL SERVER: update pack (performance, log severity, run-day punch list)

This is a second delivery on top of the `TrialServer/` handoff you already have running. It adds three
things, all of them things the flight project grew after the first handoff and all of them useful here:

1. **A performance / resource monitor** (frame time distribution, memory, thermal, storage, dropped frames)
   with a live Performance card. This is the one aimed straight at your two actual problems: **crashes and
   overheating.** Thermal throttling, a memory climb, and storage running out are exactly what it surfaces.
2. **Log severity** (INFO / WARN / ERROR / CRIT) on the dev log, with per-level filter buttons and a
   **Download log** button, so a run-day log can be pulled to a `.txt` from the phone with no adb.
3. **A "Needs fixing" punch list** on the calm (non-DEV) page: every check that is not green, worst first,
   each with the plain-language action, and a green "nothing needs fixing" when it is clear.

It is written the same way as `TRIAL_SERVER.md`: the reasoning is inline, so you (or a model you hand this to)
can decide what applies rather than copying blind.

---

## READ THIS FIRST: what is and is not tested

- The **log severity** and the **punch list** are browser-and-logic changes. They have been reviewed but not
  run on your project. They are low risk (no new threading, no new Unity APIs).
- The **performance monitor has NOT been verified on a Magic Leap 2.** It compiles cleanly and the math is
  edit-mode testable, but the DEVICE questions are open: which counters the ML2 runtime actually publishes.
  Specifically, on an ML2 we do not yet know whether the XR compositor stats (GPU app/compositor time,
  dropped frames) come through at all, or whether Android thermal status is readable. Every one of those is
  written to report **"n/a" rather than a fake zero** when the device does not answer, so an unavailable
  counter degrades cleanly instead of lying. But treat the perf card as "bring it up and see what populates",
  not "known good". The frame-time / memory / storage numbers are the reliable core; the compositor and
  thermal numbers are the ones to confirm on your hardware.

Nothing in this pack touches the data path. The perf monitor is **off by default** and does one boolean test
per frame until you switch it on from the dashboard, so a recorded trial pays nothing for it.

---

## The files

Drop-in replacements for four files you already have, plus three new ones. Everything is `namespace
TrialServer`, matching your live server (NOT the old `ExperimentServer/` handoff folder, which
`TRIAL_SERVER.md` already tells you to delete).

| File | New or replace | What changed |
|---|---|---|
| `PerfStats.cs` | **NEW** | The frame-time math (percentiles, 1% low, hitches). Pure C#, no Unity, edit-mode testable. |
| `PerfMonitor.cs` | **NEW** | The monitor: self-spawns, owns the on/off, reads every counter, writes the perf JSON. |
| `PerfProfile.cs` | **NEW** | The thresholds as a ScriptableObject. Create one asset (see setup). |
| `LogRing.cs` | **REPLACE** | Adds the severity axis (`Level` replaces `Type`), `CritSummary`, `ToText`, and a level filter on `ToJson`. |
| `StatusSnapshot.cs` | **REPLACE** | Adds a `vitals` (battery), a `crit`, and a `dev.perf` block. Your existing blocks are untouched. |
| `ExperimentServer.cs` | **REPLACE** | Adds `/api/logs/download`, a `level` param on `/api/logs`, and `/api/dev/perf`. |
| `Resources/trialserver_dashboard.txt` | **REPLACE** | Adds the punch list, health bar, Performance card, severity filters, Download-log button. |

If you customized any of these four on your side since the handoff, do NOT blind-overwrite: the "what
changed" column tells you exactly which sections to merge. If you did not touch them, the replacement is the
clean path.

### Where they go

- The three perf `.cs` and the `LogRing`/`StatusSnapshot`/`ExperimentServer` `.cs` go in your `TrialServer/`
  script folder next to the ones they replace.
- `trialserver_dashboard.txt` replaces the one in your `TrialServer/Resources/` folder.

---

## Setup (once)

1. **Drop the files in** (per the table above).
2. **Create the PerfProfile asset.** Assets > Create > TrialServer > Perf Profile. Put it in a **Resources**
   folder and name it exactly **`PerfProfile`** (PerfMonitor loads it by that name). Leave the defaults; the
   only one you might change is `targetFrameMs` (16.7 = 60 Hz; set it to your headset's real target).
   - If you skip this, the monitor still runs on built-in defaults and logs one warning. The asset just makes
     the thresholds editable without a rebuild.
3. **Nothing to wire in the Inspector.** PerfMonitor spawns itself with a `RuntimeInitializeOnLoadMethod`, so
   there is no component to add to a scene and no field to set. This is deliberate: additive scene loading
   makes duplicate components easy, and two monitors would double every counter read.
4. **Build a DEVELOPMENT build** if you want draw calls, SetPass, and GC-per-frame. Those come from Unity's
   ProfilerRecorders, which only report in a development build; in a release build they read "n/a" rather than
   a misleading zero. Everything else (frame time, memory, thermal, storage, dropped frames) reports in a
   normal build.

That is the whole installation.

---

## Using it

- **DEV panel > Performance card > "Turn sampling ON".** That is the only control that writes anything to the
  headset, and it is a start/stop, not a value edit. Turn it on when you are chasing a performance question,
  off again before you record. The sparkline is p95 frame time over the last minute with your budget drawn
  across it: a flat line at the budget is healthy, a rising line is thermal throttling creeping in, and you
  will see the slope here long before it shows in any single number.
- **The punch list** is on the calm page, always. Nothing needs fixing = green. When perf sampling is on, its
  thermal / storage / over-budget findings appear there too, so the overheating case lands in front of the
  operator without them having to open DEV.
- **Log levels:** the WARN+ / ERROR+ / CRIT buttons filter the log server-side; **Download log** saves exactly
  what is filtered.

### For the crash/overheating investigation specifically

Turn sampling on and watch, over a few minutes of a real session:
- **Thermal (now / peak).** If it climbs to MODERATE or past, the headset is throttling and slow frames are
  the hardware, not your code. This is the single most misleading failure, and the card names it.
- **Memory (alloc / RSS / peak).** A steady climb across a session points at a **leak** (different fix from
  thermal). Peak RSS is the number to watch. This is the one that most often precedes a crash.
- **Dropped frames.** On a headset the compositor holds a steady presented rate by reprojecting, so app FPS
  can read a healthy 60 while the participant sees judder. Dropped frames are the honest number, IF the ML2
  publishes them (see the caveat above).

---

## The CRIT sites: mark your own

CRIT is reserved for **"this session's data is compromised"**, not "an exception happened" (that stays ERROR).
The distinction is the whole point: a level that fires on every exception is one you learn to ignore. Nothing
is marked CRIT automatically except **storage about to run out** (PerfMonitor does that, because a gaze file
that stops mid-write is silent and unrecoverable).

To mark a line CRIT, you do **not** call a special method. You put the level in the existing `[TAG]` prefix:

```csharp
Debug.LogError("[BRIDGE:CRIT] Eye tracking permission denied: gaze data will be empty.");
```

`LogRing` parses `[BRIDGE:CRIT]` into tag `BRIDGE` + level `CRIT`. Every existing `[TAG] ...` line keeps
working untouched (its level comes from the Unity `LogType`). This is deliberately not a `Log.Critical()`
wrapper: a second logging path beside `Debug.Log` is a second home for the same thing, and the two drift.

**The site worth marking in your study:** eye-tracking permission denied / lost. That is your equivalent of
the flight project's "sim link lost": the app looks healthy, dwell-destroy silently does nothing, and the
recorded gaze is worthless. Wherever `GazeInputManager` resolves the permission as denied, log it as
`[...:CRIT]` and it will drive the red CRIT row on the punch list. (The punch list already shows the eye
permission as red from the readiness block; the CRIT log line adds it to the downloadable record.)

---

## What did NOT come across, and why

The flight dashboard has more than this. These were left out because they are specific to that study, not
because they were missed:

- **The participant picker.** The flight study identifies by participant number and derives a counterbalance;
  yours identifies by sequence 0-3. Different model, nothing to port.
- **The EEG card, the aircraft/guidance/NavBall cards.** Flight-specific telemetry.
- **The head-tracking-loss banner.** The flight study is untethered and outdoors, where a featureless sky
  drops tracking mid-trial. In an indoor lab that failure is rarer; if you want it, the pattern is a small XR
  `isTracked` read in `StatusSnapshot` plus one chip in `computeHealth`. Say the word and it is a ten-line add.
- **The write-ahead crash-survival ring** (persisting the last N seconds of telemetry to disk so a crash
  leaves a diagnosable tail, plus a clean-shutdown flag). This is genuinely the most valuable thing for a
  project whose problem IS crashing, and it is NOT in this pack: it was scoped but not built on the flight
  side yet. If your crashes keep being the blocker, this is the next thing worth building, and it is a real
  design piece (a disk ring, a session id, unhandled-exception capture), not a snippet. Flag it and we plan
  it as its own delivery.

---

## One-line summary of the contract

The perf monitor is the crown jewel here for your crash/overheat problem, but it is **device-unverified**:
bring it up, turn sampling on, and tell us which counters populate on your ML2. Everything else (severity,
download, punch list) is low-risk polish that makes a run-day failure visible from the phone in your hand.
