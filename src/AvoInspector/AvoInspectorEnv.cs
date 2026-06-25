namespace Avo.Inspector
{
    /// <summary>
    /// Inspector environment (SPEC.md §6). The wire string values (<c>"dev"</c>, <c>"staging"</c>,
    /// <c>"prod"</c>) are part of the wire protocol — the Inspector backend depends on them.
    /// </summary>
    public enum AvoInspectorEnv
    {
        /// <summary>Development. Logging is enabled by default; batching is forced to immediate send.</summary>
        Dev,

        /// <summary>Staging. Logging disabled by default.</summary>
        Staging,

        /// <summary>Production. Logging disabled by default.</summary>
        Prod
    }

    /// <summary>Helpers for mapping <see cref="AvoInspectorEnv"/> to/from its wire string (SPEC.md §6.1).</summary>
    public static class AvoInspectorEnvExtensions
    {
        /// <summary>Returns the exact wire string for an environment (SPEC.md §6.1).</summary>
        public static string ToWireString(this AvoInspectorEnv env)
        {
            switch (env)
            {
                case AvoInspectorEnv.Staging: return "staging";
                case AvoInspectorEnv.Prod: return "prod";
                case AvoInspectorEnv.Dev:
                default: return "dev";
            }
        }

        /// <summary>
        /// Parses an environment wire string. Returns <c>true</c> and the parsed value for the exact
        /// strings <c>"dev"</c>/<c>"staging"</c>/<c>"prod"</c>; otherwise returns <c>false</c>
        /// (the caller applies the SPEC.md §6.3 fallback to <see cref="AvoInspectorEnv.Dev"/>).
        /// </summary>
        public static bool TryParse(string? value, out AvoInspectorEnv env)
        {
            switch (value)
            {
                case "dev":
                    env = AvoInspectorEnv.Dev;
                    return true;
                case "staging":
                    env = AvoInspectorEnv.Staging;
                    return true;
                case "prod":
                    env = AvoInspectorEnv.Prod;
                    return true;
                default:
                    env = AvoInspectorEnv.Dev;
                    return false;
            }
        }
    }
}
