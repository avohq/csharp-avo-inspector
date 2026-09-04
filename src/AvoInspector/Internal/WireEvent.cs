using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Avo.Inspector.Internal
{
    /// <summary>
    /// A single self-contained event object on the Inspector wire (SPEC.md §7.3). Each batch is a
    /// JSON array of these.
    /// </summary>
    /// <remarks>
    /// <para><b>No <c>sessionId</c> (SPEC.md §3.3).</b> Spec 3.0.0 removed the field from the wire
    /// body: a server SDK has no session to report, and <c>/inspector/v2/track</c> supplies the
    /// value itself. Spec 2.0.0 had REQUIRED it as <c>""</c> because the ingestion pipeline then
    /// dropped events whose body omitted it — answering <c>200 {"success":true}</c> while the event
    /// never reached the dashboard — which is why the field is <i>removed</i> rather than
    /// <i>forbidden</i>: the 3.0.0 schemas still accept a body that carries it, so a sender that has
    /// not regenerated stays valid. Correlation across events belongs in <c>streamId</c>, which is
    /// OPTIONAL and caller-supplied. <c>trackingId</c>, <c>visitorId</c> and <c>userId</c> remain
    /// forbidden outright.</para>
    /// <para><b>Gateway coordinate fields (SPEC.md §4.2.1 / §7.3.6; AVO-3516).</b>
    /// <see cref="OutputReference"/> and <see cref="OriginHint"/> are OPTIONAL top-level siblings
    /// of <c>eventProperties</c> for gateway-scoped API keys, carried by the <c>outputReference</c>
    /// and <c>originHint</c> parameters of
    /// <see cref="AvoInspector.TrackSchemaFromEvent(string, IDictionary{string, object}, string, string, string, string)"/>,
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
