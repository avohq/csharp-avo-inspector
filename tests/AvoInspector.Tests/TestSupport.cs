using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avo.Inspector.Conformance;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>Temporarily sets <c>AVO_INSPECTOR_MOCK_ENDPOINT</c>, restoring it on dispose.</summary>
    internal sealed class MockEndpointScope : IDisposable
    {
        private const string Var = "AVO_INSPECTOR_MOCK_ENDPOINT";
        private readonly string? _previous;

        public MockEndpointScope(string? value)
        {
            _previous = Environment.GetEnvironmentVariable(Var);
            Environment.SetEnvironmentVariable(Var, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(Var, _previous);
    }

    internal static class TestNet
    {
        /// <summary>Returns a currently-free loopback TCP port.</summary>
        public static int FreePort()
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            return port;
        }
    }

    /// <summary>Builds native event-property maps for parser tests.</summary>
    internal static class Props
    {
        public static IDictionary<string, object?> Of(params (string Key, object? Value)[] entries)
        {
            var map = new OrderedPropertyDictionary();
            foreach (var (key, value) in entries)
            {
                map[key] = value;
            }
            return map;
        }
    }

    /// <summary>Order-sensitive (arrays) / order-insensitive (object keys) JSON structural equality.</summary>
    internal static class JsonAssert
    {
        public static void Equal(string expectedJson, string actualJson)
        {
            using var expected = JsonDocument.Parse(expectedJson);
            using var actual = JsonDocument.Parse(actualJson);
            if (!DeepEqual(expected.RootElement, actual.RootElement))
            {
                Assert.Fail($"JSON mismatch.\n  expected: {expectedJson}\n  actual:   {actualJson}");
            }
        }

        public static bool DeepEqual(JsonElement a, JsonElement b)
        {
            if (a.ValueKind != b.ValueKind)
            {
                // Tolerate int/float spelling differences (e.g. 1 vs 1.0) for numbers.
                if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
                {
                    return a.GetDouble() == b.GetDouble();
                }
                return false;
            }

            switch (a.ValueKind)
            {
                case JsonValueKind.Object:
                    var aProps = new Dictionary<string, JsonElement>();
                    foreach (var p in a.EnumerateObject()) aProps[p.Name] = p.Value;
                    var bCount = 0;
                    foreach (var p in b.EnumerateObject())
                    {
                        bCount++;
                        if (!aProps.TryGetValue(p.Name, out var av) || !DeepEqual(av, p.Value))
                        {
                            return false;
                        }
                    }
                    return bCount == aProps.Count;

                case JsonValueKind.Array:
                    var ae = a.EnumerateArray();
                    var be = b.EnumerateArray();
                    while (true)
                    {
                        var ha = ae.MoveNext();
                        var hb = be.MoveNext();
                        if (ha != hb) return false;
                        if (!ha) return true;
                        if (!DeepEqual(ae.Current, be.Current)) return false;
                    }

                case JsonValueKind.String:
                    return a.GetString() == b.GetString();

                case JsonValueKind.Number:
                    return a.GetDouble() == b.GetDouble();

                default:
                    return true; // True/False/Null are equal when kinds match
            }
        }
    }
}
