# =====================================================================================
# 90_simulate.R  --  Generate synthetic trial files in the EXACT format DataLogger writes.
#
# Purpose is not to model the study realistically. It is to exercise the pipeline against
# the shapes real data will take, including the awkward ones, so that the day the headset
# is unplugged the only thing that changes is cfg$trials_dir.
#
# The generated set deliberately contains every case the exclusion policy has to handle:
#   - a normal trial
#   - a trial containing a PAUSE (a multi-second hole in t_sec with no rows)
#   - a trial with heavy tracking loss, above the 20% gaze threshold
#   - a trial the operator flagged INVALID, plus the redo that replaced it
#   - a file stranded in _incomplete/ with no index row at all
#
# If the flow report does not account for all of those, the pipeline is wrong and you
# find out now rather than in November.
# =====================================================================================

suppressPackageStartupMessages({
  library(dplyr)
  library(readr)
})

# Column order is copied from DataLogger.Columns. If that array ever changes, this must
# change with it -- and the reduction will tell you, loudly, because the column it wants
# will be missing.
SIM_COLUMNS <- c(
  "t_sec", "unix_ms", "marker",
  "head_x", "head_y", "head_z", "head_pitch", "head_yaw", "head_roll",
  "eye_tracking", "gaze_valid", "gaze_conf",
  "gaze_ox", "gaze_oy", "gaze_oz", "gaze_dx", "gaze_dy", "gaze_dz",
  "fix_valid", "fix_x", "fix_y", "fix_z",
  "open_l", "open_r",
  "pupil_l_mm", "pupil_r_mm",
  "behavior", "behavior_amp_deg", "behavior_vel_dps", "behavior_dur_ms",
  "aoi", "aoi_dwell_s", "aoi_hit_dist_m"
)

SIM_AOIS <- c("target_crate", "target_barrel", "landmark_hydrant",
              "landmark_vending", "ar_waypoint", "ar_minimap")

# Mirrors DataLogger.F(): fixed decimals, and EMPTY -- never 0, never NaN -- for a value
# that was not measured. Reproducing this exactly is the point of the simulator; a
# simulator that writes zeros would hide the very bug the na= handling exists to prevent.
fmt <- function(x, decimals) {
  ifelse(is.na(x) | !is.finite(x), "", formatC(x, format = "f", digits = decimals))
}

simulate_trial_rows <- function(duration_s   = 300,
                                hz           = 60,
                                gaze_loss    = 0.05,
                                pause_at_s   = NA_real_,
                                pause_len_s  = 0,
                                aoi_rate     = 0.55) {

  dt <- 1 / hz
  n  <- floor(duration_s * hz)
  t  <- seq(0, by = dt, length.out = n)

  # Jitter, plus occasional dropped frames: the row count must never be trustworthy.
  t <- t + runif(n, 0, dt * 0.3)
  drop <- runif(n) < 0.004
  t <- t[!drop]; n <- length(t)

  # A pause is a hole in t_sec with NO rows in it. The reduction must excise it rather
  # than credit it as dwell.
  if (!is.na(pause_at_s) && pause_len_s > 0) {
    t <- ifelse(t > pause_at_s, t + pause_len_s, t)
  }

  # Tracking comes and goes in stretches, not independently per frame.
  block <- rep(seq_len(ceiling(n / 120)), each = 120)[seq_len(n)]
  lost_block <- runif(length(unique(block))) < gaze_loss
  tracking_lost <- lost_block[block]

  eye_tracking <- as.integer(!tracking_lost)
  gaze_valid   <- as.integer(!tracking_lost & runif(n) > 0.02)
  gaze_conf    <- ifelse(gaze_valid == 1, sample(1:2, n, TRUE, prob = c(0.25, 0.75)), 0L)

  # AOI dwells as runs, so looks have realistic durations rather than per-frame noise.
  aoi <- character(n)
  i <- 1L
  while (i <= n) {
    len <- max(1L, round(rgamma(1, shape = 2, rate = 2) * hz))
    len <- min(len, n - i + 1L)
    aoi[i:(i + len - 1L)] <- if (runif(1) < aoi_rate) sample(SIM_AOIS, 1) else ""
    i <- i + len
  }
  aoi[gaze_valid == 0] <- ""     # GazeAoi clears attribution when gaze is unusable

  # Dwell counter resets on entry, exactly as AoiData.Set does.
  dwell <- numeric(n); run <- 0
  for (k in seq_len(n)) {
    if (aoi[k] == "" || (k > 1 && aoi[k] != aoi[k - 1])) run <- 0
    if (aoi[k] != "") run <- run + dt
    dwell[k] <- run
  }

  # Eye behaviour: the current event repeats across frames, and its duration accumulates.
  behavior <- character(n); b_dur <- rep(NA_real_, n)
  b_amp <- rep(NA_real_, n); b_vel <- rep(NA_real_, n)
  k <- 1L
  while (k <= n) {
    type <- sample(c("Fixation", "Saccade", "Blink"), 1, prob = c(0.72, 0.24, 0.04))
    len  <- switch(type,
                   Fixation = round(runif(1, 0.15, 0.6) * hz),
                   Saccade  = round(runif(1, 0.03, 0.08) * hz),
                   Blink    = round(runif(1, 0.10, 0.25) * hz))
    len <- max(1L, min(len, n - k + 1L))
    idx <- k:(k + len - 1L)
    behavior[idx] <- type
    b_dur[idx] <- seq_len(len) * dt * 1000
    if (type == "Saccade") {
      b_amp[idx] <- runif(1, 2, 25)
      b_vel[idx] <- runif(1, 80, 500)     # NaN during fixation in the real device -> empty
    } else {
      b_amp[idx] <- 0
    }
    k <- k + len
  }
  behavior[eye_tracking == 0] <- ""
  b_dur[eye_tracking == 0] <- NA; b_amp[eye_tracking == 0] <- NA; b_vel[eye_tracking == 0] <- NA

  # A slow walk with head turns, so head_path_m and head_yaw_travel_deg are non-trivial
  # and the 360-degree wrap actually gets exercised.
  head_x <- cumsum(rnorm(n, 0, 0.012))
  head_z <- cumsum(rnorm(n, 0, 0.012))
  head_y <- 1.65 + rnorm(n, 0, 0.01)
  yaw    <- (cumsum(rnorm(n, 0, 1.1))) %% 360

  marker <- rep("", n)
  marker[1] <- "TRIAL_START"
  marker[n] <- "TRIAL_STOP"
  if (any(tracking_lost)) {
    first_loss <- which(tracking_lost)[1]
    if (first_loss > 1) marker[first_loss] <- "TRACKING_LOST"
  }

  # Derived numerics computed BEFORE the tibble. Inside tibble() a later expression sees
  # the earlier columns, so `gaze_ox = fmt(head_x, 4)` would be formatting the character
  # column just written, not the numeric vector.
  is_fix <- behavior == "Fixation"
  fix_x  <- ifelse(is_fix, head_x + 2, NA_real_)
  fix_y  <- ifelse(is_fix, head_y,     NA_real_)
  fix_z  <- ifelse(is_fix, head_z + 2, NA_real_)
  openness <- ifelse(behavior == "Blink", 0.05, 0.95)
  gaze_o_x <- head_x; gaze_o_y <- head_y; gaze_o_z <- head_z
  # -1, matching the real logger: the Magic Leap OpenXR path exposes no pupillometry, so
  # EyeAndHeadTracker writes the unavailable-sentinel every frame. Simulating plausible
  # ~3.4 mm readings here would have hidden the one thing this column needs to prove --
  # that a sentinel never becomes a number.
  pupil_l <- rep(-1, n)
  pupil_r <- rep(-1, n)

  tibble(
    t_sec        = fmt(t, 4),
    unix_ms      = format(as.numeric(1785000000000) + round(t * 1000),
                          scientific = FALSE, trim = TRUE),
    marker       = marker,
    head_x = fmt(head_x, 4), head_y = fmt(head_y, 4), head_z = fmt(head_z, 4),
    head_pitch = fmt(rnorm(n, 0, 6) %% 360, 2), head_yaw = fmt(yaw, 2),
    head_roll = fmt(rnorm(n, 0, 2) %% 360, 2),
    # Written as character throughout. as.matrix() on a mixed-type frame runs every
    # column through format(), which pads to a common width -- writing " 1" instead of
    # "1". read_csv trims it, but keeping the file byte-honest is cheaper than relying
    # on that.
    eye_tracking = as.character(eye_tracking),
    gaze_valid   = as.character(gaze_valid),
    gaze_conf    = as.character(gaze_conf),
    gaze_ox = fmt(gaze_o_x, 4), gaze_oy = fmt(gaze_o_y, 4), gaze_oz = fmt(gaze_o_z, 4),
    gaze_dx = fmt(rnorm(n, 0, .2), 4), gaze_dy = fmt(rnorm(n, 0, .2), 4),
    gaze_dz = fmt(rep(1, n), 4),
    fix_valid = as.character(as.integer(is_fix)),
    fix_x = fmt(fix_x, 4), fix_y = fmt(fix_y, 4), fix_z = fmt(fix_z, 4),
    open_l = fmt(openness, 3),
    open_r = fmt(openness, 3),
    pupil_l_mm = fmt(pupil_l, 3),
    pupil_r_mm = fmt(pupil_r, 3),
    behavior = behavior,
    behavior_amp_deg = fmt(b_amp, 3),
    behavior_vel_dps = fmt(b_vel, 2),
    behavior_dur_ms  = fmt(b_dur, 2),
    aoi = aoi,
    aoi_dwell_s = fmt(dwell, 3),
    aoi_hit_dist_m = ifelse(aoi == "", "", fmt(runif(n, 1, 8), 3))
  )
}

# Reproduces WriteParamHeader(): '#' comment lines above the column header, carrying the
# study parameters in force. The reduction reads gaze.minConfidence back out of these.
write_sim_trial <- function(path, rows, min_conf = 1L) {
  dir.create(dirname(path), recursive = TRUE, showWarnings = FALSE)
  con <- file(path, "w")
  on.exit(close(con), add = TRUE)
  writeLines(c(
    "# AR Cockpit trial. Rows below the column header are the data.",
    paste0("# recorded_utc = ", format(Sys.time(), "%Y-%m-%d %H:%M:%S", tz = "UTC"), "Z"),
    "# app_version = 0.1.0-sim",
    "# --- study parameters in force for THIS trial (from StudyProfile + any dev override) ---",
    paste0("# study.gaze.minConfidence = ", min_conf),
    "# study.gaze.maxDistanceMeters = 10",
    "# study.gaze.dwellToConfirmSeconds = 0.5",
    "# study.flushEveryRows = 60"
  ), con)
  writeLines(paste(SIM_COLUMNS, collapse = ","), con)
  writeLines(apply(rows[SIM_COLUMNS], 1, paste, collapse = ","), con)
}

# ---------------------------------------------------------------------------------
# Build a whole synthetic session tree: valid/, invalid/, _incomplete/, trials_index.csv
# ---------------------------------------------------------------------------------
simulate_study <- function(dir          = "analysis/sim/trials",
                           participants = sprintf("P%02d", 1:4),
                           conditions   = cfg$conditions,
                           seed         = cfg$seed) {

  set.seed(seed)
  unlink(dir, recursive = TRUE)
  dir.create(file.path(dir, "valid"), recursive = TRUE, showWarnings = FALSE)
  dir.create(file.path(dir, "invalid"), recursive = TRUE, showWarnings = FALSE)
  dir.create(file.path(dir, "_incomplete"), recursive = TRUE, showWarnings = FALSE)

  index <- list()
  clock <- as.POSIXct("2026-09-14 14:00:00", tz = "UTC")

  emit <- function(pid, cond, trial, valid, redo, note, rows, dur, ...) {
    stamp <- format(clock, "%Y%m%d_%H%M%S")
    fname <- sprintf("%s_%s_trial_%d%s_%s.csv", pid, cond, trial,
                     if (redo) "_redo" else "", stamp)
    sub <- if (valid) "valid" else "invalid"
    write_sim_trial(file.path(dir, sub, fname), rows)
    index[[length(index) + 1L]] <<- tibble(
      started_utc = format(clock, "%Y-%m-%d %H:%M:%S"),
      ended_utc   = format(clock + dur, "%Y-%m-%d %H:%M:%S"),
      participant = pid, condition = cond, trial = trial,
      redo = as.integer(redo), valid = as.integer(valid), note = note,
      rows = nrow(rows), duration_s = round(dur, 1),
      file = paste0(sub, "/", fname)
    )
    clock <<- clock + dur + 300
  }

  for (pid in participants) {
    # A per-participant Latin-square-ish rotation, purely so the simulated design is
    # balanced. Your real mapping comes from the design table, not from here.
    order_i <- ((match(pid, participants) - 1L) + seq_along(conditions) - 1L) %% length(conditions) + 1L
    conds <- conditions[order_i]

    for (trial in seq_along(conds)) {
      cond <- conds[trial]

      # P02 trial 2: the operator spoils it, then re-runs. Two attempts, one cell.
      if (pid == "P02" && trial == 2) {
        emit(pid, cond, trial, valid = FALSE, redo = FALSE,
             note = "participant removed headset",
             rows = simulate_trial_rows(duration_s = 70), dur = 70)
        emit(pid, cond, trial, valid = TRUE, redo = TRUE, note = "",
             rows = simulate_trial_rows(duration_s = 290), dur = 290)
        next
      }

      # P03 trial 3: eye tracker never really held calibration. Must survive into the
      # recall/survey analyses and drop out of the gaze ones.
      if (pid == "P03" && trial == 3) {
        emit(pid, cond, trial, valid = TRUE, redo = FALSE, note = "",
             rows = simulate_trial_rows(duration_s = 280, gaze_loss = 0.42), dur = 280)
        next
      }

      # P01 trial 4: paused mid-trial for a room reset.
      if (pid == "P01" && trial == 4) {
        emit(pid, cond, trial, valid = TRUE, redo = FALSE, note = "paused for reset",
             rows = simulate_trial_rows(duration_s = 300, pause_at_s = 150,
                                        pause_len_s = 47), dur = 347)
        next
      }

      emit(pid, cond, trial, valid = TRUE, redo = FALSE, note = "",
           rows = simulate_trial_rows(duration_s = runif(1, 240, 330)),
           dur = runif(1, 240, 330))
    }
  }

  # A crash: the file exists in _incomplete/ and appears in NO index row, because
  # AppendIndex only runs on a classified close.
  write_sim_trial(
    file.path(dir, "_incomplete", "P04_baseline_trial_4_20260914_181233.csv"),
    simulate_trial_rows(duration_s = 96)
  )

  idx <- bind_rows(index)
  readr::write_csv(idx, file.path(dir, "trials_index.csv"))

  message("Simulated study written to ", normalizePath(dir), "\n",
          "  ", nrow(idx), " index rows, ",
          sum(idx$valid == 1), " valid, ", sum(idx$valid == 0), " invalid, ",
          "1 stranded incomplete file")
  invisible(dir)
}
