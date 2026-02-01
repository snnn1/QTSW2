using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace QTSW2.Robot.Core;

/// <summary>
/// Terminal state classification for streams.
/// Provides explicit classification of how a stream ended.
/// </summary>
public enum StreamTerminalState
{
    /// <summary>
    /// No trade occurred - no breakout detected before market close or other no-trade condition.
    /// </summary>
    NO_TRADE,
    
    /// <summary>
    /// Trade completed successfully (STOP or TARGET filled).
    /// </summary>
    TRADE_COMPLETED,
    
    /// <summary>
    /// Stream was skipped at timetable parse (canonical mismatch, disabled, etc.).
    /// Note: This state is set when stream is never created, not persisted in StreamJournal.
    /// </summary>
    SKIPPED_CONFIG,
    
    /// <summary>
    /// Runtime failure (stand down, corruption, protective failure, etc.).
    /// </summary>
    FAILED_RUNTIME,
    
    /// <summary>
    /// Suspended due to insufficient data.
    /// </summary>
    SUSPENDED_DATA,
    
    /// <summary>
    /// Zero bars loaded during hydration (CSV missing, BarsRequest failed, or timeout with 0 bars).
    /// Distinct from NO_TRADE (no breakout) - represents data unavailability, not market conditions.
    /// </summary>
    ZERO_BAR_HYDRATION
}

public sealed class JournalStore
{
    private readonly string _journalDir;
    private static readonly Dictionary<string, object> _fileLocks = new();
    private static readonly object _locksLock = new();

    public JournalStore(string projectRoot)
    {
        _journalDir = Path.Combine(projectRoot, "logs", "robot", "journal");
        Directory.CreateDirectory(_journalDir);
    }

    public string GetJournalPath(string tradingDate, string stream)
        => Path.Combine(_journalDir, $"{tradingDate}_{stream}.json");

    public StreamJournal? TryLoad(string tradingDate, string stream)
    {
        var path = GetJournalPath(tradingDate, stream);
        if (!File.Exists(path)) return null;
        
        // Use file lock for reads too to prevent reading while writing
        var fileLock = GetFileLock(path);
        lock (fileLock)
        {
            try
            {
                var json = File.ReadAllText(path);
                return JsonUtil.Deserialize<StreamJournal>(json);
            }
            catch (IOException)
            {
                // File might be locked by another process, return null (fail open)
                return null;
            }
        }
    }

    public void Save(StreamJournal journal)
    {
        var path = GetJournalPath(journal.TradingDate, journal.Stream);
        var json = JsonUtil.Serialize(journal);
        
        // Use per-file locking to prevent concurrent writes to the same journal file
        var fileLock = GetFileLock(path);
        lock (fileLock)
        {
            try
            {
                // Use atomic write: write to temp file, then rename (prevents partial writes)
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, json);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tempPath, path);
            }
            catch (IOException ex)
            {
                // If file is locked, retry once after a short delay
                Thread.Sleep(10);
                try
                {
                    var tempPath = path + ".tmp";
                    File.WriteAllText(tempPath, json);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    File.Move(tempPath, path);
                }
                catch
                {
                    // If still failing, log error but don't crash (fail open)
                    // Journal writes are not critical for execution
                }
            }
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

public sealed class StreamJournal
{
    public string TradingDate { get; set; } = "";

    public string Stream { get; set; } = "";

    public bool Committed { get; set; }

    public string? CommitReason { get; set; } // ENTRY_FILLED | NO_TRADE_MARKET_CLOSE | FORCED_FLATTEN

    /// <summary>
    /// Formal terminal state classification.
    /// Set on commit to provide explicit classification of how stream ended.
    /// </summary>
    public StreamTerminalState? TerminalState { get; set; }

    public string LastState { get; set; } = "";

    public string LastUpdateUtc { get; set; } = "";

    public string? TimetableHashAtCommit { get; set; }
    
    // RESTART RECOVERY: Persist order submission state
    public bool StopBracketsSubmittedAtLock { get; set; } = false;
    
    // RESTART RECOVERY: Persist entry detection state (backup to execution journal)
    public bool EntryDetected { get; set; } = false;
}

