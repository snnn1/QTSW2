// SINGLE SOURCE OF TRUTH
// This file is the authoritative implementation of RobotLoggingService.
// It is compiled into Robot.Core.dll and should be referenced from that DLL.
// Do not duplicate this file elsewhere - if source is needed, reference Robot.Core.dll instead.
//
// Linked into: Robot.Core.csproj (modules/robot/core/)
// Referenced by: RobotCore_For_NinjaTrader (via Robot.Core.dll)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QTSW2.Robot.Core;

/// <summary>
/// Centralized async single-writer logging service.
/// All strategy instances publish log events to an in-memory queue.
/// A background worker flushes to disk in order.
/// OnBarUpdate only enqueues (non-blocking), never touches disk.
/// 
/// CRITICAL: This service is a singleton per project root to prevent file lock contention.
/// Multiple engines (one per instrument) share the same service instance.
/// </summary>
public sealed class RobotLoggingService : IDisposable
{
    // Singleton pattern: one service per project root
    private static readonly Dictionary<string, RobotLoggingService> _instances = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _instancesLock = new();
    private static int _instanceCounter = 0;

    private readonly ConcurrentQueue<RobotLogEvent> _queue = new();
    private readonly string _logDirectory;
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writersLock = new();
    private readonly Dictionary<string, StreamWriter> _healthWriters = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _healthWritersLock = new();
    private readonly string _healthDirectory;
    private CancellationTokenSource _cancellationTokenSource = new();
    private Task? _backgroundWorker;
    private bool _disposed = false;
    private int _referenceCount = 0; // Track how many engines are using this instance
    private readonly object _referenceLock = new();

    // Configuration constants (now configurable via LoggingConfig)
    private readonly int FLUSH_INTERVAL_MS;
    private readonly int MAX_QUEUE_SIZE;
    private readonly int MAX_BATCH_PER_FLUSH;
    private const int ERROR_LOG_INTERVAL_SECONDS = 10;

    // Log rotation and filtering configuration
    private readonly LoggingConfig _config;
    private readonly int _maxLogFileSizeBytes;
    private readonly string _minLogLevel;

    private DateTime _lastErrorLogTime = DateTime.MinValue;
    private long _droppedDebugCount = 0; // Changed to long for Interlocked operations
    private long _droppedInfoCount = 0;  // Changed to long for Interlocked operations
    private int _writeFailureCount = 0;
    private DateTime _lastBackpressureEventUtc = DateTime.MinValue;
    private DateTime _lastWorkerErrorEventUtc = DateTime.MinValue;
    private const int BACKPRESSURE_EVENT_RATE_LIMIT_SECONDS = 60; // Emit backpressure event max once per minute
    private const int WORKER_ERROR_EVENT_RATE_LIMIT_SECONDS = 60; // Emit worker error event max once per minute

    // Human-friendly daily summary (written by background worker)
    private DateTime _lastDailySummaryWriteUtc = DateTime.MinValue;
    private const int DAILY_SUMMARY_WRITE_INTERVAL_SECONDS = 60;
    private readonly DailySummaryAggregator _dailySummary = new();
    
    // Daily rotation tracking
    private DateTime _lastRotationDateUtc = DateTime.MinValue;
    
    // Rate limiting: track last emission time and counts per event type
    private readonly Dictionary<string, DateTimeOffset> _lastEventEmission = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _eventCountsPerMinute = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastRateLimitResetUtc = DateTimeOffset.UtcNow;
    private readonly object _rateLimitLock = new();

    private sealed class DailySummaryAggregator
    {
        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        public long TotalEvents { get; private set; }
        public long Errors { get; private set; }
        public long Warnings { get; private set; }

        public long RangeEvents { get; private set; }
        public long RangeLocked { get; private set; }

        public long OrdersSubmitted { get; private set; }
        public long ExecutionsFilled { get; private set; }
        public long OrdersRejected { get; private set; }

        public long PushoverEvents { get; private set; }
        public long LoggingPipelineErrors { get; private set; } // LOG_* events

        public readonly Dictionary<string, long> OrderTypeCounts = new(StringComparer.OrdinalIgnoreCase);

        private RobotLogEvent? _latestError;
        private RobotLogEvent? _latestOrderSubmitted;
        private RobotLogEvent? _latestRangeLocked;
        private RobotLogEvent? _latestPushover;
        private RobotLogEvent? _latestLoggingPipelineError;

        public void Observe(RobotLogEvent evt)
        {
            TotalEvents++;

            if (string.Equals(evt.level, "ERROR", StringComparison.OrdinalIgnoreCase))
            {
                Errors++;
                _latestError = evt;
            }
            else if (string.Equals(evt.level, "WARN", StringComparison.OrdinalIgnoreCase))
            {
                Warnings++;
            }

            var name = evt.@event ?? "";

            if (name.StartsWith("LOG_", StringComparison.OrdinalIgnoreCase))
            {
                LoggingPipelineErrors++;
                _latestLoggingPipelineError = evt;
            }

            if (name.StartsWith("RANGE_", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "RANGE_LOCKED", StringComparison.OrdinalIgnoreCase))
            {
                RangeEvents++;
                if (string.Equals(name, "RANGE_LOCKED", StringComparison.OrdinalIgnoreCase))
                {
                    RangeLocked++;
                    _latestRangeLocked = evt;
                }
            }

            if (string.Equals(name, "ORDER_SUBMITTED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "ORDER_SUBMIT_SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                OrdersSubmitted++;
                _latestOrderSubmitted = evt;

                // Best-effort order_type breakdown
                if (evt.data != null && evt.data.TryGetValue("order_type", out var otObj))
                {
                    var ot = otObj?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(ot))
                    {
                        if (!OrderTypeCounts.TryGetValue(ot, out var cur)) cur = 0;
                        OrderTypeCounts[ot] = cur + 1;
                    }
                }
            }

            if (string.Equals(name, "EXECUTION_FILLED", StringComparison.OrdinalIgnoreCase))
            {
                ExecutionsFilled++;
            }

            if (string.Equals(name, "ORDER_REJECTED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "ORDER_SUBMIT_FAIL", StringComparison.OrdinalIgnoreCase))
            {
                OrdersRejected++;
            }

            if (name.StartsWith("PUSHOVER_", StringComparison.OrdinalIgnoreCase))
            {
                PushoverEvents++;
                _latestPushover = evt;
            }
        }

        private static string Compact(string? s, int maxLen = 160)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }

        private static string Fmt(RobotLogEvent? e)
        {
            if (e == null) return "N/A";
            return $"{e.ts_utc} | {e.instrument} | {e.@event} | {Compact(e.message)}";
        }

        public string RenderMarkdown(string logDir, long droppedDebug, long droppedInfo, int writeFailures)
        {
            var nowUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            var date = DateTime.UtcNow.ToString("yyyyMMdd");

            var sb = new StringBuilder();
            sb.AppendLine($"# Robot Daily Log Summary ({date})");
            sb.AppendLine();
            sb.AppendLine($"Generated: {nowUtc} UTC");
            sb.AppendLine();
            sb.AppendLine("## Location");
            sb.AppendLine($"- log_dir: `{logDir}`");
            sb.AppendLine();
            sb.AppendLine("## Health");
            sb.AppendLine($"- total_events: {TotalEvents}");
            sb.AppendLine($"- errors: {Errors}");
            sb.AppendLine($"- warnings: {Warnings}");
            sb.AppendLine($"- dropped_debug: {droppedDebug}");
            sb.AppendLine($"- dropped_info: {droppedInfo}");
            sb.AppendLine($"- write_failures: {writeFailures}");
            sb.AppendLine();
            sb.AppendLine("## Latest notable events");
            sb.AppendLine($"- latest_error: {Fmt(_latestError)}");
            sb.AppendLine($"- latest_logging_pipeline_error: {Fmt(_latestLoggingPipelineError)}");
            sb.AppendLine();
            sb.AppendLine("## Ranges");
            sb.AppendLine($"- range_events: {RangeEvents}");
            sb.AppendLine($"- range_locked: {RangeLocked}");
            sb.AppendLine($"- latest_range_locked: {Fmt(_latestRangeLocked)}");
            sb.AppendLine();
            sb.AppendLine("## Orders");
            sb.AppendLine($"- orders_submitted: {OrdersSubmitted}");
            sb.AppendLine($"- orders_rejected: {OrdersRejected}");
            sb.AppendLine($"- executions_filled: {ExecutionsFilled}");
            sb.AppendLine($"- latest_order_submitted: {Fmt(_latestOrderSubmitted)}");
            if (OrderTypeCounts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Order types");
                foreach (var kv in OrderTypeCounts.OrderByDescending(kv => kv.Value))
                {
                    sb.AppendLine($"- {kv.Key}: {kv.Value}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("## Notifications");
            sb.AppendLine($"- pushover_events: {PushoverEvents}");
            sb.AppendLine($"- latest_pushover: {Fmt(_latestPushover)}");

            sb.AppendLine();
            sb.AppendLine("## Notes");
            sb.AppendLine("- This file is maintained by the logging background worker (non-blocking).");
            sb.AppendLine("- For full details, grep the JSONL files in `logs/robot/`.");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Get or create the singleton instance for the given project root.
    /// Multiple engines share the same instance to prevent file lock contention.
    /// </summary>
    public static RobotLoggingService GetOrCreate(string projectRoot, string? customLogDir = null)
    {
        var key = customLogDir ?? Path.Combine(projectRoot, "logs", "robot");
        
        lock (_instancesLock)
        {
            if (!_instances.TryGetValue(key, out var instance) || instance._disposed)
            {
                instance = new RobotLoggingService(projectRoot, customLogDir);
                _instances[key] = instance;
            }
            
            lock (instance._referenceLock)
            {
                instance._referenceCount++;
            }
            
            return instance;
        }
    }

    /// <summary>
    /// Release a reference to this instance. When reference count reaches zero, the instance is disposed.
    /// </summary>
    public void Release()
    {
        lock (_referenceLock)
        {
            _referenceCount--;
            if (_referenceCount <= 0)
            {
                lock (_instancesLock)
                {
                    var key = _logDirectory;
                    if (_instances.TryGetValue(key, out var instance) && instance == this)
                    {
                        _instances.Remove(key);
                    }
                }
                Dispose();
            }
        }
    }

    private RobotLoggingService(string projectRoot, string? customLogDir = null)
    {
        _logDirectory = customLogDir ?? Path.Combine(projectRoot, "logs", "robot");
        Directory.CreateDirectory(_logDirectory);
        _healthDirectory = Path.Combine(projectRoot, "logs", "health");
        Directory.CreateDirectory(_healthDirectory);
        _instanceCounter++;
        
        // Load logging configuration
        _config = LoggingConfig.LoadFromFile(projectRoot);
        _maxLogFileSizeBytes = _config.max_file_size_mb * 1024 * 1024;
        _minLogLevel = _config.min_log_level ?? "INFO";
        
        // Use configurable values (with defaults)
        MAX_QUEUE_SIZE = _config.max_queue_size > 0 ? _config.max_queue_size : 50000;
        MAX_BATCH_PER_FLUSH = _config.max_batch_per_flush > 0 ? _config.max_batch_per_flush : 2000;
        FLUSH_INTERVAL_MS = _config.flush_interval_ms > 0 ? _config.flush_interval_ms : 500;
        
        // Archive old logs on startup
        ArchiveOldLogs();
    }
    
    // Selected INFO events that should go to health sink
    private static readonly HashSet<string> HEALTH_SINK_INFO_EVENTS = new(StringComparer.OrdinalIgnoreCase)
    {
        "TRADE_COMPLETED",
        "CRITICAL_EVENT_REPORTED"  // Critical events timeline for forensic analysis
        // Note: ENGINE_HEARTBEAT not included as it doesn't exist in current event registry
    };

    /// <summary>
    /// Non-blocking enqueue. Never throws from OnBarUpdate.
    /// </summary>
    public void Log(RobotLogEvent evt)
    {
        if (_disposed) return;

        // CRITICAL: ERROR and CRITICAL events always bypass all filtering
        bool isErrorOrCritical = string.Equals(evt.level, "ERROR", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(evt.level, "CRITICAL", StringComparison.OrdinalIgnoreCase);
        
        if (!isErrorOrCritical)
        {
            // Diagnostics filtering: drop DEBUG events if diagnostics disabled
            if (!_config.diagnostics_enabled && string.Equals(evt.level, "DEBUG", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            
            // Rate limiting: apply only to DEBUG and INFO levels
            if (string.Equals(evt.level, "DEBUG", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(evt.level, "INFO", StringComparison.OrdinalIgnoreCase))
            {
                if (!CheckRateLimit(evt))
                {
                    return; // Rate limit exceeded, drop event
                }
            }
        }

        // Log level filtering: drop events below minimum level
        if (!ShouldLog(evt.level))
        {
            return;
        }
        
        // Sensitive data filtering (if enabled)
        if (_config.enable_sensitive_data_filter && evt.data != null)
        {
            evt.data = SensitiveDataFilter.FilterDictionary(evt.data);
        }

        // Backpressure: if queue exceeds max size, drop DEBUG first, then INFO
        if (_queue.Count >= MAX_QUEUE_SIZE)
        {
            if (evt.level == "DEBUG")
            {
                Interlocked.Increment(ref _droppedDebugCount);
                // Emit rate-limited ERROR event for backpressure drops
                EmitBackpressureEvent("DEBUG");
                return;
            }
            if (evt.level == "INFO")
            {
                Interlocked.Increment(ref _droppedInfoCount);
                // Emit rate-limited ERROR event for backpressure drops
                EmitBackpressureEvent("INFO");
                return;
            }
            // WARN and ERROR are never dropped
        }

        _queue.Enqueue(evt);
    }
    
    /// <summary>
    /// Check rate limit for event. Returns true if event should be logged, false if rate limit exceeded.
    /// Rate limits apply ONLY to DEBUG and INFO levels. ERROR and CRITICAL always bypass.
    /// </summary>
    private bool CheckRateLimit(RobotLogEvent evt)
    {
        if (_config.event_rate_limits == null || _config.event_rate_limits.Count == 0)
        {
            return true; // No rate limits configured
        }
        
        var eventType = evt.@event ?? "";
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return true; // No event type, can't rate limit
        }
        
        if (!_config.event_rate_limits.TryGetValue(eventType, out int maxPerMinute))
        {
            return true; // No rate limit for this event type
        }
        
        var now = DateTimeOffset.UtcNow;
        
        lock (_rateLimitLock)
        {
            // Reset counters every minute
            if ((now - _lastRateLimitResetUtc).TotalMinutes >= 1.0)
            {
                _eventCountsPerMinute.Clear();
                _lastRateLimitResetUtc = now;
            }
            
            // Get current count for this event type
            if (!_eventCountsPerMinute.TryGetValue(eventType, out int currentCount))
            {
                currentCount = 0;
            }
            
            // Check if rate limit exceeded
            if (currentCount >= maxPerMinute)
            {
                return false; // Rate limit exceeded
            }
            
            // Increment count
            _eventCountsPerMinute[eventType] = currentCount + 1;
            return true;
        }
    }

    /// <summary>
    /// Check if event should be logged based on minimum log level.
    /// </summary>
    private bool ShouldLog(string level)
    {
        return _minLogLevel switch
        {
            "DEBUG" => true, // DEBUG level logs everything
            "INFO" => level == "INFO" || level == "WARN" || level == "ERROR",
            "WARN" => level == "WARN" || level == "ERROR",
            "ERROR" => level == "ERROR", // ERROR level only logs errors
            _ => true // Default: log everything if level is unknown
        };
    }

    /// <summary>
    /// Start the background worker thread.
    /// </summary>
    public void Start()
    {
        if (_backgroundWorker != null && !_backgroundWorker.IsCompleted) return;

        // If previous worker completed or was cancelled, create new token source
        if (_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
        }
        
        _backgroundWorker = Task.Run(() => WorkerLoop(_cancellationTokenSource.Token));
    }

    /// <summary>
    /// Stop the service: drain queue and flush, then stop.
    /// Bounded time limit to prevent NinjaTrader termination from hanging.
    /// </summary>
    public void Stop()
    {
        if (_backgroundWorker == null) return;

        // Signal cancellation
        _cancellationTokenSource.Cancel();

        bool workerCompleted = false;
        try
        {
            // Wait for worker to finish (with timeout)
            workerCompleted = _backgroundWorker.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // Expected when cancellation token is triggered
        }

        if (!workerCompleted)
        {
            // Worker didn't complete in time - log warning but continue shutdown
            LogErrorToFile("WARNING: Worker thread did not complete within timeout during shutdown");
        }

        // Final flush: drain remaining queue (bounded)
        var remainingCount = _queue.Count;
        if (remainingCount > 0)
        {
            // Limit final flush to prevent hanging on very large queues
            var maxFinalFlush = Math.Min(remainingCount, 10000);
            for (int i = 0; i < maxFinalFlush && _queue.TryDequeue(out var evt); i++)
            {
                // Process remaining events in small batches
                if (i % 1000 == 0)
                {
                    FlushBatch(force: false);
                }
            }
            FlushBatch(force: true);
            
            if (_queue.Count > 0)
            {
                LogErrorToFile($"WARNING: {_queue.Count} events were dropped during shutdown due to timeout");
            }
        }
        else
        {
            FlushBatch(force: true);
        }

        // Close all writers
        lock (_writersLock)
        {
            foreach (var writer in _writers.Values)
            {
                try
                {
                    writer.Flush();
                    writer.Dispose();
                }
                catch
                {
                    // Ignore errors during shutdown
                }
            }
            _writers.Clear();
        }
        
        // Close all health writers
        lock (_healthWritersLock)
        {
            foreach (var writer in _healthWriters.Values)
            {
                try
                {
                    writer.Flush();
                    writer.Dispose();
                }
                catch
                {
                    // Ignore errors during shutdown
                }
            }
            _healthWriters.Clear();
        }

        _backgroundWorker = null;
    }

    private void WorkerLoop(CancellationToken cancellationToken)
    {
        // Mark thread as background so NinjaTrader can exit cleanly
        Thread.CurrentThread.IsBackground = true;
        
        var lastFlush = DateTime.UtcNow;
        var lastBackpressureWarning = DateTime.MinValue;
        const int BACKPRESSURE_WARNING_INTERVAL_SECONDS = 10;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var timeSinceFlush = (now - lastFlush).TotalMilliseconds;
                var queueSize = _queue.Count;

                // Log backpressure warnings periodically
                if (queueSize > MAX_QUEUE_SIZE * 0.8 && 
                    (now - lastBackpressureWarning).TotalSeconds >= BACKPRESSURE_WARNING_INTERVAL_SECONDS)
                {
                    lastBackpressureWarning = now;
                    var droppedDebug = Interlocked.Read(ref _droppedDebugCount);
                    var droppedInfo = Interlocked.Read(ref _droppedInfoCount);
                    LogErrorToFile($"Backpressure: queue_size={queueSize}, dropped_debug={droppedDebug}, dropped_info={droppedInfo}");
                }

                if (timeSinceFlush >= FLUSH_INTERVAL_MS || queueSize >= MAX_BATCH_PER_FLUSH)
                {
                    FlushBatch(force: false);
                    lastFlush = now;
                }

                // Sleep briefly to avoid tight loop
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                LogErrorToFile($"Worker loop error: {ex.Message}");
                // Emit rate-limited ERROR event for worker loop errors
                EmitWorkerErrorEvent(ex);
                Thread.Sleep(1000); // Back off on error
            }
        }

        // Final flush on cancellation
        FlushBatch(force: true);
    }

    private void FlushBatch(bool force)
    {
        var batch = new List<RobotLogEvent>();
        var count = force ? int.MaxValue : MAX_BATCH_PER_FLUSH;

        // Dequeue up to batch size
        while (batch.Count < count && _queue.TryDequeue(out var evt))
        {
            batch.Add(evt);
        }

        if (batch.Count == 0) return;

        // Group by instrument for per-instrument files
        var byInstrument = new Dictionary<string, List<RobotLogEvent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in batch)
        {
            var instrument = string.IsNullOrWhiteSpace(evt.instrument) ? "ENGINE" : evt.instrument;
            if (!byInstrument.TryGetValue(instrument, out var list))
            {
                list = new List<RobotLogEvent>();
                byInstrument[instrument] = list;
            }
            list.Add(evt);
        }

        // Write each instrument's batch
        foreach (var kvp in byInstrument)
        {
            WriteBatchToFile(kvp.Key, kvp.Value);
        }
        
        // Write to health sink if enabled (events >= WARN + selected INFO)
        if (_config.enable_health_sink)
        {
            foreach (var evt in batch)
            {
                if (ShouldWriteToHealthSink(evt))
                {
                    WriteToHealthSink(evt);
                }
            }
        }

        // Periodically emit a human-friendly daily summary (best-effort).
        MaybeWriteDailySummary(force);

        // Log dropped counts if any (aggregated every flush)
        var droppedDebug = Interlocked.Exchange(ref _droppedDebugCount, 0);
        var droppedInfo = Interlocked.Exchange(ref _droppedInfoCount, 0);
        if (droppedDebug > 0 || droppedInfo > 0)
        {
            var queueSize = _queue.Count;
            LogErrorToFile($"Backpressure: dropped_debug={droppedDebug}, dropped_info={droppedInfo}, queue_size={queueSize}");
        }
    }

    private void MaybeWriteDailySummary(bool force)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastDailySummaryWriteUtc).TotalSeconds < DAILY_SUMMARY_WRITE_INTERVAL_SECONDS)
                return;

            _lastDailySummaryWriteUtc = now;

            var date = now.ToString("yyyyMMdd");
            var summaryPath = Path.Combine(_logDirectory, $"daily_{date}.md");

            // Snapshot counters that can change across threads
            var droppedDebug = Interlocked.Read(ref _droppedDebugCount);
            var droppedInfo = Interlocked.Read(ref _droppedInfoCount);
            var writeFailures = Volatile.Read(ref _writeFailureCount);

            var md = _dailySummary.RenderMarkdown(_logDirectory, droppedDebug, droppedInfo, writeFailures);
            File.WriteAllText(summaryPath, md, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // Do not throw from worker loop; log once per interval to sidecar file.
            LogErrorToFile($"Daily summary write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if event should be written to health sink.
    /// Health sink includes: events >= WARN OR selected INFO events (TRADE_COMPLETED).
    /// </summary>
    private bool ShouldWriteToHealthSink(RobotLogEvent evt)
    {
        // Always include WARN, ERROR, CRITICAL
        if (string.Equals(evt.level, "WARN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.level, "ERROR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.level, "CRITICAL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        
        // Include selected INFO events
        if (string.Equals(evt.level, "INFO", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(evt.@event) &&
            HEALTH_SINK_INFO_EVENTS.Contains(evt.@event))
        {
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Write event to health sink with slot_instance_key granularity.
    /// Path: logs/health/<trading_date>_<instrument>_<stream>_<slot_instance_key>.jsonl
    /// Falls back to logs/health/<trading_date>_<instrument>_<stream>.jsonl if slot_instance_key is null.
    /// </summary>
    private void WriteToHealthSink(RobotLogEvent evt)
    {
        StreamWriter? writer = null;
        try
        {
            // Extract fields for health sink path
            var tradingDate = evt.trading_date ?? "";
            var instrument = string.IsNullOrWhiteSpace(evt.instrument) ? "ENGINE" : evt.instrument;
            var stream = evt.stream ?? "";
            var slotInstanceKey = ExtractSlotInstanceKey(evt);
            
            // Build health sink key (used for writer lookup)
            var healthKey = string.IsNullOrWhiteSpace(slotInstanceKey)
                ? $"{tradingDate}_{instrument}_{stream}"
                : $"{tradingDate}_{instrument}_{stream}_{slotInstanceKey}";
            
            lock (_healthWritersLock)
            {
                if (!_healthWriters.TryGetValue(healthKey, out writer))
                {
                    var sanitizedKey = SanitizeFileName(healthKey);
                    var filePath = Path.Combine(_healthDirectory, $"{sanitizedKey}.jsonl");
                    var fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    writer = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = false };
                    _healthWriters[healthKey] = writer;
                }
            }
            
            var json = JsonUtil.Serialize(evt);
            writer.WriteLine(json);
            writer.Flush();
        }
        catch (Exception ex)
        {
            // Log error but don't fail - health sink is supplementary
            LogErrorToFile($"Health sink write failure: {ex.Message}");
            
            // Remove broken writer
            if (writer != null)
            {
                try
                {
                    lock (_healthWritersLock)
                    {
                        // Find and remove the broken writer
                        var keyToRemove = "";
                        foreach (var kvp in _healthWriters)
                        {
                            if (kvp.Value == writer)
                            {
                                keyToRemove = kvp.Key;
                                break;
                            }
                        }
                        if (!string.IsNullOrEmpty(keyToRemove))
                        {
                            _healthWriters.Remove(keyToRemove);
                            writer.Dispose();
                        }
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
    
    /// <summary>
    /// Extract slot_instance_key from event data if present.
    /// </summary>
    private string? ExtractSlotInstanceKey(RobotLogEvent evt)
    {
        if (evt.data == null) return null;
        
        // Try to extract from data dictionary
        if (evt.data is Dictionary<string, object?> dataDict)
        {
            if (dataDict.TryGetValue("slot_instance_key", out var slotKey) && slotKey is string slotKeyStr)
            {
                return slotKeyStr;
            }
            // Also check nested payload
            if (dataDict.TryGetValue("payload", out var payload) && payload is Dictionary<string, object?> payloadDict)
            {
                if (payloadDict.TryGetValue("slot_instance_key", out var nestedSlotKey) && nestedSlotKey is string nestedSlotKeyStr)
                {
                    return nestedSlotKeyStr;
                }
            }
        }
        
        return null;
    }
    
    private void WriteBatchToFile(string instrument, List<RobotLogEvent> batch)
    {
        StreamWriter? writer = null;
        try
        {
            lock (_writersLock)
            {
                // Check if rotation is needed before writing
                RotateLogFileIfNeeded(instrument);
                
                if (!_writers.TryGetValue(instrument, out writer))
                {
                    var sanitizedInstrument = SanitizeFileName(instrument);
                    var filePath = Path.Combine(_logDirectory, $"robot_{sanitizedInstrument}.jsonl");
                    var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };
                    _writers[instrument] = writer;
                }
            }

            // Serialize and write each event (compact JSON, one per line)
            foreach (var evt in batch)
            {
                // Update daily summary counters from worker thread (no locks needed)
                _dailySummary.Observe(evt);
                var json = JsonUtil.Serialize(evt);
                writer.WriteLine(json);
            }

            writer.Flush();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _writeFailureCount);
            LogErrorToFile($"Write failure for {instrument}: {ex.Message}");
            // Emit ERROR event for write failures (bypass queue since write is broken)
            EmitWriteFailureEvent(instrument, ex);

            // If writer is broken, remove it so we can retry next time
            if (writer != null)
            {
                try
                {
                    lock (_writersLock)
                    {
                        _writers.Remove(instrument);
                        writer.Dispose();
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    /// <summary>
    /// Rotate log file if it exceeds maximum size or daily rotation is enabled.
    /// </summary>
    private void RotateLogFileIfNeeded(string instrument)
    {
        var sanitizedInstrument = SanitizeFileName(instrument);
        var filePath = Path.Combine(_logDirectory, $"robot_{sanitizedInstrument}.jsonl");
        
        if (!File.Exists(filePath)) return;
        
        var fileInfo = new FileInfo(filePath);
        var nowUtc = DateTime.UtcNow;
        var shouldRotate = false;
        
        // Check size-based rotation
        if (fileInfo.Length > _maxLogFileSizeBytes)
        {
            shouldRotate = true;
        }
        // Check daily rotation (if enabled)
        else if (_config.rotate_daily)
        {
            var fileDate = fileInfo.CreationTimeUtc.Date;
            var todayDate = nowUtc.Date;
            if (fileDate < todayDate)
            {
                shouldRotate = true;
            }
        }
        
        if (!shouldRotate) return;

        // Close current writer if it exists
        if (_writers.TryGetValue(instrument, out var writer))
        {
            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch
            {
                // Ignore errors during rotation
            }
            _writers.Remove(instrument);
        }

        // Rotate file with timestamp
        try
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var rotatedPath = Path.Combine(_logDirectory, $"robot_{sanitizedInstrument}_{timestamp}.jsonl");
            File.Move(filePath, rotatedPath);
            
            // Cleanup old rotated files (keep only last N)
            CleanupOldRotatedFiles(sanitizedInstrument);
        }
        catch (Exception ex)
        {
            LogErrorToFile($"Log rotation failed for {instrument}: {ex.Message}");
        }
    }

    /// <summary>
    /// Clean up old rotated log files, keeping only the most recent N files.
    /// </summary>
    private void CleanupOldRotatedFiles(string sanitizedInstrument)
    {
        try
        {
            var pattern = $"robot_{sanitizedInstrument}_*.jsonl";
            var rotatedFiles = Directory.GetFiles(_logDirectory, pattern);
            
            if (rotatedFiles.Length <= _config.max_rotated_files)
                return;

            // Sort by creation time (oldest first)
            Array.Sort(rotatedFiles, (a, b) => File.GetCreationTime(a).CompareTo(File.GetCreationTime(b)));
            
            // Delete oldest files, keeping only the most recent N
            var filesToDelete = rotatedFiles.Length - _config.max_rotated_files;
            for (int i = 0; i < filesToDelete; i++)
            {
                try
                {
                    File.Delete(rotatedFiles[i]);
                }
                catch
                {
                    // Ignore individual file deletion errors
                }
            }
        }
        catch
        {
            // Ignore cleanup errors (fail-open)
        }
    }

    /// <summary>
    /// Archive old rotated log files to archive directory.
    /// </summary>
    private void ArchiveOldLogs()
    {
        try
        {
            var archiveDir = Path.Combine(_logDirectory, "archive");
            Directory.CreateDirectory(archiveDir);
            
            var cutoffDate = DateTime.UtcNow.AddDays(-_config.archive_days);
            var rotatedFiles = Directory.GetFiles(_logDirectory, "robot_*_*.jsonl");
            
            foreach (var file in rotatedFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.CreationTimeUtc < cutoffDate)
                    {
                        var fileName = Path.GetFileName(file);
                        var archivePath = Path.Combine(archiveDir, fileName);
                        File.Move(file, archivePath);
                    }
                }
                catch
                {
                    // Ignore individual file archive errors
                }
            }
        }
        catch
        {
            // Ignore archive errors (fail-open)
        }
    }

    private static string SanitizeFileName(string instrument)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = instrument;
        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c, '_');
        }
        return sanitized.ToUpperInvariant();
    }

    /// <summary>
    /// Emit backpressure drop event directly to JSONL (bypasses queue since queue is full).
    /// Rate-limited to prevent spam.
    /// </summary>
    private void EmitBackpressureEvent(string droppedLevel)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastBackpressureEventUtc).TotalSeconds < BACKPRESSURE_EVENT_RATE_LIMIT_SECONDS)
            return;

        _lastBackpressureEventUtc = now;
        try
        {
            var droppedDebug = Interlocked.Read(ref _droppedDebugCount);
            var droppedInfo = Interlocked.Read(ref _droppedInfoCount);
            var queueSize = _queue.Count;

            var errorEvent = new RobotLogEvent(
                DateTimeOffset.UtcNow,
                "ERROR",
                "RobotLoggingService",
                "ENGINE",
                "LOG_BACKPRESSURE_DROP",
                $"Logging backpressure: dropped {droppedLevel} events",
                runId: "LOGGING_SERVICE",
                data: new Dictionary<string, object?>
                {
                    ["dropped_level"] = droppedLevel,
                    ["dropped_debug_count"] = droppedDebug,
                    ["dropped_info_count"] = droppedInfo,
                    ["queue_size"] = queueSize,
                    ["max_queue_size"] = MAX_QUEUE_SIZE,
                    ["note"] = "Events dropped due to queue backpressure - consider increasing MAX_QUEUE_SIZE or reducing log volume"
                }
            );

            // Write directly to ENGINE log file, bypassing queue
            WriteEventDirectly("ENGINE", errorEvent);
        }
        catch
        {
            // If even direct write fails, silently fail to prevent infinite loops
        }
    }

    /// <summary>
    /// Emit worker loop error event directly to JSONL (bypasses queue).
    /// Rate-limited to prevent spam.
    /// </summary>
    private void EmitWorkerErrorEvent(Exception ex)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastWorkerErrorEventUtc).TotalSeconds < WORKER_ERROR_EVENT_RATE_LIMIT_SECONDS)
            return;

        _lastWorkerErrorEventUtc = now;
        try
        {
            var errorEvent = new RobotLogEvent(
                DateTimeOffset.UtcNow,
                "ERROR",
                "RobotLoggingService",
                "ENGINE",
                "LOG_WORKER_LOOP_ERROR",
                $"Worker loop error: {ex.Message}",
                runId: "LOGGING_SERVICE",
                data: new Dictionary<string, object?>
                {
                    ["exception_type"] = ex.GetType().Name,
                    ["error"] = ex.Message,
                    ["stack_trace"] = ex.StackTrace != null && ex.StackTrace.Length > 500 ? ex.StackTrace.Substring(0, 500) : ex.StackTrace,
                    ["note"] = "Background worker loop encountered an error"
                }
            );

            // Write directly to ENGINE log file, bypassing queue
            WriteEventDirectly("ENGINE", errorEvent);
        }
        catch
        {
            // If even direct write fails, silently fail to prevent infinite loops
        }
    }

    /// <summary>
    /// Emit write failure event directly to JSONL (bypasses queue since write is broken).
    /// </summary>
    private void EmitWriteFailureEvent(string instrument, Exception ex)
    {
        try
        {
            var errorEvent = new RobotLogEvent(
                DateTimeOffset.UtcNow,
                "ERROR",
                "RobotLoggingService",
                instrument,
                "LOG_WRITE_FAILURE",
                $"Write failure for {instrument}: {ex.Message}",
                runId: "LOGGING_SERVICE",
                data: new Dictionary<string, object?>
                {
                    ["exception_type"] = ex.GetType().Name,
                    ["error"] = ex.Message,
                    ["stack_trace"] = ex.StackTrace != null && ex.StackTrace.Length > 500 ? ex.StackTrace.Substring(0, 500) : ex.StackTrace,
                    ["write_failure_count"] = _writeFailureCount,
                    ["note"] = "Failed to write log batch to file"
                }
            );

            // Write directly to ENGINE log file (since instrument-specific write is broken)
            WriteEventDirectly("ENGINE", errorEvent);
        }
        catch
        {
            // If even direct write fails, silently fail to prevent infinite loops
        }
    }

    /// <summary>
    /// Write event directly to JSONL file, bypassing the queue.
    /// Used for critical errors when queue might be full or broken.
    /// </summary>
    private void WriteEventDirectly(string instrument, RobotLogEvent evt)
    {
        try
        {
            lock (_writersLock)
            {
                var sanitizedInstrument = SanitizeFileName(instrument);
                var filePath = Path.Combine(_logDirectory, $"robot_{sanitizedInstrument}.jsonl");

                // Use File.AppendAllText for direct write (thread-safe for append-only)
                var json = JsonUtil.Serialize(evt);
                File.AppendAllText(filePath, json + Environment.NewLine);
            }
        }
        catch
        {
            // If even direct write fails, silently fail to prevent infinite loops
        }
    }

    private void LogErrorToFile(string message)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastErrorLogTime).TotalSeconds < ERROR_LOG_INTERVAL_SECONDS)
            return;

        _lastErrorLogTime = now;
        try
        {
            var errorLogPath = Path.Combine(_logDirectory, "robot_logging_errors.txt");
            var errorMsg = $"[{now:yyyy-MM-dd HH:mm:ss} UTC] {message}\n";
            File.AppendAllText(errorLogPath, errorMsg);
        }
        catch
        {
            // Silently fail if we can't write error log
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cancellationTokenSource.Dispose();
    }

    /// <summary>
    /// Get the current singleton instance (for testing/debugging).
    /// Returns null if no instance exists.
    /// </summary>
    public static RobotLoggingService? GetInstance(string projectRoot, string? customLogDir = null)
    {
        var key = customLogDir ?? Path.Combine(projectRoot, "logs", "robot");
        lock (_instancesLock)
        {
            _instances.TryGetValue(key, out var instance);
            return instance;
        }
    }
}
