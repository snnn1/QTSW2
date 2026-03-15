// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// NinjaTrader Sim adapter: places orders in NT Sim account only.
/// 
/// Submission Sequencing (Safety-First Approach):
/// 1. Submit entry order (market order initially)
/// 2. On entry fill confirmation → submit protective stop + target (OCO pair)
/// 3. On BE trigger reached → modify stop to break-even
/// 4. On target/stop fill → flatten remaining position
/// 
/// Hard Safety Requirements:
/// - Must verify SIM account usage (fail closed if not Sim)
/// - All orders must be namespaced by (intent_id, stream) for isolation
/// - OCO grouping must be stream-local (no cross-stream interference)
/// </summary>
public sealed partial class NinjaTraderSimAdapter : IExecutionAdapter, IIEAOrderExecutor, INtActionExecutor, IIntentRegistrationAdapter
{
    private static int _adapterInstanceCounter;
    private readonly int _adapterInstanceId;

    private readonly RobotLogger _log;
    private readonly string _projectRoot;
    private readonly ExecutionJournal _executionJournal;
    
    // Order tracking: intentId -> NT order info (or IEA's when use_instrument_execution_authority)
    private readonly ConcurrentDictionary<string, OrderInfo> _orderMap = new();
    
    // Intent tracking: intentId -> Intent (or IEA's when IEA enabled)
    private readonly ConcurrentDictionary<string, Intent> _intentMap = new();
    
    // Fill callback: intentId -> callback action (for protective order submission)
    private readonly ConcurrentDictionary<string, Action<string, decimal, int, DateTimeOffset>> _fillCallbacks = new();
    
    // Intent policy tracking: intentId -> policy expectation (or IEA's when IEA enabled)
    private readonly Dictionary<string, IntentPolicyExpectation> _intentPolicy = new();
    
    // IEA: when enabled, use shared maps so all instances see same orders
    private bool _useInstrumentExecutionAuthority = false;
    private AggregationPolicy? _aggregationPolicy;
    private InstrumentExecutionAuthority? _iea;
    private string? _ieaAccountName;
    private string? _ieaEngineExecutionInstrument;
    private readonly Dictionary<string, DateTimeOffset> _lastIeaExecUpdateRoutedUtc = new();
    
    /// <summary>Effective order map: IEA's when enabled, else adapter's.</summary>
    private ConcurrentDictionary<string, OrderInfo> OrderMap => _useInstrumentExecutionAuthority && _iea != null ? _iea.OrderMap : _orderMap;
    /// <summary>Effective intent map: IEA's when enabled, else adapter's.</summary>
    private ConcurrentDictionary<string, Intent> IntentMap => _useInstrumentExecutionAuthority && _iea != null ? _iea.IntentMap : _intentMap;
    /// <summary>Effective intent policy: IEA's when enabled, else adapter's.</summary>
    private Dictionary<string, IntentPolicyExpectation> IntentPolicy => _useInstrumentExecutionAuthority && _iea != null ? _iea.IntentPolicy : _intentPolicy;
    
    // Track which intents have already triggered emergency (idempotent)
    private readonly HashSet<string> _emergencyTriggered = new();
    
    // NT Account and Instrument references (injected from Strategy host)
    private object? _ntAccount; // NinjaTrader.Cbi.Account
    private object? _ntInstrument; // NinjaTrader.Cbi.Instrument
    private bool _simAccountVerified = false;
    private bool _ntContextSet = false;
    
    // PHASE 2: Callback to stand down stream on protective order failure
    private Action<string, DateTimeOffset, string>? _standDownStreamCallback;
    
    // PHASE 2: Callback to get notification service for alerts
    private Func<object?>? _getNotificationServiceCallback;
    
    // PHASE 2: Callback to check if execution is allowed (recovery state guard)
    private Func<bool>? _isExecutionAllowedCallback;

    /// <summary>Gap 5: Callback when IEA EnqueueAndWait fails (timeout/overflow). Engine blocks instrument and stands down streams.</summary>
    private Action<string, DateTimeOffset, string>? _blockInstrumentCallback;

    /// <summary>Gap 5: Canonical event writer. Set by engine before SetNTContext.</summary>
    private ExecutionEventWriter? _eventWriter;

    /// <summary>Gap 5: Set canonical event writer for replay. Call before SetNTContext when using IEA.</summary>
    public void SetEventWriter(ExecutionEventWriter? writer) => _eventWriter = writer;
    private Action<string, string, object>? _onSupervisoryCriticalCallback;
    
    /// <summary>Callback when reentry order fills. Engine invokes HandleReentryFill on matching stream.</summary>
    private Action<string, DateTimeOffset>? _onReentryFillCallback;
    
    /// <summary>Callback when reentry protective bracket is accepted (both stop and target Working). Engine invokes HandleReentryProtectionAccepted on matching stream.</summary>
    private Action<string, DateTimeOffset>? _onReentryProtectionAcceptedCallback;
    
    /// <summary>Reentry intents for which we have already invoked the protection-accepted callback (idempotency).</summary>
    private readonly HashSet<string> _reentryProtectionAcceptedNotified = new(StringComparer.OrdinalIgnoreCase);
    
    // Intent exposure coordinator
    private InstrumentIntentCoordinator? _coordinator;
    
    // Rate-limiting for INSTRUMENT_MISMATCH logging (operational hygiene)
    // Prevents log flooding when instrument mismatch persists
    private readonly Dictionary<string, DateTimeOffset> _lastInstrumentMismatchLogUtc = new();
    private const int INSTRUMENT_MISMATCH_RATE_LIMIT_MINUTES = 60; // Log at most once per hour per instrument
    
    // Diagnostic: Track rate-limit diagnostic logs separately
    private readonly Dictionary<string, DateTimeOffset> _lastInstrumentMismatchDiagLogUtc = new();

    // Canonical fill: monotonic sequence per execution_instrument_key (reproducible under replay)
    private static readonly ConcurrentDictionary<string, int> _executionSequenceByKey = new();
    private static readonly object _executionSequenceLock = new();

    // Broker flatten recognition: when we call Flatten(), the resulting close order has no QTSW2 tag.
    // Track recent flatten calls so we don't treat that fill as UNTrackED and trigger another flatten.
    private readonly object _flattenRecognitionLock = new();
    private string? _lastFlattenInstrument;
    private DateTimeOffset _lastFlattenUtc;
    private const int FLATTEN_RECOGNITION_WINDOW_SECONDS = 10;

    /// <summary>Non-IEA path dedup: key (orderId, executionId or composite) -> first-seen UTC. TTL ~5 min.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonIeaExecutionDedup = new();

    /// <summary>Stage 1: Pending protective submissions during recovery. Processed when broker ready or fail-safe timeout.</summary>
    private readonly List<PendingRecoveryProtective> _pendingRecoveryProtectives = new();
    private readonly object _pendingRecoveryLock = new();
    private const double RECOVERY_PROTECTIVE_TIMEOUT_SECONDS = 12.0;

    /// <summary>Non-IEA path: pending unresolved executions for deferred retry (no Thread.Sleep).</summary>
    private readonly List<UnresolvedExecutionRecord> _pendingUnresolvedExecutions = new();
    private readonly object _pendingUnresolvedLock = new();
    private const double UNRESOLVED_GRACE_MS = 500.0;
    private const int UNRESOLVED_MAX_RETRIES = 5;
    private const double UNRESOLVED_RETRY_INTERVAL_MS = 75.0;
    private int _nonIeaDedupInsertCount;
    private const int NON_IEA_DEDUP_EVICTION_INTERVAL = 100;
    private const double NON_IEA_DEDUP_MAX_AGE_MINUTES = 5.0;

    /// <summary>Non-IEA dedup. Returns true if duplicate (skip), false if new (marks). Key built by NT partial.</summary>
    internal bool TryMarkAndCheckDuplicateNonIea(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var now = DateTimeOffset.UtcNow;
        if (_nonIeaExecutionDedup.TryAdd(key, now))
        {
            var c = System.Threading.Interlocked.Increment(ref _nonIeaDedupInsertCount);
            if (c % NON_IEA_DEDUP_EVICTION_INTERVAL == 0)
                EvictNonIeaDedupEntries();
            return false;
        }
        return true;
    }
    private void EvictNonIeaDedupEntries()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-NON_IEA_DEDUP_MAX_AGE_MINUTES);
        foreach (var k in _nonIeaExecutionDedup.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList())
            _nonIeaExecutionDedup.TryRemove(k, out _);
    }

    /// <summary>Flatten verification: instrumentKey -> (requestedUtc, retryCount, deadline, correlationId, instrumentRef).
    /// instrumentRef: strategy's Instrument instance used for position check (avoids string-resolution ambiguity).</summary>
    private readonly ConcurrentDictionary<string, (DateTimeOffset RequestedUtc, int RetryCount, DateTimeOffset VerifyDeadlineUtc, string CorrelationId, object? InstrumentRef)> _pendingFlattenVerifications = new();
    private const double FLATTEN_VERIFY_WINDOW_SEC = 4.0;
    private const int FLATTEN_VERIFY_MAX_RETRIES = 4;

    // BE Modify Confirmation: pending requests keyed by stop_order_id. Register BEFORE Change(); confirm via OrderUpdate.
    private readonly ConcurrentDictionary<string, PendingBERequest> _pendingBERequests = new();

    /// <summary>BE Phase 2: Per-intent protection state. Prevents false flatten when protectives are pending/in-flight.</summary>
    internal enum ProtectionState { None, Enqueued, Executing, Submitted, Working }
    private readonly ConcurrentDictionary<string, ProtectionState> _protectionStateByIntent = new(StringComparer.OrdinalIgnoreCase);
    private const double BE_STOP_VISIBILITY_TIMEOUT_SEC = 5.0;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstMissingStopUtcByIntent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Part 3 (optional): Enqueued backlog warning/timeout. Default OFF.</summary>
    private const bool ENABLE_PROTECTION_ENQUEUED_TIMEOUT = false;
    private const double PROTECTION_ENQUEUED_WARN_SEC = 5;
    private const double PROTECTION_ENQUEUED_TIMEOUT_SEC = 30;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstEnqueuedUtcByIntent = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastEnqueuedWarnUtcByIntent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Strategy-thread NT action queue. Worker enqueues; strategy drains under EntrySubmissionLock.</summary>
    private StrategyThreadExecutor? _ntActionQueue;

    /// <summary>Strategy-thread context: ref-count for nested Enter/Exit, and thread ID for strict validation.</summary>
    [ThreadStatic] private static int _strategyThreadContextCount;
    [ThreadStatic] private static int _strategyThreadId;

    private void SetStrategyThreadContext(bool value)
    {
        if (value)
        {
            _strategyThreadContextCount++;
            if (_strategyThreadContextCount == 1)
                _strategyThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }
        else
        {
            if (_strategyThreadContextCount > 0)
                _strategyThreadContextCount--;
            if (_strategyThreadContextCount == 0)
                _strategyThreadId = 0;
        }
    }

    /// <summary>
    /// Guard: ensure we're on strategy thread before calling NT APIs.
    /// If not on strategy thread: emit NT_THREAD_VIOLATION, enqueue action, return false.
    /// In DEBUG: assert. In RELEASE: fail-safe by enqueuing.
    /// </summary>
    /// <returns>true if action ran; false if enqueued (caller should return without touching NT APIs).</returns>
    private bool EnsureStrategyThreadOrEnqueue(string methodName, string? intentId, string? instrument, string? correlationId, Action ntAction)
    {
        var currentThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        var inContext = _strategyThreadContextCount > 0 && _strategyThreadId == currentThreadId;
        if (inContext)
        {
            ntAction();
            return true;
        }
        var utcNow = DateTimeOffset.UtcNow;
        var cid = correlationId ?? $"GUARD:{methodName}:{intentId ?? ""}:{instrument ?? ""}:{utcNow:yyyyMMddHHmmssfff}";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "NT_THREAD_VIOLATION", state: "CRITICAL",
            new
            {
                method = methodName,
                intent_id = intentId,
                instrument = instrument,
                correlation_id = cid,
                note = "NT API called from non-strategy thread - action enqueued for strategy thread"
            }));
#if DEBUG
        System.Diagnostics.Debug.Fail($"NT_THREAD_VIOLATION: {methodName} called from worker thread. Action enqueued.");
#endif
        if (_ntActionQueue != null)
            _ntActionQueue.EnqueueNtAction(new NtDeferredAction(cid, intentId, instrument, $"THREAD_VIOLATION:{methodName}", ntAction), out _);
        _guardViolationCallback?.Invoke(methodName);
        return false;
    }

    /// <summary>Optional callback for canary tests. Invoked when guard triggers (NT_THREAD_VIOLATION).</summary>
    private Action<string>? _guardViolationCallback;
    internal void SetGuardViolationCallback(Action<string>? callback) => _guardViolationCallback = callback;

    bool IIEAOrderExecutor.EnqueueNtAction(INtAction action)
    {
        if (_ntActionQueue == null) return false;
        if (string.Equals(action.ActionType, "SUBMIT_PROTECTIVES", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(action.IntentId))
        {
            SetProtectionState(action.IntentId, ProtectionState.Enqueued);
            _firstEnqueuedUtcByIntent.TryAdd(action.IntentId, DateTimeOffset.UtcNow);
        }
        return _ntActionQueue.EnqueueNtAction(action, out _);
    }

    private void SetProtectionState(string intentId, ProtectionState state, string? reason = null)
    {
        if (string.IsNullOrEmpty(intentId)) return;
        var prev = _protectionStateByIntent.AddOrUpdate(intentId, state, (_, _) => state);
        if (state != ProtectionState.Enqueued)
            _firstEnqueuedUtcByIntent.TryRemove(intentId, out _);
        var shouldLog = prev != state || !string.IsNullOrEmpty(reason);
        if (shouldLog)
        {
            var accountName = _iea?.AccountName ?? "";
            var execInstKey = _iea?.ExecutionInstrumentKey ?? "";
            var payload = new Dictionary<string, object>
            {
                ["intent_id"] = intentId,
                ["execution_instrument_key"] = execInstKey,
                ["account_name"] = accountName,
                ["previous_state"] = prev.ToString(),
                ["new_state"] = state.ToString()
            };
            if (!string.IsNullOrEmpty(reason))
                payload["reason"] = reason;
            _log.Write(RobotEvents.ExecutionBase(DateTimeOffset.UtcNow, intentId, "", "BE_PROTECTION_STATE_CHANGED", payload));
        }
    }

    private ProtectionState GetProtectionState(string intentId)
    {
        return string.IsNullOrEmpty(intentId) ? ProtectionState.None : _protectionStateByIntent.TryGetValue(intentId, out var s) ? s : ProtectionState.None;
    }

    /// <summary>Remove intent from BE state dictionaries. Call when intent is terminal/removed.</summary>
    private void PruneIntentState(string intentId, string reason)
    {
        if (string.IsNullOrEmpty(intentId)) return;
        var removed = _protectionStateByIntent.TryRemove(intentId, out _);
        _firstMissingStopUtcByIntent.TryRemove(intentId, out _);
        _firstEnqueuedUtcByIntent.TryRemove(intentId, out _);
        _lastEnqueuedWarnUtcByIntent.TryRemove(intentId, out _);
        if (removed)
            _log.Write(RobotEvents.ExecutionBase(DateTimeOffset.UtcNow, intentId, "", "BE_PROTECTION_STATE_PRUNED", new { intent_id = intentId, reason }));
    }

    object? IIEAOrderExecutor.GetEntrySubmissionLock() => _iea?.EntrySubmissionLock;

    void IIEAOrderExecutor.EnterStrategyThreadContext() => SetStrategyThreadContext(true);
    void IIEAOrderExecutor.ExitStrategyThreadContext() => SetStrategyThreadContext(false);

    void IIEAOrderExecutor.SetProtectionStateWorkingForAdoptedStop(string intentId)
    {
        if (string.IsNullOrEmpty(intentId)) return;
        SetProtectionState(intentId, ProtectionState.Working, "ADOPT_RESTART_STOP");
    }

    void IIEAOrderExecutor.DrainNtActions()
    {
        ProcessRecoveryQueue();
        if (_ntActionQueue != null && this is INtActionExecutor executor)
            _ntActionQueue.DrainNtActions(executor);
        OnVerifyPendingFlattens();
    }

    bool IIEAOrderExecutor.TryQueueProtectiveForRecovery(string intentId, Intent intent, int totalFilledQuantity, DateTimeOffset utcNow)
    {
        if (_isExecutionAllowedCallback == null || _isExecutionAllowedCallback())
            return false;
        QueueProtectiveForRecovery(intentId, intent, totalFilledQuantity, utcNow);
        return true;
    }

    /// <summary>
    /// Stage 1: Add to queue. Stage 2/3: ProcessRecoveryQueue submits or fail-safe flattens.
    /// </summary>
    private void QueueProtectiveForRecovery(string intentId, Intent intent, int totalFilledQuantity, DateTimeOffset utcNow)
    {
        lock (_pendingRecoveryLock)
        {
            _pendingRecoveryProtectives.Add(new PendingRecoveryProtective(intentId, intent, totalFilledQuantity, utcNow));
        }
    }

    /// <summary>
    /// Stage 2: Submit queued protectives when broker ready. Stage 3: Fail-safe flatten if timeout exceeded.
    /// Called at start of DrainNtActions (strategy thread).
    /// </summary>
    private void ProcessRecoveryQueue()
    {
        List<PendingRecoveryProtective> snapshot;
        lock (_pendingRecoveryLock)
        {
            if (_pendingRecoveryProtectives.Count == 0) return;
            snapshot = new List<PendingRecoveryProtective>(_pendingRecoveryProtectives);
            _pendingRecoveryProtectives.Clear();
        }

        var utcNow = DateTimeOffset.UtcNow;
        var executionAllowed = _isExecutionAllowedCallback == null || _isExecutionAllowedCallback();

        foreach (var pending in snapshot)
        {
            var elapsed = (utcNow - pending.QueuedAtUtc).TotalSeconds;

            // Stage 3: Fail-safe timer — flatten if protectives cannot be placed within timeout
            if (elapsed > RECOVERY_PROTECTIVE_TIMEOUT_SECONDS)
            {
                var failureReason = $"Recovery protective timeout ({RECOVERY_PROTECTIVE_TIMEOUT_SECONDS}s) - protective orders not placed in time";
                _log.Write(RobotEvents.ExecutionBase(utcNow, pending.IntentId, pending.Intent.Instrument, "RECOVERY_PROTECTIVE_TIMEOUT_FLATTENED",
                    new
                    {
                        intent_id = pending.IntentId,
                        elapsed_seconds = elapsed,
                        timeout_seconds = RECOVERY_PROTECTIVE_TIMEOUT_SECONDS,
                        note = "Fail-safe: position flattened after recovery protective timeout"
                    }));
                FailClosed(
                    pending.IntentId,
                    pending.Intent,
                    failureReason,
                    "RECOVERY_PROTECTIVE_TIMEOUT_FLATTENED",
                    $"RECOVERY_PROTECTIVE_TIMEOUT:{pending.IntentId}",
                    $"CRITICAL: Recovery Protective Timeout - {pending.Intent.Instrument}",
                    $"Protective orders not placed within {RECOVERY_PROTECTIVE_TIMEOUT_SECONDS}s during recovery. Position flattened. Stream: {pending.Intent.Stream}, Intent: {pending.IntentId}.",
                    null, null, new { elapsed_seconds = elapsed, timeout_seconds = RECOVERY_PROTECTIVE_TIMEOUT_SECONDS }, utcNow);
                continue;
            }

            // Stage 2: Broker ready — submit protectives
            if (executionAllowed)
            {
                if (_coordinator != null && !_coordinator.CanSubmitExit(pending.IntentId, pending.TotalFilledQuantity))
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, pending.IntentId, pending.Intent.Instrument, "EXECUTION_ERROR",
                        new { error = "Exit validation failed for recovery queued protective", intent_id = pending.IntentId, total_filled_quantity = pending.TotalFilledQuantity }));
                    continue;
                }
                var correlationId = $"PROTECTIVES_RECOVERY:{pending.IntentId}:{utcNow:yyyyMMddHHmmssfff}";
                var cmd = new NtSubmitProtectivesCommand(
                    correlationId,
                    pending.IntentId,
                    pending.Intent.Instrument ?? "",
                    pending.Intent.Direction!,
                    pending.Intent.StopPrice!.Value,
                    pending.Intent.TargetPrice!.Value,
                    pending.TotalFilledQuantity,
                    null,
                    "RECOVERY_QUEUE",
                    utcNow);
                EnqueueNtActionInternal(cmd);
                _log.Write(RobotEvents.ExecutionBase(utcNow, pending.IntentId, pending.Intent.Instrument, "PROTECTIVE_ORDERS_SUBMITTED_FROM_RECOVERY_QUEUE",
                    new { intent_id = pending.IntentId, elapsed_seconds = elapsed }));
            }
            else
            {
                // Still in recovery — put back in queue for next drain
                lock (_pendingRecoveryLock)
                {
                    _pendingRecoveryProtectives.Add(pending);
                }
            }
        }
    }

    private void EnqueueNtActionInternal(INtAction action)
    {
        if (_ntActionQueue != null)
            _ntActionQueue.EnqueueNtAction(action, out _);
    }

    partial void OnVerifyPendingFlattens();

    private bool HasPendingBEForIntent(string intentId)
    {
        foreach (var p in _pendingBERequests.Values)
            if (string.Equals(p.IntentId, intentId, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingBECancelUtcByIntent = new(); // Replace-semantics: intent_id -> when old stop went CancelPending
    /// <summary>BE cancel+replace: intent_id -> (new_stop_order_id, start_utc). Used to log BE_CANCEL_REPLACE_STOP_WORKING and quantify overlap/gap.</summary>
    private readonly ConcurrentDictionary<string, (string NewStopOrderId, DateTimeOffset StartUtc)> _pendingBECancelReplaceByIntent = new(StringComparer.OrdinalIgnoreCase);
    private const int BE_REPLACE_CANCEL_WINDOW_SEC = 30;
    private const int BE_CONFIRM_TIMEOUT_SEC = 15;
    private const int BE_RETRY_INTERVAL_SEC = 5;
    private const int BE_MAX_RETRY_ATTEMPTS = 3;
    public NinjaTraderSimAdapter(string projectRoot, RobotLogger log, ExecutionJournal executionJournal)
    {
        _adapterInstanceId = System.Threading.Interlocked.Increment(ref _adapterInstanceCounter);
        _projectRoot = projectRoot;
        _log = log;
        _executionJournal = executionJournal;
        
        // Note: SIM account verification happens when NT context is set via SetNTContext()
        // Mock mode has been removed - only real NT API execution is supported
    }
    
    /// <summary>
    /// PHASE 2: Set callbacks for stream stand-down and notification service access.
    /// Gap 5: blockInstrumentCallback invoked when IEA EnqueueAndWait fails (timeout/overflow); engine freezes instrument and stands down streams.
    /// </summary>
    public void SetEngineCallbacks(
        Action<string, DateTimeOffset, string>? standDownStreamCallback,
        Func<object?>? getNotificationServiceCallback,
        Func<bool>? isExecutionAllowedCallback = null,
        Action<string, DateTimeOffset, string>? blockInstrumentCallback = null,
        Action<string, string, object>? onSupervisoryCriticalCallback = null,
        Action<string, DateTimeOffset>? onReentryFillCallback = null,
        Action<string, DateTimeOffset>? onReentryProtectionAcceptedCallback = null)
    {
        _standDownStreamCallback = standDownStreamCallback;
        _getNotificationServiceCallback = getNotificationServiceCallback;
        _isExecutionAllowedCallback = isExecutionAllowedCallback;
        _blockInstrumentCallback = blockInstrumentCallback;
        _onSupervisoryCriticalCallback = onSupervisoryCriticalCallback;
        _onReentryFillCallback = onReentryFillCallback;
        _onReentryProtectionAcceptedCallback = onReentryProtectionAcceptedCallback;
    }
    
    /// <summary>
    /// Set intent exposure coordinator.
    /// </summary>
    public void SetCoordinator(InstrumentIntentCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    /// <summary>
    /// Set NinjaTrader context (Account, Instrument) from Strategy host.
    /// Called by RobotSimStrategy after NT context is available.
    /// </summary>
    /// <param name="account">NT Account.</param>
    /// <param name="instrument">NT Instrument.</param>
    /// <param name="engineExecutionInstrument">Engine's execution instrument (e.g., MNQ) — used for IEA key when IEA enabled.</param>
    public void SetNTContext(object account, object instrument, string? engineExecutionInstrument = null)
    {
        _ntAccount = account;
        _ntInstrument = instrument;
        _ntContextSet = true;
        _ieaEngineExecutionInstrument = engineExecutionInstrument;
        
        // Resolve IEA when use_instrument_execution_authority is enabled
        var accountName = GetAccountName(account);
        var executionInstrumentKey = ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(accountName, instrument, engineExecutionInstrument);
        if (_useInstrumentExecutionAuthority)
        {
            _ntActionQueue = new StrategyThreadExecutor(_log);
            _ieaAccountName = accountName;
            _iea = InstrumentExecutionAuthorityRegistry.GetOrCreate(accountName, executionInstrumentKey,
                () => new InstrumentExecutionAuthority(accountName, executionInstrumentKey, this, _log, _aggregationPolicy));
            _iea.SetEventWriter(_eventWriter);
            _iea.SetOnEnqueueFailureCallback(_blockInstrumentCallback);
            _iea.SetOnRecoveryRequestedCallback(OnRecoveryRequested);
            _iea.SetOnRecoveryFlattenRequestedCallback(OnRecoveryFlattenRequested);
            _iea.SetOnBootstrapSnapshotRequestedCallback(OnBootstrapSnapshotRequested);
            _iea.SetOnSupervisoryCriticalCallback(_onSupervisoryCriticalCallback);
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "IEA_BINDING", state: "ENGINE",
                new
                {
                    account_name = accountName,
                    execution_instrument_key = executionInstrumentKey,
                    iea_instance_id = _iea.InstanceId,
                    note = "IEA bound for execution routing"
                }));
        }

        // Deterministic routing: register endpoint for (account, executionInstrumentKey)
        // GC chart with MGC execution registers for MGC so fills route correctly regardless of which strategy receives the callback
        // Phase 1: TryRegisterEndpoint - fail closed on conflict (no overwrite)
        if (!string.IsNullOrEmpty(accountName) && !string.IsNullOrEmpty(executionInstrumentKey))
        {
            var instanceId = _useInstrumentExecutionAuthority && _iea != null
                ? $"IEA:{_iea.InstanceId}"
                : $"ADAPTER:{_adapterInstanceId}";
            var endpoint = _useInstrumentExecutionAuthority && _iea != null
                ? (Action<object, object>)_iea.EnqueueExecutionUpdate
                : HandleExecutionUpdate;
            if (!ExecutionUpdateRouter.TryRegisterEndpoint(accountName, executionInstrumentKey, endpoint, instanceId, _log))
                throw new InvalidOperationException($"EXEC_ROUTER_ENDPOINT_CONFLICT: Cannot register for ({accountName}, {executionInstrumentKey}) - different instance already owns. Fail closed.");
        }

        // STEP 1: SIM Account Verification (MANDATORY) - now with real NT account
        VerifySimAccount();

        // One-time: Re-register intents from open journal entries BEFORE bootstrap so runtime snapshot and adoption have correct IntentMap.
        HydrateIntentsFromOpenJournals();

        // Phase 4: Bootstrap after hydration. Centralizes startup/reconnect determinism.
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            _iea.BeginBootstrapForInstrument(executionInstrumentKey, BootstrapReason.STRATEGY_START, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// One-time hydration: Register intents from open journal entries so BE detection works after restart.
    /// Only registers intents matching this adapter's execution instrument. Idempotent.
    /// </summary>
    private void HydrateIntentsFromOpenJournals()
    {
        var ourExecutionInstrument = (_iea?.ExecutionInstrumentKey ?? _ieaEngineExecutionInstrument ?? "").Trim();
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "INTENTS_HYDRATION_ATTEMPT", state: "ENGINE",
            new { execution_instrument = ourExecutionInstrument ?? "(empty)", iea_set = _iea != null, note = "Hydration attempt (runs on SetNTContext)" }));
        if (string.IsNullOrEmpty(ourExecutionInstrument))
            return;
        var ourRoot = ourExecutionInstrument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ourExecutionInstrument;

        var byInst = _executionJournal.GetOpenJournalEntriesByInstrument();
        var count = 0;
        foreach (var kvp in byInst)
        {
            var journalInstrument = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(journalInstrument)) continue;
            var journalRoot = journalInstrument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? journalInstrument;
            if (!string.Equals(journalRoot, ourRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var (tradingDate, stream, intentId, entry) in kvp.Value)
            {
                if (IntentMap.ContainsKey(intentId)) continue;
                var intent = CreateIntentFromJournalEntry(tradingDate, stream, intentId, entry);
                if (intent == null) continue;
                RegisterIntent(intent);
                count++;
            }
        }
        if (count > 0)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "INTENTS_HYDRATED_FROM_JOURNAL", state: "ENGINE",
                new { intent_count = count, execution_instrument = ourRoot, note = "Re-registered intents from open journals for BE detection" }));
        }
        else if (byInst.Count > 0)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "INTENTS_HYDRATION_SKIPPED", state: "ENGINE",
                new { execution_instrument = ourRoot, open_journal_instruments = string.Join(",", byInst.Keys), note = "Hydration ran but no matching open journals for this chart" }));
        }
    }

    /// <summary>
    /// Create Intent from journal entry for hydration. Returns null if required fields missing.
    /// </summary>
    private static Intent? CreateIntentFromJournalEntry(string tradingDate, string stream, string intentId, ExecutionJournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(tradingDate) || string.IsNullOrWhiteSpace(stream) || string.IsNullOrWhiteSpace(entry.Instrument))
            return null;
        var executionInstrument = entry.Instrument.Trim();
        var canonicalInstrument = DeriveCanonicalFromStream(stream);
        var session = DeriveSessionFromStream(stream);
        var slotTimeChicago = ParseSlotFromOcoGroup(entry.OcoGroup) ?? "09:00";
        var direction = entry.Direction ?? "Long";
        var entryPrice = entry.EntryPrice ?? entry.FillPrice ?? 0;
        var stopPrice = entry.StopPrice ?? entryPrice;
        var targetPrice = entry.TargetPrice ?? entryPrice;
        var beTrigger = ComputeBeTrigger(entryPrice, targetPrice, direction);
        var entryTimeUtc = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(entry.EntryFilledAtUtc) && DateTimeOffset.TryParse(entry.EntryFilledAtUtc, out var parsed))
            entryTimeUtc = parsed;

        return new Intent(
            tradingDate,
            stream,
            canonicalInstrument,
            executionInstrument,
            session,
            slotTimeChicago,
            direction,
            entryPrice,
            stopPrice,
            targetPrice,
            beTrigger,
            entryTimeUtc,
            "JOURNAL_REHYDRATION");
    }

    private static string DeriveCanonicalFromStream(string stream)
    {
        if (string.IsNullOrEmpty(stream)) return "UNKNOWN";
        var upper = stream.ToUpperInvariant();
        if (upper.StartsWith("RTY")) return "RTY";
        if (upper.StartsWith("YM")) return "YM";
        if (upper.StartsWith("ES")) return "ES";
        if (upper.StartsWith("NQ")) return "NQ";
        if (upper.StartsWith("GC")) return "GC";
        if (upper.StartsWith("NG")) return "NG";
        if (upper.StartsWith("CL")) return "CL";
        return stream.Length >= 2 ? stream.Substring(0, 2).ToUpperInvariant() : "UNKNOWN";
    }

    private static string DeriveSessionFromStream(string stream)
    {
        if (string.IsNullOrEmpty(stream)) return "S1";
        var last = stream[stream.Length - 1];
        return char.IsDigit(last) ? $"S{last}" : "S1";
    }

    private static string? ParseSlotFromOcoGroup(string? ocoGroup)
    {
        if (string.IsNullOrEmpty(ocoGroup)) return null;
        var parts = ocoGroup.Split(':');
        if (parts.Length >= 5 && parts[4].Length >= 5)
            return parts[4];
        return null;
    }

    private static decimal? ComputeBeTrigger(decimal entryPrice, decimal targetPrice, string direction)
    {
        var dist = Math.Abs(targetPrice - entryPrice);
        var bePts = dist * 0.65m;
        return direction == "Long" ? entryPrice + bePts : entryPrice - bePts;
    }
    
    /// <summary>Get account name from NT account object.</summary>
    private static string GetAccountName(object account)
    {
        if (account == null) return "";
        try
        {
            dynamic dyn = account;
            return dyn.Name as string ?? "";
        }
        catch { return ""; }
    }
    
    /// <summary>
    /// Phase 3: Request recovery for instrument. Routes to IEA when bound.
    /// </summary>
    public void RequestRecoveryForInstrument(string instrument, string reason, object context, DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(_ieaAccountName)) return;
        var execKey = ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(_ieaAccountName, instrument, null);
        if (string.IsNullOrEmpty(execKey)) execKey = (instrument ?? "").Trim().ToUpperInvariant();
        if (InstrumentExecutionAuthorityRegistry.TryGet(_ieaAccountName, execKey, out var iea))
            iea.RequestRecovery(instrument, reason, context, utcNow);
    }

    /// <summary>
    /// Phase 5: Request supervisory action for instrument. Routes to IEA when bound.
    /// </summary>
    public void RequestSupervisoryActionForInstrument(string instrument, SupervisoryTriggerReason reason, SupervisorySeverity severity, object? context, DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(_ieaAccountName)) return;
        var execKey = ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(_ieaAccountName, instrument, null);
        if (string.IsNullOrEmpty(execKey)) execKey = (instrument ?? "").Trim().ToUpperInvariant();
        if (InstrumentExecutionAuthorityRegistry.TryGet(_ieaAccountName, execKey, out var iea))
            iea.RequestSupervisoryAction(instrument, reason, severity, context, utcNow);
    }

    /// <summary>
    /// Enqueue execution command. Forwards to IEA when bound; no-op when IEA not enabled.
    /// Strategy layers should use this instead of calling adapter.Flatten/SubmitOrders/CancelOrders directly.
    /// </summary>
    public FlattenResult? RequestSessionCloseFlattenImmediate(string intentId, string instrument, DateTimeOffset utcNow)
    {
        if (_ntActionQueue == null || !(this is INtActionExecutor executor)) return null;
        var cidCancel = $"SESSION_CLOSE_CANCEL:{intentId}:{utcNow:yyyyMMddHHmmssfff}";
        var cidFlatten = $"SESSION_CLOSE_FLATTEN:{intentId}:{utcNow:yyyyMMddHHmmssfff}";
        _ntActionQueue.EnqueueNtAction(new NtCancelOrdersCommand(cidCancel, intentId, instrument, false, "FORCED_FLATTEN_CANCEL", utcNow), out _);
        _ntActionQueue.EnqueueNtAction(new NtFlattenInstrumentCommand(cidFlatten, intentId, instrument, "SESSION_FORCED_FLATTEN", utcNow), out _);
        var lockObj = _iea?.EntrySubmissionLock;
        if (lockObj != null)
        {
            lock (lockObj)
            {
                _ntActionQueue.DrainNtActions(executor);
                return FlattenResult.SuccessResult(utcNow);
            }
        }
        _ntActionQueue.DrainNtActions(executor);
        return FlattenResult.SuccessResult(utcNow);
    }

    public void EnqueueExecutionCommand(ExecutionCommandBase command)
    {
        if (command == null) return;
        if (!_useInstrumentExecutionAuthority || _iea == null)
        {
            _log.Write(RobotEvents.ExecutionBase(command.TimestampUtc, command.IntentId ?? "", command.Instrument, "EXECUTION_COMMAND_SKIPPED",
                new { commandId = command.CommandId, reason = "IEA not enabled", commandType = command.GetType().Name }));
            return;
        }
        var instrument = command.Instrument ?? "";
        var execKey = !string.IsNullOrEmpty(_ieaAccountName)
            ? ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(_ieaAccountName, instrument, null)
            : (instrument ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(execKey)) execKey = instrument.Trim().ToUpperInvariant();
        if (InstrumentExecutionAuthorityRegistry.TryGet(_ieaAccountName ?? "", execKey, out var iea))
            iea.EnqueueExecutionCommand(command);
        else if (_iea != null)
            _iea.EnqueueExecutionCommand(command);
    }

    /// <summary>
    /// Get IEA identity for CRITICAL event payloads (when IEA enabled).
    /// </summary>
    private object? GetIeaIdentityForCriticalEvents()
    {
        if (!_useInstrumentExecutionAuthority || _iea == null) return null;
        return new { iea_instance_id = _iea.InstanceId, execution_instrument_key = _iea.ExecutionInstrumentKey, account_name = _iea.AccountName };
    }

    /// <summary>
    /// Gap 6: Ensure CRITICAL events include iea_instance_id when IEA enabled. Invariant: no CRITICAL without IEA context.
    /// </summary>
    internal void LogCriticalWithIeaContext(DateTimeOffset utcNow, string intentId, string instrument, string eventType, object data)
    {
        var payload = _iea != null ? MergeIeaContext(data, _iea) : data;
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, eventType, payload));
    }

    /// <summary>
    /// Gap 6: EngineBase variant for CRITICAL events.
    /// </summary>
    internal void LogCriticalEngineWithIeaContext(DateTimeOffset utcNow, string tradingDate, string eventType, string state, object data)
    {
        var payload = _iea != null ? MergeIeaContext(data, _iea) : data;
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, eventType, state, payload));
    }

    private static System.Collections.Generic.Dictionary<string, object> MergeIeaContext(object data, InstrumentExecutionAuthority iea)
    {
        var dict = new System.Collections.Generic.Dictionary<string, object>();
        if (data != null)
        {
            foreach (var p in data.GetType().GetProperties())
                dict[p.Name] = p.GetValue(data);
        }
        dict["iea_instance_id"] = iea.InstanceId;
        dict["execution_instrument_key"] = iea.ExecutionInstrumentKey;
        return dict;
    }

    /// <summary>
    /// Canonical fill: Get next monotonic execution_sequence.
    /// Scope: Per execution_instrument_key (NOT per strategy instance, NOT per stream, NOT global across instruments).
    /// Multi-account: key = account|execution_instrument_key to avoid cross-account collision.
    /// Must be reproducible under replay.
    /// </summary>
    internal static int GetNextExecutionSequence(string executionInstrumentKey, string? accountName = null)
    {
        var key = string.IsNullOrEmpty(executionInstrumentKey) ? "_default" : executionInstrumentKey;
        if (!string.IsNullOrEmpty(accountName))
            key = accountName + "|" + key;
        lock (_executionSequenceLock)
        {
            if (!_executionSequenceByKey.TryGetValue(key, out var seq))
                seq = 0;
            seq++;
            _executionSequenceByKey[key] = seq;
            return seq;
        }
    }

    /// <summary>
    /// Canonical fill: Deterministic fill_group_id. Use broker execution id when available; else hash.
    /// Must be reproducible under replay (no random UUID).
    /// </summary>
    internal static string ComputeFillGroupId(string? brokerExecutionId, string orderId, string brokerOrderId, string timestampUtc, decimal fillPrice, int fillQty)
    {
        if (!string.IsNullOrWhiteSpace(brokerExecutionId))
            return brokerExecutionId;
        var input = $"{orderId}|{brokerOrderId}|{timestampUtc}|{fillPrice}|{fillQty}";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        var hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        return hex.Length >= 16 ? hex.Substring(0, 16) : hex;
    }

    /// <summary>
    /// Set whether to use Instrument Execution Authority (IEA).
    /// Must be called before SetNTContext. Called by RobotEngine when execution policy has use_instrument_execution_authority.
    /// </summary>
    public void SetUseInstrumentExecutionAuthority(bool use)
    {
        _useInstrumentExecutionAuthority = use;
    }

    /// <summary>
    /// Phase 2: Set aggregation/bracket policy for IEA. Must be called before SetNTContext when IEA is enabled.
    /// </summary>
    public void SetAggregationPolicy(AggregationPolicy? policy)
    {
        _aggregationPolicy = policy;
    }

    /// <summary>
    /// STEP 1: Verify we're connected to NT Sim account (fail closed if not).
    /// REQUIRES: NINJATRADER preprocessor directive and NT context to be set.
    /// </summary>
    private void VerifySimAccount()
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "NT_CONTEXT_NOT_SET", error }));
            throw new InvalidOperationException(error);
        }

        VerifySimAccountReal();
    }

    /// <summary>
    /// STEP 2: Implement Entry Order Submission (REAL NT API)
    /// </summary>
    public OrderSubmissionResult SubmitEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal? entryPrice,
        int quantity,
        string? entryOrderType,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "ENTRY",
            direction,
            entry_price = entryPrice,
            entry_order_type = entryOrderType,
            quantity,
            account = "SIM"
        }));

        // Hard safety: Verify Sim account (should already be verified, but double-check)
        if (!_simAccountVerified)
        {
            var error = "SIM account not verified - not placing orders";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                account = "SIM"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED"
        }));
        return OrderSubmissionResult.FailureResult(error, utcNow);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                reason = "NT_CONTEXT_NOT_SET"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        // Invariant 1: When IEA enabled, IEA must be bound (SetNTContext with engineExecutionInstrument)
        if (_useInstrumentExecutionAuthority && _iea == null)
        {
            var error = "CRITICAL: IEA enabled but not bound - order submission blocked (IEA_BYPASS_ATTEMPTED)";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "IEA_BYPASS_ATTEMPTED", state: "ENGINE",
                new { intent_id = intentId, instrument, reason = "IEA_ENABLED_BUT_NOT_BOUND", error }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            return SubmitEntryOrderReal(intentId, instrument, direction, entryPrice, quantity, entryOrderType, utcNow);
        }
        catch (Exception ex)
        {
            // Journal: ENTRY_SUBMIT_FAILED
            // Get Intent info for journal logging
            string tradingDate = "";
            string stream = "";
            if (IntentMap.TryGetValue(intentId, out var entryIntent))
            {
                tradingDate = entryIntent.TradingDate;
                stream = entryIntent.Stream;
            }
            _executionJournal.RecordRejection(intentId, tradingDate, stream, $"ENTRY_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "ENTRY", rejectedPrice: entryPrice, rejectedQuantity: quantity);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                account = "SIM",
                exception_type = ex.GetType().Name
            }));
            
            return OrderSubmissionResult.FailureResult($"Entry order submission failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// STEP 2b: Submit stop-market entry order (breakout stop).
    /// Used to place stop entries immediately after RANGE_LOCKED (before breakout occurs).
    /// </summary>
    public OrderSubmissionResult SubmitStopEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "ENTRY_STOP",
            direction,
            stop_price = stopPrice,
            quantity,
            oco_group = ocoGroup,
            account = "SIM"
        }));

        // Hard safety: Verify Sim account (should already be verified, but double-check)
        if (!_simAccountVerified)
        {
            var error = "SIM account not verified - not placing stop entry orders";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                order_type = "ENTRY_STOP",
                account = "SIM"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED",
            order_type = "ENTRY_STOP"
        }));
        return OrderSubmissionResult.FailureResult(error, utcNow);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                reason = "NT_CONTEXT_NOT_SET",
                order_type = "ENTRY_STOP"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        // Invariant 1: When IEA enabled, IEA must be bound
        if (_useInstrumentExecutionAuthority && _iea == null)
        {
            var error = "CRITICAL: IEA enabled but not bound - order submission blocked (IEA_BYPASS_ATTEMPTED)";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "IEA_BYPASS_ATTEMPTED", state: "ENGINE",
                new { intent_id = intentId, instrument, reason = "IEA_ENABLED_BUT_NOT_BOUND", error }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            return SubmitStopEntryOrderReal(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
        }
        catch (Exception ex)
        {
            // Get Intent info for journal logging
            string tradingDate = "";
            string stream = "";
            if (IntentMap.TryGetValue(intentId, out var stopEntryIntent))
            {
                tradingDate = stopEntryIntent.TradingDate;
                stream = stopEntryIntent.Stream;
            }
            _executionJournal.RecordRejection(intentId, tradingDate, stream, $"ENTRY_STOP_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "ENTRY_STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                order_type = "ENTRY_STOP",
                account = "SIM",
                exception_type = ex.GetType().Name
            }));

            return OrderSubmissionResult.FailureResult($"Stop entry order submission failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// STEP 3: Handle NT OrderUpdate event (called by Strategy host).
    /// Public method for Strategy to forward NT events.
    /// </summary>
    public void HandleOrderUpdate(object order, object orderUpdate)
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif
        // Gap 2: When IEA enabled, route through queue so OrderMap mutations run on worker (single mutation lane).
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            _iea.EnqueueOrderUpdate(order, orderUpdate);
        }
        else
        {
            HandleOrderUpdateReal(order, orderUpdate);
        }
    }

    /// <summary>
    /// STEP 3: Handle NT ExecutionUpdate event (called by Strategy host).
    /// Public method for Strategy to forward NT events.
    /// </summary>
    public void HandleExecutionUpdate(object execution, object order)
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            var utcNow = DateTimeOffset.UtcNow;
            var key = $"{_iea.AccountName}:{_iea.ExecutionInstrumentKey}";
            if (!_lastIeaExecUpdateRoutedUtc.TryGetValue(key, out var last) || (utcNow - last).TotalSeconds >= 1)
            {
                _lastIeaExecUpdateRoutedUtc[key] = utcNow;
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "IEA_EXEC_UPDATE_ROUTED", state: "ENGINE",
                    new { account_name = _iea.AccountName, execution_instrument_key = _iea.ExecutionInstrumentKey, iea_instance_id = _iea.InstanceId }));
            }
            _iea.EnqueueExecutionUpdate(execution, order);
        }
        else
        {
            HandleExecutionUpdateReal(execution, order);
        }
    }

    /// <summary>
    /// Phase 1: Process pending unresolved executions (non-IEA path). Called from strategy thread on OnBarUpdate/OnMarketData.
    /// </summary>
    public void ProcessPendingUnresolvedExecutions()
    {
        if (_useInstrumentExecutionAuthority) return;
        List<UnresolvedExecutionRecord> snapshot;
        lock (_pendingUnresolvedLock)
        {
            if (_pendingUnresolvedExecutions.Count == 0) return;
            snapshot = new List<UnresolvedExecutionRecord>(_pendingUnresolvedExecutions);
            _pendingUnresolvedExecutions.Clear();
        }
        foreach (var record in snapshot)
            ProcessUnresolvedRetry(record);
    }
    
    /// <summary>
    /// PHASE 2: Handle entry fill and submit protective orders with retry and failure recovery.
    /// </summary>
    private void HandleEntryFill(string intentId, Intent intent, decimal fillPrice, int fillQuantity, int totalFilledQuantity, DateTimeOffset utcNow)
    {
        // CRITICAL FIX: totalFilledQuantity is the cumulative filled quantity (passed from caller)
        // For incremental fills, protective orders must cover the ENTIRE position, not just the new fill
        // This prevents position accumulation bugs where protective orders only cover the delta
        
        // Record entry fill time for watchdog tracking
        if (OrderMap.TryGetValue(intentId, out var entryOrderInfo))
        {
            entryOrderInfo.EntryFillTime = utcNow;
            entryOrderInfo.ProtectiveStopAcknowledged = false;
            entryOrderInfo.ProtectiveTargetAcknowledged = false;
        }
        
        // CRITICAL: Validate intent has all required fields for protective orders
        // REAL RISK FIX: Treat missing intent fields the same as protective submission failure
        // If we cannot prove the position is protected, flatten immediately (fail-closed)
        var missingFields = new List<string>();
        if (intent.Direction == null) missingFields.Add("Direction");
        if (intent.StopPrice == null) missingFields.Add("StopPrice");
        if (intent.TargetPrice == null) missingFields.Add("TargetPrice");
        
        if (missingFields.Count > 0)
        {
            var failureReason = $"Intent incomplete - missing fields: {string.Join(", ", missingFields)}";
            
            // Log critical error
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "INTENT_INCOMPLETE_UNPROTECTED_POSITION",
                new 
                { 
                    error = failureReason,
                    intent_id = intentId,
                    missing_fields = missingFields,
                    direction = intent.Direction,
                    stop_price = intent.StopPrice,
                    target_price = intent.TargetPrice,
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    total_filled_quantity = totalFilledQuantity,
                    stream = intent.Stream,
                    instrument = intent.Instrument,
                    action = "FLATTEN_IMMEDIATELY",
                    note = "Entry order filled but intent incomplete - position unprotected. Flattening immediately (fail-closed behavior)."
                }));
            
            // SIMPLIFICATION: Use centralized fail-closed pattern
            FailClosed(
                intentId,
                intent,
                failureReason,
                "INTENT_INCOMPLETE_FLATTENED",
                $"INTENT_INCOMPLETE:{intentId}",
                $"CRITICAL: Intent Incomplete - Unprotected Position - {intent.Instrument}",
                $"Entry filled but intent incomplete (missing: {string.Join(", ", missingFields)}). Position flattened. Stream: {intent.Stream}, Intent: {intentId}.",
                null, // stopResult
                null, // targetResult
                new { missing_fields = missingFields }, // additionalData
                utcNow);
            
            return;
        }
        
        // Stage 1: Entry fill during recovery — queue protective submission (three-stage safety model)
        if (_isExecutionAllowedCallback != null && !_isExecutionAllowedCallback())
        {
            QueueProtectiveForRecovery(intentId, intent, totalFilledQuantity, utcNow);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_ORDERS_QUEUED_RECOVERY",
                new
                {
                    intent_id = intentId,
                    fill_quantity = fillQuantity,
                    fill_price = fillPrice,
                    total_filled_quantity = totalFilledQuantity,
                    timeout_seconds = RECOVERY_PROTECTIVE_TIMEOUT_SECONDS,
                    note = "Protective orders queued for submission after recovery; fail-safe flatten if not placed within timeout"
                }));
            return;
        }
        
        // Validate exit orders before submission
        // CRITICAL FIX: Use totalFilledQuantity (cumulative) for validation
        // Protective orders must cover the ENTIRE position, not just the new fill
        if (_coordinator != null)
        {
            if (!_coordinator.CanSubmitExit(intentId, totalFilledQuantity))
            {
                var error = "Exit validation failed - cannot submit protective orders";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "EXECUTION_ERROR",
                    new { 
                        error = error, 
                        intent_id = intentId, 
                        fill_quantity = fillQuantity,
                        total_filled_quantity = totalFilledQuantity
                    }));
                return;
            }
        }
        
        // PHASE 2: Submit protective orders with retry
        // CRITICAL FIX: Generate unique OCO group for each retry attempt
        // NinjaTrader does not allow reusing OCO IDs once they've been used (even if rejected)
        // Both stop and target must use the same OCO group to be paired, so we retry both together
        const int MAX_RETRIES = 3;
        const int RETRY_DELAY_MS = 100;

        SetProtectionState(intentId, ProtectionState.Executing);
        OrderSubmissionResult? stopResult = null;
        OrderSubmissionResult? targetResult = null;
        
        for (int attempt = 0; attempt < MAX_RETRIES; attempt++)
        {
            if (attempt > 0)
            {
                System.Threading.Thread.Sleep(RETRY_DELAY_MS);
            }
            
            // Validate again before each retry
            // CRITICAL FIX: Use totalFilledQuantity (cumulative) for validation and protective orders
            // Protective orders must cover the ENTIRE position, not just the new fill
            if (_coordinator != null && !_coordinator.CanSubmitExit(intentId, totalFilledQuantity))
            {
                var error = "Exit validation failed during retry";
                stopResult = OrderSubmissionResult.FailureResult(error, utcNow);
                targetResult = OrderSubmissionResult.FailureResult(error, utcNow);
                break;
            }
            
            // SIMPLIFICATION: Use centralized OCO group generation
            // Generate unique OCO group for this attempt (append attempt number and timestamp)
            // Both stop and target must use the same OCO group to be paired
            var protectiveOcoGroup = GenerateProtectiveOcoGroup(intentId, attempt, utcNow);
            
            // Submit stop order
            // CRITICAL FIX: Use totalFilledQuantity (cumulative) for protective orders
            // This ensures protective orders cover the ENTIRE position for incremental fills
            stopResult = SubmitProtectiveStop(
                intentId,
                intent.Instrument,
                intent.Direction,
                intent.StopPrice.Value,
                totalFilledQuantity,  // CRITICAL: Use total, not delta
                protectiveOcoGroup,
                utcNow);
            
            // Only submit target if stop succeeded (they must be OCO paired)
            if (stopResult.Success)
            {
                targetResult = SubmitTargetOrder(
                    intentId,
                    intent.Instrument,
                    intent.Direction,
                    intent.TargetPrice.Value,
                    totalFilledQuantity,  // CRITICAL: Use total, not delta
                    protectiveOcoGroup,
                    utcNow);
                
                // If both succeeded, we're done
                if (targetResult.Success)
                {
                    SetProtectionState(intentId, ProtectionState.Submitted);
                    break;
                }
                
                // If target failed but stop succeeded, we need to cancel stop and retry both
                // Cancel the stop order before retrying (OCO pairing broken)
                if (stopResult.BrokerOrderId != null)
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_RETRY_CANCEL_STOP",
                        new
                        {
                            attempt = attempt + 1,
                            reason = "Target submission failed - canceling stop to retry both as OCO pair",
                            stop_broker_order_id = stopResult.BrokerOrderId
                        }));
                    // Note: Stop will be canceled by NinjaTrader when we submit new orders, or we could explicitly cancel
                    // For now, continue to next retry attempt with new OCO group
                }
            }
            // If stop failed, continue to next retry attempt
        }
        
        // PHASE 2: If either protective leg failed after retries, flatten position and stand down stream
        if (!stopResult.Success || !targetResult.Success)
        {
            PruneIntentState(intentId, "protective_orders_failed");
            var failedLegs = new List<string>();
            if (!stopResult.Success) failedLegs.Add($"STOP: {stopResult.ErrorMessage}");
            if (!targetResult.Success) failedLegs.Add($"TARGET: {targetResult.ErrorMessage}");
            
            var failureReason = $"Protective orders failed after {MAX_RETRIES} retries: {string.Join(", ", failedLegs)}";
            
            // SIMPLIFICATION: Use centralized fail-closed pattern
            FailClosed(
                intentId,
                intent,
                failureReason,
                "PROTECTIVE_ORDERS_FAILED_FLATTENED",
                $"PROTECTIVE_FAILURE:{intentId}",
                $"CRITICAL: Protective Order Failure - {intent.Instrument}",
                $"Entry filled but protective orders failed. Position flattened. Stream: {intent.Stream}, Intent: {intentId}. Failures: {failureReason}",
                stopResult,
                targetResult,
                new { retry_count = MAX_RETRIES }, // additionalData
                utcNow);
            
            return;
        }
        
        // Log protective orders submitted successfully
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_ORDERS_SUBMITTED",
            new
            {
                stop_order_id = stopResult.BrokerOrderId,
                target_order_id = targetResult.BrokerOrderId,
                stop_price = intent.StopPrice,
                target_price = intent.TargetPrice,
                fill_quantity = fillQuantity,  // Delta for this fill
                total_filled_quantity = totalFilledQuantity,  // Cumulative total for protective orders
                note = "Protective orders submitted for total filled quantity (covers entire position for incremental fills)"
            }));
        
        // Check for unprotected positions after protective order submission
        CheckUnprotectedPositions(utcNow);
        
        // CRITICAL FIX: Check all instruments for flat positions and cancel entry stops
        // This detects manual position closures that bypass robot code
        // Called on every execution update to catch manual flattens quickly
        CheckAllInstrumentsForFlatPositions(utcNow);

        // Proof log: unambiguous, includes encoded envelope and decoded identity
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVES_PLACED", new
        {
            intent_id = intentId,
            encoded_entry_tag = RobotOrderIds.EncodeTag(intentId),
            stop_tag = RobotOrderIds.EncodeStopTag(intentId),
            target_tag = RobotOrderIds.EncodeTargetTag(intentId),
            order_type = "ENTRY_OR_ENTRY_STOP",
            stop_price = intent.StopPrice,
            target_price = intent.TargetPrice,
            fill_quantity = fillQuantity,  // Delta for this fill
            protected_quantity = totalFilledQuantity,  // Cumulative total protected
            note = "Protective stop + target successfully placed/ensured for total filled quantity (covers entire position for incremental fills)"
        }));
    }
    
    /// <summary>
    /// SIMPLIFICATION: Generate unique OCO group for protective orders.
    /// NinjaTrader does not allow reusing OCO IDs once they've been used (even if rejected),
    /// so we generate a unique group per retry attempt.
    /// </summary>
    private string GenerateProtectiveOcoGroup(string intentId, int attempt, DateTimeOffset utcNow)
    {
        return $"QTSW2:{intentId}_PROTECTIVE_A{attempt}_{utcNow:HHmmssfff}";
    }
    
    /// <summary>
    /// SIMPLIFICATION: Fail-closed pattern - flatten position, stand down stream, alert, and persist incident.
    /// Centralizes the fail-closed behavior used in multiple places (intent incomplete, recovery blocked, protective failure, unprotected timeout).
    /// </summary>
    private void FailClosed(
        string intentId,
        Intent intent,
        string failureReason,
        string eventType,
        string notificationKey,
        string notificationTitle,
        string notificationMessage,
        OrderSubmissionResult? stopResult,
        OrderSubmissionResult? targetResult,
        object? additionalData,
        DateTimeOffset utcNow)
    {
        // Notify coordinator of protective failure
        _coordinator?.OnProtectiveFailure(intentId, intent.Stream, utcNow);

        // NT THREADING FIX: When IEA enabled, worker may call FailClosed. Enqueue NT actions for strategy thread.
        if (_useInstrumentExecutionAuthority && _ntActionQueue != null)
        {
            var cid = $"FAILCLOSED:{intentId}:{utcNow:yyyyMMddHHmmssfff}";
            _ntActionQueue.EnqueueNtAction(new NtCancelOrdersCommand(cid, intentId, intent.Instrument, false, failureReason, utcNow), out _);
            _ntActionQueue.EnqueueNtAction(new NtFlattenInstrumentCommand(cid + ":F", intentId, intent.Instrument ?? "", failureReason, utcNow), out _);
            _standDownStreamCallback?.Invoke(intent.Stream, utcNow, $"{eventType}: {failureReason}");
            PersistProtectiveFailureIncident(intentId, intent, stopResult, targetResult, FlattenResult.FailureResult("Enqueued for strategy thread", utcNow), utcNow);
            var notificationSvc = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
            if (notificationSvc != null)
                notificationSvc.EnqueueNotification(notificationKey, notificationTitle, notificationMessage, priority: 2);
            var logDataIea = new Dictionary<string, object>
            {
                { "intent_id", intentId }, { "stream", intent.Stream }, { "instrument", intent.Instrument },
                { "failure_reason", failureReason }, { "note", "Fail-closed: NT actions enqueued for strategy thread" }
            };
            if (_iea != null) { logDataIea["iea_instance_id"] = _iea.InstanceId; logDataIea["execution_instrument_key"] = _iea.ExecutionInstrumentKey; }
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, eventType, logDataIea));
            return;
        }

        // Flatten position immediately with retry logic (strategy thread path)
        var flattenResult = FlattenWithRetry(intentId, intent.Instrument, utcNow);
        
        // Stand down stream
        _standDownStreamCallback?.Invoke(intent.Stream, utcNow, $"{eventType}: {failureReason}");
        
        // Persist incident record
        PersistProtectiveFailureIncident(intentId, intent, stopResult, targetResult, flattenResult, utcNow);
        
        // Raise high-priority alert
        var notificationSvc2 = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
        if (notificationSvc2 != null)
        {
            notificationSvc2.EnqueueNotification(notificationKey, notificationTitle, notificationMessage, priority: 2); // Emergency priority
        }
        
        // Log final flattened event
        var logDataFlatten = new Dictionary<string, object>
        {
            { "intent_id", intentId },
            { "stream", intent.Stream },
            { "instrument", intent.Instrument },
            { "flatten_success", flattenResult.Success },
            { "flatten_error", flattenResult.ErrorMessage },
            { "failure_reason", failureReason },
            { "note", "Position flattened and stream stood down (fail-closed behavior)" }
        };
        
        if (stopResult != null)
        {
            logDataFlatten["stop_success"] = stopResult.Success;
            logDataFlatten["stop_error"] = stopResult.ErrorMessage;
        }
        if (targetResult != null)
        {
            logDataFlatten["target_success"] = targetResult.Success;
            logDataFlatten["target_error"] = targetResult.ErrorMessage;
        }
        if (additionalData != null)
        {
            // Merge additional data into log
            var props = additionalData.GetType().GetProperties();
            foreach (var prop in props)
            {
                logDataFlatten[prop.Name] = prop.GetValue(additionalData);
            }
        }

        // Gap 3: Ensure CRITICAL logs include IEA context when IEA enabled.
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            logDataFlatten["iea_instance_id"] = _iea.InstanceId;
            logDataFlatten["execution_instrument_key"] = _iea.ExecutionInstrumentKey;
        }
        
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, eventType, logDataFlatten));
    }
    
    /// <summary>
    /// PHASE 2: Persist protective order failure incident record.
    /// </summary>
    private void PersistProtectiveFailureIncident(
        string intentId,
        Intent intent,
        OrderSubmissionResult? stopResult,
        OrderSubmissionResult? targetResult,
        FlattenResult flattenResult,
        DateTimeOffset utcNow)
    {
        try
        {
            var incidentDir = System.IO.Path.Combine(_projectRoot, "data", "execution_incidents");
            System.IO.Directory.CreateDirectory(incidentDir);
            
            var incidentPath = System.IO.Path.Combine(incidentDir, $"protective_failure_{intentId}_{utcNow:yyyyMMddHHmmss}.json");
            
            var incident = new
            {
                incident_type = "PROTECTIVE_ORDER_FAILURE",
                timestamp_utc = utcNow.ToString("o"),
                intent_id = intentId,
                trading_date = intent.TradingDate,
                stream = intent.Stream,
                instrument = intent.Instrument,
                session = intent.Session,
                direction = intent.Direction,
                entry_price = intent.EntryPrice,
                stop_price = intent.StopPrice,
                target_price = intent.TargetPrice,
                stop_result = stopResult != null ? new { success = stopResult.Success, error = stopResult.ErrorMessage, broker_order_id = stopResult.BrokerOrderId } : null,
                target_result = targetResult != null ? new { success = targetResult.Success, error = targetResult.ErrorMessage, broker_order_id = targetResult.BrokerOrderId } : null,
                flatten_result = new { success = flattenResult.Success, error = flattenResult.ErrorMessage },
                action_taken = "POSITION_FLATTENED_STREAM_STOOD_DOWN"
            };
            
            var json = JsonUtil.Serialize(incident);
            System.IO.File.WriteAllText(incidentPath, json);
        }
        catch (Exception ex)
        {
            // Log error but don't fail execution
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "INCIDENT_PERSIST_ERROR",
                new { error = ex.Message, exception_type = ex.GetType().Name }));
        }
    }
    
    /// <summary>
    /// Register intent for fill callback handling.
    /// Called by StreamStateMachine or test inject.
    /// </summary>
    public void RegisterIntent(Intent intent)
    {
        // Option A (Stronger): Fail-closed when execution differs from canonical but ExecutionInstrument is null.
        // Prevents journal/reconciliation mismatch (e.g. RTY stored when M2K expected).
        var execContext = _iea?.ExecutionInstrumentKey ?? _ieaEngineExecutionInstrument ?? "";
        var execRoot = string.IsNullOrEmpty(execContext) ? "" : execContext.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? execContext;
        var canonical = (intent.Instrument ?? "").Trim();
        var executionNull = string.IsNullOrWhiteSpace(intent.ExecutionInstrument);
        var executionDiffersFromCanonical = !string.IsNullOrEmpty(execRoot) && !string.IsNullOrEmpty(canonical) &&
            !string.Equals(execRoot, canonical, StringComparison.OrdinalIgnoreCase);
        if (executionDiffersFromCanonical && executionNull)
        {
            var utcNow = DateTimeOffset.UtcNow;
            _log.Write(RobotEvents.ExecutionBase(utcNow, intent.TradingDate ?? "", "INTENT_EXECUTION_INSTRUMENT_MISSING", "CRITICAL",
                new
                {
                    intent_id = intent.ComputeIntentId(),
                    stream = intent.Stream,
                    canonical_instrument = canonical,
                    execution_instrument_context = execRoot,
                    note = "Execution differs from canonical but intent.ExecutionInstrument is null. Would cause journal/reconciliation mismatch. Fail-closed."
                }));
            _standDownStreamCallback?.Invoke(intent.Stream ?? "", utcNow, $"INTENT_EXECUTION_INSTRUMENT_MISSING:{intent.ComputeIntentId()}");
            throw new InvalidOperationException($"Intent registration rejected: ExecutionInstrument is null but execution context ({execRoot}) differs from canonical ({canonical}). Journal would store wrong instrument for reconciliation.");
        }

        var intentId = intent.ComputeIntentId();
        IntentMap[intentId] = intent;
        
        // Log intent registration for debugging
        _log.Write(RobotEvents.ExecutionBase(DateTimeOffset.UtcNow, intentId, intent.Instrument, "INTENT_REGISTERED",
            new
            {
                intent_id = intentId,
                stream = intent.Stream,
                instrument = intent.Instrument,
                direction = intent.Direction,
                entry_price = intent.EntryPrice,
                stop_price = intent.StopPrice,
                target_price = intent.TargetPrice,
                be_trigger = intent.BeTrigger,  // CRITICAL: Log BE trigger to verify it's set
                has_direction = intent.Direction != null,
                has_stop_price = intent.StopPrice != null,
                has_target_price = intent.TargetPrice != null,
                has_be_trigger = intent.BeTrigger != null,  // CRITICAL: Log whether BE trigger is set
                note = "Intent registered - required for protective order placement on fill. BE trigger must be set for break-even detection."
            }));
    }
    
    /// <summary>
    /// Register intent policy expectation for quantity invariant tracking.
    /// Called by StreamStateMachine after intent creation, before order submission.
    /// 
    /// VERIFICATION CHECKLIST:
    /// 1. Check for INTENT_EXECUTION_EXPECTATION_DECLARED event after intent creation
    ///    - Should show policy_base_size and policy_max_size matching policy file
    /// 2. Check for INTENT_POLICY_REGISTERED event in adapter
    ///    - Should match INTENT_EXECUTION_EXPECTATION_DECLARED values
    /// 3. Check for ENTRY_SUBMIT_PRECHECK before every order submission
    ///    - Should show allowed=true for valid submissions
    ///    - Should show allowed=false and reason for blocked submissions
    /// 4. Check for ORDER_CREATED_VERIFICATION after CreateOrder()
    ///    - Should show verified=true for correct orders
    ///    - Should show verified=false and QUANTITY_MISMATCH_EMERGENCY for mismatches
    /// 5. Check for INTENT_FILL_UPDATE on every fill
    ///    - Should show cumulative_filled_qty increasing
    ///    - Should show overfill=false for normal fills
    ///    - Should show overfill=true and INTENT_OVERFILL_EMERGENCY for overfills
    /// 6. Verify end-to-end: policy_base_size → expected_quantity → order_quantity → cumulative_filled_qty
    ///    - All should match for successful execution
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
        
        // Emit log event
        _log.Write(RobotEvents.ExecutionBase(DateTimeOffset.UtcNow, intentId, execution, 
            "INTENT_POLICY_REGISTERED", new
        {
            intent_id = intentId,
            canonical_instrument = canonical,
            execution_instrument = execution,
            expected_qty = expectedQty,
            max_qty = maxQty,
            source = policySource
        }));
    }

    /// <summary>
    /// STEP 4: Protective Orders (ON FILL ONLY)
    /// Called when entry is fully filled.
    /// </summary>
    public OrderSubmissionResult SubmitProtectiveStop(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "PROTECTIVE_STOP",
            direction,
            stop_price = stopPrice,
            quantity,
            account = "SIM"
        }));

        if (!_simAccountVerified)
        {
            var error = "SIM account not verified";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED",
            order_type = "PROTECTIVE_STOP"
        }));
        return OrderSubmissionResult.FailureResult(error, utcNow);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                reason = "NT_CONTEXT_NOT_SET",
                order_type = "PROTECTIVE_STOP"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            return SubmitProtectiveStopReal(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
        }
        catch (Exception ex)
        {
            // Journal: STOP_SUBMIT_FAILED
            // Get Intent info for journal logging
            string tradingDate = "";
            string stream = "";
            if (IntentMap.TryGetValue(intentId, out var stopIntent))
            {
                tradingDate = stopIntent.TradingDate;
                stream = stopIntent.Stream;
            }
            _executionJournal.RecordRejection(intentId, tradingDate, stream, $"STOP_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                order_type = "PROTECTIVE_STOP",
                account = "SIM"
            }));
            
            return OrderSubmissionResult.FailureResult($"Stop order submission failed: {ex.Message}", utcNow);
        }
    }

    public OrderSubmissionResult SubmitTargetOrder(
        string intentId,
        string instrument,
        string direction,
        decimal targetPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "TARGET",
            direction,
            target_price = targetPrice,
            quantity,
            account = "SIM"
        }));

        if (!_simAccountVerified)
        {
            var error = "SIM account not verified";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED",
            order_type = "TARGET"
        }));
        return OrderSubmissionResult.FailureResult(error, utcNow);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                reason = "NT_CONTEXT_NOT_SET",
                order_type = "TARGET"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            return SubmitTargetOrderReal(intentId, instrument, direction, targetPrice, quantity, ocoGroup, utcNow);
        }
        catch (Exception ex)
        {
            // Journal: TARGET_SUBMIT_FAILED
            // Get Intent info for journal logging
            string tradingDate = "";
            string stream = "";
            if (IntentMap.TryGetValue(intentId, out var targetIntent))
            {
                tradingDate = targetIntent.TradingDate;
                stream = targetIntent.Stream;
            }
            _executionJournal.RecordRejection(intentId, tradingDate, stream, $"TARGET_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "TARGET", rejectedPrice: targetPrice, rejectedQuantity: quantity);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                order_type = "TARGET",
                account = "SIM"
            }));
            
            return OrderSubmissionResult.FailureResult($"Target order submission failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// Phase 3: Evaluate break-even. When IEA enabled, delegates to IEA. When not, runs BE logic (legacy path).
    /// Timeout check runs first (same tick path) — post-read, retry, or STOP_MODIFY_FAILED.
    /// </summary>
    public void EvaluateBreakEven(decimal tickPrice, DateTimeOffset? tickTimeFromEvent, string executionInstrument)
    {
#if NINJATRADER
        CheckPendingBETimeouts(DateTimeOffset.UtcNow);
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            // NT THREADING FIX: order.Change() must run on strategy thread (OnMarketData context).
            // IEA worker caused stop ping-pong (49604↔49305) and BE not sticking. Same pattern as entry submission.
            lock (_iea.EntrySubmissionLock)
            {
                var eventTime = tickTimeFromEvent ?? DateTimeOffset.UtcNow;
                var hasEventTime = tickTimeFromEvent.HasValue;
                _iea.EvaluateBreakEvenDirect(tickPrice, eventTime, hasEventTime, executionInstrument);
            }
        }
        else
        {
            EvaluateBreakEvenCoreImpl(tickPrice, tickTimeFromEvent ?? DateTimeOffset.UtcNow, executionInstrument);
        }
#endif
    }

    /// <summary>
    /// STEP 5: Break-Even Modification.
    /// </summary>
    public OrderModificationResult ModifyStopToBreakEven(
        string intentId,
        string instrument,
        decimal beStopPrice,
        DateTimeOffset utcNow) =>
        ModifyStopToBreakEven(intentId, instrument, beStopPrice, utcNow, retryCount: 0);

    /// <summary>
    /// Internal overload for retry path (retryCount > 0).
    /// </summary>
    internal OrderModificationResult ModifyStopToBreakEven(
        string intentId,
        string instrument,
        decimal beStopPrice,
        DateTimeOffset utcNow,
        int retryCount)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_ATTEMPT", new
        {
            be_stop_price = beStopPrice,
            account = "SIM",
            retry_count = retryCount
        }));

        if (!_simAccountVerified)
        {
            var error = "SIM account not verified";
            return OrderModificationResult.FailureResult(error, utcNow);
        }

        try
        {
            // STEP 5: Check journal to prevent duplicate BE modifications
            // CRITICAL: Use tradingDate and stream from intent - empty strings never match journal keys
            var tradingDate = "";
            var stream = "";
            if (IntentMap.TryGetValue(intentId, out var intentForBe))
            {
                tradingDate = intentForBe.TradingDate ?? "";
                stream = intentForBe.Stream ?? "";
            }
            if (_executionJournal.IsBEModified(intentId, tradingDate, stream))
            {
                var error = "BE modification already attempted for this intent";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_SKIPPED", new
                {
                    reason = "DUPLICATE_BE_MODIFICATION",
                    account = "SIM"
                }));
                return OrderModificationResult.FailureResult(error, utcNow);
            }
            
#if !NINJATRADER
            var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                       "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_FAIL", new
            {
                error,
                reason = "NINJATRADER_NOT_DEFINED"
            }));
            return OrderModificationResult.FailureResult(error, utcNow);
#endif

            if (!_ntContextSet)
            {
                var error = "CRITICAL: NT context is not set. " +
                           "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                           "Mock mode has been removed - only real NT API execution is supported.";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_FAIL", new
                {
                    error,
                    reason = "NT_CONTEXT_NOT_SET"
                }));
                return OrderModificationResult.FailureResult(error, utcNow);
            }

            return ModifyStopToBreakEvenReal(intentId, instrument, beStopPrice, utcNow, retryCount);
        }
        catch (Exception ex)
        {
            // Journal: BE_MODIFY_FAILED
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_FAIL", new
            {
                error = ex.Message,
                account = "SIM"
            }));
            
            return OrderModificationResult.FailureResult($"BE modification failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// REAL RISK FIX: Flatten with retry logic (3 retries, short delay).
    /// Flatten is the last line of defense - if it fails due to transient issues,
    /// we must retry before giving up.
    /// Gap 7: When IEA enabled, route through queue for serialization.
    /// </summary>
    private FlattenResult FlattenWithRetry(
        string intentId,
        string instrument,
        DateTimeOffset utcNow)
    {
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            return _iea.EnqueueFlattenAndWait(() => FlattenWithRetryCore(intentId, instrument, utcNow), 10000);
        }
        return FlattenWithRetryCore(intentId, instrument, utcNow);
    }

    private FlattenResult FlattenWithRetryCore(
        string intentId,
        string instrument,
        DateTimeOffset utcNow)
    {
        const int MAX_RETRIES = 3;
        const int RETRY_DELAY_MS = 200; // Short delay between retries
        
        FlattenResult? lastResult = null;
        
        for (int attempt = 0; attempt < MAX_RETRIES; attempt++)
        {
            if (attempt > 0)
            {
                // Short delay before retry
                System.Threading.Thread.Sleep(RETRY_DELAY_MS);
            }
            
            lastResult = Flatten(intentId, instrument, utcNow);
            
            if (lastResult.Success)
            {
                if (attempt > 0)
                {
                    // Log successful retry
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_RETRY_SUCCEEDED",
                        new
                        {
                            intent_id = intentId,
                            instrument = instrument,
                            attempt = attempt + 1,
                            total_attempts = MAX_RETRIES,
                            note = "Flatten succeeded on retry"
                        }));
                }
                return lastResult;
            }
            
            // Log retry attempt
            if (attempt < MAX_RETRIES - 1)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_RETRY_ATTEMPT",
                    new
                    {
                        intent_id = intentId,
                        instrument = instrument,
                        attempt = attempt + 1,
                        total_attempts = MAX_RETRIES,
                        error = lastResult.ErrorMessage,
                        note = "Flatten failed, retrying..."
                    }));
            }
        }
        
        // All retries failed - log POSITION_FLATTEN_FAIL_CLOSED and stand down
        var finalError = $"Flatten failed after {MAX_RETRIES} attempts: {lastResult?.ErrorMessage ?? "Unknown error"}";
        
        // Log POSITION_FLATTEN_FAIL_CLOSED at ERROR level (CRITICAL event)
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "POSITION_FLATTEN_FAIL_CLOSED", new
        {
            intent_id = intentId,
            instrument = instrument,
            retry_count = MAX_RETRIES,
            final_error = finalError,
            last_error_message = lastResult?.ErrorMessage ?? "Unknown error",
            note = "All flatten retries exhausted - position may still be open. Manual intervention required."
        }));
        
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_FAILED_ALL_RETRIES",
            new
            {
                intent_id = intentId,
                instrument = instrument,
                total_attempts = MAX_RETRIES,
                final_error = finalError,
                note = "CRITICAL: Flatten failed after all retries - manual intervention required"
            }));
        
        // Scream loudly: Send emergency notification
        var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
        if (notificationService != null)
        {
            var title = $"EMERGENCY: Flatten Failed After All Retries - {instrument}";
            var message = $"CRITICAL: Flatten failed after {MAX_RETRIES} attempts. Error: {finalError}. Intent: {intentId}. Manual intervention required immediately.";
            notificationService.EnqueueNotification($"FLATTEN_FAILED:{intentId}", title, message, priority: 2); // Emergency priority
        }
        
        // Stand down stream if callback available
        if (IntentMap.TryGetValue(intentId, out var intent))
        {
            _standDownStreamCallback?.Invoke(intent.Stream, utcNow, $"FLATTEN_FAILED_ALL_RETRIES: {finalError}");
        }
        
        return lastResult ?? FlattenResult.FailureResult(finalError, utcNow);
    }

    public FlattenResult Flatten(
        string intentId,
        string instrument,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_ATTEMPT", new
        {
            account = "SIM"
        }));

        if (!_simAccountVerified)
        {
            var error = "SIM account not verified";
            return FlattenResult.FailureResult(error, utcNow);
        }

#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED"
        }));
        return FlattenResult.FailureResult(error, utcNow);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_FAIL", new
            {
                error,
                reason = "NT_CONTEXT_NOT_SET"
            }));
            return FlattenResult.FailureResult(error, utcNow);
        }

        try
        {
            if (_useInstrumentExecutionAuthority && _iea != null)
            {
                var inContext = _strategyThreadContextCount > 0 && _strategyThreadId == System.Threading.Thread.CurrentThread.ManagedThreadId;
                if (inContext)
                    return _iea.RequestFlatten(instrument, intentId, intentId, utcNow);
                var cmd = new NtFlattenInstrumentCommand($"FLATTEN:{intentId}:{utcNow:yyyyMMddHHmmssfff}", intentId, instrument, "FLATTEN_DELEGATED", utcNow);
                _ntActionQueue?.EnqueueNtAction(cmd, out _);
                return FlattenResult.FailureResult("Enqueued for strategy thread", utcNow);
            }
            return FlattenIntentReal(intentId, instrument, utcNow);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_FAIL", new
            {
                error = ex.Message,
                account = "SIM",
                exception_type = ex.GetType().Name
            }));
            
            return FlattenResult.FailureResult($"Flatten failed: {ex.Message}", utcNow);
        }
    }

    public int GetCurrentPosition(string instrument)
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "NT_CONTEXT_NOT_SET", error }));
            throw new InvalidOperationException(error);
        }

        return GetCurrentPositionReal(instrument);
    }
    
    public AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow)
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "NT_CONTEXT_NOT_SET", error }));
            throw new InvalidOperationException(error);
        }

        return GetAccountSnapshotReal(utcNow);
    }
    
    public void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow)
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "NT_CONTEXT_NOT_SET", error }));
            throw new InvalidOperationException(error);
        }

        CancelRobotOwnedWorkingOrdersReal(snap, utcNow);
    }

    /// <summary>
    /// Check all instruments for flat positions and cancel entry stops.
    /// Called on every execution update to detect manual position closures.
    /// </summary>
    private void CheckAllInstrumentsForFlatPositions(DateTimeOffset utcNow)
    {
        OnCheckAllInstrumentsForFlatPositions(utcNow);
    }

    partial void OnCheckAllInstrumentsForFlatPositions(DateTimeOffset utcNow);
    
    /// <summary>
    /// Check for unprotected positions and flatten if protectives not acknowledged within timeout.
    /// </summary>
    private void CheckUnprotectedPositions(DateTimeOffset utcNow)
    {
        const double UNPROTECTED_POSITION_TIMEOUT_SECONDS = 10.0;
        
        foreach (var kvp in OrderMap)
        {
            var orderInfo = kvp.Value;
            
            // Only check entry orders that are filled
            if (!orderInfo.IsEntryOrder || orderInfo.State != "FILLED" || !orderInfo.EntryFillTime.HasValue)
                continue;
            
            var elapsed = (utcNow - orderInfo.EntryFillTime.Value).TotalSeconds;
            
            // Check if timeout exceeded and protectives not acknowledged
            if (elapsed > UNPROTECTED_POSITION_TIMEOUT_SECONDS)
            {
                if (!orderInfo.ProtectiveStopAcknowledged || !orderInfo.ProtectiveTargetAcknowledged)
                {
                    // Flatten position and stand down stream
                    var intentId = orderInfo.IntentId;
                    var instrument = orderInfo.Instrument;
                    
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "UNPROTECTED_POSITION_TIMEOUT",
                        new
                        {
                            intent_id = intentId,
                            instrument = instrument,
                            elapsed_seconds = elapsed,
                            protective_stop_acknowledged = orderInfo.ProtectiveStopAcknowledged,
                            protective_target_acknowledged = orderInfo.ProtectiveTargetAcknowledged,
                            timeout_seconds = UNPROTECTED_POSITION_TIMEOUT_SECONDS,
                            note = "Position unprotected beyond timeout - flattening and standing down stream"
                        }));
                    
                    // Get intent to find stream
                    if (IntentMap.TryGetValue(intentId, out var intent))
                    {
                        // SIMPLIFICATION: Use centralized fail-closed pattern
                        var failureReason = $"Unprotected position timeout ({UNPROTECTED_POSITION_TIMEOUT_SECONDS}s) - protective orders not acknowledged";
                        FailClosed(
                            intentId,
                            intent,
                            failureReason,
                            "UNPROTECTED_POSITION_TIMEOUT_FLATTENED",
                            $"UNPROTECTED_TIMEOUT:{intentId}",
                            $"CRITICAL: Unprotected Position Timeout - {instrument}",
                            $"Entry filled but protective orders not acknowledged within {UNPROTECTED_POSITION_TIMEOUT_SECONDS} seconds. Position flattened. Stream: {intent.Stream}, Intent: {intentId}",
                            null, // stopResult
                            null, // targetResult
                            new 
                            { 
                                elapsed_seconds = elapsed,
                                protective_stop_acknowledged = orderInfo.ProtectiveStopAcknowledged,
                                protective_target_acknowledged = orderInfo.ProtectiveTargetAcknowledged,
                                timeout_seconds = UNPROTECTED_POSITION_TIMEOUT_SECONDS
                            }, // additionalData
                            utcNow);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Trigger quantity emergency handler (idempotent).
    /// Cancels orders, flattens intent, and stands down stream.
    /// </summary>
    private void TriggerQuantityEmergency(string intentId, string emergencyType, DateTimeOffset utcNow, Dictionary<string, object> details)
    {
        // Idempotent: only trigger once per intent unless reason changes
        if (_emergencyTriggered.Contains(intentId))
        {
            // Already triggered - log but don't repeat actions
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", 
                $"{emergencyType}_REPEAT", new
            {
                intent_id = intentId,
                note = "Emergency already triggered for this intent"
            }));
            return;
        }
        
        _emergencyTriggered.Add(intentId);
        
        // Cancel all remaining intent orders
        CancelIntentOrders(intentId, utcNow);
        
        // Flatten intent exposure with retry logic
        if (IntentMap.TryGetValue(intentId, out var intent))
        {
            FlattenWithRetry(intentId, intent.Instrument, utcNow);
        }
        
        // Emit emergency log
        var emergencyPayload = new Dictionary<string, object>(details)
        {
            { "intent_id", intentId },
            { "action_taken", new[] { "CANCEL_ORDERS", "FLATTEN_INTENT" } }
        };
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", emergencyType, emergencyPayload));
        
        // Emit notification (highest priority)
        var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
        if (notificationService != null)
        {
            var title = $"EMERGENCY: {emergencyType} - {intentId}";
            var reason = details.TryGetValue("reason", out var reasonObj) ? reasonObj?.ToString() ?? "Unknown" : "Unknown";
            var message = $"Quantity invariant violated: {reason}. Orders cancelled, position flattened.";
            notificationService.EnqueueNotification($"{emergencyType}:{intentId}", title, message, priority: 2); // Emergency priority
        }
        
        // Stand down stream/intent (via callback)
        if (IntentMap.TryGetValue(intentId, out var intentForCallback))
        {
            _standDownStreamCallback?.Invoke(intentForCallback.Stream, utcNow, emergencyType);
        }
    }
    
    /// <summary>
    /// Get active intents that need break-even monitoring.
    /// Returns intents with filled entries that haven't had BE triggered yet.
    /// 
    /// CRITICAL FIX: Check execution journal instead of OrderMap because protective orders
    /// overwrite entry orders in OrderMap (entry order is replaced with stop/target orders).
    /// Execution journal is the source of truth for entry fill status.
    /// </summary>
    /// <param name="executionInstrument">Optional. When provided, only return intents for this execution instrument (e.g. MES, MYM).
    /// CRITICAL: Each strategy instance receives ticks for ONE instrument only. Without this filter, we'd compare MES tick price
    /// against YM/GC/NG intents (wrong price scales) causing false triggers or missed triggers.</param>
    public List<(string intentId, Intent intent, decimal beTriggerPrice, decimal entryPrice, decimal? actualFillPrice, string direction)> GetActiveIntentsForBEMonitoring()
    {
        return GetActiveIntentsForBEMonitoring(null);
    }

    public List<(string intentId, Intent intent, decimal beTriggerPrice, decimal entryPrice, decimal? actualFillPrice, string direction)> GetActiveIntentsForBEMonitoring(string? executionInstrument)
    {
        var activeIntents = new List<(string, Intent, decimal, decimal, decimal?, string)>();
        
        // CRITICAL FIX: Iterate over IntentMap instead of OrderMap
        // OrderMap gets overwritten by protective orders (stop/target), so entry orders are lost
        // Execution journal is the authoritative source for entry fill status
        foreach (var kvp in IntentMap)
        {
            var intentId = kvp.Key;
            var intent = kvp.Value;
            
            // CRITICAL FIX: Filter by execution instrument - each strategy only gets ticks for its chart
            // Without this, MES strategy would compare MES tick price against YM/GC/NG intents (wrong!)
            if (!string.IsNullOrEmpty(executionInstrument) &&
                !string.Equals(intent.ExecutionInstrument, executionInstrument, StringComparison.OrdinalIgnoreCase))
                continue;
            
            // Check if intent has required fields for BE monitoring
            if (intent.BeTrigger == null || intent.EntryPrice == null || intent.Direction == null)
                continue;
            
            // Check if entry has been filled using execution journal (source of truth)
            // Execution journal tracks entry fills regardless of OrderMap state
            var tradingDate = intent.TradingDate ?? "";
            var stream = intent.Stream ?? "";
            
            ExecutionJournalEntry? journalEntry = null;
            try
            {
                journalEntry = _executionJournal.GetEntry(intentId, tradingDate, stream);
            }
            catch
            {
                // If journal lookup fails, skip this intent
                continue;
            }
            
            // Only check intents with filled entries
            if (journalEntry == null || !journalEntry.EntryFilled || journalEntry.EntryFilledQuantityTotal <= 0)
                continue;
            
            // ZOMBIE_STOP FIX: Never place or modify BE for completed trades - prevents zombie stop orders
            // that can fill after target/stop hit, creating extra positions not in journal (QTY_MISMATCH)
            if (journalEntry.TradeCompleted)
                continue;
            
            // TERMINAL_INTENT HARDENING: Only include intents with remaining open quantity
            if (journalEntry.ExitFilledQuantityTotal >= journalEntry.EntryFilledQuantityTotal)
                continue;
            
            // Check if BE has already been triggered (idempotency check)
            if (_executionJournal.IsBEModified(intentId, tradingDate, stream))
                continue;

            // Skip if pending modify (waiting for confirmation)
            if (HasPendingBEForIntent(intentId))
                continue;
            
            // Get actual fill price from execution journal for logging/debugging purposes
            // NOTE: BE stop uses breakout level (entryPrice), not actual fill price
            // The breakout level is the strategic entry point, slippage shouldn't affect BE stop placement
            decimal? actualFillPrice = journalEntry.FillPrice;
            
            // entryPrice is the breakout level (brkLong/brkShort for stop orders, limit price for limit orders)
            activeIntents.Add((intentId, intent, intent.BeTrigger.Value, intent.EntryPrice.Value, actualFillPrice, intent.Direction));
        }
        
        return activeIntents;
    }

    /// <summary>
    /// Get count of open journal entries for an instrument (for BE_ACCOUNT_EXPOSURE_WITHOUT_INTENT).
    /// </summary>
    public int GetOpenJournalCountForInstrument(string executionInstrument, string? canonicalInstrument = null)
    {
        return _executionJournal.GetOpenJournalCountForInstrument(executionInstrument, canonicalInstrument);
    }
    
    /// <summary>
    /// Cancel orders for a specific intent only.
    /// </summary>
    public bool CancelIntentOrders(string intentId, DateTimeOffset utcNow)
    {
        if (!_simAccountVerified)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_BLOCKED", state: "ENGINE",
                new { intent_id = intentId, reason = "SIM_ACCOUNT_NOT_VERIFIED" }));
            return false;
        }
        
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_BLOCKED", state: "ENGINE",
            new { intent_id = intentId, reason = "NINJATRADER_NOT_DEFINED", error }));
        return false;
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_BLOCKED", state: "ENGINE",
                new { intent_id = intentId, reason = "NT_CONTEXT_NOT_SET", error }));
            return false;
        }

        try
        {
            return CancelIntentOrdersReal(intentId, utcNow);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_ERROR", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    error = ex.Message,
                    exception_type = ex.GetType().Name
                }));
            return false;
        }
    }
    
    /// <summary>
    /// Flatten exposure for a specific intent only.
    /// </summary>
    public FlattenResult FlattenIntent(string intentId, string instrument, DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_INTENT_ATTEMPT", state: "ENGINE",
            new
            {
                intent_id = intentId,
                instrument = instrument
            }));
        
        if (!_simAccountVerified)
        {
            var error = "SIM account not verified";
            return FlattenResult.FailureResult(error, utcNow);
        }
        
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_INTENT_ERROR", state: "ENGINE",
            new { intent_id = intentId, instrument, reason = "NINJATRADER_NOT_DEFINED", error }));
        return FlattenResult.FailureResult(error, utcNow);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_INTENT_ERROR", state: "ENGINE",
                new { intent_id = intentId, instrument, reason = "NT_CONTEXT_NOT_SET", error }));
            return FlattenResult.FailureResult(error, utcNow);
        }

        // Use retry logic for FlattenIntent as well
        return FlattenWithRetry(intentId, instrument, utcNow);
    }

    /// <summary>Quantize price to tick using deterministic rounding (MidpointRounding.AwayFromZero).</summary>
    private static decimal QuantizeToTick(decimal price, decimal tickSize)
    {
        if (tickSize <= 0) return price;
        return Math.Round(price / tickSize, 0, MidpointRounding.AwayFromZero) * tickSize;
    }

    /// <summary>Purge pending BE for intent on exit (target/stop fill, flatten, journal close). Emit STOP_MODIFY_PENDING_CLEARED_ON_EXIT.</summary>
    internal void PurgePendingBEForIntent(string intentId, DateTimeOffset utcNow, string instrument = "", string reason = "exit")
    {
        var purged = new List<string>();
        string? lastInstrument = null;
        foreach (var kvp in _pendingBERequests.ToArray())
        {
            if (kvp.Value.IntentId == intentId)
            {
                lastInstrument = kvp.Value.Instrument;
                if (_pendingBERequests.TryRemove(kvp.Key, out _))
                    purged.Add(kvp.Key);
            }
        }
        if (purged.Count > 0)
        {
            _pendingBECancelUtcByIntent.TryRemove(intentId, out _);
        _pendingBECancelReplaceByIntent.TryRemove(intentId, out _);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, lastInstrument ?? instrument, "STOP_MODIFY_PENDING_CLEARED_ON_EXIT", new
            {
                intent_id = intentId,
                purged_stop_order_ids = purged,
                reason
            }));
        }
    }

    /// <summary>Get tick size for instrument. YM: 1, CL: 0.01, etc.</summary>
    private static decimal GetTickSizeForInstrument(string instrument)
    {
        var u = (instrument ?? "").ToUpperInvariant();
        if (u.Contains("YM") || u.Contains("MYM")) return 1m;
        if (u.Contains("CL") || u.Contains("MCL")) return 0.01m;
        if (u.Contains("NQ") || u.Contains("MNQ") || u.Contains("M2K")) return 0.25m;
        if (u.Contains("ES") || u.Contains("MES")) return 0.25m;
        if (u.Contains("GC") || u.Contains("MGC")) return 0.1m;
        if (u.Contains("NG") || u.Contains("MNG")) return 0.001m;
        return 0.01m; // default
    }
}

/// <summary>Pending BE modification request. Keyed by stop_order_id.</summary>
internal sealed class PendingBERequest
{
    public string IntentId { get; set; } = "";
    public string? OcoId { get; set; }
    public decimal RequestedStopPriceRaw { get; set; }
    public decimal RequestedStopPriceQuantized { get; set; }
    public DateTimeOffset RequestedUtc { get; set; }
    public string TradingDate { get; set; } = "";
    public string Stream { get; set; } = "";
    public string Instrument { get; set; } = "";
    public string ExecutionInstrumentKey { get; set; } = "";
    public int RetryCount { get; set; }
    public string RawTag { get; set; } = "";
    /// <summary>When set, we're waiting for retry time. Null = initial confirmation wait.</summary>
    public DateTimeOffset? RetryUtc { get; set; }
}
