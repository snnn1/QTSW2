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
    /// </summary>
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
}
