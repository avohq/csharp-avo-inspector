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
