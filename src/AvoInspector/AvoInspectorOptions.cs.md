---
import:
src/AvoInspector/AvoInspectorEnv.cs.md
---

## Short description

Strongly-typed construction options for `AvoInspector` (SPEC.md §5). All batch configuration is
fixed at construction time. Plain mutable POCO with public get/set properties.

## Data

```csharp
public sealed class AvoInspectorOptions
{
    public string ApiKey { get; set; }            // REQUIRED, non-empty/non-whitespace; wire: apiKey
    public AvoInspectorEnv Env { get; set; }       // REQUIRED, default Dev; wire: env
    public string Version { get; set; }            // REQUIRED, non-empty/non-whitespace; wire: appVersion
    public string? AppName { get; set; }           // OPTIONAL, default ""; wire: appName
    public int? BatchSize { get; set; }            // OPTIONAL, default 30; MUST be >= 1
    public double? BatchFlushSeconds { get; set; } // OPTIONAL, default 30; MUST be > 0
    public int? MaxQueueSize { get; set; }         // OPTIONAL, default 1000
    public bool? DisableBatchTimer { get; set; }   // OPTIONAL, default false
    public string? PublicEncryptionKey { get; set; } // OPTIONAL; hex P-256 public key; wire: publicEncryptionKey
}
```

## Functional requirements

- `ApiKey` and `Version` MUST be non-empty/non-whitespace; otherwise the `AvoInspector` constructor
  throws (validation lives in the consumer, not here).
- `BatchSize` is forced to `1` when `Env == Dev`. Out-of-range numeric options fall back to their
  defaults with a warning (enforced by `AvoInspector`, SPEC.md §12.2).
- Serverless deployments SHOULD set `DisableBatchTimer = true` (SPEC.md §11.2).
- `PublicEncryptionKey` (AVO-4069, an extension beyond the spec): in `Dev`/`Staging`, property
  values are sent encrypted with it as `encryptedPropertyValue`; never used for encryption in
  `Prod`; needs .NET 8 or later (the net8.0 build). A `0x` prefix is accepted. Only on the options
  constructor.
- Default property values: `ApiKey`/`Version` default to `""`, `Env` defaults to `Dev`; nullable
  options default to `null` (meaning "use the SDK default").
