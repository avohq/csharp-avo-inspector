using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Avo.Inspector.Internal
{
    /// <summary>
    /// A single self-contained event object on the Inspector wire (SPEC.md §7.3). Each batch is a
    /// JSON array of these.
    /// </summary>
    /// <remarks>
    /// <para><b><c>sessionId</c> (SPEC.md §3.3).</b> Every event carries <c>sessionId: ""</c>, which
    /// the spec has REQUIRED of server SDKs since v2.0.0: the live Inspector ingestion pipeline
    /// silently <i>drops</i> events that omit <c>sessionId</c> (the request still returns
    /// <c>200 {"success":true}</c>, yet the event never appears on the dashboard). Verified
    /// empirically by field-bisection against the live API: adding only <c>sessionId: ""</c> to an
    /// otherwise spec-shaped body is necessary and sufficient for ingestion;
    /// <c>trackingId</c>/<c>eventId</c>/<c>eventHash</c>/<c>avoFunction</c> are not. This SDK is
    /// therefore conformant, not divergent — it emits <c>sessionId: ""</c> and omits
    /// <c>trackingId</c>/<c>visitorId</c>/<c>userId</c>, as SPEC.md §3.3 requires.</para>
    /// <para><b>Gateway coordinate fields (SPEC.md §4.2.1 / §7.3.6; AVO-3516).</b>
    /// <see cref="OutputReference"/> and <see cref="OriginHint"/> are OPTIONAL top-level siblings
    /// of <c>eventProperties</c> for gateway-scoped API keys (see <see cref="TrackOptions"/>),
    /// omitted entirely when absent. <see cref="AppVersion"/> is therefore also nullable — per the
    /// §7.3.6 resolution table, with <c>originHint</c> set and no usable per-event app version,
    /// <c>appVersion</c> is sent as a literal JSON <c>null</c> rather than falling back to the
    /// instance's configured version; the <c>/inspector/v2/track</c> endpoint decodes both
    /// coordinates and stores a <c>null</c> <c>appVersion</c> as <c>"unversioned"</c>.</para>
    /// <para><b><c>apiKey</c> and <c>env</c> are also sent as request headers</b> (SPEC.md §7.2).
    /// The v2 endpoint reads them from the headers and ignores these body copies, but they stay in
    /// the body so one body shape and one JSON Schema serve every Inspector sender.</para>
    /// </remarks>
    internal sealed class WireEvent
    {
        [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = string.Empty;
        [JsonPropertyName("appName")] public string AppName { get; set; } = string.Empty;
        [JsonPropertyName("appVersion")] public string? AppVersion { get; set; }
        [JsonPropertyName("libVersion")] public string LibVersion { get; set; } = string.Empty;
        [JsonPropertyName("env")] public string Env { get; set; } = string.Empty;
        [JsonPropertyName("libPlatform")] public string LibPlatform { get; set; } = string.Empty;
        [JsonPropertyName("messageId")] public string MessageId { get; set; } = string.Empty;
        [JsonPropertyName("streamId")] public string StreamId { get; set; } = string.Empty;
        // REQUIRED on the wire as "" for server SDKs (SPEC.md §3.3; see class remarks).
        [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
        [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = string.Empty;
        [JsonPropertyName("samplingRate")] public double SamplingRate { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "event";
        [JsonPropertyName("eventName")] public string EventName { get; set; } = string.Empty;
        [JsonPropertyName("eventProperties")] public IReadOnlyList<SchemaEntry> EventProperties { get; set; }
            = new List<SchemaEntry>();

        // Gateway coordinate fields (SPEC.md §7.3.6, see class remarks). Individually
        // JsonIgnore'd rather than a global DefaultIgnoreCondition, so appVersion: null above
        // remains serialized.
        [JsonPropertyName("outputReference")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OutputReference { get; set; }

        [JsonPropertyName("originHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OriginHint { get; set; }
    }
}
