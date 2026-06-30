## Short description

`Logger` is the SDK's minimal diagnostic logger. **All output goes to `stderr`** — `stdout` is reserved for the conformance harness's single result envelope.

## Tech stack

Static C# class in namespace `Avo.Inspector.Internal`, writing via `Console.Error`.

## Functional requirements

Every line is prefixed with `"[Avo Inspector] "`. Public surface:

- `public static void Warn(string message)` — writes unconditionally to stderr. Used for configuration warnings that must always surface.
- `public static void Error(bool shouldLog, string message)` — writes to stderr **only when `shouldLog` is true**; otherwise a no-op. Used for runtime/send errors gated by the caller's dev/staging logging policy.

## Non-functional requirements

**IMPORTANT: this logger receives only pre-redacted category strings and counts.** Callers MUST NOT pass the `apiKey` or full request bodies (SPEC.md §7.5.1); the logger performs no redaction of its own.
