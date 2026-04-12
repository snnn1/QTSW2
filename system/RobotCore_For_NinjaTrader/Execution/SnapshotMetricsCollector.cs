// Phase B: Snapshot metrics instrumentation.
// Central collector for GetAccountSnapshot calls. Emits aggregated stats for CPU audit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Collects GetAccountSnapshot call metrics. Singleton per process.
/// Emits aggregated stats: calls_per_sec, p50/p95/p99 duration, max_burst_50ms.
/// </summary>
public sealed class SnapshotMetricsCollector
{
    private static SnapshotMetricsCollector? _instance;
    private static readonly object _instanceLock = new();

    private readonly object _lock = new();
    private readonly List<CallRecord> _records = new();
    private const int MAX_RECORDS = 10000; // ~10 min at 14/s
    private const double WINDOW_SECONDS = 60.0;
    private const double BURST_WINDOW_MS = 50.0;
    private DateTimeOffset _lastEmitUtc = DateTimeOffset.MinValue;
    private const double EMIT_INTERVAL_SECONDS = 60.0;
    private readonly RobotLogger? _log;

    private struct CallRecord
    {
        public long TimestampTicks;
        public long DurationMs;
        public int ThreadId;
        public int PositionsCount;
        public int OrdersCount;
    }

    public static SnapshotMetricsCollector GetOrCreate(RobotLogger? log)
    {
        lock (_instanceLock)
        {
            if (_instance == null)
                _instance = new SnapshotMetricsCollector(log);
            return _instance;
        }
    }

    private SnapshotMetricsCollector(RobotLogger? log)
    {
        _log = log;
    }

    /// <summary>Record a GetAccountSnapshot call. Call from adapter layer.</summary>
    public void RecordCall(DateTimeOffset timestamp, long durationMs, int positionsCount, int ordersCount)
    {
        var threadId = Thread.CurrentThread.ManagedThreadId;
        lock (_lock)
        {
            _records.Add(new CallRecord
            {
                TimestampTicks = timestamp.UtcTicks,
                DurationMs = durationMs,
                ThreadId = threadId,
                PositionsCount = positionsCount,
                OrdersCount = ordersCount
            });
            if (_records.Count > MAX_RECORDS)
                _records.RemoveRange(0, _records.Count - MAX_RECORDS);
        }

        TryEmitIfDue(timestamp);
    }

    private void TryEmitIfDue(DateTimeOffset utcNow)
    {
        if (_log == null) return;
        if (_lastEmitUtc != DateTimeOffset.MinValue &&
            (utcNow - _lastEmitUtc).TotalSeconds < EMIT_INTERVAL_SECONDS)
            return;

        lock (_lock)
        {
            if (_lastEmitUtc != DateTimeOffset.MinValue &&
                (utcNow - _lastEmitUtc).TotalSeconds < EMIT_INTERVAL_SECONDS)
                return;

            var cutoff = utcNow.AddSeconds(-WINDOW_SECONDS).UtcTicks;
            var inWindow = _records.Where(r => r.TimestampTicks >= cutoff).ToList();
            if (inWindow.Count == 0)
            {
                _lastEmitUtc = utcNow;
                return;
            }

            var callsPerSec = inWindow.Count / WINDOW_SECONDS;
            var durations = inWindow.Select(r => r.DurationMs).OrderBy(x => x).ToList();
            var p50 = Percentile(durations, 0.50);
            var p95 = Percentile(durations, 0.95);
            var p99 = Percentile(durations, 0.99);

            var maxBurst = ComputeMaxBurst50Ms(inWindow);

            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SNAPSHOT_METRICS", state: "ENGINE",
                new
                {
                    calls_per_sec = Math.Round(callsPerSec, 2),
                    p50_duration_ms = p50,
                    p95_duration_ms = p95,
                    p99_duration_ms = p99,
                    max_burst_calls_in_50ms_window = maxBurst,
                    sample_count = inWindow.Count,
                    window_seconds = WINDOW_SECONDS,
                    note = "Phase B: GetAccountSnapshot aggregated metrics"
                }));

            _lastEmitUtc = utcNow;

            // Prune old records outside window
            var toRemove = _records.TakeWhile(r => r.TimestampTicks < cutoff).Count();
            if (toRemove > 0)
                _records.RemoveRange(0, toRemove);
        }
    }

    private static long Percentile(List<long> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        if (idx < 0) idx = 0;
        return sorted[idx];
    }

    private static int ComputeMaxBurst50Ms(List<CallRecord> records)
    {
        if (records.Count == 0) return 0;
        var ticksPer50Ms = (long)(BURST_WINDOW_MS * TimeSpan.TicksPerMillisecond);
        var maxBurst = 1;
        for (var i = 0; i < records.Count; i++)
        {
            var start = records[i].TimestampTicks;
            var count = records.Count(r => r.TimestampTicks >= start && r.TimestampTicks < start + ticksPer50Ms);
            if (count > maxBurst) maxBurst = count;
        }
        return maxBurst;
    }
}
