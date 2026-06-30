---
import: src/AvoInspector/AvoInspector.cs.md
import: examples/AvoInspector.Example/LocalSink.cs.md
---

## Short description

Runnable example CLI for the Avo Inspector C# SDK. It demonstrates schema extraction and the
track → batch → flush lifecycle. By default it runs fully offline; `--live` sends to the real API.

## Tech stack

C# (`net8.0` console exe); the `AvoInspector` SDK; `System.Text.Json` (pretty-printing);
`LocalSink` (dry-run sink).

## Users and permissions

A developer running the example. `--live` requires `AVO_INSPECTOR_API_KEY` (and optional
`AVO_INSPECTOR_ENV`, default `dev`).

## Functional requirements

- `--help`/`-h` → print usage, exit 0.
- **Schema showcase (always, offline):** construct a dev instance and pretty-print
  `ExtractSchema(...)` for sample events (primitives, nested object, `list(string)`, `list(object)`,
  `null`).
- **Default (dry run):** start a `LocalSink`, point the SDK at it for this process only, construct a
  `Staging` instance with `BatchSize = 2`, `DisableBatchTimer = true`, then track three events and
  `Flush()`. Print each captured POST (decompressed, pretty JSON) showing the size-triggered batch,
  the flush-drained partial batch, and gzip on the larger body. Then `Destroy()`. Print the
  shutdown-contract reminder.
- **`--live`:** require `AVO_INSPECTOR_API_KEY` (else stderr error, exit 1); read env from
  `AVO_INSPECTOR_ENV`; **clear any leftover `AVO_INSPECTOR_MOCK_ENDPOINT`** (with a warning) so it
  truly targets the real API; track two events and `Flush()`.

## Non-functional requirements

- The dry run sets the **test-only** `AVO_INSPECTOR_MOCK_ENDPOINT` override for the demo process and
  restores it afterward; this is example plumbing, not production guidance.
- Exit 0 on success; `--live` exits 1 when the API key is absent.
