using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Instrument Execution Authority (IEA) — single broker authority per (account, execution instrument).
/// Owns _orderMap and _intentMap so all strategy instances share the same tracking.
/// Phase 2: Owns aggregation and protective submission.
/// Spec: Per-instrument queue serializes ALL broker-state mutations (fills, BE, etc.).
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    private static int _instanceCounter;
    private readonly int _instanceId;

    /// <summary>Per-instrument execution queue. Serializes fills, BE, bracket mutations.</summary>
    private readonly BlockingCollection<Action> _executionQueue = new();
    private readonly Thread _workerThread;
    private volatile bool _workerRunning = true;
    private DateTimeOffset _lastMutationUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHeartbeatUtc = DateTimeOffset.MinValue;
    private const int HEARTBEAT_INTERVAL_SECONDS = 60;

    /// <summary>Gap 5: Monotonic sequence for debugging multi-instrument incidents.</summary>
    private long _enqueueSequence;
    private long _lastProcessedSequence;

    /// <summary>Gap 3: Processed execution IDs for deduplication. Key: ExecutionId or composite. Value: first-seen UTC.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _processedExecutionIds = new();
    private int _dedupInsertCount;
    private const int DEDUP_EVICTION_INTERVAL = 100;
    private const double DEDUP_MAX_AGE_MINUTES = 12.0;

    /// <summary>Execution ordering hardening metrics.</summary>
    private int _deferredExecutionCount;
    private int _duplicateExecutionCount;
    private int _executionResolutionRetries;

    /// <summary>Gap 1: Max queue depth before EnqueueAndWait fails closed.</summary>
    private const int MAX_QUEUE_DEPTH_FOR_ENQUEUE = 500;

    /// <summary>Gap 5: Instrument blocked after EnqueueAndWait failure (timeout/overflow). Fail-closed policy: no new work until restart.</summary>
    private volatile bool _instrumentBlocked;

    /// <summary>Queue health: current command tracking.</summary>
    private DateTimeOffset _currentWorkStartedUtc = DateTimeOffset.MinValue;
    private volatile string _currentWorkType = "";
    private DateTimeOffset _lastSuccessfulCompletionUtc = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private const int COMMAND_STALL_WARN_MS = 3000;
    private const int COMMAND_STALL_CRITICAL_MS = 8000;
    private const int POISON_THRESHOLD = 3;
    private DateTimeOffset _lastStallEmittedForStartUtc = DateTimeOffset.MinValue;
    private readonly Timer? _stallCheckTimer;

    /// <summary>Gap 5: Callback when instrument is blocked (notify engine to stand down streams, freeze instrument).</summary>
    private Action<string, DateTimeOffset, string>? _onEnqueueFailureCallback;

    /// <summary>Gap 5: Set callback for EnqueueAndWait failure. Called by adapter when binding IEA.</summary>
    internal void SetOnEnqueueFailureCallback(Action<string, DateTimeOffset, string>? callback)
    {
        _onEnqueueFailureCallback = callback;
    }

    /// <summary>Account name for this IEA.</summary>
    public string AccountName { get; }

    /// <summary>Execution instrument key (e.g., MNQ, MGC).</summary>
    public string ExecutionInstrumentKey { get; }

    /// <summary>Order executor for NT operations (adapter implements).</summary>
    internal readonly IIEAOrderExecutor? Executor;

    /// <summary>Logger for audit events.</summary>
    internal readonly RobotLogger? Log;

    /// <summary>Gap 5: Canonical event writer for replay. Set by adapter when binding.</summary>
    private ExecutionEventWriter? _eventWriter;

    internal void SetEventWriter(ExecutionEventWriter? writer) => _eventWriter = writer;

    /// <summary>Order tracking: intentId -> OrderInfo. Shared across all instances.</summary>
    internal readonly ConcurrentDictionary<string, OrderInfo> OrderMap = new();

    /// <summary>Intent tracking: intentId -> Intent. Shared across all instances.</summary>
    internal readonly ConcurrentDictionary<string, Intent> IntentMap = new();

    /// <summary>Intent policy: intentId -> policy expectation.</summary>
    internal readonly Dictionary<string, IntentPolicyExpectation> IntentPolicy = new();

    /// <summary>Unique instance ID for audit events (iea_instance_id).</summary>
    public int InstanceId => _instanceId;

    /// <summary>Phase 2: Bracket/aggregation policy. Null = use defaults (require identical, 0 tolerance).</summary>
    internal readonly AggregationPolicy? AggregationPolicy;

    /// <summary>Lock for entry submission when running on strategy thread (NT threading fix).
    /// NT CreateOrder/Submit must run on strategy thread; use this lock to serialize across instances sharing this IEA.</summary>
    internal readonly object EntrySubmissionLock = new object();

    /// <summary>Event clock for BE/dedup. Null = use UtcNow (live). Injected for replay.</summary>
    private readonly IEventClock? _eventClock;

    /// <summary>Wall clock for EnqueueAndWait timeouts. Null = use UtcNow (live). Injected for Full-System replay.</summary>
    private readonly IWallClock? _wallClock;

    public InstrumentExecutionAuthority(string accountName, string executionInstrumentKey, IIEAOrderExecutor? executor = null, RobotLogger? log = null, AggregationPolicy? aggregationPolicy = null, IEventClock? eventClock = null, IWallClock? wallClock = null)
    {
        AccountName = accountName ?? "";
        ExecutionInstrumentKey = (executionInstrumentKey ?? "").Trim().ToUpperInvariant();
        Executor = executor;
        Log = log;
        AggregationPolicy = aggregationPolicy;
        _eventClock = eventClock;
        _wallClock = wallClock;
        _instanceId = System.Threading.Interlocked.Increment(ref _instanceCounter);

        _workerThread = new Thread(WorkerLoop) { IsBackground = true, Name = $"IEA-{ExecutionInstrumentKey}-{_instanceId}" };
        _workerThread.Start();
        _stallCheckTimer = new Timer(CheckCommandStall, null, 2000, 2000);
    }

    private void CheckCommandStall(object? _)
    {
        var started = _currentWorkStartedUtc;
        if (started == DateTimeOffset.MinValue) return;
        var elapsed = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
        if (elapsed < COMMAND_STALL_CRITICAL_MS) return;
        if (started == _lastStallEmittedForStartUtc) return;
        _lastStallEmittedForStartUtc = started;
        var now = DateTimeOffset.UtcNow;
        Log?.Write(RobotEvents.EngineBase(now, tradingDate: "", eventType: "EXECUTION_COMMAND_STALLED", state: "ENGINE",
            new
            {
                iea_instance_id = InstanceId,
                execution_instrument_key = ExecutionInstrumentKey,
                current_command_type = _currentWorkType,
                runtime_ms = (long)elapsed,
                threshold_ms = COMMAND_STALL_CRITICAL_MS,
                policy = "IEA_COMMAND_STALL_SUPERVISOR_REQUIRED"
            }));
        _onEnqueueFailureCallback?.Invoke(ExecutionInstrumentKey, now, "EXECUTION_COMMAND_STALLED");
    }

    private void WorkerLoop()
    {
        while (_workerRunning)
        {
            try
            {
                if (_executionQueue.TryTake(out var work, 1000))
                {
                    _currentWorkStartedUtc = DateTimeOffset.UtcNow;
                    _currentWorkType = "QueueWork";
                    try
                    {
                        work();
                        _lastMutationUtc = DateTimeOffset.UtcNow;
                        _lastSuccessfulCompletionUtc = _lastMutationUtc;
                        _consecutiveFailures = 0;
                    }
                    finally
                    {
                        _currentWorkStartedUtc = DateTimeOffset.MinValue;
                        _currentWorkType = "";
                    }
                    EmitHeartbeatIfDue();
                }
            }
            catch (Exception ex)
            {
                var cf = Interlocked.Increment(ref _consecutiveFailures);
                Log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "IEA_QUEUE_WORKER_ERROR", state: "ENGINE",
                    new
                    {
                        error = ex.Message,
                        iea_instance_id = InstanceId,
                        execution_instrument_key = ExecutionInstrumentKey,
                        enqueue_sequence = Interlocked.Read(ref _enqueueSequence),
                        last_processed_sequence = Interlocked.Read(ref _lastProcessedSequence),
                        consecutive_failures = cf
                    }));
                if (cf >= POISON_THRESHOLD)
                {
                    _instrumentBlocked = true;
                    var utcNow = DateTimeOffset.UtcNow;
                    Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_QUEUE_POISON_DETECTED", state: "ENGINE",
                        new
                        {
                            iea_instance_id = InstanceId,
                            execution_instrument_key = ExecutionInstrumentKey,
                            consecutive_failures = cf,
                            threshold = POISON_THRESHOLD,
                            policy = "IEA_POISON_BLOCK_INSTRUMENT"
                        }));
                    _onEnqueueFailureCallback?.Invoke(ExecutionInstrumentKey, utcNow, "EXECUTION_QUEUE_POISON_DETECTED");
                }
            }
        }
    }

    /// <summary>Gap 2: Enqueue order update for serialized processing. Uses EnqueueRecoveryEssential so order state changes are processed during recovery (registry lifecycle).</summary>
    internal void EnqueueOrderUpdate(object order, object orderUpdate)
    {
        if (Executor == null) return;
        var o = order;
        var ou = orderUpdate;
        EnqueueRecoveryEssential(() => Executor.ProcessOrderUpdate(o, ou));
    }

    /// <summary>Enqueue recovery-essential work (execution/order updates). Always allowed; needed for flatten fills, stale snapshot detection, registry lifecycle.</summary>
    internal void EnqueueRecoveryEssential(Action work)
    {
        if (Executor == null) return;
        EnqueueCore(work);
    }

    /// <summary>Enqueue work for serialized processing. Blocks when IsInRecovery (normal management). Used for BE evaluation.</summary>
    internal void Enqueue(Action work)
    {
        if (Executor == null) return;
#if NINJATRADER
        if (IsInRecovery)
        {
            Log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "RECOVERY_GUARD_BLOCKED_NORMAL_MANAGEMENT", state: "ENGINE",
                new { operation = "Enqueue", recovery_state = CurrentRecoveryState.ToString(), iea_instance_id = InstanceId, execution_instrument_key = ExecutionInstrumentKey }));
            return;
        }
#endif
        EnqueueCore(work);
    }

    private void EnqueueCore(Action work)
    {
        try
        {
            var seq = Interlocked.Increment(ref _enqueueSequence);
            _executionQueue.Add(() =>
            {
                try
                {
                    work();
                }
                finally
                {
                    Interlocked.Exchange(ref _lastProcessedSequence, seq);
                }
            });
        }
        catch (InvalidOperationException) { /* queue completed */ }
    }

    /// <summary>Queue depth for heartbeat/observability.</summary>
    internal int QueueDepth => _executionQueue.Count;

    /// <summary>Last mutation timestamp for heartbeat.</summary>
    internal DateTimeOffset LastMutationUtc => _lastMutationUtc;

    private DateTimeOffset NowEvent() => _eventClock?.NowEvent() ?? DateTimeOffset.UtcNow;
    private DateTimeOffset NowWall() => _wallClock?.NowWall() ?? DateTimeOffset.UtcNow;

    /// <summary>
    /// Gap 1: Enqueue work and block until result. Used for entry submission and flatten.
    /// Deadlock guard: if called from worker thread, executes inline. Fail-fast on queue overflow or timeout.
    /// </summary>
    internal (bool success, T? result) EnqueueAndWait<T>(Func<T> work, int timeoutMs = 5000)
    {
        if (Executor == null) return (false, default);
        if (!_workerRunning) return (false, default);

#if NINJATRADER
        // Phase 3: Block normal management during recovery
        if (IsInRecovery)
        {
            Log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "RECOVERY_GUARD_BLOCKED_NORMAL_MANAGEMENT", state: "ENGINE",
                new { operation = "EnqueueAndWait", recovery_state = CurrentRecoveryState.ToString(), iea_instance_id = InstanceId, execution_instrument_key = ExecutionInstrumentKey }));
            return (false, default);
        }
#endif
        // Gap 5: Fail-closed policy — once blocked, reject all new work until restart.
        if (_instrumentBlocked)
        {
            Log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "IEA_ENQUEUE_REJECTED_INSTRUMENT_BLOCKED", state: "ENGINE",
                new
                {
                    iea_instance_id = InstanceId,
                    execution_instrument_key = ExecutionInstrumentKey,
                    enqueue_sequence = Interlocked.Read(ref _enqueueSequence),
                    last_processed_sequence = Interlocked.Read(ref _lastProcessedSequence),
                    policy = "IEA_ENQUEUE_FAILURE_BLOCKED_INSTRUMENT",
                    note = "Instrument blocked after prior EnqueueAndWait timeout/overflow; no new work accepted until restart"
                }));
            return (false, default);
        }

        if (QueueDepth > MAX_QUEUE_DEPTH_FOR_ENQUEUE)
        {
            _instrumentBlocked = true;
            var utcNow = NowWall();
            Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW", state: "ENGINE",
                new
                {
                    iea_instance_id = InstanceId,
                    execution_instrument_key = ExecutionInstrumentKey,
                    queue_depth = QueueDepth,
                    threshold = MAX_QUEUE_DEPTH_FOR_ENQUEUE,
                    enqueue_sequence = Interlocked.Read(ref _enqueueSequence),
                    last_processed_sequence = Interlocked.Read(ref _lastProcessedSequence),
                    policy = "IEA_FAIL_CLOSED_BLOCK_INSTRUMENT",
                    note = "Queue overflow — instrument blocked; no new intents until restart"
                }));
            _onEnqueueFailureCallback?.Invoke(ExecutionInstrumentKey, utcNow, "IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW");
            return (false, default);
        }
        if (Thread.CurrentThread == _workerThread)
            return (true, work());
        T? result = default;
        Exception? ex = null;
        var done = new ManualResetEventSlim(false);
        try
        {
            Enqueue(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception e) { ex = e; }
                finally { done.Set(); }
            });
            if (!done.Wait(timeoutMs))
            {
                _instrumentBlocked = true;
                var utcNow = NowWall();
                Log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "IEA_ENQUEUE_AND_WAIT_TIMEOUT", state: "ENGINE",
                    new
                    {
                        iea_instance_id = InstanceId,
                        execution_instrument_key = ExecutionInstrumentKey,
                        timeout_ms = timeoutMs,
                        enqueue_sequence = Interlocked.Read(ref _enqueueSequence),
                        last_processed_sequence = Interlocked.Read(ref _lastProcessedSequence),
                        policy = "IEA_FAIL_CLOSED_BLOCK_INSTRUMENT",
                        note = "Worker timeout — instrument blocked; no new intents until restart"
                    }));
                _onEnqueueFailureCallback?.Invoke(ExecutionInstrumentKey, utcNow, "IEA_ENQUEUE_AND_WAIT_TIMEOUT");
                return (false, default);
            }
            if (ex != null) throw ex;
            return (true, result);
        }
        finally
        {
            done.Dispose();
        }
    }

    /// <summary>
    /// Gap 7: Enqueue flatten work and block until result. Serializes flatten with fills and other mutations.
    /// Caller provides the work (e.g. FlattenWithRetryCore) so retry logic runs on worker.
    /// </summary>
    internal FlattenResult EnqueueFlattenAndWait(Func<FlattenResult> work, int timeoutMs = 10000)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var (success, result) = EnqueueAndWait(work, timeoutMs);
        if (!success) return FlattenResult.FailureResult("Flatten failed (IEA queue overflow or timeout)", utcNow);
        return result ?? FlattenResult.FailureResult("Flatten returned null", utcNow);
    }

    private void EmitHeartbeatIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastHeartbeatUtc).TotalSeconds < HEARTBEAT_INTERVAL_SECONDS) return;
        _lastHeartbeatUtc = now;
        CheckFlattenLatchTimeouts();
#if NINJATRADER
        EmitRecoveryMetrics(now);
        TryExpireCooldown(ExecutionInstrumentKey, now);
        EmitSupervisoryMetrics(now);
#endif
        // Phase 2: Registry cleanup and integrity verification
        var activeIntentIds = Executor != null
            ? new HashSet<string>(Executor.GetActiveIntentsForBEMonitoring(ExecutionInstrumentKey).Select(x => x.intentId), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RunRegistryCleanup(now, id => activeIntentIds.Contains(id));
        VerifyRegistryIntegrity(now);
        EmitRegistryMetrics(now);
        EmitExecutionOrderingMetrics(now);
        var currentStarted = _currentWorkStartedUtc;
        var runtimeMs = currentStarted != DateTimeOffset.MinValue
            ? (long)(now - currentStarted).TotalMilliseconds
            : (long?)null;
        Log?.Write(RobotEvents.EngineBase(now, tradingDate: "", eventType: "IEA_HEARTBEAT", state: "ENGINE",
            new
            {
                iea_instance_id = InstanceId,
                execution_instrument_key = ExecutionInstrumentKey,
                account_name = AccountName,
                queue_depth = QueueDepth,
                last_mutation_utc = _lastMutationUtc.ToString("o"),
                enqueue_sequence = Interlocked.Read(ref _enqueueSequence),
                last_processed_sequence = Interlocked.Read(ref _lastProcessedSequence),
                current_command_type = string.IsNullOrEmpty(_currentWorkType) ? null : _currentWorkType,
                current_command_runtime_ms = runtimeMs,
                consecutive_failures = _consecutiveFailures,
                last_successful_completion_utc = _lastSuccessfulCompletionUtc != DateTimeOffset.MinValue ? _lastSuccessfulCompletionUtc.ToString("o") : null
            }));
    }

    /// <summary>
    /// Register intent for fill callback handling.
    /// </summary>
    public void RegisterIntent(Intent intent)
    {
        var intentId = intent.ComputeIntentId();
        IntentMap[intentId] = intent;
    }

    /// <summary>
    /// Register intent policy expectation.
    /// </summary>
    public void RegisterIntentPolicy(
        string intentId,
        int expectedQty,
        int maxQty,
        string canonical,
        string execution,
        string policySource = "EXECUTION_POLICY_FILE")
    {
        IntentPolicy[intentId] = new IntentPolicyExpectation
        {
            ExpectedQuantity = expectedQty,
            MaxQuantity = maxQty,
            PolicySource = policySource,
            CanonicalInstrument = canonical,
            ExecutionInstrument = execution
        };
    }

    /// <summary>
    /// Add or update order in map (called when adapter submits orders).
    /// </summary>
    internal void AddOrUpdateOrder(string intentId, OrderInfo orderInfo)
    {
        OrderMap[intentId] = orderInfo;
    }

    /// <summary>
    /// Try get order info.
    /// </summary>
    internal bool TryGetOrder(string intentId, out OrderInfo? orderInfo)
    {
        return OrderMap.TryGetValue(intentId, out orderInfo);
    }

    /// <summary>
    /// Try get intent.
    /// </summary>
    public bool TryGetIntent(string intentId, out Intent? intent)
    {
        return IntentMap.TryGetValue(intentId, out intent);
    }

    /// <summary>
    /// Try get intent policy.
    /// </summary>
    internal bool TryGetIntentPolicy(string intentId, out IntentPolicyExpectation? policy)
    {
        return IntentPolicy.TryGetValue(intentId, out policy);
    }

    /// <summary>
    /// Gap 3: Deduplicate execution by primitives. Returns true if duplicate (caller should skip), false if new (marks as processed).
    /// Used by replay and by NT adapter (after parsing NT types to primitives).
    /// </summary>
    internal bool TryMarkAndCheckDuplicateCore(string? executionId, string orderId, long executionTimeTicks, int quantity, string marketPosition)
    {
        var key = !string.IsNullOrEmpty(executionId)
            ? executionId
            : $"{orderId}|{executionTimeTicks}|{quantity}|{marketPosition ?? ""}|{orderId}";
        if (string.IsNullOrEmpty(key)) return false;

        if (_processedExecutionIds.TryAdd(key, NowEvent()))
        {
            var count = Interlocked.Increment(ref _dedupInsertCount);
            if (count % DEDUP_EVICTION_INTERVAL == 0)
                EvictDedupEntries();
            return false;
        }
        Interlocked.Increment(ref _duplicateExecutionCount);
        return true;
    }

    /// <summary>Increment deferred execution count (adapter calls when deferring for registry resolution).</summary>
    internal void IncrementDeferredExecutionCount() => Interlocked.Increment(ref _deferredExecutionCount);

    /// <summary>Increment execution resolution retries (adapter calls when ProcessUnresolvedRetry re-enqueues).</summary>
    internal void IncrementExecutionResolutionRetries() => Interlocked.Increment(ref _executionResolutionRetries);

    /// <summary>
    /// Canonical ledger: position state. Net filled qty by execution instrument.
    /// </summary>
    public PositionState GetPositionState()
    {
        var state = new PositionState { ExecutionInstrumentKey = ExecutionInstrumentKey };
        int netLong = 0, netShort = 0;
        decimal sumLong = 0, sumShort = 0;
        foreach (var kvp in OrderMap)
        {
            var oi = kvp.Value;
            if (oi.IsEntryOrder && oi.State == "FILLED")
            {
                var qty = oi.FilledQuantity;
                if (oi.Direction == "Long") { netLong += qty; sumLong += (oi.Price ?? 0) * qty; }
                else if (oi.Direction == "Short") { netShort += qty; sumShort += (oi.Price ?? 0) * qty; }
            }
        }
        var net = netLong - netShort;
        state.NetFilledQty = Math.Abs(net);
        state.Direction = net > 0 ? "Long" : net < 0 ? "Short" : "";
        state.AverageFillPrice = net != 0 ? (net > 0 ? (sumLong > 0 ? sumLong / netLong : null) : (sumShort > 0 ? sumShort / netShort : null)) : null;
        return state;
    }

    /// <summary>
    /// Canonical ledger: bracket state. Working stop(s), target(s), state machine.
    /// </summary>
    public BracketState GetBracketState()
    {
        var state = new BracketState { Kind = BracketStateKind.NONE };
        var stopIds = new List<string>();
        var stopPrices = new List<decimal>();
        var targetIds = new List<string>();
        var targetPrices = new List<decimal>();
        foreach (var kvp in OrderMap)
        {
            var oi = kvp.Value;
            var ot = oi.OrderType ?? "";
            if ((ot.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0) && (oi.State == "SUBMITTED" || oi.State == "ACCEPTED" || oi.State == "WORKING"))
            {
                if (!string.IsNullOrEmpty(oi.OrderId)) stopIds.Add(oi.OrderId);
                if (oi.Price.HasValue) stopPrices.Add(oi.Price.Value);
            }
            else if ((ot.IndexOf("Limit", StringComparison.OrdinalIgnoreCase) >= 0) && (oi.State == "SUBMITTED" || oi.State == "ACCEPTED" || oi.State == "WORKING"))
            {
                if (!string.IsNullOrEmpty(oi.OrderId)) targetIds.Add(oi.OrderId);
                if (oi.Price.HasValue) targetPrices.Add(oi.Price.Value);
            }
        }
        if (stopIds.Count > 0 || targetIds.Count > 0)
        {
            state.Kind = BracketStateKind.WORKING;
            state.WorkingStopOrderIds = stopIds;
            state.WorkingStopPrices = stopPrices.Count > 0 ? stopPrices : null;
            state.WorkingTargetOrderIds = targetIds;
            state.WorkingTargetPrices = targetPrices.Count > 0 ? targetPrices : null;
        }
        return state;
    }
}
