using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// Lets participants collect gems with the controller, using the same point-and-press gesture that selects a
/// button in the menu, and giving them the same fill-bar countdown the eye-gaze path gives.
///
/// It deliberately reuses the menu's own machinery rather than reading the trigger directly: the ray comes
/// from the scene's <see cref="XRRayInteractor"/> (the one already drawing the visible controller line) and
/// the button comes from the <see cref="ActionBasedController"/>'s Select action. In Application.unity the
/// controller's Select, Activate and UI Press actions are all bound to the same action, so "the button that
/// clicks a menu item" and "the button that collects a gem" are the same physical press by construction --
/// not two bindings that have to be kept in sync by hand.
///
/// Both the fill and the collection are delegated to <see cref="EyeAndHeadTracker"/>, which owns the target
/// list, the fill maths and the destruction log. A controller collection is therefore logged, autosaved and
/// counted identically to an eye-dwell one, and still fires the end-of-trial handshake with TrialManager.
/// </summary>
public class ControllerGemCollector : MonoBehaviour
{
    /// <summary>What has to be true for the countdown to run.</summary>
    public enum FillTrigger
    {
        /// <summary>Hold the select button while pointing at the gem. Releasing abandons the countdown.</summary>
        HoldSelectButton,

        /// <summary>Merely pointing at the gem fills it; no button involved. The controller equivalent of eye dwell.</summary>
        PointOnly,
    }

    [Header("Wiring (left empty, these are found in the scene at Start)")]
    [Tooltip("The tracker that owns the target list, the fill maths and the destruction log. Found automatically if unset.")]
    [SerializeField] private EyeAndHeadTracker tracker;

    [Tooltip("The controller whose Select action collects a gem. This is the same action the menu uses for " +
             "UI Press. Found automatically if unset.")]
    [SerializeField] private ActionBasedController controller;

    [Tooltip("The ray used to decide which gem is being pointed at. Normally the interactor on the same " +
             "GameObject as the controller above, i.e. the visible controller line. Found automatically if unset.")]
    [SerializeField] private XRRayInteractor rayInteractor;

    [Header("Behaviour")]
    [Tooltip("Master switch for controller collection. Turn this off for an eye-gaze-only condition.")]
    [SerializeField] private bool enableControllerCollection = true;

    [Tooltip("Use the SAME duration as eye dwell (the tracker's Min Dwell Time Over Target), so the two " +
             "modalities are directly comparable and there is only one number to change. Uncheck to set an " +
             "independent controller duration below.")]
    [SerializeField] private bool matchEyeDwellTime = true;

    [Tooltip("Seconds the gem must be held before it is collected, shown as the fill bar filling up. " +
             "Only used when 'Match Eye Dwell Time' is unchecked. Set to 0 to collect instantly on press, " +
             "with no countdown and no fill.")]
    [SerializeField] [Range(0f, 10f)] private float secondsToCollect = 3f;

    /// <summary>
    /// The countdown actually in force this frame. Read through a property rather than copied at Start so
    /// that changing the tracker's dwell time -- in the Inspector mid-session, or between trials -- moves
    /// both modalities together instead of leaving the controller on a stale value.
    /// </summary>
    private float EffectiveSecondsToCollect =>
        (matchEyeDwellTime && tracker != null) ? tracker.MinDwellTimeOverTarget : secondsToCollect;

    [Tooltip("Whether the countdown needs the select button held, or runs on pointing alone.")]
    [SerializeField] private FillTrigger fillTrigger = FillTrigger.HoldSelectButton;

    [Tooltip("Keep the accumulated countdown when the ray leaves the gem, instead of resetting it. The bar " +
             "resumes where it left off when the participant comes back to the same gem. Progress is still " +
             "dropped when they move to a DIFFERENT gem.")]
    [SerializeField] private bool keepProgressWhenAimingAway = false;

    [Tooltip("Ignore presses that land while the ray is over a UI element, so clicking a menu button can " +
             "never also collect a gem sitting behind it.")]
    [SerializeField] private bool ignorePressesOverUI = true;

    [Tooltip("Minimum seconds between two controller collections. Guards against one physical press being " +
             "read as several, and against a participant sweeping the ray across a cluster while held down.")]
    [SerializeField] [Range(0f, 2f)] private float minSecondsBetweenCollections = 0.25f;

    [Tooltip("Log every press that did not collect anything, with the reason. Useful while piloting; noisy " +
             "in a real session.")]
    [SerializeField] private bool logMissedPresses = false;

    [Header("Ray Length During Tasks")]
    [Tooltip("Shorten the controller ray while a tutorial or trial is running, so gems must be approached " +
             "rather than collected from across the site. The full length is restored in the menu, where " +
             "the same ray has to reach the buttons.")]
    [SerializeField] private bool shortenRayDuringTasks = true;

    [Tooltip("How far the controller ray reaches during a task, in metres. An absolute distance rather than a " +
             "fraction of the interactor's own setting: the reach a participant has to walk into is a property " +
             "of the study, not something that should move if the interactor is ever retuned.")]
    [SerializeField] [Range(0.1f, 30f)] private float taskRayLengthMeters = 1.75f;

    [Tooltip("Also shorten the visible line so it ends where the ray actually stops. Without this the line " +
             "still reaches its normal length and participants aim at gems the ray can no longer touch.")]
    [SerializeField] private bool matchLineVisualToRayLength = true;

    // Captured at Start, before anything is changed, so the menu always gets the authored values back
    // rather than a value some earlier shortening left behind.
    private float originalMaxRaycastDistance;
    private XRInteractorLineVisual lineVisual;
    private float originalLineLength;
    private bool originalOverrideLineLength;
    private bool rayIsShortened;

    private float lastCollectionTime = -999f;
    private bool wasPressed;

    // The gem currently being counted down, and how much of the countdown it has banked. Kept as a single
    // pair rather than a per-gem table: the countdown belongs to what the participant is aiming at right now.
    private MeshRenderer heldTarget;
    private float holdTimer;

    private void Start()
    {
        if (tracker == null) tracker = FindObjectOfType<EyeAndHeadTracker>();
        if (controller == null) controller = FindObjectOfType<ActionBasedController>();
        if (rayInteractor == null)
        {
            // Prefer the interactor sitting on the same object as the controller: a scene can hold several
            // (left hand, right hand, a gaze interactor), and collecting off a different ray than the one the
            // participant can see would be indistinguishable from the feature being broken.
            if (controller != null) rayInteractor = controller.GetComponent<XRRayInteractor>();
            if (rayInteractor == null) rayInteractor = FindObjectOfType<XRRayInteractor>();
        }

        if (tracker == null)
            Debug.LogError("[CONTROLLER:CRIT] ControllerGemCollector found no EyeAndHeadTracker: gems cannot be " +
                           "collected with the controller.");
        if (controller == null)
            Debug.LogError("[CONTROLLER:CRIT] ControllerGemCollector found no ActionBasedController: the select " +
                           "button will never be read.");
        if (rayInteractor == null)
            Debug.LogError("[CONTROLLER:CRIT] ControllerGemCollector found no XRRayInteractor: there is no ray " +
                           "to decide what is being pointed at.");

        if (rayInteractor != null)
        {
            originalMaxRaycastDistance = rayInteractor.maxRaycastDistance;

            lineVisual = rayInteractor.GetComponent<XRInteractorLineVisual>();
            if (lineVisual != null)
            {
                originalLineLength = lineVisual.lineLength;
                originalOverrideLineLength = lineVisual.overrideInteractorLineLength;
            }
        }
    }

    /// <summary>
    /// Keeps the ray short for the duration of a task and full length in the menu. Driven by the tracker's
    /// recording flag rather than a phase variable of its own: recording is on for exactly the tutorial and
    /// the trials, so the ray can never be left short under a menu the participant then cannot click.
    /// </summary>
    private void UpdateRayLength()
    {
        if (!shortenRayDuringTasks || rayInteractor == null) return;

        bool shouldShorten = tracker != null && tracker.IsRecording;
        if (shouldShorten == rayIsShortened) return;

        rayIsShortened = shouldShorten;

        // Never lengthen the ray: if the interactor was authored shorter than the task distance, the authored
        // value stands rather than this quietly extending the participant's reach.
        float distance = shouldShorten
            ? Mathf.Min(taskRayLengthMeters, originalMaxRaycastDistance)
            : originalMaxRaycastDistance;

        rayInteractor.maxRaycastDistance = distance;

        if (matchLineVisualToRayLength && lineVisual != null)
        {
            if (shouldShorten)
            {
                // The line has its own length, independent of the raycast: left alone it would keep drawing
                // past the point the ray stops, pointing at gems that can no longer be hit.
                lineVisual.overrideInteractorLineLength = true;
                lineVisual.lineLength = distance;
            }
            else
            {
                lineVisual.overrideInteractorLineLength = originalOverrideLineLength;
                lineVisual.lineLength = originalLineLength;
            }
        }

        Debug.Log($"[CONTROLLER] Ray length set to {distance:F2}m ({(shouldShorten ? "task" : "menu")}).");
    }

    /// <summary>Put the interactor back exactly as it was authored.</summary>
    private void RestoreRayLength()
    {
        if (!rayIsShortened || rayInteractor == null) return;

        rayInteractor.maxRaycastDistance = originalMaxRaycastDistance;
        if (matchLineVisualToRayLength && lineVisual != null)
        {
            lineVisual.overrideInteractorLineLength = originalOverrideLineLength;
            lineVisual.lineLength = originalLineLength;
        }

        rayIsShortened = false;
    }

    private void OnDisable()
    {
        // A claim outlives this component otherwise, and the eye path would refuse to draw that gem's bar
        // for the rest of the trial.
        if (tracker != null)
        {
            ReleaseHeldTarget();
            tracker.SuspendEyeDwell(false);
        }

        RestoreRayLength();
    }

    private void Update()
    {
        // Outside the enable check: the ray length is about how far the controller can reach at all, which
        // still matters in an eye-gaze-only condition where controller collection is switched off.
        UpdateRayLength();

        if (!enableControllerCollection) return;
        if (tracker == null || controller == null || rayInteractor == null) return;

        var action = controller.selectAction.action;

        bool pressed = action != null && action.IsPressed();
        bool justPressed = pressed && !wasPressed;
        wasPressed = pressed;

        float required = EffectiveSecondsToCollect;

        // No countdown configured: keep the original one-press-one-gem behaviour, with no fill to drive.
        if (required <= 0f)
        {
            tracker.SuspendEyeDwell(false);
            if (justPressed) TryInstantCollect();
            return;
        }

        MeshRenderer aimedAt = ResolveAimedTarget(out string missReason);

        // Aiming at a different gem always abandons the previous countdown -- and clears only THAT gem's bar,
        // never every target's, so an eye-dwell fill in progress elsewhere is left alone.
        if (heldTarget != null && aimedAt != heldTarget)
        {
            ReleaseHeldTarget();
        }

        bool engaged = fillTrigger == FillTrigger.PointOnly || pressed;

        if (aimedAt == null || !engaged)
        {
            // Aiming away, or the button was released. Either abandon the countdown or let it coast,
            // depending on keepProgressWhenAimingAway; either way stop advancing it this frame.
            if (!keepProgressWhenAimingAway) ReleaseHeldTarget();

            // The controller is not mid-interaction, so the eye is free to collect again. Only one modality
            // is ever live at a time; this is the handover back.
            tracker.SuspendEyeDwell(false);

            if (logMissedPresses && justPressed && aimedAt == null) Debug.Log($"[CONTROLLER] Press ignored: {missReason}");
            return;
        }

        if (Time.realtimeSinceStartup - lastCollectionTime < minSecondsBetweenCollections)
            return;

        heldTarget = aimedAt;

        // The participant is using the controller right now, so the eye stops collecting entirely -- not just
        // visually. Without this a gem they merely glanced at could complete its dwell while their attention
        // and intent were on the controller.
        tracker.SuspendEyeDwell(true);

        // Take the bar before drawing on it. The eye path blanks every unclaimed target on any frame its own
        // ray misses -- which is most frames while the participant is aiming with the controller -- so an
        // unclaimed controller bar would flicker rather than fill.
        tracker.ClaimFillOwnership(heldTarget);

        holdTimer += Time.deltaTime;
        tracker.SetTargetFillProgress(heldTarget, holdTimer / required);

        if (holdTimer >= required)
        {
            var collected = heldTarget;

            // Clear the countdown BEFORE collecting: CollectTarget destroys the GameObject, after which the
            // renderer reference is a destroyed Unity object and touching its material would throw.
            tracker.ReleaseFillOwnership(collected);
            heldTarget = null;
            holdTimer = 0f;

            // gazeStability is left at its -1 "not applicable" default: a controller collection has no dwell.
            if (tracker.CollectTarget(collected, EyeAndHeadTracker.CollectionMethodController))
            {
                lastCollectionTime = Time.realtimeSinceStartup;
                Debug.Log($"[CONTROLLER] Collected '{collected.gameObject.name}' by controller after " +
                          $"{required:F2}s.");
            }
        }
    }

    /// <summary>
    /// The registered target currently under the controller ray, or null with a reason for the log.
    /// </summary>
    private MeshRenderer ResolveAimedTarget(out string missReason)
    {
        missReason = null;

        if (ignorePressesOverUI && rayInteractor.IsOverUIGameObject())
        {
            missReason = "ray was over UI.";
            return null;
        }

        if (!rayInteractor.TryGetCurrent3DRaycastHit(out RaycastHit hit))
        {
            missReason = "ray hit nothing.";
            return null;
        }

        if (!tracker.TryResolveTarget(hit.transform, out MeshRenderer renderer))
        {
            missReason = $"'{hit.collider.name}' is not a live registered target.";
            return null;
        }

        return renderer;
    }

    /// <summary>Abandon the current countdown and put that one gem's bar back to empty.</summary>
    private void ReleaseHeldTarget()
    {
        if (heldTarget != null)
        {
            // Order matters: hand the bar back only after blanking it, or the eye path could redraw its own
            // progress onto this gem in the same frame and the abandoned countdown would appear to persist.
            tracker.ClearTargetFill(heldTarget);
            tracker.ReleaseFillOwnership(heldTarget);
        }

        heldTarget = null;
        holdTimer = 0f;
    }

    /// <summary>The no-countdown path: one press collects whatever is under the ray.</summary>
    private void TryInstantCollect()
    {
        if (Time.realtimeSinceStartup - lastCollectionTime < minSecondsBetweenCollections)
            return;

        MeshRenderer renderer = ResolveAimedTarget(out string missReason);
        if (renderer == null)
        {
            if (logMissedPresses) Debug.Log($"[CONTROLLER] Press ignored: {missReason}");
            return;
        }

        if (tracker.CollectTarget(renderer, EyeAndHeadTracker.CollectionMethodController))
        {
            lastCollectionTime = Time.realtimeSinceStartup;
            Debug.Log($"[CONTROLLER] Collected '{renderer.gameObject.name}' by controller select.");
        }
    }
}
