# Changelog

All notable changes to the Avo Inspector C# SDK are documented here. This project
follows [Semantic Versioning](https://semver.org/). The `libVersion` sent on the wire is
the SDK library version (`InspectorVersion.LibVersion`), independent of the spec contract
version it implements (`InspectorVersion.SpecVersion`).

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
- `sessionId: ""` on every event (deliberate divergence from spec v1.0.0 §3.3/§7.3.1).
  The live ingestion pipeline drops events that omit `sessionId` despite returning
  `200 {"success":true}`; verified by field-bisection against the live API. The spec is
  being corrected in avohq/spec-first-inspector-server-sdk#2 (sessionId becomes a required
  wire field); this SDK implements that. The vendored conformance fixtures and
  `scripts/run-conformance.sh` (via `SPEC_REF`) track that branch, so the suite is 30/30.
- Batching (SPEC §12): size + time/idle flush triggers, `maxQueueSize` FIFO bound,
  at-most-once delivery (no re-queue on failure), atomic swap-and-clear under concurrency,
  and a background flush timer that never holds the process open.
- Process-wide logging flag; per-event sampling with server-driven rate updates.
- `AvoInspector.Conformance` CLI harness (runner-contract v1.0.0): passes all 30 fixtures.
- `AvoInspector.Tests`: 44 unit tests covering the manual-matrix behaviors (constructor
  validation, env fallback, `0.0`→float, prod fail-closed gate, process-wide logging,
  destroy post-state, scheduled flush, transient-failure no-requeue, gzip).
- Multi-targets `netstandard2.0` and `net8.0`.

[1.0.0]: https://github.com/avohq/csharp-avo-inspector/releases/tag/v1.0.0
