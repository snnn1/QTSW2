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

    public ExecutionJournal(string projectRoot, RobotLogger log)
    {
        _journalDir = Path.Combine(projectRoot, "data", "execution_journals");
        Directory.CreateDirectory(_journalDir);
        _log = log;
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
                // If journal is corrupted, treat as not submitted (fail open)
                // Log error for observability (fail-open is correct, but errors should be visible)
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate, "EXECUTION_JOURNAL_READ_ERROR", "ENGINE",
                    new
                    {
                        error = ex.Message,
                        exception_type = ex.GetType().Name,
                        intent_id = intentId,
                        stream = stream,
                        trading_date = tradingDate,
                        journal_path = journalPath,
                        note = "Journal read failed, treating as not submitted (fail-open for idempotency)"
                    }));
            }
        }

        return false;
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
        DateTimeOffset utcNow)
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
                // Journal read failed, create new entry (fail-open)
                // Log error for observability
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_READ_ERROR", "ENGINE",
                    new
                    {
                        error = ex.Message,
                        exception_type = ex.GetType().Name,
                        intent_id = intentId,
                        stream = stream,
                        trading_date = tradingDate,
                        journal_path = journalPath,
                        note = "Journal read failed during RecordSubmission, creating new entry"
                    }));
                entry = new ExecutionJournalEntry();
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
        }

        _cache[key] = entry;
        SaveJournal(journalPath, entry);
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
        DateTimeOffset utcNow)
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
                    // Journal read failed, create new entry (fail-open)
                    // Log error for observability
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_READ_ERROR", "ENGINE",
                        new
                        {
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            intent_id = intentId,
                            stream = stream,
                            trading_date = tradingDate,
                            journal_path = journalPath,
                            note = "Journal read failed during RecordFill, creating new entry"
                        }));
                    entry = new ExecutionJournalEntry();
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

        _cache[key] = entry;
        SaveJournal(journalPath, entry);
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
                    // Journal read failed, create new entry (fail-open)
                    // Log error for observability
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_READ_ERROR", "ENGINE",
                        new
                        {
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            intent_id = intentId,
                            stream = stream,
                            trading_date = tradingDate,
                            journal_path = journalPath,
                            note = "Journal read failed during RecordRejection, creating new entry"
                        }));
                    entry = new ExecutionJournalEntry();
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
                    // Journal read failed, create new entry (fail-open)
                    // Log error for observability
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "EXECUTION_JOURNAL_READ_ERROR", "ENGINE",
                        new
                        {
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            intent_id = intentId,
                            stream = stream,
                            trading_date = tradingDate,
                            journal_path = journalPath,
                            note = "Journal read failed during RecordBEModification, creating new entry"
                        }));
                    entry = new ExecutionJournalEntry();
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

    /// <summary>
    /// Check if BE modification was already attempted (prevent duplicates).
    /// </summary>
    public bool IsBEModified(string intentId, string tradingDate, string stream)
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
                // If journal is corrupted, treat as not modified (fail open)
                // Log error for observability
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate, "EXECUTION_JOURNAL_READ_ERROR", "ENGINE",
                    new
                    {
                        error = ex.Message,
                        exception_type = ex.GetType().Name,
                        intent_id = intentId,
                        stream = stream,
                        trading_date = tradingDate,
                        journal_path = journalPath,
                        note = "Journal read failed during IsBEModified check, treating as not modified (fail-open)"
                    }));
            }
        }

        return false;
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
}
