## Short description

Exception thrown by `AvoInspector.TrackSchemaFromEvent` on a synchronous internal error that occurs
before an event is enqueued (SPEC.md §4.2.5, §7.5).

## Functional requirements

```csharp
public sealed class AvoInspectorTrackException : Exception
{
    public AvoInspectorTrackException();
}
```

- The parameterless constructor sets `Message` to the **exact** spec-mandated rejection string:
  `"Avo Inspector: something went wrong. Please report to support@avo.app."`
- The original underlying error is never exposed through this type (no inner exception); only the
  fixed message crosses the public boundary.
