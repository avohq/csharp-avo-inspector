---
import: src/AvoInspector/SchemaEntry.cs.md
---

## Short description

`System.Text.Json` converter that serializes a `SchemaEntry` to the exact Inspector wire shape. Write
-only.

`internal sealed class SchemaEntryJsonConverter : JsonConverter<SchemaEntry>`

## Tech stack

C# / .NET, `System.Text.Json` (`Utf8JsonWriter`).

## Functional requirements

- `Write` emits an object: `{ "propertyName": <string>, "propertyType": <string> }`, then a
  `"children"` member **only when** `SchemaEntry.Children` is non-`null` (a `null` `Children` is
  omitted entirely — not written as JSON `null`).
- The `children` value is written recursively as a union member, by runtime type:
  - `null` → JSON null
  - `string` → JSON string
  - `SchemaEntry` → nested object (same `propertyName`/`propertyType`/`children?` shape)
  - `IEnumerable` → JSON array, each element recursively written.
- **IMPORTANT:** a `string` is checked before the generic `IEnumerable` branch (a string is an
  `IEnumerable<char>`); otherwise type strings would serialize as char arrays.
- `Read` is unsupported and throws `NotSupportedException` ("SchemaEntry is serialize-only.").

## Non-functional requirements

- A value matching none of the union cases falls back to `value.ToString()` as a JSON string
  (defensive; unreachable for well-formed schema output).
