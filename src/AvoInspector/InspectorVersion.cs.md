## Short description

Compile-time version metadata for the SDK, sent on the wire and used by drift-guard tests.

`public static class InspectorVersion`

## Data

Public constants:

- `const string LibVersion = "1.1.0"` — SDK library version, sent on the wire as `libVersion`.
  **IMPORTANT:** plain SemVer, no suffix. MUST be bumped on every release and **kept in sync with
  `<Version>` in AvoInspector.csproj** — a `VersionTests` drift-guard fails CI if they diverge.
- `const string LibPlatform = "csharp"` — SDK platform/language, sent on the wire as `libPlatform`
  **and as the `X-Avo-Client` request header** (SPEC.md §7.2): a generated server SDK's client
  token is its `libPlatform` value.
- `const string SpecVersion = "3.0.0"` — version of the spec-first inspector server SDK contract this
  SDK implements; independent of `LibVersion`. `3.0.0` is a `[WIRE]` MAJOR — the unified
  `POST /inspector/v2/track` endpoint plus the REQUIRED `api-key`/`env`/`X-Avo-Client` request
  headers (SPEC.md §7.1, §7.2) — on top of `2.1.0`'s gateway track options (SPEC.md §4.2.1,
  §7.3.6) — passed as top-level optional parameters, the shape §4.2.1 requires of a language with named arguments — and **removes** the wire `sessionId` that `2.0.0` had REQUIRED (SPEC.md §3.3).
- `const string HarnessContractVersion = "1.1.0"` — version of the conformance runner contract this
  SDK's harness implements. `1.1.0` adds the optional `options` object on single-event
  `trackSchemaFromEvent` input and on sequence `track` steps.
