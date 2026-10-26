# Avo Inspector — C# / .NET SDK

> Implements [`avohq/spec-first-inspector-server-sdk`](https://github.com/avohq/spec-first-inspector-server-sdk) **v3.0.0**

A server-side SDK for [Avo Inspector](https://www.avo.app/). It extracts a type
schema from the properties of the analytics events your backend sends, and reports
those schemas to the Avo Inspector API so you can catch tracking-plan drift before it
reaches production.

It is **server-side only**: no browser, no session/visitor tracking, no persistent
storage. All state is in memory.

- ✅ Passes all 36 fixtures of the spec conformance suite (schema-extraction,
  wire-protocol, error-handling, batching) — see [Conformance](#conformance).
- ✅ Thread-safe; safe for concurrent use on multi-threaded servers.
- ✅ Batching with size + time/idle flush triggers, bounded queue, and gzip.
- ✅ In `dev` and `staging`: property values are checked against your tracking plan
  (allowed values, pinned values, regex, min/max), and can be sent encrypted so the
  Inspector Debugger shows them — see [Value checks](#value-checks-dev-and-staging).
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
individual destination. To support that, `TrackSchemaFromEvent` takes three **optional
trailing parameters** that let a gateway-scoped key tell observations taken at different
checkpoints, and from different upstream sources, apart (SPEC.md §4.2.1; the wire mapping
is §7.3.6).

```csharp
using Avo.Inspector;

await inspector.TrackSchemaFromEvent(
    eventName: "Purchase Completed",
    eventProperties: new Dictionary<string, object?> { ["amount"] = 99.0 },
    streamId: "web",
    outputReference: "meta-x7k2q",  // which output checkpoint this observation was bound for
    originHint: "web",              // which upstream source produced the event
    originAppVersion: "5.1.0");     // that source's app version — optional; see "Origin hint"
```

All three are `string?`, optional and independent — set any combination, by name:

| Parameter | Purpose |
|---|---|
| `outputReference` | Which gateway output (destination checkpoint) this observation was bound for. Avo mints the value per output — a `<slug>-<shortid>` string, set once — and the Avo MCP prints it on that output's line under `### Outputs` in `get(itemType: "gateway")`. Leave unset for a gateway-level observation not tied to one output. |
| `originHint` | Identifies the event's upstream source (e.g. `"web"`, `"ios"`, `"android"`). See "Origin hint" below. |
| `originAppVersion` | App version **of the source named by `originHint`**, not of this SDK instance — a per-event override. See "Origin hint" below for how the two interact. |

They are named parameters rather than an options object because SPEC.md §4.2.1 picks the
call-site shape from the target language: C# has named arguments, so the three are flattened
and an IDE surfaces them at the call site. The wire body is identical to the grouped shape
other SDKs use.

All three values are trimmed before sending; empty or whitespace-only values are treated
as absent, and `outputReference`/`originHint` are then omitted from the wire body
entirely rather than sent as `null` or `""` (SPEC.md §7.3.6). Supplying none of them —
including through the retained three-parameter overload, which stays source- and
binary-compatible with 1.0.0 — produces a wire body with exactly the 1.0.0 key set and
values; only `libVersion` differs.

> A customer's own event property literally named `outputReference`, `originHint`, or
> `appVersion` (with unrelated business meaning) is unaffected — SPEC.md §7.3.6 calls it
> "an ordinary property". It still appears inside `eventProperties` with its own extracted
> type, exactly as before, and the top-level fields described here come only from these
> parameters, never from event data; neither direction leaks into the other.

### Origin hint

`originHint` must be a **low-cardinality** value (e.g. `"web"`, `"ios"`, `"android"`) —
it **MUST NOT** be a user identifier or any other high-cardinality value. This is a
documentation-only rule; the SDK does not validate it at runtime.

Setting `originHint` marks the event as coming from a different source than the app this
`AvoInspector` instance was constructed with, which changes how `originAppVersion` resolves
on the wire. The parameter is named for whose version it carries: `originHint` says which
source, `originAppVersion` says that source's version. It sets the event's `appVersion` on
the wire — the wire field keeps its own name.

| `originHint` (normalized) | `originAppVersion` (normalized) | wire `appVersion` |
|---|---|---|
| set | set (non-blank) | `originAppVersion`, trimmed |
| set | absent/blank | literal JSON `null` |
| absent | set (non-blank) | `originAppVersion`, trimmed |
| absent | absent/blank | the constructor `Version` value (unchanged behavior) |

`originHint` without an `originAppVersion` is an ordinary, fully supported call: the
`/inspector/v2/track` endpoint this SDK posts to decodes both gateway coordinates and
stores a `null` `appVersion` as `"unversioned"`. The SDK logs nothing about it.

---

## Value checks (dev and staging)

In `Dev` and `Staging`, the SDK checks each event's property **values** against the event
in your tracking plan, as the web, Node and mobile SDKs do. Before sending an event it
fetches the event's spec from Avo (`GET https://api.avo.app/trackingPlan/eventSpec`), then
checks every value constraint the spec sets:

- **allowed values** — the value must be one of the property's allowed values;
- **pinned values** — the value must equal the pinned value;
- **regex** — a string value must match the pattern;
- **min/max** — a number must be within the range (either bound may be open).

Constraints are checked on each property and on the properties of an object, or of each
object in a list (one level deep). Each constraint belongs to the tracking-plan event or
variant that sets it, so a value can fail for one variant and pass for another. Failures
show up as value issues in the Inspector Debugger. Types are not checked here: Avo
compares the reported schema with the tracking plan for that.

```csharp
var inspector = new AvoInspector(new AvoInspectorOptions
{
    ApiKey  = "my-inspector-api-key",
    Env     = AvoInspectorEnv.Staging,
    Version = "1.4.2",
    // Optional: lets the Debugger show the values themselves (see below).
    PublicEncryptionKey = "04a1b2…",
});

// "Enterprise" is not one of the plan's allowed values: the Debugger shows a value issue.
await inspector.TrackSchemaFromEvent("Plan Upgraded",
    new Dictionary<string, object?> { ["plan"] = "Enterprise" });
```

**Gateways.** With `originHint` (and `outputReference`), the spec fetch carries them too, so
a gateway key is checked against the event as expected at that checkpoint: the gateway
input with `originHint` alone, or that output with both. Each checkpoint's spec is cached
on its own. A gateway key has no spec without `originHint`; such events are sent without
value checks.

**It never blocks or drops an event.** A spec is cached for 60 seconds (or 50 events) per
event name, stream id and checkpoint, and concurrent tracks of the same event share one
fetch. If the fetch fails, times out (10 seconds), or Avo has no spec for the event, the
event is sent without value checks, as before; a failed fetch is retried after 5 seconds
(60 for a 404). The first track of an event therefore waits for its spec fetch;
`Flush()` waits for tracks still waiting on a spec, within its timeout. Checks are bounded:
a list of more than 1,000 items, or a string of more than 10,000 characters against a
regex, is not checked, and each regex match has a 100 ms budget.

**`Prod` is unchanged:** no spec fetch, no value checks, no values sent.

### Showing values in the Debugger: `PublicEncryptionKey`

Without `PublicEncryptionKey`, no property values ever leave your service — only names,
types and which checks failed. With it, in `Dev` and `Staging`, each property's value is
encrypted with that key (ECIES on P-256 with AES-256-GCM, the same format as the web and
Node SDKs) and sent as `encryptedPropertyValue`; only the holder of the matching private
key, in the Inspector Debugger, can read it. An object's value is encrypted as its JSON; a
list's value is not sent. Copy the public key from your Avo workspace (66 or 130 hex
characters). A key that cannot be used is reported once on stderr at construction, and
events are then sent without values.

Encryption needs **.NET 8 or later** (the SDK's `net8.0` build): `netstandard2.0` has no
elliptic-curve Diffie-Hellman. On .NET Framework or .NET 6/7 the key is ignored with a
warning at construction; value checks still run.

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
| `PublicEncryptionKey` | string | no | — | Hex P-256 public key. In `Dev`/`Staging`, values are sent encrypted for the Inspector Debugger. Options constructor only. See [Value checks](#value-checks-dev-and-staging). |

The constructor throws synchronously (an `ArgumentException`) for a missing/whitespace
`ApiKey` or `Version`. An invalid or absent `env` string **never throws** — it falls
back to `Dev` with a warning.

### `Task<IReadOnlyList<SchemaEntry>> TrackSchemaFromEvent(string eventName, IDictionary<string, object?>? eventProperties, string? streamId)`

### `Task<IReadOnlyList<SchemaEntry>> TrackSchemaFromEvent(string eventName, IDictionary<string, object?>? eventProperties, string? streamId = null, string? outputReference = null, string? originHint = null, string? originAppVersion = null)`

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

`outputReference`, `originHint` and `originAppVersion` are the optional per-call gateway
coordinates — see "Gateways" above for the full behavior and the app-version resolution
table. Supplying none of them produces a wire body with exactly the 1.0.0 key set and
values; only `libVersion` differs.

The three-parameter overload is the 1.0.0 signature, kept so precompiled 1.0.0 callers keep
binding; it delegates to the form above. It declares no default for `streamId` — a default
there would make a two-argument call ambiguous between the two — which changes nothing for
callers: a default is call-site metadata, not part of the CLR signature, so a two-argument
call simply resolves to the form above and produces the identical body.

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

Events are POSTed as a JSON array to `https://api.avo.app/inspector/v2/track` — the one
endpoint every Avo Inspector sender uses, browser and server alike. Request bodies
**≥ 1024 bytes are gzip-compressed** (`Content-Encoding: gzip`); smaller bodies are sent
uncompressed. The .NET runtime always provides gzip, so this SDK is never exempt from the
compression requirement. Every request has a 10-second timeout. Certificate validation always
uses the platform default and cannot be disabled.

Request headers:

| Header | Value |
|---|---|
| `Content-Type` | `application/json` |
| `Accept` | `application/json` |
| `api-key` | your Inspector API key. **Required** — the endpoint answers `400` without it. |
| `env` | `dev` / `staging` / `prod`. **Required**, and one of exactly those three strings. |
| `X-Avo-Client` | `csharp` — the SDK's `libPlatform`. Lets Avo attribute traffic per sender at the edge without decoding a body. |
| `Content-Encoding` | `gzip`, only when the body was compressed. |

There is still no `Authorization` header. `apiKey` and `env` also remain in every event body:
the endpoint reads them from the headers and ignores the body copies, but keeping both holds
one body shape — and one JSON Schema — across every Inspector sender.

`AVO_INSPECTOR_MOCK_ENDPOINT` redirects requests to a test endpoint, but is **fail-closed**:
a `Prod` instance ignores it unconditionally, so production traffic can never be redirected.

> **No `sessionId`.** Spec 3.0.0 removed the field from the wire body
> ([SPEC.md §3.3](https://github.com/avohq/spec-first-inspector-server-sdk/blob/main/SPEC.md)):
> a server SDK has no session to report, and `/inspector/v2/track` supplies the value itself.
> Spec 2.0.0 had REQUIRED it as `""` because ingestion then dropped events whose body omitted
> it — returning `200 {"success":true}` while nothing reached the dashboard — which is why the
> field is *removed* rather than *forbidden*: a body that still carries it stays valid. Use
> `streamId` to correlate events. `trackingId`/`visitorId`/`userId` remain forbidden.
>
> **Gateway fields (SPEC.md §7.3.6).** Since 1.1.0 an event may also carry two optional top-level
> siblings of `eventProperties` — `outputReference` and `originHint` — and `appVersion` may be a
> literal JSON `null` when `originHint` is set without a per-event app version. Both keys are
> omitted entirely when not provided, so a call that supplies none of the three coordinates
> produces a body with exactly the 1.0.0 key set and values — only `libVersion` differs. See
> [Gateways](#gateways).
>
> **Value checks (since 1.2.0, `dev` and `staging` only).** A validated event carries
> `eventSpecMetadata` (`schemaId`, `branchId`, `latestActionId`, `sourceId`), and each property
> that failed a value check carries `failedEventIds` (on the property, or on its children
> for an object or list of objects). With `PublicEncryptionKey`, the body carries
> `publicEncryptionKey` and properties carry `encryptedPropertyValue`. None of these keys is
> sent without a spec or a key, so the body is then exactly the 1.1.x one. See
> [Value checks](#value-checks-dev-and-staging).

---

## Thread safety

Safe for concurrent use. The pending batch buffer and sampling rate are lock-guarded;
the atomic enqueue / swap-and-clear holds the lock only briefly and the HTTP send is
always performed outside the lock.

---

## Conformance

This SDK ships a thin CLI harness (`AvoInspector.Conformance`) implementing version **1.1.0** of the
[runner contract](https://github.com/avohq/spec-first-inspector-server-sdk/blob/main/conformance/runner-contract.md)
— it forwards the three values from a fixture's `options` object as the matching top-level named
arguments (`outputReference`, `originHint`, `originAppVersion`), and passes none of them when the
fixture has no `options`. The suite drives the SDK through
`AVO_INSPECTOR_MOCK_ENDPOINT`, so the endpoint move to `/inspector/v2/track` needs no harness
change; the runner records request headers itself and asserts them via a fixture's
`expected_request_headers`. To run the official suite:

```sh
./scripts/run-conformance.sh
```

The script builds the harness, fetches the spec repo (which hosts the language-agnostic
suite-runner + mock server), and runs its fixtures. It defaults to `SPEC_REF=main`, which carries
spec **3.0.0** — the version `InspectorVersion.SpecVersion` records: the unified
`/inspector/v2/track` endpoint and its three required request headers, on top of 2.1.0's gateway
track options. All **36/36** fixtures pass, including the six gateway fixtures
(`wire-9`…`wire-13`, `batch-7`) and the header assertions carried by `wire-1` and `batch-1`.

3.0.0 reached `main` in [spec PR #3](https://github.com/avohq/spec-first-inspector-server-sdk/pull/3)
(merged 2026-09-09). Earlier revisions of this README told you to point `SPEC_REF` at the
`gateway-track-options` branch that PR was developed on; that branch is tree-identical to the merge,
so the override still works, but `main` is now the ref to use.

Of the header assertions, `wire-1` pins `env: dev` while `batch-1` pins `env: staging` on that same
header, so an SDK that hardcodes either value fails one of the two.

The vendored fixtures under `conformance/fixtures/` (a byte-identical snapshot of the spec's `main`
suite) also back a self-contained `dotnet test` run.

```sh
dotnet test       # unit tests + the 13 schema-extraction golden fixtures
```

---

## Maintainers

- **Bump `InspectorVersion.LibVersion`** (in `src/AvoInspector/InspectorVersion.cs`) and
  the `<Version>` in the `.csproj` on every release. It is sent on the wire as `libVersion`
  and MUST be a plain SemVer string with no suffix.
- `InspectorVersion.SpecVersion` records the spec contract version this SDK implements
  (currently `3.0.0`), and `InspectorVersion.HarnessContractVersion` the conformance runner
  contract its harness implements (currently `1.1.0`). When a new `[WIRE]`-tagged spec release
  appears, regenerate/update and bump them, along with `<AvoInspectorSpecVersion>` and the
  `Description` in `AvoInspector.csproj` and the badge at the top of this README.

### Publishing a release (one tag to publish)

The package is distributed on [NuGet](https://www.nuget.org/) (`dotnet add package AvoInspector`).
Releases are automated by [`.github/workflows/publish.yml`](./.github/workflows/publish.yml):

1. Bump **both** `<Version>` in `src/AvoInspector/AvoInspector.csproj` **and**
   `InspectorVersion.LibVersion` to the new SemVer (the `VersionTests` drift-guard and the
   publish workflow both fail if they disagree). Update `CHANGELOG.md`.
2. Tag and push: `git tag v1.0.1 && git push origin v1.0.1`.
3. The workflow verifies the tag matches `<Version>`, runs the tests, and pushes the
   `.nupkg` + `.snupkg` (symbols) to nuget.org.

No API-key secret is involved: the workflow authenticates to nuget.org over OIDC
(`NuGet/login@v1` as the `avo.cado` account, which needs `id-token: write` — already set in the
workflow). The package ships the README, XML docs (IntelliSense), and SourceLink symbols, so
consumers can step into the SDK's source while debugging.

### Publishing manually (fallback)

Use this only when the tag workflow cannot run — GitHub Actions is down or disabled, the OIDC
login fails, or you need to publish from a branch. **Prefer the tag flow above**: a local
`dotnet pack` does not set `ContinuousIntegrationBuild` (the `.csproj` gates it on
`GITHUB_ACTIONS`), so the package you build by hand is not the deterministic, reproducible one
CI produces and its SourceLink metadata may point at local paths.

You need push rights on the [`AvoInspector`](https://www.nuget.org/packages/AvoInspector)
package, and an API key from nuget.org → **Account → API Keys** → *Create* — scope it to
**Push** for the `AvoInspector` package only. The key is shown once; store it in your password
manager, not in the repo.

1. **Pre-flight.** On the commit you intend to ship:

   ```sh
   dotnet test tests/AvoInspector.Tests/AvoInspector.Tests.csproj -c Release
   ./scripts/run-conformance.sh          # see SPEC_REF in the script header
   ```

2. **Bump the version in both files** — `<Version>` in `src/AvoInspector/AvoInspector.csproj`
   and `InspectorVersion.LibVersion`. They are two files by spec necessity (SPEC.md §7.3.3
   requires a hardcoded constant), and `VersionTests` fails if they disagree, which is what stops
   a package shipping a stale `libVersion` on the wire. Add the `CHANGELOG.md` entry now, while
   you still remember what changed.

3. **Pack.**

   ```sh
   dotnet pack src/AvoInspector/AvoInspector.csproj -c Release -o artifacts
   ```

   This produces `artifacts/AvoInspector.<version>.nupkg` and a matching `.snupkg`.

4. **Check what you are about to publish** — cheap, and the one step that catches a mis-packed
   release before it becomes permanent:

   ```sh
   unzip -l artifacts/AvoInspector.<version>.nupkg
   ```

   Expect `README.md` at the package root, `lib/netstandard2.0/` **and** `lib/net8.0/`, and an
   `AvoInspector.xml` next to each `AvoInspector.dll` (that file is what gives consumers
   IntelliSense).

5. **Push.**

   ```sh
   dotnet nuget push "artifacts/AvoInspector.<version>.nupkg" \
     --api-key "$NUGET_API_KEY" \
     --source https://api.nuget.org/v3/index.json \
     --skip-duplicate
   ```

   The `.snupkg` alongside it is pushed automatically — pass `--no-symbols` to suppress that.
   `--skip-duplicate` turns "this version already exists" into a no-op instead of a failure.

6. **Tag the release** so git and nuget.org agree on what `v<version>` means:

   ```sh
   git tag v<version> && git push origin v<version>
   ```

   Pushing the tag **also triggers `publish.yml`**. That is fine and expected: it will pack the
   same version and its push will no-op on `--skip-duplicate`. Tag only after a successful manual
   push, so a failed manual attempt does not leave a tag pointing at an unpublished version.

7. **Verify.** The package appears at `https://www.nuget.org/packages/AvoInspector/<version>`;
   indexing usually takes a few minutes before `dotnet add package AvoInspector --version
   <version>` resolves it.

> **A published version is permanent.** nuget.org does not allow re-uploading a version, even
> after deleting it — you can only *unlist* it (which hides it from search while keeping existing
> restores working). If you ship a broken package, the fix is to bump the patch version and
> publish again, so double-check step 4 before step 5.

---

## License

[MIT](./LICENSE).
