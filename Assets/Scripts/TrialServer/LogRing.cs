using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace TrialServer
{
    /// <summary>
    /// A bounded, in-memory ring of the app's own log lines, so the dashboard's DEV view can show what you
    /// would otherwise only see by sitting in a terminal running adb logcat with a Select-String filter.
    /// That terminal loop is the thing this exists to delete: on run day there is no PC tethered to the
    /// headset, and "what is the study doing right now" should be answerable from a phone browser.
    ///
    /// HOW IT IS FILLED. Unity's Application.logMessageReceivedThreaded fires for every Debug.Log in the
    /// process, from whatever thread logged it. So this class is written from MANY threads and read from the
    /// HTTP listener thread, and it touches NO Unity API anywhere (a plain lock plus plain .NET types). The
    /// threaded variant is used deliberately: the non-threaded one only fires on the main thread, and any
    /// worker-thread callback would be missing from exactly the log you opened the dev view to read.
    ///
    /// THE TAG IS THE POINT. Every subsystem already prefixes its lines ("[SERVER] ...", "[BRIDGE] ..."),
    /// which is what makes a logcat filter work. We parse that prefix once, on the way in, so the dashboard
    /// can offer the same filter as a set of buttons: one view per subsystem, no regex, no terminal.
    ///
    /// SEVERITY RIDES THE SAME PREFIX. A line may name its own level as "[SERVER:CRIT] ...", parsed here into
    /// tag "SERVER" + level CRIT. Everything without an explicit level derives one from Unity's LogType, so
    /// every existing "[SERVER] ..." line keeps working untouched and no call site had to be rewritten. This
    /// is deliberately NOT a Log.Critical() wrapper: a second logging path beside Debug.Log is a second home
    /// for the same thing, and the two would drift. There is one path, and the prefix carries the metadata.
    ///
    /// CRIT is reserved for "this session's data is compromised" (e.g. eye-tracking permission denied so the
    /// gaze log is worthless, storage about to run out so the file will stop mid-write), NOT for "an exception
    /// happened" (that is ERROR). The distinction is the whole point: a level that fires on every exception is
    /// one you learn to ignore.
    ///
    /// The cursor (Seq) is monotonic, so the dashboard polls "everything since N" and never re-fetches or
    /// misses a line. When the ring wraps, the client's next poll simply resumes from the oldest line still
    /// held, and the response says so, rather than silently skipping.
    /// </summary>
    public static class LogRing
    {
        public struct Line
        {
            public long Seq;         // monotonic; the dashboard's cursor
            public string Tag;       // "SERVER", "BRIDGE", ... or "" when the line has no [TAG] prefix
            public string Level;     // "INFO" | "WARN" | "ERROR" | "CRIT"
            public float Time;       // seconds since startup, sampled on the main thread (see Pump)
            public string Message;
        }

        // The severity axis. ONE axis, not two: an earlier version carried both a LogType-derived "type" and
        // a semantic level, which is the same fact stored twice and free to disagree. Rank orders them so a
        // filter can say "this and worse" rather than needing a checkbox per level.
        public const string LevelInfo = "INFO";
        public const string LevelWarn = "WARN";
        public const string LevelError = "ERROR";
        public const string LevelCrit = "CRIT";

        /// <summary>Severity order, low to high. An unknown string reads as INFO.</summary>
        public static int Rank(string level)
        {
            if (string.Equals(level, LevelCrit, StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(level, LevelError, StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(level, LevelWarn, StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        // Capacity is set ONCE, on the main thread, by Install. The buffer is a plain array: resizing it
        // mid-session would mean locking out every logging thread to reallocate, which is a real cost for a
        // knob nobody turns twice. The value therefore takes effect on the next launch.
        static readonly object Gate = new object();
        static Line[] _ring = new Line[800];
        static int _count;           // lines currently held (<= _ring.Length)
        static int _head;            // next write index
        static long _seq;            // total lines ever accepted
        static bool _installed;

        // ---- Optional file sink -------------------------------------------------------------------------
        //
        // The download endpoint serves the in-memory ring, which needs a network and holds only the last N
        // lines. For an OUTDOOR run with no phone attached, that is nothing. This sink writes every captured
        // line to a file on the headset instead, flushed per line so a crash keeps the tail, so the whole run
        // is on disk to pull with adb (or from the dashboard's files list once back on a network). It is the
        // SAME single log path: the file gets exactly what the ring gets, so the two cannot drift.
        static readonly object FileGate = new object();
        static volatile StreamWriter _file;
        static string _filePath = "";

        // ---- Flood control ------------------------------------------------------------------------------
        //
        // A single misbehaving callback can log every frame forever. On 2026-08-22 a PLUME input-recorder
        // NullReferenceException fired ~450x/sec for 12 minutes: 328,276 of the run log's 328,681 lines were
        // two distinct messages, the file reached 76 MB, and AutoFlush turned every line into ~6 flush
        // syscalls. The session was killed without ever running OnApplicationPause. None of that should be
        // possible from logging alone, so the file sink now collapses consecutive duplicates, caps its own
        // size, and batches flushes instead of flushing per write.
        const long MaxFileBytes = 8L * 1024 * 1024;
        const float FlushIntervalSeconds = 1f;

        static long _bytesWritten;
        static bool _fileCapped;
        static string _lastLevel;
        static string _lastMessage;
        static int _repeatCount;
        static bool _dirty;
        static float _lastFlushAt;

        /// <summary>The run log file currently being written, or "" if the file sink is off. Logged once at
        /// startup so the operator (or an AI reading the run afterward) knows where the run was captured.</summary>
        public static string FilePath { get { lock (FileGate) return _filePath; } }

        // Sampled on the main thread each frame and read by the logging threads, because Time.realtimeSinceStartup
        // is a Unity API and a line can be logged from a worker thread.
        static volatile float _now;

        /// <summary>Total lines ever accepted. The oldest line still held is (Total - Count).</summary>
        public static long Total { get { lock (Gate) return _seq; } }

        /// <summary>Hook the ring up to Unity's log stream. Called once by ExperimentServer on the main
        /// thread. Safe to call again (it no-ops), which matters because additive scene loading makes
        /// duplicate components easy.</summary>
        public static void Install(int capacity)
        {
            lock (Gate)
            {
                if (_installed) return;
                _installed = true;
                _ring = new Line[Mathf.Clamp(capacity, 100, 5000)];
                _count = 0;
                _head = 0;
            }
            Application.logMessageReceivedThreaded += OnLog;
        }

        public static void Uninstall()
        {
            lock (Gate)
            {
                if (!_installed) return;
                _installed = false;
            }
            Application.logMessageReceivedThreaded -= OnLog;
        }

        /// <summary>Start mirroring every captured line to a file at <paramref name="dir"/>/logs/run_&lt;stamp&gt;.log.
        /// Called once by ExperimentServer on the main thread when logToFile is on. Independent of the web
        /// server: a no-network outdoor run still lands a full log on disk. No-ops if already writing.</summary>
        public static void StartFileLog(string dir)
        {
            lock (FileGate)
            {
                if (_file != null) return;
                try
                {
                    string logDir = Path.Combine(dir, "logs");
                    Directory.CreateDirectory(logDir);
                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    _filePath = Path.Combine(logDir, "run_" + stamp + ".log");
                    // AutoFlush is OFF: Pump() flushes on an interval and CRIT flushes immediately, so a
                    // sustained error loop cannot turn into a syscall storm. Worst case on a hard kill is
                    // losing up to FlushIntervalSeconds of tail.
                    _file = new StreamWriter(_filePath, false, new UTF8Encoding(false)) { AutoFlush = false };
                    _bytesWritten = 0;
                    _fileCapped = false;
                    _lastLevel = null;
                    _lastMessage = null;
                    _repeatCount = 0;
                    _dirty = false;
                    _lastFlushAt = _now;
                    _file.WriteLine("# Trial server run log started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    _file.WriteLine("# One line per Debug.Log, from every thread. CRIT means the session's data is compromised.");
                    _file.WriteLine();
                }
                catch (Exception e)
                {
                    _file = null;
                    _filePath = "";
                    Debug.LogWarning("[SERVER] Run log file could not be opened (" + e.Message + "). Logging to the ring only.");
                }
            }
        }

        /// <summary>Flush and close the run log file, so the last lines survive and the handle is released.
        /// Called on teardown / application quit.</summary>
        public static void StopFileLog()
        {
            lock (FileGate)
            {
                if (_file == null) return;
                try { if (!_fileCapped) FlushRepeatSummary(_now); } catch { }
                try { _file.Flush(); _file.Dispose(); } catch { /* closing a broken writer is not worth a throw */ }
                _file = null;
                _filePath = "";
            }
        }

        // Append one already-parsed line to the file sink, if open. Same format as ToText, so a line in the
        // file reads identically to a line in the downloaded log. Its OWN lock, not the ring's: file I/O must
        // not stall the logging threads. A write failure disables the sink rather than throwing into whatever
        // thread happened to log, and stays silent (logging from inside the log callback could recurse).
        static void WriteFileLine(string level, float time, string message)
        {
            if (_file == null) return;
            lock (FileGate)
            {
                if (_file == null || _fileCapped) return;

                level = level ?? LevelInfo;

                // Collapse a repeating line instead of writing it again. The count is emitted once the
                // message changes (or the log closes), so a stuck callback costs one line, not millions.
                if (message == _lastMessage && level == _lastLevel)
                {
                    _repeatCount++;
                    return;
                }

                try
                {
                    FlushRepeatSummary(time);

                    _lastLevel = level;
                    _lastMessage = message;
                    _repeatCount = 0;

                    RawWriteLine(level, time, message);
                    AfterWrite(level);
                }
                catch
                {
                    try { _file.Dispose(); } catch { }
                    _file = null;
                    _filePath = "";
                }
            }
        }

        // Emit the "repeated N times" note for the previous line. Caller holds FileGate.
        static void FlushRepeatSummary(float time)
        {
            if (_repeatCount <= 0) return;
            int n = _repeatCount;
            _repeatCount = 0;
            RawWriteLine(_lastLevel, time, "... previous line repeated " + n + " more time" + (n == 1 ? "" : "s"));
        }

        // Actual write + byte accounting. Caller holds FileGate and has checked _file / _fileCapped.
        static void RawWriteLine(string level, float time, string message)
        {
            string stamp = time.ToString("F1", System.Globalization.CultureInfo.InvariantCulture).PadLeft(8);
            _file.Write('[');
            _file.Write(stamp);
            _file.Write("] ");
            _file.Write((level ?? LevelInfo).PadRight(5));
            _file.Write("  ");
            _file.WriteLine(message);

            _bytesWritten += stamp.Length + 9 + (message == null ? 0 : message.Length);
            _dirty = true;
        }

        // Post-write policy: flush CRIT immediately, and stop the sink once it has had its budget.
        // Caller holds FileGate.
        static void AfterWrite(string level)
        {
            if (string.Equals(level, LevelCrit, StringComparison.OrdinalIgnoreCase))
            {
                try { _file.Flush(); _dirty = false; } catch { }
            }

            if (_bytesWritten >= MaxFileBytes)
            {
                _fileCapped = true;
                try
                {
                    _file.WriteLine();
                    _file.WriteLine("# Run log capped at " + (MaxFileBytes / (1024 * 1024)) + " MB. Further lines are in the in-memory ring only.");
                    _file.Flush();
                    _dirty = false;
                }
                catch { }
            }
        }

        /// <summary>Main-thread tick: samples the clock the logging threads stamp their lines with.</summary>
        public static void Pump(float realtimeSinceStartup)
        {
            _now = realtimeSinceStartup;

            // Batched flush: keeps the on-disk tail close to live without a syscall per line.
            if (_file == null) return;
            if (realtimeSinceStartup - _lastFlushAt < FlushIntervalSeconds) return;

            lock (FileGate)
            {
                if (_file == null) return;
                _lastFlushAt = realtimeSinceStartup;
                if (!_dirty) return;
                try { _file.Flush(); _dirty = false; } catch { }
            }
        }

        static void OnLog(string message, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(message)) return;

            // The line's own declared level wins; otherwise Unity's LogType supplies it. Note that an
            // exception maps to ERROR, never CRIT: CRIT is a claim about the SESSION DATA, and only a call
            // site that knows the data is compromised can make that claim.
            ParsePrefix(message, out string tag, out string declared);
            string kind = declared ?? (type == LogType.Warning ? LevelWarn
                                     : (type == LogType.Log ? LevelInfo : LevelError));

            // An exception's message alone is often useless ("Object reference not set..."), so keep the first
            // stack frame with it. Only for the error kinds: a stack trace on every Debug.Log would bury the ring.
            if (Rank(kind) >= 2 && !string.IsNullOrEmpty(stackTrace))
            {
                string first = FirstLine(stackTrace);
                if (!string.IsNullOrEmpty(first)) message = message + "  |  " + first;
            }

            lock (Gate)
            {
                if (!_installed || _ring.Length == 0) return;
                _ring[_head] = new Line
                {
                    Seq = ++_seq,
                    Tag = tag,
                    Level = kind,
                    Time = _now,
                    Message = message
                };
                _head = (_head + 1) % _ring.Length;
                if (_count < _ring.Length) _count++;
            }

            // Mirror to the file sink (if on) with the SAME message the ring got, so the on-disk run log and
            // the downloaded ring read identically. Outside the ring lock so slow storage cannot stall logging.
            WriteFileLine(kind, _now, message);
        }

        // "[SERVER] Listening..."          -> tag "SERVER", level null (caller did not declare one)
        // "[BRIDGE:CRIT] Eye perm denied"  -> tag "BRIDGE", level "CRIT"
        //
        // Only a prefix at the very start counts, so a bracket inside a message body cannot masquerade as a
        // subsystem tag. An unrecognized suffix ("[SERVER:hello]") is left as part of the tag rather than
        // silently dropped: inventing a level from a typo would be worse than showing the typo.
        static void ParsePrefix(string message, out string tag, out string level)
        {
            tag = "";
            level = null;
            if (message.Length < 3 || message[0] != '[') return;

            int close = message.IndexOf(']');
            if (close <= 1 || close > 24) return;

            string inside = message.Substring(1, close - 1);
            int colon = inside.LastIndexOf(':');
            if (colon > 0 && colon < inside.Length - 1)
            {
                string suffix = inside.Substring(colon + 1).Trim();
                if (string.Equals(suffix, LevelCrit, StringComparison.OrdinalIgnoreCase)) level = LevelCrit;
                else if (string.Equals(suffix, LevelError, StringComparison.OrdinalIgnoreCase)) level = LevelError;
                else if (string.Equals(suffix, LevelWarn, StringComparison.OrdinalIgnoreCase)) level = LevelWarn;
                else if (string.Equals(suffix, LevelInfo, StringComparison.OrdinalIgnoreCase)) level = LevelInfo;

                if (level != null) inside = inside.Substring(0, colon).Trim();
            }

            tag = inside;
        }

        /// <summary>How many held lines are at CRIT, and the most recent one's message. The dashboard's
        /// run-day punch list uses this to say "something in the log needs you" without the operator having
        /// to open the DEV log stream.</summary>
        public static void CritSummary(out int critCount, out string latestCrit)
        {
            critCount = 0;
            latestCrit = "";
            lock (Gate)
            {
                for (int i = 0; i < _count; i++)
                {
                    Line line = _ring[Index(i)];
                    if (!string.Equals(line.Level, LevelCrit, StringComparison.OrdinalIgnoreCase)) continue;
                    critCount++;
                    latestCrit = line.Message;
                }
            }
        }

        static string FirstLine(string s)
        {
            int nl = s.IndexOf('\n');
            string line = nl < 0 ? s : s.Substring(0, nl);
            return line.Trim();
        }

        /// <summary>Every distinct tag currently in the ring. The dashboard turns these into its filter
        /// buttons, so the filter list is always whatever the app is ACTUALLY logging right now, never a
        /// hardcoded list that goes stale the moment a subsystem is renamed.</summary>
        public static List<string> Tags()
        {
            var tags = new List<string>();
            lock (Gate)
            {
                for (int i = 0; i < _count; i++)
                {
                    string t = _ring[Index(i)].Tag;
                    if (!string.IsNullOrEmpty(t) && !tags.Contains(t)) tags.Add(t);
                }
            }
            tags.Sort(StringComparer.OrdinalIgnoreCase);
            return tags;
        }

        /// <summary>
        /// JSON of every held line with Seq &gt; <paramref name="since"/>, oldest first, optionally filtered
        /// to one tag. Called from the HTTP LISTENER THREAD, so it builds its own StringBuilder and touches
        /// no Unity API and no shared scratch buffer.
        ///
        /// <paramref name="max"/> caps one response so a client that has been away (or a burst of logging)
        /// cannot produce a multi-megabyte payload on a headset. The returned cursor is the last line
        /// actually included, so a capped response is simply resumed by the next poll.
        ///
        /// <paramref name="minLevel"/> keeps only lines at that severity or worse ("WARN" shows WARN, ERROR
        /// and CRIT). Null or "" keeps everything. Filtered server-side, exactly like the tag, so the client's
        /// cursor and the Download button agree with what is on screen.
        /// </summary>
        public static string ToJson(long since, string tagFilter, int max, string minLevel = null)
        {
            int floor = string.IsNullOrEmpty(minLevel) ? 0 : Rank(minLevel);
            var sb = new StringBuilder(4096);
            sb.Append("{\"lines\":[");

            long cursor = since;
            long oldestHeld;
            bool first = true;
            int emitted = 0;

            lock (Gate)
            {
                oldestHeld = _seq - _count;   // Seq of the line before the oldest one still held

                for (int i = 0; i < _count && emitted < max; i++)
                {
                    Line line = _ring[Index(i)];
                    if (line.Seq <= since) continue;
                    if (!string.IsNullOrEmpty(tagFilter) &&
                        !string.Equals(line.Tag, tagFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Rank(line.Level) < floor) continue;

                    if (!first) sb.Append(',');
                    first = false;

                    sb.Append("{\"seq\":").Append(line.Seq)
                      .Append(",\"t\":").Append(line.Time.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"tag\":\"").Append(Esc(line.Tag))
                      .Append("\",\"level\":\"").Append(line.Level)
                      .Append("\",\"msg\":\"").Append(Esc(line.Message))
                      .Append("\"}");

                    cursor = line.Seq;
                    emitted++;
                }

                // With a tag or level filter on, the cursor must still advance past the lines we skipped, or
                // the next poll re-walks them forever. Only safe when we did not stop early on the cap.
                if (emitted < max) cursor = _seq;
            }

            sb.Append("],\"cursor\":").Append(cursor);
            // dropped = lines that fell out of the ring before this client ever saw them. Surfaced rather than
            // hidden: a dev log with a silent hole in it is worse than one that admits the hole.
            sb.Append(",\"dropped\":").Append(since > 0 && oldestHeld > since ? oldestHeld - since : 0);
            sb.Append(",\"total\":").Append(Total);
            return sb.Append('}').ToString();
        }

        /// <summary>
        /// The whole held ring as a plain-text log, oldest first, one line each:
        /// <c>[   12.3] WARN  [SERVER] message</c>. This is what the dashboard's "Download log" button serves,
        /// so a run-day log can be pulled to a file the same way a session file is, with no adb / terminal.
        ///
        /// Called from the HTTP LISTENER THREAD, so it builds its own StringBuilder and touches no Unity API,
        /// exactly like <see cref="ToJson"/>. Optionally filtered to one tag and/or a minimum level.
        /// </summary>
        public static string ToText(string tagFilter, string minLevel = null)
        {
            int floor = string.IsNullOrEmpty(minLevel) ? 0 : Rank(minLevel);

            var sb = new StringBuilder(8192);
            lock (Gate)
            {
                long oldestHeld = _seq - _count;
                sb.Append("# Trial server log ring: ").Append(_count).Append(" line(s) held, ")
                  .Append(_seq).Append(" total ever (oldest held seq ").Append(oldestHeld + 1).Append(").\n");
                if (!string.IsNullOrEmpty(tagFilter)) sb.Append("# filtered to tag [").Append(tagFilter).Append("]\n");
                if (floor > 0) sb.Append("# filtered to level ").Append(minLevel.ToUpperInvariant()).Append(" and worse\n");
                sb.Append("# CRIT means the session's data is compromised, not merely that an error was thrown.\n");
                sb.Append('\n');

                for (int i = 0; i < _count; i++)
                {
                    Line line = _ring[Index(i)];
                    if (!string.IsNullOrEmpty(tagFilter) &&
                        !string.Equals(line.Tag, tagFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Rank(line.Level) < floor) continue;

                    sb.Append('[').Append(line.Time.ToString("F1", System.Globalization.CultureInfo.InvariantCulture).PadLeft(8))
                      .Append("] ").Append((line.Level ?? LevelInfo).PadRight(5)).Append("  ").Append(line.Message).Append('\n');
                }
            }
            return sb.ToString();
        }

        // Ring index of the i-th oldest held line.
        static int Index(int i)
        {
            int start = (_head - _count + _ring.Length) % _ring.Length;
            return (start + i) % _ring.Length;
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Control characters are not legal raw in JSON and would break the dashboard's parse.
                        if (c < 0x20) sb.Append(' ');
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
