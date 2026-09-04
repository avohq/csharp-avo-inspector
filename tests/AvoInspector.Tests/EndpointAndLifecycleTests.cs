using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
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
                using var doc = JsonDocument.Parse(request.Body);
                var evt = doc.RootElement[0];
                Assert.Equal("my-inspector-key", evt.GetProperty("apiKey").GetString());
                Assert.Equal("staging", evt.GetProperty("env").GetString());
            }
        }

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
