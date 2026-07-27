using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reads the session files written by <see cref="EyeAndHeadTracker"/> and reports what
/// percentage of the trial the participant spent looking at each object: gems, recall
/// objects, and any other collider in the scene.
///
/// Two independent sources, either or both:
///   * gaze_session_summary_*.json    - uses the gazeAttention rollup the tracker computed.
///   * raw_eye_head_tracking_*.ndjson - recomputes the percentages from the per-frame stream,
///     which also works for recordings made before the summary rollup existed.
///
/// Run via  Tools > Gaze > Attention Analyzer.
/// </summary>
public class GazeAttentionAnalyzer : EditorWindow
{
    private const string FolderPrefKey = "GazeAttentionAnalyzer.Folder";

    private string folderPath = "";
    private bool useSummaryJson = true;
    private bool useGazeEvents = true;
    private bool useRawNdjson = true;
    private bool groupByName = true;
    private bool includeOtherCategory = true;
    private float minPercentToShow = 0f;

    private Vector2 scroll;
    private readonly List<FileResult> results = new List<FileResult>();
    private string status = "";

    private static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;

    [MenuItem("Tools/Gaze/Attention Analyzer")]
    public static void Open()
    {
        var window = GetWindow<GazeAttentionAnalyzer>("Gaze Attention");
        window.minSize = new Vector2(600, 400);
        window.Show();
    }

    private void OnEnable()
    {
        folderPath = EditorPrefs.GetString(FolderPrefKey, "");
        if (string.IsNullOrEmpty(folderPath)) folderPath = Application.persistentDataPath;
    }

    // ==================== RESULT MODEL ====================

    private class ObjectRow
    {
        public string name;
        public string category;
        public float seconds;
        public int looks;
        public float longestLook;
        public float firstLookTime = -1f;
        public float percentOfTracked;
        public float percentOfOnObjects;
    }

    private class FileResult
    {
        public string fileName;
        public string source;          // "summary JSON" or "raw NDJSON"
        public string participantId = "";
        public string sessionId = "";
        public string condition = "";
        public float trackedSeconds;
        public float onObjectSeconds;
        public float onNothingSeconds;
        public List<ObjectRow> objects = new List<ObjectRow>();
        public List<ObjectRow> categories = new List<ObjectRow>();
        public bool expanded = true;
    }

    // ==================== GUI ====================

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Session Files", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            folderPath = EditorGUILayout.TextField("Folder", folderPath);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string picked = EditorUtility.OpenFolderPanel("Folder containing gaze session files", folderPath, "");
                if (!string.IsNullOrEmpty(picked)) folderPath = picked;
                GUI.FocusControl(null);
            }
            if (GUILayout.Button("persistentDataPath", GUILayout.Width(130)))
            {
                folderPath = Application.persistentDataPath;
                GUI.FocusControl(null);
            }
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Sources", EditorStyles.boldLabel);
        useSummaryJson = EditorGUILayout.ToggleLeft("gaze_session_summary_*.json (tracker's own rollup)", useSummaryJson);
        useGazeEvents = EditorGUILayout.ToggleLeft("gaze_events_*.ndjson (per-look event stream)", useGazeEvents);
        useRawNdjson = EditorGUILayout.ToggleLeft("raw_eye_head_tracking_*.ndjson (recompute from per-frame stream)", useRawNdjson);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Display", EditorStyles.boldLabel);
        groupByName = EditorGUILayout.ToggleLeft("Merge objects that share a name (e.g. every gem of one prefab)", groupByName);
        includeOtherCategory = EditorGUILayout.ToggleLeft("Include uncategorised colliders (walls, floor, props)", includeOtherCategory);
        minPercentToShow = EditorGUILayout.Slider("Hide rows below %", minPercentToShow, 0f, 10f);

        EditorGUILayout.Space(6);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Analyze", GUILayout.Height(26))) Analyze();
            using (new EditorGUI.DisabledScope(results.Count == 0))
            {
                if (GUILayout.Button("Export CSV", GUILayout.Height(26), GUILayout.Width(110))) ExportCsv();
            }
        }

        if (!string.IsNullOrEmpty(status))
            EditorGUILayout.HelpBox(status, MessageType.Info);

        EditorGUILayout.Space(4);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (var result in results) DrawResult(result);
        EditorGUILayout.EndScrollView();
    }

    private void DrawResult(FileResult result)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        result.expanded = EditorGUILayout.Foldout(result.expanded, result.fileName + "  (" + result.source + ")", true);
        if (result.expanded)
        {
            if (!string.IsNullOrEmpty(result.participantId))
            {
                EditorGUILayout.LabelField(
                    string.Format("Participant {0} | Session {1} | {2}", result.participantId, result.sessionId, result.condition),
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.LabelField(string.Format(
                    "Tracked {0}s | on objects {1}s ({2}%) | on nothing {3}s ({4}%)",
                    result.trackedSeconds.ToString("F1", Inv),
                    result.onObjectSeconds.ToString("F1", Inv),
                    Percent(result.onObjectSeconds, result.trackedSeconds).ToString("F1", Inv),
                    result.onNothingSeconds.ToString("F1", Inv),
                    Percent(result.onNothingSeconds, result.trackedSeconds).ToString("F1", Inv)),
                EditorStyles.miniLabel);

            if (result.categories.Count > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("By category", EditorStyles.boldLabel);
                DrawRows(result.categories);
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("By object", EditorStyles.boldLabel);
            DrawRows(result.objects);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawRows(List<ObjectRow> rows)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Object", EditorStyles.miniBoldLabel, GUILayout.MinWidth(140));
            EditorGUILayout.LabelField("Category", EditorStyles.miniBoldLabel, GUILayout.Width(90));
            EditorGUILayout.LabelField("% trial", EditorStyles.miniBoldLabel, GUILayout.Width(55));
            EditorGUILayout.LabelField("% of gaze on objects", EditorStyles.miniBoldLabel, GUILayout.Width(140));
            EditorGUILayout.LabelField("Seconds", EditorStyles.miniBoldLabel, GUILayout.Width(60));
            EditorGUILayout.LabelField("Looks", EditorStyles.miniBoldLabel, GUILayout.Width(45));
        }

        int hidden = 0;
        foreach (var row in rows)
        {
            if (row.percentOfTracked < minPercentToShow) { hidden++; continue; }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(row.name, GUILayout.MinWidth(140));
                EditorGUILayout.LabelField(row.category, GUILayout.Width(90));
                EditorGUILayout.LabelField(row.percentOfTracked.ToString("F2", Inv) + "%", GUILayout.Width(55));

                Rect bar = GUILayoutUtility.GetRect(140, 16, GUILayout.Width(140));
                EditorGUI.ProgressBar(bar, Mathf.Clamp01(row.percentOfOnObjects / 100f), row.percentOfOnObjects.ToString("F2", Inv) + "%");

                EditorGUILayout.LabelField(row.seconds.ToString("F2", Inv), GUILayout.Width(60));
                EditorGUILayout.LabelField(row.looks.ToString(), GUILayout.Width(45));
            }
        }

        if (hidden > 0)
        {
            EditorGUILayout.LabelField(
                string.Format("({0} row(s) hidden below {1}%)", hidden, minPercentToShow.ToString("F1", Inv)),
                EditorStyles.miniLabel);
        }
    }

    // ==================== ANALYSIS ====================

    private void Analyze()
    {
        results.Clear();
        status = "";

        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
        {
            status = "Folder not found: " + folderPath;
            return;
        }

        EditorPrefs.SetString(FolderPrefKey, folderPath);

        int summaryFiles = 0, eventFiles = 0, rawFiles = 0, skipped = 0;

        if (useSummaryJson)
        {
            foreach (string path in Directory.GetFiles(folderPath, "gaze_session_summary_*.json").OrderBy(p => p))
            {
                var result = ReadSummary(path);
                if (result != null) { results.Add(result); summaryFiles++; }
                else skipped++;
            }
        }

        if (useGazeEvents)
        {
            foreach (string path in Directory.GetFiles(folderPath, "gaze_events_*.ndjson").OrderBy(p => p))
            {
                var result = ReadGazeEvents(path);
                if (result != null) { results.Add(result); eventFiles++; }
                else skipped++;
            }
        }

        if (useRawNdjson)
        {
            foreach (string path in Directory.GetFiles(folderPath, "raw_eye_head_tracking_*.ndjson").OrderBy(p => p))
            {
                var result = ReadRawNdjson(path);
                if (result != null) { results.Add(result); rawFiles++; }
                else skipped++;
            }
        }

        if (results.Count == 0)
        {
            status = "No usable gaze data found in " + folderPath + ".\n" +
                     "Expected gaze_session_summary_*.json and/or raw_eye_head_tracking_*.ndjson. " +
                     "Files with no gaze-object data (recorded before gaze logging was enabled) are skipped.";
            return;
        }

        status = string.Format("{0} summary file(s), {1} event file(s), {2} raw file(s) analysed{3}",
            summaryFiles, eventFiles, rawFiles, skipped > 0 ? ", " + skipped + " skipped (no gaze-object data)." : ".");
    }

    /// <summary>Reads the gazeAttention rollup the tracker already wrote into a session summary.</summary>
    private FileResult ReadSummary(string path)
    {
        try
        {
            var root = JsonUtility.FromJson<EyeAndHeadTracker.SessionSummaryRoot>(File.ReadAllText(path));
            if (root == null || root.gazeAttention == null || root.gazeAttention.perObject == null || root.gazeAttention.perObject.Count == 0)
                return null;

            var attention = root.gazeAttention;
            var result = new FileResult
            {
                fileName = Path.GetFileName(path),
                source = "summary JSON",
                trackedSeconds = attention.totalTrackedSeconds,
                onObjectSeconds = attention.totalTimeOnObjectsSeconds,
                onNothingSeconds = attention.totalTimeOnNothingSeconds
            };

            if (root.metadata != null)
            {
                result.participantId = root.metadata.participantId;
                result.sessionId = root.metadata.sessionId;
                result.condition = root.metadata.condition;
            }

            var rows = attention.perObject
                .Where(s => includeOtherCategory || s.category != "Other")
                .Select(s => new ObjectRow
                {
                    name = s.objectName,
                    category = s.category,
                    seconds = s.totalDwellSeconds,
                    looks = s.lookCount,
                    longestLook = s.longestLookSeconds,
                    firstLookTime = s.firstLookTime
                })
                .ToList();

            result.objects = Finalize(rows, result.trackedSeconds, result.onObjectSeconds, groupByName);
            result.categories = Finalize(
                rows.Select(r => new ObjectRow
                {
                    name = r.category,
                    category = r.category,
                    seconds = r.seconds,
                    looks = r.looks,
                    longestLook = r.longestLook,
                    firstLookTime = r.firstLookTime
                }).ToList(),
                result.trackedSeconds, result.onObjectSeconds, true);

            return result;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("GazeAttentionAnalyzer: could not read " + Path.GetFileName(path) + " - " + ex.Message);
            return null;
        }
    }

    // Minimal mirrors of the NDJSON line shapes; JsonUtility ignores fields we don't declare.
    [Serializable]
    private class RawFrameLine
    {
        public string type;
        public float deltaTimeMs;
        public string eyeRaycastHitObject;
        public string gazeHitCategory;
    }

    [Serializable]
    private class RawLookEventLine
    {
        public string type;
        public string objectName;
        public string category;
        public float startTime;
        public float endTime;
        public float durationSeconds;
    }

    /// <summary>
    /// Reads the append-only per-look stream. Each line is one completed look, so dwell per
    /// object is just the sum of durations. The trial length is approximated by the latest
    /// endTime seen, which means the "% trial" column here excludes any look shorter than the
    /// tracker's minLookDurationToLogSeconds. Use the summary JSON if you need exact totals.
    /// </summary>
    private FileResult ReadGazeEvents(string path)
    {
        try
        {
            var seconds = new Dictionary<string, float>();
            var categories = new Dictionary<string, string>();
            var looks = new Dictionary<string, int>();
            var longest = new Dictionary<string, float>();
            var firstLook = new Dictionary<string, float>();

            float latestEndTime = 0f;
            int parsed = 0;

            using (var reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length < 2 || line[0] != '{') continue;

                    var ev = JsonUtility.FromJson<RawLookEventLine>(line);
                    if (ev == null || string.IsNullOrEmpty(ev.objectName)) continue;

                    parsed++;
                    string name = ev.objectName;
                    string category = string.IsNullOrEmpty(ev.category) ? "Other" : ev.category;

                    if (!includeOtherCategory && category == "Other") continue;

                    float existing;
                    seconds.TryGetValue(name, out existing);
                    seconds[name] = existing + ev.durationSeconds;
                    categories[name] = category;

                    int count;
                    looks.TryGetValue(name, out count);
                    looks[name] = count + 1;

                    float best;
                    longest.TryGetValue(name, out best);
                    if (ev.durationSeconds > best) longest[name] = ev.durationSeconds;

                    float first;
                    if (!firstLook.TryGetValue(name, out first) || ev.startTime < first)
                        firstLook[name] = ev.startTime;

                    if (ev.endTime > latestEndTime) latestEndTime = ev.endTime;
                }
            }

            if (parsed == 0) return null;

            float onObjectSeconds = seconds.Values.Sum();
            float trackedSeconds = Mathf.Max(latestEndTime, onObjectSeconds);

            var rows = seconds.Select(kv =>
            {
                string category;
                int count;
                float best;
                float first;
                return new ObjectRow
                {
                    name = kv.Key,
                    category = categories.TryGetValue(kv.Key, out category) ? category : "Other",
                    seconds = kv.Value,
                    looks = looks.TryGetValue(kv.Key, out count) ? count : 0,
                    longestLook = longest.TryGetValue(kv.Key, out best) ? best : 0f,
                    firstLookTime = firstLook.TryGetValue(kv.Key, out first) ? first : -1f
                };
            }).ToList();

            var result = new FileResult
            {
                fileName = Path.GetFileName(path),
                source = "look events",
                trackedSeconds = trackedSeconds,
                onObjectSeconds = onObjectSeconds,
                onNothingSeconds = Mathf.Max(0f, trackedSeconds - onObjectSeconds),
                objects = Finalize(rows, trackedSeconds, onObjectSeconds, groupByName)
            };

            result.categories = Finalize(
                rows.Select(r => new ObjectRow
                {
                    name = r.category,
                    category = r.category,
                    seconds = r.seconds,
                    looks = r.looks,
                    longestLook = r.longestLook,
                    firstLookTime = r.firstLookTime
                }).ToList(),
                trackedSeconds, onObjectSeconds, true);

            return result;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("GazeAttentionAnalyzer: could not read " + Path.GetFileName(path) + " - " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Recomputes the percentages straight from the per-frame NDJSON stream: each frame's
    /// deltaTimeMs is charged to whatever object that frame's gaze ray was on.
    /// </summary>
    private FileResult ReadRawNdjson(string path)
    {
        try
        {
            var seconds = new Dictionary<string, float>();
            var categories = new Dictionary<string, string>();
            var eventLooks = new Dictionary<string, int>();
            var frameOnsets = new Dictionary<string, int>();

            float trackedSeconds = 0f;
            float onNothingSeconds = 0f;
            bool sawAnyHit = false;
            string previousHit = null;

            using (var reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length < 2 || line[0] != '{') continue;

                    if (line.Contains("\"type\":\"gazeLookEvent\""))
                    {
                        // Look events are per-look records. The frame stream below already carries
                        // the dwell time, so only the look count is taken from here.
                        var ev = JsonUtility.FromJson<RawLookEventLine>(line);
                        if (ev != null && !string.IsNullOrEmpty(ev.objectName))
                        {
                            int c;
                            eventLooks.TryGetValue(ev.objectName, out c);
                            eventLooks[ev.objectName] = c + 1;
                            if (!categories.ContainsKey(ev.objectName))
                                categories[ev.objectName] = string.IsNullOrEmpty(ev.category) ? "Other" : ev.category;
                        }
                        continue;
                    }

                    var frame = JsonUtility.FromJson<RawFrameLine>(line);
                    if (frame == null) continue;

                    float dt = frame.deltaTimeMs / 1000f;
                    if (dt < 0f || dt > 1f) dt = 0f;   // same guard the tracker applies
                    trackedSeconds += dt;

                    string hit = frame.eyeRaycastHitObject;
                    if (string.IsNullOrEmpty(hit) || hit == "None")
                    {
                        onNothingSeconds += dt;
                        previousHit = null;
                        continue;
                    }

                    sawAnyHit = true;
                    string category = string.IsNullOrEmpty(frame.gazeHitCategory) || frame.gazeHitCategory == "None"
                        ? "Other" : frame.gazeHitCategory;

                    if (!includeOtherCategory && category == "Other") { previousHit = hit; continue; }

                    float accumulated;
                    seconds.TryGetValue(hit, out accumulated);
                    seconds[hit] = accumulated + dt;
                    categories[hit] = category;

                    // Count look onsets from the frame stream as well, so recordings written
                    // without look events still report a look count.
                    if (previousHit != hit)
                    {
                        int onsets;
                        frameOnsets.TryGetValue(hit, out onsets);
                        frameOnsets[hit] = onsets + 1;
                    }
                    previousHit = hit;
                }
            }

            if (!sawAnyHit) return null;

            float onObjectSeconds = seconds.Values.Sum();

            var rows = seconds.Select(kv =>
            {
                string category;
                int looks;
                return new ObjectRow
                {
                    name = kv.Key,
                    category = categories.TryGetValue(kv.Key, out category) ? category : "Other",
                    seconds = kv.Value,
                    looks = eventLooks.TryGetValue(kv.Key, out looks)
                        ? looks
                        : (frameOnsets.TryGetValue(kv.Key, out looks) ? looks : 0)
                };
            }).ToList();

            var result = new FileResult
            {
                fileName = Path.GetFileName(path),
                source = "raw NDJSON",
                trackedSeconds = trackedSeconds,
                onObjectSeconds = onObjectSeconds,
                onNothingSeconds = onNothingSeconds,
                objects = Finalize(rows, trackedSeconds, onObjectSeconds, groupByName)
            };

            result.categories = Finalize(
                rows.Select(r => new ObjectRow { name = r.category, category = r.category, seconds = r.seconds, looks = r.looks }).ToList(),
                trackedSeconds, onObjectSeconds, true);

            return result;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("GazeAttentionAnalyzer: could not read " + Path.GetFileName(path) + " - " + ex.Message);
            return null;
        }
    }

    /// <summary>Optionally merges same-named rows, computes the percentages, sorts by dwell time.</summary>
    private static List<ObjectRow> Finalize(List<ObjectRow> rows, float trackedSeconds, float onObjectSeconds, bool merge)
    {
        List<ObjectRow> output;

        if (merge)
        {
            output = rows.GroupBy(r => r.name).Select(g =>
            {
                var merged = new ObjectRow
                {
                    name = g.Key,
                    category = g.First().category,
                    seconds = g.Sum(r => r.seconds),
                    looks = g.Sum(r => r.looks),
                    longestLook = g.Max(r => r.longestLook)
                };
                var firstLooks = g.Where(r => r.firstLookTime >= 0f).Select(r => r.firstLookTime).ToList();
                merged.firstLookTime = firstLooks.Count > 0 ? firstLooks.Min() : -1f;
                return merged;
            }).ToList();
        }
        else
        {
            output = new List<ObjectRow>(rows);
        }

        foreach (var row in output)
        {
            row.percentOfTracked = Percent(row.seconds, trackedSeconds);
            row.percentOfOnObjects = Percent(row.seconds, onObjectSeconds);
        }

        output.Sort((a, b) => b.seconds.CompareTo(a.seconds));
        return output;
    }

    private static float Percent(float part, float whole)
    {
        return whole > 0f ? (part / whole) * 100f : 0f;
    }

    // ==================== EXPORT ====================

    private void ExportCsv()
    {
        string savePath = EditorUtility.SaveFilePanel("Export gaze attention CSV", folderPath, "gaze_attention_summary.csv", "csv");
        if (string.IsNullOrEmpty(savePath)) return;

        var sb = new StringBuilder();
        sb.AppendLine("file,source,participantId,sessionId,scope,objectName,category,seconds,percentOfTrialTime,percentOfTimeOnObjects,looks,longestLookSeconds,firstLookTime,trackedSeconds,timeOnObjectsSeconds,timeOnNothingSeconds");

        foreach (var result in results)
        {
            foreach (var row in result.categories) AppendCsvRow(sb, result, "category", row);
            foreach (var row in result.objects) AppendCsvRow(sb, result, "object", row);
        }

        File.WriteAllText(savePath, sb.ToString());
        status = "Exported " + results.Count + " file(s) to " + savePath;
        Debug.Log("GazeAttentionAnalyzer: exported CSV to " + savePath);
    }

    private static void AppendCsvRow(StringBuilder sb, FileResult result, string scope, ObjectRow row)
    {
        sb.Append(Csv(result.fileName)).Append(',')
          .Append(Csv(result.source)).Append(',')
          .Append(Csv(result.participantId)).Append(',')
          .Append(Csv(result.sessionId)).Append(',')
          .Append(scope).Append(',')
          .Append(Csv(row.name)).Append(',')
          .Append(Csv(row.category)).Append(',')
          .Append(row.seconds.ToString("F3", Inv)).Append(',')
          .Append(row.percentOfTracked.ToString("F3", Inv)).Append(',')
          .Append(row.percentOfOnObjects.ToString("F3", Inv)).Append(',')
          .Append(row.looks).Append(',')
          .Append(row.longestLook.ToString("F3", Inv)).Append(',')
          .Append(row.firstLookTime.ToString("F3", Inv)).Append(',')
          .Append(result.trackedSeconds.ToString("F3", Inv)).Append(',')
          .Append(result.onObjectSeconds.ToString("F3", Inv)).Append(',')
          .Append(result.onNothingSeconds.ToString("F3", Inv))
          .AppendLine();
    }

    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.IndexOf(',') < 0 && value.IndexOf('"') < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
