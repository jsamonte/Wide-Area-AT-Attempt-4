using System.Collections.Generic;
using UnityEngine;
using ARCockpit.Core;

namespace ARCockpit.DevSettings
{
    /// <summary>
    /// Drives the dev settings flow for one element. Sits on the element root next to its
    /// <see cref="MarkerAnchor"/>. It registers the element profile's tunables with the
    /// <see cref="DevSettingsStore"/>, then watches which tag placed the element
    /// (<see cref="MarkerAnchor.ActiveMarkerId"/>) and maps the tag's ones-digit to a <see cref="DevMode"/>:
    ///
    ///  - Window:    apply the saved overrides AND pop the live dev settings window.
    ///  - Overrides: apply the saved overrides, no window (a clean preview of a saved setup).
    ///  - Defaults:  restore the Unity-authored values (a hardcoded build), no window.
    ///
    /// On device the mode is decided by the printed tag. In the Editor (no marker subsystem) it uses
    /// <see cref="editorDefaultMode"/> so you can tune without a headset. Each element gets its own
    /// window instance so the Map and NavBall windows can coexist independently.
    /// </summary>
    public class DevModeController : MonoBehaviour
    {
        [Tooltip("The element's profile (MapProfile / NavBallProfile). Leave empty to use the one assigned " +
                 "to the sibling MarkerAnchor's Marker Profile slot.")]
        public ScriptableObject profile;
        [Tooltip("The element's MarkerAnchor. Leave empty to find one on this GameObject.")]
        public MarkerAnchor markerAnchor;
        [Tooltip("Mode used when no marker has placed the element yet (the Editor, or before a tag is " +
                 "seen). Window lets you tune without a device.")]
        public DevMode editorDefaultMode = DevMode.Window;
        [Tooltip("Offset applied to the dev settings window relative to this element. Use negative X for left, positive X for right.")]
        public Vector3 windowOffset = new Vector3(0f, -0.25f, 0.05f);

        IDevModeSource ModeSource => profile as IDevModeSource;
        IDevTunableSource TunableSource => profile as IDevTunableSource;

        // Each element gets its own window instance (not shared) so Map and NavBall can both have windows.
        DevSettingsWindow _window;
        // This element's own tunables: registered with the store AND handed to the window, so the window
        // shows only this element's knobs and the instances stay in sync with the store.
        List<DevTunable> _tunables;

        int _lastId = int.MinValue;
        DevMode _lastMode = (DevMode)(-1);
        bool _markerEverSeen;
        bool _wasTracking;

        void Awake()
        {
            if (markerAnchor == null) markerAnchor = GetComponent<MarkerAnchor>();
            if (profile == null && markerAnchor != null) profile = markerAnchor.markerProfile;

            // Collect THIS element's tunables once, before any override is applied (so authored defaults
            // snapshot cleanly), and register those exact instances. The window renders the same list, so
            // it shows only this element's knobs and its controls stay in sync with the store.
            _tunables = new List<DevTunable>();
            if (TunableSource != null) TunableSource.CollectTunables(_tunables);
            // Also collect component tunable sources on this element (e.g. CockpitElement's saved cockpit-
            // relative position), so they appear in this element's window and ride its Export / Save / Load
            // alongside the profile's knobs. The profile is a ScriptableObject, so it is never double-counted.
            foreach (IDevTunableSource comp in GetComponents<IDevTunableSource>())
                comp.CollectTunables(_tunables);
            DevSettingsStore.Register(_tunables);
        }

        void Start()
        {
            // In the Editor (no marker subsystem), the marker will never fire, so apply the editor
            // default immediately so you can tune without a headset.
            // On device, wait until a marker is actually seen before doing anything.
            if (!HasMarkerSubsystem())
            {
                _markerEverSeen = true;   // pretend, so the editor default mode kicks in
                _lastMode = (DevMode)(-1);
                Apply(CurrentMode());
            }
        }

        void Update()
        {
            int id = markerAnchor != null ? markerAnchor.ActiveMarkerId : -1;
            bool tracking = markerAnchor != null && markerAnchor.Tracking;

            // Track whether a marker has ever been seen. Until one is, do nothing on device.
            if (id >= 0) _markerEverSeen = true;
            if (!_markerEverSeen) return;

            if (id != _lastId)
            {
                // The active tag changed: apply its mode (Window opens the window, Defaults restores).
                _lastId = id;
                Apply(CurrentMode());
            }
            else if (tracking && !_wasTracking && CurrentMode() == DevMode.Window)
            {
                // Re-acquiring the SAME window tag (a fresh sighting after it was pulled out of view)
                // reopens the window, so one tag toggles it: show to open, close with X, show again to
                // reopen. ActiveMarkerId holds the last id when the tag is gone, so id alone never changes;
                // the tracking rising edge is what tells us the tag came back.
                ShowWindow(true);
            }
            _wasTracking = tracking;
        }

        DevMode CurrentMode()
        {
            int id = markerAnchor != null ? markerAnchor.ActiveMarkerId : -1;
            if (ModeSource != null && id >= 0)
            {
                DevMode? m = ModeSource.FindDevMode(id);
                if (m.HasValue) return m.Value;
            }
            return editorDefaultMode;
        }

        void Apply(DevMode mode)
        {
            if (mode == _lastMode) return;
            _lastMode = mode;

            string elementName = gameObject.name;
            string idStr = markerAnchor != null ? markerAnchor.ActiveMarkerId.ToString() : "none";
            Debug.Log($"[DevModeController] {elementName} entering {mode} mode (Marker ID: {idStr})");

            switch (mode)
            {
                case DevMode.Defaults:
                    DevSettingsStore.RestoreDefaults();
                    ShowWindow(false);
                    break;
                case DevMode.Overrides:
                    DevSettingsStore.ApplyOverrides();
                    ShowWindow(false);
                    break;
                case DevMode.Window:
                    DevSettingsStore.ApplyOverrides();
                    ShowWindow(true);
                    break;
            }
        }

        void ShowWindow(bool show)
        {
            if (!show)
            {
                if (_window != null) _window.gameObject.SetActive(false);
                return;
            }

            if (_window == null)
            {
                var go = new GameObject($"DevSettingsWindow_{gameObject.name}");
                _window = go.AddComponent<DevSettingsWindow>();
                _window.Initialize(_tunables);   // build the panel from this element's knobs only
            }
            _window.gameObject.SetActive(true);
            // Float the panel at the specified offset relative to this element so it is reachable by the ray.
            _window.PlaceNear(transform, windowOffset);
        }

        /// <summary>True when the ML marker subsystem is available (i.e. on device, not in Editor).</summary>
        static bool HasMarkerSubsystem()
        {
            var settings = UnityEngine.XR.OpenXR.OpenXRSettings.Instance;
            if (settings == null) return false;
            var feature = settings.GetFeature<MagicLeap.OpenXR.Features.MarkerUnderstanding.MagicLeapMarkerUnderstandingFeature>();
            return feature != null && feature.enabled;
        }
    }
}
