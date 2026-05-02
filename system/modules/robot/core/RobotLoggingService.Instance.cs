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
    public static RobotLoggingService GetOrCreate(string configRoot, string? robotLogDirectory = null)
    {
        var effective = robotLogDirectory ?? RobotRunArtifactPaths.LogsRobot(configRoot);
        var key = effective;

        lock (_instancesLock)
        {
            if (!_instances.TryGetValue(key, out var instance) || instance._disposed)
            {
                instance = new RobotLoggingService(configRoot, effective);
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

    /// <param name="configRoot">Repo root for configs (not necessarily the run persistence root).</param>
    /// <param name="robotLogDirectory">Absolute path to <c>logs/robot</c> under the active run root.</param>
    private RobotLoggingService(string configRoot, string robotLogDirectory)
    {
        _logDirectory = robotLogDirectory;
        Directory.CreateDirectory(_logDirectory);
        // Authoritative health sink for run reconstruction: same persistence tree as robot JSONL (…/logs/health).
        var logsParent = Path.GetDirectoryName(_logDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
        _healthDirectory = Path.Combine(logsParent, "health");
        Directory.CreateDirectory(_healthDirectory);
        _instanceCounter++;
        
        // Load logging configuration
        _config = LoggingConfig.LoadFromFile(configRoot, _logDirectory, suppressEmergencyWrite: true);
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
}
