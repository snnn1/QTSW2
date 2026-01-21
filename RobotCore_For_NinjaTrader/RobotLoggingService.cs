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
    private CancellationTokenSource _cancellationTokenSource = new();
    private Task? _backgroundWorker;
    private bool _disposed = false;
    private int _referenceCount = 0; // Track how many engines are using this instance
    private readonly object _referenceLock = new();

    // Configuration constants
    private const int FLUSH_INTERVAL_MS = 500;
    private const int MAX_QUEUE_SIZE = 50000;
    private const int MAX_BATCH_PER_FLUSH = 2000;
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
        _instanceCounter++;
        
        // Load logging configuration
        _config = LoggingConfig.LoadFromFile(projectRoot);
        _maxLogFileSizeBytes = _config.max_file_size_mb * 1024 * 1024;
        _minLogLevel = _config.min_log_level ?? "INFO";
        
        // Archive old logs on startup
        ArchiveOldLogs();
    }

    /// <summary>
    /// Non-blocking enqueue. Never throws from OnBarUpdate.
    /// </summary>
    public void Log(RobotLogEvent evt)
    {
        if (_disposed) return;

        // Log level filtering: drop events below minimum level
        if (!ShouldLog(evt.level))
        {
            return;
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

        // Log dropped counts if any (aggregated every flush)
        var droppedDebug = Interlocked.Exchange(ref _droppedDebugCount, 0);
        var droppedInfo = Interlocked.Exchange(ref _droppedInfoCount, 0);
        if (droppedDebug > 0 || droppedInfo > 0)
        {
            var queueSize = _queue.Count;
            LogErrorToFile($"Backpressure: dropped_debug={droppedDebug}, dropped_info={droppedInfo}, queue_size={queueSize}");
        }
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
    /// Rotate log file if it exceeds maximum size.
    /// </summary>
    private void RotateLogFileIfNeeded(string instrument)
    {
        var sanitizedInstrument = SanitizeFileName(instrument);
        var filePath = Path.Combine(_logDirectory, $"robot_{sanitizedInstrument}.jsonl");
        
        if (!File.Exists(filePath)) return;
        
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length <= _maxLogFileSizeBytes) return;

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
