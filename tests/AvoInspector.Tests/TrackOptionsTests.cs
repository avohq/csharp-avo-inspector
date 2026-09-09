using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avo.Inspector.Conformance;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Wire-shape and behavior matrix for the three gateway coordinate parameters of
    /// <c>TrackSchemaFromEvent</c> (AVO-3516), per SPEC.md §4.2.1 and §7.3.6
    /// (the normalization rules, the <c>appVersion</c> resolution table, and the omission and
    /// property-name-collision rules), and mirroring conformance fixtures <c>wire-9</c> -
    /// <c>wire-13</c> and <c>batch-7</c>.
    /// </summary>
    /// <remarks>
    /// Every wire-shape test in this file constructs its instance with env <c>"staging"</c> (never
    /// <c>"dev"</c>), so <c>_shouldLog</c> — a process-wide flag — stays off for the whole matrix.
    /// The one test that turns logging on, <c>OriginHint_without_usable_OriginAppVersion_is_silent_on_v2</c>,
    /// restores it before returning.
    /// </remarks>
    public class TrackOptionsTests
    {
        private static readonly string[] V100Keys =
        {
            "apiKey", "appName", "appVersion", "libVersion", "env", "libPlatform", "messageId",
            "streamId", "createdAt", "samplingRate", "type", "eventName", "eventProperties"
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
                    "Purchase Completed", Props.Of(("amount", 9.99)), "s1",
                    outputReference: null, originHint: null, originAppVersion: null);

                Assert.Equal(1, server.RequestCount);
                var evt = FirstEvent(server);

                AssertV100KeySet(evt);
                Assert.Equal("1.4.2", evt.GetProperty("appVersion").GetString());
                Assert.False(evt.TryGetProperty("outputReference", out _));
                Assert.False(evt.TryGetProperty("originHint", out _));
            }
        }

        [Fact]
        public async Task All_three_coordinates_null_produces_1_0_0_body_exactly()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.4.2", batchSize: 1);

                await inspector.TrackSchemaFromEvent(
                    "Purchase Completed", Props.Of(("amount", 9.99)), "s1",
                    outputReference: null, originHint: null, originAppVersion: null);

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
                    outputReference: outputReference);

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
                    outputReference: "  meta-x7k2q  ");

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
                    originHint: originHint);

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
                    originHint: "  web  ", originAppVersion: "5.1.0");

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
        [InlineData("web", "", "null", null)]             // originHint set, appVersion empty string -> null
        [InlineData(null, "   ", "constructor", null)]    // originHint absent, appVersion whitespace-only -> constructor fallback
        [InlineData(null, "", "constructor", null)]       // originHint absent, appVersion empty string -> constructor fallback
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
                    originHint: originHint, originAppVersion: appVersion);

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
                    outputReference: "meta-x7k2q");

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
                    originHint: "web", originAppVersion: "5.1.0");

                Assert.Single(schema);
                Assert.Equal("originHint", schema[0].PropertyName);
                Assert.Equal("string", schema[0].PropertyType);

                var evt = FirstEvent(server);
                Assert.Equal("web", evt.GetProperty("originHint").GetString());
            }
        }

        /// <summary>
        /// SPEC.md §7.3.6: "An event property that happens to be named <c>outputReference</c>,
        /// <c>originHint</c>, or <c>appVersion</c> is an ordinary property." Completes the
        /// collision trio (conformance fixture <c>wire-13</c>) for the third name: a property
        /// literally called <c>appVersion</c> stays in the schema with its own extracted type and
        /// does NOT become the wire's top-level <c>appVersion</c>, which comes only from the
        /// <c>originAppVersion</c> parameter.
        /// </summary>
        [Fact]
        public async Task CustomerProperty_named_appVersion_stays_in_schema()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.4.2", batchSize: 1);

                var schema = await inspector.TrackSchemaFromEvent(
                    "E",
                    Props.Of(("appVersion", true)),
                    "s1",
                    originAppVersion: "5.1.0");

                Assert.Single(schema);
                Assert.Equal("appVersion", schema[0].PropertyName);
                Assert.Equal("boolean", schema[0].PropertyType); // the property's own type, untouched

                var evt = FirstEvent(server);
                // Top-level appVersion is the option's value, a string — not the boolean property.
                var wireAppVersion = evt.GetProperty("appVersion");
                Assert.Equal(JsonValueKind.String, wireAppVersion.ValueKind);
                Assert.Equal("5.1.0", wireAppVersion.GetString());

                // ...and the property itself still rides along inside eventProperties.
                var wireProperty = evt.GetProperty("eventProperties").EnumerateArray().Single();
                Assert.Equal("appVersion", wireProperty.GetProperty("propertyName").GetString());
                Assert.Equal("boolean", wireProperty.GetProperty("propertyType").GetString());
            }
        }

        /// <summary>
        /// SPEC.md §7.3.6: the gateway fields are top-level siblings of <c>eventProperties</c>,
        /// "never nested inside the schema". Asserted against the <b>wire</b> body's
        /// <c>eventProperties</c> array (not the returned schema), because that is what the
        /// backend actually receives — a serializer that appended the options to the schema on the
        /// way out would still return a clean list to the caller.
        /// </summary>
        [Fact]
        public async Task Options_never_leak_into_eventProperties()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.0.0", batchSize: 1);

                await inspector.TrackSchemaFromEvent(
                    "E",
                    Props.Of(("amount", 9.99)),
                    "s1",
                    outputReference: "meta-x7k2q", originHint: "web", originAppVersion: "5.1.0");

                var evt = FirstEvent(server);
                var wirePropertyNames = evt.GetProperty("eventProperties").EnumerateArray()
                    .Select(e => e.GetProperty("propertyName").GetString()).ToArray();

                Assert.Equal(new[] { "amount" }, wirePropertyNames);
                Assert.DoesNotContain("outputReference", wirePropertyNames);
                Assert.DoesNotContain("originHint", wirePropertyNames);
                Assert.DoesNotContain("appVersion", wirePropertyNames);

                // ...and they are present exactly once each, as top-level siblings.
                Assert.Equal("meta-x7k2q", evt.GetProperty("outputReference").GetString());
                Assert.Equal("web", evt.GetProperty("originHint").GetString());
                Assert.Equal("5.1.0", evt.GetProperty("appVersion").GetString());
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
                    outputReference: "meta-x7k2q", originHint: "web", originAppVersion: "5.1.0");

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
                    outputReference: "meta-x7k2q", originHint: "web", originAppVersion: "5.1.0");

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
                    outputReference: "meta-x7k2q", originHint: "web", originAppVersion: "5.1.0");

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
                    outputReference: "meta-x7k2q", originHint: "web", originAppVersion: "5.1.0");
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
                    outputReference: "meta-x7k2q", originHint: "web", originAppVersion: "5.1.0");

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
                    outputReference: "meta-x7k2q", originHint: "web", originAppVersion: "5.1.0");

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
                    outputReference: "meta-x7k2q");

                var evt = FirstEvent(server);
                Assert.Equal("meta-x7k2q", evt.GetProperty("outputReference").GetString());
                Assert.Equal("1.4.2", evt.GetProperty("appVersion").GetString()); // unscoped: constructor version
                Assert.False(evt.TryGetProperty("originHint", out _));
            }
        }

        /// <summary>
        /// Mirror of <see cref="OutputReference_alone_leaves_appVersion_at_constructor_value_and_omits_originHint"/>
        /// for the other coordinate, and of conformance fixture <c>wire-10</c>: with only
        /// <c>OriginHint</c> set, the <c>outputReference</c> key MUST be absent from the wire body
        /// entirely (SPEC.md §7.3.6 omission rule — never <c>null</c>, never <c>""</c>), the event
        /// is source-scoped, and <c>appVersion</c> is a literal JSON <c>null</c> rather than the
        /// constructor's version.
        /// </summary>
        [Fact]
        public async Task OriginHint_alone_omits_outputReference_and_sends_null_appVersion()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.4.2", batchSize: 1);

                await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1",
                    originHint: "web");

                var evt = FirstEvent(server);
                Assert.False(evt.TryGetProperty("outputReference", out _)); // key absent, not null/""
                Assert.Equal("web", evt.GetProperty("originHint").GetString());
                Assert.Equal(JsonValueKind.Null, evt.GetProperty("appVersion").ValueKind);
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
                    originAppVersion: "5.1.0");

                var evt = FirstEvent(server);
                Assert.Equal("5.1.0", evt.GetProperty("appVersion").GetString()); // unscoped override, trimmed
                Assert.False(evt.TryGetProperty("outputReference", out _));
                Assert.False(evt.TryGetProperty("originHint", out _));
            }
        }

        /// <summary>
        /// The primary multi-gate use case (SPEC.md §4.2.1: "Two calls for the same event with
        /// different <c>outputReference</c> values are two distinct observations and MUST both be
        /// sent (there is no deduplication in server SDKs)"; conformance fixture <c>batch-7</c>).
        /// The two calls are identical apart from <c>OutputReference</c>, so a dedup/caching bug
        /// would collapse them into one request — or, worse, reuse the first body's coordinate for
        /// the second.
        /// </summary>
        [Fact]
        public async Task Two_calls_differing_only_in_OutputReference_send_two_bodies_with_identical_eventProperties()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector("k", "staging", "1.4.2", batchSize: 1);
                var properties = Props.Of(("amount", 9.99), ("currency", "EUR"));

                await inspector.TrackSchemaFromEvent("Purchase Completed", properties, "s1",
                    outputReference: "meta-x7k2q");
                await inspector.TrackSchemaFromEvent("Purchase Completed", properties, "s1",
                    outputReference: "ga4-z9k1p");

                Assert.Equal(2, server.RequestCount); // nothing deduplicated

                var first = FirstEvent(server, 0);
                var second = FirstEvent(server, 1);

                // Each observation carries its own outputReference...
                Assert.Equal("meta-x7k2q", first.GetProperty("outputReference").GetString());
                Assert.Equal("ga4-z9k1p", second.GetProperty("outputReference").GetString());

                // ...over byte-identical eventProperties, and everything else about the two bodies
                // matches too (same event name, stream, and constructor appVersion).
                Assert.Equal(
                    first.GetProperty("eventProperties").GetRawText(),
                    second.GetProperty("eventProperties").GetRawText());
                Assert.Equal("Purchase Completed", second.GetProperty("eventName").GetString());
                Assert.Equal("s1", second.GetProperty("streamId").GetString());
                Assert.Equal("1.4.2", first.GetProperty("appVersion").GetString());
                Assert.Equal("1.4.2", second.GetProperty("appVersion").GetString());

                // Distinct observations, so distinct messageIds.
                Assert.NotEqual(
                    first.GetProperty("messageId").GetString(),
                    second.GetProperty("messageId").GetString());
            }
        }

        /// <summary>
        /// Row 2 of the SPEC.md §7.3.6 table (<c>originHint</c> set, no usable <c>originAppVersion</c>)
        /// is an ordinary, fully supported call on <c>/inspector/v2/track</c>: the endpoint decodes
        /// both coordinates and stores a <c>null</c> <c>appVersion</c> as <c>"unversioned"</c>. It
        /// MUST therefore be silent. Through spec 2.1.0 this SDK emitted a one-shot stderr warning
        /// here, because <c>/inspector/v1/track</c> discarded the coordinates and dropped the event;
        /// this test is the regression guard that the warning does not come back. The gates are
        /// deliberately wide open — a <c>dev</c> instance (logging on by default) plus an explicit
        /// <c>EnableLogging(true)</c> — so silence is a real observation, not a disabled logger. The
        /// non-vacuous half is the assertion that the other <c>Logger</c> call sites still work: a
        /// colon-bearing <c>streamId</c> on the same instance does reach stderr.
        /// </summary>
        [Fact]
        public async Task OriginHint_without_usable_OriginAppVersion_is_silent_on_v2()
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            using (var console = new ConsoleErrorScope())
            {
                var dev = new AvoInspector("k", "dev", "1.0.0");
                dev.EnableLogging(true);
                Assert.True(AvoInspector.ShouldLogForTesting);
                try
                {
                    for (var i = 0; i < 5; i++)
                    {
                        await dev.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1",
                            outputReference: "meta-x7k2q", originHint: "web");
                    }

                    // Nothing at all on stderr, and in particular no trace of the retired warning.
                    Assert.Equal(string.Empty, console.Output);

                    // ... and the appVersion still resolves to a literal JSON null on the wire.
                    Assert.Equal(JsonValueKind.Null, FirstEvent(server).GetProperty("appVersion").ValueKind);

                    // Proves the arrangement is live: an unrelated Logger call site on this very
                    // instance, with these very gates, does write to stderr.
                    await dev.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "has:colon");
                    Assert.Contains("streamId contains ':'", console.Output);
                }
                finally
                {
                    dev.EnableLogging(false);
                    dev.Destroy();
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
        public void All_three_coordinate_parameters_have_xmldoc_descriptions()
        {
            var xmlPath = Path.ChangeExtension(typeof(AvoInspector).Assembly.Location, ".xml");

            Assert.True(File.Exists(xmlPath),
                "Expected the compiled XML doc file at \"" + xmlPath + "\" (copied next to " +
                "AvoInspector.dll via ProjectReference + GenerateDocumentationFile=true). A " +
                "missing file means either GenerateDocumentationFile was turned off or " +
                "AvoInspector.Tests.csproj's reference kind reverted to PackageReference — " +
                "both real regressions this test exists to catch.");

            var doc = XDocument.Load(xmlPath);

            // SPEC.md §4.2.1 flattens the three into parameters for a language with named
            // arguments, so what needs documenting is each <param>, not a type's properties.
            const string TrackWithCoordinates =
                "M:Avo.Inspector.AvoInspector.TrackSchemaFromEvent(System.String," +
                "System.Collections.Generic.IDictionary{System.String,System.Object}," +
                "System.String,System.String,System.String,System.String)";

            AssertHasNonEmptySummary(doc, TrackWithCoordinates);
            AssertHasNonEmptyParam(doc, TrackWithCoordinates, "outputReference");
            AssertHasNonEmptyParam(doc, TrackWithCoordinates, "originHint");
            AssertHasNonEmptyParam(doc, TrackWithCoordinates, "originAppVersion");
        }

        private static void AssertHasNonEmptyParam(XDocument doc, string memberName, string paramName)
        {
            var member = doc.Descendants("member")
                .FirstOrDefault(m => (string?)m.Attribute("name") == memberName);
            Assert.True(member != null, "No <member name=\"" + memberName + "\"> entry found in the XML doc file.");

            var param = member!.Elements("param")
                .FirstOrDefault(p => (string?)p.Attribute("name") == paramName);
            Assert.True(param != null,
                "<member name=\"" + memberName + "\"> has no <param name=\"" + paramName + "\"> element.");
            Assert.False(string.IsNullOrWhiteSpace(param!.Value),
                "<param name=\"" + paramName + "\"> is empty.");
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
