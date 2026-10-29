## Short description

Internal data types for event spec validation (AVO-4069): the parsed `/trackingPlan/eventSpec`
response and the validation outcome. Ported from the Node SDK's `AvoEventSpecFetchTypes`.

## Tech stack

C# classes in namespace `Avo.Inspector.Internal.EventSpec`. Only `EventSpecMetadata` is serialized
(as the track body's `eventSpecMetadata`, through the source-generated `InspectorJsonContext`), so
only it carries `[JsonPropertyName]` attributes.

## Data

- `PropertyConstraints`: `Type` (tracking-plan type name, default `"unknown"`, never validated),
  `Required`, `IsList`, and four nullable `Dictionary<string, List<string>>` constraint maps —
  `PinnedValues`, `AllowedValues` (key: a JSON array of strings), `RegexPatterns`, `MinMaxRanges`
  (key: `"min,max"`, either bound may be empty) — each mapping a constraint key to the event IDs that
  require it; `Children` (nullable, name → constraints) for an object or list of objects.
- `EventSpecEntry`: `BranchId`, `BaseEventId`, `VariantIds`, `Props` (name → constraints; only
  constrained properties).
- `EventSpecMetadata`: `schemaId`, `branchId`, `latestActionId`, `sourceId` (all default `""`).
- `EventSpecResponse`: `Events` (empty when the endpoint knows no such event) and `Metadata`.
- `PropertyValidationOutcome`: `FailedEventIds` (first-seen order, no duplicates) and nullable
  `Children` (name → outcome).
- `EventValidationResult`: `Metadata` and `FailedProperties` (name → outcome, failed properties only).

## Functional requirements

All maps are ordinal-keyed. A parsed `EventSpecResponse` is cached and shared by every track, so
nothing may write to it after parsing.
