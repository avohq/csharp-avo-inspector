using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// A tiny in-process HTTP mock for the Inspector endpoint, used by the batching/lifecycle tests
    /// that the deterministic fixtures cannot cover (time/idle flush and transient-failure no-requeue).
    /// Records request bodies (gunzipping when <c>Content-Encoding: gzip</c>) and headers, and
    /// returns a configurable status/body. Set the SDK's endpoint to <see cref="BaseUrl"/> via
    /// <c>AVO_INSPECTOR_MOCK_ENDPOINT</c>.
    /// <para>Event spec fetches (<c>GET /trackingPlan/eventSpec</c>, dev and staging only) are
    /// answered separately and recorded in <see cref="SpecRequests"/>, never in
    /// <see cref="Requests"/>: by default with a 404, which the SDK treats as "no spec" and sends the
    /// event without validation, as the conformance mock server does. Configure a spec with
    /// <see cref="ConfigureSpec"/>.</para>
    /// </summary>
    internal sealed class TestInspectorServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly List<RecordedRequest> _requests = new List<RecordedRequest>();
        private readonly object _gate = new object();
        private int _status = 200;
        private string _body = "{\"samplingRate\":1.0}";
        private volatile bool _running;
        private readonly List<string> _specRequests = new List<string>();
        private int _specStatus = 404;
        private string _specBody = string.Empty;
        private int _specDelayMs;

        public TestInspectorServer()
        {
            // FindFreePort() releases the socket before Start(); retry to avoid a TOCTOU bind race.
            for (var attempt = 0; ; attempt++)
            {
                var port = FindFreePort();
                var baseUrl = "http://127.0.0.1:" + port;
                var listener = new HttpListener();
                listener.Prefixes.Add(baseUrl + "/");
                try
                {
                    listener.Start();
                    BaseUrl = baseUrl;
                    _listener = listener;
                    break;
                }
                catch (HttpListenerException) when (attempt < 4)
                {
                    listener.Close();
                }
            }
            _running = true;
            _ = Task.Run(Loop);
        }

        public string BaseUrl { get; }

        public int RequestCount
        {
            get { lock (_gate) { return _requests.Count; } }
        }

        public IReadOnlyList<RecordedRequest> Requests
        {
            get { lock (_gate) { return _requests.ToArray(); } }
        }

        /// <summary>Path and query of each event spec fetch, in arrival order.</summary>
        public IReadOnlyList<string> SpecRequests
        {
            get { lock (_gate) { return _specRequests.ToArray(); } }
        }

        /// <summary>Answers event spec fetches with this status and body, after <paramref name="delayMs"/>.</summary>
        public void ConfigureSpec(int status, string body, int delayMs = 0)
        {
            lock (_gate)
            {
                _specStatus = status;
                _specBody = body;
                _specDelayMs = delayMs;
            }
        }

        public void Configure(int status, string body)
        {
            lock (_gate)
            {
                _status = status;
                _body = body;
            }
        }

        private async Task Loop()
        {
            while (_running)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return; // listener stopped
                }

                if (context.Request.HttpMethod == "GET"
                    && context.Request.Url != null
                    && context.Request.Url.AbsolutePath == "/trackingPlan/eventSpec")
                {
                    _ = Task.Run(() => AnswerSpec(context));
                    continue;
                }

                try
                {
                    var record = ReadRequest(context.Request);
                    int status;
                    string body;
                    lock (_gate)
                    {
                        _requests.Add(record);
                        status = _status;
                        body = _body;
                    }

                    var payload = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = payload.Length;
                    context.Response.OutputStream.Write(payload, 0, payload.Length);
                    context.Response.OutputStream.Close();
                }
                catch
                {
                    // Abort the response on failure so the SDK's send fails fast instead of
                    // hanging until its 10s timeout.
                    try { context.Response.Abort(); }
                    catch { /* ignore per-request errors in the test mock */ }
                }
            }
        }

        private async Task AnswerSpec(HttpListenerContext context)
        {
            try
            {
                int status;
                string body;
                int delayMs;
                lock (_gate)
                {
                    _specRequests.Add(context.Request.Url!.PathAndQuery);
                    status = _specStatus;
                    body = _specBody;
                    delayMs = _specDelayMs;
                }
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
                var payload = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = payload.Length;
                context.Response.OutputStream.Write(payload, 0, payload.Length);
                context.Response.OutputStream.Close();
            }
            catch
            {
                try { context.Response.Abort(); }
                catch { /* ignore per-request errors in the test mock */ }
            }
        }

        private static RecordedRequest ReadRequest(HttpListenerRequest request)
        {
            byte[] raw;
            using (var ms = new MemoryStream())
            {
                request.InputStream.CopyTo(ms);
                raw = ms.ToArray();
            }

            var encoding = request.Headers["Content-Encoding"];
            var bodyBytes = raw;
            if (string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase))
            {
                using (var input = new MemoryStream(raw))
                using (var gz = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    gz.CopyTo(output);
                    bodyBytes = output.ToArray();
                }
            }

            // Copy the headers into a case-insensitive snapshot: the live HttpListenerRequest is
            // recycled once the response is written, so the collection cannot be held onto.
            var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (string? name in request.Headers.AllKeys)
            {
                if (name != null)
                {
                    headers[name] = request.Headers[name];
                }
            }

            return new RecordedRequest(
                Encoding.UTF8.GetString(bodyBytes),
                encoding,
                request.ContentType,
                headers);
        }

        private static int FindFreePort()
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            return port;
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _listener.Close(); } catch { /* ignore */ }
        }

        internal sealed class RecordedRequest
        {
            private readonly IReadOnlyDictionary<string, string?> _headers;

            public RecordedRequest(
                string body,
                string? contentEncoding,
                string? contentType,
                IReadOnlyDictionary<string, string?> headers)
            {
                Body = body;
                ContentEncoding = contentEncoding;
                ContentType = contentType;
                _headers = headers;
            }

            public string Body { get; }
            public string? ContentEncoding { get; }
            public string? ContentType { get; }

            /// <summary>
            /// The request header of that name (case-insensitive, as HTTP requires), or
            /// <c>null</c> when the request did not carry it.
            /// </summary>
            public string? Header(string name) => _headers.TryGetValue(name, out var value) ? value : null;
        }
    }
}
