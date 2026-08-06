# =====================================================================================
# 03_flow.R  --  The exclusion flow report.
#
# Produces the CONSORT-style numbers for the Methods section directly from the manifest,
# so the sentence "of 96 recorded trials, 4 were excluded (2 operator-flagged invalid,
# 1 incomplete, 1 superseded by a retake), leaving 92" is READ OFF A TABLE rather than
# reconstructed by hand from a lab notebook.
#
# It prints to the console and writes the same content to a file, because the number you
# quote in the paper should be one you can point at six months later.
# =====================================================================================

suppressPackageStartupMessages({
  library(dplyr)
  library(readr)
  library(stringr)
})

# Human-readable text for each machine reason code. Anything not listed falls through
# with its raw code, which is preferable to a lookup silently returning NA.
reason_labels <- c(
  incomplete_file          = "Incomplete file (app quit or crashed mid-trial)",
  operator_flagged_invalid = "Operator flagged the trial invalid during the session",
  superseded_duplicate     = "Superseded: the same cell was recorded valid more than once",
  unrecognised_condition   = "Condition string not recognised (data-entry error)",
  file_missing_on_disk     = "Listed in trials_index.csv but the file is not on disk",
  reduction_error          = "Gaze reduction failed on this file",
  gaze_quality             = "Usable gaze below threshold"
)

label_reason <- function(x) {
  out <- unname(reason_labels[x])
  ifelse(is.na(out), x, out)
}

# ---------------------------------------------------------------------------------
# The report. `gaze` is the output of reduce_all_trials(); pass NULL to report on the
# manifest alone (useful before the reduction has been run).
# ---------------------------------------------------------------------------------
build_flow_report <- function(manifest, gaze = NULL) {

  lines <- character()
  add <- function(...) lines <<- c(lines, paste0(...))

  n_attempts <- nrow(manifest)
  n_included <- sum(manifest$included)

  add("========================================================================")
  add("  TRIAL FLOW")
  add("  generated ", format(Sys.time(), "%Y-%m-%d %H:%M:%S"), " from ", cfg$trials_dir)
  add("========================================================================")
  add("")
  add("Policy in force")
  add("  - Trials flagged invalid are excluded.                      [",
      if (cfg$exclude_invalid_flag) "ON" else "OFF", "]")
  add("  - Redone trials replace the spoiled attempt.                [",
      if (cfg$redo_replaces_spoiled) "ON" else "OFF", "]")
  add("  - Incomplete files are excluded.                            [",
      if (cfg$exclude_incomplete) "ON" else "OFF", "]")
  add("  - Trials with >", sprintf("%.0f%%", 100 * cfg$max_missing_gaze),
      " of frames missing valid gaze are excluded")
  add("    from GAZE analyses but retained for survey and recall.    [ON]")
  add("")

  add("------------------------------------------------------------------------")
  add("Recorded trial attempts on disk .......... ", n_attempts)
  add("")

  excl <- manifest %>%
    filter(!included) %>%
    count(exclude_reason, name = "n") %>%
    arrange(desc(n))

  if (nrow(excl) == 0) {
    add("  Excluded ............................... 0")
  } else {
    add("  Excluded ............................... ", sum(excl$n))
    for (i in seq_len(nrow(excl))) {
      add(sprintf("      %-4d  %s", excl$n[i], label_reason(excl$exclude_reason[i])))
    }
  }
  add("")
  add("Trials retained for ANALYSIS ............. ", n_included)
  add("  (survey, recall and behavioural outcomes)")
  add("")

  # ---- Gaze subset ---------------------------------------------------------------
  if (!is.null(gaze) && nrow(gaze) > 0) {
    n_gaze_ok <- sum(gaze$use_for_gaze)
    gexcl <- gaze %>%
      filter(!use_for_gaze) %>%
      mutate(reason_group = if_else(reduce_failed, "reduction_error", "gaze_quality")) %>%
      count(reason_group, name = "n")

    add("------------------------------------------------------------------------")
    add("Of the ", n_included, " retained, eligible for GAZE analyses ... ", n_gaze_ok)
    if (nrow(gexcl) > 0) {
      for (i in seq_len(nrow(gexcl))) {
        add(sprintf("      %-4d  %s", gexcl$n[i], label_reason(gexcl$reason_group[i])))
      }
      lost <- gaze %>% filter(!use_for_gaze, !reduce_failed)
      if (nrow(lost) > 0) {
        add("")
        add("  Gaze-quality failures in detail:")
        for (i in seq_len(nrow(lost))) {
          add(sprintf("      %s  %s  trial %s  ->  %.1f%% of trial time without usable gaze",
                      lost$participant[i], lost$condition[i], lost$trial[i],
                      100 * lost$missing_gaze_frac[i]))
        }
      }
    }
    add("")

    # Tracking quality overall. If the median trial is losing 15% of its gaze, the
    # study has an instrument problem that no exclusion rule fixes, and it is better to
    # learn that on participant 4 than on participant 24.
    ok <- gaze %>% filter(!reduce_failed)
    if (nrow(ok) > 0) {
      add("  Usable-gaze fraction across retained trials:")
      add(sprintf("      min %.2f | median %.2f | mean %.2f | max %.2f",
                  min(ok$prop_gaze_usable, na.rm = TRUE),
                  stats::median(ok$prop_gaze_usable, na.rm = TRUE),
                  mean(ok$prop_gaze_usable, na.rm = TRUE),
                  max(ok$prop_gaze_usable, na.rm = TRUE)))
      add(sprintf("      effective sampling rate: median %.1f Hz",
                  stats::median(ok$effective_hz, na.rm = TRUE)))
      paused <- sum(ok$n_gaps > 0, na.rm = TRUE)
      add(sprintf("      %d of %d trials contained a pause or stall (gap time excluded from all durations)",
                  paused, nrow(ok)))
      add("")
    }
  }

  # ---- Design coverage -------------------------------------------------------------
  add("------------------------------------------------------------------------")
  add("Coverage by participant (included trials)")
  cov <- audit_completeness(manifest)
  if (nrow(cov) == 0) {
    add("  none")
  } else {
    for (i in seq_len(nrow(cov))) {
      add(sprintf("      %-12s %d of %d  %s",
                  cov$participant[i], cov$n_included[i], cov$expected[i],
                  if (cov$complete[i]) "" else "  <-- INCOMPLETE"))
    }
  }
  add("")

  cells <- manifest %>%
    filter(included) %>%
    count(condition, name = "n") %>%
    arrange(condition)
  add("Included trials per condition")
  if (nrow(cells) == 0) {
    add("  none")
  } else {
    for (i in seq_len(nrow(cells))) {
      add(sprintf("      %-14s %d", cells$condition[i], cells$n[i]))
    }
  }
  add("")

  # ---- Loud warnings ---------------------------------------------------------------
  bad_cond <- manifest %>% filter(exclude_reason == "unrecognised_condition")
  if (nrow(bad_cond) > 0) {
    add("!! ATTENTION -------------------------------------------------------------")
    add("!! Condition strings the pipeline does not recognise. TrialController takes")
    add("!! free text from the operator, so these are almost certainly typos. Fix the")
    add("!! index or add the spelling to cfg$conditions -- do NOT let them through as")
    add("!! new factor levels.")
    for (v in sort(unique(bad_cond$condition))) add("!!   \"", v, "\"")
    add("!! Expected: ", paste(cfg$conditions, collapse = ", "))
    add("")
  }

  missing_files <- manifest %>% filter(exclude_reason == "file_missing_on_disk")
  if (nrow(missing_files) > 0) {
    add("!! ATTENTION -------------------------------------------------------------")
    add("!! ", nrow(missing_files), " trial(s) are listed in trials_index.csv but absent from disk.")
    add("!! This is a transfer problem, not an exclusion. Re-pull from the headset.")
    add("")
  }

  add("========================================================================")
  paste(lines, collapse = "\n")
}

print_flow_report <- function(manifest, gaze = NULL, path = NULL) {
  txt <- build_flow_report(manifest, gaze)
  cat(txt, "\n", sep = "")
  if (!is.null(path)) {
    dir.create(dirname(path), recursive = TRUE, showWarnings = FALSE)
    writeLines(txt, path)
    message("Flow report written to ", path)
  }
  invisible(txt)
}

# A tidy machine-readable companion to the printed report: one row per excluded
# attempt. This is the supplementary table, and the thing to check when the printed
# counts surprise you.
exclusions_table <- function(manifest, gaze = NULL) {
  from_manifest <- manifest %>%
    filter(!included) %>%
    transmute(participant, condition, trial, redo, file = basename,
              stage = "analysis_set", reason = exclude_reason,
              detail = note)

  if (is.null(gaze) || nrow(gaze) == 0) return(from_manifest)

  from_gaze <- gaze %>%
    filter(!use_for_gaze) %>%
    transmute(participant, condition, trial, redo, file = basename,
              stage = "gaze_subset",
              reason = if_else(reduce_failed, "reduction_error", "gaze_quality"),
              detail = gaze_exclude_reason)

  bind_rows(from_manifest, from_gaze) %>%
    arrange(stage, participant, condition, trial)
}
