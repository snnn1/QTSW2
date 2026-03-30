using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Auditable exclusion reason for <see cref="ExecutionJournal.BuildReleaseBlockingCandidateAudit"/> (logging only).</summary>
public static class ReleaseBlockingExclusionReasons
{
    public const string NO_OPEN_QTY = "NO_OPEN_QTY";
    public const string NO_TAG = "NO_TAG";
    public const string NOT_IN_REGISTRY = "NOT_IN_REGISTRY";
    public const string DIRECTION_MISMATCH = "DIRECTION_MISMATCH";
    public const string OTHER = "OTHER";
}

/// <summary>Snapshot for RELEASE_BLOCKING_CANDIDATE_AUDIT events.</summary>
public sealed class ReleaseBlockingCandidateAuditData
{
    public int RawCandidateCount { get; init; }
    public int BlockingCandidateCount { get; init; }
    public int ExcludedCandidateCount { get; init; }
    /// <summary>64-bit blocking adoption intent set surrogate (diagnostics only; no raw ids).</summary>
    public long BlockingIntentSetHash { get; init; }
    public IReadOnlyList<string> BlockingIntentIdsSample { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedIntentIdsSample { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExclusionReasonsSample { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Execution journal for idempotency and audit trail.
/// Persists per (trading_date, stream, intent_id) to prevent double-submission.
/// </summary>
public sealed class ExecutionJournal
{
    private readonly string _journalDir;
    private readonly RobotLogger _log;
    private readonly Dictionary<string, ExecutionJournalEntry> _cache = new();
    private readonly Dictionary<string, bool> _entryFillByStream = new(); // key = "tradingDate_stream", O(1) HasEntryFillForStream
    private readonly object _lock = new object();

    /// <summary>Normalized journal instrument (entry.Instrument root) → adoption-candidate intent ids. Caller must hold <see cref="_lock"/>.</summary>
    private readonly Dictionary<string, HashSet<string>> _normInstToAdoptionCandidateIntentIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Intent id → normalized instrument bucket key for O(1) removal. Caller must hold <see cref="_lock"/>.</summary>
    private readonly Dictionary<string, string> _adoptionCandidateIntentToNormInst = new(StringComparer.OrdinalIgnoreCase);

    private bool _adoptionCandidateIndexWarmed;
    private long _adoptionCandidateIndexLookupIndexHits;
    private long _adoptionCandidateIndexLookupFallbacks;

    /// <summary>Journal bucket (trading_date segment) for durable untracked-fill recovery markers.</summary>
    public const string UntrackedFillRecoveryTradingDateBucket = "RECOVERY";

    /// <summary>Stream segment for untracked-fill recovery markers; excluded from adoption.</summary>
    public const string UntrackedFillRecoveryStream = "UNTRACKED_RECOVERY";
    
    // Callback for stream stand-down on journal corruption
    private Action<string, string, string, DateTimeOffset>? _onJournalCorruptionCallback;
    
    // Callback for recording execution costs in ExecutionSummary
    private Action<string, decimal, decimal?, decimal?>? _onExecutionCostCallback;

    private Action? _onReleaseSuppressionActivityNotify;

    /// <summary>Wired by RobotEngine to <see cref="ReleaseReconciliationRedundancySuppression.NotifyExecutionActivity"/>.</summary>
    public void SetReleaseSuppressionActivityNotify(Action? notify) =>
        _onReleaseSuppressionActivityNotify = notify;

    private void BumpReleaseSuppressionActivity() => _onReleaseSuppressionActivityNotify?.Invoke();

    public ExecutionJournal(string projectRoot, RobotLogger log)
    {
        _journalDir = Path.Combine(projectRoot, "data", "execution_journals");
        Directory.CreateDirectory(_journalDir);
        ValidateJournalDirectory();  // Phase 3.2: fail closed if not writable
        _log = log;
        try
        {
            lock (_lock)
            {
                RebuildAdoptionCandidateIndexFromDiskLocked();
            }
        }
        catch (Exception ex)
        {
            _adoptionCandidateIndexWarmed = false;
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "EXECUTION_JOURNAL_ADOPTION_INDEX_REBUILD_FAILED", "ENGINE",
                new
                {
                    warmed = false,
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    note = "Cold rebuild failed — adoption candidate lookups will use full journal scan until next successful rebuild"
                }));
        }
    }
    
    /// <summary>
    /// Phase 3.2: Startup self-check. Verifies journal dir exists and is writable.
    /// Uses unique temp file per instance to avoid race when multiple strategies start concurrently.
    /// </summary>
    private void ValidateJournalDirectory()
    {
        if (!Directory.Exists(_journalDir))
            throw new InvalidOperationException($"ExecutionJournal: journal directory does not exist: {_journalDir}");
        
        var checkPath = Path.Combine(_journalDir, $".startup_check_{Guid.NewGuid():N}");
        try
        {
            var testContent = DateTimeOffset.UtcNow.ToString("o");
            File.WriteAllText(checkPath, testContent);
            var readBack = File.ReadAllText(checkPath);
            if (readBack != testContent)
                throw new InvalidOperationException($"ExecutionJournal: startup check read-back mismatch for {_journalDir}");
            File.Delete(checkPath);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"ExecutionJournal: journal directory not writable: {_journalDir}", ex);
        }
    }
    
    /// <summary>Journal directory path (for diagnostics).</summary>
    public string JournalDirectory => _journalDir;

    /// <summary>
    /// Get journal visibility diagnostics for adoption deferral logging.
    /// Returns (directory exists, file count, directory path). FileCount is -1 on read failure.
    /// </summary>
    public (bool DirectoryExists, int FileCount, string JournalDir) GetJournalDiagnostics()
    {
        try
        {
            var exists = Directory.Exists(_journalDir);
            var files = exists ? Directory.GetFiles(_journalDir, "*.json") : Array.Empty<string>();
            return (exists, files.Length, _journalDir);
        }
        catch
        {
            return (false, -1, _journalDir);
        }
    }

    /// <summary>
    /// Set callback for journal corruption events (stream stand-down).
    /// </summary>
    public void SetJournalCorruptionCallback(Action<string, string, string, DateTimeOffset> callback)
    {
        _onJournalCorruptionCallback = callback;
    }
    
    /// <summary>
    /// Set callback for recording execution costs (slippage, commission, fees).
    /// </summary>
    public void SetExecutionCostCallback(Action<string, decimal, decimal?, decimal?> callback)
    {
        _onExecutionCostCallback = callback;
    }

    /// <summary>
    /// Compute intent ID from canonical intent fields (hash of 15 fields).
    /// </summary>
    public static string ComputeIntentId(
        string tradingDate,
        string stream,
        string instrument,
        string session,
        string slotTimeChicago,
        string? direction,
        decimal? entryPrice,
        decimal? stopPrice,
        decimal? targetPrice,
        decimal? beTrigger)
    {
        var canonical = $"{tradingDate}|{stream}|{instrument}|{session}|{slotTimeChicago}|{direction ?? "NULL"}|{entryPrice?.ToString("F2") ?? "NULL"}|{stopPrice?.ToString("F2") ?? "NULL"}|{targetPrice?.ToString("F2") ?? "NULL"}|{beTrigger?.ToString("F2") ?? "NULL"}";
        byte[] hash;
        using (var sha256 = SHA256.Create())
        {
            hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        }
        var hexString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        return hexString.Substring(0, 16); // Use first 16 chars as ID
    }

    /// <summary>
    /// Check if intent was already submitted (idempotency check).
    /// </summary>
    public bool IsIntentSubmitted(string intentId, string tradingDate, string stream)
    {
        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            
            if (_cache.TryGetValue(key, out var entry))
            {
                return entry.EntrySubmitted || entry.EntryFilled;
            }

            // Check disk
            var journalPath = GetJournalPath(tradingDate, stream, intentId);
            if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    var diskEntry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (diskEntry != null)
                    {
                        _cache[key] = diskEntry;
                        if (TryParseIntentIdFromCacheKey(key, out var sid))
                            SyncAdoptionCandidateIndexForIntentLocked(sid, diskEntry);
                        return diskEntry.EntrySubmitted || diskEntry.EntryFilled;
                    }
                }
                catch (Exception ex)
                {
                    // CRITICAL FIX: Fail-closed on journal corruption
                    // Journal corruption is actionable - stand down stream to prevent duplicate submissions
                    var utcNow = DateTimeOffset.UtcNow;
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_CORRUPTION", "ENGINE",
                        new
                        {
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            intent_id = intentId,
                            stream = stream,
                            trading_date = tradingDate,
                            journal_path = journalPath,
                            action = "STREAM_STAND_DOWN",
                            note = "Journal corruption - standing down stream to prevent duplicate submissions (fail-closed)"
                        }));
                    
                    // Stand down stream
                    _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                    
                    // Return true (treat as submitted) to prevent duplicate submission
                    return true; // Fail-closed: assume submitted to prevent duplicates
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Record order submission attempt.
    /// </summary>
    public void RecordSubmission(
        string intentId,
        string tradingDate,
        string stream,
        string instrument,
        string orderType,
        string? brokerOrderId,
        DateTimeOffset utcNow,
        decimal? expectedEntryPrice = null,
        decimal? entryPrice = null,
        decimal? stopPrice = null,
        decimal? targetPrice = null,
        string? direction = null,
        string? ocoGroup = null)
    {
        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            ExecutionJournalEntry entry;
            if (_cache.TryGetValue(key, out var existing))
            {
                entry = existing;
            }
            else if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json) ?? new ExecutionJournalEntry();
                }
                catch (Exception ex)
                {
                    // CRITICAL FIX: Fail-closed on journal corruption
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_CORRUPTION", "ENGINE",
                        new
                        {
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            intent_id = intentId,
                            stream = stream,
                            trading_date = tradingDate,
                            journal_path = journalPath,
                            action = "STREAM_STAND_DOWN",
                            note = "Journal corruption during RecordSubmission - standing down stream (fail-closed)"
                        }));
                    
                    // Stand down stream
                    _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                    
                    // Return early - don't record submission to prevent duplicates
                    return;
                }
            }
            else
            {
                entry = new ExecutionJournalEntry
                {
                    IntentId = intentId,
                    TradingDate = tradingDate,
                    Stream = stream,
                    Instrument = instrument
                };
            }

            // Ensure TradingDate and Stream are set (may be empty strings from old call sites)
            if (string.IsNullOrWhiteSpace(entry.TradingDate) && !string.IsNullOrWhiteSpace(tradingDate))
            {
                entry.TradingDate = tradingDate;
            }
            if (string.IsNullOrWhiteSpace(entry.Stream) && !string.IsNullOrWhiteSpace(stream))
            {
                entry.Stream = stream;
            }
            if (string.IsNullOrWhiteSpace(entry.Instrument) && !string.IsNullOrWhiteSpace(instrument))
            {
                entry.Instrument = instrument;
            }

            entry.EntrySubmitted = true;
            entry.EntrySubmittedAt = utcNow.ToString("o");
            entry.BrokerOrderId = brokerOrderId;

            if (orderType == "ENTRY" || orderType == "ENTRY_STOP")
            {
                entry.EntryOrderType = orderType;
                // Store expected entry price for slippage calculation
                if (expectedEntryPrice.HasValue)
                {
                    entry.ExpectedEntryPrice = expectedEntryPrice.Value;
                }
            }

            // Store intent prices (only if not already set to preserve existing values)
            if (entryPrice.HasValue && !entry.EntryPrice.HasValue)
            {
                entry.EntryPrice = entryPrice.Value;
            }
            if (stopPrice.HasValue && !entry.StopPrice.HasValue)
            {
                entry.StopPrice = stopPrice.Value;
            }
            if (targetPrice.HasValue && !entry.TargetPrice.HasValue)
            {
                entry.TargetPrice = targetPrice.Value;
            }
            if (!string.IsNullOrWhiteSpace(direction) && string.IsNullOrWhiteSpace(entry.Direction))
            {
                entry.Direction = direction;
            }
            if (!string.IsNullOrWhiteSpace(ocoGroup) && string.IsNullOrWhiteSpace(entry.OcoGroup))
            {
                entry.OcoGroup = ocoGroup;
            }

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
        }
    }

    /// <summary>
    /// Record order fill.
    /// DEPRECATED: Use RecordEntryFill or RecordExitFill instead.
    /// This method is kept for backwards compatibility but should not be used for new code.
    /// It only handles entry fills - exit fills must use RecordExitFill.
    /// </summary>
    [Obsolete("Use RecordEntryFill or RecordExitFill instead. This method only handles entry fills.")]
    public void RecordFill(
        string intentId,
        string tradingDate,
        string stream,
        decimal fillPrice,
        int fillQuantity,
        DateTimeOffset utcNow,
        decimal? contractMultiplier = null)
    {
        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            if (!_cache.TryGetValue(key, out var entry))
            {
                if (File.Exists(journalPath))
                {
                    try
                    {
                        var json = File.ReadAllText(journalPath);
                        entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json) ?? new ExecutionJournalEntry();
                    }
                    catch (Exception ex)
                    {
                        // CRITICAL FIX: Fail-closed on journal corruption
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_CORRUPTION", "ENGINE",
                            new
                            {
                                error = ex.Message,
                                exception_type = ex.GetType().Name,
                                intent_id = intentId,
                                stream = stream,
                                trading_date = tradingDate,
                                journal_path = journalPath,
                                action = "STREAM_STAND_DOWN",
                                note = "Journal corruption during RecordFill - standing down stream (fail-closed)"
                            }));
                        
                        // Stand down stream
                        _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                        
                        // Return early - don't record fill to prevent duplicates
                        return;
                    }
                }
                else
                {
                    entry = new ExecutionJournalEntry();
                }
            }

            entry.EntryFilled = true;
            entry.EntryFilledAt = utcNow.ToString("o");
            entry.FillPrice = fillPrice;
            entry.FillQuantity = fillQuantity;
            entry.ActualFillPrice = fillPrice; // Store for slippage calculation

            // Calculate slippage if expected price is available
            if (entry.ExpectedEntryPrice.HasValue)
            {
                entry.SlippagePoints = fillPrice - entry.ExpectedEntryPrice.Value;
                
                // Calculate slippage in dollars if contract multiplier is provided
                if (contractMultiplier.HasValue && fillQuantity > 0)
                {
                    entry.SlippageDollars = entry.SlippagePoints.Value * contractMultiplier.Value * fillQuantity;
                }
                
                // Log slippage event
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_SLIPPAGE_DETECTED", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        stream = stream,
                        instrument = entry.Instrument,
                        expected_entry_price = entry.ExpectedEntryPrice.Value,
                        actual_fill_price = fillPrice,
                        slippage_points = entry.SlippagePoints.Value,
                        slippage_dollars = entry.SlippageDollars,
                        fill_quantity = fillQuantity,
                        contract_multiplier = contractMultiplier
                    }));
                
                // Record cost in ExecutionSummary via callback
                if (entry.SlippageDollars.HasValue)
                {
                    _onExecutionCostCallback?.Invoke(intentId, entry.SlippageDollars.Value, entry.Commission, entry.Fees);
                }
            }

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            _entryFillByStream[$"{tradingDate}_{stream}"] = true;
        }
    }

    /// <summary>
    /// Record entry fill (delta-based, idempotent, cumulative).
    /// Accepts delta fillQuantity only, NOT cumulative filledTotal.
    /// </summary>
    /// <param name="brokerOrderInstrumentKey">Optional. Instrument from the broker order (e.g. "M2K 03-26").
    /// When provided, invariant check: journal instrument must equal execution instrument (root).
    /// Mismatch → CRITICAL JOURNAL_INSTRUMENT_KEY_MISMATCH and fail-closed.</param>
    public void RecordEntryFill(
        string intentId,
        string tradingDate,
        string stream,
        decimal fillPrice,
        int fillQuantity,  // DELTA ONLY - this fill's quantity (will be added to cumulative)
        DateTimeOffset utcNow,
        decimal contractMultiplier,
        string direction,
        string executionInstrument,
        string canonicalInstrument,
        string? brokerOrderInstrumentKey = null)
    {
        var cacheHit = false;
        WithLockTiming("RecordEntryFill", tradingDate, () =>
        {
            // INVARIANT: Journal instrument must equal execution instrument (broker position key).
            // Prevents silent divergence if canonical instrument is mistakenly used.
            if (!string.IsNullOrWhiteSpace(brokerOrderInstrumentKey) && !string.IsNullOrWhiteSpace(executionInstrument))
            {
                var brokerRoot = GetInstrumentRoot(brokerOrderInstrumentKey);
                if (!string.Equals(brokerRoot, executionInstrument.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "JOURNAL_INSTRUMENT_KEY_MISMATCH", "CRITICAL",
                        new
                        {
                            intent_id = intentId,
                            stream = stream,
                            journal_instrument = executionInstrument,
                            broker_order_instrument = brokerOrderInstrumentKey,
                            broker_root = brokerRoot,
                            action = "STREAM_STAND_DOWN",
                            note = "Journal instrument must equal execution instrument. Canonical instrument usage would cause reconciliation mismatch. Fail-closed."
                        }));
                    _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                    return; // Fail-closed: do not persist
                }
            }

            // CRITICAL: Validate key fields - reject empty/whitespace
            if (string.IsNullOrWhiteSpace(tradingDate))
            {
                _log.Write(RobotEvents.EngineBase(utcNow, "", "EXECUTION_JOURNAL_VALIDATION_FAILED", "ENGINE",
                    new
                    {
                        error = "tradingDate is empty or whitespace",
                        intent_id = intentId,
                        stream = stream,
                        action = "BLOCK_EXECUTION",
                        note = "Persistence rejects empty tradingDate - fail-closed"
                    }));
                return; // Fail-closed: do not persist
            }
            
            if (string.IsNullOrWhiteSpace(stream))
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_VALIDATION_FAILED", "ENGINE",
                    new
                    {
                        error = "stream is empty or whitespace",
                        intent_id = intentId,
                        trading_date = tradingDate,
                        action = "BLOCK_EXECUTION",
                        note = "Persistence rejects empty stream - fail-closed"
                    }));
                return; // Fail-closed: do not persist
            }
            
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);
            
            ExecutionJournalEntry entry;
            if (_cache.TryGetValue(key, out var existing))
            {
                cacheHit = true;
                entry = existing;
            }
            else if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json) ?? new ExecutionJournalEntry();
                    // Ensure identity fields are set
                    if (string.IsNullOrEmpty(entry.IntentId))
                        entry.IntentId = intentId;
                    if (string.IsNullOrEmpty(entry.TradingDate))
                        entry.TradingDate = tradingDate;
                    if (string.IsNullOrEmpty(entry.Stream))
                        entry.Stream = stream;
                    if (string.IsNullOrEmpty(entry.Instrument))
                        entry.Instrument = executionInstrument;
                }
                catch (Exception ex)
                {
                    // CRITICAL FIX: Fail-closed on journal corruption
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_CORRUPTION", "ENGINE",
                        new
                        {
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            intent_id = intentId,
                            stream = stream,
                            trading_date = tradingDate,
                            journal_path = journalPath,
                            action = "STREAM_STAND_DOWN",
                            note = "Journal corruption during RecordEntryFill - standing down stream (fail-closed)"
                        }));
                    
                    // Stand down stream
                    _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                    
                    // Return early - don't record fill to prevent duplicates
                    return;
                }
            }
            else
            {
                entry = new ExecutionJournalEntry
                {
                    IntentId = intentId,
                    TradingDate = tradingDate,
                    Stream = stream,
                    Instrument = executionInstrument
                };
            }
            
            // Normalize direction (ensure consistent casing: "Long" or "Short")
            var normalizedDirection = direction?.Trim();
            if (!string.IsNullOrEmpty(normalizedDirection))
            {
                normalizedDirection = char.ToUpperInvariant(normalizedDirection[0]) + normalizedDirection.Substring(1).ToLowerInvariant();
            }
            
            // Validate direction consistency (if already set)
            if (!string.IsNullOrEmpty(entry.Direction) && 
                !string.Equals(entry.Direction, normalizedDirection, StringComparison.OrdinalIgnoreCase))
            {
                // CRITICAL: Direction mismatch - invariant violation
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_INVARIANT_VIOLATION", "ENGINE",
                    new
                    {
                        error = "Direction mismatch",
                        intent_id = intentId,
                        stream = stream,
                        existing_direction = entry.Direction,
                        new_direction = normalizedDirection,
                        action = "BLOCK_EXECUTION",
                        note = "Direction changed mid-trade - invariant violation"
                    }));
                return; // Fail-closed
            }
            
            // Validate contract multiplier consistency (if already set)
            if (entry.ContractMultiplier.HasValue && entry.ContractMultiplier.Value != contractMultiplier)
            {
                // CRITICAL: Contract multiplier mismatch - invariant violation
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_INVARIANT_VIOLATION", "ENGINE",
                    new
                    {
                        error = "Contract multiplier mismatch",
                        intent_id = intentId,
                        stream = stream,
                        existing_multiplier = entry.ContractMultiplier.Value,
                        new_multiplier = contractMultiplier,
                        action = "BLOCK_EXECUTION",
                        note = "Contract multiplier changed mid-trade - invariant violation"
                    }));
                return; // Fail-closed
            }
            
            // Update cumulative entry quantities (delta-based accumulation)
            entry.EntryFilledQuantityTotal += fillQuantity;
            entry.EntryFillNotional = (entry.EntryFillNotional ?? 0) + (fillPrice * fillQuantity);
            entry.EntryAvgFillPrice = entry.EntryFillNotional.Value / entry.EntryFilledQuantityTotal;
            
            // Timestamp policy: Store first entry fill time only
            if (string.IsNullOrEmpty(entry.EntryFilledAtUtc))
            {
                entry.EntryFilledAtUtc = utcNow.ToString("o");
            }
            
            // Persist direction + multiplier on first entry fill (use normalized direction)
            if (string.IsNullOrEmpty(entry.Direction))
            {
                entry.Direction = normalizedDirection ?? direction; // Use normalized if available
            }
            if (!entry.ContractMultiplier.HasValue)
            {
                entry.ContractMultiplier = contractMultiplier;
            }
            
            // Store intent prices if not already set (preserve existing values)
            // These may have been set by RecordSubmission, but if not, preserve null
            // Note: Intent prices should ideally be set at submission time, but we don't block here
            
            // Legacy fields for backwards compatibility
            entry.EntryFilled = true;
            if (string.IsNullOrEmpty(entry.EntryFilledAt))
            {
                entry.EntryFilledAt = utcNow.ToString("o");
            }
            entry.FillPrice = fillPrice;
            entry.FillQuantity = entry.EntryFilledQuantityTotal; // Store cumulative for legacy compatibility
            entry.ActualFillPrice = fillPrice;
            
            // Calculate slippage if expected price is available
            if (entry.ExpectedEntryPrice.HasValue)
            {
                entry.SlippagePoints = fillPrice - entry.ExpectedEntryPrice.Value;
                
                // Calculate slippage in dollars
                if (fillQuantity > 0)
                {
                    entry.SlippageDollars = (entry.SlippageDollars ?? 0) + (entry.SlippagePoints.Value * contractMultiplier * fillQuantity);
                }
                
                // Log slippage event
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_SLIPPAGE_DETECTED", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        stream = stream,
                        instrument = entry.Instrument,
                        expected_entry_price = entry.ExpectedEntryPrice.Value,
                        actual_fill_price = fillPrice,
                        slippage_points = entry.SlippagePoints.Value,
                        slippage_dollars = entry.SlippageDollars,
                        fill_quantity = fillQuantity,
                        cumulative_entry_qty = entry.EntryFilledQuantityTotal,
                        contract_multiplier = contractMultiplier
                    }));
                
                // Record cost in ExecutionSummary via callback
                if (entry.SlippageDollars.HasValue)
                {
                    _onExecutionCostCallback?.Invoke(intentId, entry.SlippageDollars.Value, entry.Commission, entry.Fees);
                }
            }
            
            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            _entryFillByStream[$"{tradingDate}_{stream}"] = true;
        }, () => new { cache_hit = cacheHit });
    }
    
    /// <summary>
    /// Record exit fill (delta-based, completion-gated).
    /// Accepts delta exitFillQuantity only, NOT cumulative total.
    /// Computes P&L only when ExitFilledQuantityTotal == EntryFilledQuantityTotal.
    /// </summary>
    public void RecordExitFill(
        string intentId,
        string tradingDate,
        string stream,
        decimal exitFillPrice,
        int exitFillQuantity,  // DELTA ONLY - this fill's quantity
        string exitOrderType,  // "STOP", "TARGET", "EMERGENCY", "MANUAL"
        DateTimeOffset utcNow,
        string? slotInstanceKey = null)  // Optional: slot_instance_key for health sink granularity
    {
        var cacheHit = false;
        WithLockTiming("RecordExitFill", tradingDate, () =>
        {
            // CRITICAL: Validate key fields - reject empty/whitespace
            if (string.IsNullOrWhiteSpace(tradingDate))
            {
                _log.Write(RobotEvents.EngineBase(utcNow, "", "EXECUTION_JOURNAL_VALIDATION_FAILED", "ENGINE",
                    new
                    {
                        error = "tradingDate is empty or whitespace",
                        intent_id = intentId,
                        stream = stream,
                        action = "BLOCK_EXECUTION",
                        note = "Persistence rejects empty tradingDate - fail-closed"
                    }));
                return; // Fail-closed: do not persist
            }
            
            if (string.IsNullOrWhiteSpace(stream))
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_VALIDATION_FAILED", "ENGINE",
                    new
                    {
                        error = "stream is empty or whitespace",
                        intent_id = intentId,
                        trading_date = tradingDate,
                        action = "BLOCK_EXECUTION",
                        note = "Persistence rejects empty stream - fail-closed"
                    }));
                return; // Fail-closed: do not persist
            }
            
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);
            
            ExecutionJournalEntry entry;
            if (_cache.TryGetValue(key, out var existing))
            {
                cacheHit = true;
                entry = existing;
            }
            else if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry == null)
                    {
                        // CRITICAL: Entry missing when exit occurs
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_ENTRY_MISSING", "ENGINE",
                            new
                            {
                                error = "Journal entry missing when exit fill occurred",
                                intent_id = intentId,
                                stream = stream,
                                trading_date = tradingDate,
                                exit_fill_price = exitFillPrice,
                                exit_fill_quantity = exitFillQuantity,
                                exit_order_type = exitOrderType,
                                action = "STREAM_STAND_DOWN",
                                note = "Entry fill missing when exit occurs - cannot calculate P&L deterministically"
                            }));
                        
                        // Stand down stream
                        _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                        return; // Fail-closed
                    }
                }
                catch (Exception ex)
                {
                    // CRITICAL FIX: Fail-closed on journal corruption
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_CORRUPTION", "ENGINE",
                        new
                        {
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            intent_id = intentId,
                            stream = stream,
                            trading_date = tradingDate,
                            journal_path = journalPath,
                            action = "STREAM_STAND_DOWN",
                            note = "Journal corruption during RecordExitFill - standing down stream (fail-closed)"
                        }));
                    
                    // Stand down stream
                    _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                    
                    // Return early - don't record fill to prevent duplicates
                    return;
                }
            }
            else
            {
                // CRITICAL: Entry missing when exit occurs
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_ENTRY_MISSING", "ENGINE",
                    new
                    {
                        error = "Journal entry missing when exit fill occurred",
                        intent_id = intentId,
                        stream = stream,
                        trading_date = tradingDate,
                        exit_fill_price = exitFillPrice,
                        exit_fill_quantity = exitFillQuantity,
                        exit_order_type = exitOrderType,
                        action = "STREAM_STAND_DOWN",
                        note = "Entry fill missing when exit occurs - cannot calculate P&L deterministically"
                    }));
                
                // Stand down stream
                _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                return; // Fail-closed
            }
            
            // Validate entry prerequisites
            if (entry.EntryFilledQuantityTotal <= 0)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_INVALID_STATE", "ENGINE",
                    new
                    {
                        error = "EntryFilledQuantityTotal <= 0 when exit fill occurred",
                        intent_id = intentId,
                        stream = stream,
                        entry_filled_qty = entry.EntryFilledQuantityTotal,
                        action = "STREAM_STAND_DOWN",
                        note = "Cannot calculate P&L - entry quantity invalid"
                    }));
                
                // Stand down stream
                _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                return; // Fail-closed
            }
            
            if (string.IsNullOrEmpty(entry.Direction))
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_INVALID_STATE", "ENGINE",
                    new
                    {
                        error = "Direction missing when exit fill occurred",
                        intent_id = intentId,
                        stream = stream,
                        action = "STREAM_STAND_DOWN",
                        note = "Cannot calculate P&L deterministically - direction missing"
                    }));
                
                // Stand down stream
                _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                return; // Fail-closed
            }
            
            if (!entry.ContractMultiplier.HasValue)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_INVALID_STATE", "ENGINE",
                    new
                    {
                        error = "ContractMultiplier missing when exit fill occurred",
                        intent_id = intentId,
                        stream = stream,
                        action = "STREAM_STAND_DOWN",
                        note = "Cannot calculate P&L deterministically - contract multiplier missing"
                    }));
                
                // Stand down stream
                _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                return; // Fail-closed
            }
            
            // Update exit cumulatives (delta-based accumulation)
            entry.ExitFilledQuantityTotal += exitFillQuantity;
            entry.ExitFillNotional = (entry.ExitFillNotional ?? 0) + (exitFillPrice * exitFillQuantity);
            entry.ExitAvgFillPrice = entry.ExitFillNotional.Value / entry.ExitFilledQuantityTotal;
            
            // Timestamp policy: Store first exit fill time only
            if (string.IsNullOrEmpty(entry.ExitFilledAtUtc))
            {
                entry.ExitFilledAtUtc = utcNow.ToString("o");
            }
            
            // ExitOrderType: Store first exit order type
            if (string.IsNullOrEmpty(entry.ExitOrderType))
            {
                entry.ExitOrderType = exitOrderType;
            }
            else if (entry.ExitOrderType != exitOrderType)
            {
                // Different exit type arrived - set CompletionReason to EMERGENCY_OVERRIDE
                entry.CompletionReason = "EMERGENCY_OVERRIDE";
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_EXIT_TYPE_CHANGE", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        stream = stream,
                        first_exit_type = entry.ExitOrderType,
                        new_exit_type = exitOrderType,
                        note = "Exit order type changed - setting CompletionReason to EMERGENCY_OVERRIDE"
                    }));
            }
            
            // Completion logic
            if (entry.ExitFilledQuantityTotal < entry.EntryFilledQuantityTotal)
            {
                // Partial exit - NOT completed
                // Do NOT compute final P&L yet
            }
            else if (entry.ExitFilledQuantityTotal == entry.EntryFilledQuantityTotal)
            {
                // CRITICAL: Guard against duplicate TRADE_COMPLETED emission
                // If trade already completed, skip emission (idempotency)
                if (entry.TradeCompleted)
                {
                    // Trade already completed - skip duplicate emission
                    // This prevents duplicate events after restart/replay
                    _cache[key] = entry;
                    SaveJournal(journalPath, entry);
                    return;
                }
                
                // Complete - compute P&L
                var direction = entry.Direction;
                var contractMultiplier = entry.ContractMultiplier.Value;
                var entryAvg = entry.EntryAvgFillPrice.Value;
                var exitAvg = entry.ExitAvgFillPrice.Value;
                
                // Normalize direction for comparison (case-insensitive)
                var normalizedDirection = direction?.Trim();
                if (!string.IsNullOrEmpty(normalizedDirection))
                {
                    normalizedDirection = char.ToUpperInvariant(normalizedDirection[0]) + normalizedDirection.Substring(1).ToLowerInvariant();
                }
                
                // Calculate points (sign by direction)
                decimal points;
                if (string.Equals(normalizedDirection, "Long", StringComparison.OrdinalIgnoreCase))
                {
                    points = exitAvg - entryAvg;
                }
                else if (string.Equals(normalizedDirection, "Short", StringComparison.OrdinalIgnoreCase))
                {
                    points = entryAvg - exitAvg;
                }
                else
                {
                    // Invalid direction
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_INVALID_DIRECTION", "ENGINE",
                        new
                        {
                            error = $"Invalid direction: {direction}",
                            intent_id = intentId,
                            stream = stream,
                            action = "STREAM_STAND_DOWN",
                            note = "Cannot calculate P&L - invalid direction"
                        }));
                    
                    // Stand down stream
                    _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                    return; // Fail-closed
                }
                
                // Calculate gross dollars
                var grossDollars = points * entry.EntryFilledQuantityTotal * contractMultiplier;
                
                // Calculate net dollars (only subtract populated values)
                decimal netDollars = grossDollars;
                if (entry.SlippageDollars.HasValue)
                {
                    netDollars -= entry.SlippageDollars.Value;
                }
                if (entry.Commission.HasValue)
                {
                    netDollars -= entry.Commission.Value;
                }
                if (entry.Fees.HasValue)
                {
                    netDollars -= entry.Fees.Value;
                }
                
                // Store P&L
                entry.RealizedPnLPoints = points;
                entry.RealizedPnLGross = grossDollars;
                entry.RealizedPnLNet = netDollars;
                entry.TradeCompleted = true;
                entry.CompletedAtUtc = utcNow.ToString("o");
                if (string.IsNullOrEmpty(entry.CompletionReason))
                {
                    entry.CompletionReason = exitOrderType;
                }
                
                // Log trade completion with enhanced fields
                var tradeData = new Dictionary<string, object?>
                {
                    ["intent_id"] = intentId,
                    ["stream"] = stream,
                    ["instrument"] = entry.Instrument,
                    ["direction"] = direction,
                    ["entry_avg_price"] = entryAvg,
                    ["exit_avg_price"] = exitAvg,
                    ["entry_qty"] = entry.EntryFilledQuantityTotal,
                    ["exit_qty"] = entry.ExitFilledQuantityTotal,
                    ["realized_pnl_points"] = points,
                    ["realized_pnl_gross"] = grossDollars,
                    ["realized_pnl_net"] = netDollars,
                    ["completion_reason"] = entry.CompletionReason,
                    ["exit_reason"] = entry.CompletionReason, // Alias for consistency
                    ["contract_multiplier"] = contractMultiplier
                };
                
                // Add entry/exit times if available
                if (!string.IsNullOrWhiteSpace(entry.EntryFilledAt))
                {
                    tradeData["entry_time_utc"] = entry.EntryFilledAt;
                    if (DateTimeOffset.TryParse(entry.EntryFilledAt, out var entryTime))
                    {
                        tradeData["entry_time_chicago"] = TimeService.ConvertUtcToChicagoStatic(entryTime).ToString("o");
                    }
                }
                
                // Use ExitFilledAtUtc (the field name in ExecutionJournalEntry)
                var exitTimeStr = entry.ExitFilledAtUtc;
                if (!string.IsNullOrWhiteSpace(exitTimeStr))
                {
                    tradeData["exit_time_utc"] = exitTimeStr;
                    if (DateTimeOffset.TryParse(exitTimeStr, out var exitTime))
                    {
                        tradeData["exit_time_chicago"] = TimeService.ConvertUtcToChicagoStatic(exitTime).ToString("o");
                    }
                }
                
                // Add commission and fees if available
                if (entry.Commission.HasValue)
                {
                    tradeData["commission"] = entry.Commission.Value;
                }
                
                if (entry.Fees.HasValue)
                {
                    tradeData["fees"] = entry.Fees.Value;
                }
                
                // Calculate time in trade if both times available
                var entryTimeStr = entry.EntryFilledAt;
                if (!string.IsNullOrWhiteSpace(entryTimeStr) && !string.IsNullOrWhiteSpace(exitTimeStr))
                {
                    if (DateTimeOffset.TryParse(entryTimeStr, out var entryTimeParsed) &&
                        DateTimeOffset.TryParse(exitTimeStr, out var exitTimeParsed))
                    {
                        var timeInTrade = exitTimeParsed - entryTimeParsed;
                        tradeData["time_in_trade_minutes"] = (int)timeInTrade.TotalMinutes;
                        tradeData["time_in_trade_seconds"] = (int)timeInTrade.TotalSeconds;
                    }
                }
                
                // Include slot_instance_key if provided (for health sink path granularity)
                if (!string.IsNullOrWhiteSpace(slotInstanceKey))
                {
                    tradeData["slot_instance_key"] = slotInstanceKey;
                }
                
                // Use RobotEvents.Base instead of EngineBase for proper stream context
                // RobotEvents.Base signature: (TimeService?, DateTimeOffset, string?, string?, string?, string?, string?, DateTimeOffset?, string, string, Dictionary<string, object?>?)
                // Note: TimeService is required, but ExecutionJournal doesn't have access to it. Use a temporary instance.
                var tempTime = new TimeService("America/Chicago");
                _log.Write(RobotEvents.Base(tempTime, utcNow, tradingDate, stream, entry.Instrument ?? "", null, null, null, "TRADE_COMPLETED", "Trade completed", tradeData));
            }
            else
            {
                // Overfill: ExitFilledQuantityTotal > EntryFilledQuantityTotal
                // Trigger overfill emergency
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_OVERFILL", "ENGINE",
                    new
                    {
                        error = "Exit quantity exceeds entry quantity",
                        intent_id = intentId,
                        stream = stream,
                        entry_filled_qty = entry.EntryFilledQuantityTotal,
                        exit_filled_qty = entry.ExitFilledQuantityTotal,
                        action = "STREAM_STAND_DOWN",
                        note = "Overfill detected - exit quantity > entry quantity"
                    }));
                
                // Stand down stream
                _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                return; // Fail-closed
            }
            
            _cache[key] = entry;
            SaveJournal(journalPath, entry);
        }, () => new { cache_hit = cacheHit });
    }

    /// <summary>
    /// Record order rejection.
    /// </summary>
    public void RecordRejection(
        string intentId,
        string tradingDate,
        string stream,
        string reason,
        DateTimeOffset utcNow,
        string? orderType = null,
        decimal? rejectedPrice = null,
        int? rejectedQuantity = null)
    {
        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            if (!_cache.TryGetValue(key, out var entry))
            {
                if (File.Exists(journalPath))
                {
                    try
                    {
                        var json = File.ReadAllText(journalPath);
                        entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json) ?? new ExecutionJournalEntry();
                    }
                    catch (Exception ex)
                    {
                        // CRITICAL FIX: Fail-closed on journal corruption
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_CORRUPTION", "ENGINE",
                            new
                            {
                                error = ex.Message,
                                exception_type = ex.GetType().Name,
                                intent_id = intentId,
                                stream = stream,
                                trading_date = tradingDate,
                                journal_path = journalPath,
                                action = "STREAM_STAND_DOWN",
                                note = "Journal corruption during RecordRejection - standing down stream (fail-closed)"
                            }));
                        
                        // Stand down stream
                        _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                        
                        // Return early - don't record rejection to prevent duplicates
                        return;
                    }
                }
                else
                {
                    entry = new ExecutionJournalEntry();
                }
            }

            entry.Rejected = true;
            entry.RejectedAt = utcNow.ToString("o");
            entry.RejectionReason = reason;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
        }
    }

    /// <summary>
    /// Record BE modification attempt.
    /// </summary>
    public void RecordBEModification(
        string intentId,
        string tradingDate,
        string stream,
        decimal beStopPrice,
        DateTimeOffset utcNow,
        decimal? previousStopPrice = null,
        decimal? beTriggerPrice = null,
        decimal? entryPrice = null)
    {
        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            if (!_cache.TryGetValue(key, out var entry))
            {
                if (File.Exists(journalPath))
                {
                    try
                    {
                        var json = File.ReadAllText(journalPath);
                        entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json) ?? new ExecutionJournalEntry();
                    }
                    catch (Exception ex)
                    {
                        // CRITICAL FIX: Fail-closed on journal corruption
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_CORRUPTION", "ENGINE",
                            new
                            {
                                error = ex.Message,
                                exception_type = ex.GetType().Name,
                                intent_id = intentId,
                                stream = stream,
                                trading_date = tradingDate,
                                journal_path = journalPath,
                                action = "STREAM_STAND_DOWN",
                                note = "Journal corruption during RecordBEModification - standing down stream (fail-closed)"
                            }));
                        
                        // Stand down stream
                        _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                        
                        // Return early - don't record BE modification to prevent duplicates
                        return;
                    }
                }
                else
                {
                    entry = new ExecutionJournalEntry();
                }
            }

            // Ensure TradingDate and Stream are set
            if (string.IsNullOrWhiteSpace(entry.TradingDate) && !string.IsNullOrWhiteSpace(tradingDate))
            {
                entry.TradingDate = tradingDate;
            }
            if (string.IsNullOrWhiteSpace(entry.Stream) && !string.IsNullOrWhiteSpace(stream))
            {
                entry.Stream = stream;
            }

            entry.BEModified = true;
            entry.BEModifiedAt = utcNow.ToString("o");
            entry.BEStopPrice = beStopPrice;
            
            // Store BE modification context (only if not already set)
            if (previousStopPrice.HasValue && !entry.PreviousStopPrice.HasValue)
            {
                entry.PreviousStopPrice = previousStopPrice.Value;
            }
            if (beTriggerPrice.HasValue && !entry.BETriggerPrice.HasValue)
            {
                entry.BETriggerPrice = beTriggerPrice.Value;
            }
            if (entryPrice.HasValue && !entry.EntryPrice.HasValue)
            {
                entry.EntryPrice = entryPrice.Value;
            }

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
        }
    }

    /// <summary>
    /// Check if BE modification was already attempted (prevent duplicates).
    /// </summary>
    public bool IsBEModified(string intentId, string tradingDate, string stream)
    {
        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            
            if (_cache.TryGetValue(key, out var entry))
            {
                return entry.BEModified;
            }

            var journalPath = GetJournalPath(tradingDate, stream, intentId);
            if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    var diskEntry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (diskEntry != null)
                    {
                        _cache[key] = diskEntry;
                        return diskEntry.BEModified;
                    }
                }
                catch (Exception ex)
                {
                    // CRITICAL FIX: Fail-closed on journal corruption
                    var utcNow = DateTimeOffset.UtcNow;
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_CORRUPTION", "ENGINE",
                        new
                        {
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            intent_id = intentId,
                            stream = stream,
                            trading_date = tradingDate,
                            journal_path = journalPath,
                            action = "STREAM_STAND_DOWN",
                            note = "Journal corruption during IsBEModified check - standing down stream (fail-closed)"
                        }));
                    
                    // Stand down stream
                    _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                    
                    // Return true (treat as modified) to prevent duplicate BE modification
                    return true; // Fail-closed: assume modified to prevent duplicates
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Check if any entry intent was filled for the given stream and trading day.
    /// Used for restoring _entryDetected flag on restart.
    /// </summary>
    public bool HasEntryFillForStream(string tradingDate, string stream)
    {
        lock (_lock)
        {
            var cacheKey = $"{tradingDate}_{stream}";
            if (_entryFillByStream.TryGetValue(cacheKey, out var cached))
                return cached;

            // Cache miss: scan journal directory for entries matching tradingDate_stream_*
            var pattern = $"{tradingDate}_{stream}_*.json";
            var result = false;
            try
            {
                if (!Directory.Exists(_journalDir)) return false;

                var files = Directory.GetFiles(_journalDir, pattern);
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                        if (entry != null)
                        {
                            if (entry.EntryFilled || entry.EntryFilledQuantityTotal > 0)
                            {
                                result = true;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Skip corrupted files, continue scanning
                    }
                }
                _entryFillByStream[cacheKey] = result;
            }
            catch
            {
                // Fail-safe: return false on error (assume no fill)
                return false;
            }

            return result;
        }
    }

    /// <summary>
    /// Check if any trade was completed for the given stream and trading day.
    /// Used for determining terminal state (TRADE_COMPLETED vs NO_TRADE).
    /// </summary>
    public bool HasCompletedTradeForStream(string tradingDate, string stream)
    {
        lock (_lock)
        {
            // Scan journal directory for entries matching tradingDate_stream_*
            var pattern = $"{tradingDate}_{stream}_*.json";
            
            try
            {
                if (!Directory.Exists(_journalDir)) return false;
                
                var files = Directory.GetFiles(_journalDir, pattern);
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                        if (entry != null && entry.TradeCompleted)
                        {
                            return true; // Found at least one completed trade
                        }
                    }
                    catch
                    {
                        // Skip corrupted files, continue scanning
                    }
                }
            }
            catch
            {
                // Fail-safe: return false on error (assume no completed trade)
                return false;
            }
            
            return false;
        }
    }

    /// <summary>
    /// Check if an intent is terminal (TradeCompleted).
    /// Used for late-fill protection and orphan detection.
    /// </summary>
    public bool IsIntentCompleted(string intentId, string tradingDate, string stream)
    {
        var entry = GetEntry(intentId, tradingDate, stream);
        return entry != null && entry.TradeCompleted;
    }

    /// <summary>
    /// Get execution journal entry for an intent.
    /// Returns null if entry doesn't exist.
    /// </summary>
    public ExecutionJournalEntry? GetEntry(string intentId, string tradingDate, string stream)
    {
        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            
            // Check cache first
            if (_cache.TryGetValue(key, out var cachedEntry))
            {
                return cachedEntry;
            }
            
            // Check disk
            var journalPath = GetJournalPath(tradingDate, stream, intentId);
            if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                    {
                        _cache[key] = entry;
                        if (TryParseIntentIdFromCacheKey(key, out var gid))
                            SyncAdoptionCandidateIndexForIntentLocked(gid, entry);
                        return entry;
                    }
                }
                catch
                {
                    // If read fails, return null
                }
            }
            
            return null;
        }
    }
    
    private string GetJournalPath(string tradingDate, string stream, string intentId)
        => Path.Combine(_journalDir, $"{tradingDate}_{stream}_{intentId}.json");

    /// <summary>Extract instrument root (e.g. "M2K 03-26" -> "M2K") for broker/reconciliation key matching.</summary>
    private static string GetInstrumentRoot(string instrumentKey)
    {
        if (string.IsNullOrWhiteSpace(instrumentKey)) return "";
        var s = instrumentKey.Trim();
        var idx = s.IndexOf(' ');
        return idx >= 0 ? s.Substring(0, idx) : s;
    }

    /// <summary>
    /// Get all open journal entries (EntryFilled && !TradeCompleted) grouped by execution instrument.
    /// Used by ReconciliationRunner to find orphaned journals when broker is flat.
    /// </summary>
    public Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> GetOpenJournalEntriesByInstrument()
    {
        var result = new Dictionary<string, List<(string, string, string, ExecutionJournalEntry)>>(StringComparer.OrdinalIgnoreCase);
        string[] files;
        try
        {
            files = Directory.GetFiles(_journalDir, "*.json");
        }
        catch
        {
            return result;
        }

        foreach (var path in files)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                var parts = fileName.Split('_');
                if (parts.Length < 3) continue;

                var tradingDate = parts[0];
                var intentId = parts[parts.Length - 1];
                var stream = string.Join("_", parts.Skip(1).Take(parts.Length - 2));

                // Always read from disk for reconciliation - bypass cache to avoid stale data across
                // multiple RobotEngine instances (each has its own ExecutionJournal cache). See
                // docs/robot/incidents/2026-03-11_MYM_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md
                ExecutionJournalEntry? entry;
                lock (_lock)
                {
                    var json = ReadJournalFileWithRetry(path);
                    if (json == null)
                    {
                        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "EXECUTION_JOURNAL_READ_SKIPPED", "ENGINE",
                            new { path, error = "Read failed after retries (file lock?)" }));
                        continue;
                    }
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                    {
                        _cache[fileName] = entry; // Update cache for consistency with disk
                        if (TryParseIntentIdFromJournalFileName(fileName, out var oid))
                            SyncAdoptionCandidateIndexForIntentLocked(oid, entry);
                    }
                }

                if (entry == null || !entry.EntryFilled || entry.TradeCompleted) continue;
                if (entry.EntryFilledQuantityTotal <= 0) continue;

                var instrument = string.IsNullOrWhiteSpace(entry.Instrument) ? "UNKNOWN" : entry.Instrument.Trim();
                if (!result.TryGetValue(instrument, out var list))
                {
                    list = new List<(string, string, string, ExecutionJournalEntry)>();
                    result[instrument] = list;
                }
                list.Add((tradingDate, stream, intentId, entry));
            }
            catch (Exception ex)
            {
                // Log read failures (file lock, corrupt JSON) for RECONCILIATION_QTY_MISMATCH diagnostics
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "EXECUTION_JOURNAL_READ_SKIPPED", "ENGINE",
                    new { path, error = ex.Message, exception_type = ex.GetType().Name }));
            }
        }

        return result;
    }

    /// <summary>
    /// Strip NinjaTrader full contract name to root symbol (e.g. "MES 03-26" → "MES") for adoption matching.
    /// </summary>
    private static string NormalizeJournalInstrumentSymbol(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        var sp = s.IndexOf(' ');
        if (sp > 0) s = s.Substring(0, sp);
        return s;
    }

    /// <summary>True if journal entry instrument matches IEA execution key or canonical (ES vs MES, etc.).</summary>
    private static bool JournalInstrumentMatchesExecutionKey(string? entryInstrument, string executionInstrument, string? canonicalInstrument)
    {
        var inst = NormalizeJournalInstrumentSymbol(string.IsNullOrWhiteSpace(entryInstrument) ? "UNKNOWN" : entryInstrument);
        var exec = NormalizeJournalInstrumentSymbol(executionInstrument);
        var canon = string.IsNullOrEmpty(canonicalInstrument) ? null : NormalizeJournalInstrumentSymbol(canonicalInstrument);
        if (string.Equals(inst, exec, StringComparison.OrdinalIgnoreCase)) return true;
        if (canon != null && string.Equals(inst, canon, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsUntrackedFillRecoveryMarker(ExecutionJournalEntry? entry) =>
        entry != null &&
        string.Equals(entry.Stream, UntrackedFillRecoveryStream, StringComparison.OrdinalIgnoreCase);

    private static bool IsAdoptionCandidateJournalEntry(ExecutionJournalEntry? entry)
        => entry != null && entry.EntrySubmitted && !entry.TradeCompleted && !IsUntrackedFillRecoveryMarker(entry);

    /// <summary>Parse intent id from journal file basename (tradingDate_stream..._intentId).</summary>
    private static bool TryParseIntentIdFromJournalFileName(string fileNameWithoutExtension, out string intentId)
    {
        intentId = "";
        var parts = fileNameWithoutExtension.Split('_');
        if (parts.Length < 3) return false;
        intentId = parts[parts.Length - 1];
        return !string.IsNullOrEmpty(intentId);
    }

    /// <summary>Parse intent id from cache key tradingDate_stream_intentId.</summary>
    private static bool TryParseIntentIdFromCacheKey(string cacheKey, out string intentId)
        => TryParseIntentIdFromJournalFileName(cacheKey, out intentId);

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void SyncAdoptionCandidateIndexForIntentLocked(string intentId, ExecutionJournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(intentId)) return;

        if (_adoptionCandidateIntentToNormInst.TryGetValue(intentId, out var prevNorm))
        {
            if (_normInstToAdoptionCandidateIntentIds.TryGetValue(prevNorm, out var prevSet))
            {
                prevSet.Remove(intentId);
                if (prevSet.Count == 0)
                    _normInstToAdoptionCandidateIntentIds.Remove(prevNorm);
            }
            _adoptionCandidateIntentToNormInst.Remove(intentId);
        }

        if (!IsAdoptionCandidateJournalEntry(entry)) return;

        var norm = NormalizeJournalInstrumentSymbol(string.IsNullOrWhiteSpace(entry.Instrument) ? "UNKNOWN" : entry.Instrument);
        if (string.IsNullOrEmpty(norm)) norm = "UNKNOWN";

        if (!_normInstToAdoptionCandidateIntentIds.TryGetValue(norm, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _normInstToAdoptionCandidateIntentIds[norm] = set;
        }
        set.Add(intentId);
        _adoptionCandidateIntentToNormInst[intentId] = norm;
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void RebuildAdoptionCandidateIndexFromDiskLocked()
    {
        _normInstToAdoptionCandidateIntentIds.Clear();
        _adoptionCandidateIntentToNormInst.Clear();

        string[] files;
        try
        {
            files = Directory.GetFiles(_journalDir, "*.json");
        }
        catch
        {
            _adoptionCandidateIndexWarmed = false;
            return;
        }

        foreach (var path in files)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (!TryParseIntentIdFromJournalFileName(fileName, out var intentId)) continue;

                var json = ReadJournalFileWithRetry(path);
                if (json == null) continue;
                var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                if (entry == null) continue;
                SyncAdoptionCandidateIndexForIntentLocked(intentId, entry);
            }
            catch { /* skip corrupt files */ }
        }

        _adoptionCandidateIndexWarmed = true;
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "EXECUTION_JOURNAL_ADOPTION_INDEX_REBUILT", "ENGINE",
            new
            {
                warmed = true,
                journal_file_total = files.Length,
                adoption_candidate_intents = _adoptionCandidateIntentToNormInst.Count,
                note = "Cold rebuild complete — candidate lookups use in-memory index"
            }));
    }

    /// <summary>Full disk scan — same semantics as pre-index implementation. Caller must NOT hold <see cref="_lock"/> (method acquires per-file lock).</summary>
    private HashSet<string> GetAdoptionCandidateIntentIdsForInstrumentFullScan(string executionInstrument, string? canonicalInstrument)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] files;
        try
        {
            files = Directory.GetFiles(_journalDir, "*.json");
        }
        catch
        {
            return result;
        }

        foreach (var path in files)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                var parts = fileName.Split('_');
                if (parts.Length < 3) continue;

                var intentId = parts[parts.Length - 1];

                ExecutionJournalEntry? entry;
                lock (_lock)
                {
                    var json = ReadJournalFileWithRetry(path);
                    if (json == null) continue;
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                }

                if (entry == null || !entry.EntrySubmitted || entry.TradeCompleted) continue;
                if (IsUntrackedFillRecoveryMarker(entry)) continue;

                if (!JournalInstrumentMatchesExecutionKey(entry.Instrument, executionInstrument, canonicalInstrument))
                    continue;

                result.Add(intentId);
            }
            catch { /* skip corrupt files */ }
        }

        return result;
    }

    /// <summary>
    /// Get intent IDs that are adoption candidates for restart recovery.
    /// Includes: EntrySubmitted (unfilled entry stops, filled entries, protectives) and !TradeCompleted.
    /// Separate from GetActiveIntentsForBEMonitoring which requires EntryFilled — adoption must support unfilled entry stops.
    /// </summary>
    public HashSet<string> GetAdoptionCandidateIntentIdsForInstrument(string executionInstrument, string? canonicalInstrument = null)
    {
        lock (_lock)
        {
            if (!_adoptionCandidateIndexWarmed)
            {
                _adoptionCandidateIndexLookupFallbacks++;
                if (_adoptionCandidateIndexLookupFallbacks == 1 || _adoptionCandidateIndexLookupFallbacks % 25 == 0)
                {
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "EXECUTION_JOURNAL_ADOPTION_INDEX_FALLBACK", "ENGINE",
                        new
                        {
                            lookup_source = "full_scan",
                            fallback_total = _adoptionCandidateIndexLookupFallbacks,
                            warmed = false,
                            note = "Adoption candidate index cold or failed — using full journal directory scan"
                        }));
                }
            }
            else
            {
                _adoptionCandidateIndexLookupIndexHits++;
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in _normInstToAdoptionCandidateIntentIds)
                {
                    if (!JournalInstrumentMatchesExecutionKey(kvp.Key, executionInstrument, canonicalInstrument))
                        continue;
                    foreach (var id in kvp.Value)
                        result.Add(id);
                }
                return result;
            }
        }

        return GetAdoptionCandidateIntentIdsForInstrumentFullScan(executionInstrument, canonicalInstrument);
    }

    /// <summary>
    /// Union of adoption candidates for execution key and micro/root variant (MES/MES + MES, etc.), same as adapter recovery scope.
    /// </summary>
    public HashSet<string> GetAdoptionCandidateIntentIdsUnionForExecutionKeys(string executionInstrumentPrimary, string? canonicalInstrument = null)
    {
        var u = executionInstrumentPrimary?.Trim() ?? "";
        var execVariant = u.StartsWith("M", StringComparison.OrdinalIgnoreCase) && u.Length > 1 ? u : "M" + u;
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in GetAdoptionCandidateIntentIdsForInstrument(u, canonicalInstrument))
            all.Add(id);
        if (!string.Equals(execVariant, u, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var id in GetAdoptionCandidateIntentIdsForInstrument(execVariant, canonicalInstrument))
                all.Add(id);
        }
        return all;
    }

    /// <summary>
    /// <para><b>Pending adoption candidate</b> (recovery): journal row with EntrySubmitted &amp;&amp; !TradeCompleted for this instrument
    /// scope — see <see cref="GetAdoptionCandidateIntentIdsForInstrument"/>.</para>
    /// <para><b>Stale journal intent (release)</b>: pending row with no material open quantity, broker flat for the instrument,
    /// and no robot-tagged working order on the instrument references the intent id. Safe to ignore for release and close
    /// under forced convergence.</para>
    /// <para><b>Non-flat broker</b>: stale-for-release is not used; see <see cref="IsExposureRelevantAdoptionCandidateForRelease"/>.</para>
    /// </summary>
    public static bool IsStaleAdoptionJournalEntryForRelease(
        ExecutionJournalEntry entry,
        int brokerPositionQtyAbs,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        string intentId)
    {
        if (entry == null) return false;
        if (brokerPositionQtyAbs > 0) return false;
        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (tagSet.Contains(intentId)) return false;
        var remaining = entry.EntryFilled && entry.EntryFilledQuantityTotal > 0
            ? Math.Max(0, entry.EntryFilledQuantityTotal - entry.ExitFilledQuantityTotal)
            : 0;
        return remaining <= 0;
    }

    /// <summary>Remaining open quantity for a journal entry (filled legs only).</summary>
    public static int GetEntryRemainingOpenQuantity(ExecutionJournalEntry entry)
    {
        if (entry == null || !entry.EntryFilled || entry.EntryFilledQuantityTotal <= 0) return 0;
        return Math.Max(0, entry.EntryFilledQuantityTotal - entry.ExitFilledQuantityTotal);
    }

    private static int DirectionSignFromJournalDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction)) return 0;
        var u = direction.Trim().ToUpperInvariant();
        if (u.IndexOf("LONG", StringComparison.OrdinalIgnoreCase) >= 0 || u == "BUY" || u == "L") return 1;
        if (u.IndexOf("SHORT", StringComparison.OrdinalIgnoreCase) >= 0 || u == "SELL" || u == "S") return -1;
        return 0;
    }

    private static int BrokerPositionSign(int signedQty) => signedQty > 0 ? 1 : (signedQty < 0 ? -1 : 0);

    /// <summary>
    /// When the broker has non-flat exposure, an adoption candidate blocks release only if it is plausibly tied to live
    /// exposure (open qty, tags, registry trusted working, or journal direction aligned with broker position).
    /// </summary>
    public static bool IsExposureRelevantAdoptionCandidateForRelease(
        ExecutionJournalEntry entry,
        string intentId,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        if (entry == null || string.IsNullOrWhiteSpace(intentId)) return false;
        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var regSet = registryMismatchTrustedIntentIds as HashSet<string> ??
                     new HashSet<string>(registryMismatchTrustedIntentIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        if (GetEntryRemainingOpenQuantity(entry) > 0) return true;
        if (tagSet.Contains(intentId)) return true;
        if (regSet.Count > 0 && regSet.Contains(intentId)) return true;

        var bSign = BrokerPositionSign(brokerPositionQtySigned);
        if (bSign == 0) return false;
        var jSign = DirectionSignFromJournalDirection(entry.Direction);
        return jSign != 0 && jSign == bSign;
    }

    private List<(string IntentId, bool Blocking, string? ExclusionReason)> BuildReleaseBlockingDecisionList(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        var decisions = new List<(string, bool, string?)>();
        var candidates = GetAdoptionCandidateIntentIdsUnionForExecutionKeys(executionInstrumentPrimary, canonicalInstrument);
        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var regSet = registryMismatchTrustedIntentIds as HashSet<string> ??
                     new HashSet<string>(registryMismatchTrustedIntentIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var intentId in candidates)
        {
            var located = TryGetAdoptionCandidateEntry(intentId, executionInstrumentPrimary, canonicalInstrument);
            if (located == null)
            {
                decisions.Add((intentId, true, null));
                continue;
            }

            var entry = located.Value.Entry;

            if (brokerPositionQtyAbs == 0)
            {
                if (IsStaleAdoptionJournalEntryForRelease(entry, brokerPositionQtyAbs, tagSet, intentId))
                    decisions.Add((intentId, false, ReleaseBlockingExclusionReasons.NO_TAG));
                else
                    decisions.Add((intentId, true, null));
                continue;
            }

            if (IsExposureRelevantAdoptionCandidateForRelease(
                    entry,
                    intentId,
                    brokerPositionQtySigned,
                    tagSet,
                    registryMismatchTrustedIntentIds))
            {
                decisions.Add((intentId, true, null));
                continue;
            }

            var rem = GetEntryRemainingOpenQuantity(entry);
            var bSign = BrokerPositionSign(brokerPositionQtySigned);
            var jSign = DirectionSignFromJournalDirection(entry.Direction);
            string exReason;
            if (rem > 0)
                exReason = ReleaseBlockingExclusionReasons.OTHER;
            else if (jSign != 0 && bSign != 0 && jSign != bSign)
                exReason = ReleaseBlockingExclusionReasons.DIRECTION_MISMATCH;
            else if (regSet.Count > 0 && !regSet.Contains(intentId))
                exReason = ReleaseBlockingExclusionReasons.NOT_IN_REGISTRY;
            else if (!tagSet.Contains(intentId))
                exReason = ReleaseBlockingExclusionReasons.NO_TAG;
            else
                exReason = ReleaseBlockingExclusionReasons.NO_OPEN_QTY;

            decisions.Add((intentId, false, exReason));
        }

        return decisions;
    }

    /// <summary>
    /// Structured audit for release-blocking vs excluded adoption candidates (logging). Does not mutate the journal.
    /// </summary>
    public ReleaseBlockingCandidateAuditData BuildReleaseBlockingCandidateAudit(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds,
        int sampleLimit = 15)
    {
        var decisions = BuildReleaseBlockingDecisionList(
            executionInstrumentPrimary,
            canonicalInstrument,
            brokerPositionQtyAbs,
            brokerPositionQtySigned,
            robotTaggedIntentIdsOnInstrument,
            registryMismatchTrustedIntentIds);

        var raw = decisions.Count;
        var blockingIds = new List<string>();
        var excludedIds = new List<string>();
        var excludedReasons = new List<string>();
        foreach (var (intentId, blocking, exReason) in decisions)
        {
            if (blocking)
                blockingIds.Add(intentId);
            else if (exReason != null)
            {
                excludedIds.Add(intentId);
                excludedReasons.Add(exReason);
            }
        }

        var blockingCount = blockingIds.Count;
        var excludedCount = excludedIds.Count;
        blockingIds.Sort(StringComparer.OrdinalIgnoreCase);
        var blockingHash = ReleaseReconciliationRedundancySuppression.ComputeStableOrderedStringSetHashFromSortedList(blockingIds);
        return new ReleaseBlockingCandidateAuditData
        {
            RawCandidateCount = raw,
            BlockingCandidateCount = blockingCount,
            ExcludedCandidateCount = excludedCount,
            BlockingIntentSetHash = blockingHash,
            BlockingIntentIdsSample = blockingIds.Take(sampleLimit).ToList(),
            ExcludedIntentIdsSample = excludedIds.Take(sampleLimit).ToList(),
            ExclusionReasonsSample = excludedReasons.Take(sampleLimit).ToList()
        };
    }

    /// <summary>
    /// Adoption candidate intent ids that are exposure-relevant for release (subset of
    /// <see cref="GetAdoptionCandidateIntentIdsUnionForExecutionKeys"/>). Does not mutate the journal.
    /// </summary>
    public HashSet<string> FilterAdoptionCandidatesForRelease(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        var blocking = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (intentId, isBlocking, _) in BuildReleaseBlockingDecisionList(
                     executionInstrumentPrimary,
                     canonicalInstrument,
                     brokerPositionQtyAbs,
                     brokerPositionQtySigned,
                     robotTaggedIntentIdsOnInstrument,
                     registryMismatchTrustedIntentIds))
        {
            if (isBlocking)
                blocking.Add(intentId);
        }

        return blocking;
    }

    /// <summary>
    /// Count of adoption candidates that must block <see cref="StateConsistencyReleaseEvaluator"/>'s pending check.
    /// Does not mutate the journal.
    /// </summary>
    public int CountReleaseBlockingAdoptionCandidates(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        var n = 0;
        foreach (var (_, isBlocking, _) in BuildReleaseBlockingDecisionList(
                     executionInstrumentPrimary,
                     canonicalInstrument,
                     brokerPositionQtyAbs,
                     brokerPositionQtySigned,
                     robotTaggedIntentIdsOnInstrument,
                     registryMismatchTrustedIntentIds))
        {
            if (isBlocking)
                n++;
        }

        return n;
    }

    /// <summary>
    /// Single pass over blocking adoption decisions for release suppression / fingerprinting (sorted intent-set hash).
    /// </summary>
    public (int BlockingCount, long BlockingIntentSetHash) GetReleaseBlockingAdoptionStructuralFingerprint(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        int brokerPositionQtySigned,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        var blockingIds = new List<string>();
        foreach (var (intentId, isBlocking, _) in BuildReleaseBlockingDecisionList(
                     executionInstrumentPrimary,
                     canonicalInstrument,
                     brokerPositionQtyAbs,
                     brokerPositionQtySigned,
                     robotTaggedIntentIdsOnInstrument,
                     registryMismatchTrustedIntentIds))
        {
            if (isBlocking)
                blockingIds.Add(intentId);
        }

        blockingIds.Sort(StringComparer.OrdinalIgnoreCase);
        return (blockingIds.Count,
            ReleaseReconciliationRedundancySuppression.ComputeStableOrderedStringSetHashFromSortedList(blockingIds));
    }

    /// <summary>
    /// Mark stale adoption journal rows complete so recovery index and release gate converge. Only when broker is flat,
    /// no QTSW2-tagged working references the intent on this instrument snapshot, and journal has no open position quantity
    /// for the row.
    /// </summary>
    /// <returns>Number of journal files updated.</returns>
    public int ReconcileStaleAdoptionJournalCandidatesForRelease(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        int brokerPositionQtyAbs,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        DateTimeOffset utcNow)
    {
        var candidates = GetAdoptionCandidateIntentIdsUnionForExecutionKeys(executionInstrumentPrimary, canonicalInstrument);
        if (candidates.Count == 0) return 0;
        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var closed = 0;
        foreach (var intentId in candidates)
        {
            var located = TryGetAdoptionCandidateEntry(intentId, executionInstrumentPrimary, canonicalInstrument);
            if (located == null) continue;
            var (tradingDate, stream, entry) = located.Value;
            if (!IsStaleAdoptionJournalEntryForRelease(entry, brokerPositionQtyAbs, tagSet, intentId))
                continue;
            if (TryCloseStaleAdoptionJournalEntry(tradingDate, stream, intentId, utcNow))
                closed++;
        }
        if (closed > 0)
            BumpReleaseSuppressionActivity();
        return closed;
    }

    private bool TryCloseStaleAdoptionJournalEntry(string tradingDate, string stream, string intentId, DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream) || string.IsNullOrWhiteSpace(intentId))
            return false;

        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            ExecutionJournalEntry? entry;
            if (_cache.TryGetValue(key, out var existing))
                entry = existing;
            else if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                        _cache[key] = entry;
                    else
                        return false;
                }
                catch
                {
                    return false;
                }
            }
            else
                return false;

            if (entry == null || !entry.EntrySubmitted || entry.TradeCompleted)
                return false;

            if (entry.EntryFilled && entry.EntryFilledQuantityTotal > 0)
            {
                var remaining = Math.Max(0, entry.EntryFilledQuantityTotal - entry.ExitFilledQuantityTotal);
                if (remaining > 0)
                    return false;

                entry.ExitFilledQuantityTotal = entry.EntryFilledQuantityTotal;
                entry.ExitAvgFillPrice = null;
                entry.ExitFillNotional = null;
                entry.ExitFilledAtUtc = utcNow.ToString("o");
                entry.ExitOrderType = CompletionReasons.RECONCILIATION_STALE_JOURNAL_RELEASE;
            }

            entry.TradeCompleted = true;
            entry.CompletedAtUtc = utcNow.ToString("o");
            entry.CompletionReason = CompletionReasons.RECONCILIATION_STALE_JOURNAL_RELEASE;
            entry.RealizedPnLPoints = null;
            entry.RealizedPnLGross = null;
            entry.RealizedPnLNet = null;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);

            SyncAdoptionCandidateIndexForIntentLocked(intentId, entry);

            _log.Write(RobotEvents.EngineBase(utcNow, "", "EXECUTION_JOURNAL_STALE_RELEASE_CLOSED", "ENGINE",
                new
                {
                    intent_id = intentId,
                    trading_date = tradingDate,
                    stream,
                    note = "Stale adoption journal closed for state-consistency release (broker flat, no tagged working ref, no open qty)"
                }));
            return true;
        }
    }

    private static bool OpenJournalInstrumentKeyMatches(string? key, string executionInstrumentPrimary, string? alternateInstrumentKey)
    {
        var k = key?.Trim() ?? "";
        if (string.IsNullOrEmpty(k)) return false;
        var p = executionInstrumentPrimary?.Trim() ?? "";
        if (string.Equals(k, p, StringComparison.OrdinalIgnoreCase)) return true;
        var a = alternateInstrumentKey?.Trim();
        if (!string.IsNullOrEmpty(a) && string.Equals(k, a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// When sum of open journal quantities for the instrument exceeds broker position, trim phantom exposure by applying
    /// virtual exits (or full completion) to unprotected rows first (no QTSW2 tag / mismatch-trusted registry intent),
    /// then protected rows if needed. Fail-closed if excess cannot be removed without touching protected attribution.
    /// </summary>
    /// <returns>Number of journal write operations applied.</returns>
    public int ReconcileJournalOpenQuantityWithBroker(
        string executionInstrumentPrimary,
        string? alternateInstrumentKey,
        int brokerPositionQtyAbs,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds,
        DateTimeOffset utcNow)
    {
        var byInst = GetOpenJournalEntriesByInstrument();
        var rows = new List<(string TradingDate, string Stream, string IntentId, int Remaining, ExecutionJournalEntry Entry)>();
        foreach (var kvp in byInst)
        {
            if (!OpenJournalInstrumentKeyMatches(kvp.Key, executionInstrumentPrimary, alternateInstrumentKey))
                continue;
            foreach (var (tradingDate, stream, intentId, entry) in kvp.Value)
            {
                var rem = GetEntryRemainingOpenQuantity(entry);
                if (rem > 0)
                    rows.Add((tradingDate, stream, intentId, rem, entry));
            }
        }

        var sum = 0;
        foreach (var r in rows)
            sum += r.Remaining;

        if (sum <= brokerPositionQtyAbs)
            return 0;

        var excess = sum - brokerPositionQtyAbs;
        var tagSet = robotTaggedIntentIdsOnInstrument as HashSet<string> ??
                     new HashSet<string>(robotTaggedIntentIdsOnInstrument ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var regSet = registryMismatchTrustedIntentIds as HashSet<string> ??
                     new HashSet<string>(registryMismatchTrustedIntentIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        bool Protected(string intentId) =>
            tagSet.Contains(intentId) || (regSet.Count > 0 && regSet.Contains(intentId));

        static string RowRemKey((string TradingDate, string Stream, string IntentId, int Remaining, ExecutionJournalEntry Entry) row) =>
            $"{row.TradingDate}\x1f{row.Stream}\x1f{row.IntentId}";

        var ordered = rows
            .OrderBy(r => Protected(r.IntentId) ? 1 : 0)
            .ThenBy(r => r.TradingDate, StringComparer.Ordinal)
            .ThenBy(r => r.Stream, StringComparer.Ordinal)
            .ThenBy(r => r.IntentId, StringComparer.Ordinal)
            .ToList();

        var writes = 0;
        var remLeftByRow = rows.ToDictionary(RowRemKey, r => r.Remaining, StringComparer.Ordinal);
        var trimmedSamples = new List<(string td, string st, string id, int qty)>();
        var totalQtyTrimmed = 0;

        foreach (var r in ordered)
        {
            if (excess <= 0) break;
            if (Protected(r.IntentId)) continue;
            var rk = RowRemKey(r);
            if (!remLeftByRow.TryGetValue(rk, out var remLeft) || remLeft <= 0)
                continue;

            var take = Math.Min(remLeft, excess);
            if (take <= 0) continue;
            if (!TryApplyJournalPositionAlignmentExitQty(r.TradingDate, r.Stream, r.IntentId, take, utcNow))
                continue;

            remLeftByRow[rk] = remLeft - take;
            excess -= take;
            writes++;
            totalQtyTrimmed += take;
            if (trimmedSamples.Count < 15)
                trimmedSamples.Add((r.TradingDate, r.Stream, r.IntentId, take));
        }

        var journalAfter = sum - totalQtyTrimmed;
        var protectedPreserved = rows.Count(r => Protected(r.IntentId) && r.Remaining > 0);
        if (writes > 0)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "EXECUTION_JOURNAL_POSITION_ALIGNMENT", "ENGINE",
                new
                {
                    instrument = executionInstrumentPrimary,
                    broker_position_qty_before = brokerPositionQtyAbs,
                    journal_open_qty_before = sum,
                    journal_open_qty_after = journalAfter,
                    trimmed_row_count = writes,
                    trimmed_rows_sample = trimmedSamples.Select(s => new
                    {
                        trading_date = s.td,
                        stream = s.st,
                        intent_id = s.id,
                        qty_trimmed = s.qty
                    }).ToList(),
                    protected_rows_preserved_count = protectedPreserved,
                    completion_reason = CompletionReasons.RECONCILIATION_POSITION_ALIGNMENT,
                    note = "Batch summary for journal open qty alignment toward broker position"
                }));
            BumpReleaseSuppressionActivity();
        }

        return writes;
    }

    private bool TryApplyJournalPositionAlignmentExitQty(string tradingDate, string stream, string intentId, int exitQty, DateTimeOffset utcNow)
    {
        if (exitQty <= 0 || string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream) ||
            string.IsNullOrWhiteSpace(intentId))
            return false;

        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            ExecutionJournalEntry? entry;
            if (_cache.TryGetValue(key, out var existing))
                entry = existing;
            else if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                        _cache[key] = entry;
                }
                catch
                {
                    return false;
                }
            }
            else
                return false;

            if (entry == null || !entry.EntryFilled || entry.EntryFilledQuantityTotal <= 0 || entry.TradeCompleted)
                return false;

            var remaining = Math.Max(0, entry.EntryFilledQuantityTotal - entry.ExitFilledQuantityTotal);
            if (remaining <= 0)
                return false;

            var apply = Math.Min(exitQty, remaining);
            entry.ExitFilledQuantityTotal += apply;
            entry.ExitOrderType = CompletionReasons.RECONCILIATION_POSITION_ALIGNMENT;
            entry.ExitFilledAtUtc ??= utcNow.ToString("o");

            if (entry.ExitFilledQuantityTotal >= entry.EntryFilledQuantityTotal)
            {
                entry.TradeCompleted = true;
                entry.CompletedAtUtc = utcNow.ToString("o");
                entry.CompletionReason = CompletionReasons.RECONCILIATION_POSITION_ALIGNMENT;
                entry.RealizedPnLPoints = null;
                entry.RealizedPnLGross = null;
                entry.RealizedPnLNet = null;
            }

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            SyncAdoptionCandidateIndexForIntentLocked(intentId, entry);
            BumpReleaseSuppressionActivity();
            return true;
        }
    }

    /// <summary>
    /// Try get journal entry for adoption candidate by intentId.
    /// Used for cross-instance fill resolution when IntentMap may not have the intent yet.
    /// Returns (tradingDate, stream, entry) if found; null otherwise.
    /// </summary>
    public (string TradingDate, string Stream, ExecutionJournalEntry Entry)? TryGetAdoptionCandidateEntry(string intentId, string executionInstrument, string? canonicalInstrument = null)
    {
        if (string.IsNullOrWhiteSpace(intentId)) return null;
        string[] files;
        try { files = Directory.GetFiles(_journalDir, "*.json"); }
        catch { return null; }
        foreach (var path in files)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                var parts = fileName.Split('_');
                if (parts.Length < 3) continue;
                var fileIntentId = parts[parts.Length - 1];
                if (!string.Equals(fileIntentId, intentId, StringComparison.OrdinalIgnoreCase)) continue;
                var tradingDate = parts[0];
                var stream = string.Join("_", parts.Skip(1).Take(parts.Length - 2));
                ExecutionJournalEntry? entry;
                lock (_lock)
                {
                    var json = ReadJournalFileWithRetry(path);
                    if (json == null) continue;
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                }
                if (entry == null || !entry.EntrySubmitted || entry.TradeCompleted) continue;
                if (IsUntrackedFillRecoveryMarker(entry)) continue;
                if (!JournalInstrumentMatchesExecutionKey(entry.Instrument, executionInstrument, canonicalInstrument))
                    continue;
                return (tradingDate, stream, entry);
            }
            catch { /* skip */ }
        }
        return null;
    }

    private static string BuildUntrackedFillRecoveryIntentId(string instrumentKey, string brokerOrderId)
    {
        var inst = NormalizeJournalInstrumentSymbol(string.IsNullOrWhiteSpace(instrumentKey) ? "UNKNOWN" : instrumentKey);
        var bo = string.IsNullOrWhiteSpace(brokerOrderId) ? "UNKNOWN" : brokerOrderId.Trim();
        var payload = $"{inst}\u001f{bo}";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var sb = new StringBuilder(20);
        sb.Append("UNTR");
        for (var i = 0; i < 8; i++)
            sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    private static int ResolveBrokerAbsQtyForJournalInstrument(string journalInstrumentKey,
        IReadOnlyDictionary<string, int> accountQtyByInstrument)
    {
        if (accountQtyByInstrument == null || accountQtyByInstrument.Count == 0) return 0;
        var target = BrokerPositionResolver.NormalizeCanonicalKey(journalInstrumentKey);
        if (string.IsNullOrEmpty(target)) return 0;
        var sum = 0;
        foreach (var kv in accountQtyByInstrument)
        {
            if (string.Equals(BrokerPositionResolver.NormalizeCanonicalKey(kv.Key), target, StringComparison.OrdinalIgnoreCase))
                sum += kv.Value;
        }
        return sum;
    }

    /// <summary>
    /// Durable journal-backed marker when a fill cannot be attributed to an intent (fail-closed flatten still runs separately).
    /// Keeps reconciliation from settling into broker-open / journal-zero until the broker is flat or this row is completed.
    /// </summary>
    public void UpsertUntrackedFillRecoveryJournal(
        string instrumentKey,
        string brokerOrderId,
        int fillQuantitySigned,
        decimal fillPrice,
        DateTimeOffset utcNow,
        string? correlationId = null)
    {
        var absQty = Math.Max(1, Math.Abs(fillQuantitySigned));
        var intentId = BuildUntrackedFillRecoveryIntentId(instrumentKey, brokerOrderId);
        var tradingDate = UntrackedFillRecoveryTradingDateBucket;
        var stream = UntrackedFillRecoveryStream;
        var key = $"{tradingDate}_{stream}_{intentId}";
        var journalPath = GetJournalPath(tradingDate, stream, intentId);
        var inst = string.IsNullOrWhiteSpace(instrumentKey) ? "UNKNOWN" : instrumentKey.Trim();

        lock (_lock)
        {
            ExecutionJournalEntry? entry = null;
            if (_cache.TryGetValue(key, out var cached))
                entry = cached;
            else if (File.Exists(journalPath))
            {
                var json = ReadJournalFileWithRetry(journalPath);
                if (json != null)
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
            }

            entry ??= new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = tradingDate,
                Stream = stream,
                Instrument = inst
            };

            entry.IntentId = intentId;
            entry.TradingDate = tradingDate;
            entry.Stream = stream;
            if (string.IsNullOrWhiteSpace(entry.Instrument)) entry.Instrument = inst;

            entry.EntrySubmitted = true;
            entry.EntrySubmittedAt ??= utcNow.ToString("o");
            entry.EntryFilled = true;
            entry.EntryFilledAt = utcNow.ToString("o");
            entry.EntryFilledAtUtc ??= utcNow.ToString("o");
            entry.EntryOrderType = "UNTRACKED_FILL_RECOVERY";
            entry.BrokerOrderId = brokerOrderId;
            entry.EntryFilledQuantityTotal = Math.Max(entry.EntryFilledQuantityTotal, absQty);
            entry.FillPrice = fillPrice;
            entry.ActualFillPrice = fillPrice;
            entry.FillQuantity = entry.EntryFilledQuantityTotal;

            if (fillQuantitySigned != 0)
                entry.Direction = fillQuantitySigned > 0 ? "Long" : "Short";
            else if (string.IsNullOrWhiteSpace(entry.Direction))
                entry.Direction = "UNTRACKED";

            entry.TradeCompleted = false;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            BumpReleaseSuppressionActivity();
        }

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "UNTRACKED_FILL_RECOVERY_JOURNAL_UPSERT", "ENGINE",
            new
            {
                instrument = inst,
                intent_id = intentId,
                broker_order_id = brokerOrderId,
                open_journal_qty = absQty,
                correlation_id = correlationId
            }));
    }

    /// <summary>
    /// Robot-tagged exposure reconciliation: persist an open filled journal row when broker shows position but
    /// normal <see cref="RecordEntryFill"/> did not (lifecycle/fill path failure). Distinct from untracked-fill recovery.
    /// Uses the intent's real trading_date + stream + intent_id key so protectives and reconciliation align.
    /// </summary>
    public void UpsertTaggedBrokerExposureRecoveryJournal(
        string intentId,
        string tradingDate,
        string stream,
        string executionInstrument,
        string? brokerOrderId,
        int openQtyAbs,
        string direction,
        decimal? avgFillPrice,
        decimal? stopPrice,
        decimal? targetPrice,
        DateTimeOffset utcNow,
        string? correlationId = null)
    {
        if (string.IsNullOrWhiteSpace(intentId) || string.IsNullOrWhiteSpace(tradingDate) ||
            string.IsNullOrWhiteSpace(stream) || openQtyAbs <= 0)
            return;
        var inst = string.IsNullOrWhiteSpace(executionInstrument) ? "UNKNOWN" : executionInstrument.Trim();
        var key = $"{tradingDate}_{stream}_{intentId}";
        var journalPath = GetJournalPath(tradingDate, stream, intentId);
        var dRaw = direction?.Trim() ?? "";
        var normDir = dRaw.Length == 0 ? ""
            : (dRaw.Length == 1 ? dRaw.ToUpperInvariant()
                : char.ToUpperInvariant(dRaw[0]) + dRaw.Substring(1).ToLowerInvariant());

        lock (_lock)
        {
            ExecutionJournalEntry? entry = null;
            if (_cache.TryGetValue(key, out var cached))
                entry = cached;
            else if (File.Exists(journalPath))
            {
                var json = ReadJournalFileWithRetry(journalPath);
                if (json != null)
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
            }

            entry ??= new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = tradingDate,
                Stream = stream,
                Instrument = inst
            };

            entry.IntentId = intentId;
            entry.TradingDate = tradingDate;
            entry.Stream = stream;
            if (string.IsNullOrWhiteSpace(entry.Instrument)) entry.Instrument = inst;

            entry.EntrySubmitted = true;
            entry.EntrySubmittedAt ??= utcNow.ToString("o");
            entry.EntryFilled = true;
            entry.EntryFilledAt ??= utcNow.ToString("o");
            entry.EntryFilledAtUtc ??= utcNow.ToString("o");
            entry.EntryOrderType = "TAGGED_BROKER_EXPOSURE_RECOVERY";
            if (!string.IsNullOrEmpty(brokerOrderId))
                entry.BrokerOrderId = brokerOrderId;

            entry.EntryFilledQuantityTotal = Math.Max(entry.EntryFilledQuantityTotal, openQtyAbs);
            if (avgFillPrice.HasValue)
            {
                entry.EntryAvgFillPrice = avgFillPrice;
                entry.FillPrice = avgFillPrice;
                entry.ActualFillPrice = avgFillPrice;
            }
            entry.FillQuantity = entry.EntryFilledQuantityTotal;
            if (!string.IsNullOrEmpty(normDir))
                entry.Direction = normDir;
            if (stopPrice.HasValue && !entry.StopPrice.HasValue)
                entry.StopPrice = stopPrice;
            if (targetPrice.HasValue && !entry.TargetPrice.HasValue)
                entry.TargetPrice = targetPrice;

            entry.TradeCompleted = false;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            BumpReleaseSuppressionActivity();
        }

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "TAGGED_BROKER_EXPOSURE_RECOVERY_JOURNAL_UPSERT", "ENGINE",
            new
            {
                instrument = inst,
                intent_id = intentId,
                broker_order_id = brokerOrderId,
                open_journal_qty = openQtyAbs,
                correlation_id = correlationId,
                note = "TAGGED_ORPHAN_POSITION_RECOVERY — sibling to UNTRACKED; uses real stream/date"
            }));
    }

    private bool TryCompleteUntrackedFillRecoveryEntry(string tradingDate, string stream, string intentId,
        DateTimeOffset utcNow, string completionReason)
    {
        if (string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream) || string.IsNullOrWhiteSpace(intentId))
            return false;
        if (!string.Equals(stream, UntrackedFillRecoveryStream, StringComparison.OrdinalIgnoreCase)) return false;

        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            ExecutionJournalEntry? entry = null;
            if (_cache.TryGetValue(key, out var cached))
                entry = cached;
            else if (File.Exists(journalPath))
            {
                var json = ReadJournalFileWithRetry(journalPath);
                if (json != null)
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
            }

            if (entry == null || entry.TradeCompleted) return false;
            if (!string.Equals(entry.Stream, UntrackedFillRecoveryStream, StringComparison.OrdinalIgnoreCase)) return false;

            entry.ExitFilledQuantityTotal = entry.EntryFilledQuantityTotal;
            entry.ExitOrderType = completionReason;
            entry.ExitFilledAtUtc ??= utcNow.ToString("o");
            entry.TradeCompleted = true;
            entry.CompletedAtUtc = utcNow.ToString("o");
            entry.CompletionReason = completionReason;
            entry.RealizedPnLPoints = null;
            entry.RealizedPnLGross = null;
            entry.RealizedPnLNet = null;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            BumpReleaseSuppressionActivity();
            return true;
        }
    }

    /// <summary>Completes open untracked-fill recovery markers for an instrument (e.g. verified-flat or broker-flat reconciliation).</summary>
    public int CompleteOpenUntrackedFillRecoveryForInstrument(
        string executionInstrument,
        string? canonicalInstrument,
        DateTimeOffset utcNow,
        string completionReason)
    {
        var byInst = GetOpenJournalEntriesByInstrument();
        var count = 0;
        foreach (var kvp in byInst)
        {
            var key = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            if (!string.Equals(key, executionInstrument, StringComparison.OrdinalIgnoreCase) &&
                (canonicalInstrument == null ||
                 !string.Equals(key, canonicalInstrument, StringComparison.OrdinalIgnoreCase)))
                continue;

            foreach (var (tradingDate, stream, intentId, _) in kvp.Value)
            {
                if (!string.Equals(stream, UntrackedFillRecoveryStream, StringComparison.OrdinalIgnoreCase)) continue;
                if (TryCompleteUntrackedFillRecoveryEntry(tradingDate, stream, intentId, utcNow, completionReason))
                    count++;
            }
        }

        return count;
    }

    /// <summary>
    /// When the account snapshot shows no exposure for an instrument, close durable untracked-fill recovery markers
    /// so restart cannot leave stale open rows; call before periodic reconciliation pass signature.
    /// </summary>
    public int CloseUntrackedFillRecoveryMarkersWhenBrokerFlat(
        IReadOnlyDictionary<string, int> accountQtyByInstrument,
        DateTimeOffset utcNow)
    {
        var open = GetOpenJournalEntriesByInstrument();
        var instrumentsToClose = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in open)
        {
            var inst = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(inst)) continue;
            var hasRecovery = false;
            foreach (var (_, stream, _, _) in kvp.Value)
            {
                if (!string.Equals(stream, UntrackedFillRecoveryStream, StringComparison.OrdinalIgnoreCase)) continue;
                hasRecovery = true;
                break;
            }
            if (!hasRecovery) continue;
            if (ResolveBrokerAbsQtyForJournalInstrument(inst, accountQtyByInstrument) != 0) continue;
            instrumentsToClose.Add(inst);
        }

        var total = 0;
        foreach (var inst in instrumentsToClose)
        {
            var canon = BrokerPositionResolver.NormalizeCanonicalKey(inst);
            total += CompleteOpenUntrackedFillRecoveryForInstrument(inst, canon, utcNow, CompletionReasons.UNTRACKED_FILL_RECOVERY_FLAT);
        }

        return total;
    }

    /// <summary>
    /// Get count of open journal entries for an instrument. Matches execution instrument and optionally canonical.
    /// </summary>
    public int GetOpenJournalCountForInstrument(string executionInstrument, string? canonicalInstrument = null)
    {
        var byInst = GetOpenJournalEntriesByInstrument();
        var count = 0;
        foreach (var kvp in byInst)
        {
            var key = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            if (string.Equals(key, executionInstrument, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(canonicalInstrument) && string.Equals(key, canonicalInstrument, StringComparison.OrdinalIgnoreCase)))
                count += kvp.Value.Count;
        }
        return count;
    }

    /// <summary>
    /// Get sum of remaining open quantity for journal entries matching an instrument. For quantity reconciliation.
    /// Uses EntryFilledQuantityTotal - ExitFilledQuantityTotal (not just EntryFilledQuantityTotal) so partial exits
    /// (e.g. 1 of 2 at target) reconcile correctly. See 2026-03-17_YM1_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.
    /// </summary>
    public int GetOpenJournalQuantitySumForInstrument(string executionInstrument, string? canonicalInstrument = null) =>
        GetOpenJournalStructuralStateForInstrument(executionInstrument, canonicalInstrument).OpenQtySum;

    /// <summary>
    /// Open exposure sum plus stable hash of intent ids with remaining open quantity &gt; 0 (release suppression).
    /// </summary>
    public (int OpenQtySum, long OpenIntentSetHash) GetOpenJournalStructuralStateForInstrument(string executionInstrument,
        string? canonicalInstrument = null) =>
        GetOpenJournalStructuralStateForInstrumentFromMap(GetOpenJournalEntriesByInstrument(), executionInstrument,
            canonicalInstrument);

    /// <summary>
    /// Same as <see cref="GetOpenJournalStructuralStateForInstrument"/> but reuses a single
    /// <see cref="GetOpenJournalEntriesByInstrument"/> result for a whole reconciliation/mismatch pass (avoids O(instruments×files) disk replay).
    /// </summary>
    public (int OpenQtySum, long OpenIntentSetHash) GetOpenJournalStructuralStateForInstrumentFromMap(
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInstrument,
        string executionInstrument,
        string? canonicalInstrument = null)
    {
        var sum = 0;
        var openIntentIds = new List<string>();
        foreach (var kvp in openByInstrument)
        {
            var key = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            if (string.Equals(key, executionInstrument, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(canonicalInstrument) && string.Equals(key, canonicalInstrument, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var (_, _, intentId, entry) in kvp.Value)
                {
                    var remaining = GetEntryRemainingOpenQuantity(entry);
                    if (remaining <= 0) continue;
                    sum += remaining;
                    if (!string.IsNullOrWhiteSpace(intentId))
                        openIntentIds.Add(intentId);
                }
            }
        }

        openIntentIds.Sort(StringComparer.OrdinalIgnoreCase);
        return (sum, ReleaseReconciliationRedundancySuppression.ComputeStableOrderedStringSetHashFromSortedList(openIntentIds));
    }

    /// <inheritdoc cref="GetOpenJournalQuantitySumForInstrument"/>
    public int GetOpenJournalQuantitySumForInstrumentFromMap(
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInstrument,
        string executionInstrument,
        string? canonicalInstrument = null) =>
        GetOpenJournalStructuralStateForInstrumentFromMap(openByInstrument, executionInstrument, canonicalInstrument).OpenQtySum;

    /// <summary>
    /// Force-close orphan journals for an instrument when operator has confirmed account is correct.
    /// Use only after verifying broker position matches expectation. Marks journals with RECONCILIATION_MANUAL_OVERRIDE.
    /// </summary>
    /// <returns>Number of journals force-closed.</returns>
    public int ForceReconcileOrphanJournalsForInstrument(string instrument, DateTimeOffset utcNow)
    {
        var execVariant = instrument.StartsWith("M") && instrument.Length > 1 ? instrument : "M" + instrument;
        var byInst = GetOpenJournalEntriesByInstrument();
        var count = 0;

        foreach (var kvp in byInst)
        {
            var key = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            if (!string.Equals(key, instrument, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, execVariant, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var (tradingDate, stream, intentId, _) in kvp.Value)
            {
                if (ForceReconcileJournal(tradingDate, stream, intentId, utcNow))
                    count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Force-close a single journal (manual override). Use when operator confirms position was closed externally.
    /// </summary>
    private bool ForceReconcileJournal(string tradingDate, string stream, string intentId, DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream) || string.IsNullOrWhiteSpace(intentId))
            return false;

        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            ExecutionJournalEntry? entry;
            if (_cache.TryGetValue(key, out var existing))
            {
                entry = existing;
            }
            else if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                        _cache[key] = entry;
                    else
                        return false;
                }
                catch
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            if (entry == null || !entry.EntryFilled || entry.TradeCompleted)
                return false;

            if (entry.EntryFilledQuantityTotal <= 0)
                return false;

            entry.ExitFilledQuantityTotal = entry.EntryFilledQuantityTotal;
            entry.ExitAvgFillPrice = null;
            entry.ExitFillNotional = null;
            entry.ExitFilledAtUtc = utcNow.ToString("o");
            entry.ExitOrderType = CompletionReasons.RECONCILIATION_MANUAL_OVERRIDE;
            entry.TradeCompleted = true;
            entry.CompletedAtUtc = utcNow.ToString("o");
            entry.CompletionReason = CompletionReasons.RECONCILIATION_MANUAL_OVERRIDE;
            entry.RealizedPnLPoints = null;
            entry.RealizedPnLGross = null;
            entry.RealizedPnLNet = null;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            BumpReleaseSuppressionActivity();
            return true;
        }
    }

    /// <summary>
    /// Record trade completion via reconciliation (broker flat, no exit fill received).
    /// Sets TradeCompleted, CompletionReason=RECONCILIATION_BROKER_FLAT.
    /// Exit price and P&L left null so reporting layer can treat reconciled trades specially.
    /// </summary>
    public bool RecordReconciliationComplete(string tradingDate, string stream, string intentId, DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream) || string.IsNullOrWhiteSpace(intentId))
            return false;

        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            ExecutionJournalEntry? entry;
            if (_cache.TryGetValue(key, out var existing))
            {
                entry = existing;
            }
            else if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                        _cache[key] = entry;
                    else
                        return false;
                }
                catch
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            if (entry == null || !entry.EntryFilled || entry.TradeCompleted)
                return false;

            if (entry.EntryFilledQuantityTotal <= 0)
                return false;

            // Mark closed; exit price and P&L unknown (broker closed externally)
            entry.ExitFilledQuantityTotal = entry.EntryFilledQuantityTotal;
            entry.ExitAvgFillPrice = null;
            entry.ExitFillNotional = null;
            entry.ExitFilledAtUtc = utcNow.ToString("o");
            entry.ExitOrderType = CompletionReasons.RECONCILIATION_BROKER_FLAT;
            entry.TradeCompleted = true;
            entry.CompletedAtUtc = utcNow.ToString("o");
            entry.CompletionReason = CompletionReasons.RECONCILIATION_BROKER_FLAT;
            entry.RealizedPnLPoints = null;
            entry.RealizedPnLGross = null;
            entry.RealizedPnLNet = null;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
            BumpReleaseSuppressionActivity();
            return true;
        }
    }

    /// <summary>
    /// Pre-load all journal entries for a trading date into cache.
    /// Call on Realtime transition so BE monitoring never hits disk on first lookup.
    /// </summary>
    public void WarmCacheForTradingDate(string tradingDate)
    {
        if (string.IsNullOrWhiteSpace(tradingDate)) return;

        var prefix = $"{tradingDate.Trim()}_";
        string[] files;
        try
        {
            files = Directory.GetFiles(_journalDir, $"{prefix}*.json");
        }
        catch
        {
            return; // Directory doesn't exist or inaccessible - no-op
        }

        lock (_lock)
        {
            foreach (var path in files)
            {
                try
                {
                    var key = Path.GetFileNameWithoutExtension(path);
                    if (_cache.ContainsKey(key)) continue; // Already cached

                    var json = File.ReadAllText(path);
                    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                    {
                        _cache[key] = entry;
                        if (TryParseIntentIdFromCacheKey(key, out var wid))
                            SyncAdoptionCandidateIndexForIntentLocked(wid, entry);
                        // Populate entry-fill cache for O(1) HasEntryFillForStream
                        var parts = key.Split('_');
                        if (parts.Length >= 3)
                        {
                            var date = parts[0];
                            var stream = string.Join("_", parts.Skip(1).Take(parts.Length - 2));
                            var hasFill = entry.EntryFilled || entry.EntryFilledQuantityTotal > 0;
                            var fillKey = $"{date}_{stream}";
                            if (!_entryFillByStream.TryGetValue(fillKey, out var existing) || !existing)
                                _entryFillByStream[fillKey] = hasFill;
                            else if (hasFill)
                                _entryFillByStream[fillKey] = true; // OR: any intent with fill => true
                        }
                    }
                }
                catch
                {
                    // Skip individual file errors - best-effort warm
                }
            }
        }
    }

    private const int JOURNAL_IO_SLOW_THRESHOLD_MS = 100;
    private const int JOURNAL_LOCK_SLOW_WAIT_MS = 50;
    private const int JOURNAL_LOCK_SLOW_HOLD_MS = 100;

    /// <summary>Execute action under journal lock with timing. Logs JOURNAL_LOCK_SLOW when wait or hold exceeds threshold.</summary>
    /// <param name="extra">Optional extra data (e.g. cache_hit) - invoked after action, merged into log.</param>
    private void WithLockTiming(string context, string tradingDate, Action action, Func<object?>? extra = null)
    {
        var waitSw = Stopwatch.StartNew();
        lock (_lock)
        {
            var waitMs = waitSw.ElapsedMilliseconds;
            var holdSw = Stopwatch.StartNew();
            try
            {
                action();
            }
            finally
            {
                var holdMs = holdSw.ElapsedMilliseconds;
                if (waitMs >= JOURNAL_LOCK_SLOW_WAIT_MS || holdMs >= JOURNAL_LOCK_SLOW_HOLD_MS)
                {
                    var ev = new Dictionary<string, object?>
                    {
                        ["context"] = context,
                        ["lock_wait_ms"] = waitMs,
                        ["lock_hold_ms"] = holdMs,
                        ["note"] = "Correlate with disconnects"
                    };
                    var ex = extra?.Invoke();
                    if (ex != null)
                    {
                        foreach (var p in ex.GetType().GetProperties())
                            ev[p.Name] = p.GetValue(ex);
                    }
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate ?? "", "JOURNAL_LOCK_SLOW", "ENGINE", ev));
                }
            }
        }
    }

    private void SaveJournal(string path, ExecutionJournalEntry entry)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 50;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var json = JsonUtil.Serialize(entry);
                // FileShare.Read allows concurrent reads during write (avoids RECONCILIATION_QTY_MISMATCH from file lock)
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var swriter = new StreamWriter(fs))
                {
                    swriter.Write(json);
                }
                sw.Stop();
                if (sw.ElapsedMilliseconds >= JOURNAL_IO_SLOW_THRESHOLD_MS)
                {
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, entry.TradingDate ?? "", "JOURNAL_IO_SLOW", "ENGINE",
                        new { op = "write", path, elapsed_ms = sw.ElapsedMilliseconds, attempt, note = "Correlate with disconnects" }));
                }
                var fn = Path.GetFileNameWithoutExtension(path);
                if (TryParseIntentIdFromJournalFileName(fn, out var persistIntentId))
                    SyncAdoptionCandidateIndexForIntentLocked(persistIntentId, entry);
                return;
            }
            catch (Exception ex)
            {
                var isRetryable = ex is IOException;
                if (attempt < maxRetries && isRetryable)
                {
                    Thread.Sleep(retryDelayMs * attempt);
                    continue;
                }
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, entry.TradingDate ?? "", "EXECUTION_JOURNAL_ERROR", "ENGINE",
                    new { error = ex.Message, path, attempt }));
                return;
            }
        }
    }

    /// <summary>Read journal file with FileShare.ReadWrite and retry to avoid RECONCILIATION_QTY_MISMATCH from file lock.</summary>
    private string? ReadJournalFileWithRetry(string path)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 50;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                string content;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    content = sr.ReadToEnd();
                }
                sw.Stop();
                if (sw.ElapsedMilliseconds >= JOURNAL_IO_SLOW_THRESHOLD_MS)
                {
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", "JOURNAL_IO_SLOW", "ENGINE",
                        new { op = "read", path, elapsed_ms = sw.ElapsedMilliseconds, attempt, note = "Correlate with disconnects" }));
                }
                return content;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                Thread.Sleep(retryDelayMs * attempt);
            }
        }
        return null;
    }
}

/// <summary>
/// Execution journal entry (persisted per intent).
/// </summary>
public class ExecutionJournalEntry
{
    public string IntentId { get; set; } = "";

    public string? TradingDate { get; set; }

    public string Stream { get; set; } = "";

    public string Instrument { get; set; } = "";

    public bool EntrySubmitted { get; set; }

    public string? EntrySubmittedAt { get; set; }

    public bool EntryFilled { get; set; }

    public string? EntryFilledAt { get; set; }

    public string? BrokerOrderId { get; set; }

    public string? EntryOrderType { get; set; }

    public decimal? FillPrice { get; set; }

    public int? FillQuantity { get; set; }

    public bool Rejected { get; set; }

    public string? RejectedAt { get; set; }

    public string? RejectionReason { get; set; }

    public bool BEModified { get; set; }

    public string? BEModifiedAt { get; set; }

    public decimal? BEStopPrice { get; set; }
    
    // BE modification context
    public decimal? PreviousStopPrice { get; set; } // Stop price before BE modification
    public decimal? BETriggerPrice { get; set; } // BE trigger price (65% of target)
    
    // Minimal extension for recovery: deterministic rebuild fields
    public string? Direction { get; set; }
    
    public decimal? EntryPrice { get; set; }
    
    public decimal? StopPrice { get; set; }
    
    public decimal? TargetPrice { get; set; }
    
    public string? OcoGroup { get; set; }
    
    // Rejection context
    public string? RejectionOrderType { get; set; } // Order type that was rejected (ENTRY, STOP, TARGET)
    public decimal? RejectedPrice { get; set; } // Price that was rejected
    public int? RejectedQuantity { get; set; } // Quantity that was rejected
    
    // Slippage and cost tracking
    public decimal? ExpectedEntryPrice { get; set; } // Price we intended to fill at
    public decimal? ActualFillPrice { get; set; } // Price we actually filled at (same as FillPrice, kept for clarity)
    public decimal? SlippagePoints { get; set; } // ActualFillPrice - ExpectedEntryPrice (signed)
    public decimal? SlippageDollars { get; set; } // SlippagePoints * contract_multiplier * quantity
    public decimal? Commission { get; set; } // Broker commission (if available)
    public decimal? Fees { get; set; } // Exchange fees (if available)
    public decimal? TotalCost { get; set; } // SlippageDollars + Commission + Fees
    
    // Core identity (for journal-alone determinism)
    // NOTE: TradingDate and Stream may be nullable for backwards compatibility,
    // but persistence MUST reject empty/whitespace values
    // TradingDate and Stream already exist above - ensure they are populated
    
    public decimal? ContractMultiplier { get; set; } // Contract multiplier (required for P&L calculation)
    
    // Entry fill tracking (cumulative, weighted average)
    public int EntryFilledQuantityTotal { get; set; } // Cumulative total entry quantity
    public decimal? EntryAvgFillPrice { get; set; } // Weighted average entry fill price
    public decimal? EntryFillNotional { get; set; } // Sum(price * qty) for weighted avg calculation
    public string? EntryFilledAtUtc { get; set; } // First entry fill timestamp (ISO-8601 UTC)
    
    // Exit fill tracking (cumulative, weighted average)
    public int ExitFilledQuantityTotal { get; set; } // Cumulative total exit quantity
    public decimal? ExitAvgFillPrice { get; set; } // Weighted average exit fill price
    public decimal? ExitFillNotional { get; set; } // Sum(price * qty) for weighted avg calculation
    public string? ExitOrderType { get; set; } // "STOP", "TARGET", "EMERGENCY", "MANUAL"
    public string? ExitFilledAtUtc { get; set; } // First exit fill timestamp (ISO-8601 UTC)
    
    // Trade completion
    public bool TradeCompleted { get; set; } // True when exit qty == entry qty
    public decimal? RealizedPnLGross { get; set; } // Gross realized P&L in dollars
    public decimal? RealizedPnLNet { get; set; } // Net realized P&L (gross - commission - fees - slippage)
    public decimal? RealizedPnLPoints { get; set; } // Gross points before multiplier
    public string? CompletionReason { get; set; } // Exit order type that completed the trade
    public string? CompletedAtUtc { get; set; } // Trade completion timestamp (ISO-8601 UTC)
}

/// <summary>
/// Completion reason constants for execution journal (copy-safe, no enum).
/// </summary>
public static class CompletionReasons
{
    /// <summary>
    /// Trade was closed externally; broker position flat; journal reconciled.
    /// </summary>
    public const string RECONCILIATION_BROKER_FLAT = "RECONCILIATION_BROKER_FLAT";

    /// <summary>
    /// Operator confirmed account correct; orphan journals force-closed via manual trigger.
    /// </summary>
    public const string RECONCILIATION_MANUAL_OVERRIDE = "RECONCILIATION_MANUAL_OVERRIDE";

    /// <summary>
    /// Journal row had pending adoption semantics but broker/registry reality showed no exposure or tagged working refs;
    /// closed so release gate and recovery index can converge (no fill event changed).
    /// </summary>
    public const string RECONCILIATION_STALE_JOURNAL_RELEASE = "RECONCILIATION_STALE_JOURNAL_RELEASE";

    /// <summary>
    /// Open journal quantity exceeded broker position; excess exit quantity applied so release readiness reflects broker truth.
    /// Does not remove tagged/registry-attributed rows first; trim order is unprotected before protected.
    /// </summary>
    public const string RECONCILIATION_POSITION_ALIGNMENT = "RECONCILIATION_POSITION_ALIGNMENT";

    /// <summary>
    /// Untracked / untagged fill recovery marker closed after broker flat was verified (flatten verify pass or reconciliation snapshot).
    /// </summary>
    public const string UNTRACKED_FILL_RECOVERY_FLAT = "UNTRACKED_FILL_RECOVERY_FLAT";
}
