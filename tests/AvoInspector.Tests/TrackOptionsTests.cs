using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
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
    /// test here, so none of them can set the process-wide (re-armed only via the internal test hook)
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
        [InlineData(null, "   ", "constructor", null)]    // originHint absent, appVersion whitespace-only -> constructor fallback
        [InlineData(null, " 5.1.0 ", "value", "5.1.0")]   // originHint absent, padded appVersion -> trimmed override
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

        /// <summary>The exact fixed string BuildWireEvent logs (AvoInspector.cs); never contains option values.</summary>
        private const string OriginHintWarningText =
            "originHint set without a usable AppVersion; this event's appVersion will be sent " +
            "as null, which the current v1 backend silently drops (AVO-3543).";

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        /// <summary>
        /// Covers the gated, one-shot <c>Logger.Error</c> warning added in <c>BuildWireEvent</c>
        /// for decision-table row 2 (<c>OriginHint</c> set, resolved <c>appVersion</c> null), and
        /// its process-wide <c>_originHintWithoutAppVersionWarned</c> latch (re-armed only via the
        /// internal <c>ResetOriginHintWarningLatchForTesting</c> hook). This is
        /// the ONLY test in the whole feature's matrix allowed to construct a <c>dev</c> instance
        /// or leave logging enabled — every other test in <c>TrackOptionsTests.cs</c> uses
        /// <c>staging</c> specifically so it cannot consume this latch out of order (see this
        /// file's class remarks and <c>planning/gateway-track-options/spec.md</c>'s Test Plan
        /// leading note). Steps run in a fixed order within this single method: the latch is
        /// process-wide, so a second, independent test method touching the same trigger condition
        /// would create an undocumented, execution-order-dependent coupling xunit does not
        /// guarantee against.
        /// </summary>
        [Fact]
        public async Task OriginHint_without_usable_AppVersion_logs_gated_warning_once_matching_shouldLog_flag()
        {
            const string WarningText = OriginHintWarningText;

            // Re-arm the process-wide latch so this test does not depend on execution order.
            AvoInspector.ResetOriginHintWarningLatchForTesting();

            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            using (var console = new ConsoleErrorScope())
            {
                // (0) dev instance — construction alone sets the process-wide _shouldLog flag to
                // true (dev default), no explicit EnableLogging(true) needed. A triggering call
                // (OutputReference AND OriginHint both actually set, AppVersion absent) writes
                // exactly one stderr line containing the fixed warning text and none of the real,
                // present option values ("meta-x7k2q", "web") — a non-vacuous leak-check.
                var dev = new AvoInspector("k", "dev", "1.0.0");
                Assert.True(AvoInspector.ShouldLogForTesting);

                await dev.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1",
                    new TrackOptions { OutputReference = "meta-x7k2q", OriginHint = "web" });

                var afterFirstCall = console.Output;
                Assert.Equal(1, CountOccurrences(afterFirstCall, WarningText));
                Assert.DoesNotContain("meta-x7k2q", afterFirstCall);
                Assert.DoesNotContain("web", afterFirstCall);

                // (1) four more identical triggering calls (five total across steps 0-1) still
                // leave exactly one warning line — the one-shot, process-wide latch proof. If the
                // latch were absent this would observe five lines, not one.
                for (var i = 0; i < 4; i++)
                {
                    await dev.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1",
                        new TrackOptions { OutputReference = "meta-x7k2q", OriginHint = "web" });
                }

                Assert.Equal(1, CountOccurrences(console.Output, WarningText));

                // (2) a separate, never-before-exercised staging instance (logging disabled by
                // default) given the identical triggering TrackOptions writes nothing to stderr —
                // proves the _shouldLog gate independently of the latch (Logger.Error
                // short-circuits on _shouldLog before the latch is ever consulted), so this
                // sub-case does not depend on step 0/1's latch state.
                var staging = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);
                Assert.False(AvoInspector.ShouldLogForTesting);

                var beforeStagingCall = console.Output;
                await staging.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1",
                    new TrackOptions { OutputReference = "meta-x7k2q", OriginHint = "web" });

                Assert.Equal(beforeStagingCall, console.Output); // nothing new written to stderr
                Assert.Equal(1, CountOccurrences(console.Output, WarningText)); // still only step 0/1's line

                // (3) decision table rows 1/3/4 (AppVersion non-blank, or OriginHint absent) never
                // satisfy the trigger condition, so no additional warning is written on the dev
                // instance. Note: constructing the staging instance in step (2) reset the
                // process-wide _shouldLog flag to false, so logging is off here; these rows fail
                // the trigger condition's earlier clauses regardless of the flag.
                var nonTriggeringOptions = new[]
                {
                    new TrackOptions { OriginHint = "web", AppVersion = "5.1.0" }, // row 1
                    new TrackOptions { AppVersion = "5.1.0" },                     // row 3
                    new TrackOptions(),                                           // row 4
                };
                foreach (var options in nonTriggeringOptions)
                {
                    await dev.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1", options);
                }

                Assert.Equal(1, CountOccurrences(console.Output, WarningText));

                // The latch is only re-armed by the test hook, but _shouldLog is process-wide and mutable — restore
                // it to the suite's default (off) so no later test observes logging left enabled.
                dev.EnableLogging(false);
                Assert.False(AvoInspector.ShouldLogForTesting);

                dev.Destroy();
                staging.Destroy();
            }
        }

        /// <summary>
        /// The latch claim is an atomic Interlocked.CompareExchange, so N callers that all evaluate
        /// the trigger condition at the same instant still produce exactly one warning line. A plain
        /// volatile check-then-set could let several of them win (CodeRabbit review on PR #3).
        /// </summary>
        [Fact]
        public async Task OriginHint_warning_latch_is_claimed_exactly_once_under_concurrent_triggering_calls()
        {
            AvoInspector.ResetOriginHintWarningLatchForTesting();

            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            using (var console = new ConsoleErrorScope())
            {
                // staging (logging off by construction) with the gate explicitly opened; a large
                // batch size + no timer so the concurrent calls only buffer and never reach HTTP.
                var inspector = new AvoInspector("k", "staging", "1.0.0",
                    batchSize: 1000, disableBatchTimer: true);
                inspector.EnableLogging(true);
                try
                {
                    const int callers = 16;
                    using var gate = new Barrier(callers);
                    var options = new TrackOptions { OutputReference = "meta-x7k2q", OriginHint = "web" };

                    var calls = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
                    {
                        gate.SignalAndWait(); // release all callers into BuildWireEvent together
                        await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1", options);
                    })).ToArray();
                    await Task.WhenAll(calls);

                    Assert.Equal(1, CountOccurrences(console.Output, OriginHintWarningText));
                    Assert.DoesNotContain("meta-x7k2q", console.Output);
                    Assert.Equal(callers, inspector.PendingBatchCount); // every call still enqueued
                }
                finally
                {
                    inspector.EnableLogging(false);
                    inspector.Destroy();
                }
            }
        }

        /// <summary>
        /// Makes the "XML doc summaries" Acceptance Criterion binary/CI-enforced, since
        /// <c>AvoInspector.csproj</c> suppresses <c>CS1591</c> project-wide and so gives zero
        /// compiler enforcement on its own. Reads the package's <b>compiled</b> XML doc file (not
        /// the source <c>.cs</c> file), which <c>AvoInspector.Tests.csproj</c>'s
        /// <c>ProjectReference</c> to a project built with <c>GenerateDocumentationFile=true</c>
        /// copies next to <c>AvoInspector.dll</c> in this test project's own output directory.
        /// </summary>
        [Fact]
        public void TrackOptions_type_and_all_three_properties_have_xmldoc_summaries()
        {
            var xmlPath = Path.ChangeExtension(typeof(TrackOptions).Assembly.Location, ".xml");

            Assert.True(File.Exists(xmlPath),
                "Expected the compiled XML doc file at \"" + xmlPath + "\" (copied next to " +
                "AvoInspector.dll via ProjectReference + GenerateDocumentationFile=true). A " +
                "missing file means either GenerateDocumentationFile was turned off or " +
                "AvoInspector.Tests.csproj's reference kind reverted to PackageReference — " +
                "both real regressions this test exists to catch.");

            var doc = XDocument.Load(xmlPath);

            AssertHasNonEmptySummary(doc, "T:Avo.Inspector.TrackOptions");
            AssertHasNonEmptySummary(doc, "P:Avo.Inspector.TrackOptions.OutputReference");
            AssertHasNonEmptySummary(doc, "P:Avo.Inspector.TrackOptions.OriginHint");
            AssertHasNonEmptySummary(doc, "P:Avo.Inspector.TrackOptions.AppVersion");
        }

        private static void AssertHasNonEmptySummary(XDocument doc, string memberName)
        {
            var member = doc.Descendants("member")
                .FirstOrDefault(m => (string?)m.Attribute("name") == memberName);
            Assert.True(member != null, "No <member name=\"" + memberName + "\"> entry found in the XML doc file.");

            var summary = member!.Element("summary");
            Assert.True(summary != null, "<member name=\"" + memberName + "\"> has no <summary> element.");
            Assert.False(string.IsNullOrWhiteSpace(summary!.Value),
                "<member name=\"" + memberName + "\">'s <summary> is empty.");
        }
    }
}
