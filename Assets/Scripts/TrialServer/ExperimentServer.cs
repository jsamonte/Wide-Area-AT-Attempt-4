using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace TrialServer
{
    /// <summary>
    /// The trial web server. Turns the ML2 into the study's own control surface: the researcher opens a
    /// browser (phone on the same network, or a PC through an adb tunnel) and both SEES what the participant
    /// is doing and DRIVES the trials, without having to be at the headset.
    ///
    /// This is a WRAPPER, not a replacement. SequenceManager stays the trial driver and EyeAndHeadTracker
    /// stays the single data source. The SequenceBridge (a separate component) turns the commands this server
    /// accepts into clicks on the study's existing buttons. This class only owns the socket, the snapshot, and the
    /// command queue.
    ///
    /// THE THREADING CONTRACT, which is the whole design:
    ///   - HttpListener callbacks run on a BACKGROUND thread. Touching a Unity API there (Time.time,
    ///     FindObjectOfType, even Application.persistentDataPath) crashes or throws. So the listener thread
    ///     touches NOTHING of Unity's.
    ///   - Outbound: the main thread builds a JSON snapshot string in Update and publishes it to a volatile
    ///     field. The listener thread only ever hands out that finished string.
    ///   - Inbound: a request enqueues a command and returns 202 Accepted immediately. Update drains the
    ///     queue and executes on the main thread. The HTTP response means "heard you", not "done": the
    ///     dashboard confirms by watching the status change, which is also what proves the trial machinery
    ///     really ran rather than just acknowledging.
    ///
    /// The server is NOT on the data path. The gaze JSON is written on the main thread by the study's tracker; this server
    /// is a read-only observer of it. If HTTP stalls for a second, the recording is untouched and the
    /// dashboard simply shows a stale number.
    ///
    ///     adb logcat -d -s Unity | Select-String "\[SERVER\]"
    /// </summary>
    public class ExperimentServer : MonoBehaviour
    {
        [Header("Server")]
        [Tooltip("Master switch. Off means no listener thread and no socket at all.")]
        public bool enableWebServer = true;

        [Tooltip("Port to serve on. The dashboard is at http://<headset-ip>:<port>/")]
        public int port = 8080;

        [Header("Tuning")]
        [Tooltip("How many times per second the main thread rebuilds the status snapshot. A few Hz is plenty; " +
                 "the dashboard polls independently. This is not on the data path.")]
        public float snapshotHz = 10f;

        [Tooltip("Log every HTTP request to the console. Noisy; leave off unless chasing a routing problem.")]
        public bool logRequests = false;

        [Tooltip("Lines held in the in-memory dev log ring. Takes effect on the next launch.")]
        public int logBufferLines = 800;

        [Tooltip("Write the whole run to a .log file on the headset (persistentDataPath/logs/run_<time>.log), " +
                 "flushed per line. ON by default so an outdoor, no-network run is still captured for review " +
                 "later (pull with adb, or from the files list once back on a network). Independent of the web " +
                 "server: the file is written even with the server off.")]
        public bool logToFile = true;

        /// <summary>Fired on the MAIN thread when a control request arrives. The SequenceBridge subscribes
        /// to this and turns the command into a click on the study's existing UI.</summary>
        public static event Action<string, string> OnCommand;

        /// <summary>The headset's IPv4 on whatever network it is currently on. Empty if it has none (a
        /// pure USB tether has no wifi address, and the adb tunnel makes that fine).</summary>
        public static string LocalIp { get; private set; } = "";

        const string Tag = "[SERVER]";

        HttpListener _listener;
        Thread _thread;
        volatile bool _running;
        volatile string _snapshot = "{}";

        // The performance monitor's on/off is toggled from the dashboard's DEV panel, but PerfMonitor touches
        // Unity, so the request is stashed here by the listener thread and APPLIED on the main thread in
        // Update, exactly like a trial command. 0 = nothing pending, 1 = turn on, 2 = turn off.
        volatile int _perfRequest;

        // Cached on the main thread in Awake, because the listener thread may not ask Unity for them. This is
        // the gaze session directory: EyeAndHeadTracker writes its summary JSON to Application.persistentDataPath,
        // so pointing the file endpoints there lets the operator pull a session off the headset from a browser.
        string _filesDir = "";
        string _html = "";

        float _snapTimer;
        readonly ConcurrentQueue<Command> _commands = new ConcurrentQueue<Command>();

        struct Command
        {
            public string Path;
            public string Body;
        }

        void Awake()
        {
            _filesDir = Application.persistentDataPath;

            // Start capturing the log immediately, not in Start: the interesting lines (the tracker coming up,
            // the eye-tracking permission dance, a missing GazeInputManager) all happen during startup, and a
            // ring that only begins recording once the socket is bound would miss exactly the failures you open
            // the dev log to diagnose.
            LogRing.Install(logBufferLines);

            // Start the on-disk run log immediately (not in Start, and not gated on the web server): the whole
            // point is the outdoor run with no phone attached, and the startup lines are the ones worth having.
            if (logToFile)
            {
                LogRing.StartFileLog(_filesDir);
                if (!string.IsNullOrEmpty(LogRing.FilePath)) Debug.Log($"{Tag} Run log -> {LogRing.FilePath}");
            }

            // The dashboard ships as a TextAsset in Resources, NOT in StreamingAssets. On Android,
            // StreamingAssets lives inside the compressed APK and File.ReadAllText on it returns nothing: it
            // works perfectly in the Editor and serves a blank page on the device. A TextAsset is compiled in
            // and readable from memory on both.
            // Unique resource name on purpose. The flight project's dashboard is also a Resources TextAsset named
            // "dashboard_html", and Unity merges every Resources folder into one namespace, so loading that
            // name could hand back the wrong page. "trialserver_dashboard" cannot collide.
            var asset = Resources.Load<TextAsset>("trialserver_dashboard");
            if (asset != null) _html = asset.text;
            else Debug.LogWarning($"{Tag} Resources/trialserver_dashboard.txt not found. The API will still " +
                                  "serve, but the dashboard page will be a placeholder.");
        }

        void Start()
        {
            if (!enableWebServer) { Debug.Log($"{Tag} Disabled."); return; }
            LocalIp = FindLocalIPv4();
            StartServer();
        }

        void Update()
        {
            // The log ring is fed from every thread, but it needs a main-thread clock to stamp lines with, and
            // it keeps recording even with the server off (so a no-server run still has a log to pull).
            LogRing.Pump(Time.realtimeSinceStartup);

            if (!_running) return;

            _snapTimer -= Time.unscaledDeltaTime;
            if (_snapTimer <= 0f)
            {
                _snapTimer = 1f / Mathf.Max(1f, snapshotHz);
                _snapshot = StatusSnapshot.Build(LocalIp, port);
            }

            // Apply a pending performance-monitor toggle on the MAIN thread (PerfMonitor touches Unity).
            int perf = _perfRequest;
            if (perf != 0)
            {
                _perfRequest = 0;
                PerfMonitor.SetEnabled(perf == 1, "dashboard");
            }

            // Drain commands on the main thread. This is the ONLY place a request touches Unity.
            while (_commands.TryDequeue(out Command cmd))
            {
                Debug.Log($"{Tag} command {cmd.Path} {cmd.Body}");
                try { OnCommand?.Invoke(cmd.Path, cmd.Body); }
                catch (Exception e) { Debug.LogError($"{Tag} Command handler threw: {e}"); }
            }
        }

        void OnDestroy()
        {
            LogRing.Uninstall();
            LogRing.StopFileLog();
            StopServer();
        }

        void OnApplicationQuit()
        {
            LogRing.StopFileLog();   // flush the tail before the process goes away
            StopServer();
        }

        // ---- Lifecycle ---------------------------------------------------------------------------------

        void StartServer()
        {
            try
            {
                _listener = new HttpListener();
                // "*" binds every interface: the wifi address for a phone, and loopback for an adb tunnel.
                // Both deployment targets are served by the one prefix.
                _listener.Prefixes.Add($"http://*:{port}/");
                _listener.Start();
                _running = true;

                _thread = new Thread(Serve) { IsBackground = true, Name = "ExperimentServer" };
                _thread.Start();

                string where = string.IsNullOrEmpty(LocalIp) ? "(no wifi address; use adb forward)" : $"http://{LocalIp}:{port}/";
                Debug.Log($"{Tag} Listening on port {port}. Dashboard: {where}");
                Debug.Log($"{Tag} Tethered access: adb forward tcp:{port} tcp:{port}, then http://localhost:{port}/");
            }
            catch (Exception e)
            {
                _running = false;
                // "Prefix already in use" almost always means a SECOND listener inside THIS process (two
                // ExperimentServers across additively loaded scenes), not a stale socket. Force-stopping the
                // app does not help with that, so say so: the first guess sends you chasing the wrong thing.
                Debug.LogError($"{Tag} Failed to start on port {port}: {e.Message}\n" +
                               $"{Tag} If this is 'Prefix already in use': something else in THIS app is already " +
                               $"bound to {port}. Check for a duplicate ExperimentServer (additive scene loading " +
                               "makes duplicates easy). Only if that is clean is it a stale socket.");
            }
        }

        void StopServer()
        {
            if (!_running && _listener == null) return;
            _running = false;

            // Close, not just Stop. A listener left holding the port makes the NEXT run fail to bind, and
            // that failure looks like "the server is broken" rather than "the last run did not clean up".
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;

            try { _thread?.Join(200); } catch { }
            _thread = null;

            Debug.Log($"{Tag} Stopped.");
        }

        // ---- Listener thread (NO Unity API below this line) --------------------------------------------

        void Serve()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }   // Stop() unblocks GetContext by throwing; that is the exit path.

                try { Route(ctx); }
                catch (Exception e)
                {
                    try { Send(ctx, 500, "text/plain", Encoding.UTF8.GetBytes("error: " + e.Message)); }
                    catch { }
                }
            }
        }

        void Route(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            if (path.Length == 0) path = "/";
            if (logRequests) Debug.Log($"{Tag} {ctx.Request.HttpMethod} {path}");

            switch (path)
            {
                case "/":
                case "/index.html":
                    // no-store on the PAGE itself, not just the JSON. Without it the browser happily serves a
                    // cached copy of the dashboard from a previous build, so a fixed page looks unfixed and you
                    // chase a bug that is already gone.
                    ctx.Response.AddHeader("Cache-Control", "no-store, no-cache, must-revalidate");
                    Send(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(
                        string.IsNullOrEmpty(_html) ? "<h1>Trial Server</h1><p>Dashboard asset missing.</p>" : _html));
                    return;

                case "/api/status":
                case "/api/telemetry":
                    SendJson(ctx, _snapshot);
                    return;

                case "/api/files":
                    SendJson(ctx, ListFiles());
                    return;

                case "/api/file":
                    SendFile(ctx, ctx.Request.QueryString["name"]);
                    return;

                // Every gaze session JSON in one zip, built in memory. Plain System.IO.Compression, safe off
                // the main thread. The convenience of pulling a whole session without adb.
                case "/api/files/zip":
                    SendZip(ctx);
                    return;

                case "/api/ping":
                    Send(ctx, 200, "text/plain", Encoding.UTF8.GetBytes("ok"));
                    return;

                // The browser always asks for this; we do not serve one. Answer 204 (No Content) so it stops
                // logging a 404 in the console every load.
                case "/favicon.ico":
                    Send(ctx, 204, "image/x-icon", Array.Empty<byte>());
                    return;

                // ---- DEV endpoints (read-only, except the perf TOGGLE which is a control, not a value edit) --
                // /api/logs?since=<cursor>&tag=<TAG>&level=<MIN>&max=<n>
                // LogRing is plain .NET behind a lock, so it is safe to serve straight from this thread. The
                // level floor ("WARN" shows WARN, ERROR and CRIT) is filtered server-side, exactly like the tag,
                // so the client cursor and the Download button agree with what is on screen.
                case "/api/logs":
                {
                    long since = ParseLong(ctx.Request.QueryString["since"], 0);
                    string tag = ctx.Request.QueryString["tag"];
                    string level = ctx.Request.QueryString["level"];
                    int max = (int)ParseLong(ctx.Request.QueryString["max"], 300);
                    SendJson(ctx, LogRing.ToJson(since, tag, Mathf.Clamp(max, 1, 2000), level));
                    return;
                }

                // /api/logs/download?tag=<TAG>&level=<MIN> - the whole log ring as a .txt, so a run-day log can
                // be pulled to a file the same way a gaze session is, with no adb / terminal.
                case "/api/logs/download":
                {
                    string tag = ctx.Request.QueryString["tag"];
                    string level = ctx.Request.QueryString["level"];
                    string text = LogRing.ToText(tag, level);
                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"trialserver_log_{stamp}.txt\"");
                    Send(ctx, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
                    return;
                }

                // /api/dev/perf?on=1 - turn the performance monitor on/off. The ONLY write this DEV panel makes,
                // and it is a control (start/stop sampling), not a value edit. Stashed here and applied on the
                // main thread in Update, because PerfMonitor touches Unity and this runs on the listener thread.
                case "/api/dev/perf":
                {
                    bool on = ctx.Request.QueryString["on"] == "1";
                    _perfRequest = on ? 1 : 2;
                    SendJson(ctx, "{\"accepted\":true,\"on\":" + (on ? "true" : "false") + "}", 202);
                    return;
                }
            }

            // Control endpoints: /api/trial/sequence, /start, /mark. Accepted here and executed on the main
            // thread next Update by the SequenceBridge. Deliberately permissive about the verb so the
            // dashboard, a curl, or a browser address bar can all drive it during bring-up.
            if (path.StartsWith("/api/trial/", StringComparison.Ordinal))
            {
                string body = ReadBody(ctx);
                _commands.Enqueue(new Command { Path = path, Body = body });
                SendJson(ctx, "{\"accepted\":true,\"command\":\"" + path + "\"}", 202);
                return;
            }

            Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("not found: " + path));
        }

        static long ParseLong(string s, long fallback)
            => long.TryParse(s, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out long v) ? v : fallback;

        static string ReadBody(HttpListenerContext ctx)
        {
            if (!ctx.Request.HasEntityBody) return "";
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                return reader.ReadToEnd();
        }

        // System.IO is plain .NET, safe off the main thread. _filesDir was cached in Awake precisely so we
        // never have to ask Unity for persistentDataPath from here. Lists the gaze session summary/raw files.
        string ListFiles()
        {
            var sb = new StringBuilder("{\"dir\":\"");
            sb.Append(_filesDir.Replace("\\", "/")).Append("\",\"files\":[");
            try
            {
                if (Directory.Exists(_filesDir))
                {
                    // Gaze session JSON plus the run .log files, so a run captured with no network can be pulled
                    // from the same list once back on one. Two patterns, not a wildcard, so nothing stray is served.
                    var json = Directory.GetFiles(_filesDir, "*.json", SearchOption.AllDirectories);
                    var logs = Directory.GetFiles(_filesDir, "*.log", SearchOption.AllDirectories);
                    var files = new string[json.Length + logs.Length];
                    json.CopyTo(files, 0);
                    logs.CopyTo(files, json.Length);
                    Array.Sort(files);
                    bool first = true;
                    foreach (var f in files)
                    {
                        var info = new FileInfo(f);
                        string rel = f.Substring(_filesDir.Length).TrimStart('/', '\\').Replace("\\", "/");

                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"path\":\"").Append(rel)
                          .Append("\",\"name\":\"").Append(info.Name)
                          .Append("\",\"bytes\":").Append(info.Length).Append('}');
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"{Tag} Listing failed: {e.Message}"); }
            return sb.Append("]}").ToString();
        }

        void SendFile(HttpListenerContext ctx, string relPath)
        {
            // Resolve to a full path and require it to still be INSIDE the files directory: that defeats
            // "../../.." regardless of how it was encoded.
            string full;
            try { full = Path.GetFullPath(Path.Combine(_filesDir, relPath ?? "")); }
            catch { full = ""; }

            bool inside = !string.IsNullOrEmpty(full) &&
                          full.StartsWith(Path.GetFullPath(_filesDir), StringComparison.Ordinal);

            if (!inside || !File.Exists(full))
            {
                Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("no such file: " + relPath));
                return;
            }

            byte[] bytes = File.ReadAllBytes(full);
            string mime = full.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ? "text/plain; charset=utf-8" : "application/json";
            ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(full)}\"");
            Send(ctx, 200, mime, bytes);
        }

        // Every gaze session JSON under the files tree, zipped in memory.
        void SendZip(HttpListenerContext ctx)
        {
            try
            {
                if (!Directory.Exists(_filesDir))
                {
                    Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("no files directory yet"));
                    return;
                }

                byte[] bytes;
                using (var ms = new MemoryStream())
                {
                    using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                    {
                        foreach (string f in Directory.GetFiles(_filesDir, "*.json", SearchOption.AllDirectories))
                        {
                            string rel = f.Substring(_filesDir.Length).TrimStart('/', '\\').Replace("\\", "/");
                            var entry = zip.CreateEntry(rel, System.IO.Compression.CompressionLevel.Optimal);
                            using (Stream es = entry.Open())
                            using (var fs = File.OpenRead(f))
                                fs.CopyTo(es);
                        }
                    }
                    bytes = ms.ToArray();
                }

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"gaze_sessions_{stamp}.zip\"");
                Send(ctx, 200, "application/zip", bytes);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{Tag} Zip failed: {e.Message}");
                Send(ctx, 500, "text/plain", Encoding.UTF8.GetBytes("zip failed: " + e.Message));
            }
        }

        void SendJson(HttpListenerContext ctx, string json, int status = 200)
        {
            // No-store: a cached status blob is a dashboard that lies about a frozen stream, which is the
            // exact failure the live readout exists to catch.
            ctx.Response.AddHeader("Cache-Control", "no-store");
            Send(ctx, status, "application/json", Encoding.UTF8.GetBytes(json));
        }

        static void Send(HttpListenerContext ctx, int status, string contentType, byte[] body)
        {
            try
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = body.Length;
                ctx.Response.OutputStream.Write(body, 0, body.Length);
            }
            catch { /* client hung up mid-write; nothing to do and nothing to report */ }
            finally { try { ctx.Response.OutputStream.Close(); } catch { } }
        }

        // ---- Network ------------------------------------------------------------------------------------

        /// <summary>The headset's own IPv4, for the hotspot case where the phone needs an address to browse
        /// to. Empty on a pure USB tether, which is fine: that path uses adb forward.</summary>
        static string FindLocalIPv4()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = addr.Address.ToString();
                        if (ip.StartsWith("127.", StringComparison.Ordinal)) continue;
                        return ip;
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"{Tag} Could not read local IP: {e.Message}"); }
            return "";
        }
    }
}
