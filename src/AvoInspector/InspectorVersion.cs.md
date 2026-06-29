## Short description

Compile-time version metadata for the SDK, sent on the wire and used by drift-guard tests.

`public static class InspectorVersion`

## Data

Public constants:

- `const string LibVersion = "1.0.0"` — SDK library version, sent on the wire as `libVersion`.
  **IMPORTANT:** plain SemVer, no suffix. MUST be bumped on every release and **kept in sync with
  `<Version>` in AvoInspector.csproj** — a `VersionTests` drift-guard fails CI if they diverge.
- `const string LibPlatform = "csharp"` — SDK platform/language, sent on the wire as `libPlatform`.
- `const string SpecVersion = "1.0.0"` — version of the spec-first inspector server SDK contract this
  SDK implements; independent of `LibVersion`.
- `const string HarnessContractVersion = "1.0.0"` — version of the conformance runner contract this
  SDK's harness implements.
