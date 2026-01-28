using System;
using System.IO;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// PHASE 3.1: Single-executor guard for canonical markets.
/// Prevents multiple robot instances from executing the same canonical market simultaneously.
/// 
/// Uses lock file approach (portable, deterministic):
/// - Lock file: {projectRoot}/runtime_locks/canonical_{instrument}.lock
/// - Contains run_id and timestamp
/// - Second instance fails closed if lock exists and is fresh
/// </summary>
public sealed class CanonicalMarketLock : IDisposable
{
    private readonly string _lockFilePath;
    private readonly string _runId;
    private readonly RobotLogger _log;
    private bool _acquired = false;
    private const int STALE_LOCK_THRESHOLD_MINUTES = 10; // Lock older than 10 minutes is considered stale

    public CanonicalMarketLock(string projectRoot, string canonicalInstrument, string runId, RobotLogger log)
    {
        if (string.IsNullOrWhiteSpace(canonicalInstrument))
            throw new ArgumentException("Canonical instrument cannot be null or empty", nameof(canonicalInstrument));
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("Run ID cannot be null or empty", nameof(runId));

        _runId = runId;
        _log = log;
        
        // Create runtime_locks directory if it doesn't exist
        var locksDir = Path.Combine(projectRoot, "runtime_locks");
        Directory.CreateDirectory(locksDir);
        
        // Lock file name: canonical_{instrument}.lock
        var sanitizedInstrument = canonicalInstrument.ToUpperInvariant().Replace(" ", "_");
        _lockFilePath = Path.Combine(locksDir, $"canonical_{sanitizedInstrument}.lock");
    }

    /// <summary>
    /// Attempt to acquire the canonical market lock.
    /// Returns true if acquired, false if already locked by another instance.
    /// </summary>
    public bool TryAcquire(DateTimeOffset utcNow)
    {
        if (_acquired)
            return true; // Already acquired

        // Check if lock file exists
        if (File.Exists(_lockFilePath))
        {
            // Check if lock is stale
            var lockFileInfo = new FileInfo(_lockFilePath);
            var lockAge = (utcNow - lockFileInfo.LastWriteTimeUtc).TotalMinutes;
            
            if (lockAge < STALE_LOCK_THRESHOLD_MINUTES)
            {
                // Lock is fresh - another instance is active
                try
                {
                    // Read lock file to get run_id of active instance
                    var lockData = ReadLockFile();
                    var activeRunId = lockData?.run_id ?? "UNKNOWN";
                    
                    var canonicalInstrument = Path.GetFileNameWithoutExtension(_lockFilePath).Replace("canonical_", "");
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANONICAL_MARKET_ALREADY_ACTIVE", state: "ENGINE",
                        new
                        {
                            canonical_instrument = canonicalInstrument,
                            execution_instrument = "UNKNOWN", // Will be set by caller if available
                            run_id = _runId,
                            active_run_id = activeRunId,
                            lock_file_path = _lockFilePath,
                            lock_age_minutes = Math.Round(lockAge, 2),
                            stale_threshold_minutes = STALE_LOCK_THRESHOLD_MINUTES,
                            note = $"Another robot instance is already executing this canonical market. This instance will not start to prevent duplicate execution."
                        }));
                
                    return false; // Lock already held
                }
                catch (Exception ex)
                {
                    // If we can't read the lock file, treat as stale and reclaim
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANONICAL_MARKET_LOCK_STALE", state: "ENGINE",
                        new
                        {
                            canonical_instrument = Path.GetFileNameWithoutExtension(_lockFilePath).Replace("canonical_", ""),
                            run_id = _runId,
                            lock_file_path = _lockFilePath,
                            error = ex.Message,
                            note = "Lock file exists but could not be read - treating as stale and reclaiming"
                        }));
                    
                    // Fall through to reclaim stale lock
                }
            }
            else
            {
                // Lock is stale - reclaim it
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANONICAL_MARKET_LOCK_STALE", state: "ENGINE",
                    new
                    {
                        canonical_instrument = Path.GetFileNameWithoutExtension(_lockFilePath).Replace("canonical_", ""),
                        run_id = _runId,
                        lock_file_path = _lockFilePath,
                        lock_age_minutes = Math.Round(lockAge, 2),
                        stale_threshold_minutes = STALE_LOCK_THRESHOLD_MINUTES,
                        note = $"Lock file is stale (older than {STALE_LOCK_THRESHOLD_MINUTES} minutes) - reclaiming"
                    }));
            }
        }

        // Acquire lock: write lock file with run_id and timestamp
        try
        {
            var lockData = new LockFileData
            {
                run_id = _runId,
                acquired_at_utc = utcNow.ToString("o"),
                canonical_instrument = Path.GetFileNameWithoutExtension(_lockFilePath).Replace("canonical_", "")
            };

            var json = JsonUtil.Serialize(lockData);
            File.WriteAllText(_lockFilePath, json);
            
            _acquired = true;
            
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANONICAL_MARKET_LOCK_ACQUIRED", state: "ENGINE",
                new
                {
                    canonical_instrument = lockData.canonical_instrument,
                    run_id = _runId,
                    lock_file_path = _lockFilePath,
                    note = "Canonical market lock acquired - this instance is the single executor"
                }));
            
            return true;
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANONICAL_MARKET_LOCK_FAILED", state: "ENGINE",
                new
                {
                    canonical_instrument = Path.GetFileNameWithoutExtension(_lockFilePath).Replace("canonical_", ""),
                    run_id = _runId,
                    lock_file_path = _lockFilePath,
                    error = ex.Message,
                    error_type = ex.GetType().Name,
                    note = "Failed to acquire canonical market lock - treating as fail-closed"
                }));
            
            return false; // Fail closed if we can't acquire lock
        }
    }

    /// <summary>
    /// Release the canonical market lock (best-effort cleanup on shutdown).
    /// </summary>
    public void Release(DateTimeOffset utcNow)
    {
        if (!_acquired)
            return;

        try
        {
            if (File.Exists(_lockFilePath))
            {
                // Verify this is our lock before deleting
                var lockData = ReadLockFile();
                if (lockData != null && lockData.run_id == _runId)
                {
                    File.Delete(_lockFilePath);
                    
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANONICAL_MARKET_LOCK_RELEASED", state: "ENGINE",
                        new
                        {
                            canonical_instrument = Path.GetFileNameWithoutExtension(_lockFilePath).Replace("canonical_", ""),
                            run_id = _runId,
                            lock_file_path = _lockFilePath,
                            note = "Canonical market lock released"
                        }));
                }
            }
        }
        catch (Exception ex)
        {
            // Best-effort cleanup - log but don't throw
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANONICAL_MARKET_LOCK_RELEASE_FAILED", state: "ENGINE",
                new
                {
                    canonical_instrument = Path.GetFileNameWithoutExtension(_lockFilePath).Replace("canonical_", ""),
                    run_id = _runId,
                    lock_file_path = _lockFilePath,
                    error = ex.Message,
                    note = "Failed to release canonical market lock (non-critical - will be cleaned up on next start if stale)"
                }));
        }
        
        _acquired = false;
    }

    private LockFileData? ReadLockFile()
    {
        if (!File.Exists(_lockFilePath))
            return null;

        try
        {
            var json = File.ReadAllText(_lockFilePath);
            return JsonUtil.Deserialize<LockFileData>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        // Release lock on disposal (best-effort)
        Release(DateTimeOffset.UtcNow);
    }

    private class LockFileData
    {
        public string run_id { get; set; } = "";
        public string acquired_at_utc { get; set; } = "";
        public string canonical_instrument { get; set; } = "";
    }
}
