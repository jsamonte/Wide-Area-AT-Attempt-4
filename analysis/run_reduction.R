# =====================================================================================
# run_reduction.R  --  Raw 60 Hz trial files  ->  one analysis-ready row per trial.
#
#   Rscript analysis/run_reduction.R                      # uses cfg$trials_dir
#   Rscript analysis/run_reduction.R path/to/trials       # or point it somewhere else
#
# This is the "swap the file path" step. Nothing below this line is done by hand.
#
# Outputs, all in analysis/out/:
#   trial_manifest.csv       every attempt on disk, with its verdict and reason
#   trial_gaze_metrics.csv   one row per included trial -- the modelling input
#   trial_aoi_dwell.csv      per-trial x per-AOI dwell detail
#   exclusions.csv           machine-readable exclusion list
#   exclusion_flow.txt       the CONSORT-style numbers for the Methods section
#   session_info.txt         R and package versions, for reproducibility
# =====================================================================================

# Run from the project root so the relative paths in cfg resolve.
if (!dir.exists("analysis/R")) {
  stop("Run this from the project root: Rscript analysis/run_reduction.R")
}

source("analysis/R/00_config.R")

args <- commandArgs(trailingOnly = TRUE)
if (length(args) >= 1 && nzchar(args[1])) {
  cfg$trials_dir     <- args[1]
  cfg$dir_valid      <- file.path(cfg$trials_dir, "valid")
  cfg$dir_invalid    <- file.path(cfg$trials_dir, "invalid")
  cfg$dir_incomplete <- file.path(cfg$trials_dir, "_incomplete")
  cfg$index_path     <- file.path(cfg$trials_dir, "trials_index.csv")
}

source("analysis/R/01_manifest.R")
source("analysis/R/02_reduce_gaze.R")
source("analysis/R/03_flow.R")

dir.create(cfg$out_dir, recursive = TRUE, showWarnings = FALSE)

message("Reading trial index from ", cfg$trials_dir)
manifest <- build_manifest()

gaze <- reduce_all_trials(manifest)
aoi  <- reduce_all_aoi(manifest)

# The manifest carries the final gaze verdict, so one file answers "was this trial used,
# and for what".
manifest <- manifest %>%
  dplyr::left_join(
    gaze %>% dplyr::select(basename, use_for_gaze_final = use_for_gaze,
                           missing_gaze_frac, gaze_exclude_reason),
    by = "basename"
  ) %>%
  dplyr::mutate(use_for_gaze = dplyr::coalesce(use_for_gaze_final, FALSE)) %>%
  dplyr::select(-use_for_gaze_final)

readr::write_csv(manifest, file.path(cfg$out_dir, "trial_manifest.csv"))
readr::write_csv(gaze,     file.path(cfg$out_dir, "trial_gaze_metrics.csv"))
readr::write_csv(aoi,      file.path(cfg$out_dir, "trial_aoi_dwell.csv"))
readr::write_csv(exclusions_table(manifest, gaze),
                 file.path(cfg$out_dir, "exclusions.csv"))

print_flow_report(manifest, gaze, path = file.path(cfg$out_dir, "exclusion_flow.txt"))

# Reproducibility: the exact package versions that produced these numbers.
capture.output(sessionInfo(), file = file.path(cfg$out_dir, "session_info.txt"))

message("\nDone. Modelling input: ", file.path(cfg$out_dir, "trial_gaze_metrics.csv"),
        " (", sum(gaze$use_for_gaze), " trials eligible for gaze analyses)")
