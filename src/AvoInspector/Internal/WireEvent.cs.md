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

**IMPORTANT: there is no `sessionId` field.** Spec 3.0.0 removed it from the wire body (SPEC.md §3.3): a server SDK has no session to report, and `/inspector/v2/track` supplies the value itself. Spec 2.0.0 had REQUIRED it as `""` because ingestion then silently dropped events whose body omitted it — the request returned `200 {"success":true}` yet the event never reached the dashboard — which is why 3.0.0 *removes* rather than *forbids* it: the 3.0.0 schemas still accept a body that carries it, so a sender that has not regenerated stays valid. Correlation belongs in `streamId`.

**IMPORTANT: the type intentionally has no `trackingId`, `visitorId`, or `userId` field**, so they are never serialized. These are not required by the backend.

There is no `eventId`, `eventHash`, or `avoFunction` field either.

**IMPORTANT: `outputReference`/`originHint` are the OPTIONAL gateway coordinate fields of SPEC.md §4.2.1 / §7.3.6** (AVO-3516) — top-level siblings of `eventProperties` for gateway-scoped API keys, carried by the `outputReference` and `originHint` parameters of `TrackSchemaFromEvent`. They are placed after `EventProperties` and each individually carries `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`, never a global `DefaultIgnoreCondition`, because a global setting would also suppress `appVersion: null` — the one field §7.3.6 has this SDK send as a literal wire `null`.
