using System;
using System.Collections.Generic;
using Avo.Inspector.Internal.EventSpec;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Parsing of the <c>/trackingPlan/eventSpec</c> wire format, the spec cache, and the fetch URL
    /// (AVO-4069), mirroring the Node SDK's <c>TestAvoEventSpecFetcher</c> and cache tests.
    /// </summary>
    public class EventSpecParsingTests
    {
        private const string FullSpec = @"{
          ""events"": [
            { ""b"": ""main"", ""id"": ""evt_1"", ""vids"": [""var_1"", 7],
              ""p"": {
                ""plan"": { ""t"": ""string"", ""r"": true, ""v"": { ""[\""Free\"",\""Pro\""]"": [""evt_1"", ""var_1""] } },
                ""seats"": { ""t"": ""int"", ""r"": false, ""minmax"": { ""1,100"": [""evt_1""] } },
                ""code"": { ""t"": ""string"", ""r"": false, ""rx"": { ""^[A-Z]{3}$"": [""var_1""] }, ""p"": { ""ABC"": [""evt_1""] } },
                ""tags"": { ""t"": ""string"", ""r"": false, ""l"": true, ""v"": { ""[\""a\""]"": [""evt_1""] } },
                ""address"": { ""t"": ""object"", ""r"": false, ""children"": { ""country"": { ""t"": ""string"", ""r"": true, ""v"": { ""[\""IS\""]"": [""evt_1""] } } } }
              } },
            { ""b"": ""main"", ""id"": ""evt_2"", ""p"": {} }
          ],
          ""metadata"": { ""schemaId"": ""s1"", ""branchId"": ""main"", ""latestActionId"": ""a9"", ""sourceId"": ""src"" }
        }";

        [Fact]
        public void Parse_keeps_every_event_variant_and_constraint()
        {
            var spec = EventSpecFetcher.Parse(FullSpec)!;

            Assert.Equal(2, spec.Events.Count);
            var entry = spec.Events[0];
            Assert.Equal("main", entry.BranchId);
            Assert.Equal("evt_1", entry.BaseEventId);
            Assert.Equal(new[] { "var_1" }, entry.VariantIds); // the non-string id is dropped

            Assert.True(entry.Props["plan"].Required);
            Assert.Equal(new[] { "evt_1", "var_1" }, entry.Props["plan"].AllowedValues!["[\"Free\",\"Pro\"]"]);
            Assert.Equal(new[] { "evt_1" }, entry.Props["seats"].MinMaxRanges!["1,100"]);
            Assert.Equal(new[] { "var_1" }, entry.Props["code"].RegexPatterns!["^[A-Z]{3}$"]);
            Assert.Equal(new[] { "evt_1" }, entry.Props["code"].PinnedValues!["ABC"]);
            Assert.True(entry.Props["tags"].IsList);
            Assert.True(entry.Props["address"].Children!["country"].Required);

            Assert.Equal("s1", spec.Metadata.SchemaId);
            Assert.Equal("main", spec.Metadata.BranchId);
            Assert.Equal("a9", spec.Metadata.LatestActionId);
            Assert.Equal("src", spec.Metadata.SourceId);
        }

        [Fact]
        public void Parse_skips_malformed_entries_ids_and_constraints()
        {
            var spec = EventSpecFetcher.Parse(@"{
              ""events"": [
                { ""b"": ""main"", ""p"": { ""x"": { ""t"": ""string"" } } },
                ""not an object"",
                { ""id"": 42 },
                { ""id"": ""evt_ok"", ""p"": {
                    ""bad"": ""not constraints"",
                    ""ids"": { ""t"": ""string"", ""v"": { ""[\""a\""]"": [1, null], ""[\""b\""]"": ""evt"" }, ""rx"": { ""x"": [""evt_ok""] } } } }
              ]
            }")!;

            var entry = Assert.Single(spec.Events);
            Assert.Equal("evt_ok", entry.BaseEventId);
            Assert.False(entry.Props.ContainsKey("bad"));
            Assert.Null(entry.Props["ids"].AllowedValues); // no string ids left
            Assert.NotNull(entry.Props["ids"].RegexPatterns);
            Assert.Equal(string.Empty, spec.Metadata.SchemaId);
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("\"spec\"")]
        [InlineData("not json")]
        [InlineData("")]
        public void Parse_returns_null_for_a_body_that_is_not_an_object(string body)
        {
            Assert.Null(EventSpecFetcher.Parse(body));
        }

        [Fact]
        public void Parse_gives_no_events_when_none_has_an_id()
        {
            var spec = EventSpecFetcher.Parse(@"{ ""events"": [], ""metadata"": {} }")!;
            Assert.Empty(spec.Events);
        }

        [Fact]
        public void Url_carries_source_parameters_then_gateway_coordinates_when_set()
        {
            var fetcher = new EventSpecFetcher("key/+&");
            Assert.Equal(
                "http://h:1/trackingPlan/eventSpec?apiKey=key%2F%2B%26&eventName=Sign%20Up&streamId=s1",
                fetcher.BuildUrl("http://h:1", "Sign Up", "s1", null, null));
            Assert.Equal(
                "http://h:1/trackingPlan/eventSpec?apiKey=key%2F%2B%26&eventName=E&streamId=&originHint=web&outputReference=meta-x7k2q",
                fetcher.BuildUrl("http://h:1", "E", "", "web", "meta-x7k2q"));
        }

        [Fact]
        public void Cache_key_separates_every_part_and_each_checkpoint()
        {
            var keys = new HashSet<string>
            {
                EventSpecCache.MakeKey("s", "E", null, null),
                EventSpecCache.MakeKey("s", "E", "web", null),
                EventSpecCache.MakeKey("s", "E", "web", "out"),
                EventSpecCache.MakeKey("s", "E", null, "out"),
                EventSpecCache.MakeKey("sE", "", null, null),
                EventSpecCache.MakeKey("", "sE", null, null),
                EventSpecCache.MakeKey("s", "E-", null, null)
            };
            Assert.Equal(7, keys.Count);
        }

        [Fact]
        public void Cache_refreshes_a_spec_after_50_uses_but_keeps_a_failure()
        {
            var cache = new EventSpecCache();
            var spec = new EventSpecResponse();
            cache.Set("spec", spec, EventSpecCache.SpecTtl);
            cache.Set("failure", null, EventSpecCache.SpecTtl);

            for (var i = 1; i < EventSpecCache.MaxUses; i++)
            {
                Assert.True(cache.TryGet("spec", out var hit));
                Assert.Same(spec, hit);
            }
            Assert.False(cache.TryGet("spec", out _)); // the 50th use refetches

            for (var i = 0; i < 100; i++)
            {
                Assert.True(cache.TryGet("failure", out var failure));
                Assert.Null(failure);
            }
        }

        [Fact]
        public void Cache_expires_entries_and_caps_its_size()
        {
            var cache = new EventSpecCache();
            cache.Set("gone", new EventSpecResponse(), TimeSpan.Zero);
            System.Threading.Thread.Sleep(5);
            Assert.False(cache.TryGet("gone", out _));

            for (var i = 0; i < EventSpecCache.MaxEntries + 10; i++)
            {
                cache.Set("k" + i, new EventSpecResponse(), EventSpecCache.SpecTtl);
            }
            Assert.Equal(EventSpecCache.MaxEntries, cache.CountForTesting);
            Assert.True(cache.TryGet("k" + (EventSpecCache.MaxEntries + 9), out _));
        }
    }
}
