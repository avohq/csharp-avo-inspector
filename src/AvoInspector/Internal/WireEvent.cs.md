---
import: src/AvoInspector/SchemaEntry.cs.md
---

## Short description

`WireEvent` is the per-event JSON wire body sent to the Inspector ingestion API (SPEC.md §7.3). A batch on the wire is a JSON array of these objects.

## Tech stack

Internal C# class in namespace `Avo.Inspector.Internal`. Serialized with `System.Text.Json` via `[JsonPropertyName]` attributes.

## Data

`internal sealed class WireEvent` — every field carries an explicit `[JsonPropertyName("…")]` and serializes under that exact camelCase wire name. Public get/set properties:

- `string ApiKey` → `apiKey`
- `string AppName` → `appName`
- `string AppVersion` → `appVersion`
- `string LibVersion` → `libVersion`
- `string Env` → `env`
- `string LibPlatform` → `libPlatform`
- `string MessageId` → `messageId`
- `string StreamId` → `streamId`
- `string SessionId` → `sessionId`
- `string CreatedAt` → `createdAt`
- `double SamplingRate` → `samplingRate`
- `string Type` → `type` (defaults to `"event"`)
- `string EventName` → `eventName`
- `IReadOnlyList<SchemaEntry> EventProperties` → `eventProperties` (defaults to empty list)

All string fields default to `string.Empty`.

## Functional requirements

`EventProperties` is a list of `SchemaEntry`, which serializes via its own attribute-bound converter (no global naming policy is applied).

// The two load-bearing wire-shape decisions below are deliberate divergences from SPEC.md.

**IMPORTANT: `sessionId` is always emitted as `""` (empty string).** SPEC.md §3.3/§7.3.1 say `sessionId` MUST NOT be sent, but the live ingestion pipeline silently drops events that omit it — the request still returns `200 {"success":true}` yet the event never reaches the dashboard. Emitting `sessionId: ""` is necessary and sufficient for ingestion (matching the canonical `js-avo-inspector` SDK).

**IMPORTANT: the type intentionally has no `trackingId`, `visitorId`, or `userId` field**, so they are never serialized. These are not required by the backend.

There is no `eventId`, `eventHash`, or `avoFunction` field either.
