using System.Reflection;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Guards against drift between the wire <c>libVersion</c> (the SPEC.md §7.3.3 / VERSIONING.md
    /// "dedicated version file" constant) and the NuGet package <c>&lt;Version&gt;</c>. The spec
    /// mandates a hardcoded constant, so the two genuinely live in two files — this test fails CI
    /// if a release bumps one without the other (which would ship a package that reports a stale
    /// version on the wire).
    /// </summary>
    public class VersionTests
    {
        [Fact]
        public void LibVersion_matches_package_version()
        {
            var assembly = typeof(InspectorVersion).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "";

            // Strip any "+build.metadata" suffix (e.g. SourceLink commit hash).
            var packageVersion = informational.Split('+')[0];

            Assert.Equal(InspectorVersion.LibVersion, packageVersion);
        }
    }
}
