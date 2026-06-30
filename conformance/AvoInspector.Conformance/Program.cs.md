---
import: src/AvoInspector/AvoInspector.cs.md
import: conformance/AvoInspector.Conformance/JsonInterop.cs.md
---

## Short description

The `avo-inspector-conformance` CLI harness. It implements the language-agnostic stdin/stdout JSON
protocol of the Avo Inspector spec's runner contract: read one input envelope, construct an
`AvoInspector`, run the requested operation, and write exactly one output envelope. It contains no
assertion logic — the suite runner asserts. HARNESS_CONTRACT_VERSION `1.0.0`.

## Tech stack

C# (`net8.0` console exe, `AssemblyName` `AvoInspector.Conformance`); `System.Text.Json`; the
`AvoInspector` SDK (via `InternalsVisibleTo`, for the test-only sampling hook); `JsonInterop`.

## Data

Input envelope fields read: `fixture_id` (required), `suite`, `operation`, `constructor`
(`apiKey`/`env`/`version`/`appName`/`batchSize`/`batchFlushSeconds`/`maxQueueSize`/`disableBatchTimer`),
`input`, `steps`, `precondition.samplingRate`.

`OutputEnvelope` → `{ fixture_id, passed, actual, outcome, error }`.
`StepRecord` → `{ action, outcome, value }` (one per sequence step).

## Functional requirements

1. Read all of stdin; parse one JSON envelope. Missing/invalid `fixture_id` → config error.
2. Construct `AvoInspector` from the `constructor` block. A constructor throw is a harness-level
   failure: `passed:false`, `error: "Constructor threw: <message>"`, **exit 1**.
3. Apply `precondition.samplingRate` (a number) via the SDK's internal test-only setter. Any other
   precondition field, or a non-numeric value → config error.
4. Resolve `operation` (defaults to `extractSchema` when `suite == "schema-extraction"`) and dispatch:
   - `extractSchema` — convert `input` to a property map (`null` passes through) and return
     `ExtractSchema(...)` as `actual`, `outcome: "resolve"`.
   - `trackSchemaFromEvent` — call `TrackSchemaFromEvent(eventName, eventProperties, streamId?)`
     (the third argument is omitted when no `streamId` is given). On success `actual` is the resolved
     schema; an `AvoInspectorTrackException` becomes `outcome: "reject"` with the exact rejection
     string as `actual`.
   - `sequence` — run `steps` in order against one instance, recording a `StepRecord` each:
     `track` (await), `trackN` (fire `count` **genuinely concurrent** tracks via thread-pool tasks,
     join all, value = count), `flush` (await `Flush(timeoutMs?)`), `destroy`.
5. Write exactly one output envelope as a single UTF-8 JSON line to stdout, then exit.

## Non-functional requirements

- **stdout carries only the single output-envelope line**; all diagnostics go to stderr.
- Exit codes: **0** success · **1** harness/runtime failure (constructor throw, unhandled error
  after parse) · **2** configuration error (malformed envelope, missing `fixture_id`, unsupported
  operation/precondition).
- No persisted state between invocations; each run constructs a fresh instance. JSON→native
  conversion (key order, int/float) is delegated to `JsonInterop`.
