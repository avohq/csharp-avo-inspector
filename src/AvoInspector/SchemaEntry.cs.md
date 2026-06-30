---
import: src/AvoInspector/Internal/SchemaEntryJsonConverter.cs.md
---

## Short description

One extracted schema property: the `{ propertyName, propertyType, children? }` record produced by
schema extraction and serialized to the Inspector wire shape.

`public sealed class SchemaEntry`

## Tech stack

C# / .NET. Annotated `[JsonConverter(typeof(Internal.SchemaEntryJsonConverter))]` so all
serialization is delegated to that converter.

## Data

Public mutable surface:

- `string PropertyName` — the source property key. Defaults to `string.Empty`.
- `string PropertyType` — the classified type, one of:
  `string | int | float | boolean | null | object | list(string) | list(int) | list(float) |
  list(boolean) | list(object) | unknown`. Defaults to `string.Empty`.
- `object? Children` — recursive, **heterogeneous** union. Each member is either a type string (e.g.
  `"string"`), a nested `SchemaEntry` (object properties), or a nested list of those (list elements).
  Concretely holds a `List<SchemaEntry>` for an `object` type or a `List<object?>` for a `list(...)`
  type.

Constructors:

- `SchemaEntry()`
- `SchemaEntry(string propertyName, string propertyType, object? children = null)`

## Functional requirements

- **IMPORTANT:** `Children` is non-`null` (possibly empty) **iff** `PropertyType` is `"object"` or any
  `list(...)` type; it is `null` for all primitive scalar types. The converter omits `children` from
  JSON exactly when it is `null`.
