# =====================================================================================
# run_selftest.R  --  Prove the pipeline works before there is any real data.
#
#   Rscript analysis/run_selftest.R
#
# Generates a synthetic study containing every case the exclusion policy has to handle,
# runs the full reduction over it, and then ASSERTS that the pipeline noticed each one.
# A green run here means the only thing standing between you and Results is data.
#
# The assertions are the point. A reduction that runs without error but silently counts
# a 47-second pause as dwell time would pass a smoke test and ruin a paper.
# =====================================================================================

if (!dir.exists("analysis/R")) {
  stop("Run this from the project root: Rscript analysis/run_selftest.R")
}

source("analysis/R/00_config.R")
source("analysis/R/01_manifest.R")
source("analysis/R/02_reduce_gaze.R")
source("analysis/R/03_flow.R")
source("analysis/R/90_simulate.R")

sim_dir <- "analysis/sim/trials"
simulate_study(sim_dir)

cfg$trials_dir     <- sim_dir
cfg$dir_valid      <- file.path(sim_dir, "valid")
cfg$dir_invalid    <- file.path(sim_dir, "invalid")
cfg$dir_incomplete <- file.path(sim_dir, "_incomplete")
cfg$index_path     <- file.path(sim_dir, "trials_index.csv")
cfg$out_dir        <- "analysis/sim/out"

dir.create(cfg$out_dir, recursive = TRUE, showWarnings = FALSE)

manifest <- build_manifest()
gaze     <- reduce_all_trials(manifest)
aoi      <- reduce_all_aoi(manifest)

manifest <- manifest %>%
  dplyr::left_join(gaze %>% dplyr::select(basename, mgf = missing_gaze_frac,
                                          ufg = use_for_gaze, gaze_exclude_reason),
                   by = "basename") %>%
  dplyr::mutate(missing_gaze_frac = mgf,
                use_for_gaze = dplyr::coalesce(ufg, FALSE)) %>%
  dplyr::select(-mgf, -ufg)

readr::write_csv(gaze, file.path(cfg$out_dir, "trial_gaze_metrics.csv"))
readr::write_csv(aoi,  file.path(cfg$out_dir, "trial_aoi_dwell.csv"))
print_flow_report(manifest, gaze, path = file.path(cfg$out_dir, "exclusion_flow.txt"))

# =====================================================================================
# Assertions
# =====================================================================================
fails <- character()
check <- function(label, ok, detail = "") {
  if (isTRUE(ok)) {
    cat(sprintf("  PASS  %s\n", label))
  } else {
    cat(sprintf("  FAIL  %s  %s\n", label, detail))
    fails <<- c(fails, label)
  }
}

cat("\n--- SELF TEST -------------------------------------------------------\n")

# 1. The crashed file was seen, counted, and excluded -- not quietly ignored.
check("stranded _incomplete file is counted and excluded",
      sum(manifest$exclude_reason == "incomplete_file", na.rm = TRUE) == 1)

# 2. The operator's spoiled trial is out.
check("operator-flagged invalid trial is excluded",
      sum(manifest$exclude_reason == "operator_flagged_invalid", na.rm = TRUE) == 1)

# 3. ...and the redo that replaced it is IN, exactly once for that cell.
redo_cell <- manifest %>% dplyr::filter(participant == "P02", trial == 2)
check("redo replaces the spoiled attempt (one included trial for the cell)",
      sum(redo_cell$included) == 1 && all(redo_cell$redo[redo_cell$included]))

# 4. Every design cell has at most one included trial.
dupe <- manifest %>%
  dplyr::filter(included) %>%
  dplyr::count(participant, condition, trial) %>%
  dplyr::filter(n > 1)
check("no design cell has two included trials", nrow(dupe) == 0)

# 5. The bad-tracking trial survives for surveys/recall but drops out of gaze.
bad <- gaze %>% dplyr::filter(participant == "P03", trial == 3)
check("heavy tracking loss -> excluded from gaze only",
      nrow(bad) == 1 && !bad$use_for_gaze[1] && bad$missing_gaze_frac[1] > cfg$max_missing_gaze,
      if (nrow(bad) == 1) sprintf("(missing = %.2f)", bad$missing_gaze_frac[1]) else "")
check("that trial is still in the analysis set for non-gaze outcomes",
      manifest %>% dplyr::filter(participant == "P03", trial == 3) %>%
        dplyr::pull(included) %>% any())

# 6. THE IMPORTANT ONE. The paused trial spans ~347 s of wall clock but only ~300 s of
#    observed time. If duration_s came out near the wall span, gap excision is broken and
#    every dwell measure in the study is inflated.
paused <- gaze %>% dplyr::filter(participant == "P01", trial == 4)
check("pause is excised from trial duration",
      nrow(paused) == 1 && paused$gap_time_s[1] > 40 &&
        abs(paused$duration_s[1] - 300) < 25,
      if (nrow(paused) == 1)
        sprintf("(duration %.1f s, wall span %.1f s, gap %.1f s)",
                paused$duration_s[1], paused$wall_span_s[1], paused$gap_time_s[1]) else "")

# 7. Durations are summed from intervals, not from n_rows / 60. With dropped frames the
#    effective rate must land just under the nominal 60 Hz, never above it.
check("effective sampling rate is measured, not assumed",
      all(gaze$effective_hz > 50 & gaze$effective_hz <= 61, na.rm = TRUE),
      sprintf("(median %.1f Hz)", stats::median(gaze$effective_hz, na.rm = TRUE)))

# 8. Attention time cannot exceed the time gaze was actually usable.
check("AOI dwell never exceeds usable gaze time",
      all(gaze$aoi_time_s <= gaze$duration_s * gaze$prop_gaze_usable + 1e-6, na.rm = TRUE))

# 9. Empty cells became NA, not zero. Saccade velocity is empty during fixations; if the
#    reader turned those into 0 the mean peak velocity would collapse.
check("missing values read as NA, not 0 (saccade peak velocity is plausible)",
      all(gaze$mean_saccade_peak_vel_dps > 50, na.rm = TRUE),
      sprintf("(median %.0f deg/s)", stats::median(gaze$mean_saccade_peak_vel_dps, na.rm = TRUE)))

# 10. The study parameters were read back out of each file's own '#' header.
check("gaze confidence floor read from the trial file header",
      all(gaze$conf_from_header, na.rm = TRUE))

# 11. Yaw wrap handled: cumulative turn must be large and positive, never negative or NA.
check("head yaw travel handles the 360-degree wrap",
      all(is.finite(gaze$head_yaw_travel_deg)) && all(gaze$head_yaw_travel_deg > 0))

# 12. Looks survived the merge/floor filters.
check("AOI looks were extracted", all(gaze$n_looks > 0) && nrow(aoi) > 0)

cat("---------------------------------------------------------------------\n")
if (length(fails) == 0) {
  cat("ALL CHECKS PASSED. The pipeline is ready for real data:\n")
  cat("  1. set cfg$conditions in analysis/R/00_config.R to your real condition strings\n")
  cat("  2. copy the headset's trials/ folder to data/trials/\n")
  cat("  3. Rscript analysis/run_reduction.R\n")
} else {
  cat(length(fails), " CHECK(S) FAILED:\n", paste0("  - ", fails, collapse = "\n"), "\n")
  quit(status = 1)
}
