using System;

namespace Avo.Inspector.Internal
{
    /// <summary>
    /// Minimal diagnostic logger. All output goes to <c>stderr</c> — <c>stdout</c> is reserved (the
    /// conformance harness writes its single result envelope there). Callers MUST NOT pass the
    /// <c>apiKey</c> or full request bodies (SPEC.md §7.5.1); this logger only ever receives
    /// pre-redacted category strings and counts.
    /// </summary>
    internal static class Logger
    {
        private const string Prefix = "[Avo Inspector] ";

        public static void Warn(string message)
            => Console.Error.WriteLine(Prefix + message);

        public static void Error(bool shouldLog, string message)
        {
            if (shouldLog)
            {
                Console.Error.WriteLine(Prefix + message);
            }
        }
    }
}
