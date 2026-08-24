using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// Lets participants collect gems with the controller, using exactly the same point-and-press gesture that
/// selects a button in the menu.
///
/// It deliberately reuses the menu's own machinery rather than reading the trigger directly: the ray comes
/// from the scene's <see cref="XRRayInteractor"/> (the one already drawing the visible controller line) and
/// the button comes from the <see cref="ActionBasedController"/>'s Select action. In Application.unity the
/// controller's Select, Activate and UI Press actions are all bound to the same action, so "the button that
/// clicks a menu item" and "the button that collects a gem" are the same physical press by construction --
/// not two bindings that have to be kept in sync by hand.
///
/// Collection itself is delegated to <see cref="EyeAndHeadTracker.CollectTarget"/>, the single path both
/// modalities share, so a controller collection is logged, autosaved and counted identically to an eye-dwell
/// one and still fires the end-of-trial handshake with TrialManager.
/// </summary>
public class ControllerGemCollector : MonoBehaviour
{
    [Header("Wiring (left empty, these are found in the scene at Start)")]
    [Tooltip("The tracker that owns the target list and the destruction log. Found automatically if unset.")]
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

    [Tooltip("Ignore presses that land while the ray is over a UI element, so clicking a menu button can " +
             "never also collect a gem sitting behind it.")]
    [SerializeField] private bool ignorePressesOverUI = true;

    [Tooltip("Minimum seconds between two controller collections. Guards against one physical press being " +
             "read as several, and against a participant sweeping the ray across a cluster while held down.")]
    [SerializeField] [Range(0f, 2f)] private float minSecondsBetweenCollections = 0.25f;

    [Tooltip("Log every press that did not collect anything, with the reason. Useful while piloting; noisy " +
             "in a real session.")]
    [SerializeField] private bool logMissedPresses = false;

    private float lastCollectionTime = -999f;
    private bool wasPressed;

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
    }

    private void Update()
    {
        if (!enableControllerCollection) return;
        if (tracker == null || controller == null || rayInteractor == null) return;

        var action = controller.selectAction.action;
        if (action == null) return;

        // Rising edge only. Holding the trigger down must not collect gem after gem as the ray sweeps across
        // them -- one press, one gem, the same contract as a menu button.
        bool pressed = action.IsPressed();
        bool justPressed = pressed && !wasPressed;
        wasPressed = pressed;

        if (!justPressed) return;

        if (Time.realtimeSinceStartup - lastCollectionTime < minSecondsBetweenCollections)
            return;

        if (ignorePressesOverUI && rayInteractor.IsOverUIGameObject())
        {
            if (logMissedPresses) Debug.Log("[CONTROLLER] Press ignored: ray was over UI.");
            return;
        }

        if (!rayInteractor.TryGetCurrent3DRaycastHit(out RaycastHit hit))
        {
            if (logMissedPresses) Debug.Log("[CONTROLLER] Press ignored: ray hit nothing.");
            return;
        }

        if (!tracker.TryResolveTarget(hit.transform, out MeshRenderer renderer))
        {
            if (logMissedPresses)
                Debug.Log($"[CONTROLLER] Press ignored: '{hit.collider.name}' is not a live registered target.");
            return;
        }

        // gazeStability is left at its -1 "not applicable" default: a controller collection has no dwell.
        if (tracker.CollectTarget(renderer, EyeAndHeadTracker.CollectionMethodController))
        {
            lastCollectionTime = Time.realtimeSinceStartup;
            Debug.Log($"[CONTROLLER] Collected '{renderer.gameObject.name}' by controller select.");
        }
    }
}
