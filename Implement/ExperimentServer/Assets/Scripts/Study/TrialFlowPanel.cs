using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;
using ARCockpit.Core;
using ARCockpit.DevSettings;

namespace ARCockpit.Study
{
    /// <summary>
    /// The participant's start button, kept deliberately small.
    ///
    /// The trial is ARMED on the dashboard (the investigator picks which one, from the board). All the
    /// headset has to do is let the participant BEGIN it, and give them an escape hatch mid-flight. So this is
    /// a compact prompt low in the view, not a card in the face: the first version was a full panel and read
    /// as invasive, which for a person about to fly is exactly wrong.
    ///
    /// WHAT SHOWS, AND WHEN:
    ///   - Idle, a slot armed, the cockpit frame up: a small READY prompt low in view. One line of who/what,
    ///     one BEGIN button. The weather line only appears if the sim DISAGREES with the plan, so a normal
    ///     run is two lines and a button.
    ///   - Counting down: just the number.
    ///   - Recording: NOTHING. In the physical condition the participant must see no AR, and a floating chip
    ///     is AR. The investigator watches a running trial on the dashboard.
    ///   - Recording + the controller menu button HELD ~1s: a compact Pause / Finish / Redo panel. Held, not
    ///     tapped, so a fumbled thumb cannot kill an eight-minute run. Opening it is written into the CSV,
    ///     because in a no-AR trial this panel is AR the participant was not supposed to see.
    ///
    /// Head-locked, not cockpit-placed: it is the thing you reach for when the cockpit snap went wrong, so it
    /// must not be positioned BY that snap. Built as a root object and head-locked each frame (a world-space
    /// canvas parented to the head inherits the rig scale and renders huge, the FpsReadout trap).
    ///
    ///     adb logcat -d -s Unity | Select-String "\[FLOW\]"
    /// </summary>
    public class TrialFlowPanel : MonoBehaviour, IDevTunableSource
    {
        [Header("Placement")]
        [Tooltip("Where the prompt sits RELATIVE TO THE COCKPIT FRAME (241/240), in meters, once a frame " +
                 "exists. This is what makes it 'sit in front of them' in the cockpit rather than float with " +
                 "the head. The frame origin is the cockpit tag (the armrest), so this offset carries it from " +
                 "there to in front of the pilot: tune it on device. Ridable through Export / Save / Load.")]
        public Vector3 cockpitOffset = new Vector3(0f, 0.15f, 0.55f);

        [Tooltip("Meters in front of the head the prompt floats ONLY as a fallback, before any cockpit tag " +
                 "has been seen. Once the frame exists, cockpitOffset wins.")]
        public float distanceMeters = 0.7f;

        [Tooltip("Meters below eye level for the head-locked fallback.")]
        public float dropMeters = 0.34f;

        [Tooltip("Seconds the controller menu button must be HELD to open the in-trial panel. A tap does " +
                 "nothing: an eight-minute run must not be killable by a fumbled thumb.")]
        public float holdSeconds = 1f;

        [Tooltip("Countdown from BEGIN to the file opening.")]
        public int countdownSeconds = 3;

        const string Tag = "[FLOW]";

        static readonly Color PanelColor = new Color(0.06f, 0.07f, 0.09f, 0.92f);
        static readonly Color TextColor = new Color(0.90f, 0.94f, 1f, 1f);
        static readonly Color Dim = new Color(0.62f, 0.70f, 0.80f, 1f);
        static readonly Color Amber = new Color(1f, 0.78f, 0.25f, 1f);
        static readonly Color Go = new Color(0.13f, 0.55f, 0.25f, 1f);
        static readonly Color Warn = new Color(0.72f, 0.52f, 0.10f, 1f);
        static readonly Color Danger = new Color(0.55f, 0.18f, 0.18f, 1f);
        static readonly Color Neutral = new Color(0.16f, 0.20f, 0.28f, 1f);

        TrialController _trial;
        Canvas _canvas;
        RectTransform _root;
        TMP_Text _title, _big, _note;
        RectTransform _buttonRow;
        readonly List<GameObject> _buttons = new List<GameObject>();

        InputAction _menu;
        float _heldFor;
        bool _inTrialPanelOpen;
        bool _counting;
        bool _readyShown;   // is the BEGIN button currently built? (so DrawReady does not rebuild it every frame)
        bool _readyOk;      // the weather-ok state the current BEGIN button was built for

        void Awake()
        {
            _trial = FindObjectOfType<TrialController>();
            BuildCanvas();

            // Register the placement offset so it exports / saves / loads with everything else. Own tunable
            // list, applied from any saved overlay, same as CockpitElement does for its saved pose.
            var mine = new List<DevTunable>();
            CollectTunables(mine);
            DevSettingsStore.Register(mine);
            DevSettingsStore.ApplyOverridesTo(mine);

            // The ML2 controller's menu button, several candidate paths because the one that resolves depends
            // on the interaction profile, plus a keyboard fallback so the flow is testable in the Editor.
            // Watch the logged count: 0 means none matched and the in-trial panel is unreachable on device.
            _menu = new InputAction("TrialMenu", InputActionType.Button);
            _menu.AddBinding("<XRController>/menuButton");
            _menu.AddBinding("<XRController>/menu");
            _menu.AddBinding("<Keyboard>/m");
            _menu.Enable();

            Debug.Log($"{Tag} Menu-hold bound to {_menu.controls.Count} control(s)." +
                      (_menu.controls.Count == 0
                          ? " ZERO: the in-trial panel cannot open on device. Check the Magic Leap 2 Controller "
                            + "Interaction Profile under OpenXR > Android."
                          : ""));
        }

        void OnDestroy() => _menu?.Disable();

        void Update()
        {
            if (_counting) return;

            if (TrialData.State != TrialState.Idle)
            {
                // Recording. Show nothing unless the menu button is held to summon the in-trial panel.
                if (_menu != null && _menu.IsPressed()) _heldFor += Time.unscaledDeltaTime;
                else _heldFor = 0f;

                if (!_inTrialPanelOpen && _heldFor >= holdSeconds) OpenInTrialPanel();
                Show(_inTrialPanelOpen);
                return;
            }

            _heldFor = 0f;
            _inTrialPanelOpen = false;
            DrawReady();
        }

        // ---- Ready prompt (Idle) ---------------------------------------------------------------------

        void DrawReady()
        {
            SessionPlan plan = SessionPlan.Active;
            TrialSlot slot = plan?.Armed;

            // ONLY under 241 (Deploy), never 240 (Placement). 240 is setup: you are positioning elements with
            // their own dev tags and the windows are open, so a BEGIN prompt there is wrong. 241 is the trial:
            // the elements are deployed at their saved poses, the dev windows are gone, and the participant is
            // meant to start. HasFrame is true for ANY cockpit tag (240 or 241), so gating on it popped the
            // prompt during placement; the mode is the real gate.
            bool deployed = CockpitAnchor.Active != null && CockpitAnchor.Active.Mode == CockpitMode.Deploy;

            if (plan == null || slot == null || !deployed) { _readyShown = false; Show(false); return; }

            Show(true);

            _title.text = slot.practice
                ? $"{plan.participant}   ·   PRACTICE"
                : $"{plan.participant}   ·   trial {slot.number} of {plan.RecordedCount}";

            _big.text = slot.Label;
            _big.color = TextColor;

            // Weather line only when it DISAGREES. A run in the right weather says nothing, which is what keeps
            // the prompt to two lines and a button.
            TrialWeatherCheck.Verdict v = TrialWeatherCheck.Check(slot, out string why);
            bool ok = v == TrialWeatherCheck.Verdict.Matches;
            _note.text = ok ? "" : why;

            // Build the BEGIN button ONCE, not every frame. Rebuilding it each frame (this method runs every
            // frame while Idle) DESTROYED and recreated the very GameObject the controller ray was pressing, so
            // the press never survived to a release and the click never fired: the ray hit it (red) and nothing
            // happened. Rebuild only when the weather-ok state flips, which changes the button color.
            if (!_readyShown || _readyOk != ok)
            {
                _readyShown = true;
                _readyOk = ok;
                SetButtons(("B E G I N", ok ? Go : Warn, Begin));
            }
        }

        void Begin()
        {
            if (_counting) return;
            StartCoroutine(Countdown());
        }

        IEnumerator Countdown()
        {
            _counting = true;
            _readyShown = false;   // the BEGIN button is being torn down; rebuild it if we return to Idle
            _title.text = "";
            _note.text = "hands on the controls";
            SetButtons();   // nothing to misclick during the count

            for (int i = countdownSeconds; i > 0; i--)
            {
                _big.text = i.ToString();
                yield return new WaitForSeconds(1f);
            }

            Show(false);
            _counting = false;

            // TrialController owns every guard (participant, armed slot, calibration, file open). This panel
            // re-checks none of them: two copies of a guard drift apart and one starts letting a bad trial
            // through. A refusal lands on the dashboard and the log, and the ready prompt is still here.
            _trial?.StartTrial();
        }

        // ---- In-trial panel (held menu button) -------------------------------------------------------

        void OpenInTrialPanel()
        {
            _inTrialPanelOpen = true;
            _heldFor = 0f;
            _readyShown = false;   // switching to the in-trial buttons; DrawReady must rebuild BEGIN afterward

            // In a no-AR trial this panel IS AR, shown to a participant supposed to see none. Put it in the
            // file: weeks later at analysis nobody remembers it came up at minute six.
            _trial?.MarkEvent("PANEL_OPEN");
            Debug.Log($"{Tag} In-trial panel opened (marked into the CSV).");

            bool paused = TrialData.State == TrialState.Paused;
            _title.text = paused ? "PAUSED" : "RECORDING";
            _big.text = $"{TrialData.ElapsedSeconds:0} s   ·   {TrialData.RowsWritten} rows";
            _big.color = TextColor;
            _note.text = "";

            SetButtons(
                (paused ? "Resume" : "Pause", Neutral, () =>
                {
                    if (paused) _trial?.ResumeTrial(); else _trial?.PauseTrial();
                    CloseInTrialPanel();
                }),
                ("Finish", Go, () => Confirm("Finish and keep this trial?", () =>
                {
                    _trial?.StopTrial(true);
                    CloseInTrialPanel();
                })),
                ("Redo", Danger, () => Confirm("Throw this run away and fly it again?", () =>
                {
                    _trial?.StopTrial(false, "redo from the headset panel");
                    CloseInTrialPanel();
                })),
                ("Back", Neutral, CloseInTrialPanel));
        }

        void CloseInTrialPanel()
        {
            _inTrialPanelOpen = false;
            _heldFor = 0f;
            Show(false);
        }

        void Confirm(string question, System.Action yes)
        {
            _big.text = "";
            _note.text = question;
            SetButtons(
                ("Yes", Danger, () => yes()),
                ("Cancel", Neutral, OpenInTrialPanel));
        }

        // ---- Canvas ----------------------------------------------------------------------------------

        void LateUpdate()
        {
            if (_canvas == null || !_canvas.gameObject.activeSelf) return;
            Transform cam = Camera.main != null ? Camera.main.transform : null;
            if (cam == null) return;

            CockpitAnchor cockpit = CockpitAnchor.Active;
            if (cockpit != null && cockpit.HasFrame)
            {
                // World-placed in the cockpit: a fixed spot the participant can look toward, not one that
                // rides their eyes. Position is locked to the frame; only the facing tracks the head, so it
                // stays readable without wandering. The head-lock fallback below is for before the frame
                // exists (nothing is placed then anyway, so BEGIN is not offered until it does).
                _root.position = cockpit.Frame.TransformPoint(cockpitOffset);
            }
            else
            {
                _root.position = cam.position + cam.forward * distanceMeters - cam.up * dropMeters;
            }

            _root.rotation = Quaternion.LookRotation(_root.position - cam.position, cam.up);
        }

        // The placement offset is exposed as dev tunables so it rides Export / Save / Load and is recorded,
        // like every cockpit position. NOTE: it is not yet on a dev WINDOW, so on-device nudging is not wired
        // (placement is still being defined, and may re-anchor to the sim-room digital twin instead of the
        // cockpit tag). Until then it is set here and exported, not tuned live: that is the honest state.
        public void CollectTunables(List<DevTunable> into)
        {
            into.Add(DevTunable.F("trialpanel.offX", "Panel X (m)", -2f, 2f,
                () => cockpitOffset.x, v => cockpitOffset.x = v,
                "Left/right offset of the BEGIN prompt from the cockpit frame.").In("Trial panel"));
            into.Add(DevTunable.F("trialpanel.offY", "Panel Y (m)", -2f, 2f,
                () => cockpitOffset.y, v => cockpitOffset.y = v,
                "Up/down offset of the BEGIN prompt from the cockpit frame.").In("Trial panel"));
            into.Add(DevTunable.F("trialpanel.offZ", "Panel Z (m)", -2f, 2f,
                () => cockpitOffset.z, v => cockpitOffset.z = v,
                "Toward/away offset of the BEGIN prompt from the cockpit frame.").In("Trial panel"));
        }

        void BuildCanvas()
        {
            // Small on purpose: about 24 cm wide, sat well down in the view.
            const float pixelsWide = 520f, pixelsTall = 250f, metersWide = 0.24f;

            var go = new GameObject("TrialFlowPrompt", typeof(RectTransform), typeof(Canvas));
            _root = (RectTransform)go.transform;
            _canvas = go.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.worldCamera = Camera.main;
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
            go.AddComponent<GraphicRaycaster>();
            go.AddComponent<TrackedDeviceGraphicRaycaster>();

            _root.sizeDelta = new Vector2(pixelsWide, pixelsTall);
            float s = metersWide / pixelsWide;
            _root.localScale = new Vector3(s, s, s);

            var bg = NewImage(_root, "Panel", PanelColor);
            Stretch(bg.rectTransform);

            _title = NewText(_root, "Title", "", 22f, FontStyles.Normal, TextAlignmentOptions.Center);
            _title.color = Dim;
            Anchor(_title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -14f), new Vector2(-16f, -48f));

            _big = NewText(_root, "Big", "", 40f, FontStyles.Bold, TextAlignmentOptions.Center);
            Anchor(_big.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -52f), new Vector2(-16f, -112f));

            _note = NewText(_root, "Note", "", 20f, FontStyles.Italic, TextAlignmentOptions.Center);
            _note.color = Amber;
            Anchor(_note.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -114f), new Vector2(-16f, -168f));

            var row = new GameObject("Buttons", typeof(RectTransform));
            _buttonRow = (RectTransform)row.transform;
            _buttonRow.SetParent(_root, false);
            Anchor(_buttonRow, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(14f, 14f), new Vector2(-14f, 74f));
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;

            EnsureEventSystem();
            go.SetActive(false);
        }

        void Show(bool on)
        {
            if (_canvas == null) return;

            // Camera.main is null at Awake (the XR rig has not spawned its camera yet), so worldCamera set in
            // BuildCanvas came up null and the world-space GraphicRaycaster never hit-tested the controller ray:
            // the buttons looked dead. Bind it the first time the panel is actually shown, by which point the
            // rig camera exists. This is what the dev windows get for free by building their canvas late.
            if (on && _canvas.worldCamera == null && Camera.main != null) _canvas.worldCamera = Camera.main;

            if (_canvas.gameObject.activeSelf != on) _canvas.gameObject.SetActive(on);
        }

        void SetButtons(params (string label, Color color, System.Action onClick)[] defs)
        {
            foreach (GameObject b in _buttons) Destroy(b);
            _buttons.Clear();
            if (defs == null) return;

            foreach (var d in defs)
            {
                var go = new GameObject(d.label, typeof(RectTransform), typeof(Image), typeof(Button));
                go.transform.SetParent(_buttonRow, false);
                go.GetComponent<Image>().color = d.color;

                System.Action click = d.onClick;
                go.GetComponent<Button>().onClick.AddListener(() => click?.Invoke());

                var t = NewText((RectTransform)go.transform, "Label", d.label, 24f, FontStyles.Bold, TextAlignmentOptions.Center);
                t.raycastTarget = false;
                Stretch(t.rectTransform);

                _buttons.Add(go);
            }
        }

        // ---- uGUI helpers (local, so the panel does not depend on the dev window's private layout) ----

        static Image NewImage(RectTransform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            return img;
        }

        static TMP_Text NewText(RectTransform parent, string name, string text, float size, FontStyles style,
                                TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = align;
            t.color = TextColor;
            t.enableWordWrapping = true;
            return t;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static void Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }

        // Match DevSettingsWindow exactly: the controller ray needs an XRUIInputModule. A scene EventSystem
        // that carries a StandaloneInputModule instead (the uGUI default) will NOT drive the tracked-device
        // ray, so the buttons look dead. Swap the module rather than just checking for an EventSystem's
        // existence, which is the naive version that let a wrong module sit there.
        static void EnsureEventSystem()
        {
            var es = FindObjectOfType<EventSystem>();
            if (es == null)
            {
                var go = new GameObject("EventSystem");
                go.AddComponent<EventSystem>();
                go.AddComponent<XRUIInputModule>();
                return;
            }
            if (es.GetComponent<XRUIInputModule>() != null) return;
            var existing = es.GetComponent<BaseInputModule>();
            if (existing != null) Destroy(existing);
            es.gameObject.AddComponent<XRUIInputModule>();
        }
    }
}
