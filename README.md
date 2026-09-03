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

## Gateways

Avo Inspector is moving to a multi-gate model: one Inspector API key per *gateway* (a
server-side proxy or event bus checkpoint) rather than one Inspector source per
individual destination. `TrackSchemaFromEvent` accepts an optional trailing
four-parameter overload with a trailing `TrackOptions? options` argument that lets a gateway-scoped key tell
observations taken at different checkpoints, and from different upstream sources, apart.

```csharp
using Avo.Inspector;

await inspector.TrackSchemaFromEvent(
    eventName: "Purchase Completed",
    eventProperties: new Dictionary<string, object?> { ["amount"] = 99.0 },
    streamId: "web",
    options: new TrackOptions
    {
        OutputReference = "meta-x7k2q", // which output checkpoint this observation was bound for
        OriginHint      = "web",        // which upstream source produced the event
        AppVersion      = "5.1.0",      // that source's app version — keep this set whenever
                                        // OriginHint is set (see "Backend note" below)
    });
```

> Until the backend change tracked in AVO-3543 ships, always pair `OriginHint` with a non-blank
> `AppVersion` as above. `OriginHint` without `AppVersion` is a valid call per the contract, but
> the current `/inspector/v1/track` endpoint silently drops that event — see the **Backend note**
> further down.

`TrackOptions` has three `string?` properties, all optional and independent — set any
combination:

| Property | Purpose |
|---|---|
| `OutputReference` | Which gateway output (destination checkpoint) this observation was bound for. Leave `null` for a gateway-level observation not tied to one output. |
| `OriginHint` | Identifies the event's upstream source (e.g. `"web"`, `"ios"`, `"android"`). See "Origin hint" below. |
| `AppVersion` | Per-event app version override — see "Origin hint" below for how it interacts with `OriginHint`. |

All three values are trimmed before sending; empty or whitespace-only values are treated
as absent, and `OutputReference`/`OriginHint` are then omitted from the wire body
entirely rather than sent as `null` or `""`. The original three-parameter overload is
retained unchanged (source- and binary-compatible with 1.0.0) and delegates with
`options: null`; that call, or an empty `new TrackOptions()`, produces a wire body with
exactly the 1.0.0 key set and values — only `libVersion` differs. In the four-parameter
overload both `streamId` and `options` are required, so pass `streamId: null` when you
have no stream id; this keeps two- and three-argument calls binding unambiguously.

> A customer's own event property literally named `outputReference` or `originHint`
> (with unrelated business meaning) is unaffected. It still appears inside
> `eventProperties` exactly as before — the top-level `outputReference`/`originHint`
> fields described here come only from `TrackOptions`, never from event data, and
> neither direction leaks into the other.

### Origin hint

`OriginHint` must be a **low-cardinality** value (e.g. `"web"`, `"ios"`, `"android"`) —
it **MUST NOT** be a user identifier or any other high-cardinality value. This is a
documentation-only rule; the SDK does not validate it at runtime.

Setting `OriginHint` marks the event as coming from a different source than the app this
`AvoInspector` instance was constructed with, which changes how `AppVersion` resolves on
the wire:

| `OriginHint` (normalized) | `AppVersion` (normalized) | wire `appVersion` |
|---|---|---|
| set | set (non-blank) | `AppVersion`, trimmed |
| set | absent/blank | literal JSON `null` |
| absent | set (non-blank) | `AppVersion`, trimmed |
| absent | absent/blank | the constructor `Version` value (unchanged behavior) |

> **Backend note:** the Inspector backend does not yet honor `outputReference` or
> `originHint` on this SDK's endpoint (`POST /inspector/v1/track`), and does not yet
> accept a literal `appVersion: null`. Until the backend is updated, setting
> `OriginHint` without a non-blank `AppVersion` override causes the event to be
> **silently dropped** — the HTTP response is still `200`, but the event never reaches
> the Inspector dashboard. Track this at AVO-3543; a follow-up backend ticket must land
> before `OriginHint` can be used safely without an `AppVersion` override.

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

### `Task<IReadOnlyList<SchemaEntry>> TrackSchemaFromEvent(string eventName, IDictionary<string, object?>? eventProperties, string? streamId, TrackOptions? options)`

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

`options` carries optional per-call gateway coordinates (`OutputReference`, `OriginHint`)
and a per-event `AppVersion` override — see "Gateways" above for the full behavior and
the app-version resolution table. Omitting `options` (or passing an empty
`new TrackOptions()`) produces a wire body identical to before this parameter existed.

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
`children`. The C# runtime type is authoritative.

| CLR type | `propertyType` |
|---|---|
| `string`, `char` | `string` |
| `int`/`long`/`short`/`byte` (and unsigned) | `int` |
| `float`/`double`/`decimal` | `float` (so `0.0` is `float`, not `int`) |
| `bool` | `boolean` |
| `null` | `null` |
| `DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly`, `Guid`, `Uri` | `string` (these have no JSON-primitive type but serialize to strings) |
| `IDictionary` / any string-keyed map | `object` (recursive `children`) |
| arrays / `IEnumerable` | `list(<element>)` from the first element |
| anything else (enums, custom structs/classes) | `unknown` |

> **Enums and custom types map to `unknown`.** Their wire representation is ambiguous (an enum may
> serialize as its name or its number, a class however your serializer renders it), so the SDK does
> not guess. If you want a specific type, pre-convert the value (e.g. `myEnum.ToString()` for
> `string`, `(int)myEnum` for `int`) before tracking. A list whose elements are `unknown` is emitted
> as `list(object)`.

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

> **Gateway fields.** Since 1.1.0 an event may also carry two optional top-level siblings of
> `eventProperties` — `outputReference` and `originHint` — and `appVersion` may be a literal JSON
> `null` when `originHint` is set without a per-event app version. Both are omitted entirely when
> not provided, so a call without `TrackOptions` produces the 1.0.0 body unchanged. See
> [Gateways](#gateways).

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
