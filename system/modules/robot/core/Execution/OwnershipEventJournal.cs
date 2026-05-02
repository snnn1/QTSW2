using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

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
    private readonly object _sequenceLock = new();
    private readonly object _fileWriteLock = new();
    private readonly object _writerStartLock = new();
    private readonly ConcurrentQueue<QueuedOwnershipEventWrite> _writeQueue = new();
    private readonly AutoResetEvent _writeSignal = new(false);
    private volatile bool _writerStarted;
    private int _pendingWriteCount;
    private int _lastBacklogWarningBucket;
    private const int BacklogWarningInterval = 100;

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
        lock (_sequenceLock)
        {
            if (!_nextSequence.TryGetValue(key, out var seq))
                seq = 0;
            _nextSequence[key] = seq + 1;
            return seq;
        }
    }

    /// <summary>
    /// Queue an ownership event record for durable persistence. Thread-safe and non-blocking for hot execution paths.
    /// </summary>
    public void Append(string tradingDate, OwnershipEventRecord record)
    {
        var td = string.IsNullOrWhiteSpace(tradingDate)
            ? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
            : tradingDate.Trim();

        _writeQueue.Enqueue(new QueuedOwnershipEventWrite(td, record));
        var pending = Interlocked.Increment(ref _pendingWriteCount);
        EnsureWriterStarted();
        _writeSignal.Set();

        var bucket = pending / BacklogWarningInterval;
        if (bucket > 0 && bucket != Volatile.Read(ref _lastBacklogWarningBucket) &&
            Interlocked.Exchange(ref _lastBacklogWarningBucket, bucket) != bucket)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "OWNERSHIP_EVENT_JOURNAL_BACKLOG", "ENGINE",
                new
                {
                    pending_writes = pending,
                    warning_interval = BacklogWarningInterval,
                    note = "Ownership event persistence is lagging, but execution workers are not blocked on file I/O."
                }));
        }
    }

    public bool Flush(TimeSpan timeout, string reason = "manual")
    {
        EnsureWriterStarted();
        _writeSignal.Set();

        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _pendingWriteCount) > 0 && sw.Elapsed < timeout)
            Thread.Sleep(10);

        var remaining = Volatile.Read(ref _pendingWriteCount);
        if (remaining > 0)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "OWNERSHIP_EVENT_JOURNAL_FLUSH_TIMEOUT", "ENGINE",
                new
                {
                    reason,
                    timeout_ms = (long)timeout.TotalMilliseconds,
                    pending_writes = remaining,
                    note = "Flush timed out; execution state remains in memory and persistence writer continues in the background."
                }));
            return false;
        }

        return true;
    }

    private void EnsureWriterStarted()
    {
        if (_writerStarted) return;
        lock (_writerStartLock)
        {
            if (_writerStarted) return;
            _writerStarted = true;
            var writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "QTSW2OwnershipEventJournalWriter"
            };
            writer.Start();
        }
    }

    private void WriterLoop()
    {
        while (true)
        {
            while (_writeQueue.TryDequeue(out var queued))
            {
                try
                {
                    WriteRecordSync(queued);
                }
                finally
                {
                    var remaining = Interlocked.Decrement(ref _pendingWriteCount);
                    if (remaining < BacklogWarningInterval)
                        Volatile.Write(ref _lastBacklogWarningBucket, 0);
                }
            }

            _writeSignal.WaitOne(TimeSpan.FromMilliseconds(250));
        }
    }

    private void WriteRecordSync(QueuedOwnershipEventWrite queued)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var dir = Path.Combine(_baseDir, queued.TradingDate);
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, "events.jsonl");

            var json = JsonSerializer.Serialize(queued.Record, OwnershipEventRecordJsonCtx.Default.OwnershipEventRecord);
            lock (_fileWriteLock)
            {
                File.AppendAllText(filePath, json + Environment.NewLine);
            }
            if (sw.ElapsedMilliseconds >= 500)
            {
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, queued.TradingDate, "OWNERSHIP_EVENT_JOURNAL_WRITE_SLOW", "ENGINE",
                    new
                    {
                        elapsed_ms = sw.ElapsedMilliseconds,
                        instrument = queued.Record.Instrument,
                        kind = queued.Record.Kind.ToString(),
                        pending_writes = Volatile.Read(ref _pendingWriteCount),
                        note = "Durable ownership event write was slow on the background writer thread."
                    }));
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "OWNERSHIP_EVENT_JOURNAL_WRITE_ERROR", "ENGINE",
                new { error = ex.Message, kind = queued.Record.Kind.ToString(), instrument = queued.Record.Instrument }));
        }
    }

    /// <summary>
    /// Read all ownership event records for a trading date, ordered by <see cref="OwnershipEventRecord.OwnershipEventSequence"/>.
    /// Used at restore time to replay events deterministically.
    /// </summary>
    public List<OwnershipEventRecord> ReadEvents(string tradingDate)
    {
        Flush(TimeSpan.FromMilliseconds(250), "read_events");

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
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                lines = reader.ReadToEnd()
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            }

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
            lock (_sequenceLock)
            {
                foreach (var group in records.GroupBy(r => r.Instrument?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    var maxSeq = group.Max(r => r.OwnershipEventSequence);
                    _nextSequence[group.Key] = maxSeq + 1;
                }
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

    private readonly struct QueuedOwnershipEventWrite
    {
        public string TradingDate { get; }
        public OwnershipEventRecord Record { get; }

        public QueuedOwnershipEventWrite(string tradingDate, OwnershipEventRecord record)
        {
            TradingDate = tradingDate;
            Record = record;
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
