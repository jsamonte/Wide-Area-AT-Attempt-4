// AnchorBudgetGuard.cs
// Startup sanity check for World Locking Tools' anchor configuration, plus optional
// anchor-count telemetry.
//
// Background — the ML2 abort of 2026-07-18..20:
//   With anchorSubsystem = ARFoundation and MaxLocalAnchors = 0 ("unlimited"),
//   AnchorManagerARF adds a real ARAnchor roughly every MinNewAnchorDistance metres of
//   travel and never culls (CheckForCull only culls when the budget is positive). In a
//   wide-area session that grows without bound until the Magic Leap perception service
//   starts failing anchor queries, at which point the ML OpenXR runtime throws
//   ml::perception::Exception on its own anchor-polling thread. That thread has no
//   handler, so it aborts the process:
//       "terminating with uncaught exception of type ml::perception::Exception"
//       Fatal signal 6 (SIGABRT) in tid <n> (UnityMain), pid <n> (com.Trial1)
//   That configuration produced 21 aborts. It was resolved on 2026-07-20 by switching
//   anchorSubsystem to Null, which stops WLT creating platform anchors altogether.
//
// This component guards against silently drifting back into the crashing combination —
// e.g. flipping anchorSubsystem back to ARFoundation, or ticking "Use Defaults", which
// calls AnchorSettings.InitToDefaults() and resets MaxLocalAnchors to 0.

using System.Collections;
using UnityEngine;
using Microsoft.MixedReality.WorldLocking.Core;

public class AnchorBudgetGuard : MonoBehaviour
{
    [Header("Anchor Budget")]
    [Tooltip("Cap applied when a real anchor subsystem is active but the budget is unlimited (<= 0).")]
    [SerializeField] private int fallbackMaxLocalAnchors = 128;

    [Header("Telemetry")]
    [Tooltip("Log the live spongy anchor count on this interval. Zero disables.")]
    [SerializeField] private float reportIntervalSeconds = 60f;

    [Tooltip("Warn once the anchor count reaches this fraction of the budget.")]
    [Range(0.5f, 1f)]
    [SerializeField] private float warnAtFractionOfBudget = 0.9f;

    private bool hasWarnedNearBudget = false;

    private IEnumerator Start()
    {
        WorldLockingManager wltManager = WorldLockingManager.GetInstance();
        if (wltManager == null)
        {
            Debug.LogError("[AnchorBudget] No WorldLockingManager instance. Guard disabled.");
            yield break;
        }

        // The anchor manager is built asynchronously during WLT startup.
        yield return new WaitUntil(() => wltManager.AnchorManager != null);

        IAnchorManager anchorManager = wltManager.AnchorManager;
        var subsystem = wltManager.AnchorSettings.anchorSubsystem;

        if (subsystem == AnchorSettings.AnchorSubsystem.Null)
        {
            // AnchorManagerNull creates SpongyAnchorNull dummies: no platform anchors, so
            // no ML runtime anchor traffic and nothing to budget. Also means WLT is not
            // actually correcting for drift.
            Debug.Log(
                "[AnchorBudget] Anchor subsystem is Null — WLT creates no platform anchors. " +
                "Crash-safe, but world-locking drift correction is inert.");
            yield break;
        }

        if (anchorManager.MaxLocalAnchors <= 0)
        {
            Debug.LogError(
                $"[AnchorBudget] ❌ Anchor subsystem is {subsystem} with an unlimited budget " +
                $"(MaxLocalAnchors={anchorManager.MaxLocalAnchors}). This is the configuration that " +
                $"aborted the ML2 runtime in long wide-area sessions. Clamping to {fallbackMaxLocalAnchors}. " +
                $"Fix WorldLockingContext > Anchor Management in the scene.");

            anchorManager.MaxLocalAnchors = fallbackMaxLocalAnchors;
        }
        else
        {
            Debug.Log(
                $"[AnchorBudget] ✅ Subsystem={subsystem}, MaxLocalAnchors={anchorManager.MaxLocalAnchors}, " +
                $"MinNewAnchorDistance={anchorManager.MinNewAnchorDistance}m.");
        }

        if (reportIntervalSeconds > 0f)
            StartCoroutine(ReportAnchorCount(anchorManager));
    }

    private IEnumerator ReportAnchorCount(IAnchorManager anchorManager)
    {
        // SpongyAnchors is only exposed on the concrete base class, not the interface.
        AnchorManager concrete = anchorManager as AnchorManager;
        if (concrete == null)
        {
            Debug.Log("[AnchorBudget] Anchor manager does not expose a spongy anchor list; telemetry disabled.");
            yield break;
        }

        var wait = new WaitForSeconds(reportIntervalSeconds);

        while (true)
        {
            yield return wait;

            int count = concrete.SpongyAnchors.Count;
            int budget = anchorManager.MaxLocalAnchors;

            if (budget > 0 && count >= budget * warnAtFractionOfBudget)
            {
                if (!hasWarnedNearBudget)
                {
                    Debug.LogWarning($"[AnchorBudget] ⚠️ Spongy anchors {count}/{budget} — culling is now load-bearing.");
                    hasWarnedNearBudget = true;
                }
            }
            else
            {
                hasWarnedNearBudget = false;
            }

            Debug.Log($"[AnchorBudget] Spongy anchors: {count}/{budget}");
        }
    }
}
