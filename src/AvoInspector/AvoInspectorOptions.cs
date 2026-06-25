namespace Avo.Inspector
{
    /// <summary>
    /// Strongly-typed construction options for <see cref="AvoInspector"/> (SPEC.md §5).
    /// Batch configuration is fixed at construction time.
    /// </summary>
    public sealed class AvoInspectorOptions
    {
        /// <summary>
        /// Inspector API key (REQUIRED). MUST be non-empty and non-whitespace, else the constructor
        /// throws (SPEC.md §4.1). Sent on the wire as <c>apiKey</c>.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Environment (REQUIRED). Controls logging defaults; sent on the wire as <c>env</c>.</summary>
        public AvoInspectorEnv Env { get; set; } = AvoInspectorEnv.Dev;

        /// <summary>
        /// Application version (REQUIRED). MUST be non-empty and non-whitespace, else the
        /// constructor throws (SPEC.md §4.1). Sent on the wire as <c>appVersion</c>.
        /// </summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>Application name (OPTIONAL, default <c>""</c>). Sent on the wire as <c>appName</c>.</summary>
        public string? AppName { get; set; }

        /// <summary>
        /// Flush the pending batch when its length reaches this value (OPTIONAL, default <c>30</c>).
        /// Forced to <c>1</c> when <see cref="Env"/> is <see cref="AvoInspectorEnv.Dev"/>. MUST be
        /// ≥ 1; smaller values fall back to the default with a warning (SPEC.md §12.2).
        /// </summary>
        public int? BatchSize { get; set; }

        /// <summary>
        /// Maximum age (seconds) of the oldest buffered event before a time/idle flush should occur
        /// (OPTIONAL, default <c>30</c>). MUST be &gt; 0 (SPEC.md §12.2).
        /// </summary>
        public double? BatchFlushSeconds { get; set; }

        /// <summary>
        /// Hard cap on buffered events; oldest dropped first on overflow (OPTIONAL, default
        /// <c>1000</c>) (SPEC.md §12.5).
        /// </summary>
        public int? MaxQueueSize { get; set; }

        /// <summary>
        /// When <c>true</c>, no background/scheduled flush timer is started (OPTIONAL, default
        /// <c>false</c>). Serverless deployments SHOULD set this <c>true</c> (SPEC.md §12.2, §11.2).
        /// </summary>
        public bool? DisableBatchTimer { get; set; }
    }
}
