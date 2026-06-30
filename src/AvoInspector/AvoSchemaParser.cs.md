---
import: src/AvoInspector/SchemaEntry.cs.md
---

## Short description

Pure, synchronous schema extraction. Maps an arbitrary tree of native event properties into the
Inspector schema shape: a `List<SchemaEntry>`.

`public static class AvoSchemaParser`

## Tech stack

C# / .NET. No external dependencies; reflects over `System.Collections` / `System.Collections.Generic`.

## Functional requirements

The public surface is one method:

`public static List<SchemaEntry> ExtractSchema(IDictionary<string, object?>? eventProperties)`

- A `null` map returns an **empty list** (not null).
- Every key in the map becomes one top-level `SchemaEntry` whose `PropertyName` is the key.

<!--
Type classification (the load-bearing contract). `propertyType` for a value is resolved by its
NATIVE RUNTIME TYPE, not its textual value:
-->

- `null` → `"null"`
- `string` → `"string"`
- `bool` → `"boolean"`
- integral types (`sbyte byte short ushort int uint long ulong`) → `"int"`
- floating types (`float double decimal`) → `"float"`. **IMPORTANT:** classification is by runtime
  type, so a whole-valued float such as `0.0` MUST classify as `"float"`, never `"int"`.
- a string-keyed map → `"object"`. **A map of ANY value type counts** — `IDictionary<string,object?>`,
  `Dictionary<string,int>`, `Dictionary<string,string>`, or any non-generic `IDictionary`. Such a map
  is an object, never a list of key/value pairs.
- an array / non-string `IEnumerable` → `"list(<elemType>)"`, where `<elemType>` is the basic type of
  the **FIRST element only**. An empty array, or one whose first element is `null`, defaults to
  `"list(string)"`.
- a **nested array** (an array element that is itself an array) → `"object"` (parity with JS
  `typeof [] === "object"`), as does any other unrecognized non-string collection.
- anything else → `"unknown"`.

`children` presence rule:

- `SchemaEntry.Children` is present (non-null, possibly empty) **iff** the value is "complex": a
  string-keyed map (→ `object`) or a non-string `IEnumerable` (→ `list(...)`). For all scalar types
  `Children` stays `null` and is omitted from the wire.
- For an object value, `Children` is a `List<SchemaEntry>` (one per key, recursively mapped).
- For an array value, `Children` is a `List<object?>` of the mapped element types (recursively),
  after structural dedup (below).

Array element dedup:

- Mapped array element types are deduplicated preserving first-seen order. Primitive type strings are
  compared by value; nested structures (`SchemaEntry` objects and nested arrays) by **structural deep
  equality** (`PropertyName` + `PropertyType` + recursive `Children`). `DeepEqual` is `internal` and
  exposed for tests.

Depth-limited recursion:

- Maximum recursion depth is **10** (`MaxDepth`). Beyond it, a complex value is NOT descended into:
  for an object property it becomes `propertyType: "object"` with an empty `Children` list; for an
  array element it becomes the bare type string `"object"`.

Internal helpers (not public): `Mapping`, `MapObject`, `MapArray`, `GetPropValueType`,
`GetBasicPropType`, `IsComplex`, `IsObjectMap`, `EnumerateMap`, `FirstOrNull`, `RemoveDuplicates`.

## Non-functional requirements

- Pure and synchronous; allocates fresh lists, mutates no input.
- **No internal try/catch.** The parser MAY throw on pathological input; the safe boundary that
  catches and returns an empty list lives in the caller (`AvoInspector.ExtractSchema`).

## Examples

Input map `{ "id": 5, "ratio": 0.0, "tags": ["a", "b"], "user": { "name": "x", "vip": true } }`
extracts to:

<!--
[
  { propertyName: "id",    propertyType: "int" },
  { propertyName: "ratio", propertyType: "float" },                       // whole-valued float
  { propertyName: "tags",  propertyType: "list(string)", children: ["string"] },  // deduped
  { propertyName: "user",  propertyType: "object", children: [
      { propertyName: "name", propertyType: "string" },
      { propertyName: "vip",  propertyType: "boolean" }
  ]}
]
-->
