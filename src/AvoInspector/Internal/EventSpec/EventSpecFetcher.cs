using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Avo.Inspector.Internal.EventSpec
{
    /// <summary>How a spec fetch ended.</summary>
    internal enum FetchOutcome
    {
        /// <summary>200 with a parseable body (the spec may still have no events).</summary>
        Ok,

        /// <summary>404: the endpoint has no spec for these keys (e.g. a gateway key without <c>originHint</c>).</summary>
        NotFound,

        /// <summary>Any other status, a timeout, a network error, or an unparseable body.</summary>
        Failed,

        /// <summary>Abandoned by <c>Destroy()</c>; nothing is cached.</summary>
        Abandoned
    }

    internal readonly struct FetchResult
    {
        public FetchResult(FetchOutcome outcome, EventSpecResponse? spec)
        {
            Outcome = outcome;
            Spec = spec;
        }

        public FetchOutcome Outcome { get; }
        public EventSpecResponse? Spec { get; }
    }

    /// <summary>
    /// Fetches an event's spec from <c>GET /trackingPlan/eventSpec</c> (a port of the Node SDK's
    /// <c>AvoEventSpecFetcher</c>). Concurrent fetches of the same key share one request. Never
    /// throws: every failure is a <see cref="FetchResult"/>, and the caller sends the event without
    /// validation.
    /// </summary>
    /// <remarks>
    /// The response is read with <see cref="JsonDocument"/>, which needs no reflection, so parsing
    /// works in trimmed and Native AOT host applications. Logs carry the event name, status codes and
    /// error types only, never the API key.
    /// </remarks>
    internal sealed class EventSpecFetcher
    {
        internal const string SpecPath = "/trackingPlan/eventSpec";

        // Nesting the parser follows in a spec's children; deeper levels are left empty. Validation
        // itself stops at the second level (EventValidator), so this only bounds the parse.
        private const int MaxParseDepth = 10;

        /// <summary>Budget for one fetch. Internal and settable for tests only.</summary>
        internal static TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

        private static readonly HttpClient SharedClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly string _apiKey;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Task<FetchResult>> _inFlight =
            new Dictionary<string, Task<FetchResult>>(StringComparer.Ordinal);

        public EventSpecFetcher(string apiKey)
        {
            _apiKey = apiKey;
        }

        /// <summary>
        /// Fetches the spec for <paramref name="key"/> (see <see cref="EventSpecCache.MakeKey"/>),
        /// joining a fetch of the same key already in flight.
        /// </summary>
        /// <param name="key">The fetch's cache key; a fetch of the same key in flight is shared.</param>
        /// <param name="baseUrl">Scheme and authority to send to, without a trailing slash.</param>
        /// <param name="eventName">The event's name.</param>
        /// <param name="streamId">The event's stream id (<c>""</c> when absent).</param>
        /// <param name="originHint">Normalized gateway origin hint, or <c>null</c>.</param>
        /// <param name="outputReference">Normalized gateway output reference, or <c>null</c>.</param>
        /// <param name="shouldLog">Whether diagnostics may be written to stderr.</param>
        /// <param name="abortToken">Cancelled by <c>Destroy()</c> to abandon the fetch.</param>
        public Task<FetchResult> FetchAsync(
            string key,
            string baseUrl,
            string eventName,
            string streamId,
            string? originHint,
            string? outputReference,
            bool shouldLog,
            CancellationToken abortToken)
        {
            lock (_lock)
            {
                if (_inFlight.TryGetValue(key, out var existing))
                {
                    return existing;
                }
                var url = BuildUrl(baseUrl, eventName, streamId, originHint, outputReference);
                // On the thread pool, so no work runs on the caller's synchronization context. The
                // continuation frees the key once the fetch completes, even if it already has.
                var task = Task.Run(() => SendAsync(url, eventName, shouldLog, abortToken), CancellationToken.None);
                _inFlight[key] = task;
                task.ContinueWith(
                    _ =>
                    {
                        lock (_lock)
                        {
                            _inFlight.Remove(key);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return task;
            }
        }

        internal string BuildUrl(string baseUrl, string eventName, string streamId, string? originHint, string? outputReference)
        {
            var url = new StringBuilder(baseUrl);
            url.Append(SpecPath)
                .Append("?apiKey=").Append(Uri.EscapeDataString(_apiKey))
                .Append("&eventName=").Append(Uri.EscapeDataString(eventName))
                .Append("&streamId=").Append(Uri.EscapeDataString(streamId));
            // Appended after the source parameters, so a fetch without coordinates is unchanged.
            if (originHint != null)
            {
                url.Append("&originHint=").Append(Uri.EscapeDataString(originHint));
            }
            if (outputReference != null)
            {
                url.Append("&outputReference=").Append(Uri.EscapeDataString(outputReference));
            }
            return url.ToString();
        }

        private static async Task<FetchResult> SendAsync(string url, string eventName, bool shouldLog, CancellationToken abortToken)
        {
            try
            {
                using (var timeoutCts = new CancellationTokenSource(FetchTimeout))
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(abortToken, timeoutCts.Token))
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using (var response = await SharedClient
                        .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                        .ConfigureAwait(false))
                    {
                        var status = (int)response.StatusCode;
                        if (status != 200)
                        {
                            Logger.Error(shouldLog, "Event spec fetch for \"" + eventName + "\" returned status " + status + "; sending without validation.");
                            return new FetchResult(status == 404 ? FetchOutcome.NotFound : FetchOutcome.Failed, null);
                        }
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var spec = Parse(body);
                        if (spec == null)
                        {
                            Logger.Error(shouldLog, "Event spec response for \"" + eventName + "\" could not be parsed; sending without validation.");
                            return new FetchResult(FetchOutcome.Failed, null);
                        }
                        return new FetchResult(FetchOutcome.Ok, spec);
                    }
                }
            }
            catch (Exception ex)
            {
                if (abortToken.IsCancellationRequested)
                {
                    return new FetchResult(FetchOutcome.Abandoned, null);
                }
                var label = ex is OperationCanceledException ? "timed out" : "failed (" + ex.GetType().Name + ")";
                Logger.Error(shouldLog, "Event spec fetch for \"" + eventName + "\" " + label + "; sending without validation.");
                return new FetchResult(FetchOutcome.Failed, null);
            }
        }

        // ----- wire parsing ----------------------------------------------------------------------

        /// <summary>
        /// Parses the wire format of <c>/trackingPlan/eventSpec</c>:
        /// <c>{ events: [{ b, id, vids, p: { "Prop": { t, r, l?, p?, v?, rx?, minmax?, children? } } }],
        /// metadata: { schemaId, branchId, latestActionId, sourceId } }</c>, where <c>p</c>, <c>v</c>,
        /// <c>rx</c> and <c>minmax</c> each map a constraint key to the event IDs that require it.
        /// Returns <c>null</c> when the body is not a JSON object. Malformed entries, IDs and
        /// constraints are skipped; an entry without a string <c>id</c> has no event to attribute
        /// results to, so it is skipped too.
        /// </summary>
        internal static EventSpecResponse? Parse(string body)
        {
            try
            {
                using (var doc = JsonDocument.Parse(body))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }
                    var response = new EventSpecResponse();
                    if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
                    {
                        response.Metadata = new EventSpecMetadata
                        {
                            SchemaId = StringOrEmpty(metadata, "schemaId"),
                            BranchId = StringOrEmpty(metadata, "branchId"),
                            LatestActionId = StringOrEmpty(metadata, "latestActionId"),
                            SourceId = StringOrEmpty(metadata, "sourceId")
                        };
                    }
                    if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in events.EnumerateArray())
                        {
                            var parsed = ParseEntry(entry);
                            if (parsed != null)
                            {
                                response.Events.Add(parsed);
                            }
                        }
                    }
                    return response;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static EventSpecEntry? ParseEntry(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var parsed = new EventSpecEntry
            {
                BranchId = StringOrEmpty(entry, "b"),
                BaseEventId = id.GetString()!
            };
            if (entry.TryGetProperty("vids", out var vids) && vids.ValueKind == JsonValueKind.Array)
            {
                foreach (var vid in vids.EnumerateArray())
                {
                    if (vid.ValueKind == JsonValueKind.String)
                    {
                        parsed.VariantIds.Add(vid.GetString()!);
                    }
                }
            }
            if (entry.TryGetProperty("p", out var props))
            {
                parsed.Props = ParseConstraintsByName(props, 0);
            }
            return parsed;
        }

        private static Dictionary<string, PropertyConstraints> ParseConstraintsByName(JsonElement wire, int depth)
        {
            var result = new Dictionary<string, PropertyConstraints>(StringComparer.Ordinal);
            if (wire.ValueKind != JsonValueKind.Object || depth >= MaxParseDepth)
            {
                return result;
            }
            foreach (var property in wire.EnumerateObject())
            {
                var constraints = ParseConstraints(property.Value, depth);
                if (constraints != null)
                {
                    result[property.Name] = constraints;
                }
            }
            return result;
        }

        private static PropertyConstraints? ParseConstraints(JsonElement wire, int depth)
        {
            if (wire.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var constraints = new PropertyConstraints
            {
                Type = wire.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "unknown",
                Required = wire.TryGetProperty("r", out var r) && r.ValueKind == JsonValueKind.True,
                IsList = wire.TryGetProperty("l", out var l) && l.ValueKind == JsonValueKind.True,
                PinnedValues = ParseIdMapping(wire, "p"),
                AllowedValues = ParseIdMapping(wire, "v"),
                RegexPatterns = ParseIdMapping(wire, "rx"),
                MinMaxRanges = ParseIdMapping(wire, "minmax")
            };
            if (wire.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Object)
            {
                constraints.Children = ParseConstraintsByName(children, depth + 1);
            }
            return constraints;
        }

        // A constraint key -> event IDs mapping; IDs that are not strings, and keys left with none,
        // are dropped. Null when nothing is left.
        private static Dictionary<string, List<string>>? ParseIdMapping(JsonElement container, string name)
        {
            if (!container.TryGetProperty(name, out var wire) || wire.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            Dictionary<string, List<string>>? result = null;
            foreach (var property in wire.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                var ids = new List<string>();
                foreach (var id in property.Value.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.String)
                    {
                        ids.Add(id.GetString()!);
                    }
                }
                if (ids.Count > 0)
                {
                    result ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    result[property.Name] = ids;
                }
            }
            return result;
        }

        private static string StringOrEmpty(JsonElement container, string name)
            => container.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : string.Empty;
    }
}
