using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Statically-typed schema-parser invariants not exercised by the universal fixtures:
    /// whole-valued float classification (SPEC.md §9.3.1) and recursion-depth safety (§9.3.2).
    /// </summary>
    public class SchemaParserEdgeTests
    {
        private static readonly AvoInspector Inspector = new AvoInspector("k", "dev", "1.0.0");

        private enum Color { Red, Green }

        [Fact]
        public void Whole_valued_float_classifies_as_float()
        {
            // SPEC.md §9.3.1 MUST for statically-typed languages: 0.0/1.0 are floats by runtime type.
            var schema = Inspector.ExtractSchema(Props.Of(
                ("zero", 0.0),
                ("one", 1.0),
                ("pi", 3.14),
                ("intZero", 0),
                ("big", 42L)));

            Assert.Equal("float", TypeOf(schema, "zero"));
            Assert.Equal("float", TypeOf(schema, "one"));
            Assert.Equal("float", TypeOf(schema, "pi"));
            Assert.Equal("int", TypeOf(schema, "intZero"));
            Assert.Equal("int", TypeOf(schema, "big"));
        }

        [Fact]
        public void Float_and_int_lists_classify_by_first_element_runtime_type()
        {
            var schema = Inspector.ExtractSchema(Props.Of(
                ("floats", new List<object?> { 1.0, 2.0 }),
                ("ints", new List<object?> { 1, 2 })));

            Assert.Equal("list(float)", TypeOf(schema, "floats"));
            Assert.Equal("list(int)", TypeOf(schema, "ints"));
        }

        [Fact]
        public void Decimal_classifies_as_float()
        {
            var schema = Inspector.ExtractSchema(Props.Of(("amount", 9.99m)));
            Assert.Equal("float", TypeOf(schema, "amount"));
        }

        [Fact]
        public void Common_clr_string_like_types_classify_as_string()
        {
            // .NET types with no JSON-primitive equivalent that serialize to strings on the wire.
            var schema = Inspector.ExtractSchema(Props.Of(
                ("when", DateTime.UtcNow),
                ("whenOffset", DateTimeOffset.UtcNow),
                ("span", TimeSpan.FromSeconds(5)),
                ("id", Guid.NewGuid()),
                ("ch", 'x'),
                ("uri", new Uri("https://avo.app"))));

            foreach (var name in new[] { "when", "whenOffset", "span", "id", "ch", "uri" })
            {
                Assert.Equal("string", TypeOf(schema, name));
            }
        }

        [Fact]
        public void Enum_classifies_as_unknown_and_list_of_unknown_normalizes_to_list_object()
        {
            var schema = Inspector.ExtractSchema(Props.Of(
                ("color", Color.Red),
                ("colors", new List<object?> { Color.Red, Color.Green })));

            Assert.Equal("unknown", TypeOf(schema, "color"));
            // "list(unknown)" is not in the spec's propertyType enum, so it normalizes to "list(object)".
            Assert.Equal("list(object)", TypeOf(schema, "colors"));
        }

        [Fact]
        public void List_of_string_like_clr_types_classifies_as_list_string()
        {
            var schema = Inspector.ExtractSchema(Props.Of(
                ("dates", new List<object?> { DateTime.UtcNow, DateTime.UtcNow })));
            Assert.Equal("list(string)", TypeOf(schema, "dates"));
        }

        [Fact]
        public void Nested_non_object_valued_dictionaries_map_as_objects()
        {
            // A nested map whose value type is NOT object? (e.g. Dictionary<string,int>) must still
            // map to "object" with recursive children — not fall through to the array branch and
            // serialize as a list of KeyValuePair entries.
            var schema = Inspector.ExtractSchema(Props.Of(
                ("counts", new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }),
                ("labels", new Dictionary<string, string> { ["x"] = "hi" })));

            var counts = schema.First(e => e.PropertyName == "counts");
            Assert.Equal("object", counts.PropertyType);
            var countsChildren = Assert.IsType<List<SchemaEntry>>(counts.Children);
            Assert.Equal(2, countsChildren.Count);
            Assert.All(countsChildren, c => Assert.Equal("int", c.PropertyType));

            var labels = schema.First(e => e.PropertyName == "labels");
            Assert.Equal("object", labels.PropertyType);
            var labelsChildren = Assert.IsType<List<SchemaEntry>>(labels.Children);
            Assert.Equal("string", labelsChildren.Single().PropertyType);
        }

        [Fact]
        public void Deeply_nested_input_does_not_throw_and_truncates()
        {
            // SPEC.md §9.3.2: the parser MUST NOT crash on pathological nesting; beyond the depth
            // limit a complex value is included as object with empty children.
            IDictionary<string, object?> node = Props.Of(("leaf", 1));
            for (var i = 0; i < 25; i++)
            {
                node = Props.Of(("child", node));
            }

            var schema = Inspector.ExtractSchema(node);
            Assert.Single(schema);
            Assert.Equal("object", schema[0].PropertyType);

            // Walk down until truncation: a truncated node is "object" with empty children.
            var truncationFound = false;
            var current = schema[0];
            for (var depth = 0; depth < 30 && current.Children is List<SchemaEntry> kids; depth++)
            {
                if (kids.Count == 0)
                {
                    truncationFound = true;
                    break;
                }
                current = kids[0];
            }
            Assert.True(truncationFound, "expected depth truncation to produce an empty-children object");
        }

        private static string TypeOf(IReadOnlyList<SchemaEntry> schema, string name)
            => schema.First(e => e.PropertyName == name).PropertyType;
    }
}
