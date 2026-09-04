using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Avo.Inspector.Internal
{
    /// <summary>Outcome of a single HTTP send (SPEC.md §7.5 error taxonomy).</summary>
    internal enum SendStatus
    {
        /// <summary>200 OK.</summary>
        Ok,

        /// <summary>A non-200 HTTP response (4xx/5xx).</summary>
        Non200,

        /// <summary>A network error or the 10s timeout (swallowed; never re-queued).</summary>
        Error
    }

    /// <summary>Result of a send: the status plus any sampling rate parsed from a 200 body.</summary>
    internal readonly struct SendResult
    {
        public SendResult(SendStatus status, double? newSamplingRate)
        {
            Status = status;
            NewSamplingRate = newSamplingRate;
        }

        public SendStatus Status { get; }
        public double? NewSamplingRate { get; }
    }

    /// <summary>
    /// Performs the Inspector HTTP POST (SPEC.md §7): JSON array body, the §7.2 request headers,
    /// mandatory gzip for bodies ≥ 1024 bytes (§7.3.5), a 10-second timeout (§7.6), and the §7.5
    /// error taxonomy. Never throws — every failure is mapped to <see cref="SendStatus"/>.
    /// </summary>
    internal static class InspectorHttpSender
    {
        private const int GzipThresholdBytes = 1024; // SPEC.md §7.3.5
        private const int RequestTimeoutMs = 10_000;  // SPEC.md §7.6

        // A single shared client; per-request timeout is enforced via CancellationToken, not the
        // client-wide Timeout (which would be shared across concurrent sends).
        private static readonly HttpClient SharedClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            // SchemaEntry uses its own converter (via attribute); WireEvent uses property-name
            // attributes. No camelCase policy needed.
        };

        /// <summary>
        /// Serializes and sends one batch. Returns a <see cref="SendResult"/>; never throws.
        /// </summary>
        /// <param name="endpoint">Resolved endpoint URL (SPEC.md §7.1).</param>
        /// <param name="apiKey">Inspector API key; sent as the REQUIRED <c>api-key</c> header (SPEC.md §7.2).</param>
        /// <param name="env">Environment wire string; sent as the REQUIRED <c>env</c> header (SPEC.md §7.2).</param>
        /// <param name="batch">The events to send as a single JSON array.</param>
        /// <param name="shouldLog">Whether diagnostics may be written to stderr.</param>
        /// <param name="abortToken">Cancelled by <c>Destroy()</c> to abandon an in-flight send.</param>
        public static async Task<SendResult> SendAsync(
            string endpoint,
            string apiKey,
            string env,
            WireEvent[] batch,
            bool shouldLog,
            CancellationToken abortToken = default)
        {
            byte[] rawBytes;
            try
            {
                var json = JsonSerializer.Serialize(batch, SerializerOptions);
                rawBytes = Encoding.UTF8.GetBytes(json);
            }
            catch (Exception ex)
            {
                // Serialization should never fail for valid wire bodies; treat as an internal error.
                Logger.Error(shouldLog, "request serialization failed: " + ex.GetType().Name);
                return new SendResult(SendStatus.Error, null);
            }

            var payload = rawBytes;
            var gzipped = false;
            if (rawBytes.Length >= GzipThresholdBytes)
            {
                if (TryGzip(rawBytes, out var compressed))
                {
                    payload = compressed;
                    gzipped = true;
                }
                // else: fall back to the uncompressed body (SPEC.md §7.3.5).
            }

            using (var timeoutCts = new CancellationTokenSource(RequestTimeoutMs))
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(abortToken, timeoutCts.Token))
            using (var content = new ByteArrayContent(payload))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                if (gzipped)
                {
                    content.Headers.ContentEncoding.Add("gzip");
                }
                // Content-Length is set automatically from the payload byte length.

                using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content })
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    // SPEC.md §7.2 — the unified /inspector/v2/track endpoint reads the credential
                    // and the environment from headers (a missing or invalid one is a 400) and
                    // attributes traffic by sender at the edge via X-Avo-Client, without decoding
                    // the body. apiKey and env stay in the body as well (§7.3): v2 ignores the body
                    // copies, and keeping them holds one body shape and one JSON Schema for every
                    // sender. X-Avo-Client carries this SDK's libPlatform, so the token is "csharp".
                    //
                    // TryAddWithoutValidation rather than Add: Add throws FormatException on a
                    // pathological apiKey, and that would escape SendAsync's "never throws"
                    // contract from outside the try below. Skipping the up-front validation is
                    // safe — the HTTP stack still rejects a header value containing CR/LF when it
                    // writes the request, and that throw lands in the catch below as SendStatus.Error.
                    request.Headers.TryAddWithoutValidation("api-key", apiKey);
                    request.Headers.TryAddWithoutValidation("env", env);
                    request.Headers.TryAddWithoutValidation("X-Avo-Client", InspectorVersion.LibPlatform);

                    try
                    {
                        using (var response = await SharedClient
                            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                            .ConfigureAwait(false))
                        {
                            if ((int)response.StatusCode == 200)
                            {
                                var samplingRate = await TryReadSamplingRateAsync(response).ConfigureAwait(false);
                                return new SendResult(SendStatus.Ok, samplingRate);
                            }

                            // SPEC.md §7.4/§7.5 — non-200: log (dev/staging), resolve, do not re-queue.
                            Logger.Error(shouldLog, "Inspector API returned status " + (int)response.StatusCode);
                            return new SendResult(SendStatus.Non200, null);
                        }
                    }
                    catch (Exception ex)
                    {
                        // SPEC.md §7.5/§7.6 — network error, 10s timeout, or destroy()-abandon.
                        // Swallow; do not re-queue. Never log the request body or apiKey (§7.5.1).
                        string label;
                        if (abortToken.IsCancellationRequested) label = "Request abandoned (destroyed)";
                        else if (ex is OperationCanceledException) label = "Request timed out";
                        else label = "Request failed";
                        Logger.Error(shouldLog, "send failed (" + label + ")");
                        return new SendResult(SendStatus.Error, null);
                    }
                }
            }
        }

        private static bool TryGzip(byte[] raw, out byte[] compressed)
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        gz.Write(raw, 0, raw.Length);
                    }
                    compressed = ms.ToArray();
                    return true;
                }
            }
            catch
            {
                compressed = raw;
                return false;
            }
        }

        private static async Task<double?> TryReadSamplingRateAsync(HttpResponseMessage response)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(body))
                {
                    return null;
                }
                using (var doc = JsonDocument.Parse(body))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("samplingRate", out var rate)
                        && rate.ValueKind == JsonValueKind.Number
                        && rate.TryGetDouble(out var value)
                        && value >= 0.0 && value <= 1.0)
                    {
                        return value;
                    }
                }
            }
            catch
            {
                // Ignore body parse errors (SPEC.md §7.4 — only update on a valid numeric value).
            }
            return null;
        }
    }
}
