// Gap 5: Append-only canonical execution event writer.
// Persists one JSON object per line. Thread-safe. Fails safely on write error.

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Writes canonical execution events to append-only JSONL streams.
/// Per-instrument per-trading-day. Thread-safe. On write failure: log critical, continue.
/// </summary>
public sealed class ExecutionEventWriter
{
    private readonly string _projectRoot;
    private readonly Func<string> _getTradingDate;
    private readonly RobotLogger? _log;
    private readonly ExecutionEventSequence _sequence = new ExecutionEventSequence();
    private readonly object _writeLock = new object();

    private int _writeCount;
    private int _writeFailures;
    private readonly HashSet<string> _streamsOpened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public ExecutionEventWriter(string projectRoot, Func<string> getTradingDate, RobotLogger? log)
    {
        _projectRoot = projectRoot ?? throw new ArgumentNullException(nameof(projectRoot));
        _getTradingDate = getTradingDate ?? (() => "");
        _log = log;
    }

    /// <summary>
    /// Emit a canonical event. Assigns event_id, event_sequence, appends to stream.
    /// On failure: logs critical, does not throw.
    /// </summary>
    public void Emit(CanonicalExecutionEvent evt)
    {
        if (evt == null) return;

        var tradingDate = evt.TradingDate ?? _getTradingDate();
        var instrument = evt.Instrument ?? "";
        var streamKey = ExecutionEventSequence.BuildStreamKey(tradingDate, instrument);

        evt.EventId = Guid.NewGuid().ToString("N");
        evt.EventSequence = _sequence.GetNextSequence(streamKey);
        evt.TimestampUtc = string.IsNullOrEmpty(evt.TimestampUtc) ? DateTimeOffset.UtcNow.ToString("o") : evt.TimestampUtc;
        evt.TradingDate = string.IsNullOrEmpty(evt.TradingDate) ? tradingDate : evt.TradingDate;
        evt.EventFamily = string.IsNullOrEmpty(evt.EventFamily) ? ExecutionEventFamilies.GetFamily(evt.EventType).ToString() : evt.EventFamily;

        var path = GetStreamPath(tradingDate, instrument);
        var line = SerializeEvent(evt);

        lock (_writeLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(path, line + Environment.NewLine);

                _writeCount++;
                if (!_streamsOpened.Contains(streamKey))
                    _streamsOpened.Add(streamKey);
            }
            catch (Exception ex)
            {
                _writeFailures++;
                _log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate, "EXECUTION_EVENT_WRITE_FAILED", "CRITICAL",
                    new
                    {
                        error = ex.Message,
                        path,
                        event_type = evt.EventType,
                        event_id = evt.EventId,
                        note = "Canonical event append failed - continuing execution"
                    }));
            }
        }
    }

    private string GetStreamPath(string tradingDate, string instrument)
    {
        var td = string.IsNullOrWhiteSpace(tradingDate) ? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") : tradingDate.Trim();
        var inst = string.IsNullOrWhiteSpace(instrument) ? "_unknown" : SanitizeFileName(instrument);
        var baseDir = Path.Combine(_projectRoot, "automation", "logs", "execution_events", td);
        return Path.Combine(baseDir, inst + ".jsonl");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = name;
        foreach (var c in invalid)
            result = result.Replace(c, '_');
        return result.Trim().ToUpperInvariant();
    }

    private static string SerializeEvent(CanonicalExecutionEvent evt)
    {
        var dict = new Dictionary<string, object?>
        {
            ["event_id"] = evt.EventId,
            ["event_sequence"] = evt.EventSequence,
            ["event_family"] = evt.EventFamily,
            ["event_type"] = evt.EventType,
            ["timestamp_utc"] = evt.TimestampUtc,
            ["trading_date"] = evt.TradingDate,
            ["instrument"] = evt.Instrument,
            ["stream_key"] = evt.StreamKey,
            ["intent_id"] = evt.IntentId,
            ["command_id"] = evt.CommandId,
            ["broker_order_id"] = evt.BrokerOrderId,
            ["order_id"] = evt.OrderId,
            ["execution_id"] = evt.ExecutionId,
            ["lifecycle_state_before"] = evt.LifecycleStateBefore,
            ["lifecycle_state_after"] = evt.LifecycleStateAfter,
            ["severity"] = evt.Severity,
            ["source"] = evt.Source,
            ["payload"] = evt.Payload
        };
        return JsonUtil.Serialize(dict);
    }

    public int WriteCount => _writeCount;
    public int WriteFailures => _writeFailures;
    public int StreamsOpened => _streamsOpened.Count;
}
