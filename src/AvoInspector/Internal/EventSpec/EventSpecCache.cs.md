---
import: src/AvoInspector/Internal/EventSpec/EventSpecTypes.cs.md
---

## Short description

Per-instance, thread-safe cache of fetched event specs (AVO-4069), a port of the Node SDK's
`AvoEventSpecCache`.

## Data

Entries: value (`EventSpecResponse?`, `null` = cached failure), expiry, use count, last access, on
`Stopwatch` timestamps (monotonic). Constants: `MaxEntries` 50, `MaxUses` 50, `SpecTtl` 60 s,
`TransientFailureTtl` 5 s.

## Functional requirements

- `MakeKey(streamId, eventName, originHint, outputReference)`: every part length-prefixed
  (`<len>:<value>;`, `-;` for an absent coordinate), so no two different fetches share a key and each
  gateway checkpoint has its own entry. The cache is per instance, so the API key is not part of it.
- `TryGet(key, out value)`: false on a miss or an expired entry (removed). On a hit, increments the
  use count; a non-`null` spec reaching `MaxUses` is removed and reported as a miss (refetch); a
  cached failure is never refreshed by use.
- `Set(key, value, ttl)`: replaces the entry. Over `MaxEntries`, expired entries are purged, then the
  least recently used are evicted until the cap holds.
- `Clear()` empties the cache (`Destroy`).

## Non-functional requirements

One lock guards every operation.
