using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Execution journal for idempotency and audit trail.
/// Persists per (trading_date, stream, intent_id) to prevent double-submission.
/// </summary>
public sealed class ExecutionJournal
{
    private readonly string _journalDir;
    private readonly RobotLogger _log;
    private readonly Dictionary<string, ExecutionJournalEntry> _cache = new();
    private readonly object _lock = new object();
    
    // Callback for stream stand-down on journal corruption
    private Action<string, string, string, DateTimeOffset>? _onJournalCorruptionCallback;
    
    // Callback for recording execution costs in ExecutionSummary
    private Action<string, decimal, decimal?, decimal?>? _onExecutionCostCallback;

    public ExecutionJournal(string projectRoot, RobotLogger log)
    {
        _journalDir = Path.Combine(projectRoot, "data", "execution_journals");
        Directory.CreateDirectory(_journalDir);
        _log = log;
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
        decimal? expectedEntryPrice = null)
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

            entry.EntrySubmitted = true;
            entry.EntrySubmittedAt = utcNow.ToString("o");
            entry.BrokerOrderId = brokerOrderId;

            if (orderType == "ENTRY")
            {
                entry.EntryOrderType = orderType;
                // Store expected entry price for slippage calculation
                if (expectedEntryPrice.HasValue)
                {
                    entry.ExpectedEntryPrice = expectedEntryPrice.Value;
                }
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
        }
    }

    /// <summary>
    /// Record entry fill (delta-based, idempotent, cumulative).
    /// Accepts delta fillQuantity only, NOT cumulative filledTotal.
    /// </summary>
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
        string canonicalInstrument)
    {
        lock (_lock)
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
        }
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
        DateTimeOffset utcNow)
    {
        lock (_lock)
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
                
                // Log trade completion
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "TRADE_COMPLETED", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        stream = stream,
                        instrument = entry.Instrument,
                        direction = direction,
                        entry_avg_price = entryAvg,
                        exit_avg_price = exitAvg,
                        entry_qty = entry.EntryFilledQuantityTotal,
                        exit_qty = entry.ExitFilledQuantityTotal,
                        realized_pnl_points = points,
                        realized_pnl_gross = grossDollars,
                        realized_pnl_net = netDollars,
                        completion_reason = entry.CompletionReason,
                        contract_multiplier = contractMultiplier
                    }));
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
        }
    }

    /// <summary>
    /// Record order rejection.
    /// </summary>
    public void RecordRejection(
        string intentId,
        string tradingDate,
        string stream,
        string reason,
        DateTimeOffset utcNow)
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
        DateTimeOffset utcNow)
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

            entry.BEModified = true;
            entry.BEModifiedAt = utcNow.ToString("o");
            entry.BEStopPrice = beStopPrice;

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
                        if (entry != null)
                        {
                            // Check both legacy EntryFilled flag and new EntryFilledQuantityTotal
                            if (entry.EntryFilled || entry.EntryFilledQuantityTotal > 0)
                            {
                                return true; // Found at least one filled entry
                            }
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
                // Fail-safe: return false on error (assume no fill)
                return false;
            }
            
            return false;
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

    private string GetJournalPath(string tradingDate, string stream, string intentId)
        => Path.Combine(_journalDir, $"{tradingDate}_{stream}_{intentId}.json");

    private void SaveJournal(string path, ExecutionJournalEntry entry)
    {
        try
        {
            var json = JsonUtil.Serialize(entry);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            // Log error but don't fail execution
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, entry.TradingDate ?? "", "EXECUTION_JOURNAL_ERROR", "ENGINE",
                new { error = ex.Message, path }));
        }
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
    
    // Minimal extension for recovery: deterministic rebuild fields
    public string? Direction { get; set; }
    
    public decimal? EntryPrice { get; set; }
    
    public decimal? StopPrice { get; set; }
    
    public decimal? TargetPrice { get; set; }
    
    public string? OcoGroup { get; set; }
    
    // Slippage and cost tracking
    public decimal? ExpectedEntryPrice { get; set; } // Price we intended to fill at
    public decimal? ActualFillPrice { get; set; } // Price we actually filled at (same as FillPrice, kept for clarity)
    public decimal? SlippagePoints { get; set; } // ActualFillPrice - ExpectedEntryPrice (signed)
    public decimal? SlippageDollars { get; set; } // SlippagePoints * contract_multiplier * quantity
    public decimal? Commission { get; set; } // Broker commission (if available)
    public decimal? Fees { get; set; } // Exchange fees (if available)
    public decimal? TotalCost { get; set; } // SlippageDollars + Commission + Fees
    
    // Contract multiplier (required for P&L calculation)
    public decimal? ContractMultiplier { get; set; }
    
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
