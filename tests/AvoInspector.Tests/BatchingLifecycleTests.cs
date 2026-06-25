using System;
using System.Text.Json;
using System.Threading.Tasks;
using Avo.Inspector.Conformance;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Batching behaviors the deterministic single-process fixtures cannot cover: the time/idle
    /// scheduled flush (SPEC.md §12.3, SHOULD) and the transient-failure at-most-once / no-requeue
    /// path (§12.5, which needs a controllable clock / connection-drop).
    /// </summary>
    public class BatchingLifecycleTests
    {
        [Fact]
        public async Task Scheduled_timer_flushes_partial_batch_after_interval()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0",
                    batchSize: 30, batchFlushSeconds: 0.3, disableBatchTimer: false);
                try
                {
                    await inspector.TrackSchemaFromEvent("E", Props.Of(("a", 1)), "s1"); // buffered (1 < 30)

                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                    while (server.RequestCount == 0 && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(50);
                    }

                    Assert.Equal(1, server.RequestCount); // flushed by the scheduled timer, no explicit flush()
                }
                finally
                {
                    inspector.Destroy();
                }
            }
        }

        [Fact]
        public async Task DisableBatchTimer_keeps_buffer_until_explicit_flush()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0",
                    batchSize: 30, batchFlushSeconds: 0.3, disableBatchTimer: true);

                await inspector.TrackSchemaFromEvent("E", Props.Of(("a", 1)), "s1");
                await Task.Delay(1000); // well past batchFlushSeconds
                Assert.Equal(0, server.RequestCount); // no scheduled timer fired

                await inspector.Flush();
                Assert.Equal(1, server.RequestCount); // explicit flush drains it
            }
        }

        [Fact]
        public async Task Transient_network_failure_drops_batch_without_requeue()
        {
            using var server = new TestInspectorServer();
            var closedPort = TestNet.FreePort(); // free port with nothing listening -> connection refused
            var previous = Environment.GetEnvironmentVariable("AVO_INSPECTOR_MOCK_ENDPOINT");
            try
            {
                Environment.SetEnvironmentVariable("AVO_INSPECTOR_MOCK_ENDPOINT", "http://127.0.0.1:" + closedPort);
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 2, disableBatchTimer: true);

                await inspector.TrackSchemaFromEvent("E1", Props.Of(("a", 1)), "s1");
                await inspector.TrackSchemaFromEvent("E2", Props.Of(("b", 2)), "s2"); // triggers failing send
                await inspector.Flush(2000); // awaits the failed send

                // A correct at-most-once SDK does NOT re-queue the failed batch.
                Assert.Equal(0, inspector.PendingBatchCount);

                // Now point at a healthy server; the next batch must be ONLY the new events.
                Environment.SetEnvironmentVariable("AVO_INSPECTOR_MOCK_ENDPOINT", server.BaseUrl);
                await inspector.TrackSchemaFromEvent("E3", Props.Of(("c", 3)), "s1");
                await inspector.TrackSchemaFromEvent("E4", Props.Of(("d", 4)), "s2"); // triggers send
                await inspector.Flush(2000);

                Assert.Equal(1, server.RequestCount);
                using var doc = JsonDocument.Parse(server.Requests[0].Body);
                Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
                Assert.Equal(2, doc.RootElement.GetArrayLength()); // [E3, E4], not [E1..E4]
            }
            finally
            {
                Environment.SetEnvironmentVariable("AVO_INSPECTOR_MOCK_ENDPOINT", previous);
            }
        }

        [Fact]
        public async Task Large_body_is_gzip_compressed_over_the_wire()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "dev", "1.0.0"); // batchSize 1 -> immediate send
                var big = new OrderedPropertyDictionary();
                for (var i = 0; i < 40; i++)
                {
                    big["attribute_" + i.ToString("D2")] = "value";
                }

                await inspector.TrackSchemaFromEvent("Large Payload Event", big, "s1");
                await inspector.Flush();

                Assert.Equal(1, server.RequestCount);
                Assert.Equal("gzip", server.Requests[0].ContentEncoding); // >= 1024 bytes -> gzip
                Assert.Equal("application/json", server.Requests[0].ContentType); // stays application/json
                using var doc = JsonDocument.Parse(server.Requests[0].Body); // gunzipped round-trips
                Assert.Equal(1, doc.RootElement.GetArrayLength());
            }
        }
    }
}
