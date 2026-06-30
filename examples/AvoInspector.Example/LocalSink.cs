using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Avo.Inspector.Example
{
    /// <summary>
    /// A throwaway loopback HTTP sink used by the <c>--dry-run</c> demo so you can see the exact
    /// payloads the SDK posts — without real credentials or touching the network. It decompresses
    /// gzip bodies and records each request. NOT part of the SDK; example-only.
    /// </summary>
    internal sealed class LocalSink : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly List<Captured> _captured = new List<Captured>();
        private readonly object _gate = new object();
        private volatile bool _running;

        public LocalSink()
        {
            // FreePort() releases the socket before Start(), so the port can be taken in between
            // (TOCTOU). Retry with a fresh port instead of trusting the probed one.
            for (var attempt = 0; ; attempt++)
            {
                var port = FreePort();
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
            _ = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }

        public IReadOnlyList<Captured> Requests
        {
            get { lock (_gate) { return _captured.ToArray(); } }
        }

        private async Task LoopAsync()
        {
            while (_running)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { return; }

                try
                {
                    var captured = Read(context.Request);
                    lock (_gate) { _captured.Add(captured); }

                    var payload = Encoding.UTF8.GetBytes("{\"samplingRate\":1.0}");
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = payload.Length;
                    context.Response.OutputStream.Write(payload, 0, payload.Length);
                    context.Response.OutputStream.Close();
                }
                catch
                {
                    // Always close the response, even on failure — otherwise the SDK's send hangs
                    // until its 10s timeout (turning the dry-run Flush() into a slow stall).
                    try
                    {
                        context.Response.StatusCode = 500;
                        context.Response.Close();
                    }
                    catch { /* ignore in the demo sink */ }
                }
            }
        }

        private static Captured Read(HttpListenerRequest request)
        {
            byte[] raw;
            using (var ms = new MemoryStream())
            {
                request.InputStream.CopyTo(ms);
                raw = ms.ToArray();
            }

            var encoding = request.Headers["Content-Encoding"];
            var bytes = raw;
            if (string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase))
            {
                using var input = new MemoryStream(raw);
                using var gz = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gz.CopyTo(output);
                bytes = output.ToArray();
            }

            return new Captured(
                request.ContentType,
                encoding,
                raw.Length,
                Encoding.UTF8.GetString(bytes));
        }

        private static int FreePort()
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

        internal sealed class Captured
        {
            public Captured(string? contentType, string? contentEncoding, int wireBytes, string jsonBody)
            {
                ContentType = contentType;
                ContentEncoding = contentEncoding;
                WireBytes = wireBytes;
                JsonBody = jsonBody;
            }

            public string? ContentType { get; }
            public string? ContentEncoding { get; }
            public int WireBytes { get; }
            public string JsonBody { get; }
        }
    }
}
