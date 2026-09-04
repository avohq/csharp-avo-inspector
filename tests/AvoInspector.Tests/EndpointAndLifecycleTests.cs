using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Avo.Inspector.Internal;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Security and lifecycle MUSTs not covered by fixtures: the unified production endpoint and
    /// its REQUIRED request headers (SPEC.md §7.1, §7.2), the fail-closed mock-endpoint gate
    /// (§7.1), sampling drop (§7.7), and destroy post-state (§4.5 — AC-19).
    /// </summary>
    public class EndpointAndLifecycleTests
    {
        private const string Production = "https://api.avo.app/inspector/v2/track";

        [Fact]
        public void Prod_instance_ignores_mock_endpoint_fail_closed()
        {
            using (new MockEndpointScope("http://attacker.example:9999"))
            {
                var prod = new AvoInspector("k", "prod", "1.0.0");
                Assert.Equal(Production, prod.ResolvedEndpointForTesting());
            }
        }

        [Theory]
        [InlineData("dev")]
        [InlineData("staging")]
        public void NonProd_instance_honors_mock_endpoint(string env)
        {
            using (new MockEndpointScope("http://127.0.0.1:9876"))
            {
                var inspector = new AvoInspector("k", env, "1.0.0");
                Assert.Equal("http://127.0.0.1:9876", inspector.ResolvedEndpointForTesting());
            }
        }

        [Fact]
        public void Unset_mock_endpoint_uses_production()
        {
            using (new MockEndpointScope(null))
            {
                var inspector = new AvoInspector("k", "dev", "1.0.0");
                Assert.Equal(Production, inspector.ResolvedEndpointForTesting());
            }
        }

        /// <summary>
        /// SPEC.md §7.2 — every request carries <c>api-key</c>, <c>env</c> and <c>X-Avo-Client</c>
        /// as headers. <c>/inspector/v2/track</c> answers <c>400</c> when <c>api-key</c> or
        /// <c>env</c> is missing or not one of <c>dev</c>/<c>staging</c>/<c>prod</c>, so this is a
        /// send-or-fail contract, not a nicety. <c>X-Avo-Client</c> is how the edge attributes
        /// traffic per sender without decoding a body; a generated server SDK sends its
        /// <c>libPlatform</c> token there, so this SDK sends <c>csharp</c>.
        /// </summary>
        [Theory]
        [InlineData("dev")]
        [InlineData("staging")]
        public async Task Request_carries_apiKey_env_and_client_headers(string env)
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("my-inspector-key", env, "1.0.0", batchSize: 1);
                await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1");
                await inspector.Flush();

                var request = Assert.Single(server.Requests);
                Assert.Equal("my-inspector-key", request.Header("api-key"));
                Assert.Equal(env, request.Header("env"));
                Assert.Equal("csharp", request.Header("X-Avo-Client"));
                Assert.Equal(InspectorVersion.LibPlatform, request.Header("x-avo-client")); // names are case-insensitive
                Assert.Equal("application/json", request.ContentType);
            }
        }

        /// <summary>
        /// The header copies do not replace the body copies: <c>/inspector/v2/track</c> reads
        /// <c>apiKey</c>/<c>env</c> from the headers and ignores the body's, but keeping both holds
        /// one body shape (and one JSON Schema) across every Inspector sender — SPEC.md §7.3.
        /// </summary>
        [Fact]
        public async Task ApiKey_and_env_stay_in_the_body_as_well_as_the_headers()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("my-inspector-key", "staging", "1.0.0", batchSize: 1);
                await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1");
                await inspector.Flush();

                var request = Assert.Single(server.Requests);

                // The header copies v2 actually reads (SPEC.md §7.2) ...
                Assert.Equal("my-inspector-key", request.Header("api-key"));
                Assert.Equal("staging", request.Header("env"));

                // ... and the body copies that survive beside them, same values, same request.
                using var doc = JsonDocument.Parse(request.Body);
                var evt = doc.RootElement[0];
                Assert.Equal("my-inspector-key", evt.GetProperty("apiKey").GetString());
                Assert.Equal("staging", evt.GetProperty("env").GetString());
            }
        }

        /// <summary>
        /// SPEC.md §7.2 carries the apiKey in a request header, so a key holding CR, LF or NUL could
        /// terminate the header line and inject content into the outbound request. The header is set
        /// with <c>TryAddWithoutValidation</c>, which by definition does not check, and there is no
        /// transport backstop: with the guard removed, net8.0 sends all three of these and the server
        /// parses <c>X-Injected</c> as a real header. The sender MUST therefore reject the batch
        /// itself — nothing transmitted, <c>SendStatus.Error</c> returned. The vector is specific to
        /// the v2 header move: in v1 the key travelled only in the JSON body, where it cannot break
        /// framing.
        /// </summary>
        [Theory]
        [InlineData("key\rX-Injected: 1")]
        [InlineData("key\nX-Injected: 1")]
        [InlineData("key\r\nX-Injected: 1")]
        [InlineData("key\0truncated")]
        public async Task ApiKey_with_a_control_character_is_rejected_before_any_request(string key)
        {
            using var server = new TestInspectorServer();

            var result = await InspectorHttpSender.SendAsync(
                server.BaseUrl, key, "staging", OneEvent(key), shouldLog: false);

            Assert.Equal(SendStatus.Error, result.Status);
            Assert.Null(result.NewSamplingRate);
            Assert.Equal(0, server.RequestCount); // rejected before the connection, not by the server
        }

        /// <summary>
        /// The guard rejects only the three characters that can break header framing. A key built
        /// from other unusual but legal bytes still sends, so the check cannot quietly disable an
        /// otherwise working SDK.
        /// </summary>
        [Fact]
        public async Task ApiKey_with_unusual_but_legal_characters_still_sends()
        {
            const string key = "k3y-with spaces_and.symbols+/=";
            using var server = new TestInspectorServer();

            var result = await InspectorHttpSender.SendAsync(
                server.BaseUrl, key, "staging", OneEvent(key), shouldLog: false);

            Assert.Equal(SendStatus.Ok, result.Status);
            var request = Assert.Single(server.Requests);
            Assert.Equal(key, request.Header("api-key"));
        }

        /// <summary>
        /// End to end. The constructor accepts the key and only warns: SPEC.md §4.1 makes empty and
        /// whitespace the sole fatal apiKey inputs, and even requires an invalid env to fall back
        /// rather than throw, so an unspecified third throw would diverge from the sibling SDKs and
        /// would kill a process over lost analytics. The sender is what holds the wire safe, so the
        /// event still resolves at enqueue and still never reaches the network.
        /// </summary>
        [Fact]
        public async Task Constructor_accepts_a_crlf_key_and_no_event_reaches_the_wire()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("key\r\nX-Injected: 1", "staging", "1.0.0", batchSize: 1);

                var schema = await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1");

                Assert.Single(schema); // schema is still returned at enqueue (SPEC.md §4.2)
                await inspector.Flush();
                Assert.Equal(0, server.RequestCount);
            }
        }

        /// <summary>
        /// Pins the predicate directly, independent of any transport. Removing the guard and running
        /// the tests above on net8.0 shows SocketsHttpHandler does not reject these at all: CR and
        /// CRLF keys are written through and the server sees a genuine injected header, while an LF
        /// key is folded into one corrupted value. The rejection has to be the SDK's own, so pin it
        /// where no framework difference can reach it. Empty counts as safe here by design — SPEC.md
        /// §4.1 already rejects an empty apiKey at construction, so this predicate stays
        /// single-purpose.
        /// </summary>
        [Theory]
        [InlineData("plain-key", true)]
        [InlineData("k3y-with spaces_and.symbols+/=", true)]
        [InlineData("", true)]
        [InlineData("key\r", false)]
        [InlineData("key\n", false)]
        [InlineData("key\r\nX-Injected: 1", false)]
        [InlineData("key\0truncated", false)]
        [InlineData(null, false)]
        public void IsSafeHeaderValue_rejects_only_the_header_framing_characters(string? value, bool expected)
            => Assert.Equal(expected, InspectorHttpSender.IsSafeHeaderValue(value));

        /// <summary>A minimal well-formed batch; only the apiKey argument matters to these tests.</summary>
        private static WireEvent[] OneEvent(string apiKey) => new[]
        {
            new WireEvent
            {
                ApiKey = apiKey,
                AppName = "TestApp",
                AppVersion = "1.0.0",
                LibVersion = InspectorVersion.LibVersion,
                Env = "staging",
                LibPlatform = InspectorVersion.LibPlatform,
                MessageId = "00000000-0000-4000-8000-000000000000",
                StreamId = "s1",
                SessionId = string.Empty,
                CreatedAt = "2026-01-01T00:00:00.000Z",
                SamplingRate = 1.0,
                EventName = "E",
                EventProperties = new List<SchemaEntry>(),
            },
        };

        [Fact]
        public async Task SamplingRate_zero_drops_event_with_no_http_call()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);
                inspector.SetSamplingRateForTesting(0.0);

                var schema = await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1");

                Assert.Single(schema); // schema still returned at enqueue
                await inspector.Flush();
                Assert.Equal(0, server.RequestCount); // dropped before buffering
            }
        }

        [Fact]
        public async Task Destroy_discards_buffer_persists_state_and_makes_track_a_noop()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 30);
                inspector.SetSamplingRateForTesting(0.5);

                await inspector.TrackSchemaFromEvent("E1", Props.Of(("a", 1)), "s1");
                await inspector.TrackSchemaFromEvent("E2", Props.Of(("b", 2)), "s1");
                inspector.Destroy();

                Assert.True(inspector.IsDestroyed);
                Assert.Equal(0, inspector.PendingBatchCount);          // buffer discarded
                Assert.Equal(0.5, inspector.CurrentSamplingRate);      // sampling persists

                // Subsequent track is a no-op: resolves [], no enqueue, no HTTP.
                var schema = await inspector.TrackSchemaFromEvent("E3", Props.Of(("c", 3)), "s1");
                Assert.Empty(schema);
                await inspector.Flush();
                Assert.Equal(0, server.RequestCount);
            }
        }
    }
}
