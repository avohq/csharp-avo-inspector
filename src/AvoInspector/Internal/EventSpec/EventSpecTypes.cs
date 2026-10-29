using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Avo.Inspector.Internal.EventSpec
{
    /// <summary>
    /// The value constraints the event spec sets on one property. Each constraint maps a key (a
    /// pinned value, a JSON array of allowed values, a regex pattern, or a <c>"min,max"</c> range) to
    /// the tracking-plan event IDs (base event or variant) that require it, so one property can carry
    /// different rules for different variants.
    /// </summary>
    internal sealed class PropertyConstraints
    {
        /// <summary>Tracking-plan type name (<c>"string"</c>, <c>"int"</c>, <c>"bool"</c>…). Not validated.</summary>
        public string Type { get; set; } = "unknown";

        /// <summary>Whether a <c>null</c> or missing value is checked against the constraints.</summary>
        public bool Required { get; set; }

        /// <summary>True when the property is a list of <see cref="Type"/>.</summary>
        public bool IsList { get; set; }

        public Dictionary<string, List<string>>? PinnedValues { get; set; }
        public Dictionary<string, List<string>>? AllowedValues { get; set; }
        public Dictionary<string, List<string>>? RegexPatterns { get; set; }
        public Dictionary<string, List<string>>? MinMaxRanges { get; set; }

        /// <summary>Constraints on the properties of an object, or of each object in a list.</summary>
        public Dictionary<string, PropertyConstraints>? Children { get; set; }
    }

    /// <summary>One tracking-plan event matching the requested name: its base event and variants.</summary>
    internal sealed class EventSpecEntry
    {
        public string BranchId { get; set; } = string.Empty;
        public string BaseEventId { get; set; } = string.Empty;
        public List<string> VariantIds { get; set; } = new List<string>();

        /// <summary>Only the properties that carry value constraints.</summary>
        public Dictionary<string, PropertyConstraints> Props { get; set; } =
            new Dictionary<string, PropertyConstraints>();
    }

    /// <summary>
    /// Identifies the spec a validated event was checked against. Sent on the track body as
    /// <c>eventSpecMetadata</c>, so its wire names are fixed here.
    /// </summary>
    internal sealed class EventSpecMetadata
    {
        [JsonPropertyName("schemaId")] public string SchemaId { get; set; } = string.Empty;
        [JsonPropertyName("branchId")] public string BranchId { get; set; } = string.Empty;
        [JsonPropertyName("latestActionId")] public string LatestActionId { get; set; } = string.Empty;
        [JsonPropertyName("sourceId")] public string SourceId { get; set; } = string.Empty;
    }

    /// <summary>
    /// A parsed <c>/trackingPlan/eventSpec</c> response. <see cref="Events"/> is empty when the
    /// endpoint knows no event by that name, in which case nothing is validated.
    /// </summary>
    internal sealed class EventSpecResponse
    {
        public List<EventSpecEntry> Events { get; set; } = new List<EventSpecEntry>();
        public EventSpecMetadata Metadata { get; set; } = new EventSpecMetadata();
    }

    /// <summary>
    /// The validation outcome for one property: the tracking-plan event IDs whose constraints the
    /// value breaks, and the outcome for the object's properties when it has any.
    /// </summary>
    internal sealed class PropertyValidationOutcome
    {
        public PropertyValidationOutcome(List<string> failedEventIds, Dictionary<string, PropertyValidationOutcome>? children = null)
        {
            FailedEventIds = failedEventIds;
            Children = children;
        }

        public List<string> FailedEventIds { get; }
        public Dictionary<string, PropertyValidationOutcome>? Children { get; }
    }

    /// <summary>The result of validating one event: the spec's metadata and the failed properties.</summary>
    internal sealed class EventValidationResult
    {
        public EventValidationResult(EventSpecMetadata metadata, Dictionary<string, PropertyValidationOutcome> failedProperties)
        {
            Metadata = metadata;
            FailedProperties = failedProperties;
        }

        public EventSpecMetadata Metadata { get; }

        /// <summary>Property name → outcome, for the properties that failed for at least one event ID.</summary>
        public Dictionary<string, PropertyValidationOutcome> FailedProperties { get; }
    }
}
