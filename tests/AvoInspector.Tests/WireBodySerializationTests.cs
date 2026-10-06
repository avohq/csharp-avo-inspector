using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Avo.Inspector.Internal;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Locks the wire body produced by <see cref="InspectorHttpSender.SerializeBody"/>, which goes
    /// through the source-generated <see cref="InspectorJsonContext"/> so the SDK works in trimmed
    /// and Native AOT host applications. The body must match the reflection-based serializer the
    /// SDK used before, and the exact wire shape (SPEC.md §7.3).
    /// </summary>
    public class WireBodySerializationTests
    {
        [Fact]
        public void Body_matches_golden_wire_shape()
        {
            var batch = new[]
            {
                new WireEvent
                {
                    ApiKey = "k",
                    AppName = "app",
                    AppVersion = null,
                    LibVersion = "1.1.0",
                    Env = "dev",
                    LibPlatform = "csharp",
                    MessageId = "m-1",
                    StreamId = "s-1",
                    CreatedAt = "2026-01-01T00:00:00.000Z",
                    SamplingRate = 1.0,
                    EventName = "Signed Up",
                    EventProperties = NestedProperties(),
                    OriginHint = "web",
                },
            };

            var json = Encoding.UTF8.GetString(InspectorHttpSender.SerializeBody(batch));

            // appVersion: null is written; outputReference (null) is omitted; originHint is present.
            const string expected =
                "[{\"apiKey\":\"k\",\"appName\":\"app\",\"appVersion\":null,\"libVersion\":\"1.1.0\"," +
                "\"env\":\"dev\",\"libPlatform\":\"csharp\",\"messageId\":\"m-1\",\"streamId\":\"s-1\"," +
                "\"createdAt\":\"2026-01-01T00:00:00.000Z\",\"samplingRate\":1,\"type\":\"event\"," +
                "\"eventName\":\"Signed Up\",\"eventProperties\":[" +
                "{\"propertyName\":\"name\",\"propertyType\":\"string\"}," +
                "{\"propertyName\":\"address\",\"propertyType\":\"object\",\"children\":[" +
                "{\"propertyName\":\"city\",\"propertyType\":\"string\"}]}," +
                "{\"propertyName\":\"tags\",\"propertyType\":\"list(string)\",\"children\":[\"string\"]}" +
                "],\"originHint\":\"web\"}]";
            Assert.Equal(expected, json);
        }

        [Fact]
        public void Body_matches_reflection_based_serializer()
        {
            var batch = new[]
            {
                new WireEvent
                {
                    ApiKey = "k",
                    AppName = "app",
                    AppVersion = "2.0.0",
                    LibVersion = "1.1.0",
                    Env = "prod",
                    LibPlatform = "csharp",
                    MessageId = "m-1",
                    StreamId = "s-1",
                    CreatedAt = "2026-01-01T00:00:00.000Z",
                    SamplingRate = 0.25,
                    EventName = "Ünïcode <&> \"quoted\"",
                    EventProperties = NestedProperties(),
                    OutputReference = "out-1",
                },
                new WireEvent { EventName = "Empty" },
            };

            var sourceGenerated = Encoding.UTF8.GetString(InspectorHttpSender.SerializeBody(batch));
            var reflection = JsonSerializer.Serialize(batch, new JsonSerializerOptions());

            Assert.Equal(reflection, sourceGenerated);
        }

        private static List<SchemaEntry> NestedProperties() => new List<SchemaEntry>
        {
            new SchemaEntry("name", "string"),
            new SchemaEntry("address", "object", new List<SchemaEntry> { new SchemaEntry("city", "string") }),
            new SchemaEntry("tags", "list(string)", new List<object?> { "string" }),
        };
    }
}
