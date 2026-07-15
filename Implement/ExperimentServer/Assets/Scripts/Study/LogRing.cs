using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ARCockpit.Study
{
    /// <summary>
    /// A bounded, in-memory ring of the app's own log lines, so the dashboard's DEV view can show what you
    /// would otherwise only see by sitting in a terminal running adb logcat with a Select-String filter.
    /// That terminal loop is the thing this exists to delete: on run day there is no PC tethered to the
    /// headset, and "what is the write path doing right now" should be answerable from a phone browser.
    ///
    /// HOW IT IS FILLED. Unity's Application.logMessageReceivedThreaded fires for every Debug.Log in the
    /// process, from whatever thread logged it. So this class is written from MANY threads and read from the
    /// HTTP listener thread, and it touches NO Unity API anywhere (a plain lock plus plain .NET types). The
    /// threaded variant is used deliberately: the non-threaded one only fires on the main thread, and the
    /// receiver's web requests and the writer's coroutine callbacks would be missing from exactly the log you
    /// opened the dev view to read.
    ///
    /// THE TAG IS THE POINT. Every subsystem already prefixes its lines ("[FDCmd] ...", "[XP12] ...",
    /// "[AOI] ..."), which is what makes a logcat filter work. We parse that prefix once, on the way in, so
    /// the dashboard can offer the same filter as a set of buttons: one view per subsystem, no regex, no
    /// terminal.
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
            public string Tag;       // "FDCmd", "XP12", "AOI", ... or "" when the line has no [TAG] prefix
            public string Type;      // "log" | "warn" | "error"
            public float Time;       // seconds since startup, sampled on the main thread (see Pump)
            public string Message;
        }

        // Capacity is read from StudyProfile ONCE, on the main thread, by Install. The buffer is a plain
        // array: resizing it from a dev-window edit mid-session would mean locking out every logging thread
        // to reallocate, which is a real cost for a knob nobody turns twice. The value therefore takes effect
        // on the next launch, and the tunable's help says so.
        static readonly object Gate = new object();
        static Line[] _ring = new Line[800];
        static int _count;           // lines currently held (<= _ring.Length)
        static int _head;            // next write index
        static long _seq;            // total lines ever accepted
        static bool _installed;

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

        /// <summary>Main-thread tick: samples the clock the logging threads stamp their lines with.</summary>
        public static void Pump(float realtimeSinceStartup) => _now = realtimeSinceStartup;

        static void OnLog(string message, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(message)) return;

            string tag = ParseTag(message);
            string kind = type == LogType.Warning ? "warn"
                        : (type == LogType.Log ? "log" : "error");   // Error, Assert and Exception all read as error

            // An exception's message alone is often useless ("Object reference not set..."), so keep the first
            // stack frame with it. Only for the error kinds: a stack trace on every Debug.Log would bury the ring.
            if (kind == "error" && !string.IsNullOrEmpty(stackTrace))
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
                    Type = kind,
                    Time = _now,
                    Message = message
                };
                _head = (_head + 1) % _ring.Length;
                if (_count < _ring.Length) _count++;
            }
        }

        // "[FDCmd] ARMED: writing..." -> "FDCmd". Only a prefix at the very start counts, so a bracket
        // inside a message body cannot masquerade as a subsystem tag.
        static string ParseTag(string message)
        {
            if (message.Length < 3 || message[0] != '[') return "";
            int close = message.IndexOf(']');
            if (close <= 1 || close > 24) return "";
            return message.Substring(1, close - 1);
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
        /// </summary>
        public static string ToJson(long since, string tagFilter, int max)
        {
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

                    if (!first) sb.Append(',');
                    first = false;

                    sb.Append("{\"seq\":").Append(line.Seq)
                      .Append(",\"t\":").Append(line.Time.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"tag\":\"").Append(Esc(line.Tag))
                      .Append("\",\"type\":\"").Append(line.Type)
                      .Append("\",\"msg\":\"").Append(Esc(line.Message))
                      .Append("\"}");

                    cursor = line.Seq;
                    emitted++;
                }

                // With a tag filter on, the cursor must still advance past the lines we skipped, or the next
                // poll re-walks them forever. Only safe when we did not stop early on the cap.
                if (emitted < max) cursor = _seq;
            }

            sb.Append("],\"cursor\":").Append(cursor);
            // dropped = lines that fell out of the ring before this client ever saw them. Surfaced rather than
            // hidden: a dev log with a silent hole in it is worse than one that admits the hole.
            sb.Append(",\"dropped\":").Append(since > 0 && oldestHeld > since ? oldestHeld - since : 0);
            sb.Append(",\"total\":").Append(Total);
            return sb.Append('}').ToString();
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
