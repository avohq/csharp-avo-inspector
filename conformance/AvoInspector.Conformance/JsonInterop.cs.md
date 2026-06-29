## Short description

Materializes parsed JSON into the native value types the SDK's schema parser expects, and an
insertion-order-preserving string-keyed map. Lives in the conformance harness (and is linked into
the test project) so fixture inputs reach the SDK exactly as a real caller's native objects would.

## Tech stack

C#; `System.Text.Json` (`JsonElement`); `System.Collections.Generic`.

## Data

`internal static class JsonInterop`
- `static object? ToNative(JsonElement element)` — recursive conversion:
  - Object → `OrderedPropertyDictionary` (keys in document order)
  - Array → `List<object?>`
  - String → `string`; True/False → `bool`; Null/Undefined → `null`
  - Number → `ToNativeNumber` (see invariant)
- `static IDictionary<string, object?>? ToPropertyMap(JsonElement element)` — an Object becomes the
  ordered map; any other kind (incl. JSON `null`) becomes `null`.

`internal sealed class OrderedPropertyDictionary : IDictionary<string, object?>` — backed by a key
list plus a dictionary; enumerates strictly in insertion order.

## Functional requirements

<invariant>
**Int-vs-float is preserved from the JSON literal.** A number whose raw text contains `.`, `e`, or
`E` becomes a `double`; otherwise an `Int64` when it fits, else a `double`. So `3` → `long`
(classified `int`), `3.14`/`0.0` → `double` (classified `float`).
</invariant>

<invariant>
**Object key order is preserved.** Objects become `OrderedPropertyDictionary`, because the SDK
emits schema properties in the map's enumeration order and a plain `Dictionary` does not guarantee
that order.
</invariant>

## Non-functional requirements

Pure/synchronous; no I/O. `OrderedPropertyDictionary.Add` throws on a duplicate key; otherwise a
faithful `IDictionary<string, object?>` implementation.
