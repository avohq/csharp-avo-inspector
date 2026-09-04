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
- `string? AppVersion` → `appVersion` — **nullable, no `[JsonIgnore]`: always serialized, even when
  `null`** (the one field on the wire that may legitimately be a literal JSON `null`; see below)
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
- `string? OutputReference` → `outputReference` — nullable, `[JsonIgnore(Condition =
  JsonIgnoreCondition.WhenWritingNull)]`: omitted from the wire entirely when `null`, never sent as
  `null` (SPEC.md §7.3.6)
- `string? OriginHint` → `originHint` — same nullable/`[JsonIgnore(WhenWritingNull)]` shape as
  `OutputReference`

Non-nullable string fields default to `string.Empty`. `AppVersion`/`OutputReference`/`OriginHint`
default to `null` (no initializer).

## Functional requirements

`EventProperties` is a list of `SchemaEntry`, which serializes via its own attribute-bound converter (no global naming policy is applied).

**IMPORTANT: `sessionId` is always emitted as `""` (empty string).** SPEC.md §3.3 REQUIRES exactly this of server SDKs (since spec v2.0.0): the live ingestion pipeline silently drops events that omit `sessionId` — the request still returns `200 {"success":true}` yet the event never reaches the dashboard. Emitting `sessionId: ""` is necessary and sufficient for ingestion (matching the canonical `js-avo-inspector` SDK). This is conformance, not a divergence.

**IMPORTANT: the type intentionally has no `trackingId`, `visitorId`, or `userId` field**, so they are never serialized. These are not required by the backend.

There is no `eventId`, `eventHash`, or `avoFunction` field either.

**IMPORTANT: `outputReference`/`originHint` are the OPTIONAL gateway coordinate fields of SPEC.md §4.2.1 / §7.3.6** (AVO-3516) — top-level siblings of `eventProperties` for gateway-scoped API keys (see `TrackOptions`). They are placed after `EventProperties` and each individually carries `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`, never a global `DefaultIgnoreCondition`, because a global setting would also suppress `appVersion: null` — the one field §7.3.6 has this SDK send as a literal wire `null`.
