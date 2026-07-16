# TRIALSERVER BUGS (from device test, work tomorrow)

Captured at end of session. Raw list, prioritize tomorrow.

**STATUS 2026-07-16: all items below are FIXED IN CODE or DOCUMENTED, none device-verified.**
Items 1-8: written (arm model, end/pause/resume, bad-trial flags, 20-min limit banner, wording, condition
label). Item 2 was NOT a control bug: sequence select correctly starts the study's TUTORIAL (4 gems +
recording, the study's design); the dashboard now shows TUTORIAL as its own state. Items 9-11 + the docs
section: written into `Assets/Scripts/TrialServer/TRIAL_SERVER.md` under "Notes for the study's maintainer"
(9 and 10 are one fact: the eye permission was genuinely not granted, which is exactly why dwell did not
fire). Next: build, deploy, walk the verification list.

**SECOND DEVICE TEST 2026-07-16, addressed same day:** the paused trial clock now freezes on the page
instead of blanking (whether the trial rules should use the page's pause-excluding clock or the tracker's
pause-including one is flagged as an OPEN DECISION in TRIAL_SERVER.md). New findings documented for the
colleague in TRIAL_SERVER.md ("Findings from the second device test"): no distance cap on dwell destroys
(raycast is Mathf.Infinity; ~3 m rule needs a cap in EyeAndHeadTracker), dwell unreliability (zero-grace
timer reset + stale gaze pose on tracking dropout + tiny angular size at range), tutorial gems not
destroyable (ranked suspects: child-collider layer not propagated, RefreshTargetList count, the
eyeRaycastHitObject diagnostic), and the sequence-flow design question (no confirm step before the tutorial
auto-starts) to discuss with him. Server is otherwise ready for handoff prep.

## Behavior / control model
1. **Server should only ARM a trial, not start it.** The dashboard "Start" must arm/approve the queued trial;
   the USER (participant) starts it on device. Rework the command + phase so operator = arm, participant =
   start. Re-check the phase model (Ready -> Armed -> participant starts -> Recording).
2. **Clicking a Sequence button immediately started the trial.** Selecting a sequence should NOT begin a
   trial. It should only select the sequence (and let his tutorial run). Investigate: the sequence command is
   triggering the trial start. Likely the button wiring or phase logic. HIGH priority, core bug.
3. **Need a way to END a trial** from the dashboard. Not in the current build.
4. **Need PAUSE and RESUME** (his tracker has PauseRecording / ResumeRecording; wire commands to them).
5. **Need bad/failed-trial tracking** (like the old flight dashboard's Stop: BAD with a reason). A way to mark
   a run invalid.
6. **Trial timer.** A trial should not exceed ~20 min. Add either an auto-end or an investigator notification
   at a threshold. Show elapsed vs limit on the dashboard.

## Wording / pronouns
7. **Fix pronoun/wording so it does not read as if I (Thomas) run the server.** The dashboard and docs should
   not imply the operator is "me." Neutral phrasing. Sweep dashboard text, TRIAL_SERVER.md, and comments.

## Trial display / identifiers
8. **Show trial identifiers/conditions clearly:** wireframe active or not, and Night vs Dusk. His conditions
   are (Night/Dusk) x (Wireframe on/off). Make the dashboard show the full condition label per trial. (Cards
   exist for pool/wireframe/time-of-day; make the condition explicit and prominent.)

## Eye tracking / permissions (may be his scope; document for colleague)
9. **Eye permission showed "denied" on the dashboard, yet the app worked.** How is that allowed? On first
   launch the popup asked about photos/videos access (NOT clearly eye tracking). Later an eye popup appeared.
   Confirm the eye-tracking permission flow is actually granting, and that the dashboard's eyePermission
   reflects the true state. Possible: `GazeInputManager.EyeTrackingPermissionGranted` not yet true when read,
   or the permission request is for the wrong permission. Document for colleague if outside our scope.
10. **Eye gaze is NOT working on the gems** (dwell-destroy not triggering). Suspected causes to document/check:
    - ArUco markers may not be scanned yet, and the flow will not let scanning happen. Confirm the scan step.
    - Something in the scene may need to be enabled (GazeInputManager present/active? layers? colliders?).
    - Tie to bug 9: if eye permission is not truly granted, dwell cannot work.
    Document this clearly for the colleague; likely his scene setup.

## Study content (document for colleague, likely his change)
11. **Wireframe spawns for ALL sequences.** Looks like the wireframe appears regardless of sequence. He may
    change this later. Just document it.

## Confirmed working
- Starting the trial on the device works and the webpage reflects the state correctly.
- The pop up did eventually show up (permissions).

## Docs to add (README / colleague writeup)
- How he can add more debugging logs that surface on the website (tag a Debug.Log with `[TAG]` and it shows
  in the DEV log stream with a filter button). Give a short how-to.
- If he wants eye-confidence / calibration signals on the dashboard (to know calibration worked), document how
  to expose them (his tracker/GazeInputManager would need to surface confidence; add to StatusSnapshot ready
  block). Note current build only has eyePermission/gazeManager/tracker presence.
- Document the ArUco-scan prerequisite for eye-gaze/gem interaction (bug 10).
