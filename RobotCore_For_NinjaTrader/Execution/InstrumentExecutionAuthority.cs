using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
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
    private const double DEDUP_MAX_AGE_MINUTES = 5.0;

    /// <summary>Gap 1: Max queue depth before EnqueueAndWait fails closed.</summary>
    private const int MAX_QUEUE_DEPTH_FOR_ENQUEUE = 500;

    /// <summary>Gap 5: Instrument blocked after EnqueueAndWait failure (timeout/overflow). Fail-closed policy: no new work until restart.</summary>
    private volatile bool _instrumentBlocked;

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

    public InstrumentExecutionAuthority(string accountName, string executionInstrumentKey, IIEAOrderExecutor? executor = null, RobotLogger? log = null, AggregationPolicy? aggregationPolicy = null)
    {
        AccountName = accountName ?? "";
        ExecutionInstrumentKey = (executionInstrumentKey ?? "").Trim().ToUpperInvariant();
        Executor = executor;
        Log = log;
        AggregationPolicy = aggregationPolicy;
        _instanceId = System.Threading.Interlocked.Increment(ref _instanceCounter);

        _workerThread = new Thread(WorkerLoop) { IsBackground = true, Name = $"IEA-{ExecutionInstrumentKey}-{_instanceId}" };
        _workerThread.Start();
    }

    private void WorkerLoop()
    {
        while (_workerRunning)
        {
            try
            {
                if (_executionQueue.TryTake(out var work, 1000))
                {
                    work();
                    _lastMutationUtc = DateTimeOffset.UtcNow;
                    EmitHeartbeatIfDue();
                }
            }
            catch (Exception ex)
            {
                Log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "IEA_QUEUE_WORKER_ERROR", state: "ENGINE",
                    new
                    {
                        error = ex.Message,
                        iea_instance_id = InstanceId,
                        execution_instrument_key = ExecutionInstrumentKey,
                        enqueue_sequence = Interlocked.Read(ref _enqueueSequence),
                        last_processed_sequence = Interlocked.Read(ref _lastProcessedSequence)
                    }));
            }
        }
    }

    /// <summary>Gap 2: Enqueue order update for serialized processing. OrderMap mutations run on worker.</summary>
    internal void EnqueueOrderUpdate(object order, object orderUpdate)
    {
        if (Executor == null) return;
        var o = order;
        var ou = orderUpdate;
        Enqueue(() => Executor.ProcessOrderUpdate(o, ou));
    }

    /// <summary>Enqueue work for serialized processing. Used for execution updates and BE evaluation.</summary>
    internal void Enqueue(Action work)
    {
        if (Executor == null) return;
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

    /// <summary>
    /// Gap 1: Enqueue work and block until result. Used for entry submission and flatten.
    /// Deadlock guard: if called from worker thread, executes inline. Fail-fast on queue overflow or timeout.
    /// </summary>
    internal (bool success, T? result) EnqueueAndWait<T>(Func<T> work, int timeoutMs = 5000)
    {
        if (Executor == null) return (false, default);
        if (!_workerRunning) return (false, default);

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
            var utcNow = DateTimeOffset.UtcNow;
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
                var utcNow = DateTimeOffset.UtcNow;
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
        Log?.Write(RobotEvents.EngineBase(now, tradingDate: "", eventType: "IEA_HEARTBEAT", state: "ENGINE",
            new
            {
                iea_instance_id = InstanceId,
                execution_instrument_key = ExecutionInstrumentKey,
                account_name = AccountName,
                queue_depth = QueueDepth,
                last_mutation_utc = _lastMutationUtc.ToString("o"),
                enqueue_sequence = Interlocked.Read(ref _enqueueSequence),
                last_processed_sequence = Interlocked.Read(ref _lastProcessedSequence)
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
