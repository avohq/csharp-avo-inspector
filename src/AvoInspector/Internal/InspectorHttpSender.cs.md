---
import: src/AvoInspector/Internal/WireEvent.cs.md
import: src/AvoInspector/Internal/Logger.cs.md
---

## Short description

`InspectorHttpSender` performs the single Inspector HTTP POST for a batch of events (SPEC.md §7): a JSON-array body, conditional gzip, a 10-second per-request timeout, and the §7.5 error taxonomy. **It never throws — every outcome is mapped to a `SendStatus`.**

## Tech stack

Static C# class in namespace `Avo.Inspector.Internal`, built on `System.Net.Http`, `System.Text.Json`, and `System.IO.Compression`. Async (`Task`-returning).

## Data

`internal enum SendStatus { Ok, Non200, Error }` — the three terminal outcomes:
- `Ok` — HTTP 200.
- `Non200` — any non-200 response (4xx/5xx).
- `Error` — a network error, the 10s timeout, or a destroy-triggered abandon.

`internal readonly struct SendResult` — `SendResult(SendStatus status, double? newSamplingRate)` exposing `SendStatus Status` and `double? NewSamplingRate`. `NewSamplingRate` is non-null only on a successful 200 carrying a valid rate.

## Functional requirements

Public surface is one method:

`public static async Task<SendResult> SendAsync(string endpoint, WireEvent[] batch, bool shouldLog, CancellationToken abortToken = default)`

- Serializes `batch` to a JSON array (UTF-8). **A serialization failure is caught, logged via `Logger.Error(shouldLog, …)`, and returned as `SendStatus.Error` — never rethrown.**
- **Gzip is applied only when the raw UTF-8 body is ≥ 1024 bytes** (SPEC.md §7.3.5). When gzipped, sets `Content-Encoding: gzip`; **`Content-Type` stays `application/json` regardless**. If compression fails, falls back to the uncompressed body (no gzip header). `Content-Length` is set automatically from the payload length. Sends `Accept: application/json`.
- POSTs to `endpoint` using a single shared `HttpClient` whose client-wide `Timeout` is infinite; the **10-second timeout is enforced per-request via a `CancellationTokenSource`** linked together with `abortToken` (so `Destroy()` cancelling `abortToken` abandons an in-flight send).
- On HTTP 200: parses the response body and returns `SendResult(Ok, samplingRate)`.
- On any other status: logs `"Inspector API returned status <code>"` (gated by `shouldLog`) and returns `SendResult(Non200, null)`. **The batch is never re-queued.**
- On any exception (network error, timeout, or abandon): swallows it and returns `SendResult(Error, null)`. The log label distinguishes abandon (`abortToken` cancelled) → `"Request abandoned (destroyed)"`, timeout (`OperationCanceledException`) → `"Request timed out"`, else `"Request failed"`. **IMPORTANT: never logs the request body or apiKey (SPEC.md §7.5.1); never re-queues.**

Internal helpers:
- `TryGzip(byte[] raw, out byte[] compressed)` — gzips at `CompressionLevel.Optimal`; returns false (and yields `raw`) on failure.
- `TryReadSamplingRateAsync(HttpResponseMessage)` — returns the `samplingRate` from a JSON object body **only when it is a number in `[0.0, 1.0]`**; returns null on empty/missing/out-of-range/unparseable bodies (parse errors are swallowed). This is the sole signal callers use to update the sampling rate, **so the sampling rate updates only on a valid 200 body** (SPEC.md §7.4).

## Non-functional requirements

The shared `HttpClient` is reused across all sends (no per-call socket churn); concurrency safety relies on per-request `CancellationToken`s rather than the shared client `Timeout`.
