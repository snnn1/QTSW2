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

/// <summary>
/// Slot lifecycle status - tracks slot state independent of execution state.
/// </summary>
public enum SlotStatus
{
    /// <summary>
    /// Slot is logically active (pre or post-entry).
    /// </summary>
    ACTIVE,
    
    /// <summary>
    /// Slot completed (stop/target hit).
    /// </summary>
    COMPLETE,
    
    /// <summary>
    /// Slot expired (next slot time reached).
    /// </summary>
    EXPIRED,
    
    /// <summary>
    /// No entry by market close (pre-entry only).
    /// </summary>
    NO_TRADE,
    
    /// <summary>
    /// Runtime failure (protection rejection, re-entry failure, etc.).
    /// </summary>
    FAILED_RUNTIME
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

    public string? CommitReason { get; set; } // ENTRY_FILLED | NO_TRADE_MARKET_CLOSE | FORCED_FLATTEN | SLOT_EXPIRED

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
    
    // SLOT PERSISTENCE: Slot lifecycle identity and status
    /// <summary>
    /// Stable across date rollover; defines lifecycle identity.
    /// Format: "{Stream}_{SlotTimeChicago}_{StartTradingDate}"
    /// Set once at slot start, NEVER overwritten.
    /// </summary>
    public string? SlotInstanceKey { get; set; }
    
    /// <summary>
    /// Slot lifecycle status - tracks slot state independent of execution state.
    /// </summary>
    public SlotStatus SlotStatus { get; set; } = SlotStatus.ACTIVE;
    
    /// <summary>
    /// True if forced flattened at market close (execution interruption, not slot completion).
    /// </summary>
    public bool ExecutionInterruptedByClose { get; set; }
    
    /// <summary>
    /// When forced flatten occurred (optional).
    /// </summary>
    public DateTimeOffset? ForcedFlattenTimestamp { get; set; }
    
    /// <summary>
    /// Reference to locate canonical ExecutionJournalEntry (contains bracket levels).
    /// Used ONLY to read bracket levels, NOT for re-entry order submission.
    /// </summary>
    public string? OriginalIntentId { get; set; }
    
    /// <summary>
    /// Deterministic, stable across restart.
    /// Derive from: "{SlotInstanceKey}_REENTRY" or hash-based derivation.
    /// Does NOT include TradingDate.
    /// Used for re-entry order submission idempotency (distinct from OriginalIntentId).
    /// </summary>
    public string? ReentryIntentId { get; set; }
    
    // Re-entry idempotency markers
    public bool ReentrySubmitted { get; set; }
    public bool ReentryFilled { get; set; }
    public bool ProtectionSubmitted { get; set; }
    public bool ProtectionAccepted { get; set; }
    
    /// <summary>
    /// Next occurrence of slot_time for expiry (optional, can be recomputed deterministically from SlotInstanceKey).
    /// </summary>
    public DateTimeOffset? NextSlotTimeUtc { get; set; }
    
    /// <summary>
    /// Reference to previous day's journal for carry-forward mechanism.
    /// Format: "{PreviousTradingDate}_{Stream}"
    /// Used when cloning-forward post-entry active slots across date rollover.
    /// </summary>
    public string? PriorJournalKey { get; set; }
}

