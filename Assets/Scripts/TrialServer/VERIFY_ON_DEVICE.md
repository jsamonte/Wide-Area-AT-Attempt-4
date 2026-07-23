# VERIFY ON DEVICE (TrialServer debug pack + run-log file)

Everything below is WRITTEN but NOT device-verified. Thomas has no ML2 access at the time of writing.
This is the punch list for the next headset session. Order is worst-first: the top items are the ones that
would waste a whole session if they are wrong.

## What changed (so you know what you are testing)
1. The TrialServer_Update pack was applied to `Assets/Scripts/TrialServer/`: log severity (INFO/WARN/ERROR/
   CRIT), server-side log filtering, a Download-log button, the run-day punch list, headset vitals, and a
   Performance card + `PerfMonitor` (off by default).
2. A run-log FILE sink was added to `LogRing`: with `ExperimentServer.logToFile` on (default), every log line
   is also written to `persistentDataPath/logs/run_<timestamp>.log`, flushed per line. This is the outdoor,
   no-network capture: the whole run lands on disk to review later.

## 0. Does it compile and come up? (blocks everything)
- Build has no compile errors from `namespace TrialServer` (new files: PerfStats, PerfMonitor, PerfProfile).
- App launches, dashboard loads at `http://<headset-ip>:8080/`.
- WRONG: red console on `PerfMonitor` / `PerfProfile` / a missing-meta warning on the new scripts.

## 1. The run-log file (the point of this session's ask)
- After a short run, pull `persistentDataPath/logs/` and confirm a `run_<timestamp>.log` exists with the
  whole run in it (not just the last N lines the in-memory ring holds).
  - adb: `adb shell ls -l /sdcard/Android/data/<package>/files/logs/` then `adb pull` the file.
- Confirm it captured STARTUP lines (the `[SERVER] Run log -> ...` line should be near the top).
- Kill the app hard (not a clean quit) and confirm the log still has its tail: per-line flush should mean no
  lost lines. This is the crash-survival claim and the reason it is flushed every line.
- On a network: the `.log` file also shows in the dashboard DEV files list and downloads as text/plain.
- WRONG: file missing, file empty, file stops several lines before the crash, or a stall/hitch that tracks
  the logging rate (would mean per-line flush is too expensive; tell me and I will batch-flush on a timer).

## 2. Log severity + Download (low risk, browser only)
- WARN+ / ERROR+ / CRIT filter buttons filter the live stream; Download log saves exactly what is filtered.
- WRONG: filter shows nothing, or Download saves an empty / unfiltered file.

## 3. Punch list (calm page)
- Green "nothing needs fixing" when all checks pass; worst-first findings when not (e.g. no eye permission).
- WRONG: a known-bad state (pull the eye-tracking permission) still shows green.

## 4. Performance card (DEVICE-UNVERIFIED, this is the one to actually watch)
- DEV panel > Performance > Turn sampling ON. Note WHICH counters populate vs read "n/a" on the ML2:
  - Expected solid: frame time, memory (alloc/RSS/peak), storage free, battery.
  - OPEN on ML2: XR compositor stats (GPU app/compositor ms, dropped frames) and Android thermal status.
    Report which of these come through. They are the crash/overheat counters and we do not yet know if the
    ML2 runtime publishes them.
  - Draw calls / SetPass / GC-per-frame only report in a DEVELOPMENT build; "n/a" in a release build is
    correct, not a bug.
- Turn sampling OFF before recording a real trial (telemetry costs frame time).
- WRONG: the card shows a full set of numbers while OFF (should be nearly empty), or numbers that never
  change while ON.

## 5. Eye-permission CRIT (code-only, built; verify on device)
- On the headset, DENY the eye-tracking permission popup on purpose for one run.
- Confirm a red "Eyes: permission DENIED" chip AND a red CRIT row on the punch list.
- Confirm the run `.log` file contains a `[GAZE:CRIT] Eye tracking permission denied` line; a granted run
  instead contains `[GAZE] Eye tracking permission granted`.
- WRONG: neither line appears (means the permission callback never fired, itself a finding), or a denial
  shows no CRIT.

## 6. Head-tracking-loss chip (code-only, built; verify on device)
- On the headset with tracking good: "Head tracking: ok" chip.
- Provoke tracking loss (cover the cameras, or face a blank/featureless area): chip flips to red "Head
  tracking: LOST" and appears on the punch list; recovers to ok when the pose returns.
- In the Editor / App Simulator (no HMD): NO head-tracking chip at all (the read returns null by design).
- WRONG: a permanent red "LOST" on device even with good tracking (would mean `CommonUsages.isTracked` is
  not the right signal on the ML2; tell me and I will switch to the XR `trackingState` flags).

## 7. PerfProfile asset
- `Assets/Scripts/TrialServer/Resources/PerfProfile.asset` was hand-authored. Confirm Unity imported it
  cleanly (open it: fields populated, no "script missing"). If it is broken, delete it; the monitor falls
  back to built-in defaults with one warning.

---

## NOT built yet (decide if you want these next)
- **Bridge reflection WARN.** `SequenceBridge` reads the study's private trial tables by reflection; when a
  lookup misses, `Pool`/`Wireframe` silently read 0/false and the dashboard's trial readout is guessing
  rather than reading. A one-time WARN when a reflection lookup fails would make that visible. Code-only,
  but needs a careful read of the reflection sites, so it is proposed rather than done.
