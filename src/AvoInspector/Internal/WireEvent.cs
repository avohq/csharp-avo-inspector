using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Avo.Inspector.Internal
{
    /// <summary>
    /// A single self-contained event object on the Inspector wire (SPEC.md §7.3). Each batch is a
    /// JSON array of these. Only the fields declared here are emitted — the forbidden identifiers
    /// (<c>sessionId</c>, <c>trackingId</c>, <c>visitorId</c>, <c>userId</c>) are never present
    /// (SPEC.md §3.3, §7.3.1).
    /// </summary>
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
        [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = string.Empty;
        [JsonPropertyName("samplingRate")] public double SamplingRate { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "event";
        [JsonPropertyName("eventName")] public string EventName { get; set; } = string.Empty;
        [JsonPropertyName("eventProperties")] public IReadOnlyList<SchemaEntry> EventProperties { get; set; }
            = new List<SchemaEntry>();
    }
}
