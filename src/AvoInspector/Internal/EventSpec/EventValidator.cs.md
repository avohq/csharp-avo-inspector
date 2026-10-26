---
import:
src/AvoInspector/Internal/EventSpec/EventSpecTypes.cs.md
src/AvoInspector/Internal/RawValues.cs.md
src/AvoInspector/Internal/Logger.cs.md
---

## Short description

Checks property values against an event spec's value constraints (AVO-4069): pinned values, allowed
values, regex patterns and min/max ranges, per tracking-plan event ID, on a property and one level
into objects and lists of objects. A port of the Node SDK 2.0.0 `EventValidator` (itself following
the web and Android SDKs).

## Functional requirements

1. `Validate(spec, eventSchema, eventProperties, shouldLog)` → name → outcome for each schema
   property that failed for at least one event ID (or whose children did). Unconstrained properties
   are left out. A property absent from the map is "missing" (compared as `"undefined"`).
2. Constraints of the same property name across events are merged into a new object (the cached spec
   is never written): ID maps are unioned per key, `Required` is AND, `IsList` is OR, children merge
   recursively.
3. Per property (depth 0, children at depth 1, nothing deeper):
   - `IsList`: a value that is not a list is not checked; a list of more than 1,000 items is not
     checked (logged). With `Children`, each child is checked across every item (object items only;
     anything else has no children) and an ID fails the child when any item fails it. Otherwise each
     item is checked as a value.
   - `Children` (object): each child is checked against the object's value for that key, also when
     the object is `null` or not a map (a required, constrained child then fails).
   - Otherwise an optional `null`/missing value is not checked; anything else is.
4. Value checks:
   - Pinned / allowed: compare `RawValues.ComparableString(value)` (JavaScript `String()`; JSON for
     maps and lists). Allowed-value keys that are not a JSON array of strings are skipped.
   - Regex: only values `RawValues.TryGetString` accepts can match; any other value fails every
     pattern. A string over 10,000 characters is not checked (logged). Patterns compile with
     `RegexOptions.ECMAScript` (JavaScript's ASCII `\d`/`\w`), falling back to
     `RegexOptions.CultureInvariant` when that option rejects the pattern; an invalid pattern is
     skipped. Each match has a 100 ms timeout; a timeout skips the pattern.
   - Min/max: only `RawValues.TryGetNumber` values can be in range; `NaN` never is. A range that is
     not exactly two comma-separated parts parsing as invariant doubles (empty = open) is skipped.
5. Compiled patterns and parsed allowed-value lists are cached process-wide by wire string (null for
   skipped ones), cleared when 1,000 entries are reached.

## Non-functional requirements

Types are not checked (Avo compares the schema with the plan). Logs carry spec strings and counts,
never property values.
