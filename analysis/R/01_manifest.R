# =====================================================================================
# 01_manifest.R  --  Build the trial manifest and apply the exclusion policy.
#
# The manifest is one row per trial ATTEMPT that exists on disk, carrying whether it is
# included, and if not, exactly why. Nothing is deleted and nothing is filtered away
# silently: every attempt the study produced appears in the manifest with a verdict, so
# the exclusion counts in the paper are read off a table rather than reconstructed from
# memory.
#
# Policy implemented (from the analysis plan):
#   - Trials flagged invalid are excluded.
#   - Redone trials replace the spoiled attempt.
#   - Incomplete files (stranded in _incomplete/ by a crash) are excluded.
#   - Trials with >20% of frames missing valid gaze are excluded from GAZE analyses
#     but retained for survey and recall analyses.   <- applied in 02, needs the data
# =====================================================================================

suppressPackageStartupMessages({
  library(readr)
  library(dplyr)
  library(stringr)
})

# ---------------------------------------------------------------------------------
# trials_index.csv is the spine. DataLogger appends one row per classified close, so a
# multi-session pull is simply a longer file. Written by AppendIndex() with columns:
#   started_utc,ended_utc,participant,condition,trial,redo,valid,note,rows,duration_s,file
# ---------------------------------------------------------------------------------
read_trials_index <- function(path = cfg$index_path) {
  if (!file.exists(path)) {
    stop("No trials_index.csv at: ", normalizePath(path, mustWork = FALSE),
         "\nThis file is the spine of the analysis. Check cfg$trials_dir.")
  }

  idx <- readr::read_csv(
    path,
    na = c("", "NA"),
    col_types = readr::cols(
      started_utc = readr::col_character(),
      ended_utc   = readr::col_character(),
      participant = readr::col_character(),
      condition   = readr::col_character(),
      trial       = readr::col_integer(),
      redo        = readr::col_integer(),
      valid       = readr::col_integer(),
      note        = readr::col_character(),
      rows        = readr::col_integer(),
      duration_s  = readr::col_double(),
      file        = readr::col_character()
    )
  )

  idx %>%
    mutate(
      started_utc = as.POSIXct(started_utc, format = "%Y-%m-%d %H:%M:%S", tz = "UTC"),
      ended_utc   = as.POSIXct(ended_utc,   format = "%Y-%m-%d %H:%M:%S", tz = "UTC"),
      participant = str_trim(participant),
      # Canonicalise the operator's free text once, here. Everything downstream
      # compares against cfg$conditions.
      condition   = tolower(str_trim(condition)),
      redo        = coalesce(redo, 0L) == 1L,
      valid       = coalesce(valid, 0L) == 1L,
      note        = coalesce(note, ""),
      basename    = basename(coalesce(file, "")),
      path        = file.path(cfg$trials_dir, coalesce(file, ""))
    )
}

# ---------------------------------------------------------------------------------
# Files stranded in _incomplete/ never reached a classified close, so they have no
# index row at all: the app died, the battery went, or the operator quit mid-trial.
# They are real partial recordings and must be COUNTED even though they are excluded,
# or the flow diagram will not add up.
# ---------------------------------------------------------------------------------
scan_incomplete <- function(dir = cfg$dir_incomplete) {
  if (!dir.exists(dir)) return(tibble::tibble())
  files <- list.files(dir, pattern = "\\.csv$", full.names = TRUE)
  if (length(files) == 0) return(tibble::tibble())

  tibble::tibble(
    basename = basename(files),
    path     = files
  ) %>%
    # File names are  {participant}_{condition}_trial_{n}[_redo]_{stamp}.csv
    # (TrialController.TrialFileName). Parsing them is best-effort only: these files
    # are excluded regardless, and the parse exists so the flow report can say WHOSE
    # trial was lost rather than just "3 files".
    mutate(
      participant = str_match(basename, "^([^_]+)_")[, 2],
      trial       = suppressWarnings(as.integer(str_match(basename, "_trial_(\\d+)")[, 2])),
      condition   = tolower(str_match(basename, "^[^_]+_(.+?)_trial_")[, 2]),
      redo        = str_detect(basename, "_redo_"),
      valid       = FALSE,
      source      = "incomplete"
    )
}

# ---------------------------------------------------------------------------------
# The manifest: every attempt, with a verdict.
# ---------------------------------------------------------------------------------
build_manifest <- function(idx = read_trials_index(), incomplete = scan_incomplete()) {

  attempts <- idx %>%
    mutate(source = if_else(valid, "valid", "invalid")) %>%
    select(any_of(c("started_utc", "ended_utc", "participant", "condition", "trial",
                    "redo", "valid", "note", "rows", "duration_s",
                    "basename", "path", "source")))

  if (nrow(incomplete) > 0) {
    attempts <- bind_rows(attempts, incomplete %>% mutate(note = "stranded in _incomplete/"))
  }

  attempts <- attempts %>%
    mutate(
      exclude_reason = NA_character_,

      # 1. The file is listed but is not where the index says it is. This is a data
      #    integrity problem, not an exclusion: something went wrong in the transfer
      #    off the headset, and it should stop the analysis rather than quietly
      #    shrinking n.
      file_missing = !file.exists(path)
    )

  # ---- Rule: incomplete ----------------------------------------------------------
  if (isTRUE(cfg$exclude_incomplete)) {
    attempts <- attempts %>%
      mutate(exclude_reason = if_else(
        is.na(exclude_reason) & source == "incomplete",
        "incomplete_file", exclude_reason))
  }

  # ---- Rule: operator flagged invalid --------------------------------------------
  # Note this fires BEFORE the redo rule, which is what makes "the redo replaces the
  # spoiled attempt" work without any special casing: the spoiled attempt is already
  # gone, and the retake is simply the surviving trial for that cell.
  if (isTRUE(cfg$exclude_invalid_flag)) {
    attempts <- attempts %>%
      mutate(exclude_reason = if_else(
        is.na(exclude_reason) & source == "invalid",
        "operator_flagged_invalid", exclude_reason))
  }

  # ---- Rule: one surviving attempt per design cell --------------------------------
  # An invalid trial does not consume its trial number (TrialController.StopTrial), so
  # (participant, condition, trial) legitimately has several attempts. After the rules
  # above at most one should survive. If more than one does, that is an operator error
  # -- the same cell recorded twice as valid -- and the LATER one is kept, because the
  # standard cause is a valid-by-mistake stop followed by the real run.
  attempts <- attempts %>%
    group_by(participant, condition, trial) %>%
    arrange(started_utc, .by_group = TRUE) %>%
    mutate(
      .surviving = is.na(exclude_reason),
      .n_surviving = sum(.surviving),
      .is_last_surviving = .surviving & cumsum(.surviving) == .n_surviving,
      exclude_reason = if_else(
        .surviving & .n_surviving > 1 & !.is_last_surviving,
        "superseded_duplicate", exclude_reason)
    ) %>%
    ungroup() %>%
    select(-.surviving, -.n_surviving, -.is_last_surviving)

  # ---- Integrity check, not an exclusion ------------------------------------------
  attempts <- attempts %>%
    mutate(
      condition_ok = condition %in% cfg$conditions,
      exclude_reason = if_else(
        is.na(exclude_reason) & !condition_ok,
        "unrecognised_condition", exclude_reason),
      exclude_reason = if_else(
        is.na(exclude_reason) & file_missing,
        "file_missing_on_disk", exclude_reason),
      included = is.na(exclude_reason),
      # Gaze eligibility starts equal to inclusion and is narrowed in 02 once the
      # actual tracking loss per trial is known.
      use_for_gaze = included
    ) %>%
    arrange(participant, condition, trial, started_utc)

  attempts
}

# ---------------------------------------------------------------------------------
# Completeness audit. Reports, never drops. A participant missing a trial is something
# you want to know about while you can still do something, not on submission week.
# ---------------------------------------------------------------------------------
audit_completeness <- function(manifest) {
  manifest %>%
    filter(included) %>%
    count(participant, name = "n_included") %>%
    mutate(
      expected = cfg$n_trials_expected,
      complete = n_included == expected
    ) %>%
    arrange(participant)
}
