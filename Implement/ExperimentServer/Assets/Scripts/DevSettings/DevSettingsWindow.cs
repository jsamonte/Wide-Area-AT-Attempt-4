using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;

namespace ARCockpit.DevSettings
{
    /// <summary>
    /// The in-app dev settings panel: a world-space uGUI canvas built in code (no prefab) from ONE
    /// element's tunables (map OR NavBall), handed in by <see cref="DevModeController"/>. Each row edits a
    /// profile field live; every control also has +/- fine-tune buttons (precise nudging is hard with the
    /// controller ray) and a "?" that shows a plain-language explanation in the footer. The footer also
    /// carries Export (writes this element's own auto-incrementing file and shows the name), Save (persist
    /// the overlay across launches), and Reset (with a Yes/Cancel confirm). Operated on the ML2 by the
    /// controller ray. The panel rides its anchor every frame so it follows the marker-placed element.
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    public class DevSettingsWindow : MonoBehaviour
    {
        const float MetersWide = 0.36f;
        const float PixelsWide = 640f;
        const float PixelsTall = 900f;
        static readonly Color PanelColor = new Color(0.06f, 0.07f, 0.09f, 0.94f);
        static readonly Color RowColor = new Color(1f, 1f, 1f, 0.04f);
        static readonly Color Accent = new Color(0.30f, 0.65f, 1f, 1f);
        static readonly Color BtnColor = new Color(0.16f, 0.20f, 0.28f, 1f);
        static readonly Color TextColor = new Color(0.90f, 0.94f, 1f, 1f);

        RectTransform _content;
        readonly List<System.Action> _refreshers = new List<System.Action>();
        List<DevTunable> _tunables;
        string _element = "element";
        bool _built;

        TMP_Text _info;
        GameObject _buttonBar;
        GameObject _confirmBar;
        TMP_Text _slotText;
        int _slot = 1;

        Transform _followTarget;
        Vector3 _followOffsetWorld;

        [Tooltip("Open the window with every section collapsed, so you see a short list of headers instead " +
                 "of scrolling past every knob in the profile to reach the one you want. Tap a header to " +
                 "open that section.")]
        public bool startSectionsCollapsed = true;

        void Awake() => EnsureEventSystem();

        void LateUpdate()
        {
            if (_followTarget == null) return;
            transform.position = _followTarget.position + _followOffsetWorld;
            Transform cam = Camera.main != null ? Camera.main.transform : null;
            if (cam != null) transform.rotation = Quaternion.LookRotation(transform.position - cam.position);
        }

        /// <summary>Build the panel for one element's tunables. Called once by DevModeController.</summary>
        public void Initialize(List<DevTunable> tunables)
        {
            if (_built) return;
            _built = true;
            _tunables = tunables;
            if (_tunables != null && _tunables.Count > 0)
                _element = _tunables[0].Key.Split('.')[0];   // "map" / "navball" from the key prefix
            BuildCanvas();
            BuildHeader();
            BuildFooter();
            BuildScroll();
            BuildRows();
        }

        public void PlaceNear(Transform anchor, Vector3 offset)
        {
            if (anchor == null) return;
            _followTarget = anchor;
            Transform cam = Camera.main != null ? Camera.main.transform : null;
            Vector3 right = cam != null ? cam.right : anchor.right;
            Vector3 up = cam != null ? cam.up : anchor.up;
            Vector3 fwd = cam != null ? cam.forward : anchor.forward;
            _followOffsetWorld = right * offset.x + up * offset.y + fwd * offset.z;
            LateUpdate();
        }

        // --- build ---

        void BuildCanvas()
        {
            var canvas = GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            gameObject.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
            gameObject.AddComponent<GraphicRaycaster>();
            gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();

            var rt = (RectTransform)transform;
            rt.sizeDelta = new Vector2(PixelsWide, PixelsTall);
            float scale = MetersWide / PixelsWide;
            rt.localScale = new Vector3(scale, scale, scale);

            Stretch(NewImage(rt, "Panel", PanelColor).rectTransform);
        }

        void BuildHeader()
        {
            var rt = (RectTransform)transform;
            // A draggable title bar: grab it with the ray to move the panel. It keeps riding the element
            // afterward, just at the new offset.
            var bar = NewImage(rt, "TitleBar", new Color(0.12f, 0.16f, 0.22f, 1f));
            Anchor(bar.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(6f, -8f), new Vector2(-6f, -50f));
            bar.raycastTarget = true;
            bar.gameObject.AddComponent<PanelDragHandle>().window = this;

            var title = NewText(bar.rectTransform, "Title", _element.ToUpper() + " SETTINGS   (drag to move)", 21f, FontStyles.Bold, TextAlignmentOptions.Left);
            title.raycastTarget = false;   // let the bar receive the drag, not the text
            Anchor(title.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(14f, 0f), new Vector2(-52f, 0f));

            // Close (X): hide this window. It reopens when the element's dev tag changes and re-selects Window
            // mode (e.g. show a different tag, then 230 again). Handy for the flight-plan window, which shares
            // the view with the map.
            var close = NewButton(bar.rectTransform, "X", new Color(0.5f, 0.18f, 0.18f, 1f), Close);
            Anchor((RectTransform)close.transform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-46f, 5f), new Vector2(-5f, -5f));
        }

        /// <summary>Hide the window (the X button). DevModeController re-shows it on the next Window-mode tag.</summary>
        public void Close() => gameObject.SetActive(false);

        // --- drag: reposition the panel along the pointing ray, then keep following the element ---

        float _dragDist;

        public void OnHandleBeginDrag(PointerEventData e)
        {
            if (TryGetRay(e, out Vector3 origin, out Vector3 dir))
                _dragDist = Mathf.Max(0.1f, Vector3.Dot(transform.position - origin, dir));
        }

        public void OnHandleDrag(PointerEventData e)
        {
            if (_followTarget == null) return;
            if (TryGetRay(e, out Vector3 origin, out Vector3 dir))
                _followOffsetWorld = (origin + dir * _dragDist) - _followTarget.position;   // LateUpdate applies it
        }

        // The pointing ray: the controller ray on device (TrackedDeviceEventData), or the mouse in the Editor.
        static bool TryGetRay(PointerEventData e, out Vector3 origin, out Vector3 dir)
        {
            if (e is TrackedDeviceEventData t && t.rayPoints != null && t.rayPoints.Count >= 2)
            {
                origin = t.rayPoints[0];
                dir = t.rayPoints[t.rayPoints.Count - 1] - origin;
                if (dir.sqrMagnitude < 1e-8f) dir = t.rayPoints[1] - origin;
                dir.Normalize();
                return true;
            }
            Camera cam = Camera.main;
            if (cam != null) { Ray r = cam.ScreenPointToRay(e.position); origin = r.origin; dir = r.direction; return true; }
            origin = Vector3.zero; dir = Vector3.forward; return false;
        }

        void BuildFooter()
        {
            var rt = (RectTransform)transform;

            // Info / help line (wraps, a couple of lines tall).
            _info = NewText(rt, "Info", "Tap ? on any row for an explanation.", 15f, FontStyles.Italic, TextAlignmentOptions.TopLeft);
            _info.color = new Color(0.75f, 0.85f, 0.95f, 1f);
            _info.enableWordWrapping = true;
            _info.overflowMode = TextOverflowModes.Truncate;
            Anchor(_info.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(18f, 102f), new Vector2(-18f, 164f));

            // Slot picker row: Load slot [<] N / max [>]  Load
            var slotRow = NewContainer(rt, "SlotRow");
            Anchor((RectTransform)slotRow.transform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(14f, 64f), new Vector2(-14f, 98f));
            var slotLabel = NewText(slotRow.transform, "SlotLabel", "Load slot", 16f, FontStyles.Normal, TextAlignmentOptions.Left);
            Anchor(slotLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0.28f, 1f), new Vector2(2f, 0f), new Vector2(-2f, 0f));
            var dec = NewButton(slotRow.transform, "<", BtnColor, () => SetSlot(_slot - 1));
            Anchor((RectTransform)dec.transform, new Vector2(0.28f, 0f), new Vector2(0.38f, 1f), Vector2.zero, Vector2.zero);
            _slotText = NewText(slotRow.transform, "SlotNum", "1", 18f, FontStyles.Bold, TextAlignmentOptions.Center);
            Anchor(_slotText.rectTransform, new Vector2(0.38f, 0f), new Vector2(0.54f, 1f), Vector2.zero, Vector2.zero);
            var inc = NewButton(slotRow.transform, ">", BtnColor, () => SetSlot(_slot + 1));
            Anchor((RectTransform)inc.transform, new Vector2(0.54f, 0f), new Vector2(0.64f, 1f), Vector2.zero, Vector2.zero);
            var load = NewButton(slotRow.transform, "Load", new Color(0.16f, 0.30f, 0.24f, 1f), DoLoad);
            Anchor((RectTransform)load.transform, new Vector2(0.68f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
            SetSlot(1);

            // Normal button bar: Export | Save | Reset.
            _buttonBar = NewContainer(rt, "ButtonBar");
            Anchor((RectTransform)_buttonBar.transform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(14f, 14f), new Vector2(-14f, 58f));
            string[] labels = { "Export", "Save", "Reset" };
            System.Action[] acts =
            {
                DoExport,
                () => { Debug.Log($"[DevWindow] {_element}: Save"); DevSettingsStore.Save(); SetInfo("Saved. These values will be restored next launch."); },
                ShowConfirm,
            };
            float bw = (PixelsWide - 28f - 20f) / 3f;
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var b = NewButton(_buttonBar.transform, labels[i], BtnColor, () => acts[idx]());
                float x = i * (bw + 10f);
                Anchor((RectTransform)b.transform, new Vector2(0f, 0f), new Vector2(0f, 1f),
                       new Vector2(x, 0f), new Vector2(x + bw, 0f));
            }

            // Confirm bar (hidden until Reset): question + Yes / Cancel.
            _confirmBar = NewContainer(rt, "ConfirmBar");
            Anchor((RectTransform)_confirmBar.transform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(14f, 14f), new Vector2(-14f, 58f));
            var q = NewText(_confirmBar.transform, "Q", "Reset " + _element + " to defaults?", 17f, FontStyles.Bold, TextAlignmentOptions.Left);
            Anchor(q.rectTransform, new Vector2(0f, 0f), new Vector2(0.55f, 1f), new Vector2(4f, 0f), new Vector2(-4f, 0f));
            var yes = NewButton(_confirmBar.transform, "Yes, reset", new Color(0.5f, 0.18f, 0.18f, 1f), () =>
            {
                Debug.Log($"[DevWindow] {_element}: Reset to defaults");
                DevSettingsStore.RestoreDefaults(_tunables);
                RefreshAll();
                SetInfo("Reset " + _element + " to Unity defaults.");
                HideConfirm();
            });
            Anchor((RectTransform)yes.transform, new Vector2(0.56f, 0f), new Vector2(0.78f, 1f), Vector2.zero, Vector2.zero);
            var cancel = NewButton(_confirmBar.transform, "Cancel", BtnColor, HideConfirm);
            Anchor((RectTransform)cancel.transform, new Vector2(0.79f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
            _confirmBar.SetActive(false);
        }

        void BuildScroll()
        {
            var rt = (RectTransform)transform;
            var viewportGo = NewImage(rt, "Viewport", new Color(0f, 0f, 0f, 0.001f));
            var viewport = viewportGo.rectTransform;
            Anchor(viewport, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(12f, 170f), new Vector2(-12f, -52f));
            viewportGo.gameObject.AddComponent<RectMask2D>();

            var scroll = viewportGo.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.viewport = viewport;
            scroll.scrollSensitivity = 24f;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            _content = (RectTransform)contentGo.transform;
            _content.SetParent(viewport, false);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.offsetMin = Vector2.zero;
            _content.offsetMax = Vector2.zero;

            var vlg = contentGo.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6f;
            vlg.padding = new RectOffset(6, 6, 6, 6);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            contentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = _content;
        }

        void BuildRows()
        {
            if (_tunables == null) return;

            // Group the knobs into collapsible sections so a growing list stays readable. A section starts
            // whenever the tunable's Section changes; each header toggles its own rows (independent accordion,
            // not single-open). Colors fall back to a "Colors" heading even when a profile sets no section, so
            // the wall of R/G/B sliders always gets a label.
            string current = null;
            List<GameObject> currentRows = null;
            var allRows = new List<GameObject>();
            foreach (DevTunable t in _tunables)
            {
                string sec = SectionOf(t);
                if (currentRows == null || sec != current)
                {
                    current = sec;
                    currentRows = new List<GameObject>();
                    BuildSectionHeader(sec, currentRows);
                }
                GameObject row = BuildRowFor(t);
                currentRows.Add(row);
                allRows.Add(row);
            }

            // Every section starts CLOSED. There are far too many knobs now for an all-expanded window to be
            // usable on the headset: you would scroll past a hundred rows to reach the one you want. Collapse
            // here rather than in BuildSectionHeader, because the rows do not exist yet when the header is
            // built (the header captures a list the caller fills afterward).
            if (startSectionsCollapsed)
                foreach (GameObject r in allRows)
                    if (r != null) r.SetActive(false);
        }

        static string SectionOf(DevTunable t)
        {
            if (!string.IsNullOrEmpty(t.Section)) return t.Section;
            return t.Type == DevTunable.Kind.Color ? "Colors" : "General";
        }

        GameObject BuildRowFor(DevTunable t)
        {
            switch (t.Type)
            {
                case DevTunable.Kind.Float: return BuildStepperRow(t, false);
                case DevTunable.Kind.Int: return BuildStepperRow(t, true);
                case DevTunable.Kind.Bool: return BuildBoolRow(t);
                case DevTunable.Kind.Color: return BuildColorRow(t);
                default: return null;
            }
        }

        // A clickable section title that collapses/expands the rows beneath it. Rows are added to
        // <paramref name="rows"/> by the caller as they are built, and the toggle reads that list at click
        // time, so it sees every row in the section. Starts collapsed (see startSectionsCollapsed).
        void BuildSectionHeader(string title, List<GameObject> rows)
        {
            var headerGo = NewImage(_content, "Section_" + title, new Color(0.12f, 0.16f, 0.22f, 1f));
            var le = headerGo.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 40f;
            le.preferredHeight = 40f;

            var caret = NewText(headerGo.rectTransform, "Caret", "-", 20f, FontStyles.Bold, TextAlignmentOptions.Center);
            caret.color = Accent;
            Anchor(caret.rectTransform, new Vector2(0f, 0f), new Vector2(0.10f, 1f), new Vector2(6f, 0f), new Vector2(0f, 0f));

            var label = NewText(headerGo.rectTransform, "SecLabel", title.ToUpper(), 17f, FontStyles.Bold, TextAlignmentOptions.Left);
            label.color = Accent;
            Anchor(label.rectTransform, new Vector2(0.10f, 0f), new Vector2(1f, 1f), new Vector2(2f, 0f), new Vector2(-8f, 0f));

            var btn = headerGo.gameObject.AddComponent<Button>();
            btn.targetGraphic = headerGo;
            bool expanded = !startSectionsCollapsed;
            caret.text = expanded ? "-" : "+";
            btn.onClick.AddListener(() =>
            {
                expanded = !expanded;
                caret.text = expanded ? "-" : "+";
                foreach (GameObject r in rows) if (r != null) r.SetActive(expanded);
            });
        }

        RectTransform NewRow(string name, float height)
        {
            var row = NewImage(_content, name, RowColor).rectTransform;
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            return row;
        }

        // label | [-] slider [+] | value | [?]
        GameObject BuildStepperRow(DevTunable t, bool asInt)
        {
            var row = NewRow(t.Key, 46f);

            var label = NewText(row, "Label", t.Label, 17f, FontStyles.Normal, TextAlignmentOptions.Left);
            Anchor(label.rectTransform, new Vector2(0f, 0f), new Vector2(0.30f, 1f), new Vector2(8f, 0f), new Vector2(-2f, 0f));

            var valueText = NewText(row, "Value", "", 17f, FontStyles.Normal, TextAlignmentOptions.Right);
            Anchor(valueText.rectTransform, new Vector2(0.74f, 0f), new Vector2(0.90f, 1f), Vector2.zero, Vector2.zero);

            Slider slider = NewSlider(row, t.Min, t.Max);
            slider.wholeNumbers = asInt;
            Anchor((RectTransform)slider.transform, new Vector2(0.37f, 0f), new Vector2(0.66f, 1f), new Vector2(4f, 12f), new Vector2(-4f, -12f));

            System.Action<float> apply = v =>
            {
                v = Mathf.Clamp(v, t.Min, t.Max);
                if (asInt) t.SetInt(Mathf.RoundToInt(v)); else t.SetFloat(v);
                slider.SetValueWithoutNotify(asInt ? Mathf.RoundToInt(v) : v);
                valueText.text = asInt ? Mathf.RoundToInt(v).ToString() : v.ToString("0.####");
                DevSettingsStore.RecordEdit(t);
            };

            var minus = NewButton(row, "-", BtnColor, () => apply((asInt ? t.GetInt() : t.GetFloat()) - t.Step));
            Anchor((RectTransform)minus.transform, new Vector2(0.30f, 0f), new Vector2(0.37f, 1f), new Vector2(0f, 8f), new Vector2(-2f, -8f));
            var plus = NewButton(row, "+", BtnColor, () => apply((asInt ? t.GetInt() : t.GetFloat()) + t.Step));
            Anchor((RectTransform)plus.transform, new Vector2(0.66f, 0f), new Vector2(0.73f, 1f), new Vector2(2f, 8f), new Vector2(0f, -8f));

            slider.onValueChanged.AddListener(v => apply(v));

            System.Action refresh = () =>
            {
                float v = asInt ? t.GetInt() : t.GetFloat();
                slider.SetValueWithoutNotify(v);
                valueText.text = asInt ? Mathf.RoundToInt(v).ToString() : v.ToString("0.####");
            };
            refresh();
            _refreshers.Add(refresh);

            AddHelp(row, t);
            return row.gameObject;
        }

        GameObject BuildBoolRow(DevTunable t)
        {
            var row = NewRow(t.Key, 46f);
            var label = NewText(row, "Label", t.Label, 17f, FontStyles.Normal, TextAlignmentOptions.Left);
            Anchor(label.rectTransform, new Vector2(0f, 0f), new Vector2(0.8f, 1f), new Vector2(8f, 0f), new Vector2(-4f, 0f));

            var box = NewImage(row, "Box", new Color(1f, 1f, 1f, 0.15f));
            Anchor(box.rectTransform, new Vector2(0.83f, 0.5f), new Vector2(0.83f, 0.5f), new Vector2(-14f, -14f), new Vector2(14f, 14f));
            var check = NewImage(box.rectTransform, "Check", Accent);
            Stretch(check.rectTransform, 5f);

            var toggle = box.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = box;
            toggle.graphic = check;
            System.Action refresh = () => toggle.SetIsOnWithoutNotify(t.GetBool());
            toggle.onValueChanged.AddListener(v => { t.SetBool(v); DevSettingsStore.RecordEdit(t); });
            refresh();
            _refreshers.Add(refresh);

            AddHelp(row, t);
            return row.gameObject;
        }

        // name + swatch on top; three R/G/B labeled sliders on the bottom.
        GameObject BuildColorRow(DevTunable t)
        {
            var row = NewRow(t.Key, 84f);

            var label = NewText(row, "Label", t.Label, 18f, FontStyles.Bold, TextAlignmentOptions.Left);
            Anchor(label.rectTransform, new Vector2(0f, 1f), new Vector2(0.66f, 1f), new Vector2(10f, -4f), new Vector2(-4f, -30f));

            var swatch = NewImage(row, "Swatch", t.GetColor());
            Anchor(swatch.rectTransform, new Vector2(0.70f, 1f), new Vector2(0.90f, 1f), new Vector2(0f, -6f), new Vector2(0f, -30f));

            Slider[] rgb = new Slider[3];
            for (int i = 0; i < 3; i++)
            {
                var s = NewSlider(row, 0f, 1f);
                rgb[i] = s;
                float frac = i / 3f, next = (i + 1) / 3f;
                var letter = NewText(row, "Ch" + i, "RGB"[i].ToString(), 13f, FontStyles.Bold, TextAlignmentOptions.Center);
                Anchor(letter.rectTransform, new Vector2(frac, 0f), new Vector2(next, 0f), new Vector2(8f, 30f), new Vector2(-6f, 50f));
                Anchor((RectTransform)s.transform, new Vector2(frac, 0f), new Vector2(next, 0f), new Vector2(8f, 8f), new Vector2(-6f, 30f));
                s.onValueChanged.AddListener(_ =>
                {
                    Color c = new Color(rgb[0].value, rgb[1].value, rgb[2].value, t.GetColor().a);
                    t.SetColor(c);
                    swatch.color = c;
                    DevSettingsStore.RecordEdit(t);
                });
            }
            System.Action refresh = () =>
            {
                Color c = t.GetColor();
                rgb[0].SetValueWithoutNotify(c.r);
                rgb[1].SetValueWithoutNotify(c.g);
                rgb[2].SetValueWithoutNotify(c.b);
                swatch.color = c;
            };
            refresh();
            _refreshers.Add(refresh);

            AddHelp(row, t);
            return row.gameObject;
        }

        // A small "?" button pinned top-right of a row that prints the tunable's help in the footer.
        void AddHelp(RectTransform row, DevTunable t)
        {
            string help = string.IsNullOrEmpty(t.Help) ? t.Label + " (no description)" : t.Label + ": " + t.Help;
            var b = NewButton(row, "?", new Color(0.22f, 0.30f, 0.42f, 1f), () => SetInfo(help));
            Anchor((RectTransform)b.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-30f, -30f), new Vector2(-4f, -4f));
        }

        void ShowConfirm() { if (_buttonBar) _buttonBar.SetActive(false); if (_confirmBar) _confirmBar.SetActive(true); }
        void HideConfirm() { if (_confirmBar) _confirmBar.SetActive(false); if (_buttonBar) _buttonBar.SetActive(true); }

        void DoExport()
        {
            string file = DevSettingsStore.ExportElement(_element, _tunables);
            Debug.Log($"[DevWindow] {_element}: Export -> {(file ?? "FAILED")}");
            SetInfo(file != null
                ? "Exported " + file + "  (in the app's persistentDataPath; adb pull it)."
                : "Export failed - see the log.");
            SetSlot(_slot);   // refresh the "of N" count now that a new slot exists
        }

        void DoLoad()
        {
            string file = DevSettingsStore.ImportElement(_element, _slot, _tunables);
            Debug.Log($"[DevWindow] {_element}: Load slot {_slot} -> {(file ?? "MISSING")}");
            if (file != null) { RefreshAll(); SetInfo("Loaded " + file + "."); }
            else SetInfo("No saved slot " + _slot + " for " + _element + ".");
        }

        // Pick a save slot to load, clamped to what actually exists on disk (shows "N / max").
        void SetSlot(int s)
        {
            int max = Mathf.Max(1, DevSettingsStore.LatestIndex(_element));
            _slot = Mathf.Clamp(s, 1, max);
            if (_slotText != null) _slotText.text = _slot + " / " + max;
        }

        void RefreshAll() { foreach (var r in _refreshers) r(); }
        void SetInfo(string s) { if (_info != null) _info.text = s; }

        // --- uGUI factory helpers ---

        static GameObject NewContainer(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        static Image NewImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        static TMP_Text NewText(Transform parent, string name, string text, float size, FontStyles style, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = align;
            t.color = TextColor;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        static Button NewButton(Transform parent, string label, Color bg, UnityEngine.Events.UnityAction onClick)
        {
            var img = NewImage(parent, "Btn_" + label, bg);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);
            var txt = NewText(img.rectTransform, "Text", label, 17f, FontStyles.Bold, TextAlignmentOptions.Center);
            Stretch(txt.rectTransform);
            return btn;
        }

        static Slider NewSlider(Transform parent, float min, float max)
        {
            var go = new GameObject("Slider", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var slider = go.AddComponent<Slider>();

            var bg = NewImage(go.transform, "Background", new Color(1f, 1f, 1f, 0.12f));
            bg.rectTransform.anchorMin = new Vector2(0f, 0.4f);
            bg.rectTransform.anchorMax = new Vector2(1f, 0.6f);
            bg.rectTransform.offsetMin = Vector2.zero;
            bg.rectTransform.offsetMax = Vector2.zero;

            var fillArea = new GameObject("Fill Area", typeof(RectTransform)).GetComponent<RectTransform>();
            fillArea.SetParent(go.transform, false);
            fillArea.anchorMin = new Vector2(0f, 0.4f);
            fillArea.anchorMax = new Vector2(1f, 0.6f);
            fillArea.offsetMin = Vector2.zero;
            fillArea.offsetMax = Vector2.zero;
            var fill = NewImage(fillArea, "Fill", Accent);
            fill.rectTransform.sizeDelta = new Vector2(10f, 0f);

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform)).GetComponent<RectTransform>();
            handleArea.SetParent(go.transform, false);
            Stretch(handleArea);
            var handle = NewImage(handleArea, "Handle", new Color(0.95f, 0.97f, 1f, 1f));
            handle.rectTransform.sizeDelta = new Vector2(20f, 0f);

            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            return slider;
        }

        static void Stretch(RectTransform rt, float padding = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padding, padding);
            rt.offsetMax = new Vector2(-padding, -padding);
        }

        static void Anchor(RectTransform rt, Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax)
        {
            rt.anchorMin = aMin;
            rt.anchorMax = aMax;
            rt.offsetMin = offMin;
            rt.offsetMax = offMax;
        }

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

    /// <summary>Forwards drag events on the title bar to its <see cref="DevSettingsWindow"/> so the panel
    /// can be grabbed and moved with the controller ray (or the mouse in the Editor).</summary>
    public class PanelDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public DevSettingsWindow window;
        public void OnBeginDrag(PointerEventData eventData) { if (window != null) window.OnHandleBeginDrag(eventData); }
        public void OnDrag(PointerEventData eventData) { if (window != null) window.OnHandleDrag(eventData); }
    }
}
