# =====================================================================================
# 00_config.R  --  Every path and every analysis knob, in one place.
#
# Nothing downstream hard-codes a threshold. If a number decides what the data MEAN,
# it lives here, it gets written into the run log, and changing it is a visible diff
# rather than an edit buried in a pipeline script.
#
# The values marked PREREGISTER are analysis decisions, not implementation details.
# Fix them before you look at real data.
# =====================================================================================

cfg <- list(

  # ---- Paths --------------------------------------------------------------------
  # Point trials_dir at the "trials" folder pulled off the headset. It is expected to
  # contain valid/, invalid/, _incomplete/ and trials_index.csv, exactly as
  # DataLogger.cs writes them.
  trials_dir = "data/trials",
  out_dir    = "analysis/out",

  # ---- Study design -------------------------------------------------------------
  # TODO: replace with your real condition strings. TrialController accepts free text
  # from the operator, so these are the ONLY spellings the pipeline will accept; any
  # other value is treated as a data-entry error and reported, never silently
  # absorbed as a new factor level.
  conditions = c("baseline", "wireframe", "sequence", "highlight"),

  # Trials each participant is expected to complete. Used only for the completeness
  # audit ("P04 has 3 of 4 trials"), never to drop anything.
  n_trials_expected = 4L,

  # ---- Frame timing -------------------------------------------------------------
  # Rows are ~60 Hz but NOT uniformly spaced, and t_sec keeps running while a trial is
  # paused (WriteRow is not called while Paused, so a pause appears as one huge
  # inter-row interval). Every duration in this pipeline is therefore a sum of
  # inter-row intervals, never n_rows / 60 and never max(t_sec).
  #
  # An interval longer than this is treated as a GAP: real wall-clock time during
  # which we have no idea what the participant was doing. Gap time is excluded from
  # all durations and breaks any dwell or eye-event that spans it.
  # 0.2 s = 12 dropped frames at 60 Hz. Generous enough to survive an ordinary hitch,
  # tight enough that a pause or a tracking stall can never be counted as dwell.
  max_frame_gap_s = 0.20,

  # ---- Gaze usability -----------------------------------------------------------
  # GazeAoi.cs only attributes gaze when eye_tracking & gaze_valid & conf >= min.
  # The trial file header records the value actually in force, and the pipeline reads
  # it per-file; this is the fallback when the header is missing or unparseable.
  gaze_min_conf_fallback = 1L,

  # PREREGISTER. Share of trial time without usable gaze, above which the trial is
  # dropped from GAZE analyses only. Survey and recall outcomes are unaffected: a
  # participant whose eye tracker calibration drifted still answered the forms and
  # still filled in the recall sheet, and throwing that away would be discarding good
  # data because a different instrument failed.
  max_missing_gaze = 0.20,

  # ---- Dwell / AOI construction -------------------------------------------------
  # PREREGISTER. Two looks at the same AOI separated by less than this are one look.
  # Without it, a single blink mid-look is scored as two looks and halves the mean
  # look duration.
  aoi_gap_merge_s = 0.10,

  # PREREGISTER. Looks shorter than this are discarded as ray-crossings: the gaze ray
  # clipping the edge of an object on its way somewhere else is not "looking at" it.
  aoi_min_look_s = 0.10,

  # ---- Exclusion rules (the written policy, applied automatically) --------------
  # These mirror the analysis plan verbatim. Set any to FALSE only with a reason you
  # are willing to write in the paper.
  exclude_invalid_flag  = TRUE,   # trials the operator filed as invalid
  exclude_incomplete    = TRUE,   # files stranded in _incomplete/ by a crash or quit
  redo_replaces_spoiled = TRUE,   # keep the retake, drop the attempt it replaced

  # ---- Design -------------------------------------------------------------------
  # Generated from SequenceManager.cs groupPools / groupWireframes. This is the ONLY
  # thing that says which condition a survey response belongs to: the Post-Trial form
  # records participant, group and trial number, never the condition.
  design_path = "analysis/design/design_table.csv",

  # ---- Recall -------------------------------------------------------------------
  recall_key_path       = "analysis/design/recall_key.csv",
  recall_responses_path = "analysis/design/recall_responses.csv",

  # PREREGISTER. How the three-way response maps onto a yes/no recognition decision.
  # "strict"  -> only "R" counts as remembered            (primary)
  # "lenient" -> "R" and "U" both count as remembered     (sensitivity analysis)
  # Both are always computed; this names which one is primary.
  recall_primary_policy = "strict",

  # PREREGISTER. Loglinear correction (Hautus 1995): add 0.5 to hits and false alarms
  # and 1 to each total, applied to EVERY trial rather than only the degenerate ones.
  # With 30 items a hit rate of 1.00 is likely, and uncorrected d' would be infinite.
  # Applying it selectively biases the cells it touches, so it is all or nothing.
  recall_loglinear = TRUE,

  # ---- Surveys ------------------------------------------------------------------
  # Google Forms exports use the full question text as the column name. Rather than
  # depend on exact punctuation surviving a copy-paste, columns are matched by regex
  # against distinctive fragments of each question (see 10_surveys.R). Drop a real
  # export here and the loader reports any question it could not match.
  survey_pre_path   = "data/surveys/pre_study.csv",
  survey_trial_path = "data/surveys/post_trial.csv",
  survey_post_path  = "data/surveys/post_study.csv",

  # ---- Reproducibility ----------------------------------------------------------
  seed = 20260803L
)

# Comparisons downstream are case- and whitespace-insensitive; canonicalise once here
# so "Baseline " and "baseline" cannot become two levels.
cfg$conditions <- tolower(trimws(cfg$conditions))

# Resolved subpaths, so no script builds a path by pasting strings of its own.
cfg$dir_valid      <- file.path(cfg$trials_dir, "valid")
cfg$dir_invalid    <- file.path(cfg$trials_dir, "invalid")
cfg$dir_incomplete <- file.path(cfg$trials_dir, "_incomplete")
cfg$index_path     <- file.path(cfg$trials_dir, "trials_index.csv")
