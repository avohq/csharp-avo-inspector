## Short description

Inspector environment enum and its wire-string conversion helpers (SPEC.md §6). Defines the three
deployment environments and the canonical mapping to/from the protocol strings the Inspector backend
depends on.

## Data

```csharp
public enum AvoInspectorEnv { Dev, Staging, Prod }
```

The wire strings are **part of the wire protocol** and are exactly:

- `Dev` ↔ `"dev"` — logging enabled by default; batching forced to immediate send.
- `Staging` ↔ `"staging"` — logging disabled by default.
- `Prod` ↔ `"prod"` — logging disabled by default.

## Functional requirements

Public static helper class `AvoInspectorEnvExtensions`:

```csharp
public static string ToWireString(this AvoInspectorEnv env)
public static bool TryParse(string? value, out AvoInspectorEnv env)
```

- `ToWireString` returns the exact wire string above. **IMPORTANT:** it throws
  `ArgumentOutOfRangeException` on an out-of-range enum value (e.g. an invalid cast) rather than
  coercing to `"dev"`. String-based fallback is the caller's job, not this method's.
- `TryParse` returns `true` with the parsed value only for the exact strings `"dev"`/`"staging"`/
  `"prod"` (case-sensitive). For anything else (including `null`) it returns `false` and sets the
  `out` value to `AvoInspectorEnv.Dev`; the caller applies the §6.3 fallback.
