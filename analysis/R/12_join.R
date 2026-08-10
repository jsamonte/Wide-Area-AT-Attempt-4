# =====================================================================================
# 12_join.R  --  Assemble the modelling table.
#
# The design table is the hinge. Nothing the participant fills in records the condition:
# the Post-Trial form asks for participant, group and trial number only. Joining
# (group, trial_order) onto the design table is what attaches `wireframe` and `pool` to
# every survey response and every recall sheet. Without it there is no independent
# variable anywhere in the dataset.
# =====================================================================================

suppressPackageStartupMessages({
  library(readr); library(dplyr); library(tidyr); library(tibble)
})

load_design <- function(path = cfg$design_path) {
  d <- readr::read_csv(path, na = c("", "NA"), progress = FALSE,
                       col_types = readr::cols(
                         group = "i", trial_order = "i", pool = "i", wireframe = "i",
                         condition = "c", part = "i", part_label = "c", set_id = "i"))

  if (nrow(d) != 16) warning("Design table has ", nrow(d), " rows; expected 16 ",
                             "(4 groups x 4 trials).", call. = FALSE)
  if (any(is.na(d$condition)) || any(is.na(d$set_id)))
    stop("Design table has blank condition or set_id. Fill it in before joining.")

  d %>% mutate(wireframe = factor(wireframe, levels = c(0, 1), labels = c("off", "on")),
               pool = factor(pool))
}

# The counterbalancing properties the Unity source claims. Checking them mechanically
# costs nothing and catches a hand-edit to the design table that quietly breaks the
# balance -- which would be invisible in the data and fatal in the analysis.
verify_design <- function(design = load_design()) {
  chk <- list(
    pool_x_wireframe = design %>% count(pool, wireframe) %>%
      summarise(ok = all(n == 2)) %>% pull(ok),
    position_balance = design %>% count(trial_order, wireframe) %>%
      summarise(ok = all(n == 2)) %>% pull(ok),
    pool_once_per_group = design %>% count(group, pool) %>%
      summarise(ok = all(n == 1)) %>% pull(ok),
    pool_once_per_position = design %>% count(trial_order, pool) %>%
      summarise(ok = all(n == 1)) %>% pull(ok)
  )
  for (nm in names(chk)) {
    if (!isTRUE(chk[[nm]]))
      warning("Design table fails the balance check: ", nm, call. = FALSE)
  }
  chk
}

# ---------------------------------------------------------------------------------
# Ideal collection route per pool (the efficiency-ratio denominator).
# ---------------------------------------------------------------------------------
# Written by the Unity editor tool GemOptimalPathCalculator, one row per pool: the exact
# Held-Karp open tour over that pool's 20 target positions, straight-line, in the ground
# plane. It is an authoring-time constant, not measured data -- the same 4 numbers apply
# to every participant -- which is why it lives in analysis/design/ next to the design
# table rather than being derived per trial.
#
# Returns NULL when the file is absent so build_trial_table() can carry on without it.
load_ideal_paths <- function(path = cfg$ideal_path_path) {
  if (is.null(path) || !file.exists(path)) {
    message("No ideal-path file at '", path, "'. path_ratio will be NA. ",
            "Run GemOptimalPathCalculator once per pool in the Unity editor.")
    return(NULL)
  }

  d <- readr::read_csv(path, na = c("", "NA"), progress = FALSE,
                       col_types = readr::cols(
                         pool = "i", n_targets = "i", seed = "i",
                         ideal_path_xz_m = "d", ideal_path_3d_m = "d",
                         method = "c", computed_utc = "c"))

  dup <- d %>% count(pool) %>% filter(n > 1)
  if (nrow(dup) > 0)
    stop("ideal_paths.csv has ", nrow(dup), " duplicated pool row(s). One row per pool, ",
         "or the join will duplicate every trial in that pool.")

  missing <- setdiff(as.character(1:4), as.character(d$pool))
  if (length(missing) > 0)
    warning("ideal_paths.csv is missing pool(s): ", paste(missing, collapse = ", "),
            ". Trials in those pools get path_ratio = NA.", call. = FALSE)

  # A tour computed over the wrong object set is the failure mode that matters here: it
  # produces a plausible number rather than an error. 20 targets is what RandomSpawner
  # spawns, so anything else means the tool picked up recall objects or a partial layout.
  odd <- d %>% filter(!is.na(n_targets), n_targets != 20L)
  if (nrow(odd) > 0)
    warning(nrow(odd), " pool(s) in ideal_paths.csv were computed over a target count ",
            "other than 20: ", paste0("pool ", odd$pool, " n=", odd$n_targets,
                                      collapse = "; "),
            ". Re-run the calculator on a freshly spawned pool.", call. = FALSE)

  d %>% mutate(pool = factor(pool))
}

# ---------------------------------------------------------------------------------
# The trial-level modelling table. One row per participant x trial.
# ---------------------------------------------------------------------------------
build_trial_table <- function(post_trial, recall_trials = NULL, gaze = NULL,
                              pre_study = NULL, design = load_design(),
                              ideal_paths = load_ideal_paths()) {

  out <- post_trial %>%
    mutate(group = as.integer(group), trial_order = as.integer(trial_order)) %>%
    left_join(design, by = c("group", "trial_order"))

  orphan <- out %>% filter(is.na(condition))
  if (nrow(orphan) > 0)
    warning(nrow(orphan), " post-trial response(s) have a (group, trial) pair not in ",
            "the design table. Check for typos in Group Number or Trial number.",
            call. = FALSE)

  if (!is.null(recall_trials)) {
    # set_id is joined from BOTH sides: the design table says which set that trial
    # should have used, the transcription says which sheet was actually filled in.
    # A mismatch means the wrong sheet was handed out, and that is worth an error
    # rather than a silent scoring of the wrong objects.
    r <- recall_trials %>%
      select(participant_id, trial_order, set_id_sheet = set_id,
             d_prime, criterion, hit_rate, fa_rate, recall_policy,
             ends_with("_strict"), ends_with("_lenient"), n_uncertain)
    out <- out %>% left_join(r, by = c("participant_id", "trial_order"))

    wrong <- out %>% filter(!is.na(set_id_sheet), set_id_sheet != set_id)
    if (nrow(wrong) > 0)
      warning(nrow(wrong), " trial(s) were scored against a SET sheet that does not ",
              "match the design table's pool. The wrong recall sheet may have been ",
              "handed out. Inspect before trusting d'.", call. = FALSE)
  }

  if (!is.null(gaze)) {
    out <- out %>% left_join(
      gaze %>% select(participant_id = participant, trial_order = trial,
                      any_of(c("prop_gaze_on_aoi", "n_looks", "mean_look_s",
                               "head_path_m", "head_path_xz_m",
                               "head_yaw_travel_deg", "use_for_gaze"))),
      by = c("participant_id", "trial_order"))
  }

  # Efficiency ratio: distance actually walked over the shortest route that would have
  # visited all 20 targets. Both terms are planar (see head_path_xz_m). The ratio is
  # ALWAYS well above 1 -- the ideal route assumes the target positions are known, and
  # finding them is the task -- so it is read as a relative index across conditions
  # within a pool, never as an absolute detour factor. Because the denominator is
  # constant within a pool, it is a per-pool rescaling of head_path_xz_m: it changes what
  # the number means and makes it comparable across pools, but adds no information to a
  # model that already carries pool.
  if (!is.null(ideal_paths)) {
    out <- out %>%
      left_join(ideal_paths %>% select(pool, ideal_path_xz_m), by = "pool")
    if ("head_path_xz_m" %in% names(out)) {
      out <- out %>% mutate(path_ratio = head_path_xz_m / ideal_path_xz_m)
    }
  }

  if (!is.null(pre_study)) {
    out <- out %>% left_join(
      pre_study %>% select(participant_id, sbsod, arvr_comfort),
      by = "participant_id")
  }

  out %>%
    mutate(
      participant_id = factor(participant_id),
      # Continuous by default: one interaction term rather than three, and a directly
      # interpretable practice/fatigue slope. Kept as an integer alongside so a factor
      # refit is a one-line change.
      trial_c = trial_order - mean(unique(design$trial_order))
    ) %>%
    arrange(participant_id, trial_order)
}
