using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

/// <summary>
/// Singleton service for persisting hydration lifecycle events to separate JSONL files.
/// Logs edge events only: STREAM_INITIALIZED, PRE_HYDRATION_COMPLETE, ARMED, RANGE_BUILDING_START, RANGE_LOCKED.
/// Ensures fail-safe operation (never throws exceptions).
/// </summary>
public sealed class HydrationEventPersister
{
    private readonly string _hydrationDir;
    private static HydrationEventPersister? _instance;
    private static readonly object _instanceLock = new();
    
    // Per-file locks for thread-safe writes (similar to RangeLockedEventPersister pattern)
    private static readonly Dictionary<string, object> _fileLocks = new();
    private static readonly object _locksLock = new();
    
    private HydrationEventPersister(string projectRoot)
    {
        _hydrationDir = Path.Combine(projectRoot, "logs", "robot");
        Directory.CreateDirectory(_hydrationDir);
    }
    
    public static HydrationEventPersister GetInstance(string projectRoot)
    {
        if (_instance == null)
        {
            lock (_instanceLock)
            {
                if (_instance == null)
                {
                    _instance = new HydrationEventPersister(projectRoot);
                }
            }
        }
        return _instance;
    }
    
    /// <summary>
    /// Persist a hydration event to the hydration log file.
    /// Never throws exceptions - all errors are caught and logged internally.
    /// </summary>
    public void Persist(HydrationEvent evt)
    {
        try
        {
            var filePath = GetFilePath(evt.trading_day);
            var json = JsonUtil.Serialize(evt);
            var fileLock = GetFileLock(filePath);
            
            lock (fileLock)
            {
                try
                {
                    // Append to JSONL file
                    File.AppendAllText(filePath, json + Environment.NewLine);
                }
                catch (IOException ex)
                {
                    // File might be locked by another process - fail silently (fail-safe)
                    // This is acceptable - hydration logging is non-critical
                }
            }
        }
        catch (Exception)
        {
            // Fail-safe: Never throw exceptions to caller
            // Persistence failure does not affect execution
        }
    }
    
    private string GetFilePath(string tradingDay)
    {
        return Path.Combine(_hydrationDir, $"hydration_{tradingDay}.jsonl");
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
