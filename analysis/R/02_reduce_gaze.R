# =====================================================================================
# 02_reduce_gaze.R  --  Reduce the raw ~60 Hz trial files to one row per trial.
#
# This replaces the Unity GazeAttentionAnalyzer as the load-bearing analysis step. That
# tool stays useful as a during-the-study sanity check, but it is driven by hand through
# an Editor window, so it cannot be re-run identically three months from now. Everything
# here is scripted, versioned, and reproducible from the raw files alone.
#
# THREE THINGS THIS FILE GETS RIGHT, AND THEY ARE THE WHOLE REASON IT IS LONGER THAN
# A GROUP_BY:
#
#   1. Time is summed from inter-row intervals, never counted in rows. Frames drop, and
#      a 59-row second is not a 59/60 second. DataLogger's own header comment makes this
#      point: "the analysis must never have to trust the frame counter".
#
#   2. Pauses and stalls are excised. t_sec runs on wall clock (realtimeSinceStartup) but
#      no rows are written while Paused, so a pause appears as one enormous inter-row
#      interval. Counting it would credit the participant with minutes of dwell on
#      whatever they happened to be looking at when the operator hit pause.
#
#   3. Missing means missing. DataLogger writes an EMPTY CELL, never a zero and never
#      NaN, for anything not measured (see its F() helper). Reading those as 0 would
#      drag every mean toward zero. na = c("", "NA") throughout, and every summary is
#      computed over the rows that actually have the measurement.
# =====================================================================================

suppressPackageStartupMessages({
  library(readr)
  library(dplyr)
  library(tidyr)
  library(purrr)
  library(stringr)
  library(tibble)
})

# Null/empty coalesce, for the study-parameter header lookups below.
`%||%` <- function(a, b) if (is.null(a) || length(a) == 0L || all(is.na(a))) b else a

# ---------------------------------------------------------------------------------
# Reading a trial file
# ---------------------------------------------------------------------------------

# The file opens with '#' comment lines carrying the study parameters in force for THAT
# trial. They are not decoration: the gaze confidence floor and the dwell-to-confirm
# threshold decide what the numbers mean, and two trials recorded under different values
# are not directly comparable. We read them, and we carry them into the output so the
# reduced table can be audited for exactly that.
#
# Counting the comment lines and using skip= is deliberate, rather than readr's
# comment='#'. comment= applies inside quoted fields too, so a '#' in a marker or an AOI
# key would silently truncate a data row.
count_header_comments <- function(path, max_scan = 500L) {
  con <- file(path, "r")
  on.exit(close(con), add = TRUE)
  n <- 0L
  repeat {
    line <- readLines(con, n = 1L, warn = FALSE)
    if (length(line) == 0L) break
    if (!startsWith(line, "#")) break
    n <- n + 1L
    if (n >= max_scan) break
  }
  n
}

read_trial_params <- function(path, n_comments = count_header_comments(path)) {
  if (n_comments == 0L) return(list())
  lines <- readLines(path, n = n_comments, warn = FALSE)
  kv <- lines[str_detect(lines, "=")]
  if (length(kv) == 0L) return(list())

  body <- str_trim(str_remove(kv, "^#\\s*"))
  keys <- str_trim(str_remove(body, "=.*$"))
  vals <- str_trim(str_remove(body, "^[^=]*="))
  # AppendIndex marks device-overridden knobs inline; strip the annotation, keep the value.
  vals <- str_trim(str_remove(vals, "\\(overridden on device\\)\\s*$"))

  setNames(as.list(vals), keys)
}

# Column types are declared, not guessed. `marker` is empty on almost every row, and
# readr's type guesser reads an all-empty character column as logical -- which then
# throws a parse failure the moment TRIAL_START appears 3000 rows in, past the guess
# window. Everything in this file is numeric except three genuine strings.
trial_col_types <- readr::cols(
  .default = readr::col_double(),
  marker   = readr::col_character(),
  behavior = readr::col_character(),
  aoi      = readr::col_character()
)

read_trial_file <- function(path) {
  n_comments <- count_header_comments(path)
  df <- readr::read_csv(
    path,
    skip      = n_comments,
    na        = c("", "NA"),
    col_types = trial_col_types,
    progress  = FALSE
  )
  attr(df, "params") <- read_trial_params(path, n_comments)
  df
}

# ---------------------------------------------------------------------------------
# Frame timing: the foundation everything else is weighted by.
# ---------------------------------------------------------------------------------
# dt[i] is the interval from row i to row i+1, i.e. how long the state described by row
# i persisted. The last row has no successor; it gets the median interval, which is the
# smallest assumption available and affects one frame in ~36,000.
add_frame_timing <- function(df, max_gap_s = cfg$max_frame_gap_s) {
  n <- nrow(df)
  if (n == 0L) return(df %>% mutate(dt = numeric(0), is_gap = logical(0), dt_eff = numeric(0)))

  raw_dt <- if (n > 1L) diff(df$t_sec) else numeric(0)
  med <- if (length(raw_dt) > 0) stats::median(raw_dt, na.rm = TRUE) else NA_real_
  dt <- c(raw_dt, if (is.na(med)) 0 else med)

  # Guard against a non-monotonic clock. t_sec comes from a monotonic source so this
  # should never fire, but a negative interval would silently subtract dwell time.
  dt[!is.finite(dt) | dt < 0] <- 0

  is_gap <- dt > max_gap_s

  df %>% mutate(
    dt     = dt,
    is_gap = is_gap,
    dt_eff = if_else(is_gap, 0, dt)   # gap time is not observed time
  )
}

# ---------------------------------------------------------------------------------
# Run-length segmentation, gap-aware.
# ---------------------------------------------------------------------------------
# Used for both AOI dwells and eye-behavior events. A run breaks when the value changes,
# and ALSO when the preceding interval was a gap: a dwell cannot span a pause.
segment_runs <- function(value, prev_was_gap) {
  n <- length(value)
  if (n == 0L) return(integer(0))
  v <- ifelse(is.na(value), "", value)
  changed <- c(TRUE, v[-1] != v[-n])
  cumsum(changed | prev_was_gap)
}

# TRUE when the interval ENDING at row i was a gap (i.e. row i-1 -> row i).
prev_gap_flag <- function(is_gap) {
  n <- length(is_gap)
  if (n == 0L) return(logical(0))
  c(FALSE, is_gap[-n])
}

# ---------------------------------------------------------------------------------
# AOI looks
# ---------------------------------------------------------------------------------
# A "look" is a run of consecutive frames attributed to the same AOI. Two refinements,
# both preregistered knobs in 00_config.R:
#   - looks at the same AOI separated by a sub-threshold gap are merged (a blink
#     mid-look is one look, not two)
#   - looks shorter than the floor are dropped (the ray clipping an object's edge on its
#     way past is not looking at it)
extract_looks <- function(df,
                          gap_merge_s = cfg$aoi_gap_merge_s,
                          min_look_s  = cfg$aoi_min_look_s) {

  empty <- tibble(aoi = character(), t_start = numeric(), t_end = numeric(),
                  duration_s = numeric())
  if (nrow(df) == 0L) return(empty)

  runs <- df %>%
    mutate(
      .prev_gap = prev_gap_flag(is_gap),
      .run      = segment_runs(aoi, .prev_gap),
      .aoi      = ifelse(is.na(aoi), "", aoi)
    ) %>%
    group_by(.run) %>%
    summarise(
      aoi        = first(.aoi),
      t_start    = first(t_sec),
      duration_s = sum(dt_eff),
      .groups    = "drop"
    ) %>%
    mutate(t_end = t_start + duration_s) %>%
    filter(aoi != "")

  if (nrow(runs) == 0L) return(empty)

  # Merge same-AOI looks separated by less than gap_merge_s of anything else.
  runs <- runs %>% arrange(t_start)
  merged <- vector("list", nrow(runs))
  k <- 0L
  for (i in seq_len(nrow(runs))) {
    r <- runs[i, ]
    if (k > 0L &&
        merged[[k]]$aoi == r$aoi &&
        (r$t_start - merged[[k]]$t_end) < gap_merge_s) {
      merged[[k]]$t_end      <- r$t_end
      merged[[k]]$duration_s <- merged[[k]]$duration_s + r$duration_s
    } else {
      k <- k + 1L
      merged[[k]] <- r
    }
  }

  bind_rows(merged[seq_len(k)]) %>%
    filter(duration_s >= min_look_s) %>%
    select(aoi, t_start, t_end, duration_s)
}

# ---------------------------------------------------------------------------------
# Eye-behavior events (fixation / saccade / blink)
# ---------------------------------------------------------------------------------
# The device reports the CURRENT event on every frame, so one saccade occupies many
# rows. Events are therefore de-duplicated into runs, and the device's own reported
# duration is used rather than a frame count.
#
# Two adjacent events of the same type would merge into one run on the type alone, so a
# decrease in behavior_dur_ms -- the device restarting its clock for a new event -- also
# breaks the run.
#
# The behavior string is whatever MLGazeRecognition's enum stringifies to, which has
# varied across SDK versions ("Fixation" / "kFixation"). Matching is therefore a
# case-insensitive substring test, not equality.
extract_eye_events <- function(df) {
  empty <- tibble(type = character(), t_start = numeric(), duration_ms = numeric(),
                  amplitude_deg = numeric(), peak_velocity_dps = numeric())
  if (nrow(df) == 0L || !"behavior" %in% names(df)) return(empty)

  d <- df %>% filter(!is.na(behavior), behavior != "")
  if (nrow(d) == 0L) return(empty)

  d <- d %>%
    mutate(
      # Gaps are recomputed on the FILTERED rows: dropping the no-event rows can leave
      # two kept rows seconds apart, and those must not be joined into one event.
      .prev_gap  = c(FALSE, diff(t_sec) > cfg$max_frame_gap_s),
      .dur       = coalesce(behavior_dur_ms, 0),
      .restarted = c(FALSE, diff(.dur) < 0),
      .run       = segment_runs(behavior, .prev_gap | .restarted)
    )

  d %>%
    group_by(.run) %>%
    summarise(
      type              = first(behavior),
      t_start           = first(t_sec),
      # Duration accumulates within an event, so the last/greatest value is the
      # device's final word on it.
      duration_ms       = suppressWarnings(max(behavior_dur_ms, na.rm = TRUE)),
      amplitude_deg     = suppressWarnings(max(behavior_amp_deg, na.rm = TRUE)),
      # Peak velocity is the standard saccade metric; the per-frame column is NaN
      # (empty) during fixations, which read as NA and drop out here rather than
      # dragging a mean toward zero.
      peak_velocity_dps = suppressWarnings(max(behavior_vel_dps, na.rm = TRUE)),
      .groups           = "drop"
    ) %>%
    mutate(across(c(duration_ms, amplitude_deg, peak_velocity_dps),
                  ~ if_else(is.finite(.x), .x, NA_real_))) %>%
    select(type, t_start, duration_ms, amplitude_deg, peak_velocity_dps)
}

# ---------------------------------------------------------------------------------
# Head movement
# ---------------------------------------------------------------------------------
# Euler angles wrap at 360, so a turn from 359 to 1 degree is +2, not -358. Every
# angular difference goes through this.
angle_diff_deg <- function(a, b) {
  ((a - b + 180) %% 360) - 180
}

summarise_head <- function(df) {
  if (nrow(df) < 2L) {
    return(tibble(head_path_m = NA_real_, head_path_xz_m = NA_real_,
                  head_speed_mean_mps = NA_real_,
                  head_yaw_travel_deg = NA_real_, head_yaw_rate_mean_dps = NA_real_))
  }

  n <- nrow(df)
  keep <- !df$is_gap[-n]     # intervals that are not gaps

  step   <- sqrt(diff(df$head_x)^2 + diff(df$head_y)^2 + diff(df$head_z)^2)
  step_h <- sqrt(diff(df$head_x)^2 + diff(df$head_z)^2)
  yaw  <- abs(angle_diff_deg(df$head_yaw[-1], df$head_yaw[-n]))
  dt   <- df$dt[-n]

  path <- sum(step[keep],   na.rm = TRUE)
  path_h <- sum(step_h[keep], na.rm = TRUE)
  turn <- sum(yaw[keep],  na.rm = TRUE)
  time <- sum(dt[keep],   na.rm = TRUE)

  tibble(
    # Locomotion. In a wide-area study this is a first-class outcome, not a covariate:
    # how much ground someone covered to build the same mental map is the point.
    head_path_m            = path,
    # Ground-plane distance travelled, ignoring vertical head movement. This is the
    # numerator of the efficiency ratio: the ideal-route denominator authored by
    # GemOptimalPathCalculator is a planar tour, so the two have to be measured in the
    # same plane. It is also the more honest locomotion number on its own -- head_path_m
    # accumulates a few centimetres of head bob per step at 60 Hz, which is gait, not
    # route choice, and it inflates with trial duration rather than with distance covered.
    head_path_xz_m         = path_h,
    head_speed_mean_mps    = if (time > 0) path / time else NA_real_,
    # Cumulative yaw: how much visual search the participant did by turning.
    head_yaw_travel_deg    = turn,
    head_yaw_rate_mean_dps = if (time > 0) turn / time else NA_real_
  )
}

# ---------------------------------------------------------------------------------
# One trial -> one row
# ---------------------------------------------------------------------------------
reduce_trial <- function(path,
                         gaze_min_conf_fallback = cfg$gaze_min_conf_fallback) {

  df <- read_trial_file(path)
  params <- attr(df, "params")

  # The confidence floor that GazeAoi actually used for THIS trial, straight from the
  # file's own header. Falling back to a config default is a last resort, and the output
  # records which happened so a mixed-parameter dataset cannot be analysed unknowingly.
  # The exact key is whatever DevSettingsStore registered it as, and that name has no
  # guarantee of stability across builds. Match on shape, not on a literal string, so a
  # rename in Unity degrades to the documented fallback instead of silently changing the
  # confidence floor the analysis applies.
  conf_key <- grep("gaze.*min.*conf", names(params), ignore.case = TRUE, value = TRUE)
  hdr_conf <- if (length(conf_key) >= 1) suppressWarnings(as.numeric(params[[conf_key[1]]])) else NA_real_
  conf_from_header <- length(hdr_conf) == 1L && is.finite(hdr_conf)
  min_conf <- if (conf_from_header) hdr_conf else gaze_min_conf_fallback

  df <- add_frame_timing(df)

  n_rows      <- nrow(df)
  duration_s  <- sum(df$dt_eff)
  gap_time_s  <- sum(df$dt[df$is_gap])
  n_gaps      <- sum(df$is_gap)
  wall_span_s <- if (n_rows > 0) max(df$t_sec) - min(df$t_sec) else NA_real_

  # Usable gaze, by the same rule GazeAoi.cs applies live (Update(), line 39):
  # tracking AND valid AND confidence at or above the floor.
  gaze_ok <- df$eye_tracking == 1 & df$gaze_valid == 1 &
    coalesce(df$gaze_conf, -Inf) >= min_conf
  gaze_ok[is.na(gaze_ok)] <- FALSE

  gaze_time_s      <- sum(df$dt_eff[gaze_ok])
  prop_gaze_usable <- if (duration_s > 0) gaze_time_s / duration_s else NA_real_

  looks  <- extract_looks(df)
  events <- extract_eye_events(df)

  aoi_time_s <- sum(looks$duration_s)

  wmean <- function(x, w) {
    ok <- is.finite(x) & is.finite(w) & w > 0
    if (!any(ok)) return(NA_real_)
    sum(x[ok] * w[ok]) / sum(w[ok])
  }

  # The logger writes -1 for an eye metric the hardware does not expose. A negative
  # diameter or openness is physically impossible, so the sentinel is unambiguous and is
  # mapped to NA here rather than being averaged into a number.
  drop_sentinel <- function(x) if_else(!is.na(x) & x < 0, NA_real_, x)

  ev_type <- tolower(events$type %||% character(0))
  is_fix  <- str_detect(ev_type, "fixation")
  is_sac  <- str_detect(ev_type, "saccade")
  is_blk  <- str_detect(ev_type, "blink")
  minutes <- duration_s / 60

  markers <- df %>%
    filter(!is.na(marker), marker != "") %>%
    select(t_sec, marker)

  core <- tibble(
    file = basename(path),

    # ---- Trial timing --------------------------------------------------------
    n_rows              = n_rows,
    duration_s          = duration_s,
    wall_span_s         = wall_span_s,
    gap_time_s          = gap_time_s,
    n_gaps              = n_gaps,
    effective_hz        = if (duration_s > 0) n_rows / duration_s else NA_real_,

    # ---- Tracking quality ----------------------------------------------------
    prop_gaze_usable    = prop_gaze_usable,
    missing_gaze_frac   = 1 - prop_gaze_usable,
    gaze_min_conf_used  = min_conf,
    conf_from_header    = conf_from_header,

    # ---- Attention ------------------------------------------------------------
    aoi_time_s          = aoi_time_s,
    prop_time_on_aoi    = if (duration_s > 0) aoi_time_s / duration_s else NA_real_,
    # Denominator matters here. Share of USABLE GAZE time is the honest attention
    # measure; share of trial time confounds attention with tracker dropout.
    prop_gaze_on_aoi    = if (gaze_time_s > 0) aoi_time_s / gaze_time_s else NA_real_,
    time_on_nothing_s   = max(0, gaze_time_s - aoi_time_s),
    n_looks             = nrow(looks),
    n_aoi_visited       = dplyr::n_distinct(looks$aoi),
    mean_look_s         = if (nrow(looks) > 0) mean(looks$duration_s) else NA_real_,
    longest_look_s      = if (nrow(looks) > 0) max(looks$duration_s) else NA_real_,
    # Switches between distinct AOIs: a scanning strategy reads differently from a
    # settling one even at identical total dwell.
    n_aoi_switches      = if (nrow(looks) > 1) sum(looks$aoi[-1] != looks$aoi[-nrow(looks)]) else 0,

    # ---- Eye behaviour --------------------------------------------------------
    n_fixations         = sum(is_fix),
    mean_fixation_ms    = if (any(is_fix)) mean(events$duration_ms[is_fix], na.rm = TRUE) else NA_real_,
    n_saccades          = sum(is_sac),
    mean_saccade_amp_deg = if (any(is_sac)) mean(events$amplitude_deg[is_sac], na.rm = TRUE) else NA_real_,
    mean_saccade_peak_vel_dps = if (any(is_sac)) mean(events$peak_velocity_dps[is_sac], na.rm = TRUE) else NA_real_,
    n_blinks            = sum(is_blk),
    blink_rate_per_min  = if (minutes > 0) sum(is_blk) / minutes else NA_real_,

    # ---- Pupil ----------------------------------------------------------------
    # NOT A MEASURE IN THIS STUDY, and these columns exist only to prove it stayed
    # missing. The Magic Leap OpenXR path in use (EyeTrackingUsages.gazePosition/
    # gazeRotation) returns one combined gaze pose and no pupillometry at all, so
    # EyeAndHeadTracker writes pupilDiameterMm = -1 for both eyes. drop_sentinel turns
    # that -1 into NA: averaged as a number it would produce a stable, plausible-looking
    # -1.00 mm per trial, which is exactly the kind of value that survives into a table.
    # An earlier build wrote a hardcoded 3.4/3.5 mm instead, which is worse -- it is
    # indistinguishable from a real reading. Anything non-NA in these columns means the
    # logger changed and the claim in the Methods section needs revisiting.
    pupil_l_mean_mm     = wmean(drop_sentinel(df$pupil_l_mm)[gaze_ok], df$dt_eff[gaze_ok]),
    pupil_r_mean_mm     = wmean(drop_sentinel(df$pupil_r_mm)[gaze_ok], df$dt_eff[gaze_ok]),

    # ---- Provenance -----------------------------------------------------------
    n_markers           = nrow(markers),
    had_tracking_lost   = any(str_detect(markers$marker, "TRACKING_LOST")),
    recorded_utc        = params[["recorded_utc"]] %||% NA_character_,
    app_version         = params[["app_version"]] %||% NA_character_
  )

  # bind_cols rather than splicing summarise_head() inside tibble(): explicit, and it
  # does not depend on tibble's auto-splice behaviour for unnamed data frames.
  dplyr::bind_cols(core, summarise_head(df)) %>%
    mutate(pupil_mean_mm = rowMeans(cbind(pupil_l_mean_mm, pupil_r_mean_mm), na.rm = TRUE)) %>%
    mutate(pupil_mean_mm = if_else(is.nan(pupil_mean_mm), NA_real_, pupil_mean_mm))
}

# Per-AOI detail, kept alongside the per-trial row. This is the table you plot when a
# reviewer asks WHERE the attention went, rather than how much of it there was.
reduce_trial_aoi <- function(path) {
  df <- add_frame_timing(read_trial_file(path))
  looks <- extract_looks(df)
  if (nrow(looks) == 0L) return(tibble())

  total <- sum(looks$duration_s)
  looks %>%
    group_by(aoi) %>%
    summarise(
      dwell_s        = sum(duration_s),
      n_looks        = dplyr::n(),
      longest_look_s = max(duration_s),
      first_look_s   = min(t_start),
      .groups        = "drop"
    ) %>%
    mutate(
      prop_of_aoi_time = dwell_s / total,
      file             = basename(path)
    ) %>%
    arrange(desc(dwell_s))
}

# ---------------------------------------------------------------------------------
# Drive the reduction across every included trial, and apply the gaze-quality rule.
# ---------------------------------------------------------------------------------
reduce_all_trials <- function(manifest) {

  todo <- manifest %>% filter(included)
  if (nrow(todo) == 0L) stop("No included trials to reduce. Check the exclusion flow.")

  message("Reducing ", nrow(todo), " trial file(s)...")

  safely_reduce <- function(p) {
    tryCatch(reduce_trial(p),
             error = function(e) {
               warning("Failed to reduce ", basename(p), ": ", conditionMessage(e),
                       call. = FALSE)
               tibble(file = basename(p), reduce_error = conditionMessage(e))
             })
  }

  metrics <- purrr::map_dfr(todo$path, safely_reduce)

  # map_dfr only creates this column if something actually failed; downstream code
  # references it unconditionally, so materialise it.
  if (!"reduce_error" %in% names(metrics)) metrics$reduce_error <- NA_character_

  out <- todo %>%
    select(participant, condition, trial, redo, basename, path, started_utc,
           rows_index = rows, duration_index = duration_s) %>%
    left_join(metrics, by = c("basename" = "file"))

  # ---- The >20% rule, applied here because it needs the reduced data ------------
  # GAZE analyses only. Survey and recall outcomes keep the trial: a tracker that lost
  # calibration says nothing about whether the participant filled in their recall sheet.
  out <- out %>%
    mutate(
      gaze_quality_fail = !is.na(missing_gaze_frac) & missing_gaze_frac > cfg$max_missing_gaze,
      reduce_failed     = !is.na(reduce_error),
      use_for_gaze      = !gaze_quality_fail & !reduce_failed,
      gaze_exclude_reason = case_when(
        reduce_failed     ~ "reduction_error",
        gaze_quality_fail ~ sprintf("gaze_loss_%.0f%%", 100 * missing_gaze_frac),
        TRUE              ~ NA_character_
      )
    )

  # A row-count cross-check against the index. These should agree exactly; a mismatch
  # means the file on disk is not the file the index describes.
  mismatch <- out %>% filter(!is.na(rows_index), !is.na(n_rows), rows_index != n_rows)
  if (nrow(mismatch) > 0) {
    warning(nrow(mismatch), " trial(s) have a row count that disagrees with ",
            "trials_index.csv. Inspect before trusting these files.", call. = FALSE)
  }

  out
}

reduce_all_aoi <- function(manifest) {
  todo <- manifest %>% filter(included)
  purrr::map_dfr(todo$path, function(p) {
    res <- tryCatch(reduce_trial_aoi(p), error = function(e) tibble())
    res
  }) %>%
    left_join(
      todo %>% select(participant, condition, trial, basename),
      by = c("file" = "basename")
    )
}
