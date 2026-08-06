# =====================================================================================
# 11_recall.R  --  Score the recall sheets: signal detection per trial, plus the
#                  item-level long table the GLMM needs.
#
# TWO INPUTS, AND THEY ARE DIFFERENT THINGS:
#   recall_key.csv        what was ACTUALLY in the room. 4 sets x 30 positions, filled
#                         in once from the study design. Identical for every participant.
#   recall_responses.csv  what each participant CLAIMED. One row per completed sheet,
#                         transcribed from paper.
#
# d' is the comparison of the two. Neither is interpretable alone.
#
# SET = POOL (confirmed): the design table's set_id is the pool whose objects were in
# the room, so the key row for (set_id, pos) is the truth for that trial.
# =====================================================================================

suppressPackageStartupMessages({
  library(readr); library(dplyr); library(tidyr); library(stringr); library(tibble)
})

# ---------------------------------------------------------------------------------
# Loading
# ---------------------------------------------------------------------------------
load_recall_key <- function(path = cfg$recall_key_path) {
  key <- readr::read_csv(path, na = c("", "NA"), progress = FALSE,
                         col_types = readr::cols(
                           set_id = "i", pos = "i", sheet_row = "i", sheet_col = "i",
                           item_label = "c", present = "i"))

  blank <- key %>% filter(is.na(present) | is.na(item_label) | item_label == "")
  if (nrow(blank) > 0) {
    stop(nrow(blank), " of ", nrow(key), " answer-key rows are still blank ",
         "(item_label or present). Recall cannot be scored until the key is complete.\n",
         "First blank: set ", blank$set_id[1], " pos ", blank$pos[1])
  }
  if (!all(key$present %in% c(0L, 1L)))
    stop("recall_key.csv: 'present' must be 0 or 1.")

  # An item label appearing in two sets would make the (1|item) random effect wrong:
  # the model would treat two different objects as one.
  dupes <- key %>% count(item_label) %>% filter(n > 1)
  if (nrow(dupes) > 0)
    warning("Answer key reuses ", nrow(dupes), " item label(s) across positions/sets: ",
            paste(head(dupes$item_label, 5), collapse = ", "),
            ". Item-level models will treat these as the same object.", call. = FALSE)

  key
}

# The transcription arrives wide -- 30 columns, in the reading order of the printed
# sheet -- because that is the order a human's eye moves and the fastest thing to type
# accurately. Everything downstream wants it long.
load_recall_responses <- function(path = cfg$recall_responses_path) {
  wide <- readr::read_csv(path, na = c("", "NA"), progress = FALSE,
                          col_types = readr::cols(.default = "c"))

  item_cols <- grep("^item_\\d+$", names(wide), value = TRUE)
  if (length(item_cols) != 30)
    warning("Expected 30 item_NN columns in the transcription, found ",
            length(item_cols), ".", call. = FALSE)

  long <- wide %>%
    mutate(participant_id = canon_pid(participant_id),
           trial_order = as.integer(trial),
           set_id = as.integer(set_id)) %>%
    select(participant_id, group, trial_order, set_id, any_of(c("typed_by", "typed_date")),
           all_of(item_cols)) %>%
    pivot_longer(all_of(item_cols), names_to = "pos", values_to = "response_raw") %>%
    mutate(
      pos = as.integer(str_remove(pos, "item_")),
      # Accept R/U/N in any case, and the spelled-out words, because a transcriber will
      # type both over 96 sheets.
      response = case_when(
        str_detect(response_raw, regex("^(r|rem)", ignore_case = TRUE))     ~ "R",
        str_detect(response_raw, regex("^(u|unc|m|may)", ignore_case = TRUE)) ~ "U",
        str_detect(response_raw, regex("^(n|no|not)", ignore_case = TRUE))  ~ "N",
        TRUE ~ NA_character_
      )
    )

  bad <- long %>% filter(!is.na(response_raw), is.na(response))
  if (nrow(bad) > 0) {
    warning(nrow(bad), " transcribed response(s) could not be read as R/U/N. ",
            "Unrecognised values: ",
            paste(unique(bad$response_raw)[1:min(5, length(unique(bad$response_raw)))],
                  collapse = ", "), call. = FALSE)
  }
  long
}

# ---------------------------------------------------------------------------------
# Item-level table: one row per (participant, trial, item), truth joined to response.
# This is the GLMM input, and it is also what the per-trial SDT counts are built from,
# so the two can never disagree.
# ---------------------------------------------------------------------------------
build_recall_items <- function(responses = load_recall_responses(),
                               key = load_recall_key()) {

  out <- responses %>%
    left_join(key, by = c("set_id", "pos"))

  unmatched <- out %>% filter(is.na(present))
  if (nrow(unmatched) > 0)
    warning(nrow(unmatched), " response row(s) had no matching answer-key entry. ",
            "Check that set_id values in the transcription exist in the key.", call. = FALSE)

  out %>%
    mutate(
      # PREREGISTERED, both computed. "strict" is primary: only an unambiguous
      # "I remember this" counts as a yes. "lenient" folds uncertainty into yes, and
      # exists to show the conclusion does not hinge on that choice.
      said_yes_strict  = response == "R",
      said_yes_lenient = response %in% c("R", "U"),
      is_target        = present == 1L
    )
}

# ---------------------------------------------------------------------------------
# Signal detection
# ---------------------------------------------------------------------------------
# Loglinear correction (Hautus 1995): 0.5 added to hits and false alarms, 1 to each
# total, applied to EVERY trial rather than only the degenerate ones. With 30 items a
# hit rate of 1.00 is common, and the uncorrected d' would be infinite; correcting only
# the broken cells biases exactly those cells.
sdt_from_counts <- function(hits, n_target, fa, n_lure, loglinear = cfg$recall_loglinear) {
  if (loglinear) {
    h <- (hits + 0.5) / (n_target + 1)
    f <- (fa   + 0.5) / (n_lure   + 1)
  } else {
    h <- hits / n_target
    f <- fa   / n_lure
  }
  tibble(
    hit_rate = h,
    fa_rate  = f,
    d_prime  = qnorm(h) - qnorm(f),
    # Response bias. Negative = liberal (says "remember" readily), positive = conservative.
    criterion = -0.5 * (qnorm(h) + qnorm(f)),
    # Nonparametric companion; does not assume equal-variance Gaussian, and stays finite.
    a_prime = ifelse(
      h >= f,
      0.5 + ((h - f) * (1 + h - f)) / (4 * h * (1 - f)),
      0.5 - ((f - h) * (1 + f - h)) / (4 * f * (1 - h))
    )
  )
}

score_recall_trials <- function(items = build_recall_items()) {

  one_policy <- function(yes_col, suffix) {
    items %>%
      filter(!is.na(present), !is.na(response)) %>%
      group_by(participant_id, group, trial_order, set_id) %>%
      summarise(
        n_items   = dplyr::n(),
        n_target  = sum(is_target),
        n_lure    = sum(!is_target),
        hits      = sum(is_target  & .data[[yes_col]]),
        misses    = sum(is_target  & !.data[[yes_col]]),
        false_alarms = sum(!is_target &  .data[[yes_col]]),
        correct_rej  = sum(!is_target & !.data[[yes_col]]),
        n_uncertain  = sum(response == "U"),
        .groups = "drop"
      ) %>%
      bind_cols(., sdt_from_counts(.$hits, .$n_target, .$false_alarms, .$n_lure)) %>%
      rename_with(~ paste0(.x, suffix),
                  c(hits, misses, false_alarms, correct_rej,
                    hit_rate, fa_rate, d_prime, criterion, a_prime))
  }

  strict  <- one_policy("said_yes_strict",  "_strict")
  lenient <- one_policy("said_yes_lenient", "_lenient") %>%
    select(participant_id, trial_order, ends_with("_lenient"))

  out <- strict %>% left_join(lenient, by = c("participant_id", "trial_order"))

  # Surface the primary policy under plain names so downstream models never have to
  # know which one was chosen -- and record the choice alongside, so a table can say so.
  suffix <- if (identical(cfg$recall_primary_policy, "lenient")) "_lenient" else "_strict"
  out %>%
    mutate(
      d_prime   = .data[[paste0("d_prime", suffix)]],
      criterion = .data[[paste0("criterion", suffix)]],
      hit_rate  = .data[[paste0("hit_rate", suffix)]],
      fa_rate   = .data[[paste0("fa_rate", suffix)]],
      recall_policy = cfg$recall_primary_policy
    )
}
