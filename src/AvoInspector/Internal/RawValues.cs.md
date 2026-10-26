---
import: src/AvoInspector/AvoSchemaParser.cs.md
---

## Short description

Reads raw event property values the way the JavaScript SDKs see them, for event spec validation and
value encryption (AVO-4069).

## Functional requirements

- `IsMap` = `AvoSchemaParser.IsObjectMap`; `IsList` = a non-string, non-map `IEnumerable`.
- `TryGetChild(container, name, out value)`: from `IDictionary<string, object?>` or a non-generic
  `IDictionary` (keys compared by `ToString()`); false otherwise.
- `TryGetString`: `string`, `char`, `Guid` (`D`), `DateTime`/`DateTimeOffset` (`O`), `TimeSpan`
  (`c`), `Uri` (original string), `DateOnly`/`TimeOnly` on net8.0 — the types the parser reports as
  `"string"`.
- `TryGetNumber`: every integral type, `float`, `double`, `decimal`, as `double`.
- `ComparableString`: `"null"`, `"true"`/`"false"`, strings as above, numbers as JavaScript formats
  them (`FormatNumber`; a `float` from its own shortest form, a `decimal` without trailing zeros,
  `long`/`ulong` exactly), maps and lists as `ToJson`, falling back to `"[object Object]"` when that
  throws; anything else `Convert.ToString(…, InvariantCulture)`.
- `ToJson`: compact JSON via `Utf8JsonWriter` (no reflection): maps in enumeration order, lists as
  arrays, numbers as `ComparableString`, `NaN`/infinities as `null`, other types as their string.
  Throws `InvalidOperationException` past depth 10 or 10,000 nodes (so a self-containing value never
  overflows the stack).
- `FormatNumber`: JavaScript `Number.prototype.toString()` — shortest round-trip digits, plain
  notation while the decimal exponent n satisfies -6 < n ≤ 21, else `d.ddde±x`; `NaN`, `Infinity`,
  `-Infinity`, `0` for ±0.
