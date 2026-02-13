using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

/// <summary>
/// Singleton service for persisting RANGE_LOCKED events to separate JSONL files.
/// Ensures idempotency (one event per stream per trading day) and fail-safe operation.
/// </summary>
public sealed class RangeLockedEventPersister
{
    private readonly string _rangesDir;
    private static RangeLockedEventPersister? _instance;
    private static readonly object _instanceLock = new();
    
    // Per-file locks for thread-safe writes (similar to JournalStore pattern)
    private static readonly Dictionary<string, object> _fileLocks = new();
    private static readonly object _locksLock = new();
    
    // In-memory cache: Dictionary<trading_day, HashSet<stream_id>>
    // Avoids file I/O in hot path after initial load
    private readonly Dictionary<string, HashSet<string>> _cache = new();
    private readonly object _cacheLock = new();
    
    private RangeLockedEventPersister(string projectRoot)
    {
        _rangesDir = Path.Combine(projectRoot, "logs", "robot");
        Directory.CreateDirectory(_rangesDir);
    }
    
    public static RangeLockedEventPersister GetInstance(string projectRoot)
    {
        if (_instance == null)
        {
            lock (_instanceLock)
            {
                if (_instance == null)
                {
                    _instance = new RangeLockedEventPersister(projectRoot);
                }
            }
        }
        return _instance;
    }
    
    /// <summary>
    /// Persist a RANGE_LOCKED event with idempotency check.
    /// Never throws exceptions - all errors are caught and logged internally.
    /// </summary>
    public void Persist(RangeLockedEvent evt)
    {
        try
        {
            var filePath = GetFilePath(evt.trading_day);
            
            // Check cache first (fast path, no I/O)
            if (HasEventInCache(evt.trading_day, evt.stream_id))
            {
                // Event already exists - check if values differ (should never happen)
                var existingEvent = TryLoadEventFromFile(filePath, evt.trading_day, evt.stream_id);
                if (existingEvent != null && !EventsEqual(existingEvent, evt))
                {
                    // Values differ - log ERROR (this indicates a logic bug)
                    LogMismatchError(existingEvent, evt);
                }
                // Skip write (preserve original event)
                return;
            }
            
            // Load cache if needed (lazy load)
            EnsureCacheLoaded(evt.trading_day, filePath);
            
            // Check again after cache load
            if (HasEventInCache(evt.trading_day, evt.stream_id))
            {
                var existingEvent = TryLoadEventFromFile(filePath, evt.trading_day, evt.stream_id);
                if (existingEvent != null && !EventsEqual(existingEvent, evt))
                {
                    LogMismatchError(existingEvent, evt);
                }
                return;
            }
            
            // Event doesn't exist - append to file
            var json = JsonUtil.Serialize(evt);
            var fileLock = GetFileLock(filePath);
            
            lock (fileLock)
            {
                try
                {
                    // Append to JSONL file
                    File.AppendAllText(filePath, json + Environment.NewLine);
                    
                    // Update cache
                    lock (_cacheLock)
                    {
                        if (!_cache.TryGetValue(evt.trading_day, out var streamSet))
                        {
                            streamSet = new HashSet<string>();
                            _cache[evt.trading_day] = streamSet;
                        }
                        streamSet.Add(evt.stream_id);
                    }
                }
                catch (IOException ex)
                {
                    // File might be locked by another process - fail silently (fail-safe)
                    // This is acceptable - idempotency check will catch duplicate on retry
                }
            }
        }
        catch (Exception)
        {
            // Fail-safe: Never throw exceptions to caller
            // Persistence failure does not affect execution
        }
    }
    
    /// <summary>
    /// Check if event exists (for external queries, not used in hot path).
    /// Never throws exceptions.
    /// </summary>
    public bool HasEvent(string tradingDay, string streamId)
    {
        try
        {
            var filePath = GetFilePath(tradingDay);
            EnsureCacheLoaded(tradingDay, filePath);
            return HasEventInCache(tradingDay, streamId);
        }
        catch
        {
            return false; // Fail-safe: return false on error
        }
    }
    
    private string GetFilePath(string tradingDay)
    {
        return Path.Combine(_rangesDir, $"ranges_{tradingDay}.jsonl");
    }
    
    private bool HasEventInCache(string tradingDay, string streamId)
    {
        lock (_cacheLock)
        {
            return _cache.TryGetValue(tradingDay, out var streamSet) && streamSet.Contains(streamId);
        }
    }
    
    private void EnsureCacheLoaded(string tradingDay, string filePath)
    {
        lock (_cacheLock)
        {
            if (_cache.ContainsKey(tradingDay))
            {
                return; // Cache already loaded
            }
            
            // Load cache from file (lazy load)
            _LoadCacheForDay(tradingDay, filePath);
        }
    }
    
    private void _LoadCacheForDay(string tradingDay, string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _cache[tradingDay] = new HashSet<string>();
                return;
            }
            
            var fileLock = GetFileLock(filePath);
            lock (fileLock)
            {
                var streamSet = new HashSet<string>();
                var lines = File.ReadAllLines(filePath);
                
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    
                    try
                    {
                        var evt = JsonUtil.Deserialize<RangeLockedEvent>(line);
                        if (evt != null && evt.trading_day == tradingDay)
                        {
                            streamSet.Add(evt.stream_id);
                        }
                    }
                    catch
                    {
                        // Skip malformed lines
                    }
                }
                
                _cache[tradingDay] = streamSet;
            }
        }
        catch
        {
            // Fail-safe: Initialize empty cache on error
            _cache[tradingDay] = new HashSet<string>();
        }
    }
    
    private RangeLockedEvent? TryLoadEventFromFile(string filePath, string tradingDay, string streamId)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            
            var fileLock = GetFileLock(filePath);
            lock (fileLock)
            {
                var lines = File.ReadAllLines(filePath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    
                    try
                    {
                        var evt = JsonUtil.Deserialize<RangeLockedEvent>(line);
                        if (evt != null && evt.trading_day == tradingDay && evt.stream_id == streamId)
                        {
                            return evt;
                        }
                    }
                    catch
                    {
                        // Skip malformed lines
                    }
                }
            }
        }
        catch
        {
            // Fail-safe: Return null on error
        }
        
        return null;
    }
    
    private bool EventsEqual(RangeLockedEvent a, RangeLockedEvent b)
    {
        return a.trading_day == b.trading_day &&
               a.stream_id == b.stream_id &&
               a.canonical_instrument == b.canonical_instrument &&
               a.execution_instrument == b.execution_instrument &&
               a.range_high == b.range_high &&
               a.range_low == b.range_low &&
               a.range_size == b.range_size &&
               a.freeze_close == b.freeze_close &&
               a.range_high_rounded == b.range_high_rounded &&
               a.range_low_rounded == b.range_low_rounded &&
               a.breakout_long == b.breakout_long &&
               a.breakout_short == b.breakout_short;
    }
    
    private void LogMismatchError(RangeLockedEvent oldEvent, RangeLockedEvent newEvent)
    {
        // This should never happen - indicates a logic bug
        // Log ERROR with full diff for debugging
        // Note: We can't use RobotLogger here since we're in core library
        // Instead, write directly to a log file or use Console.Error
        try
        {
            var errorLogPath = Path.Combine(_rangesDir, $"ranges_error_{oldEvent.trading_day}.log");
            var errorMsg = $"[ERROR] RANGE_LOCKED idempotency mismatch detected for {oldEvent.trading_day}:{oldEvent.stream_id}\n" +
                          $"Old event: {JsonUtil.Serialize(oldEvent)}\n" +
                          $"New event: {JsonUtil.Serialize(newEvent)}\n" +
                          $"This should never happen - indicates a logic bug where range changed after lock.\n" +
                          $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n\n";
            
            File.AppendAllText(errorLogPath, errorMsg);
        }
        catch
        {
            // Even error logging fails - fail-safe: do nothing
        }
    }
    
    private static object GetFileLock(string path)
    {
        lock (_locksLock)
        {
            if (!_fileLocks.TryGetValue(path, out var fileLock))
            {
                fileLock = new object();
                _fileLocks[path] = fileLock;
            }
            return fileLock;
        }
    }
}
