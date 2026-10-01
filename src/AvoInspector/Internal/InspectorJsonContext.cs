using System.Text.Json.Serialization;

namespace Avo.Inspector.Internal
{
    /// <summary>
    /// Source-generated serialization metadata for the wire body, so the SDK works when the host
    /// application is trimmed or published with Native AOT (where reflection-based
    /// <c>System.Text.Json</c> serialization is disabled). <see cref="SchemaEntry"/> keeps its
    /// hand-written <see cref="SchemaEntryJsonConverter"/> via its <c>[JsonConverter]</c> attribute.
    /// </summary>
    [JsonSerializable(typeof(WireEvent[]))]
    internal sealed partial class InspectorJsonContext : JsonSerializerContext
    {
    }
}
