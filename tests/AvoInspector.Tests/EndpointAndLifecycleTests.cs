using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Security and lifecycle MUSTs not covered by fixtures: the fail-closed mock-endpoint gate
    /// (SPEC.md §7.1), sampling drop (§7.7), and destroy post-state (§4.5 — AC-19).
    /// </summary>
    public class EndpointAndLifecycleTests
    {
        private const string Production = "https://api.avo.app/inspector/v1/track";

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
