// High-frequency execution trace: logs/robot/execution_trace.jsonl — adapter-level observability (no gate/coordinator changes).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace QTSW2.Robot.Core.Diagnostics;

/// <summary>
/// Append-only JSONL trace for NinjaTrader adapter callbacks. Thread-safe. Optional loop burst signal (lightweight).
/// </summary>
public sealed class ExecutionTraceWriter
{
    private const int RingCapacity = 50;
    private const double LoopWindowMs = 50.0;
    private const int LoopRepeatThreshold = 10; // >10 same pattern in window -> emit (i.e. 11+)

    private static long _globalSequence;

    private readonly string _path;
    private readonly object _ioLock = new();
    private readonly Dictionary<string, List<(long Ticks, string Pattern)>> _rings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastLoopEmitTicks = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private ExecutionTraceWriter(string path) => _path = path;

    /// <summary>Creates writer for logs/robot/execution_trace.jsonl under project root; returns null if path invalid.</summary>
    /// <remarks>Off by default; set env <c>QTSW2_EXECUTION_TRACE=1</c> to enable file I/O.</remarks>
    public static ExecutionTraceWriter? TryCreate(string projectRoot)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("QTSW2_EXECUTION_TRACE"), "1", StringComparison.Ordinal))
            return null;
        if (string.IsNullOrWhiteSpace(projectRoot)) return null;
        try
        {
            var dir = Path.Combine(projectRoot.Trim(), "logs", "robot");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "execution_trace.jsonl");
            return new ExecutionTraceWriter(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Primary trace line (EXECUTION_TRACE).</summary>
    public void WriteExecutionTrace(
        DateTimeOffset utcNow,
        string source,
        string callStage,
        string instrument,
        string intentId,
        string orderId,
        string executionId,
        int fillQty,
        string orderState)
    {
        var seq = Interlocked.Increment(ref _globalSequence);
        var line = new ExecutionTraceDto
        {
            TsUtc = utcNow.ToString("o", CultureInfo.InvariantCulture),
            Event = "EXECUTION_TRACE",
            Source = source ?? "",
            Instrument = instrument ?? "",
            IntentId = intentId ?? "",
            OrderId = orderId ?? "",
            ExecutionId = executionId ?? "",
            FillQty = fillQty,
            OrderState = orderState ?? "",
            CallStage = callStage ?? "",
            ThreadId = Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture),
            Sequence = seq
        };

        AppendLine(line);
        RecordPatternAndMaybeEmitLoop(utcNow, instrument, intentId, source, callStage, orderState, fillQty, seq);
    }

    private void AppendLine<T>(T payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            lock (_ioLock)
            {
                File.AppendAllText(_path, json + Environment.NewLine);
            }
        }
        catch
        {
            // Observability only — never throw into adapter
        }
    }

    private void RecordPatternAndMaybeEmitLoop(
        DateTimeOffset utcNow,
        string instrument,
        string intentId,
        string source,
        string callStage,
        string orderState,
        int fillQty,
        long sequence)
    {
        var inst = string.IsNullOrEmpty(instrument) ? "_" : instrument.Trim();
        var iid = string.IsNullOrEmpty(intentId) ? "_" : intentId.Trim();
        var ringKey = inst + "\u001f" + iid;
        var pattern = string.Join("|",
            source ?? "",
            callStage ?? "",
            orderState ?? "",
            fillQty.ToString(CultureInfo.InvariantCulture));

        var t = utcNow.UtcTicks;
        lock (_ioLock)
        {
            if (!_rings.TryGetValue(ringKey, out var ring))
            {
                ring = new List<(long, string)>(RingCapacity + 1);
                _rings[ringKey] = ring;
            }

            ring.Add((t, pattern));
            while (ring.Count > RingCapacity)
                ring.RemoveAt(0);

            var windowTicks = (long)(LoopWindowMs * TimeSpan.TicksPerMillisecond);
            var cutoff = t - windowTicks;
            var same = 0;
            long minPat = long.MaxValue, maxPat = long.MinValue;
            for (var i = 0; i < ring.Count; i++)
            {
                if (ring[i].Ticks < cutoff) continue;
                if (!string.Equals(ring[i].Pattern, pattern, StringComparison.Ordinal)) continue;
                same++;
                var tt = ring[i].Ticks;
                if (tt < minPat) minPat = tt;
                if (tt > maxPat) maxPat = tt;
            }

            if (same <= LoopRepeatThreshold)
                return;

            if (_lastLoopEmitTicks.TryGetValue(ringKey, out var lastEmit) && t - lastEmit < windowTicks)
                return;

            _lastLoopEmitTicks[ringKey] = t;

            var spanTicks = (minPat == long.MaxValue || maxPat == long.MinValue) ? 0L : maxPat - minPat;
            var windowMs = spanTicks <= 0 ? 0.0 : spanTicks / (double)TimeSpan.TicksPerMillisecond;

            var loopDto = new ExecutionTraceLoopDto
            {
                TsUtc = utcNow.ToString("o", CultureInfo.InvariantCulture),
                Event = "EXECUTION_TRACE_LOOP_DETECTED",
                Instrument = inst,
                IntentId = iid,
                RepetitionCount = same,
                TimeWindowMs = Math.Round(windowMs, 3),
                EventPattern = pattern,
                Source = source ?? "",
                Sequence = sequence
            };

            var json = JsonSerializer.Serialize(loopDto, JsonOpts);
            try
            {
                File.AppendAllText(_path, json + Environment.NewLine);
            }
            catch
            {
                // ignore
            }
        }
    }

    private sealed class ExecutionTraceDto
    {
        [JsonPropertyName("ts_utc")] public string TsUtc { get; set; } = "";
        [JsonPropertyName("event")] public string Event { get; set; } = "";
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        [JsonPropertyName("instrument")] public string Instrument { get; set; } = "";
        [JsonPropertyName("intent_id")] public string IntentId { get; set; } = "";
        [JsonPropertyName("order_id")] public string OrderId { get; set; } = "";
        [JsonPropertyName("execution_id")] public string ExecutionId { get; set; } = "";
        [JsonPropertyName("fill_qty")] public int FillQty { get; set; }
        [JsonPropertyName("order_state")] public string OrderState { get; set; } = "";
        [JsonPropertyName("call_stage")] public string CallStage { get; set; } = "";
        [JsonPropertyName("thread_id")] public string ThreadId { get; set; } = "";
        [JsonPropertyName("sequence")] public long Sequence { get; set; }
    }

    private sealed class ExecutionTraceLoopDto
    {
        [JsonPropertyName("ts_utc")] public string TsUtc { get; set; } = "";
        [JsonPropertyName("event")] public string Event { get; set; } = "";
        [JsonPropertyName("instrument")] public string Instrument { get; set; } = "";
        [JsonPropertyName("intent_id")] public string IntentId { get; set; } = "";
        [JsonPropertyName("repetition_count")] public int RepetitionCount { get; set; }
        [JsonPropertyName("time_window_ms")] public double TimeWindowMs { get; set; }
        [JsonPropertyName("event_pattern")] public string EventPattern { get; set; } = "";
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        [JsonPropertyName("sequence")] public long Sequence { get; set; }
    }
}
