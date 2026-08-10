# =====================================================================================
# 91_simulate_surveys.R  --  Synthetic surveys, answer key and recall transcriptions.
#
# The survey CSVs are written with the FULL question text as column names, the way
# Google Forms exports them, so the regex column matching in 10_surveys.R is exercised
# rather than bypassed. If a pattern there is wrong, the self-test says so now.
#
# A known true effect is planted (wireframe ON -> better recognition memory, lower
# workload) so the pipeline can be checked for recovering it, plus a deliberate ceiling
# participant to prove the loglinear correction keeps d' finite.
# =====================================================================================

suppressPackageStartupMessages({
  library(dplyr); library(readr); library(tibble); library(tidyr)
})

# Verbatim question text from the three forms, so the exports look like the real thing.
Q_TLX <- c(
  "How mentally demanding was the task? 0 (Very Low) to 100 (Very High)",
  "How physically demanding was the task?   0 (Very Low) to 100 (Very High)",
  "How hurried or rushed was the pace of the task?   0 (Very Low) to 100 (Very High)",
  "How successful were you in accomplishing what you were asked to do? 0 (Perfect) to 100 (Failure)",
  "How hard did you have to work to accomplish your level of performance? 0 (Very Low) to 100 (Very High)",
  "How insecure, discouraged, irritated, stressed, or annoyed were you?   0 (Very Low) to 100 (Very High)"
)
Q_MEC <- c(
  "I was able to imagine the arrangement of the spaces in the environment very well.",
  "I had a precise idea of the spatial surroundings I moved through.",
  "I was able to make a good estimate of the size of the space I was in.",
  "I still have a concrete mental image of the space.",
  "I could accurately point to the locations of objects behind me without seeing them."
)

# Post-Trial section 3. Ten items, but NOT the SART -- see score_trial_sa() in
# 10_surveys.R. Order matters here: items 1-3 are Demand, 4-5 Supply, 6-10 Understanding,
# and the simulator below relies on that grouping.
Q_TRIAL_SA <- c(
  "How much were you having to divide your attention between multiple things?",
  "How much mental effort was required to anticipate what would happen next?",
  "How complex was the situation, with many interrelated factors?",
  "How much spare mental capacity did you have to take on additional tasks?",
  "How much attention could you devote to the situation, rather than being preoccupied?",
  "How familiar were you with the situation?",
  "How clear was your mental picture of the environment's layout?",
  "How well were you able to identify and focus on important aspects of the situation?",
  "To what degree could you identify the meaning and significance of what was happening?",
  "How well were you able to predict what would happen in the environment next?"
)

# Post-Trial section 4. Note "task?" singular -- the post-study form asks "tasks?" plural
# about the session as a whole. The wording differs, so the two are matched separately.
Q_PT_MEM <- c(
  "How successful were you overall at the memory (object-recall) task?",
  "How would you rate your performance on the memory (object-recall) task over time?",
  "How would you rate the overall brightness of the virtual objects?",
  "How would you rate the brightness of the virtual objects compared to the physical objects?"
)

Q_SBSOD <- c(
  " I am very good at giving directions.",
  "I have a poor memory for where I left things.",
  "I am very good at judging distances.",
  "My \"sense of direction\" is very good.",
  "I tend to think of my environment in terms of cardinal directions (N, S, E, W).",
  "I very easily get lost in a new city.",
  "I enjoy reading maps.",
  "I have trouble understanding directions.",
  "I am very good at reading maps.",
  "I don't remember routes very well while riding as a passenger in a car. ",
  "I don't enjoy giving directions. ",
  "It's not important to me to know where I am.",
  "I usually let someone else do the navigational planning for long trips.",
  "I can usually remember a new route after I have traveled it only once.",
  "I don't have a very good \"mental map\" of my environment. "
)
SBSOD_REVERSED <- c(2, 6, 8, 10, 11, 12, 13, 15)   # ground truth for the self-test

Q_SART <- c(
  "How changeable was the environment? Was it likely to change suddenly and unexpectedly, or was it stable and predictable?",
  "How many things in the environment were changing at once? Were there many varying factors to keep track of, or very few?",
  "How complicated was the environment? Was it complex with many interrelated parts, or simple and straightforward?",
  "How alert and ready for activity were you during the task?",
  "How much mental capacity did you have to spare? Did you have enough left over to attend to other things, or none at all?",
  "How much were you concentrating on the task? Were you bringing all your thoughts to bear on it, or was your attention elsewhere?",
  "How divided was your attention? Were you attending to many aspects of the environment at once, or focused on just one?",
  "How much information did you gain about the environment? Did you receive and understand a great deal, or very little?",
  "How good was the information you gained about the environment? Was it clear and useful, or poor and hard to use?",
  "How familiar were you with the environment? Did you have a great deal of relevant experience with it, or was it entirely new?"
)
Q_SSQ <- c(
  "General discomfort", "Fatigue", "Headache", "Eye strain", "Difficulty focusing",
  "Increased salivation ", "Sweating ", "Nausea ", "Difficulty concentrating ",
  "Fullness of head ", "Blurred vision ", "Dizziness (eyes open) ",
  "Dizziness (eyes closed) ", "Vertigo ", "Stomach awareness ", "Burping"
)
Q_WF <- c(
  "Overall, how helpful was the blue wireframe grid in finding the objects?",
  "The wireframe helped me understand the layout of the physical space.  ",
  "The wireframe helped me plan my path between objects.   ",
  "The wireframe made it easier to tell targets apart from distractors.   ",
  "The wireframe made the environment feel more cluttered or distracting.   "
)

lik <- function(n, m, s = 1.1, lo = 1, hi = 7) pmin(hi, pmax(lo, round(rnorm(n, m, s))))

simulate_surveys <- function(dir = "analysis/sim/surveys",
                             participants = sprintf("P%02d", 1:24),
                             seed = cfg$seed) {
  set.seed(seed)
  dir.create(dir, recursive = TRUE, showWarnings = FALSE)
  design <- load_design()

  # Groups assigned in equal numbers, as the protocol specifies (6 each at n = 24).
  roster <- tibble(participant_id = participants,
                   group = rep(1:4, length.out = length(participants)))

  # ---- Answer key: 15 targets and 15 lures per set ------------------------------
  key <- expand_grid(set_id = 1:4, pos = 1:30) %>%
    mutate(sheet_row = ((pos - 1) %/% 6) + 1,
           sheet_col = ((pos - 1) %% 6) + 1,
           item_label = sprintf("set%d_item%02d", set_id, pos)) %>%
    group_by(set_id) %>%
    mutate(present = as.integer(pos %in% sample(1:30, 15))) %>%
    ungroup()
  write_csv(key, file.path(dir, "recall_key.csv"))

  # ---- Recall transcriptions ----------------------------------------------------
  # Planted effect: wireframe ON raises sensitivity. P01 is forced to ceiling (every
  # target hit, no false alarms) so the loglinear correction has something to catch.
  trials <- roster %>%
    tidyr::crossing(trial_order = 1:4) %>%
    left_join(design %>% mutate(group = as.integer(group)),
              by = c("group", "trial_order"))

  sheets <- lapply(seq_len(nrow(trials)), function(i) {
    tr <- trials[i, ]
    k <- key %>% filter(set_id == tr$set_id) %>% arrange(pos)
    ceiling_case <- tr$participant_id == "P01"

    p_hit <- if (ceiling_case) 1.0 else if (tr$wireframe == "on") 0.80 else 0.62
    p_fa  <- if (ceiling_case) 0.0 else if (tr$wireframe == "on") 0.14 else 0.22

    resp <- ifelse(
      k$present == 1,
      ifelse(runif(30) < p_hit, "R", sample(c("U", "N"), 30, TRUE, c(.4, .6))),
      ifelse(runif(30) < p_fa,  "R", sample(c("U", "N"), 30, TRUE, c(.25, .75)))
    )
    if (ceiling_case) resp <- ifelse(k$present == 1, "R", "N")

    c(list(participant_id = tr$participant_id, group = tr$group,
           trial = tr$trial_order, set_id = tr$set_id,
           typed_by = "SIM", typed_date = "2026-09-14"),
      setNames(as.list(resp), sprintf("item_%02d", 1:30)))
  })
  write_csv(bind_rows(lapply(sheets, as_tibble)), file.path(dir, "recall_responses.csv"))

  # ---- Post-Trial form ----------------------------------------------------------
  # TLX option labels read 0,5,...,100, so Forms exports 0-100 directly.
  pt <- lapply(seq_len(nrow(trials)), function(i) {
    tr <- trials[i, ]
    load <- if (tr$wireframe == "on") 38 else 52     # planted: wireframe lowers workload
    tlx <- pmin(100, pmax(0, round(rnorm(6, load, 12) / 5) * 5))
    mecm <- if (tr$wireframe == "on") 4.0 else 3.2

    # Planted: wireframe ON lowers Demand and raises Supply and Understanding, so
    # sa_index must come out higher under ON. Generated blockwise in Q_TRIAL_SA order.
    sa <- c(lik(3, if (tr$wireframe == "on") 3.2 else 4.3),   # D, higher = worse
            lik(2, if (tr$wireframe == "on") 4.6 else 3.8),   # S, higher = better
            lik(5, if (tr$wireframe == "on") 5.1 else 4.1))   # U, higher = better

    # Recall self-rating tracks the planted recognition effect. Both brightness items sit
    # near the midpoint, so the signed and deviation scorings stay small and distinct.
    ptm <- c(lik(1, if (tr$wireframe == "on") 4.8 else 4.0),
             lik(1, 4.2), lik(1, 4.1), lik(1, 4.3))

    out <- c(list(
      "Timestamp" = "2026/09/14 3:15:22 PM",
      "Participant ID" = tr$participant_id,
      "Group Number" = tr$group,
      "Trial number" = tr$trial_order
    ), setNames(as.list(tlx), Q_TLX),
    setNames(as.list(sa), Q_TRIAL_SA),
    setNames(as.list(ptm), Q_PT_MEM),
    list("How much did visual clutter in the environment interfere with your ability to focus on the search task?  " =
           lik(1, if (tr$wireframe == "on") 4.2 else 3.1)),
    setNames(as.list(lik(5, mecm, 0.8, 1, 5)), Q_MEC))
    as_tibble(out)
  })
  write_csv(bind_rows(pt), file.path(dir, "post_trial.csv"))

  # ---- Pre-Study form -----------------------------------------------------------
  # P01 answers 7 to everything: after reversal its SBSOD must NOT be 7, which is the
  # cleanest possible check that the reverse key fires.
  pre <- lapply(seq_len(nrow(roster)), function(i) {
    r <- roster[i, ]
    sb <- if (r$participant_id == "P01") rep(7, 15) else lik(15, 4.4, 1.4)
    as_tibble(c(list(
      "Timestamp" = "2026/09/14 1:02:11 PM",
      "Participant ID" = r$participant_id,
      "Group Number" = r$group,
      "What is your age?" = sample(c("Under 19", "19 - 24", "25 - 34"), 1),
      "   What is your gender?  " = sample(c("Male", "Female", "Non-binary"), 1),
      " How comfortable are you with AR/VR environments? (5 most comfortable)  " = sample(1:5, 1),
      "Do you have prior experience using AR or VR?  " = sample(c("Yes", "No"), 1),
      "Please estimate your cumulative usage hour with AR/VR technology:  " = "1 - 10 hours",
      "Do you currently experience any sight conditions or problems (e.g., severe astigmatism, color blindness, lack of stereoscopic/depth vision, or inability to wear contact lenses/glasses with a headset)?" = "No"
    ), setNames(as.list(sb), Q_SBSOD)))
  })
  write_csv(bind_rows(pre), file.path(dir, "pre_study.csv"))

  # ---- Post-Study form ----------------------------------------------------------
  # P01 reports every SSQ symptom as Severe: total severity must come out at the
  # documented maximum, which pins the Kennedy weights.
  post <- lapply(seq_len(nrow(roster)), function(i) {
    r <- roster[i, ]
    ssq <- if (r$participant_id == "P01") rep("Severe", 16)
           else sample(c("None", "Slight", "Moderate"), 16, TRUE, c(.7, .2, .1))
    as_tibble(c(list(
      "Timestamp" = "2026/09/14 5:40:03 PM",
      "Participant ID" = r$participant_id,
      "Group Number" = r$group,
      "In your own words, what do you think this study was testing?" = "Memory in AR"
    ),
    setNames(as.list(lik(10, 4.2, 1.3)), Q_SART),
    setNames(as.list(ssq), Q_SSQ),
    setNames(as.list(lik(5, 5.0, 1.2)), Q_WF),
    list(
      "How successful were you overall at the memory (object-recall) tasks?" = lik(1, 4.5),
      "How would you rate your performance on the memory (object-recall) task over time?" = lik(1, 4.6),
      "How would you rate the overall brightness of the virtual objects?" = lik(1, 4.1),
      "How would you rate the brightness of the virtual objects compared to the physical objects?" = lik(1, 4.3),
      "Did you feel disoriented during the AR experience?" = lik(1, 2.3),
      "Did you feel disoriented after removing the AR headset?" = lik(1, 1.8),
      "Did you notice any patterns in the layout of the objects? If yes, please describe." = "",
      "How likely were you to walk into a physical object?" = lik(1, 2.6),
      "In your own words, describe any specific way the wireframe did or didn't help you.  " = "It helped."
    )))
  })
  write_csv(bind_rows(post), file.path(dir, "post_study.csv"))

  message("Simulated surveys written to ", normalizePath(dir))
  invisible(dir)
}
