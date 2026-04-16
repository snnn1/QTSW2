using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Append-only durable journal for orphan/untracked fills. Each record persists the fill details,
/// orphan reason, action taken, and back-reference to the ownership ledger slot.
/// Written under <c>events/orphan_fills/{trading_date}/</c>.
/// </summary>
public sealed class OrphanFillJournal
{
    private readonly string _baseDir;
    private readonly RobotLogger _log;
    private readonly Func<string?>? _getTradingDate;
    private readonly object _lock = new();

    public OrphanFillJournal(string persistenceBase, RobotLogger log, Func<string?>? getTradingDate = null)
    {
        _baseDir = Path.Combine(persistenceBase ?? "", "events", "orphan_fills");
        _log = log;
        _getTradingDate = getTradingDate;
    }

    /// <summary>
    /// Record an orphan fill. Appends a JSONL line to the trading date file.
    /// </summary>
    public void RecordOrphanFill(OrphanFillRecord record)
    {
        try
        {
            var tradingDate = ResolveTradingDate(record.TradingDate, DateTimeOffset.UtcNow);
            var dir = Path.Combine(_baseDir, tradingDate);
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, "orphan_fills.jsonl");

            var json = JsonSerializer.Serialize(record);
            lock (_lock)
            {
                File.AppendAllText(filePath, json + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "ORPHAN_FILL_JOURNAL_WRITE_ERROR", "ENGINE",
                new
                {
                    error = ex.Message,
                    broker_order_id = record.BrokerOrderId,
                    instrument = record.Instrument,
                    note = "Failed to persist orphan fill record"
                }));
        }
    }

    /// <summary>
    /// Update an existing orphan fill record with the flatten result.
    /// Appends a follow-up line (not an in-place edit — JSONL is append-only).
    /// </summary>
    public void RecordOrphanFlattenResult(string tradingDate, string orphanSlotId,
        OrphanActionTaken action, string? flattenError, DateTimeOffset utcNow)
    {
        try
        {
            var td = ResolveTradingDate(tradingDate, utcNow);
            var dir = Path.Combine(_baseDir, td);
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, "orphan_fills.jsonl");

            var update = new OrphanFillUpdateRecord
            {
                OrphanSlotId = orphanSlotId,
                ActionTaken = action,
                FlattenError = flattenError,
                UpdatedUtc = utcNow.ToString("o")
            };

            var json = JsonSerializer.Serialize(update);
            lock (_lock)
            {
                File.AppendAllText(filePath, json + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "ORPHAN_FILL_JOURNAL_UPDATE_ERROR", "ENGINE",
                new { error = ex.Message, orphan_slot_id = orphanSlotId }));
        }
    }

    /// <summary>
    /// Read all still-open orphan fills for a given trading date. An orphan is considered
    /// "still open" if no subsequent update record records <see cref="OrphanActionTaken.FlattenSucceeded"/>.
    /// Returns the list of <see cref="OrphanFillRecord"/> for orphans that remain unresolved.
    /// </summary>
    public List<OrphanFillRecord> ReadOrphanFills(string tradingDate)
    {
        var td = ResolveTradingDate(tradingDate, DateTimeOffset.UtcNow);
        var filePath = Path.Combine(_baseDir, td, "orphan_fills.jsonl");

        if (!File.Exists(filePath))
            return new List<OrphanFillRecord>();

        var fillsBySlotId = new Dictionary<string, OrphanFillRecord>(StringComparer.Ordinal);
        var resolvedSlotIds = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            string[] lines;
            lock (_lock) { lines = File.ReadAllLines(filePath); }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.Contains("\"OrphanSlotId\"") && !line.Contains("\"BrokerOrderId\""))
                {
                    try
                    {
                        var update = JsonSerializer.Deserialize<OrphanFillUpdateRecord>(line);
                        if (update != null && update.ActionTaken == OrphanActionTaken.FlattenSucceeded)
                            resolvedSlotIds.Add(update.OrphanSlotId);
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        var record = JsonSerializer.Deserialize<OrphanFillRecord>(line);
                        if (record != null && !string.IsNullOrEmpty(record.OwnershipLedgerSlotId))
                            fillsBySlotId[record.OwnershipLedgerSlotId] = record;
                    }
                    catch { }
                }
            }

            return fillsBySlotId
                .Where(kv => !resolvedSlotIds.Contains(kv.Key))
                .Select(kv => kv.Value)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "ORPHAN_FILL_JOURNAL_READ_ERROR", "ENGINE",
                new { error = ex.Message, trading_date = td }));
            return new List<OrphanFillRecord>();
        }
    }

    private string ResolveTradingDate(string? explicitTradingDate, DateTimeOffset utcNow)
    {
        if (!string.IsNullOrWhiteSpace(explicitTradingDate))
            return explicitTradingDate.Trim();

        try
        {
            var providerTradingDate = _getTradingDate?.Invoke();
            if (!string.IsNullOrWhiteSpace(providerTradingDate))
                return providerTradingDate.Trim();
        }
        catch { }

        return utcNow.ToString("yyyy-MM-dd");
    }

    private static class OrphanFillJsonContext
    {
        public static string Serialize(OrphanFillRecord record, object? _) =>
            JsonSerializer.Serialize(record);

        public static object Default => null!;
    }

    private static class OrphanUpdateJsonContext
    {
        public static string Serialize(OrphanFillUpdateRecord record, object? _) =>
            JsonSerializer.Serialize(record);

        public static object Default => null!;
    }
}

/// <summary>
/// Durable record for an orphan fill event.
/// </summary>
public sealed class OrphanFillRecord
{
    public string BrokerOrderId { get; init; } = "";
    public string? IntentIdIfKnown { get; init; }
    public string Instrument { get; init; } = "";
    public decimal FillPrice { get; init; }
    public int FillQty { get; init; }
    public string FillUtc { get; init; } = "";
    public string TradingDate { get; init; } = "";
    public OrphanReason OrphanReason { get; init; }
    public OrphanActionTaken ActionTaken { get; init; }
    public string? FlattenError { get; init; }
    public string? OwnershipLedgerSlotId { get; init; }
    public string RecordedUtc { get; init; } = "";
    /// <summary>Direction of the orphan fill. Added for restore support; may be absent in legacy records.</summary>
    public SlotDirection Direction { get; init; }
}

/// <summary>
/// Follow-up record appended after a flatten attempt completes.
/// </summary>
public sealed class OrphanFillUpdateRecord
{
    public string OrphanSlotId { get; init; } = "";
    public OrphanActionTaken ActionTaken { get; init; }
    public string? FlattenError { get; init; }
    public string UpdatedUtc { get; init; } = "";
}

public enum OrphanActionTaken
{
    FlattenAttempted,
    FlattenSucceeded,
    FlattenFailed,
    NoAction
}
