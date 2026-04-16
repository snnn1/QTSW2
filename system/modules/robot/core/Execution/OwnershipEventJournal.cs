using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Durable, append-only journal for ALL ownership mutations (fills, transfers, orphan lifecycle).
/// Each record receives a monotonic <see cref="OwnershipEventRecord.OwnershipEventSequence"/>
/// assigned under the per-instrument lock BEFORE the JSONL write.
/// <para/>
/// Written under <c>events/ownership_events/{trading_date}/events.jsonl</c>.
/// This journal is the single durable backbone and replaces the Phase 2a class_a.jsonl persistence.
/// <para/>
/// The <see cref="OwnershipEventSequence"/> is per-instrument (same scope as ownershipVersion).
/// On restore, rows are ordered by this sequence to guarantee deterministic replay.
/// </summary>
public sealed class OwnershipEventJournal
{
    private readonly string _baseDir;
    private readonly RobotLogger _log;
    private readonly object _writeLock = new();

    /// <summary>Per-instrument monotonic counter. Assigned under the ledger's per-instrument lock.</summary>
    private readonly Dictionary<string, long> _nextSequence = new(StringComparer.OrdinalIgnoreCase);

    public OwnershipEventJournal(string persistenceBase, RobotLogger log)
    {
        _baseDir = Path.Combine(persistenceBase ?? "", "events", "ownership_events");
        _log = log;
    }

    /// <summary>
    /// Assign the next monotonic sequence for the given instrument. MUST be called under the
    /// ledger's per-instrument lock to guarantee ordering consistency with in-memory state.
    /// </summary>
    public long AssignNextSequence(string instrument)
    {
        var key = instrument?.Trim() ?? "";
        lock (_writeLock)
        {
            if (!_nextSequence.TryGetValue(key, out var seq))
                seq = 0;
            _nextSequence[key] = seq + 1;
            return seq;
        }
    }

    /// <summary>
    /// Persist an ownership event record. Thread-safe; uses its own write lock for file I/O.
    /// </summary>
    public void Append(string tradingDate, OwnershipEventRecord record)
    {
        try
        {
            var td = string.IsNullOrWhiteSpace(tradingDate)
                ? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
                : tradingDate.Trim();
            var dir = Path.Combine(_baseDir, td);
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, "events.jsonl");

            var json = JsonSerializer.Serialize(record, OwnershipEventRecordJsonCtx.Default.OwnershipEventRecord);
            lock (_writeLock)
            {
                File.AppendAllText(filePath, json + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "OWNERSHIP_EVENT_JOURNAL_WRITE_ERROR", "ENGINE",
                new { error = ex.Message, kind = record.Kind.ToString(), instrument = record.Instrument }));
        }
    }

    /// <summary>
    /// Read all ownership event records for a trading date, ordered by <see cref="OwnershipEventRecord.OwnershipEventSequence"/>.
    /// Used at restore time to replay events deterministically.
    /// </summary>
    public List<OwnershipEventRecord> ReadEvents(string tradingDate)
    {
        var td = string.IsNullOrWhiteSpace(tradingDate)
            ? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
            : tradingDate.Trim();
        var filePath = Path.Combine(_baseDir, td, "events.jsonl");
        if (!File.Exists(filePath))
            return new List<OwnershipEventRecord>();

        var records = new List<OwnershipEventRecord>();
        try
        {
            string[] lines;
            lock (_writeLock) { lines = File.ReadAllLines(filePath); }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var rec = JsonSerializer.Deserialize<OwnershipEventRecord>(line, OwnershipEventRecordJsonCtx.Default.OwnershipEventRecord);
                    if (rec != null) records.Add(rec);
                }
                catch { }
            }

            records.Sort((a, b) =>
            {
                var cmp = string.Compare(a.Instrument, b.Instrument, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                return a.OwnershipEventSequence.CompareTo(b.OwnershipEventSequence);
            });

            // Update in-memory sequence counters so live writes continue from max+1
            foreach (var group in records.GroupBy(r => r.Instrument?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var maxSeq = group.Max(r => r.OwnershipEventSequence);
                _nextSequence[group.Key] = maxSeq + 1;
            }

            return records;
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "OWNERSHIP_EVENT_JOURNAL_READ_ERROR", "ENGINE",
                new { error = ex.Message, trading_date = td }));
            return new List<OwnershipEventRecord>();
        }
    }
}

public sealed class OwnershipEventRecord
{
    public long OwnershipEventSequence { get; init; }
    public long OwnershipVersion { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OwnershipEventKind Kind { get; init; }

    public string Account { get; init; } = "";
    public string Instrument { get; init; } = "";
    public string IntentId { get; init; } = "";
    public string? FromIntentId { get; init; }
    public string? ToIntentId { get; init; }
    public int Qty { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SlotDirection Direction { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrphanReason OrphanReason { get; init; }

    public string EventUtc { get; init; } = "";
    public string? Detail { get; init; }
}

[JsonSerializable(typeof(OwnershipEventRecord))]
internal partial class OwnershipEventRecordJsonCtx : JsonSerializerContext { }
