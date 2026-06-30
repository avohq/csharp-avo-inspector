---
import:
src/AvoInspector/AvoInspectorOptions.cs.md
src/AvoInspector/AvoInspectorEnv.cs.md
src/AvoInspector/AvoSchemaParser.cs.md
src/AvoInspector/SchemaEntry.cs.md
src/AvoInspector/AvoInspectorTrackException.cs.md
src/AvoInspector/InspectorVersion.cs.md
src/AvoInspector/Internal/WireEvent.cs.md
src/AvoInspector/Internal/InspectorHttpSender.cs.md
src/AvoInspector/Internal/Logger.cs.md
src/AvoInspector/Internal/ThreadSafeRandom.cs.md
---

## Short description

`public sealed class AvoInspector` — the public server-side SDK surface (SPEC.md §4). Extracts a
type schema from arbitrary event-property maps and reports it to the Inspector HTTP API, handling
sampling (§7.7), batching (§12), and graceful error recovery (§7.5) transparently.

## Tech stack

C# / .NET. Uses `System.Threading.Timer` for scheduled flushes, `Task`-based async, and
`CancellationTokenSource` for shutdown. No public dependency on the HTTP layer — sending is
delegated to the internal `InspectorHttpSender`.

## Data

Constructor options are fixed at construction and treated as immutable afterward: `apiKey`,
`appName`, `appVersion`, `env` (+ its wire string), `batchSize`, `batchFlushSeconds`, `maxQueueSize`,
`disableBatchTimer`.

Mutable runtime state, all guarded by a single private lock: `_samplingRate` (default `1.0`),
`_pendingBatch` (`List<WireEvent>`), `_pendingSends` (set of in-flight send `Task`s), `_destroyed`,
the `_flushTimer`, and a `CancellationTokenSource`.

`_shouldLog` is a **process-wide `static volatile bool`** (SPEC.md §4.4) — shared by every instance
in the process, not per-instance.

Constants: production endpoint `https://api.avo.app/inspector/v1/track`; mock-endpoint env var name
`AVO_INSPECTOR_MOCK_ENDPOINT`; default flush timeout `10_000` ms.

## Non-functional requirements

- **Thread safety.** Safe for concurrent use. The pending batch and sampling rate are lock-guarded;
  **HTTP sends are dispatched outside the lock** (SPEC.md §3.1).
- **Delivery is at-most-once.** Buffered and in-flight events are lost on process exit. Callers MUST
  `Flush` (or `await` the `TrackSchemaFromEvent` task) before a process / serverless handler exits
  if events may be in-flight or buffered (SPEC.md §3.4, §4.6, §11).
- The scheduled-flush `Timer` runs on background thread-pool threads and **never holds the process
  open**.

## Functional requirements

### Construction & configuration

```csharp
public AvoInspector(AvoInspectorOptions options)
public AvoInspector(string apiKey, string env, string version, string? appName = null,
    int? batchSize = null, double? batchFlushSeconds = null, int? maxQueueSize = null,
    bool? disableBatchTimer = null)
```

- The options overload throws `ArgumentNullException` on a null `options`, then maps `Env` via its
  enum→wire string.
- Both overloads converge on the same init logic and **validate in order**: `apiKey` first, then
  `version`. Each must be non-null/non-empty/non-whitespace, else throw `ArgumentException` with the
  **exact** spec messages — `"[Avo Inspector] No API key provided. Inspector can't operate without
  API key."` and `"[Avo Inspector] No version provided. Many features of Inspector rely on
  versioning. Please provide comparable string version, i.e. integer or semantic."`
- **Env fallback never throws** (SPEC.md §6.3): an absent/empty/unrecognized env string falls back
  to `Dev` with a warning. (The enum-typed options overload cannot hit this path.)
- `_shouldLog` is initialized to `true` iff `env == Dev`.
- Batch config resolution (SPEC.md §12.2): default `batchSize` 30, `batchFlushSeconds` 30,
  `maxQueueSize` 1000. Invalid values (`batchSize < 1`, `batchFlushSeconds <= 0`, `maxQueueSize < 1`)
  warn and fall back to the default. **`Dev` forces effective `batchSize = 1` (immediate send),
  overriding any supplied value.**
- A scheduled flush `Timer` is started only when `!disableBatchTimer && effectiveBatchSize > 1`;
  its period is `batchFlushSeconds * 1000` ms (floored to 1 ms). It is disabled in dev and when
  `disableBatchTimer` is set.

```csharp
public void EnableLogging(bool enable)
```

Sets the process-wide `_shouldLog` flag. **IMPORTANT:** MUST NOT be enabled in production contexts;
affects every instance in the process.

### Schema extraction

```csharp
public IReadOnlyList<SchemaEntry> ExtractSchema(IDictionary<string, object?>? eventProperties)
```

Synchronous, delegates to `AvoSchemaParser.ExtractSchema`. **Never throws** (SPEC.md §4.3): returns
an empty list for a null map or on any internal parser error (logging the error type name only).

### Tracking

```csharp
public Task<IReadOnlyList<SchemaEntry>> TrackSchemaFromEvent(
    string eventName, IDictionary<string, object?>? eventProperties, string? streamId = null)
```

Pipeline: extract schema → resolve stream id → apply per-event sampling → enqueue into the pending
batch → dispatch a send when a flush trigger fires.

- **Post-`Destroy` no-op:** resolves with an empty list, no enqueue, no HTTP call. Re-checked again
  under the lock right before enqueue to handle a mid-flight `Destroy`.
- **Sampling (SPEC.md §7.7):** snapshots the current rate under the lock and drops the event (returns
  the extracted schema, unsent) when `ThreadSafeRandom.NextDouble() >= samplingRate`. The `>=`
  comparison guarantees `samplingRate == 0.0` **always** drops. Sampling is applied at enqueue,
  before buffering.
- **Enqueue & overflow (SPEC.md §12.5):** appends a `WireEvent`; while `count > maxQueueSize`, drops
  the **oldest (FIFO)** entries and logs a **count only** (never event contents).
- **Size trigger (SPEC.md §12.3/§12.4):** when `count >= effectiveBatchSize`, the batch is swapped
  out and cleared atomically under the lock, then sent outside the lock.
- **Resolution value (SPEC.md §7.5):** resolves with the extracted schema at enqueue time. In
  immediate-send mode (`effectiveBatchSize == 1`, always true in dev) it awaits the triggered send
  and a **non-200 resolves an empty list**, while a network error/timeout resolves the schema. When
  `batchSize > 1` the resolved value **never** reflects the batch's eventual HTTP outcome.
- **Failure (SPEC.md §4.2.5):** a synchronous internal error before enqueue logs the error type name
  and faults the task with `AvoInspectorTrackException` (never the original exception).

### Stream id resolution (internal contract, SPEC.md §4.2/§8.2)

A non-empty `streamId` is used **verbatim**; if it contains `':'` a warning is logged (without the
value). An absent or empty `streamId` becomes `""`.

### Wire event construction (internal, SPEC.md §8.1)

Each event becomes a `WireEvent` carrying: `apiKey`, `appName`, `appVersion`,
`libVersion`/`libPlatform` (from `InspectorVersion`), `env` wire string, a fresh lowercase UUID v4
`messageId`, `streamId`, a UTC `createdAt` formatted `yyyy-MM-dd'T'HH:mm:ss.fff'Z'` (invariant
culture), the sampling-rate snapshot, `type = "event"`, the event name (null → `""`), and the
extracted schema as `eventProperties`.

### Flush

```csharp
public Task Flush(int timeoutMs = 10_000)
```

Force-flushes the pending batch (if any), then waits up to `timeoutMs` for all tracked in-flight
sends to complete or be abandoned. **Always resolves — a completion guarantee, not a delivery
guarantee** (SPEC.md §4.6); resolves even if a send faulted or the timeout elapses. A no-op after
`Destroy`. The instance remains usable afterward.

### Destroy

```csharp
public void Destroy()
```

Cancels and cleans up (SPEC.md §4.5, §11.3): marks destroyed, discards the pending batch **unsent**,
clears the in-flight tracking set (pending count → 0), disposes and clears the flush timer, and
cancels the shared `CancellationToken` to abort any in-flight HTTP send. **Does NOT flush.**
Constructor options, the sampling rate, and the process-wide logging flag persist. After `Destroy`,
`TrackSchemaFromEvent` is a no-op.

### Sampling-rate updates & endpoint resolution (internal)

- A batch send result updates `_samplingRate` **only on a 200 response** (when the response carries a
  new rate), under the lock (SPEC.md §7.4/§7.7).
- Batches are dispatched outside the lock and tracked in `_pendingSends` (with a continuation that
  removes the task on completion) so `Flush` can await them. A `Destroy` that races a dispatch is
  guarded both before the send starts and at tracking time, so **no batch reaches the wire after
  `Destroy`** (SPEC.md §4.5/§12.6).
- **Endpoint resolution is fail-closed (SPEC.md §7.1):** a `prod` instance **NEVER** honors
  `AVO_INSPECTOR_MOCK_ENDPOINT`. Only non-prod instances use the override when it is set (as-is, no
  path appending); otherwise the production endpoint is used.

<!-- The class also exposes a small set of `internal` test-only hooks
(SetSamplingRateForTesting, CurrentSamplingRate, IsDestroyed, EffectiveBatchSize,
PendingBatchCount, ResolvedEndpointForTesting, ShouldLogForTesting). These are deliberately NOT part
of the public API; the sampling-rate setter is internal specifically so external callers cannot force
samplingRate = 0 and silently disable telemetry. -->
