using System;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Constructor validation and env fallback (SPEC.md §4.1, §6.3 — AC-1, AC-2, AC-3, AC-4). Not
    /// covered by the conformance fixtures, which all use valid constructor blocks.
    /// </summary>
    public class ConstructorTests
    {
        private const string ApiKeyMessage =
            "[Avo Inspector] No API key provided. Inspector can't operate without API key.";
        private const string VersionMessage =
            "[Avo Inspector] No version provided. Many features of Inspector rely on versioning. " +
            "Please provide comparable string version, i.e. integer or semantic.";

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void Missing_or_whitespace_apiKey_throws_with_exact_message(string? apiKey)
        {
            var ex = Assert.Throws<ArgumentException>(() => new AvoInspector(apiKey!, "dev", "1.0.0"));
            Assert.Equal(ApiKeyMessage, ex.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Missing_or_whitespace_version_throws_with_exact_message(string? version)
        {
            var ex = Assert.Throws<ArgumentException>(() => new AvoInspector("api-key", "dev", version!));
            Assert.Equal(VersionMessage, ex.Message);
        }

        [Fact]
        public void ApiKey_is_validated_before_version()
        {
            // Both missing -> apiKey error wins (validation order, SPEC.md §4.1).
            var ex = Assert.Throws<ArgumentException>(() => new AvoInspector(null!, "dev", null!));
            Assert.Equal(ApiKeyMessage, ex.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("production")]
        [InlineData("DEV")]
        [InlineData("nonsense")]
        public void Invalid_or_absent_env_falls_back_to_dev_without_throwing(string? env)
        {
            // No throw, and dev semantics apply: batchSize is forced to 1 (SPEC.md §12.2) even
            // though 30 was requested, proving the fallback resolved to dev.
            var inspector = new AvoInspector("api-key", env!, "1.0.0", batchSize: 30);
            Assert.Equal(1, inspector.EffectiveBatchSize);
        }

        [Fact]
        public void Valid_envs_are_accepted_and_not_forced_to_dev()
        {
            Assert.Equal(30, new AvoInspector("k", "staging", "1.0.0", batchSize: 30).EffectiveBatchSize);
            Assert.Equal(30, new AvoInspector("k", "prod", "1.0.0", batchSize: 30).EffectiveBatchSize);
            Assert.Equal(1, new AvoInspector("k", "dev", "1.0.0", batchSize: 30).EffectiveBatchSize);
        }

        [Fact]
        public void Typed_options_constructor_validates_and_builds()
        {
            var options = new AvoInspectorOptions
            {
                ApiKey = "api-key",
                Env = AvoInspectorEnv.Staging,
                Version = "2.3.1",
                BatchSize = 5
            };
            var inspector = new AvoInspector(options);
            Assert.Equal(5, inspector.EffectiveBatchSize);

            var bad = new AvoInspectorOptions { ApiKey = "  ", Env = AvoInspectorEnv.Dev, Version = "1.0.0" };
            Assert.Throws<ArgumentException>(() => new AvoInspector(bad));
        }
    }
}
