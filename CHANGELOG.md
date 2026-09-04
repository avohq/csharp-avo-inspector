# Changelog

All notable changes to the Avo Inspector C# SDK are documented here. This project
follows [Semantic Versioning](https://semver.org/). The `libVersion` sent on the wire is
the SDK library version (`InspectorVersion.LibVersion`), independent of the spec contract
version it implements (`InspectorVersion.SpecVersion`).

## [1.1.0] — 2026-09-04

Implements `avohq/spec-first-inspector-server-sdk` **v3.0.0** (was v1.0.0), and its conformance
harness now implements runner contract **v1.1.0**.

### Changed — wire protocol

- **Endpoint: `POST https://api.avo.app/inspector/v1/track` → `POST https://api.avo.app/inspector/v2/track`.**
  Every Avo Inspector sender — browser SDK, GTM templates, server SDKs — now posts to this one
  endpoint, so Avo can attribute traffic at the edge without decoding a body. The SDK's public API
  is unchanged; upgrading needs no code change on your side.
- **New request headers** (SPEC.md §7.2): `api-key` (your Inspector API key) and `env`
  (`dev`/`staging`/`prod`), both **required** by the endpoint — it answers `400` if either is
  missing or invalid — plus `X-Avo-Client: csharp` (this SDK's `libPlatform`), which is how Avo
  tells senders apart. `apiKey` and `env` are still sent in every event body as well: the endpoint
  ignores the body copies, but keeping them holds one body shape and one JSON Schema across all
  senders.
- The v2 endpoint does not sample server-side: it pins `samplingRate` to `1.0` and stores exact
  counts rather than extrapolating. The SDK itself is unchanged — it still reads `samplingRate`
  from the `200` response and still applies its own client-side sampling.

### Security

- **The `api-key` header value is validated before it is sent.** Moving the key out of the JSON
  body and into a request header introduced a header-injection vector that did not exist in 1.0.0,
  where the key could not break framing: a key containing CR, LF or NUL can terminate the header
  line and append attacker-chosen headers to the outbound request. `TryAddWithoutValidation` does
  not check for this by design, and the transport is not a backstop — on `net8.0` a key of
  `key\r\nX-Injected: 1` is written through unaltered and the receiving server parses
  `X-Injected` as a genuine header. The sender now rejects such a key itself, before serializing
  or opening a connection: the batch fails as an ordinary `SendStatus.Error` and nothing is
  transmitted. **The constructor also throws**, with the exact message SPEC.md §4.1 specifies
  (`"[Avo Inspector] API key contains a control character. The API key is sent as a request header
  and cannot contain CR, LF, or NUL."`), so the misconfiguration is fatal at startup instead of
  visible only as events that never arrive. The two checks are deliberately redundant: the sender's
  is what protects the wire, but on its own it turns a configuration mistake into a process that
  starts cleanly and delivers nothing — silently, since logging is off by default outside `dev`.
  The key is trimmed first, so a key pasted with a trailing newline is still repaired rather than
  rejected; only a control character that survives trimming is fatal.

### Added

- The gateway track options of SPEC.md §4.2.1 / §7.3.6, as three **optional trailing
  parameters** on `TrackSchemaFromEvent`: `outputReference`, `originHint` (the gateway
  coordinates) and `originAppVersion` (a per-event app-version override). §4.2.1 picks the
  call-site shape from the target language — C# has named arguments, so the three are flattened
  rather than grouped in an options object, and a caller writes `originAppVersion: "5.1.0"` at
  the call site. The wire body is identical either way.
  Backward-compatible at both source and binary level: the 1.0.0 three-parameter overload is
  retained with its exact CLR signature and delegates with all three `null`, so existing call
  sites and precompiled consumers keep working unchanged. (It no longer declares a default for
  `streamId`; a default there would make a two-argument call ambiguous between the two forms.
  A default is call-site metadata rather than part of the CLR signature, so precompiled callers
  are unaffected and a two-argument source call simply resolves to the flattened form, producing
  the identical body.) Supplying none of the three sends exactly the 1.0.0 key set and values —
  the only difference from a 1.0.0 body is `libVersion`.
- Wire body gains `outputReference` / `originHint` as top-level siblings of
  `eventProperties` (each omitted, never sent as `null` or `""`, when empty/null/
  whitespace-only after trimming — SPEC.md §7.3.6); `appVersion` becomes nullable on the
  wire (still always present as a key — the one field in this feature that may
  legitimately be a literal JSON `null`, per the §7.3.6 resolution table restated in the
  README's "Gateways" section). An event property that happens to be named
  `outputReference`, `originHint`, or `appVersion` is an ordinary property and stays in
  the schema untouched.
- `originHint` must be a low-cardinality value (e.g. `"web"`, `"ios"`, `"android"`) — it
  MUST NOT be a user identifier or other high-cardinality value. Documentation-only; not
  validated at runtime.

### Removed

- **`sessionId` is no longer sent on the wire** (SPEC.md §3.3). A server SDK has no session to
  report, and `/inspector/v2/track` supplies the value itself. Spec 2.0.0 had REQUIRED it as `""`
  because ingestion then dropped events whose body omitted it — returning `200 {"success":true}`
  while nothing reached the dashboard — which is why 3.0.0 *removes* the field rather than
  *forbidding* it: a body that still carries it validates against the 3.0.0 schemas as an unknown
  extra field. Use `streamId`, which is optional and caller-supplied, to correlate events.

  > **Sequencing.** SPEC.md §7.1 carries a dated note that both ingestion parsers still required
  > `sessionId` as of 2026-09-04, and that a sender dropping it before the backend default ships
  > loses every event at HTTP `200`. Confirm that change is live before deploying this release.

### Changed

- `InspectorVersion.SpecVersion` `1.0.0` → `3.0.0` and `InspectorVersion.HarnessContractVersion`
  `1.0.0` → `1.1.0` (with `<AvoInspectorSpecVersion>`, the package `Description` in
  `AvoInspector.csproj`, and the README badge to match). Docs that described `sessionId: ""` as a
  deliberate divergence, and the gateway coordinates as an extension outside the spec, were
  rewritten to match 3.0.0: the coordinates are SPEC.md §4.2.1 / §7.3.6, and `sessionId` is gone
  from the body per §3.3.
- `AvoInspector.Conformance` forwards a fixture's `options` values (single-event
  `trackSchemaFromEvent` input and sequence `track` steps) verbatim as the matching top-level
  named arguments, reading the renamed `originAppVersion` key, and passes none of the three when a
  fixture has no `options` — runner contract 1.1.0. `scripts/run-conformance.sh` now defaults
  `SPEC_REF` to `gateway-track-options`, the branch carrying spec 3.0.0: **36/36**. Running it
  against `main` (spec 2.0.0) fails 10 of 30 fixtures, correctly — every 2.0.0 fixture body
  asserts the `sessionId: ""` this release stops sending. Switch the default back to `main` once
  the spec PR merges. The vendored `conformance/fixtures/` copies were refreshed to the same
  3.0.0 set. The harness needs no change for the v2 headers: the suite drives the SDK through
  `AVO_INSPECTOR_MOCK_ENDPOINT` and the runner records and asserts request headers itself.
- `AvoInspector.Tests` and `AvoInspector.Conformance` now build against
  `src/AvoInspector/AvoInspector.csproj` via `ProjectReference` instead of the published
  `AvoInspector` NuGet package, so CI and `./scripts/run-conformance.sh` exercise this
  repo's unreleased source rather than a pinned prior release. `examples/AvoInspector.Example`
  is unaffected and stays on the published `1.0.0` package for this release.

## [1.0.0] — 2026-06-25

Initial release. Implements `avohq/spec-first-inspector-server-sdk` **v1.0.0**.

### Added

- `AvoInspector` with the full public API: `TrackSchemaFromEvent`, `ExtractSchema`,
  `Flush`, `Destroy`, `EnableLogging`, and both typed-options and string-env constructors.
- `AvoSchemaParser` schema extraction (SPEC §9): primitive/object/list classification,
  first-element list typing, structural deduplication, and depth-limited recursion. The
  runtime numeric type is authoritative (`0.0` → `float`).
- Wire protocol (SPEC §7): self-contained JSON array bodies, UUID v4 `messageId`,
  millisecond ISO-8601 `createdAt`, mandatory gzip for bodies ≥ 1024 bytes, a 10-second
  timeout, the §7.5 error taxonomy, and a fail-closed `AVO_INSPECTOR_MOCK_ENDPOINT` gate.
- `sessionId: ""` on every event. At the time of this release the spec still told SDKs to omit
  it, but the live ingestion pipeline drops events that do, despite returning
  `200 {"success":true}` — verified by field-bisection against the live API. Spec
  avohq/spec-first-inspector-server-sdk#2 has since merged as spec 2.0.0, making
  `sessionId: ""` REQUIRED for server SDKs (SPEC.md §3.3), so what shipped here is what the
  spec now mandates.
- Batching (SPEC §12): size + time/idle flush triggers, `maxQueueSize` FIFO bound,
  at-most-once delivery (no re-queue on failure), atomic swap-and-clear under concurrency,
  and a background flush timer that never holds the process open.
- Process-wide logging flag; per-event sampling with server-driven rate updates.
- `AvoInspector.Conformance` CLI harness (runner-contract v1.0.0): passes all 30 fixtures.
- `AvoInspector.Tests`: 44 unit tests covering the manual-matrix behaviors (constructor
  validation, env fallback, `0.0`→float, prod fail-closed gate, process-wide logging,
  destroy post-state, scheduled flush, transient-failure no-requeue, gzip).
- Multi-targets `netstandard2.0` and `net8.0`.

[1.1.0]: https://github.com/avohq/csharp-avo-inspector/releases/tag/v1.1.0
[1.0.0]: https://github.com/avohq/csharp-avo-inspector/releases/tag/v1.0.0
