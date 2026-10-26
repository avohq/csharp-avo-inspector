---
import:
src/AvoInspector/Internal/EventSpec/EventSpecTypes.cs.md
src/AvoInspector/Internal/Logger.cs.md
---

## Short description

Fetches an event's spec from `GET <base>/trackingPlan/eventSpec` and parses it (AVO-4069), a port of
the Node SDK's `AvoEventSpecFetcher`. Never throws: every outcome is a `FetchResult`.

## Tech stack

`HttpClient` (one static instance, infinite client timeout; the per-fetch budget is a linked
`CancellationTokenSource`), `System.Text.Json.JsonDocument` for parsing (no reflection, so trimmed and
Native AOT hosts work).

## Data

- `FetchOutcome`: `Ok` (200, parseable body), `NotFound` (404), `Failed` (other status, timeout,
  network error, unparseable body), `Abandoned` (the abort token was cancelled).
- `FetchResult`: outcome and the parsed `EventSpecResponse?` (non-null only for `Ok`).
- `FetchTimeout`: 10 s; `internal static` and settable for tests only.

## Functional requirements

- `FetchAsync(key, baseUrl, eventName, streamId, originHint, outputReference, shouldLog, abortToken)`:
  joins an in-flight fetch of the same key; otherwise starts one on the thread pool, registers it
  under the key (under a lock) and removes it when it completes.
- URL: `baseUrl + "/trackingPlan/eventSpec?apiKey=…&eventName=…&streamId=…"`, each value
  `Uri.EscapeDataString`-encoded, then `&originHint=…` and `&outputReference=…` only when non-null
  (appended last, so a source fetch is unchanged). Header `Accept: application/json`; no API key
  header.
- `Parse(body)`: `null` unless the body is a JSON object. Metadata strings default to `""`. Each
  `events[]` entry needs a string `id` (else skipped); `b` → branch, `vids` (strings only),
  `p` → constraints by name: `t`, `r` (true only for JSON `true`), `l`, and the maps `p`, `v`, `rx`,
  `minmax` (non-string IDs dropped, keys left with none dropped, map `null` when empty), `children`
  recursively up to depth 10.
- Logs (when `shouldLog`): event name, status code, error type. Never the API key or the body.
