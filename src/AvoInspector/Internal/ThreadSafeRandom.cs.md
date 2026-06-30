## Short description

`ThreadSafeRandom` is a thread-safe source of uniform random doubles in `[0.0, 1.0)` used for per-event sampling decisions (SPEC.md §7.7).

## Tech stack

Static C# class in namespace `Avo.Inspector.Internal`, built on `System.Random` with a `[ThreadStatic]` per-thread instance.

## Functional requirements

Public surface is one method:

- `public static double NextDouble()` — returns a uniform double in `[0.0, 1.0)`.

**Each thread gets its own `Random` instance** (lazily created on first use), so concurrent `track` calls never share or contend on a single generator. Each per-thread instance is seeded from an `Interlocked`-incremented counter (initialized from `Environment.TickCount`), giving distinct, uncorrelated streams across threads.

## Non-functional requirements

Lock-free and portable across all target frameworks. Not a cryptographic RNG — suitable only for sampling.
