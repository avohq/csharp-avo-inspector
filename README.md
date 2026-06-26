# Avo Inspector — C# / .NET SDK

> Implements [`avohq/spec-first-inspector-server-sdk`](https://github.com/avohq/spec-first-inspector-server-sdk) **v1.0.0**

A server-side SDK for [Avo Inspector](https://www.avo.app/). It extracts a type
schema from the properties of the analytics events your backend sends, and reports
those schemas to the Avo Inspector API so you can catch tracking-plan drift before it
reaches production.

It is **server-side only**: no browser, no session/visitor tracking, no persistent
storage. All state is in memory.

- ✅ Passes all 30 fixtures of the official conformance suite (schema-extraction,
  wire-protocol, error-handling, batching).
- ✅ Thread-safe; safe for concurrent use on multi-threaded servers.
- ✅ Batching with size + time/idle flush triggers, bounded queue, and gzip.
- ✅ Multi-targets `netstandard2.0` and `net8.0` (works on .NET Framework 4.6.2+,
  .NET Core, .NET 5–8, Mono, Unity). The `4.6.2` floor on .NET Framework is imposed by
  the `System.Text.Json` 8.0.x dependency.

---

## Installation

```sh
dotnet add package AvoInspector
```

Or reference the project directly from source:

```sh
dotnet add reference path/to/src/AvoInspector/AvoInspector.csproj
```

---

## Quick start

```csharp
using Avo.Inspector;

// Strongly-typed construction (recommended).
var inspector = new AvoInspector(new AvoInspectorOptions
{
    ApiKey  = "my-inspector-api-key", // from the Avo Inspector dashboard
    Env     = AvoInspectorEnv.Prod,
    Version = "1.4.2",                 // your application version
    AppName = "checkout-service",
    DisableBatchTimer = false,         // set true in serverless
});

// Report the schema of an event's properties.
await inspector.TrackSchemaFromEvent(
    eventName: "Purchase Completed",
    eventProperties: new Dictionary<string, object?>
    {
        ["amount"]   = 99.0,                       // float
        ["currency"] = "USD",                      // string
        ["items"]    = new List<object?> { "sku1", "sku2" }, // list(string)
        ["user"]     = new Dictionary<string, object?>       // object
        {
            ["id"]   = 42,                          // int
            ["plan"] = "pro",
        },
    },
    streamId: "web");

// Before the process (or serverless handler) exits, flush in-flight + buffered events.
await inspector.Flush();
```

There is also a string-based constructor convenience (e.g. when reading `env` from
configuration), which applies the spec's invalid-env fallback to `"dev"`:

```csharp
var inspector = new AvoInspector(
    apiKey: "my-inspector-api-key",
    env: Environment.GetEnvironmentVariable("APP_ENV") ?? "dev",
    version: "1.4.2");
```

---

## Example CLI

A runnable example lives in [`examples/AvoInspector.Example`](./examples/AvoInspector.Example).
By default it runs fully **offline**: it prints the extracted schema for a few sample events,
then tracks a small batch against a local loopback sink and prints the exact wire payloads
the SDK would POST (showing batching, the size trigger, `flush()` draining a partial batch,
and gzip kicking in for the larger body).

```sh
dotnet run --project examples/AvoInspector.Example            # offline dry run
dotnet run --project examples/AvoInspector.Example -- --live  # send to the real API
dotnet run --project examples/AvoInspector.Example -- --help
```

For `--live`, set `AVO_INSPECTOR_API_KEY` (and optionally `AVO_INSPECTOR_ENV=dev|staging|prod`).

---

## ⚠️ Shutdown contract — you MUST flush before exit

Buffered and in-flight events are held **in memory only** and are delivered
**at-most-once**. The SDK does **not** keep your process alive to deliver them, and it
does **not** retry failed sends. If your process exits while events are buffered or a
send is in flight, those events are lost.

Therefore, **callers MUST call `await inspector.Flush()` before the process or serverless
handler returns** if events may be in flight or buffered. `Flush()` is the only universal
barrier — it force-flushes the pending batch and awaits all in-flight sends.

> Do **not** rely on `await`-ing `TrackSchemaFromEvent` as a substitute. It resolves at
> *enqueue* time, and when `BatchSize > 1` the event may still be sitting in the buffer
> (or its batch may still be on the wire) after the task completes. Only in immediate-send
> mode (`Dev`, where `BatchSize` is forced to 1) does awaiting the call also await its send —
> and even then `Flush()` is the contract to depend on.

```csharp
// AWS Lambda / Azure Functions / Google Cloud Functions handler
public async Task Handler(Event e)
{
    await inspector.TrackSchemaFromEvent("Event", e.Properties);
    await inspector.Flush();   // REQUIRED before the handler returns
}
```

In serverless environments, also set `DisableBatchTimer = true` — a background timer
may be suspended between invocations or leak across warm-container reuse.

`Flush()` is a **completion guarantee, not a delivery guarantee**: it always resolves
(never throws), even if some sends time out or error. Its default timeout is 10,000 ms.

---

## Public API

### `AvoInspector(AvoInspectorOptions options)` / `AvoInspector(string apiKey, string env, string version, …)`

| Option | Type | Required | Default | Notes |
|---|---|---|---|---|
| `ApiKey` | string | **yes** | — | Non-empty / non-whitespace, else the constructor throws. |
| `Env` | `AvoInspectorEnv` / string | **yes** | `Dev` (on invalid string) | `Dev` / `Staging` / `Prod`. Controls logging defaults. |
| `Version` | string | **yes** | — | Non-empty / non-whitespace, else the constructor throws. |
| `AppName` | string | no | `""` | |
| `BatchSize` | int | no | `30` | Flush when the buffer reaches this size. **Forced to `1` in `Dev`** (immediate send). |
| `BatchFlushSeconds` | double | no | `30` | Max age of the oldest buffered event before a scheduled flush. |
| `MaxQueueSize` | int | no | `1000` | Hard cap; oldest events dropped first (FIFO) on overflow. |
| `DisableBatchTimer` | bool | no | `false` | Disable the background scheduled-flush timer. |

The constructor throws synchronously (an `ArgumentException`) for a missing/whitespace
`ApiKey` or `Version`. An invalid or absent `env` string **never throws** — it falls
back to `Dev` with a warning.

### `Task<IReadOnlyList<SchemaEntry>> TrackSchemaFromEvent(string eventName, IDictionary<string, object?>? eventProperties, string? streamId = null)`

Extracts the event's schema, applies per-event sampling, enqueues it, and dispatches a
batch when a flush trigger fires. Resolves with the extracted schema **at enqueue time**.

- When `BatchSize == 1` (always true in `Dev`) the send is synchronous to the call, so a
  non-200 response resolves with an **empty list**.
- When `BatchSize > 1` the resolved value never reflects the batch's eventual HTTP outcome.
- After `Destroy()`, this is a no-op that resolves with an empty list.
- Network errors and timeouts are swallowed — the task still resolves with the schema.
- On a synchronous internal error before enqueue, the task faults with
  `AvoInspectorTrackException` (message: `Avo Inspector: something went wrong. Please report to support@avo.app.`).

`streamId` is passed through verbatim; a value containing `:` is warned about but still
used unchanged; an absent or empty value becomes `""` on the wire.

### `IReadOnlyList<SchemaEntry> ExtractSchema(IDictionary<string, object?>? eventProperties)`

Synchronously extracts the schema with no network call. Never throws — returns an empty
list for a `null` map or on any internal parser error.

### `Task Flush(int timeoutMs = 10000)`

Force-flushes the pending batch, then waits (up to `timeoutMs`) for all in-flight sends.
Always resolves. The instance remains usable afterward. See the shutdown contract above.

### `void Destroy()`

Cancel-and-clean-up: discards the pending batch **unsent**, abandons in-flight sends,
resets the pending count to zero, and stops the scheduled-flush timer. Does **not** flush.
Constructor options, the current sampling rate, and the process-wide logging flag persist.
Distinct from `Flush()` — do not conflate them.

### `void EnableLogging(bool enable)`

Sets the **process-wide** diagnostic logging flag (one flag for all instances in the
process). All logs go to `stderr`. ⚠️ Do **not** enable logging in production contexts —
because the flag is process-wide, it would affect production instances sharing the process.

---

## Schema extraction

Each property is classified into a `propertyType`:
`string`, `int`, `float`, `boolean`, `null`, `object`, `unknown`, or a list wrapper such
as `list(string)`, `list(int)`, `list(object)`, etc. Objects and lists carry recursive
`children`. The C# numeric runtime type is authoritative: `int`/`long` → `int`,
`float`/`double`/`decimal` → `float` (so `0.0` is `float`, not `int`).

```text
{ "user": { "id": 1, "tags": ["a", "b"] }, "scores": [1, 2] }
```
extracts to
```json
[
  { "propertyName": "user", "propertyType": "object", "children": [
      { "propertyName": "id",   "propertyType": "int" },
      { "propertyName": "tags", "propertyType": "list(string)", "children": ["string"] }
  ]},
  { "propertyName": "scores", "propertyType": "list(int)", "children": ["int"] }
]
```

> **Property order.** Schema property order follows the iteration order of the map you
> pass. `Dictionary<string, object?>` preserves insertion order in practice; pass an
> order-preserving map if you need a hard guarantee.

---

## Wire protocol

Events are POSTed as a JSON array to `https://api.avo.app/inspector/v1/track`
(`Content-Type: application/json`). Request bodies **≥ 1024 bytes are gzip-compressed**
(`Content-Encoding: gzip`); smaller bodies are sent uncompressed. The .NET runtime always
provides gzip, so this SDK is never exempt from the compression requirement. Every request
has a 10-second timeout. There is no `Authorization` header — the `apiKey` travels in the
body. Certificate validation always uses the platform default and cannot be disabled.

`AVO_INSPECTOR_MOCK_ENDPOINT` redirects requests to a test endpoint, but is **fail-closed**:
a `Prod` instance ignores it unconditionally, so production traffic can never be redirected.

> **`sessionId` (deliberate spec divergence).** Every event carries `sessionId: ""`. Spec
> v1.0.0 §3.3/§7.3.1 told SDKs to *omit* `sessionId`, but the live Inspector ingestion pipeline
> silently **drops** events that omit it (the request still returns `200 {"success":true}`, yet
> nothing reaches the dashboard). Verified by field-bisection against the live API: adding only
> `sessionId: ""` is necessary and sufficient to ingest. The spec is being corrected in
> [avohq/spec-first-inspector-server-sdk#2](https://github.com/avohq/spec-first-inspector-server-sdk/pull/2)
> (`sessionId` becomes a required wire field, empty string for server SDKs); this SDK already
> implements that. `trackingId`/`visitorId`/`userId` remain absent.

---

## Thread safety

Safe for concurrent use. The pending batch buffer and sampling rate are lock-guarded;
the atomic enqueue / swap-and-clear holds the lock only briefly and the HTTP send is
always performed outside the lock.

---

## Conformance

This SDK ships a thin CLI harness (`AvoInspector.Conformance`) implementing the
[runner contract](https://github.com/avohq/spec-first-inspector-server-sdk/blob/main/conformance/runner-contract.md).
To run the official suite against it:

```sh
./scripts/run-conformance.sh
```

The script builds the harness, fetches the spec repo (which hosts the language-agnostic
suite-runner + mock server), and runs all 30 fixtures. The vendored fixtures under
`conformance/fixtures/` also back a self-contained `dotnet test` run.

```sh
dotnet test       # unit tests + the 13 schema-extraction golden fixtures
```

---

## Maintainers

- **Bump `InspectorVersion.LibVersion`** (in `src/AvoInspector/InspectorVersion.cs`) and
  the `<Version>` in the `.csproj` on every release. It is sent on the wire as `libVersion`
  and MUST be a plain SemVer string with no suffix.
- `InspectorVersion.SpecVersion` records the spec contract version this SDK implements.
  When a new `[WIRE]`-tagged spec release appears, regenerate/update and bump it.

### Publishing a release (one tag to publish)

The package is distributed on [NuGet](https://www.nuget.org/) (`dotnet add package AvoInspector`).
Releases are automated by [`.github/workflows/publish.yml`](./.github/workflows/publish.yml):

1. Bump **both** `<Version>` in `src/AvoInspector/AvoInspector.csproj` **and**
   `InspectorVersion.LibVersion` to the new SemVer (the `VersionTests` drift-guard and the
   publish workflow both fail if they disagree). Update `CHANGELOG.md`.
2. Tag and push: `git tag v1.0.1 && git push origin v1.0.1`.
3. The workflow verifies the tag matches `<Version>`, runs the tests, and pushes the
   `.nupkg` + `.snupkg` (symbols) to nuget.org.

**One-time setup:** add a repo secret `NUGET_API_KEY` (nuget.org → Account → API Keys).
The package ships the README, XML docs (IntelliSense), and SourceLink symbols, so consumers
can step into the SDK's source while debugging. To build a package locally:
`dotnet pack src/AvoInspector/AvoInspector.csproj -c Release -o artifacts`.

---

## License

[MIT](./LICENSE).
