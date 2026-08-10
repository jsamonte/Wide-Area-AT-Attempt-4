# Analysis pipeline

Raw headset output → analysis-ready tables, with no manual steps.

## Quick start

```bash
# 1. Verify the pipeline works, with no real data at all
Rscript analysis/run_selftest.R

# 2. When data arrives: copy the headset's trials/ folder to data/trials/
Rscript analysis/run_reduction.R
```

Requires `readr`, `dplyr`, `tidyr`, `purrr`, `stringr`, `tibble` (i.e. tidyverse).

Before the first real run, set `cfg$conditions` in [R/00_config.R](R/00_config.R) to your
actual condition strings. `TrialController` accepts free text from the operator, so those
are the only spellings the pipeline will accept — anything else is reported as a
data-entry error rather than absorbed as a new factor level.

## Files

| | |
|---|---|
| [R/00_config.R](R/00_config.R) | Every path and threshold. Nothing downstream hard-codes a number. |
| [R/01_manifest.R](R/01_manifest.R) | Reads `trials_index.csv`, applies the exclusion policy, one row per attempt with a verdict. |
| [R/02_reduce_gaze.R](R/02_reduce_gaze.R) | The ~60 Hz → one-row-per-trial reduction. |
| [R/03_flow.R](R/03_flow.R) | The CONSORT-style exclusion report. |
| [R/90_simulate.R](R/90_simulate.R) | Synthetic trial files in DataLogger's exact format. |
| [run_reduction.R](run_reduction.R) | Entry point for real data. |
| [run_selftest.R](run_selftest.R) | Simulate → reduce → assert. Run this first. |

## Outputs (`analysis/out/`)

- `trial_gaze_metrics.csv` — **the modelling input.** One row per included trial.
- `trial_manifest.csv` — every attempt on disk, included or not, with the reason.
- `trial_aoi_dwell.csv` — per-trial × per-AOI dwell detail.
- `exclusions.csv` — machine-readable exclusion list (supplementary table).
- `exclusion_flow.txt` — the numbers for the Methods section.
- `session_info.txt` — package versions that produced the run.

## Three things this reduction gets right

**Time is summed from inter-row intervals, never counted in rows.** Frames drop. A
59-row second is not 59/60 of a second. `DataLogger`'s own header says the analysis must
never trust the frame counter, and this doesn't.

**Pauses and stalls are excised.** `t_sec` runs on wall clock, but no rows are written
while a trial is paused — so a pause is one enormous inter-row interval. Counting it
would credit the participant with minutes of dwell on whatever they happened to be
looking at when the operator hit pause. Intervals above `cfg$max_frame_gap_s` (0.2 s) are
treated as unobserved: excluded from every duration, and they break any dwell or eye
event that spans them.

**Missing means missing.** `DataLogger.F()` writes an empty cell — never a zero, never
NaN — for anything not measured. Saccade velocity is empty during every fixation. Read
as 0, that one column would drag mean peak velocity toward zero across the whole study.
`na = c("", "NA")` throughout, and column types are declared rather than guessed.

## Exclusion policy

Implemented automatically in `01_manifest.R` and `02_reduce_gaze.R`; the counts print to
`exclusion_flow.txt` on every run.

> Trials flagged invalid by the operator during the session were excluded. Where a
> spoiled trial was re-run, the retake replaced it (an invalid trial does not consume its
> trial number, so the retake occupies the same design cell). Files stranded in
> `_incomplete/` — recordings interrupted by an application crash or quit, which never
> reached a classified close — were excluded. Trials in which more than 20% of trial time
> lacked usable gaze (tracking active, gaze valid, and confidence at or above the floor
> recorded in that trial's own file header) were excluded from gaze analyses but retained
> for survey and recall analyses, since eye-tracker quality is uninformative about whether
> a participant completed a questionnaire or a recall sheet.

The knobs marked `PREREGISTER` in `00_config.R` — the 20% gaze threshold, the 0.10 s
look-merge window, the 0.10 s minimum look duration — are analysis decisions. Fix them
before looking at real data.

## Caveats worth knowing

**Pupil diameter is not measured.** The Magic Leap OpenXR path in use returns one
combined gaze pose and no pupillometry, so `EyeAndHeadTracker` writes
`pupilDiameterMm = -1` for both eyes, and the reduction maps that sentinel to NA.
`pupil_l_mean_mm` / `pupil_r_mean_mm` / `pupil_mean_mm` therefore exist only as evidence
that the value stayed missing: **anything non-NA in them means the logger changed**, and
the Methods section's claim needs revisiting before those numbers go anywhere. Raw TLX
is the workload measure. Even if pupillometry were available it would be uninterpretable
here, because an AR overlay condition lights up the display and pupil size tracks scene
luminance far more strongly than cognitive effort.

By the same token the `leftEye` / `rightEye` blocks in the raw stream are the combined
gaze pose offset by half an assumed 64 mm IPD, not two measured eyes. Do not compute
vergence, IPD, or any left-vs-right contrast from them: the answer is the constant
0.064.

**Behaviour strings** (`Fixation` / `Saccade` / `Blink`) come from stringifying a Magic
Leap enum, and the spelling has changed across SDK versions. Matching is a
case-insensitive substring test, so `kFixation` and `Fixation` both work.

**The Unity `GazeAttentionAnalyzer`** is still useful as a during-the-session sanity
check. It is no longer part of the analysis path. Its numbers will not match this
pipeline exactly, because it does not excise pause gaps.

## Surveys and recall

```bash
Rscript analysis/run_selftest_surveys.R
```

| | |
|---|---|
| [R/10_surveys.R](R/10_surveys.R) | TLX, clutter, MEC-SPQ, SBSOD, SART, SSQ, wireframe, brightness. |
| [R/11_recall.R](R/11_recall.R) | Signal detection: d′, criterion, A′, plus the item-level GLMM table. |
| [R/12_join.R](R/12_join.R) | Design-table join — the only thing that attaches condition to a response. |
| [design/design_table.csv](design/design_table.csv) | Generated from `SequenceManager.cs`; balance verified. |
| [design/recall_key.csv](design/recall_key.csv) | **You fill in** `item_label` and `present`, 120 rows. |

Design: `wireframe` ON/OFF is the independent variable (2 trials each, within-subject);
`pool` 1–4 is the counterbalancing nuisance factor; `set_id = pool`.

Scoring decisions worth knowing: TLX Performance is **not** reverse-coded (the item is
already worded higher-is-worse); SBSOD reverses 8 of 15 items; the loglinear correction
is applied to every trial, not just ceiling ones; brightness items are bipolar and
scored as deviation from the midpoint. Post-study measures (SART, SSQ, wireframe
impressions) are collected once per participant and **cannot** enter the condition model.

## Still needed before modelling

- **the recall answer key** — `item_label` and `present` for all 120 rows
- **the recall transcriptions** — paper sheets typed into `recall_responses.csv`
- **the NDJSON reduction** — `02_reduce_gaze.R` currently targets the disabled
  `DataLogger` CSV path; the live writer is `EyeAndHeadTracker`
