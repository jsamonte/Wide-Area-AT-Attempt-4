# =====================================================================================
# 10_surveys.R  --  Score the three Google Forms.
#
# COLUMN MATCHING. Forms exports the full question text as the column name, including
# stray double spaces and trailing whitespace that survive copy-paste unpredictably.
# Matching on exact strings would break the first time someone retypes a question mark.
# Every item below is therefore located by a REGEX on a distinctive fragment, and any
# item that matches zero columns -- or more than one -- is reported by name rather than
# silently returning NA. A survey scale that quietly loses an item still produces a
# plausible-looking mean, which is the whole problem.
#
# SCALE DIRECTIONS ARE NOT ASSUMED. Each block documents which way is "more" and which
# items reverse, because that is the class of error no amount of testing catches.
# =====================================================================================

suppressPackageStartupMessages({
  library(readr); library(dplyr); library(tidyr); library(stringr); library(tibble)
})

# ---------------------------------------------------------------------------------
# Matching helpers
# ---------------------------------------------------------------------------------
find_col <- function(df, pattern, label, required = TRUE) {
  hits <- grep(pattern, names(df), ignore.case = TRUE, perl = TRUE, value = TRUE)
  if (length(hits) == 1L) return(hits)
  if (length(hits) == 0L) {
    if (required) warning("Survey item NOT FOUND: ", label, "  [pattern: ", pattern, "]",
                          call. = FALSE)
    return(NA_character_)
  }
  warning("Survey item matched ", length(hits), " columns, using the first: ", label,
          "\n    -> ", paste(hits, collapse = "\n    -> "), call. = FALSE)
  hits[1]
}

# Pull a numeric item. Forms exports the option LABEL, so a scale whose options are
# labelled 0,5,...,100 exports as 0..100 and one labelled 1..7 exports as 1..7.
num_item <- function(df, pattern, label, required = TRUE) {
  col <- find_col(df, pattern, label, required)
  if (is.na(col)) return(rep(NA_real_, nrow(df)))
  suppressWarnings(as.numeric(str_extract(as.character(df[[col]]), "-?\\d+\\.?\\d*")))
}

txt_item <- function(df, pattern, label, required = TRUE) {
  col <- find_col(df, pattern, label, required)
  if (is.na(col)) return(rep(NA_character_, nrow(df)))
  str_trim(as.character(df[[col]]))
}

# Reverse a Likert item. `max_plus_min` is (scale max + scale min): 8 for 1-7, 6 for 1-5.
rev_item <- function(x, max_plus_min) max_plus_min - x

# Mean across items, but only where enough items were answered. A 15-item scale scored
# from 3 answers is not the same measurement, and na.rm = TRUE would hide that.
scale_mean <- function(..., min_prop = 0.8) {
  m <- cbind(...)
  n_ok <- rowSums(!is.na(m))
  out <- rowMeans(m, na.rm = TRUE)
  out[n_ok < ceiling(min_prop * ncol(m))] <- NA_real_
  out
}

# ---------------------------------------------------------------------------------
# NASA-TLX  (Post-Trial, section 2)
# ---------------------------------------------------------------------------------
# Six items. Your option labels read 0, 5, 10, ... 100, so Forms exports 0-100 directly
# and no rescaling is needed. Older drafts of the form used bare 1-21 option numbers;
# this handles BOTH and says which it saw, because guessing wrong rescales the whole
# workload measure by a factor of five.
#
# PERFORMANCE IS NOT REVERSE-CODED. The item reads "0 (Perfect) to 100 (Failure)", which
# is already higher-is-worse like the other five. Reversing it -- the reflex, because the
# canonical TLX anchors Performance the other way -- would push the composite in the
# wrong direction. This is the single most likely scoring error in this study.
tlx_patterns <- c(
  mental     = "mentally demanding",
  physical   = "physically demanding",
  temporal   = "hurried or rushed",
  # "successful were you" alone ALSO matches the post-trial memory item "How successful
  # were you overall at the memory (object-recall) task?", so this pattern must stay
  # narrow. Two matches would make find_col() take whichever column comes first.
  perform    = "successful were you in accomplishing",
  effort     = "hard did you have to work",
  frustration = "insecure, discouraged"
)

score_tlx <- function(df) {
  raw <- sapply(names(tlx_patterns), function(k)
    num_item(df, tlx_patterns[[k]], paste("TLX", k)), simplify = FALSE)
  raw <- as_tibble(raw)

  observed_max <- suppressWarnings(max(unlist(raw), na.rm = TRUE))
  if (is.finite(observed_max) && observed_max <= 21) {
    message("TLX: values look like 1-21 option indices; rescaling with (x - 1) * 5.")
    raw <- raw %>% mutate(across(everything(), ~ (.x - 1) * 5))
  } else {
    message("TLX: values already on the 0-100 scale; no rescaling.")
  }

  raw %>%
    rename_with(~ paste0("tlx_", .x)) %>%
    mutate(tlx_raw = scale_mean(tlx_mental, tlx_physical, tlx_temporal,
                                tlx_perform, tlx_effort, tlx_frustration))
}

# ---------------------------------------------------------------------------------
# Perceived mental clutter (Post-Trial, section 5) -- single item, 1-7, higher = worse
# ---------------------------------------------------------------------------------
score_clutter <- function(df) {
  tibble(clutter = num_item(df, "visual clutter", "Clutter"))
}

# ---------------------------------------------------------------------------------
# MEC-SPQ Spatial Situational Model (Post-Trial, section 6) -- 5 items, 1-5
# ---------------------------------------------------------------------------------
# All five are positively worded: higher = stronger spatial model. No reversals.
mec_patterns <- c(
  arrangement = "imagine the arrangement",
  precise     = "precise idea of the spatial",
  size        = "estimate of the size",
  image       = "concrete mental image",
  behind      = "locations of objects behind"
)

score_mec <- function(df) {
  raw <- sapply(names(mec_patterns), function(k)
    num_item(df, mec_patterns[[k]], paste("MEC-SPQ", k)), simplify = FALSE) %>% as_tibble()
  raw %>%
    rename_with(~ paste0("mec_", .x)) %>%
    mutate(mec_ssm = scale_mean(mec_arrangement, mec_precise, mec_size, mec_image, mec_behind))
}

# ---------------------------------------------------------------------------------
# Trial-level situation awareness (Post-Trial, section 3) -- 10 items, 1-7
# ---------------------------------------------------------------------------------
# THIS IS NOT THE SART, despite the heading the form gives it. Taylor's (1990) SART
# needs all ten of his dimensions. This section shares only five of them -- complexity,
# spare capacity, concentration, division of attention, familiarity -- and omits
# instability, variability, arousal, information quantity, and information quality.
# The other five items here (anticipation effort, clarity of the mental picture,
# identifying important aspects, meaning and significance, prediction) are not SART
# dimensions at all; they follow Endsley's perception/comprehension/projection framing.
#
# CONSEQUENCE: sart_total (post-study) and sa_index (here) are different measures on
# different scales. Never compare them, pool them, or share an axis between them.
#
# Why score it at all: the post-study SART is collected once per participant and cannot
# enter the condition model. This section is the ONLY situation-awareness measure that
# varies with wireframe state, so every trial-level SA claim rests on it. Until now it
# was read in and silently dropped.
#
# DIRECTIONS. All ten run 1 = Low to 7 = High as printed on the form.
#   D (higher = worse):  divided attention, anticipation effort, complexity
#   S (higher = better): spare capacity, attention devoted
#   U (higher = better): familiarity, mental picture, important aspects, meaning,
#                        prediction
# Subscales are MEANS, not sums like the post-study SART, because these three hold
# 3/2/5 items; summing would let Understanding outweigh Demand on item count alone.
sa_items <- tribble(
  ~key,         ~pattern,                               ~dim,
  "divided",    "divide your attention",                 "D",
  "anticipate", "mental effort was required",            "D",
  "complex",    "How complex was the situation",         "D",
  "spare",      "spare mental capacity",                 "S",
  "devote",     "attention could you devote",            "S",
  "familiar",   "familiar were you with the situation",  "U",
  "picture",    "clear was your mental picture",         "U",
  "important",  "identify and focus on important",       "U",
  "meaning",    "meaning and significance",              "U",
  "predict",    "predict what would happen",             "U"
)

score_trial_sa <- function(df) {
  vals <- lapply(seq_len(nrow(sa_items)), function(i)
    num_item(df, sa_items$pattern[i], paste("Trial SA", sa_items$key[i])))
  names(vals) <- paste0("sa_", sa_items$key)
  v <- as_tibble(vals)

  sub_mean <- function(dim)
    do.call(scale_mean, as.list(v[paste0("sa_", sa_items$key[sa_items$dim == dim])]))

  v %>% mutate(
    sa_demand        = sub_mean("D"),
    sa_supply        = sub_mean("S"),
    sa_understanding = sub_mean("U"),
    # Taylor-style contrast computed on the subscale means. Ranges -5 to 13, and zero
    # carries no meaning -- treat it as an interval score, not a ratio one.
    sa_index         = sa_understanding - (sa_demand - sa_supply),
    # Exploratory. The three items with no SART counterpart, i.e. Endsley's levels.
    # sa_picture is the item that most directly targets this study's hypothesis.
    sa_endsley       = scale_mean(sa_picture, sa_meaning, sa_predict)
  )
}

# ---------------------------------------------------------------------------------
# Trial-level memory and brightness (Post-Trial, section 4) -- 4 items, 1-7
# ---------------------------------------------------------------------------------
# The post-study form asks these same four questions about the session as a whole, and
# score_poststudy_impressions() already owns the names mem_* and bright_*. These are the
# per-trial versions, prefixed pt_ so both survive the same join.
#
# BRIGHTNESS IS BIPOLAR, midpoint 4 = "about right", so it is scored signed (which way
# it was off) and as absolute deviation (how far off), exactly as at post-study.
# pt_mem_trend is bipolar too (1 = got worse, 7 = got better, 4 = unchanged) but is kept
# raw to match the post-study treatment of the same item.
score_trial_memory <- function(df) {
  bright_abs <- num_item(df, "overall brightness of the virtual", "Trial brightness overall")
  bright_rel <- num_item(df, "brightness of the virtual objects compared",
                         "Trial brightness relative")

  tibble(
    pt_mem_success = num_item(df, "successful were you overall at the memory",
                              "Trial memory success"),
    pt_mem_trend   = num_item(df, "performance on the memory .{0,20}task over time",
                              "Trial memory trend"),

    pt_bright_overall_signed = bright_abs - 4,
    pt_bright_overall_dev    = abs(bright_abs - 4),
    pt_bright_rel_signed     = bright_rel - 4,
    pt_bright_rel_dev        = abs(bright_rel - 4)
  )
}

# ---------------------------------------------------------------------------------
# SBSOD (Pre-Study, section 2) -- 15 items, 1-7, higher = better sense of direction
# ---------------------------------------------------------------------------------
# Hegarty et al. (2002). Seven positively worded items score as-is; eight negatively
# worded items reverse as 8 - x. Participant-level covariate, not an outcome.
sbsod_items <- tribble(
  ~key,          ~pattern,                                  ~reverse,
  "directions",  "very good at giving directions",           FALSE,
  "memory",      "poor memory for where I left",             TRUE,
  "distances",   "good at judging distances",                FALSE,
  "sense",       "sense of direction.{0,4} is very good",    FALSE,
  "cardinal",    "cardinal directions",                      FALSE,
  "lost",        "easily get lost in a new city",            TRUE,
  "enjoymaps",   "enjoy reading maps",                       FALSE,
  "trouble",     "trouble understanding directions",         TRUE,
  "readmaps",    "very good at reading maps",                FALSE,
  "passenger",   "riding as a passenger",                    TRUE,
  "notenjoy",    "don.{0,3}t enjoy giving directions",       TRUE,
  "notimportant","not important to me to know where I am",   TRUE,
  "someoneelse", "let someone else do the navigational",     TRUE,
  "newroute",    "remember a new route",                     FALSE,
  "mentalmap",   "very good .{0,3}mental map",               TRUE
)

score_sbsod <- function(df) {
  vals <- lapply(seq_len(nrow(sbsod_items)), function(i) {
    x <- num_item(df, sbsod_items$pattern[i], paste("SBSOD", sbsod_items$key[i]))
    if (sbsod_items$reverse[i]) rev_item(x, 8) else x
  })
  names(vals) <- paste0("sbsod_", sbsod_items$key)
  as_tibble(vals) %>% mutate(sbsod = do.call(scale_mean, unname(vals)))
}

# ---------------------------------------------------------------------------------
# SART (Post-Study, section 2) -- 10 items, 1-7
# ---------------------------------------------------------------------------------
# Taylor (1990) three-dimensional form:
#   Demand        = items 1-3   (instability, variability, complexity)
#   Supply        = items 4-7   (arousal, spare capacity, concentration, division)
#   Understanding = items 8-10  (information quantity, quality, familiarity)
#   SART = U - (D - S)
# Collected ONCE per participant about the session as a whole, so it is a
# participant-level descriptive and cannot enter the condition model.
sart_items <- tribble(
  ~key,        ~pattern,                        ~dim,
  "changeable","How changeable was the",         "D",
  "many",      "changing at once",               "D",
  "complex",   "How complicated was the",        "D",
  "alert",     "alert and ready for activity",   "S",
  "spare",     "mental capacity did you have",   "S",
  "concentrate","much were you concentrating",   "S",
  "divided",   "How divided was your attention", "S",
  "quantity",  "much information did you gain",  "U",
  "quality",   "How good was the information",   "U",
  "familiar",  "How familiar were you",          "U"
)

score_sart <- function(df) {
  vals <- lapply(seq_len(nrow(sart_items)), function(i)
    num_item(df, sart_items$pattern[i], paste("SART", sart_items$key[i])))
  names(vals) <- paste0("sart_", sart_items$key)
  v <- as_tibble(vals)

  d <- v[paste0("sart_", sart_items$key[sart_items$dim == "D"])]
  s <- v[paste0("sart_", sart_items$key[sart_items$dim == "S"])]
  u <- v[paste0("sart_", sart_items$key[sart_items$dim == "U"])]

  v %>% mutate(
    sart_demand        = rowSums(d),
    sart_supply        = rowSums(s),
    sart_understanding = rowSums(u),
    sart_total         = sart_understanding - (sart_demand - sart_supply)
  )
}

# ---------------------------------------------------------------------------------
# SSQ (Post-Study, section 3) -- 16 symptoms, None/Slight/Moderate/Severe -> 0..3
# ---------------------------------------------------------------------------------
# Kennedy et al. (1993). Three overlapping subscales -- several symptoms load on more
# than one, which is correct and not a duplication error -- and a total severity score.
ssq_levels <- c("none" = 0, "slight" = 1, "moderate" = 2, "severe" = 3)

ssq_items <- tribble(
  ~key,          ~pattern,                  ~N, ~O, ~D,
  "discomfort",  "General discomfort",       1L, 1L, 0L,
  "fatigue",     "^Fatigue",                 0L, 1L, 0L,
  "headache",    "Headache",                 0L, 1L, 0L,
  "eyestrain",   "Eye ?strain",              0L, 1L, 0L,
  "focus",       "Difficulty focusing",      0L, 1L, 1L,
  "salivation",  "Increased salivation",     1L, 0L, 0L,
  "sweating",    "Sweating",                 1L, 0L, 0L,
  "nausea",      "^Nausea",                  1L, 0L, 1L,
  "concentrate", "Difficulty concentrating", 1L, 1L, 0L,
  "fullness",    "Fullness of head",         0L, 0L, 1L,
  "blurred",     "Blurred vision",           0L, 1L, 1L,
  "dizzyopen",   "Dizziness \\(eyes open",   0L, 0L, 1L,
  "dizzyclosed", "Dizziness \\(eyes closed", 0L, 0L, 1L,
  "vertigo",     "Vertigo",                  0L, 0L, 1L,
  "stomach",     "Stomach awareness",        1L, 0L, 0L,
  "burping",     "Burping",                  1L, 0L, 0L
)

score_ssq <- function(df) {
  vals <- lapply(seq_len(nrow(ssq_items)), function(i) {
    raw <- txt_item(df, ssq_items$pattern[i], paste("SSQ", ssq_items$key[i]))
    unname(ssq_levels[tolower(raw)])
  })
  names(vals) <- paste0("ssq_", ssq_items$key)
  v <- as_tibble(vals)
  m <- as.matrix(v)

  n_raw <- as.vector(m %*% ssq_items$N)
  o_raw <- as.vector(m %*% ssq_items$O)
  d_raw <- as.vector(m %*% ssq_items$D)

  v %>% mutate(
    ssq_nausea         = n_raw * 9.54,
    ssq_oculomotor     = o_raw * 7.58,
    ssq_disorientation = d_raw * 13.92,
    ssq_total          = (n_raw + o_raw + d_raw) * 3.74
  )
}

# ---------------------------------------------------------------------------------
# Post-Study impressions (sections 4-6)
# ---------------------------------------------------------------------------------
# Wireframe helpfulness: 4 positively worded items plus "made the environment feel more
# cluttered or distracting", which reverses for the composite and is ALSO kept raw,
# because clutter is arguably its own construct rather than negative helpfulness.
#
# BRIGHTNESS IS BIPOLAR. "Too dark (1) ... Too bright (7)" has its optimum in the MIDDLE.
# A mean of 4 means "about right"; 2 and 6 are both wrong, in opposite directions.
# Averaging it as though higher were better is meaningless, so it is scored two ways:
# signed (which direction) and absolute (how far off), and never composited with
# unipolar items.
score_poststudy_impressions <- function(df) {
  wf_help    <- num_item(df, "helpful was the blue wireframe", "WF overall")
  wf_layout  <- num_item(df, "understand the layout", "WF layout")
  wf_path    <- num_item(df, "plan my path", "WF path")
  wf_targets <- num_item(df, "targets apart from distractors", "WF targets")
  wf_clutter <- num_item(df, "more cluttered or distracting", "WF clutter")

  bright_abs <- num_item(df, "overall brightness of the virtual", "Brightness overall")
  bright_rel <- num_item(df, "brightness of the virtual objects compared", "Brightness relative")

  tibble(
    wf_helpful = wf_help, wf_layout = wf_layout, wf_path = wf_path,
    wf_targets = wf_targets, wf_clutter_raw = wf_clutter,
    wf_composite = scale_mean(wf_help, wf_layout, wf_path, wf_targets,
                              rev_item(wf_clutter, 8)),

    mem_success = num_item(df, "successful were you overall at the memory", "Memory success"),
    mem_trend   = num_item(df, "performance on the memory .{0,20}task over time", "Memory trend"),

    bright_overall_signed = bright_abs - 4,
    bright_overall_dev    = abs(bright_abs - 4),
    bright_rel_signed     = bright_rel - 4,
    bright_rel_dev        = abs(bright_rel - 4),

    disorient_during = num_item(df, "disoriented during", "Disorientation during"),
    disorient_after  = num_item(df, "disoriented after removing", "Disorientation after"),
    collision_risk   = num_item(df, "likely were you to walk into", "Collision likelihood")
  )
}

# ---------------------------------------------------------------------------------
# Form loaders
# ---------------------------------------------------------------------------------
# Participant IDs are typed by hand into three separate forms, so they are canonicalised
# on the way in: trimmed, upper-cased, internal spaces removed. "p01 " and "P01" are one
# person, and discovering that during a join is discovering it too late.
canon_pid <- function(x) toupper(str_remove_all(str_trim(as.character(x)), "\\s+"))

load_post_trial <- function(path = cfg$survey_trial_path) {
  df <- readr::read_csv(path, na = c("", "NA"), col_types = readr::cols(.default = "c"),
                        progress = FALSE)
  bind_cols(
    tibble(
      participant_id = canon_pid(txt_item(df, "Participant ID", "PID")),
      group          = num_item(df, "Group Number", "Group"),
      trial_order    = num_item(df, "Trial number", "Trial")
    ),
    score_tlx(df), score_clutter(df), score_mec(df),
    score_trial_sa(df), score_trial_memory(df)
  )
}

load_pre_study <- function(path = cfg$survey_pre_path) {
  df <- readr::read_csv(path, na = c("", "NA"), col_types = readr::cols(.default = "c"),
                        progress = FALSE)
  bind_cols(
    tibble(
      participant_id = canon_pid(txt_item(df, "Participant ID", "PID")),
      group          = num_item(df, "Group Number", "Group"),
      age_band       = txt_item(df, "What is your age", "Age"),
      gender         = txt_item(df, "What is your gender", "Gender"),
      arvr_comfort   = num_item(df, "comfortable are you with AR/VR", "AR/VR comfort"),
      arvr_prior     = txt_item(df, "prior experience using AR or VR", "Prior AR/VR"),
      arvr_hours     = txt_item(df, "cumulative usage hour", "AR/VR hours"),
      sight_issue    = txt_item(df, "sight conditions or problems", "Sight condition")
    ),
    score_sbsod(df)
  )
}

load_post_study <- function(path = cfg$survey_post_path) {
  df <- readr::read_csv(path, na = c("", "NA"), col_types = readr::cols(.default = "c"),
                        progress = FALSE)
  bind_cols(
    tibble(
      participant_id = canon_pid(txt_item(df, "Participant ID", "PID")),
      group          = num_item(df, "Group Number", "Group"),
      guessed_aim    = txt_item(df, "what do you think this study was testing", "Aim guess"),
      wf_freetext    = txt_item(df, "specific way the wireframe", "WF free text", required = FALSE)
    ),
    score_sart(df), score_ssq(df), score_poststudy_impressions(df)
  )
}
