using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Avo.Inspector
{
    /// <summary>
    /// One extracted schema property (SPEC.md §7.3.4, §9.1). Serializes to
    /// <c>{ "propertyName", "propertyType", "children"? }</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Children"/> is a heterogeneous, recursive union (SPEC.md §7.3.4): each element is
    /// either a type string (e.g. <c>"string"</c>), a <see cref="SchemaEntry"/> (for object
    /// properties), or a nested list of those (for list elements). It is modeled as
    /// <see cref="object"/> holding a <c>List&lt;SchemaEntry&gt;</c> (object type) or a
    /// <c>List&lt;object?&gt;</c> (list type).
    /// </para>
    /// <para>
    /// <see cref="Children"/> is present (non-<c>null</c>, possibly empty) iff
    /// <see cref="PropertyType"/> is <c>"object"</c> or any <c>list(...)</c> type, and is
    /// <c>null</c> (omitted from JSON) for primitive scalar types.
    /// </para>
    /// </remarks>
    [JsonConverter(typeof(Internal.SchemaEntryJsonConverter))]
    public sealed class SchemaEntry
    {
        /// <summary>The property key from the source event properties.</summary>
        public string PropertyName { get; set; } = string.Empty;

        /// <summary>
        /// The classified type: one of <c>string | int | float | boolean | null | object |
        /// list(string) | list(int) | list(float) | list(boolean) | list(object) | unknown</c>.
        /// </summary>
        public string PropertyType { get; set; } = string.Empty;

        /// <summary>
        /// Recursive children (see the type remarks). <c>null</c> means absent — only present for
        /// <c>object</c> and <c>list(...)</c> types.
        /// </summary>
        public object? Children { get; set; }

        /// <summary>
        /// Tracking-plan event IDs whose value constraints this property's value breaks (event spec
        /// validation, dev and staging only). Wire-only: set on the copy of the schema that is sent,
        /// never on the schema returned to the caller. Serialized as <c>failedEventIds</c> when set.
        /// </summary>
        internal List<string>? FailedEventIds { get; set; }

        /// <summary>
        /// The property's value, encrypted for the Inspector Debugger (dev and staging, with a
        /// public encryption key). Wire-only, like <see cref="FailedEventIds"/>. Serialized as
        /// <c>encryptedPropertyValue</c> when set.
        /// </summary>
        internal string? EncryptedPropertyValue { get; set; }

        /// <summary>Creates an empty entry.</summary>
        public SchemaEntry() { }

        /// <summary>Creates an entry with the given name, type, and optional children.</summary>
        public SchemaEntry(string propertyName, string propertyType, object? children = null)
        {
            PropertyName = propertyName;
            PropertyType = propertyType;
            Children = children;
        }
    }
}
