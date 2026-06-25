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
        /// (SPEC.md §7.3.3). Update this on every release.
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
