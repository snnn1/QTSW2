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

public sealed partial class RobotLoggingService
{
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
    
    /// <summary>Persistence base from <c>.../logs/robot</c> → parent of <c>logs</c> (engine <c>_persistenceBase</c>).</summary>
    private string GetPersistenceBaseFromLogsRobotDir()
    {
        try
        {
            var robotLogs = Path.GetFullPath(_logDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var logsDir = Path.GetDirectoryName(robotLogs);
            return Path.GetDirectoryName(logsDir ?? "") ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Write event to health sink with slot_instance_key granularity.
    /// Path: logs/health/<trading_date>_<instrument>_<stream>_<slot_instance_key>.jsonl
    /// Falls back to logs/health/<trading_date>_<instrument>_<stream>.jsonl if slot_instance_key is null.
    /// </summary>
    private void WriteToHealthSink(RobotLogEvent evt)
    {
        string healthKey = "";
        try
        {
            var persist = GetPersistenceBaseFromLogsRobotDir();
            evt.scope = RobotRunArtifactPaths.AuditScopeLabel(persist);
            evt.run_id = RobotRunArtifactPaths.IsRunScopedPersistence(persist)
                ? RobotRunArtifactPaths.AuditRunIdLabel(persist, evt.run_id)
                : "NONE";

            // Extract fields for health sink path
            var tradingDate = evt.trading_date ?? "";
            var instrument = string.IsNullOrWhiteSpace(evt.instrument) ? "ENGINE" : evt.instrument;
            var stream = evt.stream ?? "";
            var slotInstanceKey = ExtractSlotInstanceKey(evt);
            
            // Build health sink key (used for writer lookup)
            healthKey = string.IsNullOrWhiteSpace(slotInstanceKey)
                ? $"{tradingDate}_{instrument}_{stream}"
                : $"{tradingDate}_{instrument}_{stream}_{slotInstanceKey}";
            
            lock (_healthWritersLock)
            {
                if (!_healthWriters.TryGetValue(healthKey, out var writer))
                {
                    var sanitizedKey = SanitizeFileName(healthKey);
                    var filePath = Path.Combine(_healthDirectory, $"{sanitizedKey}.jsonl");
                    var fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    writer = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = false };
                    _healthWriters[healthKey] = writer;
                }

                var json = JsonUtil.Serialize(evt);
                writer.WriteLine(json);
                writer.Flush();
            }
        }
        catch (Exception ex)
        {
            // Log error but don't fail - health sink is supplementary
            LogErrorToFile($"Health sink write failure: {ex.Message}");
            
            // Remove broken writer
            try
            {
                lock (_healthWritersLock)
                {
                    if (string.IsNullOrWhiteSpace(healthKey))
                        return;

                    var keyToRemove = "";
                    foreach (var kvp in _healthWriters)
                    {
                        if (string.Equals(kvp.Key, healthKey, StringComparison.OrdinalIgnoreCase))
                        {
                            keyToRemove = kvp.Key;
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(keyToRemove) && _healthWriters.TryGetValue(keyToRemove, out var brokenWriter))
                    {
                        _healthWriters.Remove(keyToRemove);
                        brokenWriter.Dispose();
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors
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
        try
        {
            lock (_writersLock)
            {
                // Check if rotation is needed before writing
                RotateLogFileIfNeeded(instrument);
                
                if (!_writers.TryGetValue(instrument, out var writer))
                {
                    var sanitizedInstrument = SanitizeFileName(instrument);
                    var filePath = Path.Combine(_logDirectory, $"robot_{sanitizedInstrument}.jsonl");
                    var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };
                    _writers[instrument] = writer;
                }

                // Serialize and write each event (compact JSON, one per line).
                // Keep the writer lock through WriteLine/Flush so shutdown, rotation,
                // and direct error writes cannot touch the same StreamWriter concurrently.
                foreach (var evt in batch)
                {
                    // Update daily summary counters from worker thread (no locks needed)
                    _dailySummary.Observe(evt);
                    var json = JsonUtil.Serialize(evt);
                    writer.WriteLine(json);
                }

                writer.Flush();
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _writeFailureCount);
            LogErrorToFile($"Write failure for {instrument}: {ex.Message}");

            // If writer is broken, remove it so we can retry next time
            try
            {
                lock (_writersLock)
                {
                    if (_writers.TryGetValue(instrument, out var brokenWriter))
                    {
                        _writers.Remove(instrument);
                        brokenWriter.Dispose();
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            // Emit ERROR event for write failures after removing the broken writer.
            EmitWriteFailureEvent(instrument, ex);
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

                var json = JsonUtil.Serialize(evt);
                if (_writers.TryGetValue(instrument, out var writer))
                {
                    writer.WriteLine(json);
                    writer.Flush();
                    return;
                }

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

}
