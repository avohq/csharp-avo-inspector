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
        public const string LibVersion = "1.1.0";

        /// <summary>
        /// Identifies the SDK platform/language on the wire as <c>libPlatform</c> (SPEC.md §7.3.1),
        /// and — for a server SDK — as the <c>X-Avo-Client</c> request header that lets the
        /// Inspector edge attribute traffic by sender without decoding a body (SPEC.md §7.2).
        /// </summary>
        public const string LibPlatform = "csharp";

        /// <summary>
        /// The version of the <c>avohq/spec-first-inspector-server-sdk</c> contract this SDK
        /// implements (VERSIONING.md). Independent of <see cref="LibVersion"/>. <c>3.0.0</c> is a
        /// <c>[WIRE]</c> MAJOR: every Inspector sender moves to the unified
        /// <c>POST /inspector/v2/track</c> endpoint and sends <c>api-key</c>, <c>env</c> and
        /// <c>X-Avo-Client</c> as request headers (SPEC.md §7.1, §7.2). It also folds in the
        /// gateway track options once drafted as <c>2.1.0</c> (SPEC.md §4.2.1, §7.3.6) — passed as
        /// top-level optional parameters, the shape §4.2.1 requires of a language with named
        /// arguments — and <b>removes</b> the wire <c>sessionId</c> that <c>2.0.0</c> had REQUIRED
        /// (SPEC.md §3.3).
        /// </summary>
        public const string SpecVersion = "3.0.0";

        /// <summary>
        /// The version of <c>conformance/runner-contract.md</c> this SDK's harness implements.
        /// <c>1.1.0</c> adds the optional <c>options</c> object on single-event
        /// <c>trackSchemaFromEvent</c> input and on sequence <c>track</c> steps.
        /// </summary>
        public const string HarnessContractVersion = "1.1.0";
    }
}
