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
    /// Records request bodies (gunzipping when <c>Content-Encoding: gzip</c>) and returns a
    /// configurable status/body. Set the SDK's endpoint to <see cref="BaseUrl"/> via
    /// <c>AVO_INSPECTOR_MOCK_ENDPOINT</c>.
    /// </summary>
    internal sealed class TestInspectorServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly List<RecordedRequest> _requests = new List<RecordedRequest>();
        private readonly object _gate = new object();
        private int _status = 200;
        private string _body = "{\"samplingRate\":1.0}";
        private volatile bool _running;

        public TestInspectorServer()
        {
            var port = FindFreePort();
            BaseUrl = "http://127.0.0.1:" + port;
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
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
                    // ignore per-request errors in the test mock
                }
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

            return new RecordedRequest(
                Encoding.UTF8.GetString(bodyBytes),
                encoding,
                request.ContentType);
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
            public RecordedRequest(string body, string? contentEncoding, string? contentType)
            {
                Body = body;
                ContentEncoding = contentEncoding;
                ContentType = contentType;
            }

            public string Body { get; }
            public string? ContentEncoding { get; }
            public string? ContentType { get; }
        }
    }
}
