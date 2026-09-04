## Short description

Optional per-call gateway coordinates for `AvoInspector.TrackSchemaFromEvent` — the `TrackOptions`
record of SPEC.md §4.2.1 (AVO-3516). Used when this SDK's Inspector API key is a **gateway** key
shared across destinations, to label where an observation was taken: which output it was bound for,
and optionally which upstream source produced it. Plain mutable POCO with public get/set
properties, all defaulting to `null`.

## Data

```csharp
public sealed class TrackOptions
{
    public string? OutputReference { get; set; } // OPTIONAL, default null; wire: outputReference
    public string? OriginHint { get; set; }       // OPTIONAL, default null; wire: originHint
    public string? AppVersion { get; set; }        // OPTIONAL, default null; wire: appVersion (per-event override)
}
```

## Functional requirements

Normalization and wire mapping are normative in SPEC.md §7.3.6; conformance fixtures `wire-9` –
`wire-13` and `batch-7` gate them.

- `OutputReference` and `OriginHint` are each trimmed before sending; `null`, empty, or
  whitespace-only is treated as absent — the corresponding wire key is omitted entirely, never sent
  as `null` or `""` (SPEC.md §7.3.6).
- `OriginHint` MUST NOT be a user identifier or other high-cardinality value (a low-cardinality
  source label like `"web"`, `"ios"`, `"android"` is the intended shape). This is a
  documentation-only rule, not validated at runtime.
- Setting `OriginHint` makes the event source-scoped: `AppVersion` (trimmed, non-blank) is sent when
  present; otherwise wire `appVersion` is a literal JSON `null` — the constructing `AvoInspector`
  instance's configured version never applies in this case.
- Without `OriginHint`, a non-blank `AppVersion` (trimmed) overrides the instance's configured
  version for that one event; a blank or absent value falls back to the instance's configured
  version, unchanged.
- Introduced by `avohq/spec-first-inspector-server-sdk` v2.1.0 (SPEC.md §4.2.1 / §7.3.6;
  AVO-3516). `options` MUST NOT affect `ExtractSchema`, sampling, batching, or `streamId`
  handling, and a call without it produces the pre-2.1.0 wire body.
- `OriginHint` without an `AppVersion` is an ordinary, fully supported call and the SDK MUST log
  nothing about it: the `/inspector/v2/track` endpoint decodes both coordinates and stores a
  `null` `appVersion` as `"unversioned"`.

## Non-functional requirements

**Thread safety / reuse contract:** treat an instance as immutable once passed to
`AvoInspector.TrackSchemaFromEvent`. Its three properties are read once, by value, the first time
`options` is inspected within that call — inside `BuildWireEvent`, after schema extraction,
stream-id resolution, and the sampling check — and not at all if the event is sampled out before
`BuildWireEvent` runs. Mutating or reusing the same instance across concurrent calls is
memory-safe but produces nondeterministic, caller-surprising wire values rather than a defined
result. Construct a fresh instance per call (or per immutable set of values) instead — or, once
passed to one call, leave it unmutated and it may safely be reused as-is across further calls.
