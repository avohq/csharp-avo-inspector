using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Process-wide logging flag behavior (SPEC.md §4.4 — AC-4, AC-5). The flag is observed on
    /// stderr by the conformance runner, not asserted by fixtures, so it is verified here.
    /// </summary>
    public class LoggingTests
    {
        [Fact]
        public void Logging_default_is_tied_to_env()
        {
            _ = new AvoInspector("k", "staging", "1.0.0");
            Assert.False(AvoInspector.ShouldLogForTesting);

            _ = new AvoInspector("k", "dev", "1.0.0");
            Assert.True(AvoInspector.ShouldLogForTesting);

            _ = new AvoInspector("k", "prod", "1.0.0");
            Assert.False(AvoInspector.ShouldLogForTesting);
        }

        [Fact]
        public void EnableLogging_is_process_wide_across_instances()
        {
            var a = new AvoInspector("k", "staging", "1.0.0"); // resets flag to false
            Assert.False(AvoInspector.ShouldLogForTesting);

            var b = new AvoInspector("k", "staging", "1.0.0");

            a.EnableLogging(true);
            Assert.True(AvoInspector.ShouldLogForTesting); // visible regardless of which instance set it

            b.EnableLogging(false);
            Assert.False(AvoInspector.ShouldLogForTesting); // b's call affects the same shared flag
        }
    }
}
