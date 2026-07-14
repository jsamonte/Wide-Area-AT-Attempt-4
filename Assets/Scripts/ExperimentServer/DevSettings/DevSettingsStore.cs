using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ARCockpit.DevSettings
{
    /// <summary>
    /// THE runtime store for dev settings, mirroring how the profiles are "one source of truth" for
    /// values: this is one source of truth for the OVERRIDES on top of them. Elements register their
    /// profile's tunables; the store
    ///
    ///  - snapshots the Unity-authored value of every tunable ONCE at registration (before any override
    ///    is applied), so "Defaults" mode can restore the hardcoded build even after overrides ran;
    ///  - loads a JSON overlay from persistentDataPath at first use and can apply it over the profiles;
    ///  - lets the window edit values live (each edit updates the profile field and the override dict);
    ///  - saves the overlay back, and exports a timestamped, human-readable copy to transcribe into the
    ///    Unity asset for a final build.
    ///
    /// Profiles keep their values; this never duplicates them. The override dict only holds the keys the
    /// user has actually changed. Editor and device both write to persistentDataPath (pull the export
    /// with `adb pull` on the ML2).
    /// </summary>
    public static class DevSettingsStore
    {
        const string OverlayFile = "dev_overrides.json";

        // Every registered tunable, de-duplicated by key. The window renders from this.
        public static readonly List<DevTunable> Tunables = new List<DevTunable>();

        // Authored (Unity-baked) value per key, snapshotted at registration before any override applies.
        static readonly Dictionary<string, string> _defaults = new Dictionary<string, string>();
        // The user's overrides (only keys they changed), loaded from / saved to the overlay file.
        static readonly Dictionary<string, string> _overrides = new Dictionary<string, string>();

        static bool _fileLoaded;

        /// <summary>Raised after any value changes (an edit, ApplyOverrides, or RestoreDefaults) so live
        /// elements can rebuild.</summary>
        public static event Action OnChanged;

        static string OverlayPath => Path.Combine(Application.persistentDataPath, OverlayFile);

        /// <summary>Register a profile's tunables. Safe to call repeatedly; keys are de-duplicated.
        /// Snapshots authored defaults on first sight of each key.</summary>
        public static void Register(IDevTunableSource source)
        {
            if (source == null) return;
            var collected = new List<DevTunable>();
            source.CollectTunables(collected);
            Register(collected);
        }

        /// <summary>Register a raw list of tunables (e.g. profile knobs plus a scene component's own,
        /// like a MarkerAnchor offset). De-duplicated by key; authored defaults snapshot on first sight.</summary>
        public static void Register(IEnumerable<DevTunable> tunables)
        {
            if (tunables == null) return;
            EnsureFileLoaded();
            foreach (DevTunable t in tunables)
            {
                if (t == null || string.IsNullOrEmpty(t.Key)) continue;
                if (_defaults.ContainsKey(t.Key)) continue;   // already registered
                _defaults[t.Key] = t.Serialize();              // authored value, captured before overrides
                Tunables.Add(t);
            }
        }

        /// <summary>Apply the saved overrides onto every registered tunable (Window / Overrides modes).</summary>
        public static void ApplyOverrides()
        {
            EnsureFileLoaded();
            foreach (DevTunable t in Tunables)
                if (_overrides.TryGetValue(t.Key, out string v))
                    t.Deserialize(v);
            OnChanged?.Invoke();
        }

        /// <summary>Apply the saved overrides onto just the given tunables (a subset), without touching the
        /// rest. Lets a component load its own persisted values on startup (e.g. a CockpitElement pulling in
        /// its saved cockpit position) regardless of any element's dev mode.</summary>
        public static void ApplyOverridesTo(IEnumerable<DevTunable> tunables)
        {
            if (tunables == null) return;
            EnsureFileLoaded();
            foreach (DevTunable t in tunables)
                if (t != null && _overrides.TryGetValue(t.Key, out string v))
                    t.Deserialize(v);
        }

        /// <summary>Restore the Unity-authored value of every registered tunable (Defaults mode).</summary>
        public static void RestoreDefaults()
        {
            foreach (DevTunable t in Tunables)
                if (_defaults.TryGetValue(t.Key, out string v))
                    t.Deserialize(v);
            OnChanged?.Invoke();
        }

        /// <summary>Restore only the given tunables to their authored defaults AND drop their overrides,
        /// so a per-element Reset does not touch the other element and persists if saved.</summary>
        public static void RestoreDefaults(IEnumerable<DevTunable> subset)
        {
            if (subset == null) return;
            int count = 0;
            foreach (DevTunable t in subset)
                if (t != null && _defaults.TryGetValue(t.Key, out string v))
                {
                    t.Deserialize(v);
                    _overrides.Remove(t.Key);
                    count++;
                }
            Debug.Log($"[DevSettings] Reset {count} tunable(s) to Unity defaults.");
            OnChanged?.Invoke();
        }

        /// <summary>Push a window edit through: write the tunable's current value into the override dict
        /// and notify listeners. The window sets the profile field first, then calls this.</summary>
        public static void RecordEdit(DevTunable t)
        {
            if (t == null || string.IsNullOrEmpty(t.Key)) return;
            _overrides[t.Key] = t.Serialize();
            OnChanged?.Invoke();
        }

        /// <summary>Record an override WITHOUT firing OnChanged, for values that change continuously from
        /// something other than the window (e.g. a CockpitElement capturing a pose every frame). The field
        /// is already set by the caller; this just keeps the persisted overlay in sync so Save() carries it.</summary>
        public static void SetOverride(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            _overrides[key] = value;
        }

        /// <summary>Has this tunable been overridden from its authored default?</summary>
        public static bool IsOverridden(string key) => _overrides.ContainsKey(key);

        /// <summary>The Unity-authored value of a key (as snapshotted at registration, before any override),
        /// or null if the key was never registered. The web dashboard shows this next to the live value, so
        /// "what did the build ship with, and what has been tuned since?" is answerable without opening Unity.</summary>
        public static string DefaultOf(string key)
            => key != null && _defaults.TryGetValue(key, out string v) ? v : null;

        /// <summary>Drop one override (revert this knob to the authored default) and notify.</summary>
        public static void ClearOverride(DevTunable t)
        {
            if (t == null || !_overrides.Remove(t.Key)) return;
            if (_defaults.TryGetValue(t.Key, out string v)) t.Deserialize(v);
            OnChanged?.Invoke();
        }

        /// <summary>Persist the overlay so the tuned setup survives the next launch.</summary>
        public static void Save()
        {
            try
            {
                File.WriteAllText(OverlayPath, ToJson(_overrides));
                Debug.Log($"[DevSettings] Saved {_overrides.Count} override(s) to {OverlayPath}");
            }
            catch (Exception e) { Debug.LogError($"[DevSettings] Save failed: {e.Message}"); }
        }

        /// <summary>Export ONE element's current values to its own auto-incrementing file
        /// (<c>{element}_settings_N.txt</c>), so the map and NavBall are saved separately and nothing is
        /// overwritten. N is the next free index in persistentDataPath. Writes every one of the element's
        /// tunables (a complete snapshot to transcribe into the Unity asset), plus a matching .json.
        /// Returns the file NAME (not full path) for display, or null on failure.</summary>
        public static string ExportElement(string element, IEnumerable<DevTunable> tunables)
        {
            if (string.IsNullOrEmpty(element)) element = "element";
            string dir = Application.persistentDataPath;
            int n = NextIndex(dir, element);
            string txtPath = Path.Combine(dir, $"{element}_settings_{n}.txt");
            string jsonPath = Path.Combine(dir, $"{element}_settings_{n}.json");
            try
            {
                var overlay = new Dictionary<string, string>();
                var sb = new StringBuilder();
                sb.AppendLine($"# {element} settings {n} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("# current values; transcribe into the profile asset for a final build.");
                foreach (DevTunable t in tunables)
                {
                    if (t == null) continue;
                    string val = t.Serialize();
                    overlay[t.Key] = val;
                    sb.AppendLine($"{t.Key} = {val}");
                }
                File.WriteAllText(txtPath, sb.ToString());
                File.WriteAllText(jsonPath, ToJson(overlay));
                Debug.Log($"[DevSettings] Exported {element} to {txtPath}");
                return Path.GetFileName(txtPath);
            }
            catch (Exception e) { Debug.LogError($"[DevSettings] Export failed: {e.Message}"); return null; }
        }

        // Smallest N whose {element}_settings_N.txt does not yet exist, so repeated exports keep counting
        // up and never clobber an earlier one.
        static int NextIndex(string dir, string element)
        {
            int n = 1;
            while (File.Exists(Path.Combine(dir, $"{element}_settings_{n}.txt"))) n++;
            return n;
        }

        /// <summary>Highest saved slot N for an element (0 if none), so the window can bound its picker.</summary>
        public static int LatestIndex(string element)
        {
            string dir = Application.persistentDataPath;
            int n = 0;
            while (File.Exists(Path.Combine(dir, $"{element}_settings_{n + 1}.json"))) n++;
            return n;
        }

        /// <summary>Load a saved slot (<c>{element}_settings_N.json</c>) back onto this element's tunables
        /// and adopt them as the active overrides, so you can flip between saved setups on-device without
        /// touching Unity. Returns the file name loaded, or null if that slot does not exist.</summary>
        public static string ImportElement(string element, int index, IEnumerable<DevTunable> tunables)
        {
            string path = Path.Combine(Application.persistentDataPath, $"{element}_settings_{index}.json");
            if (!File.Exists(path)) return null;
            try
            {
                var dict = new Dictionary<string, string>();
                FromJson(File.ReadAllText(path), dict);
                foreach (DevTunable t in tunables)
                {
                    if (t != null && dict.TryGetValue(t.Key, out string v))
                    {
                        t.Deserialize(v);
                        _overrides[t.Key] = v;   // the loaded values become the live override set
                    }
                }
                OnChanged?.Invoke();
                Debug.Log($"[DevSettings] Loaded {path}");
                return Path.GetFileName(path);
            }
            catch (Exception e) { Debug.LogError($"[DevSettings] Load failed: {e.Message}"); return null; }
        }

        /// <summary>Write a timestamped JSON (reloadable) plus a flat 'key = value' .txt (eyeball /
        /// transcribe into the Unity asset). Returns the .txt path, or null on failure.</summary>
        public static string Export()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string jsonPath = Path.Combine(Application.persistentDataPath, $"dev_overrides_{stamp}.json");
            string txtPath = Path.Combine(Application.persistentDataPath, $"dev_overrides_{stamp}.txt");
            try
            {
                File.WriteAllText(jsonPath, ToJson(_overrides));

                var keys = new List<string>(_overrides.Keys);
                keys.Sort(StringComparer.Ordinal);
                var sb = new StringBuilder();
                sb.AppendLine($"# AR Cockpit dev settings export {stamp}");
                sb.AppendLine($"# {_overrides.Count} override(s). Transcribe these into the matching profile asset.");
                foreach (string k in keys) sb.AppendLine($"{k} = {_overrides[k]}");
                File.WriteAllText(txtPath, sb.ToString());

                Debug.Log($"[DevSettings] Exported to {jsonPath} and {txtPath}");
                return txtPath;
            }
            catch (Exception e) { Debug.LogError($"[DevSettings] Export failed: {e.Message}"); return null; }
        }

        static void EnsureFileLoaded()
        {
            if (_fileLoaded) return;
            _fileLoaded = true;
            try
            {
                if (!File.Exists(OverlayPath)) return;
                FromJson(File.ReadAllText(OverlayPath), _overrides);
                Debug.Log($"[DevSettings] Loaded {_overrides.Count} override(s) from {OverlayPath}");
            }
            catch (Exception e) { Debug.LogWarning($"[DevSettings] Load failed: {e.Message}"); }
        }

        // --- JSON (a list of {key,value}; JsonUtility cannot serialize a Dictionary directly) ---

        [Serializable] class Entry { public string key; public string value; }
        [Serializable] class FileModel { public List<Entry> entries = new List<Entry>(); }

        static string ToJson(Dictionary<string, string> dict)
        {
            var model = new FileModel();
            foreach (var kv in dict) model.entries.Add(new Entry { key = kv.Key, value = kv.Value });
            return JsonUtility.ToJson(model, true);
        }

        static void FromJson(string json, Dictionary<string, string> into)
        {
            into.Clear();
            FileModel model = JsonUtility.FromJson<FileModel>(json);
            if (model?.entries == null) return;
            foreach (Entry e in model.entries)
                if (!string.IsNullOrEmpty(e.key)) into[e.key] = e.value;
        }
    }
}
