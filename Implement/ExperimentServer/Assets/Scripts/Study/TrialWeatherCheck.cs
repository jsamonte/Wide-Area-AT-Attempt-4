using UnityEngine;
using ARCockpit.Data;

namespace ARCockpit.Study
{
    /// <summary>
    /// Does the sim's ACTUAL environment match the condition this trial is supposed to be flown in?
    ///
    /// WHY. The weather is a label chosen by the SessionPlan, but X-Plane is set up by a human (a situation
    /// file, or the sim's own weather window). Those two can silently disagree, and a CSV labeled RAIN that
    /// was flown in CLEAR is poison: it is not just one lost trial, it quietly contaminates the condition it
    /// is averaged into, and nothing else in the data path would ever catch it.
    ///
    /// So before BEGIN, the panel reads what the sim reports and shows it next to what the plan expects. This
    /// is READ ONLY: it does not set the weather, and it adds no write path (see the X-Plane guardrail in
    /// CLAUDE.md). It also does NOT block a start. A refusal with a participant sitting in the chair, over a
    /// threshold this file guessed at, is worse than a clearly flagged start: the operator can see the
    /// mismatch, fix the sim, and press BEGIN again.
    /// </summary>
    public static class TrialWeatherCheck
    {
        /// <summary>Above this rain fraction the sim counts as raining.</summary>
        public const float RainThreshold = 0.25f;

        /// <summary>Below this it counts as clear. Between the two is "ambiguous", and says so rather than
        /// picking a side: a drizzle that is neither is exactly the case where a confident answer is a lie.</summary>
        public const float ClearThreshold = 0.05f;

        public enum Verdict { NoSim, Matches, Mismatch, Unsure }

        /// <summary>What the sim currently reports, in the operator's words.</summary>
        public static string Observed()
        {
            if (!TrialController.XPlaneConnected) return "no sim connected";

            int h = Mathf.FloorToInt(FlightData.LocalTimeSec / 3600f) % 24;
            int m = Mathf.FloorToInt(FlightData.LocalTimeSec / 60f) % 60;

            return $"{FlightData.VisibilitySm:0.0} sm vis, rain {FlightData.RainPercent * 100f:0}%, " +
                   $"{h:00}:{m:00} local";
        }

        /// <summary>Compare the sim against the slot's expected weather.</summary>
        public static Verdict Check(TrialSlot slot, out string message)
        {
            if (slot == null || string.IsNullOrEmpty(slot.weather))
            {
                // Weather is not a factor in this study configuration, so there is nothing to disagree about.
                message = "";
                return Verdict.Matches;
            }

            if (!TrialController.XPlaneConnected)
            {
                message = "No sim connected, so the weather cannot be checked against the plan.";
                return Verdict.NoSim;
            }

            float rain = FlightData.RainPercent;
            bool wantRain = slot.weather == "rain";

            if (wantRain && rain >= RainThreshold) { message = ""; return Verdict.Matches; }
            if (!wantRain && rain <= ClearThreshold) { message = ""; return Verdict.Matches; }

            if (rain > ClearThreshold && rain < RainThreshold)
            {
                message = $"The sim reports {rain * 100f:0}% rain, which is neither clearly wet nor clearly " +
                          $"dry. This trial expects {slot.weather.ToUpperInvariant()}. Check the sim's weather " +
                          "before you begin.";
                return Verdict.Unsure;
            }

            message = $"MISMATCH: this trial expects {slot.weather.ToUpperInvariant()}, but the sim reports " +
                      $"{rain * 100f:0}% rain. Load the right weather, or this file will be labeled with a " +
                      "condition it was not flown in.";
            return Verdict.Mismatch;
        }

        /// <summary>The line that goes in the CSV header: what was expected AND what was observed, so the
        /// file itself records whether they agreed. A dashboard warning nobody wrote down is not evidence.</summary>
        public static string HeaderLine(TrialSlot slot)
        {
            Verdict v = Check(slot, out _);
            string expected = slot == null || string.IsNullOrEmpty(slot.weather) ? "n/a" : slot.weather;
            return $"weather_expected={expected}, weather_observed=({Observed()}), check={v}";
        }
    }
}
