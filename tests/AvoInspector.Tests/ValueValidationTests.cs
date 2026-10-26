using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avo.Inspector.Internal.EventSpec;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// End to end: event spec fetch, value validation and value encryption on the track path, as
    /// the Inspector receives them (AVO-4069). The mock server answers
    /// <c>GET /trackingPlan/eventSpec</c> with the configured spec.
    /// </summary>
    /// <remarks>Instances use <c>staging</c>, so the process-wide logging flag stays off.</remarks>
    public class ValueValidationTests
    {
        private const string PlanSpec = @"{
          ""events"": [{ ""b"": ""main"", ""id"": ""evt_1"", ""vids"": [""var_1""], ""p"": {
            ""plan"": { ""t"": ""string"", ""r"": true, ""v"": { ""[\""Free\"",\""Pro\""]"": [""evt_1"", ""var_1""] } },
            ""seats"": { ""t"": ""int"", ""r"": false, ""minmax"": { ""1,10"": [""var_1""] } },
            ""address"": { ""t"": ""object"", ""r"": false, ""children"": { ""country"": { ""t"": ""string"", ""r"": true, ""p"": { ""IS"": [""evt_1""] } } } },
            ""items"": { ""t"": ""object"", ""r"": false, ""l"": true, ""children"": { ""sku"": { ""t"": ""string"", ""r"": true, ""rx"": { ""^S"": [""evt_1""] } } } }
          } }],
          ""metadata"": { ""schemaId"": ""schema_1"", ""branchId"": ""main"", ""latestActionId"": ""action_9"", ""sourceId"": ""source_1"" }
        }";

        private static AvoInspector Staging(string? publicEncryptionKey = null, int batchSize = 1)
            => new AvoInspector(new AvoInspectorOptions
            {
                ApiKey = "key-1",
                Env = AvoInspectorEnv.Staging,
                Version = "1.0.0",
                BatchSize = batchSize,
                DisableBatchTimer = true,
                PublicEncryptionKey = publicEncryptionKey
            });

        private static JsonElement Event(TestInspectorServer server, int request = 0, int index = 0)
        {
            using var doc = JsonDocument.Parse(server.Requests[request].Body);
            return doc.RootElement[index].Clone();
        }

        private static JsonElement Property(JsonElement evt, string name)
            => evt.GetProperty("eventProperties").EnumerateArray().Single(p => p.GetProperty("propertyName").GetString() == name);

        private static string[]? FailedIds(JsonElement property)
            => property.TryGetProperty("failedEventIds", out var ids)
                ? ids.EnumerateArray().Select(id => id.GetString()!).ToArray()
                : null;

        [Fact]
        public async Task A_value_outside_the_allowed_values_is_reported_with_the_spec_metadata()
        {
            using var server = new TestInspectorServer();
            server.ConfigureSpec(200, PlanSpec);
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = Staging();
                var schema = await inspector.TrackSchemaFromEvent("Upgrade", Props.Of(
                    ("plan", "Enterprise"), ("seats", 50), ("note", "free text")), "stream-1");

                var evt = Event(server);
                Assert.Equal(new[] { "evt_1", "var_1" }, FailedIds(Property(evt, "plan")));
                Assert.Equal(new[] { "var_1" }, FailedIds(Property(evt, "seats")));
                Assert.Null(FailedIds(Property(evt, "note")));
                Assert.DoesNotContain(evt.GetProperty("eventProperties").EnumerateArray(), p => p.TryGetProperty("passedEventIds", out _));

                var metadata = evt.GetProperty("eventSpecMetadata");
                Assert.Equal("schema_1", metadata.GetProperty("schemaId").GetString());
                Assert.Equal("main", metadata.GetProperty("branchId").GetString());
                Assert.Equal("action_9", metadata.GetProperty("latestActionId").GetString());
                Assert.Equal("source_1", metadata.GetProperty("sourceId").GetString());

                // The call resolves with the plain schema: validation results are wire-only.
                Assert.All(schema, entry => Assert.Null(entry.FailedEventIds));

                var fetch = Assert.Single(server.SpecRequests);
                Assert.Equal("/trackingPlan/eventSpec?apiKey=key-1&eventName=Upgrade&streamId=stream-1", fetch);
            }
        }

        [Fact]
        public async Task A_valid_event_carries_the_metadata_and_no_failures()
        {
            using var server = new TestInspectorServer();
            server.ConfigureSpec(200, PlanSpec);
            using (new MockEndpointScope(server.BaseUrl))
            {
                await Staging().TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Pro")), null);

                var evt = Event(server);
                Assert.True(evt.TryGetProperty("eventSpecMetadata", out _));
                Assert.Null(FailedIds(Property(evt, "plan")));
            }
        }

        [Fact]
        public async Task Object_and_list_of_object_failures_land_on_their_children()
        {
            using var server = new TestInspectorServer();
            server.ConfigureSpec(200, PlanSpec);
            using (new MockEndpointScope(server.BaseUrl))
            {
                var items = new List<object?>
                {
                    Props.Of(("sku", "S-1")),
                    Props.Of(("sku", "X-2"), ("qty", 2))
                };
                await Staging().TrackSchemaFromEvent("Upgrade", Props.Of(
                    ("plan", "Pro"), ("address", Props.Of(("country", "DE"))), ("items", items)), null);

                var evt = Event(server);
                var address = Property(evt, "address");
                Assert.Null(FailedIds(address));
                var country = address.GetProperty("children").EnumerateArray().Single();
                Assert.Equal(new[] { "evt_1" }, FailedIds(country));

                // A list of objects has one child schema per distinct element shape; each gets it.
                var elementSchemas = Property(evt, "items").GetProperty("children").EnumerateArray().ToArray();
                Assert.Equal(2, elementSchemas.Length);
                foreach (var element in elementSchemas)
                {
                    var sku = element.EnumerateArray().Single(p => p.GetProperty("propertyName").GetString() == "sku");
                    Assert.Equal(new[] { "evt_1" }, FailedIds(sku));
                }
            }
        }

        [Fact]
        public async Task Gateway_coordinates_go_to_the_spec_fetch_and_key_its_cache()
        {
            using var server = new TestInspectorServer();
            server.ConfigureSpec(200, PlanSpec);
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = Staging();
                await inspector.TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Pro")), "s", originHint: " web ");
                await inspector.TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Pro")), "s", outputReference: "meta-x7k2q", originHint: "web");
                await inspector.TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Pro")), "s", outputReference: "meta-x7k2q", originHint: "web");
                await inspector.TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Pro")), "s");

                Assert.Equal(new[]
                {
                    "/trackingPlan/eventSpec?apiKey=key-1&eventName=Upgrade&streamId=s&originHint=web",
                    "/trackingPlan/eventSpec?apiKey=key-1&eventName=Upgrade&streamId=s&originHint=web&outputReference=meta-x7k2q",
                    "/trackingPlan/eventSpec?apiKey=key-1&eventName=Upgrade&streamId=s"
                }, server.SpecRequests);
                Assert.Equal(4, server.RequestCount);
            }
        }

        [Fact]
        public async Task Without_a_spec_the_body_is_the_1_1_x_body()
        {
            using var server = new TestInspectorServer(); // spec fetches answer 404
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = Staging();
                await inspector.TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Enterprise"), ("o", Props.Of(("a", 1)))), "s");
                await inspector.TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Enterprise")), "s");

                var evt = Event(server);
                Assert.False(evt.TryGetProperty("eventSpecMetadata", out _));
                Assert.False(evt.TryGetProperty("publicEncryptionKey", out _));
                Assert.Equal(
                    "[{\"propertyName\":\"plan\",\"propertyType\":\"string\"}," +
                    "{\"propertyName\":\"o\",\"propertyType\":\"object\",\"children\":[{\"propertyName\":\"a\",\"propertyType\":\"int\"}]}]",
                    evt.GetProperty("eventProperties").GetRawText());
                // A 404 is cached: the second event is sent without fetching again.
                Assert.Single(server.SpecRequests);
                Assert.Equal(2, server.RequestCount);
            }
        }

        [Fact]
        public async Task A_spec_with_no_events_validates_nothing()
        {
            using var server = new TestInspectorServer();
            server.ConfigureSpec(200, "{\"events\":[],\"metadata\":{\"schemaId\":\"x\"}}");
            using (new MockEndpointScope(server.BaseUrl))
            {
                await Staging().TrackSchemaFromEvent("Unknown", Props.Of(("a", 1)), null);
                Assert.False(Event(server).TryGetProperty("eventSpecMetadata", out _));
            }
        }

        [Theory]
        [InlineData(500, "{}")]
        [InlineData(200, "not json")]
        public async Task A_failed_fetch_sends_unvalidated_and_is_retried_after_a_short_while(int status, string body)
        {
            using var server = new TestInspectorServer();
            server.ConfigureSpec(status, body);
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = Staging();
                var schema = await inspector.TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Enterprise")), null);
                await inspector.TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Enterprise")), null);

                Assert.Single(schema);
                Assert.Equal(2, server.RequestCount);
                Assert.False(Event(server).TryGetProperty("eventSpecMetadata", out _));
                Assert.Single(server.SpecRequests); // cached for 5 s
            }
        }

        [Fact]
        public async Task A_spec_fetch_that_times_out_sends_the_event_unvalidated()
        {
            using var server = new TestInspectorServer();
            server.ConfigureSpec(200, PlanSpec, delayMs: 2_000);
            var previous = EventSpecFetcher.FetchTimeout;
            EventSpecFetcher.FetchTimeout = TimeSpan.FromMilliseconds(200);
            try
            {
                using (new MockEndpointScope(server.BaseUrl))
                {
                    await Staging().TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Enterprise")), null);

                    Assert.Equal(1, server.RequestCount);
                    Assert.False(Event(server).TryGetProperty("eventSpecMetadata", out _));
                }
            }
            finally
            {
                EventSpecFetcher.FetchTimeout = previous;
            }
        }

        [Fact]
        public async Task Concurrent_tracks_of_one_event_share_one_fetch()
        {
            using var server = new TestInspectorServer();
            server.ConfigureSpec(200, PlanSpec, delayMs: 300);
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = Staging(batchSize: 100);
                var tracks = Enumerable.Range(0, 20)
                    .Select(_ => inspector.TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Enterprise")), null))
                    .ToArray();
                await Task.WhenAll(tracks);
                await inspector.Flush();

                Assert.Single(server.SpecRequests);
                using var doc = JsonDocument.Parse(Assert.Single(server.Requests).Body);
                Assert.Equal(20, doc.RootElement.GetArrayLength());
                Assert.All(doc.RootElement.EnumerateArray(), evt => Assert.True(evt.TryGetProperty("eventSpecMetadata", out _)));
            }
        }

        [Fact]
        public async Task Flush_sends_events_still_waiting_for_their_spec()
        {
            using var server = new TestInspectorServer();
            server.ConfigureSpec(200, PlanSpec, delayMs: 300);
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = Staging(batchSize: 100);
                var track = inspector.TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Enterprise")), null);
                Assert.False(track.IsCompleted);

                await inspector.Flush();

                Assert.Equal(1, server.RequestCount);
                Assert.True(Event(server).TryGetProperty("eventSpecMetadata", out _));
                await track;
            }
        }

        [Fact]
        public async Task Destroy_during_a_spec_fetch_sends_nothing()
        {
            using var server = new TestInspectorServer();
            server.ConfigureSpec(200, PlanSpec, delayMs: 1_000);
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = Staging();
                var track = inspector.TrackSchemaFromEvent("Upgrade", Props.Of(("plan", "Pro")), null);
                await Task.Delay(100);
                inspector.Destroy();

                Assert.Empty(await track);
                await Task.Delay(100);
                Assert.Equal(0, server.RequestCount);
            }
        }

        [Fact]
        public async Task Prod_never_fetches_a_spec_or_sends_values()
        {
            using var server = new TestInspectorServer();
            server.ConfigureSpec(200, PlanSpec);
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector(new AvoInspectorOptions
                {
                    ApiKey = "k", Env = AvoInspectorEnv.Prod, Version = "1", BatchSize = 1,
                    PublicEncryptionKey = EncryptionTests.PublicKeyHex
                });
                Assert.False(inspector.IsValidationActiveForTesting);
                Assert.False(inspector.IsEncryptionActiveForTesting);
                await inspector.Flush();
                Assert.Empty(server.SpecRequests); // prod also ignores the mock endpoint, so nothing reaches it
            }
        }

        [Fact]
        public async Task Values_are_encrypted_for_the_debugger()
        {
            using var server = new TestInspectorServer();
            server.ConfigureSpec(200, PlanSpec);
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = Staging(EncryptionTests.PublicKeyHex);
                Assert.True(inspector.IsEncryptionActiveForTesting);
                await inspector.TrackSchemaFromEvent("Upgrade", Props.Of(
                    ("plan", "Enterprise"), ("seats", 3), ("ratio", 0.5), ("ok", true), ("none", null),
                    ("address", Props.Of(("country", "IS"))), ("tags", new[] { "a" })), null);

                var evt = Event(server);
                Assert.Equal(EncryptionTests.PublicKeyHex, evt.GetProperty("publicEncryptionKey").GetString());
                string Decrypted(string name)
                    => EncryptionTests.Decrypt(Property(evt, name).GetProperty("encryptedPropertyValue").GetString()!);
                Assert.Equal("\"Enterprise\"", Decrypted("plan"));
                Assert.Equal("3", Decrypted("seats"));
                Assert.Equal("0.5", Decrypted("ratio"));
                Assert.Equal("true", Decrypted("ok"));
                Assert.Equal("null", Decrypted("none"));
                Assert.Equal("{\"country\":\"IS\"}", Decrypted("address"));

                // A list's value is not sent, but the property stays in the schema.
                Assert.False(Property(evt, "tags").TryGetProperty("encryptedPropertyValue", out _));
                // Validation results sit next to the encrypted value.
                Assert.Equal(new[] { "evt_1", "var_1" }, FailedIds(Property(evt, "plan")));
            }
        }

        [Fact]
        public void An_unusable_key_is_reported_once_and_values_are_not_sent()
        {
            using (var console = new ConsoleErrorScope())
            {
                var inspector = Staging("not-a-key");
                Assert.True(inspector.IsValidationActiveForTesting);
                Assert.False(inspector.IsEncryptionActiveForTesting);
                Assert.Contains("publicEncryptionKey", console.Output);
                Assert.DoesNotContain("not-a-key", console.Output);
            }
        }
    }
}
