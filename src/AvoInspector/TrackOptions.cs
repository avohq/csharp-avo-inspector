namespace Avo.Inspector
{
    /// <summary>
    /// Optional per-call gateway coordinates for <see cref="AvoInspector.TrackSchemaFromEvent(string, System.Collections.Generic.IDictionary{string, object}, string, TrackOptions)"/>
    /// (SPEC.md §4.2.1; AVO-3516). Used when this SDK's Inspector API key is a <b>gateway</b> key
    /// shared across destinations, to label where an observation was taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <c>TrackOptions</c> record of <c>avohq/spec-first-inspector-server-sdk</c>
    /// v2.1.0: the OPTIONAL trailing <c>options</c> parameter of SPEC.md §4.2.1, whose
    /// normalization and wire mapping are normative in SPEC.md §7.3.6 (AVO-3516 / AVO-3543).
    /// Conformance fixtures <c>wire-9</c>-<c>wire-13</c> and <c>batch-7</c> gate this behavior.
    /// </para>
    /// <para>
    /// <b>Thread safety:</b> treat an instance as immutable once passed to
    /// <see cref="AvoInspector.TrackSchemaFromEvent(string, System.Collections.Generic.IDictionary{string, object}, string, TrackOptions)"/>. Its three properties are read once, by
    /// value, the first time <c>options</c> is inspected within that call - inside
    /// <c>BuildWireEvent</c>, after schema extraction, stream-id resolution, and the sampling
    /// check, and not at all if the event is sampled out before <c>BuildWireEvent</c> runs.
    /// Mutating or reusing the same instance across concurrent calls is memory-safe but produces
    /// nondeterministic, caller-surprising wire values rather than a defined result. Construct a
    /// fresh instance per call (or per immutable set of values) instead - or, once passed to one
    /// call, leave it unmutated and it may safely be reused as-is across further calls.
    /// </para>
    /// </remarks>
    public sealed class TrackOptions
    {
        /// <summary>
        /// Reference of the gateway output this observation was bound for (e.g.
        /// <c>"meta-x7k2q"</c>). Leave <c>null</c> for a gateway-level observation not tied to one
        /// output. Trimmed before sending; empty or whitespace-only is treated as absent - the
        /// wire key is omitted, never sent as <c>null</c> or <c>""</c>.
        /// </summary>
        public string? OutputReference { get; set; }

        /// <summary>
        /// Low-cardinality hint identifying the event's upstream source (e.g. <c>"web"</c>,
        /// <c>"ios"</c>, <c>"android"</c>). <b>MUST NOT</b> be a user identifier or any other
        /// high-cardinality value - this is a documentation-only rule; it is not validated at
        /// runtime. Trimmed before sending; empty or whitespace-only is treated as absent. Setting
        /// this makes the event source-scoped - see <see cref="AppVersion"/>.
        /// </summary>
        /// <remarks>
        /// <b>Backend note (AVO-3543):</b> as of this writing, the Inspector backend does not yet
        /// honor <c>outputReference</c> or <c>originHint</c> on this SDK's endpoint
        /// (<c>POST /inspector/v1/track</c>), and does not yet accept a literal wire
        /// <c>appVersion: null</c>. Until the backend is updated, setting <see cref="OriginHint"/>
        /// without a non-blank <see cref="AppVersion"/> override causes this event to be
        /// <b>silently dropped</b> - the HTTP response is still <c>200</c>, but the event never
        /// reaches the Inspector dashboard. What this SDK sends is already the SPEC.md §7.3.6
        /// shape, so nothing here changes when the backend catches up. As SPEC.md §7.3.6 SHOULDs,
        /// the SDK logs one warning per process on that combination - but only while logging is
        /// enabled (the <c>dev</c> default, or an explicit
        /// <see cref="AvoInspector.EnableLogging(bool)"/>); it is silent otherwise. See the README
        /// "Gateways" section and <c>CHANGELOG.md</c>'s <c>1.1.0</c> entry for the same warning.
        /// </remarks>
        public string? OriginHint { get; set; }

        /// <summary>
        /// App version of the source that produced this event, for this event only, resolved per
        /// the SPEC.md §7.3.6 <c>appVersion</c> table.
        /// <para>
        /// With <see cref="OriginHint"/> set, the event is source-scoped, so this
        /// <see cref="AvoInspector"/> instance's configured version never applies: a non-blank
        /// value here is sent (trimmed); a blank or absent value is sent as a literal JSON
        /// <c>null</c>.
        /// </para>
        /// <para>
        /// Without <see cref="OriginHint"/>, a non-blank value here overrides the instance's
        /// configured version (trimmed); a blank or absent value falls back to the instance's
        /// configured version, unchanged.
        /// </para>
        /// </summary>
        public string? AppVersion { get; set; }
    }
}
