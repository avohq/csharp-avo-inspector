using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avo.Inspector;

namespace Avo.Inspector.Conformance
{
    /// <summary>
    /// Thin conformance harness (<c>avo-inspector-conformance</c>) implementing the stdin/stdout
    /// JSON protocol in <c>conformance/runner-contract.md</c>. It contains NO assertion logic — it
    /// parses the input envelope, constructs an <see cref="AvoInspector"/>, runs the requested
    /// operation, and writes exactly one output envelope line to stdout. All diagnostics go to
    /// stderr. HARNESS_CONTRACT_VERSION: 1.0.0.
    /// </summary>
    internal static class Program
    {
        private const string InternalRejectionMessage =
            "Avo Inspector: something went wrong. Please report to support@avo.app.";

        private static readonly JsonSerializerOptions OutputOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static async Task<int> Main()
        {
            string raw;
            try
            {
                raw = Console.In.ReadToEnd();
            }
            catch (Exception ex)
            {
                ConfigError(null, "stdin read failed: " + ex.Message);
                return 2;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(raw);
            }
            catch (Exception ex)
            {
                ConfigError(null, "input JSON parse failed: " + ex.Message);
                return 2;
            }

            using (document)
            {
                var envelope = document.RootElement;

                var fixtureId = GetString(envelope, "fixture_id");
                if (fixtureId == null)
                {
                    ConfigError(null, "missing fixture_id");
                    return 2;
                }

                var suite = GetString(envelope, "suite");
                var operation = GetString(envelope, "operation");
                if (operation == null && suite == "schema-extraction")
                {
                    operation = "extractSchema";
                }

                // Construct the instance (contract step 3). A constructor throw is a harness-level
                // failure (passed:false, exit 1) — the envelope itself was well-formed.
                AvoInspector inspector;
                try
                {
                    inspector = ConstructInspector(envelope);
                }
                catch (Exception ex)
                {
                    WriteEnvelope(fixtureId, passed: false, actual: null, outcome: "resolve",
                        error: "Constructor threw: " + ex.Message);
                    return 1;
                }

                try
                {
                    if (!ApplyPreconditions(inspector, envelope, fixtureId))
                    {
                        return 2; // ConfigError already written.
                    }

                    switch (operation)
                    {
                        case "extractSchema":
                            return RunExtractSchema(inspector, envelope, fixtureId);
                        case "trackSchemaFromEvent":
                            return await RunTrack(inspector, envelope, fixtureId).ConfigureAwait(false);
                        case "sequence":
                            return await RunSequence(inspector, envelope, fixtureId).ConfigureAwait(false);
                        default:
                            ConfigError(fixtureId, "unsupported operation: " + (operation ?? "<null>"));
                            return 2;
                    }
                }
                catch (Exception ex)
                {
                    // Unhandled runtime error after the envelope was parsed — exit code 1.
                    WriteEnvelope(fixtureId, passed: false, actual: null, outcome: "resolve",
                        error: "harness runtime error: " + ex.Message);
                    return 1;
                }
            }
        }

        // ----- operations -----------------------------------------------------------------------

        private static int RunExtractSchema(AvoInspector inspector, JsonElement envelope, string fixtureId)
        {
            IDictionary<string, object?>? properties = null;
            if (envelope.TryGetProperty("input", out var input))
            {
                // The entire `input` field IS the eventProperties argument; null passes through.
                properties = JsonInterop.ToPropertyMap(input);
            }
            var actual = inspector.ExtractSchema(properties);
            WriteEnvelope(fixtureId, passed: true, actual: actual, outcome: "resolve", error: null);
            return 0;
        }

        private static async Task<int> RunTrack(AvoInspector inspector, JsonElement envelope, string fixtureId)
        {
            var input = envelope.TryGetProperty("input", out var inputEl) ? inputEl : default;
            var eventName = input.ValueKind == JsonValueKind.Object ? GetString(input, "eventName") : null;
            IDictionary<string, object?>? properties = null;
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("eventProperties", out var propsEl))
            {
                properties = JsonInterop.ToPropertyMap(propsEl);
            }
            var streamId = input.ValueKind == JsonValueKind.Object ? GetString(input, "streamId") : null;

            object? actual;
            string outcome;
            try
            {
                actual = await CallTrack(inspector, eventName ?? string.Empty, properties, streamId).ConfigureAwait(false);
                outcome = "resolve";
            }
            catch (Exception ex)
            {
                outcome = "reject";
                actual = ex is AvoInspectorTrackException ? InternalRejectionMessage : ex.Message;
            }
            WriteEnvelope(fixtureId, passed: true, actual: actual, outcome: outcome, error: null);
            return 0;
        }

        private static async Task<int> RunSequence(AvoInspector inspector, JsonElement envelope, string fixtureId)
        {
            if (!envelope.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            {
                ConfigError(fixtureId, "sequence operation requires a steps array");
                return 2;
            }

            var records = new List<StepRecord>();
            foreach (var step in steps.EnumerateArray())
            {
                var action = GetString(step, "action");
                switch (action)
                {
                    case "track":
                    {
                        var eventName = GetString(step, "eventName") ?? string.Empty;
                        IDictionary<string, object?>? properties = null;
                        if (step.TryGetProperty("eventProperties", out var propsEl))
                        {
                            properties = JsonInterop.ToPropertyMap(propsEl);
                        }
                        var streamId = GetString(step, "streamId");
                        var value = await CallTrack(inspector, eventName, properties, streamId).ConfigureAwait(false);
                        records.Add(new StepRecord("track", "resolve", value));
                        break;
                    }
                    case "trackN":
                    {
                        if (!TryGetInt(step, "count", out var count) || count < 1)
                        {
                            ConfigError(fixtureId, "trackN requires an integer count >= 1");
                            return 2;
                        }
                        var prefix = GetString(step, "eventNamePrefix") ?? string.Empty;
                        var streamId = GetString(step, "streamId") ?? string.Empty;
                        await RunTrackN(inspector, count, prefix, streamId).ConfigureAwait(false);
                        records.Add(new StepRecord("trackN", "resolve", count));
                        break;
                    }
                    case "flush":
                    {
                        if (TryGetInt(step, "timeoutMs", out var timeoutMs))
                        {
                            await inspector.Flush(timeoutMs).ConfigureAwait(false);
                        }
                        else
                        {
                            await inspector.Flush().ConfigureAwait(false);
                        }
                        records.Add(new StepRecord("flush", "resolve", null));
                        break;
                    }
                    case "destroy":
                    {
                        inspector.Destroy();
                        records.Add(new StepRecord("destroy", "resolve", null));
                        break;
                    }
                    default:
                        ConfigError(fixtureId, "unsupported sequence action: " + (action ?? "<null>"));
                        return 2;
                }
            }

            WriteEnvelope(fixtureId, passed: true, actual: records, outcome: "resolve", error: null);
            return 0;
        }

        private static async Task RunTrackN(AvoInspector inspector, int count, string prefix, string streamId)
        {
            // Fire `count` genuinely-concurrent tracks (real thread-pool parallelism) and join all
            // before resolving — exercises the atomic swap-and-clear (SPEC.md §3.1, §12.4).
            var tasks = new List<Task>(count);
            for (var i = 0; i < count; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    await inspector.TrackSchemaFromEvent(
                        prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        new OrderedPropertyDictionary(),
                        streamId).ConfigureAwait(false);
                }));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private static Task<IReadOnlyList<SchemaEntry>> CallTrack(
            AvoInspector inspector, string eventName, IDictionary<string, object?>? properties, string? streamId)
        {
            // Omit the third argument when no streamId is provided (contract-correct arity).
            return streamId == null
                ? inspector.TrackSchemaFromEvent(eventName, properties)
                : inspector.TrackSchemaFromEvent(eventName, properties, streamId);
        }

        // ----- construction + preconditions -----------------------------------------------------

        private static AvoInspector ConstructInspector(JsonElement envelope)
        {
            if (!envelope.TryGetProperty("constructor", out var ctor) || ctor.ValueKind != JsonValueKind.Object)
            {
                // No constructor block: let the SDK throw on the missing apiKey.
                return new AvoInspector(apiKey: null!, env: "dev", version: null!);
            }

            var apiKey = GetString(ctor, "apiKey");
            var env = GetString(ctor, "env");
            var version = GetString(ctor, "version");
            var appName = GetString(ctor, "appName");
            int? batchSize = TryGetInt(ctor, "batchSize", out var bs) ? bs : (int?)null;
            double? batchFlushSeconds = TryGetDouble(ctor, "batchFlushSeconds", out var bfs) ? bfs : (double?)null;
            int? maxQueueSize = TryGetInt(ctor, "maxQueueSize", out var mqs) ? mqs : (int?)null;
            bool? disableBatchTimer = TryGetBool(ctor, "disableBatchTimer", out var dbt) ? dbt : (bool?)null;

            return new AvoInspector(apiKey!, env!, version!, appName, batchSize, batchFlushSeconds, maxQueueSize, disableBatchTimer);
        }

        private static bool ApplyPreconditions(AvoInspector inspector, JsonElement envelope, string fixtureId)
        {
            if (!envelope.TryGetProperty("precondition", out var precondition)
                || precondition.ValueKind != JsonValueKind.Object)
            {
                return true;
            }
            foreach (var field in precondition.EnumerateObject())
            {
                if (field.Name == "samplingRate")
                {
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetDouble(out var rate))
                    {
                        ConfigError(fixtureId, "precondition.samplingRate must be a number");
                        return false;
                    }
                    inspector.SetSamplingRateForTesting(rate);
                }
                else
                {
                    ConfigError(fixtureId, "unsupported precondition field: " + field.Name);
                    return false;
                }
            }
            return true;
        }

        // ----- envelope IO ----------------------------------------------------------------------

        private static void WriteEnvelope(string? fixtureId, bool passed, object? actual, string outcome, string? error)
        {
            var envelope = new OutputEnvelope
            {
                FixtureId = fixtureId,
                Passed = passed,
                Actual = actual,
                Outcome = outcome,
                Error = error
            };
            var json = JsonSerializer.Serialize(envelope, OutputOptions);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            using (var stdout = Console.OpenStandardOutput())
            {
                stdout.Write(bytes, 0, bytes.Length);
                stdout.Flush();
            }
        }

        private static void ConfigError(string? fixtureId, string message)
        {
            WriteEnvelope(fixtureId, passed: false, actual: null, outcome: "resolve", error: message);
        }

        // ----- JSON accessors -------------------------------------------------------------------

        private static string? GetString(JsonElement element, string name)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
            return null;
        }

        private static bool TryGetInt(JsonElement element, string name, out int result)
        {
            result = 0;
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out result))
            {
                return true;
            }
            return false;
        }

        private static bool TryGetDouble(JsonElement element, string name, out double result)
        {
            result = 0;
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out result))
            {
                return true;
            }
            return false;
        }

        private static bool TryGetBool(JsonElement element, string name, out bool result)
        {
            result = false;
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
            {
                result = value.GetBoolean();
                return true;
            }
            return false;
        }
    }

    /// <summary>The single output envelope written to stdout (runner-contract).</summary>
    internal sealed class OutputEnvelope
    {
        [JsonPropertyName("fixture_id")] public string? FixtureId { get; set; }
        [JsonPropertyName("passed")] public bool Passed { get; set; }
        [JsonPropertyName("actual")] public object? Actual { get; set; }
        [JsonPropertyName("outcome")] public string Outcome { get; set; } = "resolve";
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    /// <summary>One per-step record for a <c>sequence</c> operation (runner-contract).</summary>
    internal sealed class StepRecord
    {
        public StepRecord(string action, string outcome, object? value)
        {
            Action = action;
            Outcome = outcome;
            Value = value;
        }

        [JsonPropertyName("action")] public string Action { get; }
        [JsonPropertyName("outcome")] public string Outcome { get; }
        [JsonPropertyName("value")] public object? Value { get; }
    }
}
