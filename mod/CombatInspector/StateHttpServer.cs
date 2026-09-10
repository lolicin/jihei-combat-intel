using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CombatInspector
{
    /// <summary>
    /// A request that must be satisfied on the Unity main thread, because touching the ECS world
    /// from anywhere else would trip the job safety system.
    /// </summary>
    public sealed class MainThreadRequest
    {
        public string Kind;                 // "snapshot" | "deep" | "dump"
        public int Enemies = 8;
        public int Depth = 4;
        public string ResultJson;
        public string Error;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    /// <summary>
    /// Minimal HTTP/1.1 server on 127.0.0.1 built on TcpListener.
    ///
    /// Deliberately not HttpListener: raw sockets avoid the Windows URL-ACL / administrator
    /// requirements and any net472-vs-Unity-Mono API surface differences, so this works the same
    /// under BepInEx 5 and 6 and under any Unity Mono profile.
    ///
    /// The serving thread never touches the ECS world. It either replays the last snapshot captured
    /// on the main thread, or queues a <see cref="MainThreadRequest"/> and waits for the Runner to
    /// fulfil it during Update().
    /// </summary>
    public sealed class StateHttpServer : IDisposable
    {
        public ConcurrentQueue<MainThreadRequest> Pending { get; } = new ConcurrentQueue<MainThreadRequest>();

        public int Port { get; private set; }
        public bool IsRunning { get; private set; }
        public int RequestsServed;

        public Action<string> Log = _ => { };

        /// <summary>Last JSON produced on the main thread. Read by GET /state without any roundtrip.</summary>
        public volatile string LatestStateJson = "{}";

        /// <summary>The embedded web dashboard served at "/" (set by Runner from the assembly resource).</summary>
        public string DashboardHtml;

        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _stop;
        private readonly int _mainThreadWaitMs;

        public StateHttpServer(int port, int mainThreadWaitMs = 3000)
        {
            Port = port;
            _mainThreadWaitMs = mainThreadWaitMs;
        }

        public bool Start()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
            }
            catch (Exception ex)
            {
                Log("could not listen on 127.0.0.1:" + Port + " - " + ex.Message);
                return false;
            }

            _stop = false;
            IsRunning = true;
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "CombatInspectorHttp" };
            _thread.Start();
            Log("HTTP endpoint listening on http://127.0.0.1:" + Port + "/");
            return true;
        }

        private void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch
                {
                    if (_stop) break;
                    continue;
                }

                try { HandleClient(client); }
                catch (Exception ex) { Log("request failed: " + ex.Message); }
                finally { try { client.Close(); } catch { } }
            }
        }

        private void HandleClient(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 10000;
            client.NoDelay = true;

            using (var stream = client.GetStream())
            {
                string head = ReadHead(stream);
                if (string.IsNullOrEmpty(head)) return;

                string[] lines = head.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;

                string[] parts = lines[0].Split(' ');
                if (parts.Length < 2) return;

                string method = parts[0].ToUpperInvariant();
                string rawTarget = parts[1];

                if (method == "OPTIONS")
                {
                    Write(stream, 204, "text/plain", "", extraCorsPreflight: true);
                    return;
                }

                string path = rawTarget;
                string query = "";
                int qi = rawTarget.IndexOf('?');
                if (qi >= 0) { path = rawTarget.Substring(0, qi); query = rawTarget.Substring(qi + 1); }
                path = path.TrimEnd('/');
                if (path.Length == 0) path = "/";

                var qs = ParseQuery(query);
                string body;
                string contentType;
                int status = 200;

                switch (path)
                {
                    case "/health":
                        body = "{\"ok\":true,\"port\":" + Port + ",\"requestsServed\":" + RequestsServed +
                               ",\"time\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\"}";
                        contentType = "application/json";
                        break;

                    case "/":
                    case "/index.html":
                    case "/dashboard":
                        if (!string.IsNullOrEmpty(DashboardHtml))
                        {
                            body = DashboardHtml;
                            contentType = "text/html";
                        }
                        else
                        {
                            body = IndexText();
                            contentType = "text/plain";
                        }
                        break;

                    case "/help":
                        body = IndexText();
                        contentType = "text/plain";
                        break;

                    case "/state":
                        body = LatestStateJson ?? "{}";
                        contentType = "application/json";
                        break;

                    case "/snapshot":
                    {
                        var req = new MainThreadRequest { Kind = "snapshot" };
                        body = RunOnMainThread(req, stream) ? req.ResultJson : ErrorJson(req);
                        contentType = "application/json";
                        break;
                    }

                    case "/deep":
                    {
                        var req = new MainThreadRequest
                        {
                            Kind = "deep",
                            Enemies = ParseInt(qs, "enemies", 8),
                            Depth = ParseInt(qs, "depth", 4)
                        };
                        bool ok = RunOnMainThread(req, stream);
                        body = ok ? (req.ResultJson ?? ErrorJson(req)) : ErrorJson(req);
                        contentType = "application/json";
                        break;
                    }

                    case "/dump":
                    {
                        var req = new MainThreadRequest { Kind = "dump" };
                        body = RunOnMainThread(req, stream) ? req.ResultJson : ErrorJson(req);
                        contentType = "application/json";
                        break;
                    }

                    default:
                        status = 404;
                        body = "{\"error\":\"unknown path\",\"path\":\"" + JsonEscape(path) + "\"}";
                        contentType = "application/json";
                        break;
                }

                Interlocked.Increment(ref RequestsServed);
                Write(stream, status, contentType, body);
            }
        }

        private bool RunOnMainThread(MainThreadRequest req, NetworkStream stream)
        {
            Pending.Enqueue(req);
            return req.Done.Wait(_mainThreadWaitMs);
        }

        private string ErrorJson(MainThreadRequest req)
        {
            string msg = string.IsNullOrEmpty(req.Error)
                ? "main thread did not answer within " + _mainThreadWaitMs + "ms (is the game running and unpaused?)"
                : req.Error;
            return "{\"error\":\"" + JsonEscape(msg) + "\"}";
        }

        private string IndexText()
        {
            var sb = new StringBuilder();
            sb.Append("CombatInspector - live combat state reader\n\n");
            sb.Append("GET /                           web dashboard (open this in a browser)\n");
            sb.Append("GET /health                     liveness probe\n");
            sb.Append("GET /state                      last snapshot captured on the main thread (cheap)\n");
            sb.Append("GET /snapshot                   force a fresh capture now and return it\n");
            sb.Append("GET /deep?enemies=8&depth=4     reflective dump of EVERY component of EVERY field\n");
            sb.Append("GET /dump                       write snapshot + deep dump to the output folder\n\n");
            sb.Append("All fields are the game's own ECS component data, read live.\n");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- plumbing

        private static string ReadHead(NetworkStream stream)
        {
            var buf = new List<byte>(512);
            var one = new byte[1];
            int guard = 0;
            while (guard++ < 16384)
            {
                int n;
                try { n = stream.Read(one, 0, 1); } catch { break; }
                if (n <= 0) break;
                buf.Add(one[0]);
                int c = buf.Count;
                if (c >= 4 && buf[c - 4] == 13 && buf[c - 3] == 10 && buf[c - 2] == 13 && buf[c - 1] == 10)
                    break;
            }
            if (buf.Count == 0) return null;
            return Encoding.UTF8.GetString(buf.ToArray());
        }

        private static void Write(NetworkStream stream, int status, string contentType, string body, bool extraCorsPreflight = false)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(StatusText(status)).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            if (extraCorsPreflight)
            {
                sb.Append("Access-Control-Allow-Methods: GET, OPTIONS\r\n");
                sb.Append("Access-Control-Allow-Headers: *\r\n");
            }
            sb.Append("Connection: close\r\n\r\n");

            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            try
            {
                stream.Write(head, 0, head.Length);
                if (payload.Length > 0) stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
            catch { }
        }

        private static string StatusText(int s)
        {
            switch (s)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 404: return "Not Found";
                case 500: return "Internal Server Error";
                default: return "OK";
            }
        }

        private static Dictionary<string, string> ParseQuery(string q)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(q)) return d;
            foreach (var pair in q.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq < 0) d[Uri.UnescapeDataString(pair)] = "";
                else d[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            return d;
        }

        private static int ParseInt(Dictionary<string, string> qs, string key, int fallback)
        {
            string v;
            int n;
            if (qs.TryGetValue(key, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                return n;
            return fallback;
        }

        public static string JsonEscape(string s)
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
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            _stop = true;
            IsRunning = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            try { if (_thread != null && _thread.IsAlive) _thread.Join(500); } catch { }
        }
    }
}
