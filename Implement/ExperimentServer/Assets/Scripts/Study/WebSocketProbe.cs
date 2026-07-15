using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ARCockpit.Study
{
    /// <summary>
    /// Answers whether System.Net.WebSockets.ClientWebSocket works on the ML2 under IL2CPP.
    ///
    /// The XP12Receiver's WebSocket transport failed on device ("Unable to connect") and we assumed an
    /// IL2CPP limitation, but that test could never distinguish three different failures: a broken
    /// ClientWebSocket, a broken adb reverse tunnel, or X-Plane not serving. So this probe removes the
    /// network from the question entirely.
    ///
    /// STAGE 1 (self-contained, always runs): the app hosts a minimal WebSocket server on a loopback port
    /// (raw TcpListener + the RFC 6455 handshake and framing, about as small as it gets) and connects to
    /// it with ClientWebSocket. If a text message round-trips, ClientWebSocket WORKS under IL2CPP and the
    /// original failure was the network, not the runtime.
    ///
    /// STAGE 2 (optional, needs the tunnel + sim): connect ClientWebSocket to the real X-Plane endpoint.
    /// Only meaningful with 'adb reverse tcp:8086 tcp:8086' up and X-Plane running.
    ///
    /// The FALLBACK if stage 1 fails is the one we already ship: the receiver's REST polling path, which
    /// works on device today. Nothing new is needed for it, so a red result here costs us nothing.
    ///
    ///     adb logcat -d -s Unity | Select-String "\[WSPROBE\]"
    /// </summary>
    public class WebSocketProbe : MonoBehaviour
    {
        [Tooltip("Loopback port for the self-contained stage 1 test. Any free port above 1024.")]
        public int loopbackPort = 8090;

        [Tooltip("Also try the real X-Plane WebSocket. Needs 'adb reverse tcp:8086 tcp:8086' and a running sim.")]
        public bool testXPlane = false;
        [Tooltip("X-Plane WebSocket endpoint for stage 2.")]
        public string xplaneUrl = "ws://localhost:8086/api/v3";

        [Tooltip("Seconds to wait on each connect / receive before calling it a failure.")]
        public float timeoutSeconds = 8f;

        public bool runOnStart = true;

        const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        TcpListener _server;
        Thread _serverThread;
        volatile string _serverStatus = "not started";

        void Start()
        {
            if (runOnStart) StartCoroutine(Probe());
        }

        public IEnumerator Probe()
        {
            Debug.Log("[WSPROBE] ===== start (ClientWebSocket under IL2CPP) =====");

            // ---- stage 1: loopback round trip, no network involved ----
            bool serverUp = StartLoopbackServer();
            bool stage1 = false;

            if (serverUp)
            {
                var task = RoundTrip($"ws://127.0.0.1:{loopbackPort}/", "hello-ml2");
                yield return WaitFor(task);

                if (task.Status == TaskStatus.RanToCompletion && task.Result == "echo:hello-ml2")
                {
                    stage1 = true;
                    Debug.Log($"[WSPROBE] STAGE 1 PASS: loopback round trip returned '{task.Result}'. " +
                              "ClientWebSocket WORKS under IL2CPP.");
                }
                else
                {
                    string why = task.Exception != null
                        ? Flatten(task.Exception)
                        : $"status={task.Status} result='{task.Result}'";
                    Debug.LogWarning($"[WSPROBE] STAGE 1 FAIL: {why}");
                }

                Debug.Log($"[WSPROBE] loopback server said: {_serverStatus}");
                StopLoopbackServer();
            }
            else
            {
                Debug.LogWarning("[WSPROBE] STAGE 1 SKIPPED: could not host the loopback server.");
            }

            // ---- stage 2: the real sim ----
            if (testXPlane)
            {
                var task = Connect(xplaneUrl);
                yield return WaitFor(task);

                if (task.Status == TaskStatus.RanToCompletion)
                    Debug.Log($"[WSPROBE] STAGE 2 PASS: connected to {xplaneUrl}. The sim's WebSocket is reachable.");
                else
                    Debug.LogWarning($"[WSPROBE] STAGE 2 FAIL: {xplaneUrl} -> {Flatten(task.Exception)}");
            }

            string verdict = stage1
                ? "ClientWebSocket is fine on device. The old XP12 WebSocket failure was the NETWORK " +
                  "(tunnel or sim), not IL2CPP. Retry the receiver's WS transport with the tunnel up."
                : "ClientWebSocket does NOT work under IL2CPP here. Stay on the REST polling fallback, " +
                  "or bring in a native WebSocket lib if we ever truly need push.";
            Debug.Log($"[WSPROBE] ===== VERDICT: {verdict} =====");
        }

        // ---------- client ----------

        async Task<string> RoundTrip(string url, string message)
        {
            using (var ws = new ClientWebSocket())
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                await ws.ConnectAsync(new Uri(url), cts.Token);

                var payload = new ArraySegment<byte>(Encoding.UTF8.GetBytes(message));
                await ws.SendAsync(payload, WebSocketMessageType.Text, true, cts.Token);

                var buffer = new ArraySegment<byte>(new byte[1024]);
                WebSocketReceiveResult result = await ws.ReceiveAsync(buffer, cts.Token);
                return Encoding.UTF8.GetString(buffer.Array, 0, result.Count);
            }
        }

        async Task Connect(string url)
        {
            using (var ws = new ClientWebSocket())
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                await ws.ConnectAsync(new Uri(url), cts.Token);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe done", CancellationToken.None);
            }
        }

        // A Task is not a coroutine, so we spin until it settles rather than blocking the main thread.
        IEnumerator WaitFor(Task task)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds + 2f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
                yield return null;
        }

        static string Flatten(AggregateException ex)
        {
            if (ex == null) return "timed out (no exception)";
            var inner = ex.Flatten().InnerException;
            return inner != null ? $"{inner.GetType().Name}: {inner.Message}" : ex.Message;
        }

        // ---------- minimal RFC 6455 server (loopback only) ----------

        bool StartLoopbackServer()
        {
            try
            {
                _server = new TcpListener(IPAddress.Loopback, loopbackPort);
                _server.Start();
                _serverThread = new Thread(ServerLoop) { IsBackground = true };
                _serverThread.Start();
                Debug.Log($"[WSPROBE] loopback WebSocket server listening on ws://127.0.0.1:{loopbackPort}/");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WSPROBE] loopback server failed to start: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        void ServerLoop()
        {
            try
            {
                using (var client = _server.AcceptTcpClient())
                using (var stream = client.GetStream())
                {
                    if (!Handshake(stream)) return;

                    string message = ReadTextFrame(stream);
                    if (message == null) return;

                    WriteTextFrame(stream, "echo:" + message);
                    _serverStatus = $"handshook and echoed '{message}'";
                }
            }
            catch (SocketException) { }        // listener stopped, expected on teardown
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _serverStatus = $"error {ex.GetType().Name}: {ex.Message}";
            }
        }

        // The RFC 6455 opening handshake: echo back a SHA-1 of the client's key plus the magic GUID.
        static bool Handshake(NetworkStream stream)
        {
            var buffer = new byte[2048];
            int read = stream.Read(buffer, 0, buffer.Length);
            string request = Encoding.UTF8.GetString(buffer, 0, read);

            string key = null;
            foreach (string line in request.Split('\n'))
            {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                {
                    key = line.Substring("Sec-WebSocket-Key:".Length).Trim();
                    break;
                }
            }
            if (key == null) return false;

            string accept;
            using (var sha1 = SHA1.Create())
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.UTF8.GetBytes(key + WebSocketGuid)));

            string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                              "Upgrade: websocket\r\n" +
                              "Connection: Upgrade\r\n" +
                              $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            byte[] bytes = Encoding.UTF8.GetBytes(response);
            stream.Write(bytes, 0, bytes.Length);
            return true;
        }

        // Client-to-server frames are always masked. Payloads here are tiny, so only the 7-bit length
        // case is handled; this is a probe, not a production server.
        static string ReadTextFrame(NetworkStream stream)
        {
            var header = new byte[2];
            if (!ReadExactly(stream, header, 2)) return null;

            bool masked = (header[1] & 0b1000_0000) != 0;
            int length = header[1] & 0b0111_1111;
            if (length > 125) return null;

            var mask = new byte[4];
            if (masked && !ReadExactly(stream, mask, 4)) return null;

            var payload = new byte[length];
            if (!ReadExactly(stream, payload, length)) return null;

            if (masked)
                for (int i = 0; i < length; i++) payload[i] = (byte)(payload[i] ^ mask[i % 4]);

            return Encoding.UTF8.GetString(payload);
        }

        static void WriteTextFrame(NetworkStream stream, string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            var frame = new byte[2 + payload.Length];
            frame[0] = 0b1000_0001;             // FIN + text opcode
            frame[1] = (byte)payload.Length;    // server frames are not masked
            Array.Copy(payload, 0, frame, 2, payload.Length);
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }

        static bool ReadExactly(NetworkStream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        void StopLoopbackServer()
        {
            try { _server?.Stop(); } catch { }
            _server = null;
            _serverThread = null;
        }

        void OnDestroy() => StopLoopbackServer();
    }
}
