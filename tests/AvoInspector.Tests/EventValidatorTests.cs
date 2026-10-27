using System;
using System.Collections.Generic;
using System.Linq;
using Avo.Inspector.Internal;
using Avo.Inspector.Internal.EventSpec;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Value checks against an event spec (AVO-4069): pinned, allowed, regex and min/max, per event
    /// ID, one level into objects and lists of objects. Ports the cases of the Node SDK's
    /// <c>TestEventValidator</c>.
    /// </summary>
    public class EventValidatorTests
    {
        // A spec with one event, "evt", whose properties carry the given constraints (wire JSON).
        private static EventSpecResponse Spec(string propsJson, string id = "evt", string vids = "")
            => EventSpecFetcher.Parse(
                "{\"events\":[{\"b\":\"main\",\"id\":\"" + id + "\",\"vids\":[" + vids + "],\"p\":" + propsJson + "}]}")!;

        private static Dictionary<string, PropertyValidationOutcome> Validate(
            EventSpecResponse spec, IDictionary<string, object?> props)
            => EventValidator.Validate(spec, AvoSchemaParser.ExtractSchema(props), props, shouldLog: false);

        private static string[]? Failed(EventSpecResponse spec, IDictionary<string, object?> props, string name)
            => Validate(spec, props).TryGetValue(name, out var outcome) ? outcome.FailedEventIds.ToArray() : null;

        // ----- allowed and pinned values ---------------------------------------------------------

        [Fact]
        public void Allowed_values_pass_and_fail()
        {
            var spec = Spec("{\"plan\":{\"t\":\"string\",\"v\":{\"[\\\"Free\\\",\\\"Pro\\\"]\":[\"evt\"]}}}");
            Assert.Null(Failed(spec, Props.Of(("plan", "Pro")), "plan"));
            Assert.Equal(new[] { "evt" }, Failed(spec, Props.Of(("plan", "Enterprise")), "plan"));
        }

        [Fact]
        public void Allowed_values_apply_per_variant()
        {
            var spec = Spec(
                "{\"plan\":{\"t\":\"string\",\"v\":{\"[\\\"Free\\\",\\\"Pro\\\"]\":[\"evt\"],\"[\\\"Pro\\\"]\":[\"var\"]}}}",
                vids: "\"var\"");
            Assert.Equal(new[] { "var" }, Failed(spec, Props.Of(("plan", "Free")), "plan"));
            Assert.Null(Failed(spec, Props.Of(("plan", "Pro")), "plan"));
        }

        [Fact]
        public void Allowed_values_compare_non_strings_by_their_JavaScript_string()
        {
            var spec = Spec("{\"n\":{\"t\":\"int\",\"v\":{\"[\\\"1\\\",\\\"2.5\\\",\\\"true\\\"]\":[\"evt\"]}}}");
            Assert.Null(Failed(spec, Props.Of(("n", 1)), "n"));
            Assert.Null(Failed(spec, Props.Of(("n", 1.0)), "n"));
            Assert.Null(Failed(spec, Props.Of(("n", 2.5m)), "n"));
            Assert.Null(Failed(spec, Props.Of(("n", true)), "n"));
            Assert.NotNull(Failed(spec, Props.Of(("n", 3)), "n"));
        }

        [Fact]
        public void Allowed_values_that_are_not_a_json_array_of_strings_are_skipped()
        {
            var spec = Spec("{\"a\":{\"t\":\"string\",\"v\":{\"not json\":[\"evt\"],\"[1,2]\":[\"evt\"]}}}");
            Assert.Empty(Validate(spec, Props.Of(("a", "anything"))));
        }

        [Fact]
        public void Pinned_values_compare_strings_and_booleans()
        {
            var spec = Spec("{\"s\":{\"t\":\"string\",\"p\":{\"web\":[\"evt\"]}},\"b\":{\"t\":\"bool\",\"p\":{\"true\":[\"evt\"]}}}");
            Assert.Empty(Validate(spec, Props.Of(("s", "web"), ("b", true))));
            var failed = Validate(spec, Props.Of(("s", "ios"), ("b", false)));
            Assert.Equal(new[] { "b", "s" }, failed.Keys.OrderBy(k => k).ToArray());
        }

        // ----- regex -----------------------------------------------------------------------------

        [Fact]
        public void Regex_patterns_match_strings_per_pattern()
        {
            var spec = Spec("{\"code\":{\"t\":\"string\",\"rx\":{\"^[A-Z]{3}$\":[\"evt\"],\"^A\":[\"var\"]}}}", vids: "\"var\"");
            Assert.Null(Failed(spec, Props.Of(("code", "ABC")), "code"));
            Assert.Equal(new[] { "evt" }, Failed(spec, Props.Of(("code", "ABCD")), "code"));
            Assert.Equal(new[] { "evt", "var" }, Failed(spec, Props.Of(("code", "xyz")), "code"));
        }

        [Fact]
        public void Regex_fails_a_non_string_and_skips_an_invalid_pattern()
        {
            var spec = Spec("{\"a\":{\"t\":\"string\",\"rx\":{\"^\\\\d+$\":[\"evt\"]}},\"b\":{\"t\":\"string\",\"rx\":{\"([\":[\"evt\"]}}}");
            Assert.Equal(new[] { "evt" }, Failed(spec, Props.Of(("a", 123)), "a"));
            Assert.Null(Failed(spec, Props.Of(("b", "x")), "b"));
        }

        [Fact]
        public void Regex_uses_JavaScript_digit_semantics()
        {
            // \d is ASCII in JavaScript; .NET's default would accept the Arabic-Indic digit.
            var spec = Spec("{\"a\":{\"t\":\"string\",\"rx\":{\"^\\\\d$\":[\"evt\"]}}}");
            Assert.Equal(new[] { "evt" }, Failed(spec, Props.Of(("a", "٣")), "a"));
            Assert.Null(Failed(spec, Props.Of(("a", "3")), "a"));
        }

        [Fact]
        public void Regex_checks_values_the_schema_reports_as_strings()
        {
            var spec = Spec("{\"id\":{\"t\":\"string\",\"rx\":{\"^[0-9a-f-]{36}$\":[\"evt\"]}}}");
            Assert.Null(Failed(spec, Props.Of(("id", Guid.NewGuid())), "id"));
        }

        [Fact]
        public void Regex_skips_a_string_over_the_length_limit()
        {
            var spec = Spec("{\"a\":{\"t\":\"string\",\"rx\":{\"^x$\":[\"evt\"]}}}");
            Assert.Null(Failed(spec, Props.Of(("a", new string('y', 10_001))), "a"));
            Assert.NotNull(Failed(spec, Props.Of(("a", new string('y', 10_000))), "a"));
        }

        [Fact]
        public void Regex_with_catastrophic_backtracking_is_checked_in_linear_time()
        {
            // On a backtracking engine this pattern needs exponential time on "aaa…a!" and would run
            // out of its budget and be skipped; the linear-time engine fails the value straight away.
            var spec = Spec("{\"a\":{\"t\":\"string\",\"rx\":{\"^(a+)+$\":[\"evt\"]}}}");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(new[] { "evt" }, Failed(spec, Props.Of(("a", new string('a', 5_000) + "!")), "a"));
            Assert.True(watch.ElapsedMilliseconds < 1_000, "took " + watch.ElapsedMilliseconds + " ms");
            Assert.Null(Failed(spec, Props.Of(("a", new string('a', 5_000))), "a"));
        }

        [Fact]
        public void Regex_the_linear_engine_cannot_take_still_runs_on_the_backtracking_engine()
        {
            var spec = Spec(
                "{\"look\":{\"t\":\"string\",\"rx\":{\"^(?=.*[0-9])[a-z0-9]+$\":[\"evt\"]}},"
                + "\"back\":{\"t\":\"string\",\"rx\":{\"^(a)\\\\1$\":[\"evt\"]}},"
                + "\"word\":{\"t\":\"string\",\"rx\":{\"\\\\bcat\\\\b\":[\"evt\"]}}}");
            Assert.Null(Failed(spec, Props.Of(("look", "abc1"), ("back", "aa"), ("word", "a cat sat")), "look"));
            Assert.Equal(new[] { "evt" }, Failed(spec, Props.Of(("look", "abc")), "look"));
            Assert.Null(Failed(spec, Props.Of(("back", "aa")), "back"));
            Assert.Equal(new[] { "evt" }, Failed(spec, Props.Of(("back", "ab")), "back"));
            Assert.Null(Failed(spec, Props.Of(("word", "a cat sat")), "word"));
            Assert.Equal(new[] { "evt" }, Failed(spec, Props.Of(("word", "concatenate")), "word"));
        }

        [Theory]
        [InlineData(@"^\d+$", "^[0-9]+$")]
        [InlineData(@"^[\d.]+$", "^[0-9.]+$")]
        [InlineData(@"\w\W\D", "[a-zA-Z0-9_][^a-zA-Z0-9_][^0-9]")]
        [InlineData(@"[\w-]", "[a-zA-Z0-9_-]")]
        [InlineData(@"\\d", @"\\d")]           // an escaped backslash, then a literal d
        [InlineData(@"[\b]", @"[\b]")]           // backspace inside a class
        [InlineData(@"^\s\.\u0041$", @"^\s\.\u0041$")]
        [InlineData(@"[\D]", null)]
        [InlineData(@"[^\W]", null)]
        [InlineData(@"\bword", null)]
        [InlineData(@"a\B", null)]
        [InlineData("[]a", null)]
        [InlineData("[^]a", null)]
        [InlineData("[a-z-[aeiou]]", null)]
        [InlineData(@"trailing\", null)]
        public void Linear_pattern_keeps_JavaScript_classes_or_declines(string pattern, string? expected)
        {
            Assert.Equal(expected, EventValidator.ToLinearPattern(pattern));
        }

        // ----- min/max ---------------------------------------------------------------------------

        [Theory]
        [InlineData(0, false)]
        [InlineData(10, false)]
        [InlineData(5.5, false)]
        [InlineData(-0.1, true)]
        [InlineData(10.01, true)]
        [InlineData(double.NaN, true)]
        public void Min_max_bounds_are_inclusive(double value, bool fails)
        {
            var spec = Spec("{\"n\":{\"t\":\"float\",\"minmax\":{\"0,10\":[\"evt\"]}}}");
            Assert.Equal(fails, Failed(spec, Props.Of(("n", value)), "n") != null);
        }

        [Fact]
        public void Min_max_with_open_bounds_non_numbers_and_bad_ranges()
        {
            var spec = Spec("{\"a\":{\"t\":\"int\",\"minmax\":{\"5,\":[\"min\"],\",7\":[\"max\"],\"x,1\":[\"bad\"],\"1,2,3\":[\"bad\"]}}}");
            Assert.Equal(new[] { "max" }, Failed(spec, Props.Of(("a", 100L)), "a"));
            Assert.Equal(new[] { "min" }, Failed(spec, Props.Of(("a", (byte)1)), "a"));
            Assert.Equal(new[] { "min", "max" }, Failed(spec, Props.Of(("a", "6")), "a")); // a string is not a number
        }

        // ----- required, null and missing --------------------------------------------------------

        [Fact]
        public void Optional_null_is_not_checked_but_required_null_is()
        {
            var optional = Spec("{\"a\":{\"t\":\"string\",\"r\":false,\"v\":{\"[\\\"x\\\"]\":[\"evt\"]}}}");
            var required = Spec("{\"a\":{\"t\":\"string\",\"r\":true,\"v\":{\"[\\\"x\\\"]\":[\"evt\"]}}}");
            Assert.Null(Failed(optional, Props.Of(("a", null)), "a"));
            Assert.Equal(new[] { "evt" }, Failed(required, Props.Of(("a", null)), "a"));
        }

        [Fact]
        public void Unconstrained_properties_are_left_out()
        {
            var spec = Spec("{\"a\":{\"t\":\"string\",\"v\":{\"[\\\"x\\\"]\":[\"evt\"]}}}");
            var failed = Validate(spec, Props.Of(("a", "y"), ("other", "z")));
            Assert.Equal(new[] { "a" }, failed.Keys.ToArray());
        }

        // ----- objects and lists -----------------------------------------------------------------

        [Fact]
        public void Object_children_fail_on_the_child_one_level_deep()
        {
            var spec = Spec("{\"address\":{\"t\":\"object\",\"children\":{\"country\":{\"t\":\"string\",\"v\":{\"[\\\"IS\\\"]\":[\"evt\"]}}," +
                "\"nested\":{\"t\":\"object\",\"children\":{\"deep\":{\"t\":\"string\",\"p\":{\"x\":[\"evt\"]}}}}}}}");
            var props = Props.Of(("address", Props.Of(("country", "DE"), ("nested", Props.Of(("deep", "y"))))));
            var outcome = Validate(spec, props)["address"];
            Assert.Empty(outcome.FailedEventIds);
            Assert.Equal(new[] { "evt" }, outcome.Children!["country"].FailedEventIds);
            Assert.False(outcome.Children.ContainsKey("nested")); // depth 2 is not validated

            Assert.Empty(Validate(spec, Props.Of(("address", Props.Of(("country", "IS"))))));
        }

        [Fact]
        public void Object_sent_as_null_fails_its_required_constrained_child()
        {
            var spec = Spec("{\"o\":{\"t\":\"object\",\"children\":{\"c\":{\"t\":\"string\",\"r\":true,\"p\":{\"x\":[\"evt\"]}}}}}");
            Assert.Equal(new[] { "evt" }, Validate(spec, Props.Of(("o", null)))["o"].Children!["c"].FailedEventIds);
        }

        [Fact]
        public void Non_generic_dictionaries_are_objects_too()
        {
            var spec = Spec("{\"o\":{\"t\":\"object\",\"children\":{\"c\":{\"t\":\"int\",\"minmax\":{\"0,1\":[\"evt\"]}}}}}");
            var map = new System.Collections.Hashtable { ["c"] = 5 };
            Assert.Equal(new[] { "evt" }, Validate(spec, Props.Of(("o", map)))["o"].Children!["c"].FailedEventIds);
        }

        [Fact]
        public void List_of_primitives_is_checked_item_by_item()
        {
            var spec = Spec("{\"tags\":{\"t\":\"string\",\"l\":true,\"v\":{\"[\\\"a\\\",\\\"b\\\"]\":[\"evt\"]}}}");
            Assert.Null(Failed(spec, Props.Of(("tags", new[] { "a", "b", "a" })), "tags"));
            Assert.Equal(new[] { "evt" }, Failed(spec, Props.Of(("tags", new List<object?> { "a", "c" })), "tags"));
            Assert.Null(Failed(spec, Props.Of(("tags", "a")), "tags")); // not a list: not checked
        }

        [Fact]
        public void List_of_objects_is_checked_per_child_across_items()
        {
            var spec = Spec("{\"items\":{\"t\":\"object\",\"l\":true,\"children\":{\"sku\":{\"t\":\"string\",\"rx\":{\"^S\":[\"evt\"]}}," +
                "\"qty\":{\"t\":\"int\",\"minmax\":{\"1,\":[\"evt\"]}}}}}");
            var items = new List<object?> { Props.Of(("sku", "S1"), ("qty", 1)), Props.Of(("sku", "X2"), ("qty", 2)) };
            var outcome = Validate(spec, Props.Of(("items", items)))["items"];
            Assert.Equal(new[] { "evt" }, outcome.Children!["sku"].FailedEventIds);
            Assert.False(outcome.Children.ContainsKey("qty"));
        }

        [Fact]
        public void A_list_over_1000_items_is_not_checked()
        {
            var spec = Spec("{\"n\":{\"t\":\"int\",\"l\":true,\"minmax\":{\"0,0\":[\"evt\"]}}}");
            Assert.Null(Failed(spec, Props.Of(("n", Enumerable.Repeat(1, 1_001).ToList())), "n"));
            Assert.NotNull(Failed(spec, Props.Of(("n", Enumerable.Repeat(1, 1_000).ToList())), "n"));
        }

        // ----- several events --------------------------------------------------------------------

        [Fact]
        public void Several_events_merge_constraints_with_their_own_ids()
        {
            var spec = EventSpecFetcher.Parse(
                "{\"events\":[" +
                "{\"id\":\"e1\",\"p\":{\"a\":{\"t\":\"string\",\"r\":true,\"v\":{\"[\\\"x\\\"]\":[\"e1\"]}}}}," +
                "{\"id\":\"e2\",\"p\":{\"a\":{\"t\":\"string\",\"r\":false,\"v\":{\"[\\\"y\\\"]\":[\"e2\"]}}}}]}")!;
            Assert.Equal(new[] { "e2" }, Failed(spec, Props.Of(("a", "x")), "a"));
            Assert.Equal(new[] { "e1", "e2" }, Failed(spec, Props.Of(("a", "z")), "a"));
            // Required only where every event requires it, so a null is not checked.
            Assert.Null(Failed(spec, Props.Of(("a", null)), "a"));
        }

        [Fact]
        public void Validation_does_not_change_the_cached_spec()
        {
            var spec = EventSpecFetcher.Parse(
                "{\"events\":[" +
                "{\"id\":\"e1\",\"p\":{\"a\":{\"t\":\"string\",\"v\":{\"[\\\"x\\\"]\":[\"e1\"]}}}}," +
                "{\"id\":\"e2\",\"p\":{\"a\":{\"t\":\"string\",\"v\":{\"[\\\"x\\\"]\":[\"e2\"]}}}}]}")!;
            Validate(spec, Props.Of(("a", "z")));
            Assert.Equal(new[] { "e1" }, spec.Events[0].Props["a"].AllowedValues!["[\"x\"]"]);
        }

        // ----- value strings ---------------------------------------------------------------------

        [Theory]
        [InlineData(1.0, "1")]
        [InlineData(-2.5, "-2.5")]
        [InlineData(0.1, "0.1")]
        [InlineData(1e21, "1e+21")]
        [InlineData(1e20, "100000000000000000000")]
        [InlineData(123456789012345680000.0, "123456789012345680000")]
        [InlineData(1e-7, "1e-7")]
        [InlineData(0.000001, "0.000001")]
        [InlineData(1.5e-10, "1.5e-10")]
        [InlineData(double.PositiveInfinity, "Infinity")]
        [InlineData(double.NaN, "NaN")]
        [InlineData(-0.0, "0")]
        public void Numbers_format_as_in_JavaScript(double value, string expected)
        {
            Assert.Equal(expected, RawValues.FormatNumber(value));
        }

        [Fact]
        public void Comparable_strings_follow_JavaScript_String_and_JSON()
        {
            Assert.Equal("null", RawValues.ComparableString(null));
            Assert.Equal("false", RawValues.ComparableString(false));
            Assert.Equal("0.1", RawValues.ComparableString(0.1f));
            Assert.Equal("2", RawValues.ComparableString(2.000m));
            Assert.Equal("18446744073709551615", RawValues.ComparableString(ulong.MaxValue));
            Assert.Equal("{\"a\":[1,\"x\",null,true]}",
                RawValues.ComparableString(Props.Of(("a", new List<object?> { 1, "x", null, true }))));
        }

        [Fact]
        public void Json_of_a_value_that_contains_itself_throws_instead_of_overflowing()
        {
            var map = new Dictionary<string, object?>();
            map["self"] = map;
            Assert.Throws<InvalidOperationException>(() => RawValues.ToJson(map));
            Assert.Equal("[object Object]", RawValues.ComparableString(map));
        }
    }
}
