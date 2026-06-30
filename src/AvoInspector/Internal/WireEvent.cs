using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Avo.Inspector.Internal
{
    /// <summary>
    /// A single self-contained event object on the Inspector wire (SPEC.md §7.3). Each batch is a
    /// JSON array of these.
    /// </summary>
    /// <remarks>
    /// <para><b>Deliberate divergence from SPEC.md §3.3/§7.3.1.</b> The spec says <c>sessionId</c>
    /// MUST NOT be sent, but the live Inspector ingestion pipeline silently <i>drops</i> events that
    /// omit <c>sessionId</c> (the request still returns <c>200 {"success":true}</c>, yet the event
    /// never appears on the dashboard). The canonical browser SDK (<c>js-avo-inspector</c>) always
    /// sends <c>sessionId: ""</c>. Verified empirically by field-bisection against the live API:
    /// adding only <c>sessionId: ""</c> to an otherwise spec-shaped body is necessary and sufficient
    /// for ingestion; <c>trackingId</c>/<c>eventId</c>/<c>eventHash</c>/<c>avoFunction</c> are not.
    /// We therefore emit <c>sessionId: ""</c> and continue to omit <c>trackingId</c>/<c>visitorId</c>/
    /// <c>userId</c> (which are not required).</para>
    /// </remarks>
    internal sealed class WireEvent
    {
        [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = string.Empty;
        [JsonPropertyName("appName")] public string AppName { get; set; } = string.Empty;
        [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
        [JsonPropertyName("libVersion")] public string LibVersion { get; set; } = string.Empty;
        [JsonPropertyName("env")] public string Env { get; set; } = string.Empty;
        [JsonPropertyName("libPlatform")] public string LibPlatform { get; set; } = string.Empty;
        [JsonPropertyName("messageId")] public string MessageId { get; set; } = string.Empty;
        [JsonPropertyName("streamId")] public string StreamId { get; set; } = string.Empty;
        // Required by the live backend for ingestion despite SPEC.md §3.3 (see class remarks).
        [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
        [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = string.Empty;
        [JsonPropertyName("samplingRate")] public double SamplingRate { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "event";
        [JsonPropertyName("eventName")] public string EventName { get; set; } = string.Empty;
        [JsonPropertyName("eventProperties")] public IReadOnlyList<SchemaEntry> EventProperties { get; set; }
            = new List<SchemaEntry>();
    }
}
