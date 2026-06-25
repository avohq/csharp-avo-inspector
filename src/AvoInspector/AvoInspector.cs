using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avo.Inspector.Internal;

namespace Avo.Inspector
{
    /// <summary>
    /// Avo Inspector server-side SDK (SPEC.md §4). Extracts a type schema from arbitrary event
    /// property maps and reports it to the Inspector HTTP API, transparently handling sampling
    /// (§7.7), batching (§12), gzip (§7.3.5), and graceful error recovery (§7.5).
    /// </summary>
    /// <remarks>
    /// <para><b>Thread safety.</b> Safe for concurrent use. The pending batch buffer and sampling
    /// rate are lock-guarded; the HTTP send is performed outside the lock (SPEC.md §3.1).</para>
    /// <para><b>Shutdown contract.</b> Buffered and in-flight events are <i>at-most-once</i> and are
    /// lost on process exit. Callers MUST <see cref="Flush"/> (or <c>await</c> the
    /// <see cref="TrackSchemaFromEvent"/> task) before the process or serverless handler exits if
    /// events may be in-flight or buffered (SPEC.md §3.4, §4.6, §11).</para>
    /// </remarks>
    public sealed class AvoInspector
    {
        private const string ProductionEndpoint = "https://api.avo.app/inspector/v1/track";
        private const string MockEndpointEnvVar = "AVO_INSPECTOR_MOCK_ENDPOINT";
        private const int DefaultFlushTimeoutMs = 10_000; // SPEC.md §4.6

        private const string NoApiKeyMessage =
            "[Avo Inspector] No API key provided. Inspector can't operate without API key.";
        private const string NoVersionMessage =
            "[Avo Inspector] No version provided. Many features of Inspector rely on versioning. " +
            "Please provide comparable string version, i.e. integer or semantic.";

        // Process-wide logging flag (SPEC.md §4.4). Volatile so a write on one instance is visible
        // to reads on all instances across threads.
        private static volatile bool _shouldLog;

        private readonly object _lock = new object();

        // Constructor options — fixed at construction (SPEC.md §5). Treated as immutable after init.
        private string _apiKey = string.Empty;
        private string _appName = string.Empty;
        private string _appVersion = string.Empty;
        private AvoInspectorEnv _env;
        private string _envWire = "dev";
        private int _batchSize;
        private double _batchFlushSeconds;
        private int _maxQueueSize;
        private bool _disableBatchTimer;

        // Mutable state guarded by _lock.
        private double _samplingRate = 1.0;
        private List<WireEvent> _pendingBatch = new List<WireEvent>();
        private readonly HashSet<Task> _pendingSends = new HashSet<Task>();
        private bool _destroyed;
        private Timer? _flushTimer;

        /// <summary>
        /// Constructs an instance from strongly-typed options (SPEC.md §4.1, §5). Throws
        /// synchronously on a missing/whitespace <see cref="AvoInspectorOptions.ApiKey"/> or
        /// <see cref="AvoInspectorOptions.Version"/>.
        /// </summary>
        public AvoInspector(AvoInspectorOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            Initialize(options.ApiKey, options.Env.ToWireString(), options.Version, options.AppName,
                options.BatchSize, options.BatchFlushSeconds, options.MaxQueueSize, options.DisableBatchTimer);
        }

        /// <summary>
        /// Constructs an instance from a raw environment string (SPEC.md §4.1, §6.3). An absent,
        /// empty, or unrecognized <paramref name="env"/> falls back to <c>"dev"</c> with a warning
        /// (never throws on env). Throws synchronously on a missing/whitespace
        /// <paramref name="apiKey"/> or <paramref name="version"/>.
        /// </summary>
        /// <param name="apiKey">Inspector API key (REQUIRED, non-empty).</param>
        /// <param name="env">Environment wire string: <c>"dev"</c>, <c>"staging"</c>, or <c>"prod"</c>.</param>
        /// <param name="version">Application version (REQUIRED, non-empty).</param>
        /// <param name="appName">Application name (default <c>""</c>).</param>
        /// <param name="batchSize">Batch flush size (default 30; forced to 1 in dev).</param>
        /// <param name="batchFlushSeconds">Time/idle flush threshold in seconds (default 30).</param>
        /// <param name="maxQueueSize">Max buffered events before FIFO drop (default 1000).</param>
        /// <param name="disableBatchTimer">Disable the scheduled flush timer (default false).</param>
        public AvoInspector(
            string apiKey,
            string env,
            string version,
            string? appName = null,
            int? batchSize = null,
            double? batchFlushSeconds = null,
            int? maxQueueSize = null,
            bool? disableBatchTimer = null)
        {
            Initialize(apiKey, env, version, appName, batchSize, batchFlushSeconds, maxQueueSize, disableBatchTimer);
        }

        private void Initialize(
            string? apiKey,
            string? rawEnv,
            string? version,
            string? appName,
            int? batchSize,
            double? batchFlushSeconds,
            int? maxQueueSize,
            bool? disableBatchTimer)
        {
            // SPEC.md §4.1 — validate apiKey, then version (exact messages). Whitespace-only is empty.
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException(NoApiKeyMessage);
            }
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new ArgumentException(NoVersionMessage);
            }

            // SPEC.md §4.1 / §6.3 — env fallback to "dev" (never throws).
            if (!AvoInspectorEnvExtensions.TryParse(rawEnv, out var env))
            {
                Logger.Warn("Invalid env \"" + (rawEnv ?? "<null>") + "\", falling back to \"dev\".");
                env = AvoInspectorEnv.Dev;
            }

            _apiKey = apiKey!;
            _appVersion = version!;
            _appName = appName ?? string.Empty;
            _env = env;
            _envWire = env.ToWireString();

            // SPEC.md §4.1 — logging default tied to env (process-wide flag, §4.4).
            _shouldLog = env == AvoInspectorEnv.Dev;

            _samplingRate = 1.0;

            // SPEC.md §12.2 batch configuration.
            var resolvedBatchSize = 30;
            if (batchSize.HasValue)
            {
                if (batchSize.Value >= 1)
                {
                    resolvedBatchSize = batchSize.Value;
                }
                else
                {
                    Logger.Warn("Invalid batchSize " + batchSize.Value + "; using default 30.");
                }
            }
            // SPEC.md §12.2 — dev forces batchSize = 1 (immediate send), overriding any value.
            _batchSize = env == AvoInspectorEnv.Dev ? 1 : resolvedBatchSize;

            _batchFlushSeconds = 30;
            if (batchFlushSeconds.HasValue)
            {
                if (batchFlushSeconds.Value > 0)
                {
                    _batchFlushSeconds = batchFlushSeconds.Value;
                }
                else
                {
                    Logger.Warn("Invalid batchFlushSeconds " + batchFlushSeconds.Value + "; using default 30.");
                }
            }

            _maxQueueSize = 1000;
            if (maxQueueSize.HasValue)
            {
                if (maxQueueSize.Value >= 1)
                {
                    _maxQueueSize = maxQueueSize.Value;
                }
                else
                {
                    Logger.Warn("Invalid maxQueueSize " + maxQueueSize.Value + "; using default 1000.");
                }
            }

            _disableBatchTimer = disableBatchTimer == true;

            // SPEC.md §12.3 / §11.4 — best-effort scheduled flush. A managed Timer fires on
            // background thread-pool threads and never holds the process open. Disabled in dev
            // (batchSize 1) and when disableBatchTimer is set.
            if (!_disableBatchTimer && _batchSize > 1)
            {
                var periodMs = (long)(_batchFlushSeconds * 1000.0);
                if (periodMs < 1)
                {
                    periodMs = 1;
                }
                _flushTimer = new Timer(OnScheduledFlush, null, periodMs, periodMs);
            }
        }

        /// <summary>
        /// Enables or disables diagnostic logging process-wide (SPEC.md §4.4). The flag is shared by
        /// every instance in the process. MUST NOT be enabled in production contexts.
        /// </summary>
        public void EnableLogging(bool enable)
        {
            _shouldLog = enable;
        }

        /// <summary>
        /// Synchronously extracts the schema from an event-property map (SPEC.md §4.3). Never throws:
        /// returns an empty list for a <c>null</c> map or on any internal parser error.
        /// </summary>
        public IReadOnlyList<SchemaEntry> ExtractSchema(IDictionary<string, object?>? eventProperties)
        {
            try
            {
                return AvoSchemaParser.ExtractSchema(eventProperties);
            }
            catch (Exception ex)
            {
                Logger.Error(_shouldLog, "extractSchema error: " + ex.GetType().Name);
                return new List<SchemaEntry>();
            }
        }

        /// <summary>
        /// Extracts the event schema, applies per-event sampling, enqueues the event into the pending
        /// batch, and dispatches a send when a flush trigger fires (SPEC.md §4.2).
        /// </summary>
        /// <remarks>
        /// Resolves with the extracted schema at enqueue time. When <c>batchSize == 1</c> (always
        /// true in <c>dev</c>) the send is synchronous to the call, so a non-200 resolves with an
        /// empty list (SPEC.md §7.5); when <c>batchSize &gt; 1</c> the resolved value never reflects
        /// the batch's eventual HTTP outcome (SPEC.md §7.5.2). After <see cref="Destroy"/> this is a
        /// no-op that resolves with an empty list. On a synchronous internal error before enqueue it
        /// faults with <see cref="AvoInspectorTrackException"/> (SPEC.md §4.2.5).
        /// </remarks>
        /// <param name="eventName">The tracked event name.</param>
        /// <param name="eventProperties">The event properties to extract a schema from.</param>
        /// <param name="streamId">Optional stream id; passed through verbatim (SPEC.md §4.2, §8.2).</param>
        public async Task<IReadOnlyList<SchemaEntry>> TrackSchemaFromEvent(
            string eventName,
            IDictionary<string, object?>? eventProperties,
            string? streamId = null)
        {
            // SPEC.md §4.5 — after destroy(), resolve([]) with no enqueue and no HTTP call.
            lock (_lock)
            {
                if (_destroyed)
                {
                    return new List<SchemaEntry>();
                }
            }

            try
            {
                var eventSchema = ExtractSchema(eventProperties);
                var resolvedStreamId = ResolveStreamId(streamId);

                double samplingSnapshot;
                lock (_lock)
                {
                    samplingSnapshot = _samplingRate;
                }

                // SPEC.md §7.7 — per-event sampling at enqueue, BEFORE buffering. Drop when
                // random >= samplingRate so that samplingRate 0.0 ALWAYS drops.
                if (ThreadSafeRandom.NextDouble() >= samplingSnapshot)
                {
                    return eventSchema;
                }

                var body = BuildWireEvent(eventName, resolvedStreamId, samplingSnapshot, eventSchema);

                List<WireEvent>? batchToSend = null;
                var dropped = 0;
                lock (_lock)
                {
                    if (_destroyed)
                    {
                        return eventSchema; // destroyed mid-flight: do not enqueue or send.
                    }
                    _pendingBatch.Add(body);
                    // SPEC.md §12.5 — FIFO-oldest drop on overflow.
                    while (_pendingBatch.Count > _maxQueueSize)
                    {
                        _pendingBatch.RemoveAt(0);
                        dropped++;
                    }
                    // SPEC.md §12.3/§12.4 — size trigger: atomic swap-and-clear under the lock.
                    if (_pendingBatch.Count >= _batchSize)
                    {
                        batchToSend = _pendingBatch;
                        _pendingBatch = new List<WireEvent>();
                    }
                }

                if (dropped > 0)
                {
                    // SPEC.md §12.5 / §7.5.1 — log a count only, never event contents.
                    Logger.Error(_shouldLog, "maxQueueSize exceeded; dropped " + dropped + " oldest event(s).");
                }

                Task<SendResult>? triggeredSend = null;
                if (batchToSend != null)
                {
                    triggeredSend = DispatchSend(batchToSend);
                }

                // SPEC.md §4.2.4 / §7.5 — immediate-send mode: the per-call HTTP outcome is
                // observable. A non-200 resolves []; network error/timeout resolves the schema.
                if (_batchSize == 1 && triggeredSend != null)
                {
                    var result = await triggeredSend.ConfigureAwait(false);
                    return result.Status == SendStatus.Non200
                        ? (IReadOnlyList<SchemaEntry>)new List<SchemaEntry>()
                        : eventSchema;
                }

                return eventSchema;
            }
            catch (Exception ex)
            {
                // SPEC.md §4.2.5 / §7.5 — synchronous internal error before enqueue. Reject with the
                // exact spec string (never the original error object).
                Logger.Error(_shouldLog, "internal error: " + ex.GetType().Name);
                throw new AvoInspectorTrackException();
            }
        }

        /// <summary>
        /// Force-flushes the pending batch, then waits (up to <paramref name="timeoutMs"/>) for all
        /// in-flight sends to complete or be abandoned (SPEC.md §4.6, §12.6). Always resolves — a
        /// completion guarantee, not a delivery guarantee. A no-op after <see cref="Destroy"/>. The
        /// instance remains usable afterward.
        /// </summary>
        /// <param name="timeoutMs">Maximum time to wait for in-flight sends (default 10,000 ms).</param>
        public async Task Flush(int timeoutMs = DefaultFlushTimeoutMs)
        {
            List<WireEvent>? batch = null;
            lock (_lock)
            {
                if (_destroyed)
                {
                    return;
                }
                if (_pendingBatch.Count > 0)
                {
                    batch = _pendingBatch;
                    _pendingBatch = new List<WireEvent>();
                }
            }

            if (batch != null)
            {
                _ = DispatchSend(batch); // tracked via _pendingSends; awaited below.
            }

            Task[] sends;
            lock (_lock)
            {
                sends = _pendingSends.ToArray();
            }
            if (sends.Length == 0)
            {
                return;
            }

            try
            {
                var all = Task.WhenAll(sends);
                await Task.WhenAny(all, Task.Delay(timeoutMs)).ConfigureAwait(false);
            }
            catch
            {
                // SPEC.md §4.6 — flush() MUST resolve in all cases, even if a send faulted.
            }
        }

        /// <summary>
        /// Cancels and cleans up (SPEC.md §4.5, §11.3): discards the pending batch unsent, abandons
        /// in-flight sends, resets the pending count to zero, and clears the scheduled-flush timer.
        /// Does NOT flush. Constructor options, the sampling rate, and the process-wide logging flag
        /// persist. After <see cref="Destroy"/>, <see cref="TrackSchemaFromEvent"/> is a no-op.
        /// </summary>
        public void Destroy()
        {
            Timer? timer;
            lock (_lock)
            {
                _destroyed = true;
                _pendingBatch = new List<WireEvent>();
                _pendingSends.Clear(); // abandon in-flight (pendingCount -> 0)
                timer = _flushTimer;
                _flushTimer = null;
            }
            timer?.Dispose();
            // samplingRate, apiKey, env, version, appName, and process-wide shouldLog persist.
        }

        // ----- internals ------------------------------------------------------------------------

        private string ResolveStreamId(string? streamId)
        {
            if (streamId != null && streamId.Length > 0)
            {
                if (streamId.IndexOf(':') >= 0)
                {
                    // SPEC.md §4.2 — warn but use the value verbatim. Do not log the value itself.
                    Logger.Warn("streamId contains ':'; using the value verbatim.");
                }
                return streamId;
            }
            // SPEC.md §4.2 / §8.2 — absent or empty streamId becomes "".
            return string.Empty;
        }

        private WireEvent BuildWireEvent(
            string eventName,
            string streamId,
            double samplingSnapshot,
            IReadOnlyList<SchemaEntry> eventSchema)
        {
            return new WireEvent
            {
                ApiKey = _apiKey,
                AppName = _appName,
                AppVersion = _appVersion,
                LibVersion = InspectorVersion.LibVersion,
                Env = _envWire,
                LibPlatform = InspectorVersion.LibPlatform,
                MessageId = Guid.NewGuid().ToString("D"), // UUID v4, lowercase hex (SPEC.md §8.1)
                StreamId = streamId,
                CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                SamplingRate = samplingSnapshot,
                Type = "event",
                EventName = eventName ?? string.Empty,
                EventProperties = eventSchema
            };
        }

        private void OnScheduledFlush(object? state)
        {
            List<WireEvent>? batch = null;
            lock (_lock)
            {
                if (_destroyed)
                {
                    return;
                }
                if (_pendingBatch.Count > 0)
                {
                    batch = _pendingBatch;
                    _pendingBatch = new List<WireEvent>();
                }
            }
            if (batch != null)
            {
                _ = DispatchSend(batch); // best-effort, fire-and-forget.
            }
        }

        /// <summary>
        /// Sends a batch outside the lock and tracks the in-flight task so <see cref="Flush"/> can
        /// await it (SPEC.md §3.1, §4.2.6, §12.4).
        /// </summary>
        private Task<SendResult> DispatchSend(List<WireEvent> batch)
        {
            var task = SendBatchAsync(batch.ToArray());
            lock (_lock)
            {
                if (!_destroyed)
                {
                    _pendingSends.Add(task);
                }
            }
            task.ContinueWith(
                completed =>
                {
                    lock (_lock)
                    {
                        _pendingSends.Remove(completed);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }

        private async Task<SendResult> SendBatchAsync(WireEvent[] batch)
        {
            var endpoint = ResolveEndpoint();
            var result = await InspectorHttpSender.SendAsync(endpoint, batch, _shouldLog).ConfigureAwait(false);
            if (result.Status == SendStatus.Ok && result.NewSamplingRate.HasValue)
            {
                // SPEC.md §7.4/§7.7 — update samplingRate only on 200, under the lock.
                lock (_lock)
                {
                    _samplingRate = result.NewSamplingRate.Value;
                }
            }
            return result;
        }

        /// <summary>
        /// Resolves the send endpoint with a fail-closed mock gate (SPEC.md §7.1): a <c>prod</c>
        /// instance NEVER honors <c>AVO_INSPECTOR_MOCK_ENDPOINT</c>, regardless of the environment.
        /// </summary>
        private string ResolveEndpoint()
        {
            if (_env != AvoInspectorEnv.Prod)
            {
                var overrideUrl = Environment.GetEnvironmentVariable(MockEndpointEnvVar);
                if (!string.IsNullOrEmpty(overrideUrl))
                {
                    return overrideUrl!; // used as-is, no path appending (runner-contract).
                }
            }
            return ProductionEndpoint;
        }

        // ----- test-only hooks (not part of the documented public API; SPEC runner-contract) -----

        /// <summary>
        /// Test-only hook to override the sampling rate (runner-contract <c>precondition.samplingRate</c>).
        /// Intentionally <c>internal</c> — exposing a public setter would let callers force
        /// <c>samplingRate = 0</c> and silently disable telemetry.
        /// </summary>
        internal void SetSamplingRateForTesting(double rate)
        {
            lock (_lock)
            {
                _samplingRate = rate;
            }
        }

        internal double CurrentSamplingRate
        {
            get { lock (_lock) { return _samplingRate; } }
        }

        internal bool IsDestroyed
        {
            get { lock (_lock) { return _destroyed; } }
        }

        internal int EffectiveBatchSize => _batchSize;

        internal int PendingBatchCount
        {
            get { lock (_lock) { return _pendingBatch.Count; } }
        }

        internal string ResolvedEndpointForTesting() => ResolveEndpoint();

        internal static bool ShouldLogForTesting => _shouldLog;
    }
}
