namespace Avo.Inspector
{
    /// <summary>
    /// Version metadata for the SDK (SPEC.md §7.3.3).
    /// <para>
    /// <see cref="LibVersion"/> is the SDK library version sent on the wire as <c>libVersion</c>.
    /// It MUST be a plain SemVer string with no suffix. Maintainers MUST bump this constant on
    /// every release.
    /// </para>
    /// </summary>
    public static class InspectorVersion
    {
        /// <summary>
        /// SDK library version, sent on the wire as <c>libVersion</c>. Plain SemVer, no suffix
        /// (SPEC.md §7.3.3 mandates a hardcoded constant in a dedicated version file).
        /// <para><b>Keep this in sync with <c>&lt;Version&gt;</c> in AvoInspector.csproj on every
        /// release.</b> They are two files by spec necessity; the <c>VersionTests</c> drift-guard
        /// test fails CI if they diverge.</para>
        /// </summary>
        public const string LibVersion = "1.0.0";

        /// <summary>
        /// Identifies the SDK platform/language on the wire as <c>libPlatform</c> (SPEC.md §7.3.1).
        /// </summary>
        public const string LibPlatform = "csharp";

        /// <summary>
        /// The version of the <c>avohq/spec-first-inspector-server-sdk</c> contract this SDK
        /// implements (VERSIONING.md). Independent of <see cref="LibVersion"/>.
        /// </summary>
        public const string SpecVersion = "1.0.0";

        /// <summary>
        /// The version of <c>conformance/runner-contract.md</c> this SDK's harness implements.
        /// </summary>
        public const string HarnessContractVersion = "1.0.0";
    }
}
