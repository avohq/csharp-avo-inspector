using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avo.Inspector;

namespace Avo.Inspector.Example
{
    /// <summary>
    /// Example CLI for the Avo Inspector C# SDK.
    ///
    ///   dotnet run                # dry-run: shows schema extraction + the exact wire payloads,
    ///                             # offline, against a local loopback sink (no credentials needed)
    ///   dotnet run -- --live      # sends to the real Avo Inspector API
    ///                             #   env:   AVO_INSPECTOR_API_KEY (required), AVO_INSPECTOR_ENV (default dev),
    ///                             #          AVO_INSPECTOR_PUBLIC_ENCRYPTION_KEY (optional)
    ///   dotnet run -- --live --event "Name" --props '{"plan":"Pro"}' [--stream-id ID]
    ///                             [--origin-hint HINT] [--output-reference REF]
    ///                             # sends one event of your own, e.g. to check values against a tracking plan
    ///   dotnet run -- --help
    /// </summary>
    internal static class Program
    {
        private static readonly JsonSerializerOptions Pretty = new JsonSerializerOptions { WriteIndented = true };

        private static async Task<int> Main(string[] args)
        {
            if (args.Contains("--help") || args.Contains("-h"))
            {
                PrintHelp();
                return 0;
            }

            var live = args.Contains("--live");

            Header("Avo Inspector — C# SDK example");
            Console.WriteLine("SDK libVersion " + InspectorVersion.LibVersion +
                              "  •  implements spec v" + InspectorVersion.SpecVersion +
                              "  •  platform \"" + InspectorVersion.LibPlatform + "\"");
            Console.WriteLine();

            SchemaShowcase();

            if (live)
            {
                return await RunLiveAsync(args);
            }

            await RunDryRunAsync();
            return 0;
        }

        // ----- 1. Schema extraction (synchronous, no network) -----------------------------------

        private static void SchemaShowcase()
        {
            Header("1. ExtractSchema — turn event properties into a typed schema (offline)");

            // A throwaway dev instance is enough to call ExtractSchema; it makes no network call.
            var inspector = new AvoInspector("demo-key", "dev", "1.0.0");

            foreach (var (name, properties) in SampleEvents())
            {
                var schema = inspector.ExtractSchema(properties);
                Console.WriteLine($"▸ {name}");
                Console.WriteLine(Indent(JsonSerializer.Serialize(schema, Pretty)));
                Console.WriteLine();
            }
        }

        // ----- 2a. Dry-run: track + flush against a local sink, then print the wire payloads ----

        private static async Task RunDryRunAsync()
        {
            Header("2. Track + batch + flush — DRY RUN against a local loopback sink");
            Console.WriteLine("(No credentials and no network egress. The sink prints exactly what the");
            Console.WriteLine(" SDK would POST to the Avo Inspector API.)");
            Console.WriteLine();

            using var sink = new LocalSink();

            // The mock-endpoint override is a TEST-ONLY hook (it is fail-closed for prod). We use it
            // here purely so this example is runnable offline — never set it in production code.
            var previous = Environment.GetEnvironmentVariable("AVO_INSPECTOR_MOCK_ENDPOINT");
            Environment.SetEnvironmentVariable("AVO_INSPECTOR_MOCK_ENDPOINT", sink.BaseUrl);
            try
            {
                // env "staging" keeps batching active (dev would force immediate, batchSize 1).
                var inspector = new AvoInspector(new AvoInspectorOptions
                {
                    ApiKey = "demo-key",
                    Env = AvoInspectorEnv.Staging,
                    Version = "1.4.2",
                    AppName = "checkout-service",
                    BatchSize = 2,             // flush after every 2 buffered events
                    DisableBatchTimer = true,  // rely on the size trigger + explicit Flush()
                });

                Console.WriteLine("track \"User Signed Up\"  -> buffered (1/2)");
                await inspector.TrackSchemaFromEvent("User Signed Up",
                    Dict(("plan", "pro"), ("seats", 3), ("isTeam", true)), streamId: "web");

                Console.WriteLine("track \"Order Placed\"    -> buffer hits batchSize, size-triggered batch sent");
                await inspector.TrackSchemaFromEvent("Order Placed", OrderProperties(), streamId: "web");

                Console.WriteLine("track \"Page Viewed\"     -> buffered (1/2)");
                await inspector.TrackSchemaFromEvent("Page Viewed",
                    Dict(("path", "/pricing")), streamId: "marketing");

                Console.WriteLine("flush()                 -> drains the partial batch and awaits all sends");
                await inspector.Flush();

                Console.WriteLine();
                PrintCaptured(sink);

                // Lifecycle note: destroy() cancels and cleans up; it does NOT flush.
                inspector.Destroy();
            }
            finally
            {
                Environment.SetEnvironmentVariable("AVO_INSPECTOR_MOCK_ENDPOINT", previous);
            }

            Header("Shutdown contract");
            Console.WriteLine("Events are at-most-once and in-memory. ALWAYS `await inspector.Flush()`");
            Console.WriteLine("before the process or serverless handler exits, or buffered/in-flight");
            Console.WriteLine("events are lost. In serverless, also set DisableBatchTimer = true.");
        }

        // ----- 2b. Live: send to the real Avo Inspector API -------------------------------------

        private static async Task<int> RunLiveAsync(string[] args)
        {
            Header("2. Track + flush — LIVE against the Avo Inspector API");

            var apiKey = Environment.GetEnvironmentVariable("AVO_INSPECTOR_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.Error.WriteLine("error: --live requires AVO_INSPECTOR_API_KEY to be set.");
                Console.Error.WriteLine("       export AVO_INSPECTOR_API_KEY=... (and optionally AVO_INSPECTOR_ENV=dev|staging|prod)");
                return 1;
            }

            var env = Environment.GetEnvironmentVariable("AVO_INSPECTOR_ENV") ?? "dev";

            // --live targets the real API. The SDK honors AVO_INSPECTOR_MOCK_ENDPOINT for any
            // non-prod env, so a leftover override (e.g. from a prior dry run) would silently
            // redirect us away from Avo. Clear it for this process so --live really goes live.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVO_INSPECTOR_MOCK_ENDPOINT")))
            {
                Console.Error.WriteLine("warning: ignoring AVO_INSPECTOR_MOCK_ENDPOINT in --live mode.");
                Environment.SetEnvironmentVariable("AVO_INSPECTOR_MOCK_ENDPOINT", null);
            }

            Console.WriteLine($"Sending to env \"{env}\" as \"{InspectorVersion.LibPlatform}\" SDK v{InspectorVersion.LibVersion} ...");

            var encryptionKey = Environment.GetEnvironmentVariable("AVO_INSPECTOR_PUBLIC_ENCRYPTION_KEY");
            var inspector = new AvoInspector(new AvoInspectorOptions
            {
                ApiKey = apiKey!,
                Env = AvoInspectorEnvExtensions.TryParse(env, out var parsedEnv) ? parsedEnv : AvoInspectorEnv.Dev,
                Version = "1.4.2",
                AppName = "checkout-service",
                PublicEncryptionKey = string.IsNullOrWhiteSpace(encryptionKey) ? null : encryptionKey,
            });

            var eventName = Option(args, "--event");
            if (eventName != null)
            {
                IDictionary<string, object?> properties;
                try
                {
                    properties = ParseProperties(Option(args, "--props") ?? "{}");
                }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine("error: --props must be a JSON object: " + ex.Message);
                    return 1;
                }
                var schema = await inspector.TrackSchemaFromEvent(
                    eventName,
                    properties,
                    Option(args, "--stream-id") ?? "example",
                    outputReference: Option(args, "--output-reference"),
                    originHint: Option(args, "--origin-hint"),
                    originAppVersion: null);
                await inspector.Flush();

                Console.WriteLine($"Tracked \"{eventName}\" and flushed. Schema:");
                Console.WriteLine(Indent(JsonSerializer.Serialize(schema, Pretty)));
                Console.WriteLine("In dev, the SDK logs its spec fetch and any value failures above.");
                return 0;
            }

            await inspector.TrackSchemaFromEvent("User Signed Up",
                Dict(("plan", "pro"), ("seats", 3), ("isTeam", true)), streamId: "web");
            await inspector.TrackSchemaFromEvent("Order Placed", OrderProperties(), streamId: "web");

            // REQUIRED before exit: deliver in-flight + buffered events (completion guarantee).
            await inspector.Flush();

            Console.WriteLine("Done. Tracked 2 events and flushed. (Network errors, if any, are swallowed");
            Console.WriteLine("by design — check the Avo Inspector dashboard to confirm receipt.)");
            return 0;
        }

        private static string? Option(string[] args, string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        /// <summary>Turns a JSON object into the plain values the SDK takes (strings, numbers, bools, lists, maps).</summary>
        private static IDictionary<string, object?> ParseProperties(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("expected an object");
            }
            return (IDictionary<string, object?>)ToValue(doc.RootElement)!;
        }

        private static object? ToValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var map = new Dictionary<string, object?>();
                    foreach (var property in element.EnumerateObject())
                    {
                        map[property.Name] = ToValue(property.Value);
                    }
                    return map;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(ToValue).ToList();
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    return element.TryGetInt64(out var integer) ? integer : (object)element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                default:
                    return null;
            }
        }

        // ----- sample data ----------------------------------------------------------------------

        private static IEnumerable<(string Name, IDictionary<string, object?> Properties)> SampleEvents()
        {
            yield return ("User Signed Up",
                Dict(("plan", "pro"), ("seats", 3), ("trialDays", 14.0), ("isTeam", true), ("referrer", null)));
            yield return ("Order Placed", OrderProperties());
        }

        private static IDictionary<string, object?> OrderProperties() => Dict(
            ("orderId", "A-1007"),
            ("total", 42.5),                       // float
            ("quantity", 3),                       // int
            ("tags", new List<object?> { "promo", "sale" }),                 // list(string)
            ("items", new List<object?>                                       // list(object)
            {
                Dict(("sku", "x1"), ("qty", 2)),
                Dict(("sku", "y9"), ("qty", 1)),
            }),
            ("coupon", null));                      // null

        // ----- helpers --------------------------------------------------------------------------

        private static IDictionary<string, object?> Dict(params (string Key, object? Value)[] entries)
        {
            var map = new Dictionary<string, object?>(entries.Length);
            foreach (var (key, value) in entries)
            {
                map[key] = value;
            }
            return map;
        }

        private static void PrintCaptured(LocalSink sink)
        {
            var requests = sink.Requests;
            Console.WriteLine($"Captured {requests.Count} HTTP POST(s) at the sink:");
            Console.WriteLine();
            for (var i = 0; i < requests.Count; i++)
            {
                var req = requests[i];
                var encoding = req.ContentEncoding ?? "(none)";
                using var doc = JsonDocument.Parse(req.JsonBody);
                var count = doc.RootElement.GetArrayLength();
                Console.WriteLine($"POST #{i + 1}  events={count}  content-type={req.ContentType}  content-encoding={encoding}  wireBytes={req.WireBytes}");
                Console.WriteLine(Indent(JsonSerializer.Serialize(doc.RootElement, Pretty)));
                Console.WriteLine();
            }
        }

        private static string Indent(string text)
            => string.Join("\n", text.Split('\n').Select(line => "    " + line));

        private static void Header(string title)
        {
            Console.WriteLine(new string('=', 72));
            Console.WriteLine(title);
            Console.WriteLine(new string('=', 72));
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Avo Inspector C# SDK — example CLI");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  dotnet run                 Dry run: schema extraction + wire payloads (offline)");
            Console.WriteLine("  dotnet run -- --live       Send to the real Avo Inspector API");
            Console.WriteLine("  dotnet run -- --live --event NAME --props JSON [--stream-id ID]");
            Console.WriteLine("               [--origin-hint HINT] [--output-reference REF]");
            Console.WriteLine("                             Send one event of your own and flush");
            Console.WriteLine("  dotnet run -- --help       Show this help");
            Console.WriteLine();
            Console.WriteLine("Environment (for --live):");
            Console.WriteLine("  AVO_INSPECTOR_API_KEY      Inspector API key (required)");
            Console.WriteLine("  AVO_INSPECTOR_ENV          dev | staging | prod (default: dev)");
            Console.WriteLine("  AVO_INSPECTOR_PUBLIC_ENCRYPTION_KEY");
            Console.WriteLine("                             Hex P-256 public key; values are sent encrypted (dev/staging)");
        }
    }
}
