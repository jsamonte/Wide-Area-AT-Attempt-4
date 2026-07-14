using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using ARCockpit.DevSettings;

namespace ARCockpit.Study
{
    /// <summary>
    /// The experiment web server. Turns the ML2 into the experiment's own control surface: the researcher
    /// opens a browser (phone on the same network, or the sim PC through an adb tunnel) and sees what the
    /// participant is experiencing, without having to ask them.
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
    /// The server is NOT on the data path. The CSV is written on the main thread at frame rate; this server
    /// is a read-only observer. If HTTP stalls for a second, the recording is untouched and the dashboard
    /// simply shows a stale number. That is why a 2 Hz dashboard is fine and a slow one is harmless.
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

        // The snapshot rate and the request logging live on StudyProfile (the 231 window), so they are one
        // value and tunable on device. StudyProfile.Active is a Unity API (Resources.Load), so it may ONLY be
        // touched on the main thread: SnapshotHz is read in Update, and _logRequests is a volatile MIRROR the
        // main thread refreshes each frame for the listener thread to read. This is the same contract as the
        // status snapshot: the listener thread reads finished values, never Unity.
        StudyProfile StudyCfg => StudyProfile.Active;
        float SnapshotHz => StudyCfg != null ? StudyCfg.serverSnapshotHz : 10f;
        volatile bool _logRequests;

        /// <summary>Fired on the MAIN thread when a control request arrives. The TrialController subscribes
        /// to this. Nothing is wired to it yet, so commands are currently accepted and logged only.</summary>
        public static event Action<string, string> OnCommand;

        /// <summary>The headset's IPv4 on whatever network it is currently on. Empty if it has none (a
        /// pure USB tether has no wifi address, and the adb tunnel makes that fine).</summary>
        public static string LocalIp { get; private set; } = "";

        const string Tag = "[SERVER]";

        HttpListener _listener;
        Thread _thread;
        volatile bool _running;
        volatile string _snapshot = "{}";

        // Cached on the main thread in Awake, because the listener thread may not ask Unity for them.
        string _trialsDir = "";
        string _html = "";

        float _snapTimer;
        readonly ConcurrentQueue<Command> _commands = new ConcurrentQueue<Command>();

        struct Command
        {
            public string Path;
            public string Body;
        }

        // Built on the main thread, handed to the listener thread as finished strings, same as _snapshot.
        volatile string _tunables = "{\"tunables\":[]}";
        bool _tunablesDirty = true;
        int _lastTunableCount = -1;

        void Awake()
        {
            _trialsDir = DataLogger.TrialsDir;

            // Start capturing the log immediately, not in Start: the interesting lines (the receiver coming
            // up, the eye tracker's permission dance, a missing profile) all happen during startup, and a ring
            // that only begins recording once the socket is bound would miss exactly the failures you open the
            // dev log to diagnose.
            StudyProfile cfg = StudyProfile.Active;
            LogRing.Install(cfg != null ? cfg.devLogBufferLines : 800);

            // The tunable list changes only when a dev-window edit / Load / Reset fires, so rebuild it then
            // rather than every frame: it is a few hundred rows of string formatting.
            DevSettingsStore.OnChanged += MarkTunablesDirty;

            // The dashboard ships as a TextAsset in Resources, NOT in StreamingAssets, even though the plan
            // doc says StreamingAssets. On Android, StreamingAssets lives inside the compressed APK and
            // File.ReadAllText on it returns nothing: it works perfectly in the Editor and serves a blank
            // page on the device. A TextAsset is compiled in and readable from memory on both.
            var asset = Resources.Load<TextAsset>("dashboard_html");
            if (asset != null) _html = asset.text;
            else Debug.LogWarning($"{Tag} Resources/dashboard_html.txt not found. The API will still serve, " +
                                  "but the dashboard page will be a placeholder.");
        }

        void Start()
        {
            if (!enableWebServer) { Debug.Log($"{Tag} Disabled."); return; }
            LocalIp = FindLocalIPv4();
            StartServer();
        }

        void MarkTunablesDirty() => _tunablesDirty = true;

        void Update()
        {
            // The log ring is fed from every thread, but it needs a main-thread clock to stamp lines with, and
            // it keeps recording even with the server off (so a no-server run still has a log to pull).
            LogRing.Pump(Time.realtimeSinceStartup);

            if (!_running) return;

            StatusSnapshot.Refresh(Time.unscaledDeltaTime);

            // Mirror the profile flag for the listener thread, which must never read StudyProfile itself.
            _logRequests = StudyCfg != null && StudyCfg.serverLogRequests;

            _snapTimer -= Time.unscaledDeltaTime;
            if (_snapTimer <= 0f)
            {
                _snapTimer = 1f / Mathf.Max(1f, SnapshotHz);
                _snapshot = StatusSnapshot.Build(LocalIp, port);
            }

            // Registration happens as elements spawn (an ArUco tag places the map mid-session), so the list
            // grows after startup. Rebuild when the store changes, or while it is still filling.
            if (_tunablesDirty || DevSettingsStore.Tunables.Count != _lastTunableCount)
            {
                _tunablesDirty = false;
                _lastTunableCount = DevSettingsStore.Tunables.Count;
                _tunables = StatusSnapshot.BuildTunables();
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
            DevSettingsStore.OnChanged -= MarkTunablesDirty;
            LogRing.Uninstall();
            StopServer();
        }

        void OnApplicationQuit() => StopServer();

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
                // "Prefix already in use" almost always means a SECOND listener inside THIS process (a
                // leftover probe component, or two ExperimentServers across additively loaded scenes), not a
                // stale socket. Force-stopping the app does not help with that, so say so: the first guess
                // sends you chasing the wrong thing.
                Debug.LogError($"{Tag} Failed to start on port {port}: {e.Message}\n" +
                               $"{Tag} If this is 'Prefix already in use': something else in THIS app is already " +
                               $"bound to {port}. Check for a leftover probe component or a duplicate ExperimentServer " +
                               "(additive scene loading makes duplicates easy). Only if that is clean is it a stale socket.");
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
            if (_logRequests) Debug.Log($"{Tag} {ctx.Request.HttpMethod} {path}");

            switch (path)
            {
                case "/":
                case "/index.html":
                    Send(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(
                        string.IsNullOrEmpty(_html) ? "<h1>AR Cockpit</h1><p>Dashboard asset missing.</p>" : _html));
                    return;

                // The plan doc calls it /api/telemetry; /api/status is the same blob under a name that
                // matches what it actually carries (readiness plus telemetry). Both work.
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

                case "/api/ping":
                    Send(ctx, 200, "text/plain", Encoding.UTF8.GetBytes("ok"));
                    return;

                // ---- DEV endpoints (read-only) ----------------------------------------------------------
                // The point of these is to make `adb logcat -s Unity | Select-String "\[FDCmd\]"` unnecessary
                // on run day, when there is no PC tethered to the headset. They only ever READ: nothing here
                // can start a trial, arm the writer, or change a tunable.

                // /api/logs?since=<cursor>&tag=<TAG>&max=<n>
                // LogRing is plain .NET behind a lock, so it is safe to serve straight from this thread.
                case "/api/logs":
                {
                    long since = ParseLong(ctx.Request.QueryString["since"], 0);
                    string tag = ctx.Request.QueryString["tag"];
                    int max = (int)ParseLong(ctx.Request.QueryString["max"], 300);
                    SendJson(ctx, LogRing.ToJson(since, tag, Mathf.Clamp(max, 1, 2000)));
                    return;
                }

                // The filter buttons the dashboard offers, derived from what the app is ACTUALLY logging, so
                // they can never go stale against a renamed subsystem.
                case "/api/logtags":
                {
                    var sb = new StringBuilder("{\"tags\":[");
                    var tags = LogRing.Tags();
                    for (int i = 0; i < tags.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(tags[i].Replace("\"", "")).Append('"');
                    }
                    SendJson(ctx, sb.Append("]}").ToString());
                    return;
                }

                // The whole live dev-tunable registry. If a value is not in here, it is NOT reachable on
                // device: this list is the honest answer to "is everything exposed?".
                case "/api/tunables":
                    SendJson(ctx, _tunables);
                    return;
            }

            // Control endpoints: /api/trial/start, /stop, /pause, /resume, /redo, /identify, /capture.
            // Accepted here and executed on the main thread next Update. Deliberately permissive about the
            // verb so the dashboard, a curl, or a browser address bar can all drive it during bring-up.
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

        // System.IO is plain .NET, safe off the main thread. _trialsDir was cached in Awake precisely so we
        // never have to ask Unity for persistentDataPath from here.
        //
        // Recursive, because trials are filed into valid/ invalid/ _incomplete/. The folder is reported per
        // file so the dashboard can group them: an _incomplete file is a crashed run and must never be
        // mistaken for a good one just because it appears in a flat list.
        string ListFiles()
        {
            var sb = new StringBuilder("{\"dir\":\"");
            sb.Append(_trialsDir.Replace("\\", "/")).Append("\",\"files\":[");
            try
            {
                if (Directory.Exists(_trialsDir))
                {
                    var files = Directory.GetFiles(_trialsDir, "*.csv", SearchOption.AllDirectories);
                    Array.Sort(files);
                    bool first = true;
                    foreach (var f in files)
                    {
                        var info = new FileInfo(f);
                        string rel = f.Substring(_trialsDir.Length).TrimStart('/', '\\').Replace("\\", "/");
                        int slash = rel.LastIndexOf('/');
                        string folder = slash < 0 ? "" : rel.Substring(0, slash);

                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"path\":\"").Append(rel)
                          .Append("\",\"name\":\"").Append(info.Name)
                          .Append("\",\"folder\":\"").Append(folder)
                          .Append("\",\"bytes\":").Append(info.Length).Append('}');
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"{Tag} Listing failed: {e.Message}"); }
            return sb.Append("]}").ToString();
        }

        void SendFile(HttpListenerContext ctx, string relPath)
        {
            // The path now has a folder in it (valid/foo.csv), so GetFileName alone will not do. Resolve to
            // a full path and require it to still be INSIDE the trials directory: that defeats "../../.."
            // regardless of how it was encoded.
            string full;
            try { full = Path.GetFullPath(Path.Combine(_trialsDir, relPath ?? "")); }
            catch { full = ""; }

            bool inside = !string.IsNullOrEmpty(full) &&
                          full.StartsWith(Path.GetFullPath(_trialsDir), StringComparison.Ordinal);

            if (!inside || !File.Exists(full))
            {
                Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("no such file: " + relPath));
                return;
            }

            byte[] bytes = File.ReadAllBytes(full);
            ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(full)}\"");
            Send(ctx, 200, "text/csv", bytes);
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

        /// <summary>The headset's own IPv4, for the colleague's hotspot case where the phone needs an
        /// address to browse to. Empty on a pure USB tether, which is fine: that path uses adb forward.</summary>
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
