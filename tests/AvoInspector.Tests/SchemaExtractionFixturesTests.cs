using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avo.Inspector.Conformance;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Data-driven verification of all 13 golden schema-extraction fixtures (SPEC.md §10) against the
    /// vendored <c>conformance/fixtures/schema-extraction/fixtures.json</c>. Self-contained — does
    /// not require the Node suite-runner.
    /// </summary>
    public class SchemaExtractionFixturesTests
    {
        private static readonly AvoInspector Inspector = new AvoInspector("k", "dev", "1.0.0");

        public static IEnumerable<object[]> Fixtures()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "schema-extraction.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var fixture in doc.RootElement.EnumerateArray())
            {
                yield return new object[] { fixture.GetProperty("fixture_id").GetString()! };
            }
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Fixture_produces_expected_schema(string fixtureId)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "schema-extraction.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            JsonElement fixture = default;
            var found = false;
            foreach (var candidate in doc.RootElement.EnumerateArray())
            {
                if (candidate.GetProperty("fixture_id").GetString() == fixtureId)
                {
                    fixture = candidate.Clone();
                    found = true;
                    break;
                }
            }
            Assert.True(found, $"fixture {fixtureId} not found");

            var input = fixture.GetProperty("input");
            var properties = JsonInterop.ToPropertyMap(input);

            var actual = Inspector.ExtractSchema(properties);
            var actualJson = JsonSerializer.Serialize(actual);
            var expectedJson = fixture.GetProperty("expected").GetRawText();

            JsonAssert.Equal(expectedJson, actualJson);
        }
    }
}
