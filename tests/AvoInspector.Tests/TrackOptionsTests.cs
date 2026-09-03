using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avo.Inspector.Conformance;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Wire-shape and behavior matrix for <see cref="TrackOptions"/> / the fourth
    /// <c>TrackSchemaFromEvent</c> parameter (AVO-3516/AVO-3543), per
    /// <c>planning/gateway-track-options/spec.md</c>'s Proposed Design (the app-version decision
    /// table and before/after wire body examples) and Test Plan.
    /// </summary>
    /// <remarks>
    /// Every instance in this file is constructed with env <c>"staging"</c> (never <c>"dev"</c>,
    /// and <c>EnableLogging(true)</c> is never called), matching the standard wire-body recipe in
    /// the spec's "Existing Patterns to Follow." This keeps <c>_shouldLog == false</c> for every
    /// test here, so none of them can set the process-wide, never-reset
    /// <c>_originHintWithoutAppVersionWarned</c> one-shot latch — even the rows below that satisfy
    /// the warning's trigger condition (an <c>OriginHint</c> set without a usable
    /// <c>AppVersion</c>) merely resolve a JSON <c>null</c> <c>appVersion</c> without evaluating
    /// the gated log call. The dedicated warning test lives elsewhere (STORY-005) and is the only
    /// test in the suite that uses a <c>dev</c> instance for this feature.
    /// </remarks>
    public class TrackOptionsTests
    {
        private static readonly string[] V100Keys =
        {
            "apiKey", "appName", "appVersion", "libVersion", "env", "libPlatform", "messageId",
            "streamId", "sessionId", "createdAt", "samplingRate", "type", "eventName", "eventProperties"
        };

        private static JsonElement FirstEvent(TestInspectorServer server, int requestIndex = 0)
        {
            using var doc = JsonDocument.Parse(server.Requests[requestIndex].Body);
            return doc.RootElement.EnumerateArray().First().Clone();
        }

        private static void AssertV100KeySet(JsonElement evt)
        {
            var actualKeys = evt.EnumerateObject().Select(p => p.Name)
                .OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var expectedKeys = V100Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedKeys, actualKeys);
        }

        private static JsonElement FindEventByName(JsonElement batchArray, string eventName)
        {
            foreach (var evt in batchArray.EnumerateArray())
            {
                if (evt.GetProperty("eventName").GetString() == eventName)
                {
                    return evt;
                }
            }
            throw new InvalidOperationException("No event named \"" + eventName + "\" found in batch.");
        }

        [Fact]
        public async Task Null_options_produces_1_0_0_body_exactly()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.4.2", batchSize: 1);

                await inspector.TrackSchemaFromEvent(
                    "Purchase Completed", Props.Of(("amount", 9.99)), "s1", options: null);

                Assert.Equal(1, server.RequestCount);
                var evt = FirstEvent(server);

                AssertV100KeySet(evt);
                Assert.Equal("1.4.2", evt.GetProperty("appVersion").GetString());
                Assert.False(evt.TryGetProperty("outputReference", out _));
                Assert.False(evt.TryGetProperty("originHint", out _));
            }
        }

        [Fact]
        public async Task Empty_TrackOptions_produces_1_0_0_body_exactly()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.4.2", batchSize: 1);

                await inspector.TrackSchemaFromEvent(
                    "Purchase Completed", Props.Of(("amount", 9.99)), "s1", new TrackOptions());

                Assert.Equal(1, server.RequestCount);
                var evt = FirstEvent(server);

                AssertV100KeySet(evt);
                Assert.Equal("1.4.2", evt.GetProperty("appVersion").GetString());
                Assert.False(evt.TryGetProperty("outputReference", out _));
                Assert.False(evt.TryGetProperty("originHint", out _));
            }
        }

        [Fact]
        public async Task ThreeArgCall_still_compiles_and_matches_1_0_0_body()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.4.2", batchSize: 1);

                // Existing 3-arg call site — no options argument at all.
                await inspector.TrackSchemaFromEvent(
                    "Purchase Completed", Props.Of(("amount", 9.99)), "s1");

                Assert.Equal(1, server.RequestCount);
                var evt = FirstEvent(server);

                AssertV100KeySet(evt);
                Assert.Equal("1.4.2", evt.GetProperty("appVersion").GetString());
                Assert.False(evt.TryGetProperty("outputReference", out _));
                Assert.False(evt.TryGetProperty("originHint", out _));
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task OutputReference_omitted_for_null_empty_or_whitespace(string? outputReference)
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);

                await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1",
                    new TrackOptions { OutputReference = outputReference });

                var evt = FirstEvent(server);
                Assert.False(evt.TryGetProperty("outputReference", out _));
            }
        }

        [Fact]
        public async Task OutputReference_trimmed_when_set()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);

                await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1",
                    new TrackOptions { OutputReference = "  meta-x7k2q  " });

                var evt = FirstEvent(server);
                Assert.Equal("meta-x7k2q", evt.GetProperty("outputReference").GetString());
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task OriginHint_omitted_for_null_empty_or_whitespace(string? originHint)
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);

                await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1",
                    new TrackOptions { OriginHint = originHint });

                var evt = FirstEvent(server);
                Assert.False(evt.TryGetProperty("originHint", out _));
            }
        }

        [Fact]
        public async Task OriginHint_trimmed_when_set()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);

                // AppVersion also set (non-blank) so this row does not exercise the
                // originHint-without-AppVersion decision-table row 2 — this test's purpose is
                // trimming, not the appVersion:null case (that is AppVersion_rule_matrix's job).
                await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1",
                    new TrackOptions { OriginHint = "  web  ", AppVersion = "5.1.0" });

                var evt = FirstEvent(server);
                Assert.Equal("web", evt.GetProperty("originHint").GetString());
            }
        }

        [Theory]
        [InlineData("web", "5.1.0", "value", "5.1.0")]   // decision table row 1: originHint set, appVersion set
        [InlineData("web", null, "null", null)]           // decision table row 2: originHint set, appVersion absent
        [InlineData(null, "5.1.0", "value", "5.1.0")]     // decision table row 3: originHint absent, appVersion set
        [InlineData(null, null, "constructor", null)]     // decision table row 4: both absent
        [InlineData("web", "   ", "null", null)]          // originHint set, appVersion whitespace-only -> null
        public async Task AppVersion_rule_matrix(
            string? originHint, string? appVersion, string expectedKind, string? expectedValue)
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                const string constructorVersion = "1.4.2";
                var inspector = new AvoInspector("k", "staging", constructorVersion, batchSize: 1);

                await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1",
                    new TrackOptions { OriginHint = originHint, AppVersion = appVersion });

                var evt = FirstEvent(server);
                var wireAppVersion = evt.GetProperty("appVersion");

                switch (expectedKind)
                {
                    case "value":
                        Assert.Equal(JsonValueKind.String, wireAppVersion.ValueKind);
                        Assert.Equal(expectedValue, wireAppVersion.GetString());
                        break;
                    case "null":
                        Assert.Equal(JsonValueKind.Null, wireAppVersion.ValueKind);
                        break;
                    case "constructor":
                        Assert.Equal(JsonValueKind.String, wireAppVersion.ValueKind);
                        Assert.Equal(constructorVersion, wireAppVersion.GetString());
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(expectedKind), expectedKind, null);
                }
            }
        }

        [Fact]
        public async Task CustomerProperty_named_outputReference_stays_in_schema()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);

                var schema = await inspector.TrackSchemaFromEvent(
                    "E",
                    Props.Of(("outputReference", "customer-value")),
                    "s1",
                    new TrackOptions { OutputReference = "meta-x7k2q" });

                Assert.Single(schema);
                Assert.Equal("outputReference", schema[0].PropertyName);
                Assert.Equal("string", schema[0].PropertyType);

                var evt = FirstEvent(server);
                Assert.Equal("meta-x7k2q", evt.GetProperty("outputReference").GetString());
            }
        }

        [Fact]
        public async Task CustomerProperty_named_originHint_stays_in_schema()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);

                var schema = await inspector.TrackSchemaFromEvent(
                    "E",
                    Props.Of(("originHint", "customer-value")),
                    "s1",
                    new TrackOptions { OriginHint = "web", AppVersion = "5.1.0" });

                Assert.Single(schema);
                Assert.Equal("originHint", schema[0].PropertyName);
                Assert.Equal("string", schema[0].PropertyType);

                var evt = FirstEvent(server);
                Assert.Equal("web", evt.GetProperty("originHint").GetString());
            }
        }

        [Fact]
        public async Task Options_never_leak_into_eventProperties()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);

                var schema = await inspector.TrackSchemaFromEvent(
                    "E",
                    Props.Of(("amount", 9.99)),
                    "s1",
                    new TrackOptions { OutputReference = "meta-x7k2q", OriginHint = "web", AppVersion = "5.1.0" });

                Assert.DoesNotContain(schema, e => e.PropertyName == "outputReference");
                Assert.DoesNotContain(schema, e => e.PropertyName == "originHint");
                Assert.DoesNotContain(schema, e => e.PropertyName == "appVersion");
            }
        }

        [Fact]
        public async Task Options_survive_batching_flushed_later()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 30);

                await inspector.TrackSchemaFromEvent(
                    "E",
                    Props.Of(("x", 1)),
                    "s1",
                    new TrackOptions { OutputReference = "meta-x7k2q", OriginHint = "web", AppVersion = "5.1.0" });

                Assert.Equal(0, server.RequestCount); // buffered, not yet sent (batchSize 30)

                await inspector.Flush();

                Assert.Equal(1, server.RequestCount);
                var evt = FirstEvent(server);
                Assert.Equal("meta-x7k2q", evt.GetProperty("outputReference").GetString());
                Assert.Equal("web", evt.GetProperty("originHint").GetString());
                Assert.Equal("5.1.0", evt.GetProperty("appVersion").GetString());
            }
        }

        [Fact]
        public async Task Options_survive_immediate_send_path()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);

                await inspector.TrackSchemaFromEvent(
                    "E",
                    Props.Of(("x", 1)),
                    "s1",
                    new TrackOptions { OutputReference = "meta-x7k2q", OriginHint = "web", AppVersion = "5.1.0" });

                Assert.Equal(1, server.RequestCount); // batchSize 1 -> sent immediately
                var evt = FirstEvent(server);
                Assert.Equal("meta-x7k2q", evt.GetProperty("outputReference").GetString());
                Assert.Equal("web", evt.GetProperty("originHint").GetString());
                Assert.Equal("5.1.0", evt.GetProperty("appVersion").GetString());
            }
        }

        [Fact]
        public async Task Options_survive_gzip_path()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);
                var big = new OrderedPropertyDictionary();
                for (var i = 0; i < 40; i++)
                {
                    big["attribute_" + i.ToString("D2")] = "value";
                }

                await inspector.TrackSchemaFromEvent(
                    "Large Payload Event",
                    big,
                    "s1",
                    new TrackOptions { OutputReference = "meta-x7k2q", OriginHint = "web", AppVersion = "5.1.0" });

                Assert.Equal(1, server.RequestCount);
                Assert.Equal("gzip", server.Requests[0].ContentEncoding); // >= 1024 bytes -> gzip

                var evt = FirstEvent(server);
                Assert.Equal("meta-x7k2q", evt.GetProperty("outputReference").GetString());
                Assert.Equal("web", evt.GetProperty("originHint").GetString());
                Assert.Equal("5.1.0", evt.GetProperty("appVersion").GetString());
            }
        }

        [Fact]
        public async Task Batch_may_mix_events_with_different_options()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 30);

                await inspector.TrackSchemaFromEvent(
                    "WithOptions",
                    Props.Of(("x", 1)),
                    "s1",
                    new TrackOptions { OutputReference = "meta-x7k2q", OriginHint = "web", AppVersion = "5.1.0" });
                await inspector.TrackSchemaFromEvent(
                    "WithoutOptions",
                    Props.Of(("y", 2)),
                    "s2");

                await inspector.Flush();

                Assert.Equal(1, server.RequestCount);
                using var doc = JsonDocument.Parse(server.Requests[0].Body);
                Assert.Equal(2, doc.RootElement.GetArrayLength());

                var withOptions = FindEventByName(doc.RootElement, "WithOptions");
                Assert.Equal("meta-x7k2q", withOptions.GetProperty("outputReference").GetString());
                Assert.Equal("web", withOptions.GetProperty("originHint").GetString());
                Assert.Equal("5.1.0", withOptions.GetProperty("appVersion").GetString());

                var withoutOptions = FindEventByName(doc.RootElement, "WithoutOptions");
                Assert.False(withoutOptions.TryGetProperty("outputReference", out _));
                Assert.False(withoutOptions.TryGetProperty("originHint", out _));
                Assert.Equal("1.0.0", withoutOptions.GetProperty("appVersion").GetString());
            }
        }

        [Fact]
        public async Task Options_do_not_prevent_sampling_drop()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);
                inspector.SetSamplingRateForTesting(0.0);

                var schema = await inspector.TrackSchemaFromEvent(
                    "E",
                    Props.Of(("x", 1)),
                    "s1",
                    new TrackOptions { OutputReference = "meta-x7k2q", OriginHint = "web", AppVersion = "5.1.0" });

                Assert.Single(schema); // schema still returned at enqueue
                await inspector.Flush();
                Assert.Equal(0, server.RequestCount); // dropped before buffering; options irrelevant
            }
        }

        [Fact]
        public async Task Options_are_noop_after_Destroy()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 30);
                inspector.SetSamplingRateForTesting(0.5);

                await inspector.TrackSchemaFromEvent("E1", Props.Of(("a", 1)), "s1");
                await inspector.TrackSchemaFromEvent("E2", Props.Of(("b", 2)), "s1");
                inspector.Destroy();

                var schema = await inspector.TrackSchemaFromEvent(
                    "E3",
                    Props.Of(("c", 3)),
                    "s1",
                    new TrackOptions { OutputReference = "meta-x7k2q", OriginHint = "web", AppVersion = "5.1.0" });

                Assert.Empty(schema); // post-destroy no-op; options never inspected
                await inspector.Flush();
                Assert.Equal(0, server.RequestCount);
            }
        }

        [Fact]
        public async Task OutputReference_alone_leaves_appVersion_at_constructor_value_and_omits_originHint()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.4.2", batchSize: 1);

                await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1",
                    new TrackOptions { OutputReference = "meta-x7k2q" });

                var evt = FirstEvent(server);
                Assert.Equal("meta-x7k2q", evt.GetProperty("outputReference").GetString());
                Assert.Equal("1.4.2", evt.GetProperty("appVersion").GetString()); // unscoped: constructor version
                Assert.False(evt.TryGetProperty("originHint", out _));
            }
        }

        [Fact]
        public async Task AppVersion_alone_omits_outputReference_and_originHint()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);

                await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1",
                    new TrackOptions { AppVersion = "5.1.0" });

                var evt = FirstEvent(server);
                Assert.Equal("5.1.0", evt.GetProperty("appVersion").GetString()); // unscoped override, trimmed
                Assert.False(evt.TryGetProperty("outputReference", out _));
                Assert.False(evt.TryGetProperty("originHint", out _));
            }
        }

        [Fact]
        public async Task Same_TrackOptions_instance_reused_unmutated_across_two_calls_produces_correct_body_both_times()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);
                var options = new TrackOptions
                {
                    OutputReference = "meta-x7k2q", OriginHint = "web", AppVersion = "5.1.0"
                };

                await inspector.TrackSchemaFromEvent("E1", Props.Of(("a", 1)), "s1", options);
                await inspector.TrackSchemaFromEvent("E2", Props.Of(("b", 2)), "s2", options);

                Assert.Equal(2, server.RequestCount);
                for (var i = 0; i < 2; i++)
                {
                    var evt = FirstEvent(server, i);
                    Assert.Equal("meta-x7k2q", evt.GetProperty("outputReference").GetString());
                    Assert.Equal("web", evt.GetProperty("originHint").GetString());
                    Assert.Equal("5.1.0", evt.GetProperty("appVersion").GetString());
                }
            }
        }
    }
}
