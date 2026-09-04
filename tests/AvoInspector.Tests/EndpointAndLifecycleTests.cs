using System;
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
        /// transport backstop: with the guard removed, net8.0 transmits every one of these. The CR
        /// and CRLF keys arrive with <c>X-Injected</c> parsed as a genuine separate header, the LF
        /// key folds into one corrupted value, and the NUL key reaches the server, which rejects it.
        /// The sender MUST therefore reject the batch itself — nothing transmitted,
        /// <c>SendStatus.Error</c> returned. The vector is specific to
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
        /// A key pasted with surrounding whitespace — a trailing newline is the classic one — is
        /// repaired, not rejected. The constructor trims once and stores only the trimmed value, so
        /// the <c>api-key</c> header and the body's <c>apiKey</c> copy carry the identical trimmed
        /// token; the conformance runner asserts that agreement. Without the trim this key would
        /// fail the header guard on every send, which in a sender that never throws at the caller
        /// means losing all telemetry for the life of the process — silent in prod, where logging is
        /// off by default. Trimming costs no security: the embedded-control-character cases above
        /// survive <c>Trim</c> and are still rejected.
        /// </summary>
        [Theory]
        [InlineData("  my-inspector-key  ")]
        [InlineData("my-inspector-key\n")]
        [InlineData("my-inspector-key\r\n")]
        [InlineData("\tmy-inspector-key\t")]
        public async Task ApiKey_with_surrounding_whitespace_is_trimmed_in_both_header_and_body(string key)
        {
            using var server = new TestInspectorServer();
            using (new MockEndpointScope(server.BaseUrl))
            {
                var inspector = new AvoInspector(key, "staging", "1.0.0", batchSize: 1);
                await inspector.TrackSchemaFromEvent("E", Props.Of(("x", 1)), "s1");
                await inspector.Flush();

                var request = Assert.Single(server.Requests); // it sends, rather than failing closed
                Assert.Equal("my-inspector-key", request.Header("api-key"));

                using var doc = JsonDocument.Parse(request.Body);
                Assert.Equal("my-inspector-key", doc.RootElement[0].GetProperty("apiKey").GetString());
            }
        }

        /// <summary>
        /// Trimming does not weaken the guard. <c>Trim</c> strips only surrounding whitespace, so a
        /// control character with content on both sides survives it, and NUL survives even in
        /// trailing position because NUL is not whitespace. What survives is fatal: SPEC.md §4.1
        /// requires the constructor to throw with its exact message, so the mistake is caught at
        /// configuration time rather than becoming events that never arrive.
        /// </summary>
        [Theory]
        [InlineData("  key\r\nX-Injected: 1  ")] // embedded CRLF, whitespace stripped from around it
        [InlineData("  key\0truncated  ")]       // embedded NUL
        [InlineData("  key\0  ")]                // trailing NUL: not whitespace, so Trim leaves it
        public void Trimming_does_not_rescue_a_key_whose_control_character_survives_it(string key)
        {
            var ex = Assert.Throws<ArgumentException>(
                () => new AvoInspector(key, "staging", "1.0.0", batchSize: 1));

            Assert.Equal(
                "[Avo Inspector] API key contains a control character. The API key is sent as a "
                + "request header and cannot contain CR, LF, or NUL.",
                ex.Message);
        }

        /// <summary>
        /// SPEC.md §4.1 is unaffected by the trim: a whitespace-only key is still empty after
        /// trimming, so it still throws with the exact spec message.
        /// </summary>
        [Theory]
        [InlineData("   ")]
        [InlineData("\r\n")]
        [InlineData("\t")]
        public void Whitespace_only_apiKey_still_throws_the_exact_spec_message(string key)
        {
            var ex = Assert.Throws<ArgumentException>(() => new AvoInspector(key, "staging", "1.0.0"));
            Assert.Equal("[Avo Inspector] No API key provided. Inspector can't operate without API key.", ex.Message);
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
        /// The two §4.1 apiKey rejections are distinct and each keeps its own exact message, so a
        /// caller reading the exception learns which mistake they made. Ordering matters: emptiness
        /// is judged after the trim, so a whitespace-only key is "no API key" and never reaches the
        /// control-character check.
        /// </summary>
        [Fact]
        public void The_two_apiKey_rejections_keep_distinct_exact_messages()
        {
            var empty = Assert.Throws<ArgumentException>(() => new AvoInspector("  ", "staging", "1.0.0"));
            var control = Assert.Throws<ArgumentException>(() => new AvoInspector("k\r\nX: 1", "staging", "1.0.0"));

            Assert.Equal("[Avo Inspector] No API key provided. Inspector can't operate without API key.", empty.Message);
            Assert.StartsWith("[Avo Inspector] API key contains a control character.", control.Message);
            Assert.NotEqual(empty.Message, control.Message);
        }

        /// <summary>
        /// The §7.2 send-time refusal is deliberately redundant with the §4.1 constructor throw and
        /// is what actually guards the wire, so it is pinned directly on the sender — the only way
        /// in now that the constructor rejects such a key outright. Nothing is transmitted: measured
        /// on net8.0, removing this guard lets SocketsHttpHandler write the value through and the
        /// server parses the injected text as a genuine header.
        /// </summary>
        [Fact]
        public async Task Sender_refuses_a_crlf_key_and_nothing_reaches_the_wire()
        {
            const string key = "key\r\nX-Injected: 1";
            using var server = new TestInspectorServer();

            var result = await InspectorHttpSender.SendAsync(
                server.BaseUrl, key, "staging", OneEvent(key), shouldLog: false);

            Assert.Equal(SendStatus.Error, result.Status);
            Assert.Equal(0, server.RequestCount);
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
