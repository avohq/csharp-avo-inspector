## Short description

A throwaway in-process loopback HTTP sink used by the example's `--dry-run` so the exact payloads the
SDK would POST can be shown offline, with no credentials or network egress. Records each request
(gunzipping `Content-Encoding: gzip` bodies) and always replies `200 {"samplingRate":1.0}`.
Example-only; NOT part of the SDK.

## Tech stack

C#; `System.Net.HttpListener`; `System.Net.Sockets.TcpListener` (free-port probe);
`System.IO.Compression.GZipStream`.

## Data

`internal sealed class LocalSink : IDisposable`
- `string BaseUrl` — `http://127.0.0.1:<port>` the SDK is pointed at.
- `IReadOnlyList<Captured> Requests` — snapshot of recorded requests.
- `Captured` → `{ string? ContentType; string? ContentEncoding; int WireBytes; string JsonBody }`
  (`JsonBody` is the decompressed UTF-8 body; `WireBytes` is the on-the-wire byte count).

## Functional requirements

- On construction, bind an `HttpListener` on a loopback port; **retry with a fresh port on
  `HttpListenerException`** (the `FreePort()`→`Start()` TOCTOU window), up to a few attempts.
- For each `POST`: read the raw body, gunzip it when `Content-Encoding: gzip`, record a `Captured`,
  and respond `200 application/json {"samplingRate":1.0}`.
- `Dispose()` stops and closes the listener.

## Non-functional requirements

**Always close (or abort) the response, even on a handler error** — otherwise the SDK's send blocks
until its 10s timeout. Recording is lock-guarded for concurrent requests.
