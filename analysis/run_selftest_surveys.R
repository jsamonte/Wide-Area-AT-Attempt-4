# =====================================================================================
# run_selftest_surveys.R  --  Simulate surveys + recall, score them, assert.
#
#   Rscript analysis/run_selftest_surveys.R
#
# The assertions target the errors that produce plausible-looking wrong numbers:
# a reverse key that never fires, a TLX item reversed that shouldn't be, subscale
# weights transposed, an infinite d', and a design join that silently drops condition.
# =====================================================================================

if (!dir.exists("analysis/R")) stop("Run from the project root: Rscript analysis/run_selftest_surveys.R")

source("analysis/R/00_config.R")
source("analysis/R/10_surveys.R")
source("analysis/R/11_recall.R")
source("analysis/R/12_join.R")
source("analysis/R/91_simulate_surveys.R")

sim <- "analysis/sim/surveys"
simulate_surveys(sim)

cfg$survey_pre_path        <- file.path(sim, "pre_study.csv")
cfg$survey_trial_path      <- file.path(sim, "post_trial.csv")
cfg$survey_post_path       <- file.path(sim, "post_study.csv")
cfg$recall_key_path        <- file.path(sim, "recall_key.csv")
cfg$recall_responses_path  <- file.path(sim, "recall_responses.csv")
cfg$out_dir                <- "analysis/sim/out"
dir.create(cfg$out_dir, recursive = TRUE, showWarnings = FALSE)

pre    <- load_pre_study()
trialq <- load_post_trial()
post   <- load_post_study()

items   <- build_recall_items()
recall  <- score_recall_trials(items)
design  <- load_design()
verify_design(design)

tt <- build_trial_table(trialq, recall_trials = recall, pre_study = pre, design = design)
readr::write_csv(tt, file.path(cfg$out_dir, "trial_table.csv"))
readr::write_csv(post, file.path(cfg$out_dir, "post_study_scored.csv"))

# =====================================================================================
fails <- character()
check <- function(label, ok, detail = "") {
  if (isTRUE(ok)) cat(sprintf("  PASS  %s\n", label))
  else { cat(sprintf("  FAIL  %s  %s\n", label, detail)); fails <<- c(fails, label) }
}
cat("\n--- SURVEY / RECALL SELF TEST ---------------------------------------\n")

# ---- Column matching ------------------------------------------------------------
check("every survey column matched (no NA-only scored columns)",
      all(!is.na(trialq$tlx_raw)) && all(!is.na(pre$sbsod)) && all(!is.na(post$sart_total)))

# ---- SBSOD reversal -------------------------------------------------------------
# P01 answered 7 to all 15. Seven items are positive (stay 7) and eight are negative
# (become 1), so the mean must be (7*7 + 8*1)/15 = 3.8, NOT 7. If the reverse key never
# fired this would be exactly 7.
p01 <- pre %>% dplyr::filter(participant_id == "P01")
check("SBSOD reverse-coding fires",
      abs(p01$sbsod[1] - (7 * 7 + 8 * 1) / 15) < 1e-9,
      sprintf("(got %.3f, expected %.3f, unreversed would be 7)",
              p01$sbsod[1], (7 * 7 + 8 * 1) / 15))

# ---- TLX ------------------------------------------------------------------------
check("TLX read on the 0-100 scale",
      max(trialq$tlx_mental, na.rm = TRUE) > 21 &&
        all(trialq$tlx_raw >= 0 & trialq$tlx_raw <= 100, na.rm = TRUE))

# Performance must NOT be reversed: the simulator draws all six items from the same
# distribution, so a reversed Performance would sit ~100-mean away from the others.
gap <- abs(mean(trialq$tlx_perform, na.rm = TRUE) - mean(trialq$tlx_mental, na.rm = TRUE))
check("TLX Performance is NOT reverse-coded", gap < 12,
      sprintf("(perform %.1f vs mental %.1f)",
              mean(trialq$tlx_perform, na.rm = TRUE), mean(trialq$tlx_mental, na.rm = TRUE)))

# ---- SSQ ------------------------------------------------------------------------
# All 16 symptoms Severe (3). Raw subscale sums are 3 x 7 = 21 each, so total severity
# is (21+21+21) x 3.74 = 235.62. This pins both the 0-3 mapping and the Kennedy weights.
p01p <- post %>% dplyr::filter(participant_id == "P01")
check("SSQ scoring and Kennedy weights",
      abs(p01p$ssq_total[1] - (21 * 3) * 3.74) < 1e-6,
      sprintf("(got %.2f, expected %.2f)", p01p$ssq_total[1], 63 * 3.74))
check("SSQ subscale weights distinct and correctly assigned",
      abs(p01p$ssq_nausea[1] - 21 * 9.54) < 1e-6 &&
        abs(p01p$ssq_oculomotor[1] - 21 * 7.58) < 1e-6 &&
        abs(p01p$ssq_disorientation[1] - 21 * 13.92) < 1e-6)

# ---- SART -----------------------------------------------------------------------
check("SART = U - (D - S)",
      all(abs(post$sart_total -
                (post$sart_understanding - (post$sart_demand - post$sart_supply))) < 1e-9))

# ---- Brightness is bipolar -------------------------------------------------------
check("bipolar brightness scored as deviation from the midpoint",
      all(post$bright_overall_dev >= 0, na.rm = TRUE) &&
        any(post$bright_overall_signed < 0, na.rm = TRUE))

# ---- Recall / SDT ---------------------------------------------------------------
check("every trial produced a finite d'",
      all(is.finite(recall$d_prime)),
      sprintf("(%d non-finite)", sum(!is.finite(recall$d_prime))))

# P01 is at ceiling: 15/15 hits, 0 false alarms. Uncorrected this is qnorm(1)-qnorm(0)
# = Inf. The loglinear correction must keep it finite and large.
ceil <- recall %>% dplyr::filter(participant_id == "P01")
check("loglinear correction keeps a ceiling participant finite",
      all(is.finite(ceil$d_prime)) && all(ceil$d_prime > 2.5),
      sprintf("(d' = %s)", paste(round(ceil$d_prime, 2), collapse = ", ")))
check("ceiling case really is at ceiling",
      all(ceil$hits_strict == ceil$n_target) && all(ceil$false_alarms_strict == 0))

# ---- The planted effect must be recovered ---------------------------------------
eff <- tt %>% dplyr::group_by(wireframe) %>%
  dplyr::summarise(d = mean(d_prime, na.rm = TRUE), tlx = mean(tlx_raw, na.rm = TRUE),
                   .groups = "drop")
d_on  <- eff$d[eff$wireframe == "on"];  d_off  <- eff$d[eff$wireframe == "off"]
t_on  <- eff$tlx[eff$wireframe == "on"]; t_off <- eff$tlx[eff$wireframe == "off"]
check("planted memory effect recovered (wireframe ON has higher d')",
      d_on > d_off, sprintf("(on %.2f vs off %.2f)", d_on, d_off))
check("planted workload effect recovered (wireframe ON has lower TLX)",
      t_on < t_off, sprintf("(on %.1f vs off %.1f)", t_on, t_off))

# ---- Strict vs lenient both available --------------------------------------------
check("both response policies scored",
      all(c("d_prime_strict", "d_prime_lenient") %in% names(recall)) &&
        !isTRUE(all.equal(recall$d_prime_strict, recall$d_prime_lenient)))

# ---- The design join is what supplies condition ----------------------------------
check("design join attached condition to every trial",
      !any(is.na(tt$condition)) && !any(is.na(tt$wireframe)))
check("design table balance verified",
      all(unlist(verify_design(design))))
check("recall sheet matches the pool the design table specifies",
      all(tt$set_id_sheet == tt$set_id, na.rm = TRUE))
check("trial table has one row per participant x trial",
      nrow(tt) == dplyr::n_distinct(tt$participant_id) * 4)

cat("---------------------------------------------------------------------\n")
if (length(fails) == 0) {
  cat("ALL CHECKS PASSED.\n")
  cat("Swap in real data by pointing cfg$survey_*_path, cfg$recall_* at the real files.\n")
} else {
  cat(length(fails), "CHECK(S) FAILED:\n", paste0("  - ", fails, collapse = "\n"), "\n")
  quit(status = 1)
}
