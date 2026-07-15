using System;
using System.Globalization;
using UnityEngine;

namespace ARCockpit.DevSettings
{
    /// <summary>
    /// One tunable knob exposed in the dev settings window. It does NOT store a value: its get/set
    /// delegates point straight at the single profile field, so the value still lives in exactly one
    /// place (the MapProfile / NavBallProfile asset). This descriptor only declares WHICH field is
    /// tunable plus the UI metadata (label, range, kind). Profiles hand these out via
    /// <see cref="IDevTunableSource.CollectTunables"/>; <see cref="DevSettingsStore"/> reads/writes
    /// through them; <see cref="DevSettingsWindow"/> renders a control from the kind.
    /// </summary>
    public sealed class DevTunable
    {
        public enum Kind { Float, Int, Bool, Color }

        public string Key;     // stable serialization key, e.g. "navball.radius"
        public string Label;   // shown in the window
        public Kind Type;
        public float Min, Max; // slider range for Float / Int
        public string Help;    // plain-language explanation shown by the row's "?" button
        public string Section; // optional group heading for the dev window; null -> auto (Colors / General)

        /// <summary>Fluent section assignment so a profile can group its knobs under a collapsible heading
        /// in the dev window: <c>into.Add(DevTunable.F(...).In("Colors"))</c>. The value still lives in the
        /// one field; this only tags the UI grouping. Returns this for chaining.</summary>
        public DevTunable In(string section) { Section = section; return this; }

        public Func<float> GetFloat; public Action<float> SetFloat;
        public Func<int> GetInt; public Action<int> SetInt;
        public Func<bool> GetBool; public Action<bool> SetBool;
        public Func<Color> GetColor; public Action<Color> SetColor;

        /// <summary>A fine-tune step for the +/- buttons: 1/100 of the range for floats, at least a tiny
        /// amount, and 1 for ints.</summary>
        public float Step => Type == Kind.Int ? 1f : Mathf.Max(0.0001f, (Max - Min) / 100f);

        public static DevTunable F(string key, string label, float min, float max, Func<float> get, Action<float> set, string help = null)
            => new DevTunable { Key = key, Label = label, Type = Kind.Float, Min = min, Max = max, GetFloat = get, SetFloat = set, Help = help };

        public static DevTunable I(string key, string label, int min, int max, Func<int> get, Action<int> set, string help = null)
            => new DevTunable { Key = key, Label = label, Type = Kind.Int, Min = min, Max = max, GetInt = get, SetInt = set, Help = help };

        public static DevTunable B(string key, string label, Func<bool> get, Action<bool> set, string help = null)
            => new DevTunable { Key = key, Label = label, Type = Kind.Bool, GetBool = get, SetBool = set, Help = help };

        public static DevTunable C(string key, string label, Func<Color> get, Action<Color> set, string help = null)
            => new DevTunable { Key = key, Label = label, Type = Kind.Color, GetColor = get, SetColor = set, Help = help };

        /// <summary>The current profile value as a culture-invariant string (for JSON / export).</summary>
        public string Serialize()
        {
            switch (Type)
            {
                case Kind.Float: return GetFloat().ToString("R", CultureInfo.InvariantCulture);
                case Kind.Int: return GetInt().ToString(CultureInfo.InvariantCulture);
                case Kind.Bool: return GetBool() ? "1" : "0";
                case Kind.Color:
                    Color c = GetColor();
                    return string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R}", c.r, c.g, c.b, c.a);
                default: return string.Empty;
            }
        }

        /// <summary>Write <paramref name="s"/> (as produced by <see cref="Serialize"/>) into the profile field.</summary>
        public void Deserialize(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            switch (Type)
            {
                case Kind.Float:
                    if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) SetFloat(f);
                    break;
                case Kind.Int:
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) SetInt(i);
                    break;
                case Kind.Bool:
                    SetBool(s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase));
                    break;
                case Kind.Color:
                    string[] p = s.Split(',');
                    if (p.Length >= 3)
                    {
                        float r = ParseF(p[0]), g = ParseF(p[1]), b = ParseF(p[2]);
                        float a = p.Length >= 4 ? ParseF(p[3]) : 1f;
                        SetColor(new Color(r, g, b, a));
                    }
                    break;
            }
        }

        static float ParseF(string s)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }
}
