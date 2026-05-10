using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class ExecutionJournal
{
    private static readonly TimeSpan JournalEventOrderSkewTolerance = TimeSpan.FromMilliseconds(1000);

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
        decimal? beTriggerPrice = null,
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
            HydrateCanonicalTimestampsFromLegacy(entry);
            var submitObserved = utcNow.ToString("o");
            entry.EntrySubmittedObservedAtUtc = submitObserved;

            if (TryParseJournalIsoUtc(entry.EntryFilledAtUtc, out var fillCanon) && utcNow > fillCanon)
            {
                entry.IsReconstructedSubmission = true;
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "JOURNAL_LATE_SUBMISSION_OBSERVATION", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        stream,
                        phase = "ENTRY_SUBMIT_OBSERVED_AFTER_FIRST_FILL_CANONICAL",
                        entry_filled_at_utc_canonical = entry.EntryFilledAtUtc,
                        submission_observed_at_utc = submitObserved,
                        note = "Not setting EntrySubmittedAtUtc from late observation — would invert submit/fill chronology."
                    }));
                if (!string.IsNullOrEmpty(entry.EntrySubmittedAtUtc))
                    entry.EntrySubmittedAt = entry.EntrySubmittedAtUtc;
            }
            else
            {
                if (string.IsNullOrEmpty(entry.EntrySubmittedAtUtc))
                    entry.EntrySubmittedAtUtc = submitObserved;
                entry.EntrySubmittedAt = entry.EntrySubmittedAtUtc;
            }

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
            if (beTriggerPrice.HasValue && !entry.BETriggerPrice.HasValue)
            {
                entry.BETriggerPrice = beTriggerPrice.Value;
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

            HydrateCanonicalTimestampsFromLegacy(entry);
            entry.EntryFilledObservedAtUtc = utcNow.ToString("o");
            if (!string.IsNullOrEmpty(entry.EntrySubmittedAtUtc) &&
                TryParseJournalIsoUtc(entry.EntrySubmittedAtUtc, out var subCanon) &&
                utcNow < subCanon &&
                (subCanon - utcNow) > JournalEventOrderSkewTolerance)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "JOURNAL_EVENT_ORDER_VIOLATION", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        stream,
                        phase = "ENTRY_FILL_OBSERVED_BEFORE_SUBMIT_CANONICAL",
                        entry_submitted_at_utc_canonical = entry.EntrySubmittedAtUtc,
                        fill_observed_at_utc = entry.EntryFilledObservedAtUtc,
                        note = "Fill observation precedes canonical submit timestamp — check clock or ordering."
                    }));
            }

            entry.EntryFilled = true;
            if (string.IsNullOrEmpty(entry.EntryFilledAtUtc))
                entry.EntryFilledAtUtc = utcNow.ToString("o");
            entry.EntryFilledAt = entry.EntryFilledAtUtc;
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
        string? brokerOrderInstrumentKey = null,
        string? parityPendingDedupeKey = null)
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

            HydrateCanonicalTimestampsFromLegacy(entry);
            
            // Update cumulative entry quantities (delta-based accumulation)
            entry.EntryFilledQuantityTotal += fillQuantity;
            entry.EntryFillNotional = (entry.EntryFillNotional ?? 0) + (fillPrice * fillQuantity);
            entry.EntryAvgFillPrice = entry.EntryFillNotional.Value / entry.EntryFilledQuantityTotal;

            entry.EntryFilledObservedAtUtc = utcNow.ToString("o");
            if (!string.IsNullOrEmpty(entry.EntrySubmittedAtUtc) &&
                TryParseJournalIsoUtc(entry.EntrySubmittedAtUtc, out var subCanonR) &&
                utcNow < subCanonR &&
                (subCanonR - utcNow) > JournalEventOrderSkewTolerance)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "JOURNAL_EVENT_ORDER_VIOLATION", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        stream,
                        phase = "ENTRY_FILL_OBSERVED_BEFORE_SUBMIT_CANONICAL",
                        entry_submitted_at_utc_canonical = entry.EntrySubmittedAtUtc,
                        fill_observed_at_utc = entry.EntryFilledObservedAtUtc,
                        note = "Fill observation precedes canonical submit timestamp — check clock or ordering."
                    }));
            }
            
            // Timestamp policy: Store first entry fill time only (canonical event time)
            if (string.IsNullOrEmpty(entry.EntryFilledAtUtc))
                entry.EntryFilledAtUtc = utcNow.ToString("o");
            
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
            
            // Legacy fields for backwards compatibility (mirror canonical fill time)
            entry.EntryFilled = true;
            entry.EntryFilledAt = entry.EntryFilledAtUtc;
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
            if (!string.IsNullOrWhiteSpace(parityPendingDedupeKey))
            {
                RegisterParityPendingFillPersisted(parityPendingDedupeKey);
                JournalParityPendingLedger.TryRemove(executionInstrument.Trim(), parityPendingDedupeKey);
            }
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
        string? completedIntentId = null;
        string? completedTradingDate = null;
        string? completedStream = null;
        string? completedReason = null;
        var completedUtc = utcNow;
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

            // Late/duplicate terminal fills can still arrive after a broker-flat reconciliation close under
            // playback compression. Do not double-count those fills into an overfill emergency.
            if (entry.TradeCompleted)
            {
                var completionAgeMs = -1L;
                var boundedLateTerminalFill = false;
                if (string.Equals(entry.CompletionReason, CompletionReasons.RECONCILIATION_BROKER_FLAT, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(entry.CompletedAtUtc) &&
                    DateTimeOffset.TryParse(entry.CompletedAtUtc, out var completedAtUtc))
                {
                    completionAgeMs = Math.Max(0L, (long)(utcNow - completedAtUtc).TotalMilliseconds);
                    boundedLateTerminalFill = completionAgeMs <= 5000;
                }

                if (boundedLateTerminalFill)
                {
                    var priorExitType = entry.ExitOrderType;
                    var terminalFillUpgraded = TryUpgradeBrokerFlatCompletionFromTerminalFill(
                        entry,
                        exitFillPrice,
                        exitFillQuantity,
                        exitOrderType,
                        utcNow,
                        out var terminalFillUpgradeReason);
                    if (!terminalFillUpgraded &&
                        !string.IsNullOrWhiteSpace(exitOrderType) &&
                        !string.Equals(priorExitType, exitOrderType, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.ExitOrderType = exitOrderType;
                        if (string.Equals(entry.CompletionReason, CompletionReasons.RECONCILIATION_BROKER_FLAT, StringComparison.OrdinalIgnoreCase))
                            entry.CompletionReason = exitOrderType;
                    }

                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "COMPLETED_INTENT_LATE_TERMINAL_FILL_RECONCILED", "ENGINE",
                        new
                        {
                            intent_id = intentId,
                            stream,
                            fill_price = exitFillPrice,
                            fill_quantity = exitFillQuantity,
                            prior_completion_reason = CompletionReasons.RECONCILIATION_BROKER_FLAT,
                            prior_exit_order_type = priorExitType,
                            reconciled_exit_order_type = entry.ExitOrderType,
                            exit_avg_fill_price = entry.ExitAvgFillPrice,
                            realized_pnl_gross = entry.RealizedPnLGross,
                            realized_pnl_net = entry.RealizedPnLNet,
                            completion_age_ms = completionAgeMs,
                            action = terminalFillUpgraded ? "JOURNAL_TERMINAL_FILL_UPGRADED" : "JOURNAL_TERMINAL_FILL_TYPE_RECONCILED",
                            upgrade_reason = terminalFillUpgradeReason,
                            note = terminalFillUpgraded
                                ? "ExecutionJournal upgraded broker-flat completion with the bounded terminal fill facts without increasing exit quantity."
                                : "ExecutionJournal reconciled bounded terminal fill type after broker-flat completion but did not infer PnL from an incomplete fill fact."
                        }));

                    _cache[key] = entry;
                    SaveJournal(journalPath, entry);
                    return;
                }

                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "COMPLETED_INTENT_RECEIVED_FILL", "ENGINE",
                    new
                    {
                        error = "Exit fill received after journal already TradeCompleted",
                        intent_id = intentId,
                        stream,
                        fill_price = exitFillPrice,
                        fill_quantity = exitFillQuantity,
                        exit_order_type = exitOrderType,
                        prior_completion_reason = entry.CompletionReason,
                        action = "POST_COMPLETION_FILL_IGNORED",
                        note = "ExecutionJournal ignored a post-completion fill to avoid double-counting terminal quantity."
                    }));

                _cache[key] = entry;
                SaveJournal(journalPath, entry);
                return;
            }
            
            // Update exit cumulatives (delta-based accumulation)
            entry.ExitFilledQuantityTotal += exitFillQuantity;
            entry.ExitFillNotional = (entry.ExitFillNotional ?? 0) + (exitFillPrice * exitFillQuantity);
            entry.ExitAvgFillPrice = entry.ExitFillNotional.Value / entry.ExitFilledQuantityTotal;

            HydrateCanonicalTimestampsFromLegacy(entry);
            entry.ExitFilledObservedAtUtc = utcNow.ToString("o");
            if (TryParseJournalIsoUtc(entry.EntryFilledAtUtc, out var entryFillCanon) &&
                utcNow < entryFillCanon &&
                (entryFillCanon - utcNow) > JournalEventOrderSkewTolerance)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "JOURNAL_EVENT_ORDER_VIOLATION", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        stream,
                        phase = "EXIT_FILL_OBSERVED_BEFORE_ENTRY_FILL_CANONICAL",
                        entry_filled_at_utc_canonical = entry.EntryFilledAtUtc,
                        exit_observed_at_utc = entry.ExitFilledObservedAtUtc,
                        note = "Exit fill observation precedes canonical entry fill — check clock or ordering."
                    }));
            }
            
            // Timestamp policy: Store first exit fill time only (canonical)
            if (string.IsNullOrEmpty(entry.ExitFilledAtUtc))
                entry.ExitFilledAtUtc = utcNow.ToString("o");
            
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
                if (entry.Rejected && IsProtectiveOrderRejection(entry.RejectionOrderType, entry.RejectionReason))
                    entry.Rejected = false;
                
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
                
                // Add entry/exit times if available (canonical fill time, not observation)
                var entryCanonForLog = !string.IsNullOrWhiteSpace(entry.EntryFilledAtUtc)
                    ? entry.EntryFilledAtUtc
                    : entry.EntryFilledAt;
                if (!string.IsNullOrWhiteSpace(entryCanonForLog))
                {
                    tradeData["entry_time_utc"] = entryCanonForLog;
                    if (DateTimeOffset.TryParse(entryCanonForLog, out var entryTime))
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
                completedIntentId = intentId;
                completedTradingDate = tradingDate;
                completedStream = stream;
                completedReason = entry.CompletionReason;
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

        if (!string.IsNullOrWhiteSpace(completedIntentId) &&
            !string.IsNullOrWhiteSpace(completedTradingDate) &&
            !string.IsNullOrWhiteSpace(completedStream))
        {
            _onTradeCompletedCallback?.Invoke(
                completedIntentId,
                completedTradingDate,
                completedStream,
                completedUtc,
                completedReason);
        }
    }

    private static bool TryUpgradeBrokerFlatCompletionFromTerminalFill(
        ExecutionJournalEntry entry,
        decimal exitFillPrice,
        int exitFillQuantity,
        string exitOrderType,
        DateTimeOffset utcNow,
        out string reason)
    {
        reason = "not_applicable";
        if (!string.Equals(entry.CompletionReason, CompletionReasons.RECONCILIATION_BROKER_FLAT, StringComparison.OrdinalIgnoreCase))
            return false;

        if (entry.EntryFilledQuantityTotal <= 0)
        {
            reason = "missing_entry_quantity";
            return false;
        }

        if (exitFillQuantity != entry.EntryFilledQuantityTotal)
        {
            reason = "terminal_fill_quantity_not_full_entry_quantity";
            return false;
        }

        if (!entry.EntryAvgFillPrice.HasValue)
        {
            reason = "missing_entry_avg_fill_price";
            return false;
        }

        if (!entry.ContractMultiplier.HasValue)
        {
            reason = "missing_contract_multiplier";
            return false;
        }

        var normalizedDirection = entry.Direction?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDirection))
        {
            reason = "missing_direction";
            return false;
        }

        decimal points;
        if (string.Equals(normalizedDirection, "Long", StringComparison.OrdinalIgnoreCase))
            points = exitFillPrice - entry.EntryAvgFillPrice.Value;
        else if (string.Equals(normalizedDirection, "Short", StringComparison.OrdinalIgnoreCase))
            points = entry.EntryAvgFillPrice.Value - exitFillPrice;
        else
        {
            reason = "invalid_direction";
            return false;
        }

        var grossDollars = points * entry.EntryFilledQuantityTotal * entry.ContractMultiplier.Value;
        var netDollars = grossDollars;
        if (entry.SlippageDollars.HasValue)
            netDollars -= entry.SlippageDollars.Value;
        if (entry.Commission.HasValue)
            netDollars -= entry.Commission.Value;
        if (entry.Fees.HasValue)
            netDollars -= entry.Fees.Value;

        entry.ExitFilledQuantityTotal = entry.EntryFilledQuantityTotal;
        entry.ExitFillNotional = exitFillPrice * entry.EntryFilledQuantityTotal;
        entry.ExitAvgFillPrice = exitFillPrice;
        entry.ExitFilledAtUtc = utcNow.ToString("o");
        if (!string.IsNullOrWhiteSpace(exitOrderType))
        {
            entry.ExitOrderType = exitOrderType;
            entry.CompletionReason = exitOrderType;
        }
        entry.CompletedAtUtc = utcNow.ToString("o");
        entry.RealizedPnLPoints = points;
        entry.RealizedPnLGross = grossDollars;
        entry.RealizedPnLNet = netDollars;

        reason = "full_terminal_fill_upgraded_broker_flat_completion";
        return true;
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

            var protectiveRejection = IsProtectiveOrderRejection(orderType, reason);
            var activeFilledIntent = protectiveRejection && entry.EntryFilled;
            if (activeFilledIntent)
            {
                entry.ProtectiveRejectionOrderType = orderType;
                entry.ProtectiveRejectedPrice = rejectedPrice;
                entry.ProtectiveRejectedQuantity = rejectedQuantity;
                entry.ProtectiveRejectedAt = utcNow.ToString("o");
                entry.ProtectiveRejectionReason = reason;
            }
            else
            {
                entry.RejectionOrderType = orderType;
                entry.RejectedPrice = rejectedPrice;
                entry.RejectedQuantity = rejectedQuantity;
                entry.RejectedAt = utcNow.ToString("o");
                entry.RejectionReason = reason;
            }
            if (string.IsNullOrWhiteSpace(entry.IntentId))
                entry.IntentId = intentId;
            if (string.IsNullOrWhiteSpace(entry.TradingDate) && !string.IsNullOrWhiteSpace(tradingDate))
                entry.TradingDate = tradingDate;
            if (string.IsNullOrWhiteSpace(entry.Stream) && !string.IsNullOrWhiteSpace(stream))
                entry.Stream = stream;

            if (!activeFilledIntent)
                entry.Rejected = true;

            _cache[key] = entry;
            SaveJournal(journalPath, entry);
        }
    }

    private static bool IsProtectiveOrderRejection(string? orderType, string? reason)
    {
        if (string.Equals(orderType, "STOP", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(orderType, "TARGET", StringComparison.OrdinalIgnoreCase))
            return true;

        var r = reason ?? "";
        return r.StartsWith("STOP_", StringComparison.OrdinalIgnoreCase) ||
               r.StartsWith("TARGET_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mark an entry order terminal when the broker cancels it before any fill occurs.
    /// This prevents dead opposite-side entry stops from lingering as adoption candidates.
    /// </summary>
    public bool RecordCancelledUnfilledEntry(
        string intentId,
        string tradingDate,
        string stream,
        DateTimeOffset utcNow,
        string completionReason = CompletionReasons.ENTRY_CANCELLED_UNFILLED)
    {
        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";
            var journalPath = GetJournalPath(tradingDate, stream, intentId);

            ExecutionJournalEntry? entry = null;
            if (_cache.TryGetValue(key, out var cached))
            {
                entry = cached;
            }
            else if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                }
                catch (Exception ex)
                {
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
                            note = "Journal corruption during RecordCancelledUnfilledEntry - standing down stream (fail-closed)"
                        }));

                    _onJournalCorruptionCallback?.Invoke(stream, tradingDate, intentId, utcNow);
                    return false;
                }
            }

            if (entry == null || entry.TradeCompleted || !entry.EntrySubmitted)
                return false;

            if (entry.EntryFilled || entry.EntryFilledQuantityTotal > 0)
                return false;

            entry.ExitOrderType = completionReason;
            entry.TradeCompleted = true;
            entry.CompletedAtUtc = utcNow.ToString("o");
            entry.CompletionReason = completionReason;
            entry.RealizedPnLPoints = null;
            entry.RealizedPnLGross = null;
            entry.RealizedPnLNet = null;

            _cache[key] = entry;
            SyncAdoptionCandidateIndexForIntentLocked(intentId, entry);
            SaveJournal(journalPath, entry);
            BumpReleaseSuppressionActivity();
            return true;
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
}
