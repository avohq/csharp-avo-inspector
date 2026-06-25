using System;
using System.Threading;

namespace Avo.Inspector.Internal
{
    /// <summary>
    /// Thread-safe source of uniform doubles in <c>[0.0, 1.0)</c> for per-event sampling
    /// (SPEC.md §7.7). Uses a per-thread <see cref="Random"/> so concurrent <c>track</c> calls never
    /// share or contend on a single instance. Portable across all target frameworks.
    /// </summary>
    internal static class ThreadSafeRandom
    {
        private static int _seedCounter = Environment.TickCount;

        [ThreadStatic] private static Random? _local;

        private static Random Local
        {
            get
            {
                var rng = _local;
                if (rng == null)
                {
                    // Distinct seed per thread to avoid correlated streams.
                    var seed = Interlocked.Increment(ref _seedCounter);
                    rng = new Random(seed);
                    _local = rng;
                }
                return rng;
            }
        }

        /// <summary>Returns a uniform double in <c>[0.0, 1.0)</c>.</summary>
        public static double NextDouble() => Local.NextDouble();
    }
}
