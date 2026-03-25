// Runtime CPU attribution, reconciliation convergence, loop-frequency and supervisory instability audits.
// ENGINE_CPU_PROFILE includes wall_window_ms, sum_subsystem_ms (parallel-root sum), estimated_parallelism_factor.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Diagnostics;

/// <summary>Subsystem names for ENGINE_CPU_PROFILE. Nested timers (e.g. STREAM_LOOP vs ENGINE_TICK) must not be summed with parents for parallelism.</summary>
public static class RuntimeAuditSubsystem
{
    public const string IeaWorkTotal = "IEA_WORK_TOTAL";
    public const string IeaScan = "IEA_SCAN";
    public const string RegistryVerify = "REGISTRY_VERIFY";
    public const string Reconciliation = "RECONCILIATION";
    public const string ReconciliationThrottle = "RECONCILIATION_THROTTLE";
    public const string AssembleMismatch = "ASSEMBLE_MISMATCH";
    public const string MismatchDiagnostics = "MISMATCH_DIAGNOSTICS";
    public const string ProtectiveTimerTotal = "PROTECTIVE_TIMER_TOTAL";
    public const string MismatchTimerTotal = "MISMATCH_TIMER_TOTAL";
    public const string EngineTickPerStream = "ENGINE_TICK";
    public const string StreamLoop = "STREAM_LOOP";
    public const string EngineLockRegion = "ENGINE_LOCK_REGION";
    public const string EngineTickTotal = "ENGINE_TICK_TOTAL";
    public const string EmitMetricsProtective = "EMIT_METRICS_PROTECTIVE";
    public const string EmitMetricsMismatch = "EMIT_METRICS_MISMATCH";
    public const string NtActionDrain = "NT_ACTION_DRAIN";

    internal static readonly string[] OrderedNames =
    {
        IeaWorkTotal, IeaScan, RegistryVerify, Reconciliation, ReconciliationThrottle,
        AssembleMismatch, MismatchDiagnostics, ProtectiveTimerTotal, MismatchTimerTotal,
        EngineTickPerStream, StreamLoop, EngineLockRegion, EngineTickTotal,
        EmitMetricsProtective, EmitMetricsMismatch, NtActionDrain
    };

    /// <summary>Subsystems whose wall time may overlap across threads (not double-counting strategy nested work).</summary>
    private static readonly HashSet<string> ParallelismRoots = new(StringComparer.Ordinal)
    {
        EngineTickTotal, MismatchTimerTotal, ProtectiveTimerTotal, IeaWorkTotal, NtActionDrain
    };

    public static bool IsParallelismRoot(string subsystem) => ParallelismRoots.Contains(subsystem);

    internal static int IndexOf(string subsystem)
    {
        for (var i = 0; i < OrderedNames.Length; i++)
        {
            if (string.Equals(OrderedNames[i], subsystem, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}

public static class RuntimeAuditHubRef
{
    private static RuntimeAuditHub? _active;

    public static RuntimeAuditHub? Active
    {
        get => _active;
        set => _active = value;
    }
}

public sealed class RuntimeAuditHub
{
    private const int SubsystemCount = 16;
    private const double ProfileIntervalSeconds = 5.0;
    private const int SixtySecondSlots = 12;

    private readonly RobotLogger _log;
    private readonly Func<string?> _getRunId;
    private readonly object _lock = new();

    private readonly long[] _win5TotalMs = new long[SubsystemCount];
    private readonly int[] _win5Count = new int[SubsystemCount];
    private readonly long[] _win5MaxMs = new long[SubsystemCount];

    private readonly long[,] _ring60 = new long[SixtySecondSlots, SubsystemCount];
    private int _ring60Head;

    private readonly long[] _lifeTotalMs = new long[SubsystemCount];
    private readonly int[] _lifeCount = new int[SubsystemCount];
    private readonly long[] _lifeMaxMs = new long[SubsystemCount];

    private DateTimeOffset _lastProfileUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastWallWindowStartUtc = DateTimeOffset.MinValue;

    /// <summary>Previous process CPU sample for ENGINE_CPU_PROFILE process_cpu_pct.</summary>
    private TimeSpan _procCpuAtLastSample = TimeSpan.Zero;

    private DateTimeOffset _procSampleUtc = DateTimeOffset.MinValue;

    private int _rateSecondBucket = -1;
    private readonly int[] _callsThisSecond = new int[SubsystemCount];
    private readonly int[] _lastSecondCalls = new int[SubsystemCount];

    // Stream distribution (5s window, strategy thread)
    private long _winStreamPerTickSumMs;
    private int _winStreamPerTickCount;
    private long _winSlowestStreamMs;
    private string? _winSlowestStreamId;
    private int _winMaxStreamsActive;
    private long _winStreamLoopTotalMs;
    private int _winStreamLoopTicks;

    // IEA idle wait (TryTake); active execution time = IEA_WORK_TOTAL subsystem window total
    private int _winIeaQueueDepthMax;
    private int _ieaQueueDepthApprox;

    public long HighFrequencyLoopEvents => Interlocked.Read(ref _highFreqBacking);

    public int SupervisoryInstabilityEvents { get; private set; }

    private readonly List<DateTimeOffset> _supervisoryInvalidWindow = new();
    private DateTimeOffset _lastSupervisoryInstabilityEmitUtc = DateTimeOffset.MinValue;

    private long _reconciliationRunCount;
    private long _highFreqBacking;

    public RuntimeAuditHub(RobotLogger log, Func<string?> getRunId)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _getRunId = getRunId ?? (() => null);
    }

    public static long CpuStart() => Stopwatch.GetTimestamp();

    /// <summary>Elapsed milliseconds since <see cref="CpuStart"/> (for stream-loop aggregates without double CpuEnd).</summary>
    public static long CpuElapsedMs(long startTimestamp)
    {
        if (startTimestamp == 0) return 0;
        var delta = Stopwatch.GetTimestamp() - startTimestamp;
        var ms = (long)(delta * 1000.0 / Stopwatch.Frequency);
        return ms < 0 ? 0 : ms;
    }

    public void CpuEnd(long startTimestamp, string subsystem, string? instrument = null, string? stream = null, bool onIeaWorker = false)
    {
        if (startTimestamp == 0) return;
        var idx = RuntimeAuditSubsystem.IndexOf(subsystem);
        if (idx < 0) return;
        var ms = ElapsedMs(startTimestamp);
        if (ms < 0) ms = 0;

        var utcNow = DateTimeOffset.UtcNow;
        List<(string subsystem, double cps, double threshold)>? hfViolations = null;
        lock (_lock)
        {
            Accumulate(idx, ms);
            hfViolations = RollCallRateAndCollectViolations(utcNow, idx);
            MaybeEmitProfileUnlocked(utcNow);
        }

        if (hfViolations != null)
        {
            foreach (var v in hfViolations)
                EmitHighFrequencyLoop(v.subsystem, v.cps, v.threshold);
        }

        _ = instrument;
        _ = stream;
        _ = onIeaWorker;
    }

    /// <summary>After each StreamStateMachine.Tick (strategy thread).</summary>
    public void RecordStreamTick(string streamId, long elapsedMs)
    {
        if (elapsedMs < 0) return;
        lock (_lock)
        {
            _winStreamPerTickSumMs += elapsedMs;
            _winStreamPerTickCount++;
            if (elapsedMs > _winSlowestStreamMs)
            {
                _winSlowestStreamMs = elapsedMs;
                _winSlowestStreamId = string.IsNullOrEmpty(streamId) ? "" : streamId;
            }
            MaybeEmitProfileUnlocked(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>After stream foreach completes (strategy thread).</summary>
    public void RecordStreamLoopAggregate(int streamsActiveCount, long streamLoopTotalMs)
    {
        lock (_lock)
        {
            if (streamsActiveCount > _winMaxStreamsActive)
                _winMaxStreamsActive = streamsActiveCount;
            _winStreamLoopTotalMs += streamLoopTotalMs;
            _winStreamLoopTicks++;
            MaybeEmitProfileUnlocked(DateTimeOffset.UtcNow);
        }
    }

    public void RecordIeaIdleMs(long ms)
    {
        if (ms <= 0) return;
        Interlocked.Add(ref _winIeaIdleMsBacking, ms);
    }

    private long _winIeaIdleMsBacking;

    public void NotifyIeaEnqueue()
    {
        var d = Interlocked.Increment(ref _ieaQueueDepthApprox);
        lock (_lock)
        {
            if (d > _winIeaQueueDepthMax) _winIeaQueueDepthMax = d;
        }
    }

    public void NotifyIeaDequeue()
    {
        Interlocked.Decrement(ref _ieaQueueDepthApprox);
    }

    public void NotifyReconciliationRunCompleted() => Interlocked.Increment(ref _reconciliationRunCount);

    public long ReconciliationRuns => Interlocked.Read(ref _reconciliationRunCount);

    private void EmitHighFrequencyLoop(string subsystem, double callsPerSecond, double threshold)
    {
        Interlocked.Increment(ref _highFreqBacking);
        var runId = _getRunId();
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HIGH_FREQUENCY_LOOP_DETECTED", state: "ENGINE",
            new
            {
                subsystem,
                calls_per_second = callsPerSecond,
                threshold,
                run_id = runId
            }));
    }

    public void NotifySupervisoryStateTransitionInvalid(DateTimeOffset utcNow)
    {
        lock (_lock)
        {
            _supervisoryInvalidWindow.Add(utcNow);
            var cutoff = utcNow.AddSeconds(-10);
            _supervisoryInvalidWindow.RemoveAll(t => t < cutoff);
            const int threshold = 6;
            if (_supervisoryInvalidWindow.Count >= threshold &&
                (utcNow - _lastSupervisoryInstabilityEmitUtc).TotalSeconds >= 10)
            {
                _lastSupervisoryInstabilityEmitUtc = utcNow;
                SupervisoryInstabilityEvents++;
                var runId = _getRunId();
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SUPERVISORY_INSTABILITY_DETECTED", state: "ENGINE",
                    new
                    {
                        count_10s = _supervisoryInvalidWindow.Count,
                        threshold,
                        run_id = runId
                    }));
            }
        }
    }

    public void TryEmitPeriodicWallClock(DateTimeOffset utcNow)
    {
        lock (_lock)
        {
            MaybeEmitProfileUnlocked(utcNow);
        }
    }

    public void EmitEngineAuditSummary(DateTimeOffset utcNow, string tradingDate)
    {
        lock (_lock)
        {
            MaybeEmitProfileUnlocked(utcNow);
        }

        var runId = _getRunId();
        var top = new List<(string name, long totalMs)>();
        for (var i = 0; i < SubsystemCount; i++)
        {
            if (_lifeCount[i] > 0)
                top.Add((RuntimeAuditSubsystem.OrderedNames[i], _lifeTotalMs[i]));
        }
        top.Sort((a, b) => b.totalMs.CompareTo(a.totalMs));
        var top3 = top.Count <= 3 ? top : top.GetRange(0, 3);

        var maxSingle = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < SubsystemCount; i++)
        {
            if (_lifeMaxMs[i] > 0)
                maxSingle[RuntimeAuditSubsystem.OrderedNames[i]] = _lifeMaxMs[i];
        }

        var rec = ReconciliationRuns;
        var conv = ReconciliationConvergenceTracker.SessionSnapshot;
        double pct(long n, long d) => d <= 0 ? 0 : 100.0 * n / d;
        var sigDenom = conv.ConvergedSignals + conv.StuckSignals + conv.OscillationSignals;

        var ieaIdx = RuntimeAuditSubsystem.IndexOf(RuntimeAuditSubsystem.IeaScan);
        long ieaTotal = ieaIdx >= 0 ? _lifeTotalMs[ieaIdx] : 0;
        int ieaCalls = ieaIdx >= 0 ? _lifeCount[ieaIdx] : 0;
        long ieaMax = ieaIdx >= 0 ? _lifeMaxMs[ieaIdx] : 0;

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: tradingDate, eventType: "ENGINE_AUDIT_SUMMARY", state: "ENGINE",
            new
            {
                run_id = runId,
                total_cpu_ms_by_subsystem = ToTotalsDict(),
                top_3_consumers = top3,
                max_single_execution_ms = maxSingle,
                total_reconciliation_runs = rec,
                convergence_rate_pct = new
                {
                    converged = pct(conv.ConvergedSignals, sigDenom),
                    stuck = pct(conv.StuckSignals, sigDenom),
                    oscillation = pct(conv.OscillationSignals, sigDenom)
                },
                iea_scan = new { total_scans = ieaCalls, avg_ms = ieaCalls > 0 ? (double)ieaTotal / ieaCalls : 0, max_ms = ieaMax },
                audit_events = new
                {
                    high_frequency_loop = HighFrequencyLoopEvents,
                    reconciliation_stuck = conv.StuckSignals,
                    reconciliation_oscillation = conv.OscillationSignals,
                    supervisory_instability = SupervisoryInstabilityEvents
                }
            }));
    }

    private Dictionary<string, long> ToTotalsDict()
    {
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < SubsystemCount; i++)
        {
            if (_lifeTotalMs[i] > 0)
                d[RuntimeAuditSubsystem.OrderedNames[i]] = _lifeTotalMs[i];
        }
        return d;
    }

    private void Accumulate(int idx, long ms)
    {
        _win5TotalMs[idx] += ms;
        _win5Count[idx]++;
        if (ms > _win5MaxMs[idx]) _win5MaxMs[idx] = ms;

        _lifeTotalMs[idx] += ms;
        _lifeCount[idx]++;
        if (ms > _lifeMaxMs[idx]) _lifeMaxMs[idx] = ms;
    }

    private static int UnixSeconds(DateTimeOffset utcNow)
    {
        var epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return (int)((utcNow - epoch).TotalSeconds);
    }

    private List<(string subsystem, double cps, double threshold)>? RollCallRateAndCollectViolations(DateTimeOffset utcNow, int idx)
    {
        var sec = UnixSeconds(utcNow);
        if (_rateSecondBucket < 0)
            _rateSecondBucket = sec;

        List<(string, double, double)>? list = null;
        if (sec != _rateSecondBucket)
        {
            for (var i = 0; i < SubsystemCount; i++)
                _lastSecondCalls[i] = _callsThisSecond[i];
            Array.Clear(_callsThisSecond, 0, SubsystemCount);
            _rateSecondBucket = sec;

            void Check(int si, double threshold)
            {
                if (si < 0) return;
                var c = _lastSecondCalls[si];
                if (c <= threshold) return;
                list ??= new List<(string, double, double)>();
                list.Add((RuntimeAuditSubsystem.OrderedNames[si], c, threshold));
            }

            Check(RuntimeAuditSubsystem.IndexOf(RuntimeAuditSubsystem.Reconciliation), 2.0);
            Check(RuntimeAuditSubsystem.IndexOf(RuntimeAuditSubsystem.IeaScan), 1.0);
        }

        _callsThisSecond[idx]++;
        return list;
    }

    private void MaybeEmitProfileUnlocked(DateTimeOffset utcNow)
    {
        if (_lastProfileUtc == DateTimeOffset.MinValue)
        {
            _lastProfileUtc = utcNow;
            _lastWallWindowStartUtc = utcNow;
            return;
        }
        if ((utcNow - _lastProfileUtc).TotalSeconds < ProfileIntervalSeconds)
            return;

        var wallWindowMs = Math.Max(1L, (long)Math.Round((utcNow - _lastWallWindowStartUtc).TotalMilliseconds));
        _lastWallWindowStartUtc = utcNow;
        _lastProfileUtc = utcNow;

        long sumParallelMs = 0;
        for (var i = 0; i < SubsystemCount; i++)
        {
            if (RuntimeAuditSubsystem.IsParallelismRoot(RuntimeAuditSubsystem.OrderedNames[i]))
                sumParallelMs += _win5TotalMs[i];
        }

        var parallelismFactor = wallWindowMs > 0 ? sumParallelMs / (double)wallWindowMs : 0;

        for (var i = 0; i < SubsystemCount; i++)
        {
            _ring60[_ring60Head, i] = _win5TotalMs[i];
        }
        _ring60Head = (_ring60Head + 1) % SixtySecondSlots;

        var payload5 = BuildWindowPayload(_win5TotalMs, _win5Count, _win5MaxMs);
        var sum60 = new long[SubsystemCount];
        for (var s = 0; s < SixtySecondSlots; s++)
        {
            for (var i = 0; i < SubsystemCount; i++)
                sum60[i] += _ring60[s, i];
        }
        var cnt60 = new int[SubsystemCount];
        for (var i = 0; i < SubsystemCount; i++)
        {
            for (var s = 0; s < SixtySecondSlots; s++)
            {
                if (_ring60[s, i] > 0) cnt60[i]++;
            }
        }
        var max60 = new long[SubsystemCount];
        for (var i = 0; i < SubsystemCount; i++)
        {
            for (var s = 0; s < SixtySecondSlots; s++)
            {
                if (_ring60[s, i] > max60[i]) max60[i] = _ring60[s, i];
            }
        }
        var payload60 = BuildWindowPayload(sum60, cnt60, max60);

        var ieaIdx = RuntimeAuditSubsystem.IndexOf(RuntimeAuditSubsystem.IeaWorkTotal);
        var ieaActive = ieaIdx >= 0 ? _win5TotalMs[ieaIdx] : 0;
        var ieaIdle = Interlocked.Exchange(ref _winIeaIdleMsBacking, 0);
        var streamAvg = _winStreamPerTickCount > 0 ? (double)_winStreamPerTickSumMs / _winStreamPerTickCount : 0;
        var streamLoopAvg = _winStreamLoopTicks > 0 ? (double)_winStreamLoopTotalMs / _winStreamLoopTicks : 0;

        var streamDistribution = new
        {
            streams_active_count_max = _winMaxStreamsActive,
            streams_processed_in_window = _winStreamPerTickCount,
            avg_ms_per_stream = streamAvg,
            slowest_stream_id = _winSlowestStreamId ?? "",
            slowest_stream_ms = _winSlowestStreamMs,
            stream_loop_total_ms_in_window = _winStreamLoopTotalMs,
            engine_ticks_with_stream_loop = _winStreamLoopTicks,
            avg_stream_loop_ms_per_engine_tick = streamLoopAvg
        };

        var ieaUtil = new
        {
            active_execution_time_ms_per_window = ieaActive,
            idle_time_ms_per_window = ieaIdle,
            queue_depth_max_in_window = _winIeaQueueDepthMax,
            queue_depth_approx_now = Volatile.Read(ref _ieaQueueDepthApprox)
        };

        Array.Clear(_win5TotalMs, 0, SubsystemCount);
        Array.Clear(_win5Count, 0, SubsystemCount);
        Array.Clear(_win5MaxMs, 0, SubsystemCount);

        _winStreamPerTickSumMs = 0;
        _winStreamPerTickCount = 0;
        _winSlowestStreamMs = 0;
        _winSlowestStreamId = null;
        _winMaxStreamsActive = 0;
        _winStreamLoopTotalMs = 0;
        _winStreamLoopTicks = 0;
        _winIeaQueueDepthMax = 0;

        var runId = _getRunId();
        var logicalProcessorCount = Environment.ProcessorCount;
        double? processCpuPct = null;
        try
        {
            var proc = Process.GetCurrentProcess();
            var cpuNow = proc.TotalProcessorTime;
            if (_procSampleUtc != DateTimeOffset.MinValue && logicalProcessorCount > 0)
            {
                var wallSec = Math.Max(0.001, (utcNow - _procSampleUtc).TotalSeconds);
                var cpuDeltaSec = (cpuNow - _procCpuAtLastSample).TotalSeconds;
                // Process CPU %: CPU time used / wall time / logical processors * 100 (0..100 scale).
                var raw = 100.0 * (cpuDeltaSec / (wallSec * logicalProcessorCount));
                if (raw < 0) raw = 0;
                if (raw > 100.0) raw = 100.0;
                processCpuPct = Math.Round(raw, 3);
            }

            _procCpuAtLastSample = cpuNow;
            _procSampleUtc = utcNow;
        }
        catch
        {
            _procSampleUtc = utcNow;
        }

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_CPU_PROFILE", state: "ENGINE",
            new
            {
                window_seconds = 5,
                wall_window_ms = wallWindowMs,
                sum_subsystem_ms = sumParallelMs,
                sum_subsystem_ms_note = "Sum of ENGINE_TICK_TOTAL + MISMATCH_TIMER_TOTAL + PROTECTIVE_TIMER_TOTAL + IEA_WORK_TOTAL + NT_ACTION_DRAIN (overlap-aware)",
                estimated_parallelism_factor = Math.Round(parallelismFactor, 3),
                process_cpu_pct = processCpuPct,
                logical_processor_count = logicalProcessorCount,
                subsystems = payload5,
                window_60_seconds = 60,
                subsystems_60s = payload60,
                stream_distribution = streamDistribution,
                iea_utilization = ieaUtil,
                run_id = runId
            }));

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "IEA_QUEUE_PRESSURE", state: "ENGINE",
            new
            {
                wall_window_ms = wallWindowMs,
                active_execution_time_ms_per_window = ieaActive,
                idle_time_ms_per_window = ieaIdle,
                queue_depth_max_in_window = ieaUtil.queue_depth_max_in_window,
                queue_depth_approx_now = ieaUtil.queue_depth_approx_now,
                run_id = runId,
                note = "IEA worker utilization + approximate queue depth; high depth + low active time suggests blocking elsewhere"
            }));
    }

    private static object[] BuildWindowPayload(long[] totals, int[] counts, long[] maxMs)
    {
        long grand = 0;
        for (var i = 0; i < SubsystemCount; i++)
            grand += totals[i];
        var list = new List<object>();
        for (var i = 0; i < SubsystemCount; i++)
        {
            if (counts[i] == 0 && totals[i] == 0) continue;
            var avg = counts[i] > 0 ? (double)totals[i] / counts[i] : 0;
            var pct = grand > 0 ? 100.0 * totals[i] / grand : 0;
            list.Add(new
            {
                name = RuntimeAuditSubsystem.OrderedNames[i],
                total_ms = totals[i],
                avg_ms = avg,
                max_ms = maxMs[i],
                calls = counts[i],
                pct_of_total = pct,
                parallelism_root = RuntimeAuditSubsystem.IsParallelismRoot(RuntimeAuditSubsystem.OrderedNames[i])
            });
        }
        return list.ToArray();
    }

    private static long ElapsedMs(long startTimestamp) => CpuElapsedMs(startTimestamp);
}

public sealed class ReconciliationConvergenceTracker
{
    public struct SessionSnapshotState
    {
        public long ConvergedSignals;
        public long StuckSignals;
        public long OscillationSignals;
    }

    private static long _sessionConverged;
    private static long _sessionStuck;
    private static long _sessionOscillation;

    public static SessionSnapshotState SessionSnapshot => new()
    {
        ConvergedSignals = Interlocked.Read(ref _sessionConverged),
        StuckSignals = Interlocked.Read(ref _sessionStuck),
        OscillationSignals = Interlocked.Read(ref _sessionOscillation)
    };

    private const int StuckThresholdPasses = 5;

    private readonly RobotLogger _log;
    private readonly Func<string?> _getRunId;
    private readonly object _lock = new();
    private readonly Dictionary<string, InstrumentConvergenceState> _byInstrument = new(StringComparer.OrdinalIgnoreCase);

    private sealed class InstrumentConvergenceState
    {
        public int LastMismatchHash;
        public int LastQtyDelta;
        public DateTimeOffset LastChangeUtc;
        public int ConsecutiveSameHash;
        public int OscillationCount;
        public bool HadMismatch;
        public int HashPrev2;
        public int HashPrev1;
        public int HashCurr;
        public DateTimeOffset? FirstMismatchUtc;
    }

    public ReconciliationConvergenceTracker(RobotLogger log, Func<string?> getRunId)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _getRunId = getRunId ?? (() => null);
    }

    public void OnInstrumentReconciliationSample(
        DateTimeOffset utcNow,
        string instrument,
        int accountQty,
        int journalQty,
        int openOrdersCount,
        int intentCount,
        bool hasMismatch,
        int qtyDelta)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        var inst = instrument.Trim();

        unchecked
        {
            var h = 17;
            h = h * 31 + accountQty;
            h = h * 31 + journalQty;
            h = h * 31 + openOrdersCount;
            h = h * 31 + intentCount;
            var hash = h;

            lock (_lock)
            {
                if (!_byInstrument.TryGetValue(inst, out var st))
                {
                    st = new InstrumentConvergenceState();
                    _byInstrument[inst] = st;
                }

                if (hash != st.HashCurr)
                {
                    st.HashPrev2 = st.HashPrev1;
                    st.HashPrev1 = st.HashCurr;
                    st.HashCurr = hash;
                    st.LastChangeUtc = utcNow;
                    st.ConsecutiveSameHash = 1;
                }
                else
                {
                    st.ConsecutiveSameHash++;
                }

                st.LastMismatchHash = hash;
                st.LastQtyDelta = qtyDelta;

                if (hasMismatch && !st.HadMismatch)
                    st.FirstMismatchUtc = utcNow;

                if (hasMismatch && st.ConsecutiveSameHash >= StuckThresholdPasses)
                {
                    Interlocked.Increment(ref _sessionStuck);
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "RECONCILIATION_STUCK", state: "ENGINE",
                        new
                        {
                            instrument = inst,
                            signature_name = "reconciliation_pass",
                            state_signature_hash = hash,
                            consecutive_same_state_count = st.ConsecutiveSameHash,
                            last_change_ts = st.LastChangeUtc.ToString("o"),
                            account_qty = accountQty,
                            journal_qty = journalQty,
                            open_orders_count = openOrdersCount,
                            intent_count = intentCount,
                            qty_delta = qtyDelta,
                            first_mismatch_ts = st.FirstMismatchUtc?.ToString("o"),
                            run_id = _getRunId()
                        }));
                }

                if (hasMismatch && st.HashPrev2 != 0 && st.HashCurr == st.HashPrev2 && st.HashPrev1 != 0 && st.HashCurr != st.HashPrev1)
                {
                    st.OscillationCount++;
                    Interlocked.Increment(ref _sessionOscillation);
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "RECONCILIATION_OSCILLATION", state: "ENGINE",
                        new
                        {
                            instrument = inst,
                            signature_name = "reconciliation_pass",
                            oscillation_count = st.OscillationCount,
                            hash_prev2 = st.HashPrev2,
                            hash_prev1 = st.HashPrev1,
                            hash_curr = st.HashCurr,
                            run_id = _getRunId()
                        }));
                }

                if (st.HadMismatch && !hasMismatch)
                {
                    long? ttc = null;
                    if (st.FirstMismatchUtc.HasValue)
                        ttc = (long)(utcNow - st.FirstMismatchUtc.Value).TotalMilliseconds;
                    Interlocked.Increment(ref _sessionConverged);
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "RECONCILIATION_CONVERGED", state: "ENGINE",
                        new
                        {
                            instrument = inst,
                            signature_name = "reconciliation_pass",
                            account_qty = accountQty,
                            journal_qty = journalQty,
                            first_mismatch_ts = st.FirstMismatchUtc?.ToString("o"),
                            converged_ts = utcNow.ToString("o"),
                            time_to_converge_ms = ttc,
                            run_id = _getRunId()
                        }));
                    st.HadMismatch = false;
                    st.ConsecutiveSameHash = 0;
                    st.OscillationCount = 0;
                    st.HashPrev2 = st.HashPrev1 = st.HashCurr = 0;
                    st.FirstMismatchUtc = null;
                }
                else if (hasMismatch)
                    st.HadMismatch = true;
            }
        }
    }
}
