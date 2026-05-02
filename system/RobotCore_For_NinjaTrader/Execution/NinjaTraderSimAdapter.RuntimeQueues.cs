// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class NinjaTraderSimAdapter
{
    private static string NormalizeMarketReentryExecutionInstrument(string? instrument) =>
        string.IsNullOrWhiteSpace(instrument) ? "" : instrument.Trim().ToUpperInvariant();

    bool INtMarketReentryExecutionGate.TryBeginMarketReentryExecution(
        NtSubmitMarketReentryCommand cmd,
        DateTimeOffset utcNow,
        out string deferReason,
        out string? activeIntentId)
    {
        deferReason = "";
        activeIntentId = null;
        var intentId = cmd.IntentId ?? cmd.Command.ReentryIntentId ?? "";
        var inst = NormalizeMarketReentryExecutionInstrument(cmd.InstrumentKey ?? cmd.Command.ExecutionInstrument ?? cmd.Command.Instrument);
        if (string.IsNullOrEmpty(inst) || string.IsNullOrEmpty(intentId))
            return true;

        MarketReentryExecutionLatch? acquired = null;
        lock (_marketReentryExecutionLatchLock)
        {
            if (_marketReentryExecutionLatchByInstrument.TryGetValue(inst, out var active))
            {
                if (MarketReentryLatchMatches(active, intentId, cmd.CorrelationId))
                    return true;

                active.DeferralCount++;
                activeIntentId = active.IntentId;
                deferReason = "active_reentry_waiting_for_protection";
                return false;
            }

            acquired = new MarketReentryExecutionLatch
            {
                InstrumentKey = inst,
                IntentId = intentId,
                Stream = cmd.Command.Stream,
                CorrelationId = cmd.CorrelationId,
                AcquiredAtUtc = utcNow,
                CommandUtc = cmd.Command.TimestampUtc
            };
            _marketReentryExecutionLatchByInstrument[inst] = acquired;
        }

        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, inst, "REENTRY_BATCH_LATCH_ACQUIRED",
            new
            {
                reentry_intent_id = intentId,
                stream = acquired.Stream,
                correlation_id = acquired.CorrelationId,
                command_utc = acquired.CommandUtc,
                note = "Same-instrument market reentry owns the batch latch until protection is accepted or submit/protection fails"
            }));
        return true;
    }

    void INtMarketReentryExecutionGate.ReleaseMarketReentryExecution(NtSubmitMarketReentryCommand cmd, DateTimeOffset utcNow, string reason)
    {
        ReleaseMarketReentryExecutionLatch(
            cmd.IntentId ?? cmd.Command.ReentryIntentId ?? "",
            cmd.InstrumentKey ?? cmd.Command.ExecutionInstrument ?? cmd.Command.Instrument,
            utcNow,
            reason,
            cmd.CorrelationId);
    }

    private static bool MarketReentryLatchMatches(MarketReentryExecutionLatch active, string intentId, string? correlationId)
    {
        return (!string.IsNullOrEmpty(intentId) && string.Equals(active.IntentId, intentId, StringComparison.OrdinalIgnoreCase))
               || (!string.IsNullOrEmpty(correlationId) && string.Equals(active.CorrelationId, correlationId, StringComparison.OrdinalIgnoreCase));
    }

    private void ReleaseMarketReentryExecutionLatch(string intentId, string? instrument, DateTimeOffset utcNow, string reason, string? correlationId = null)
    {
        MarketReentryExecutionLatch? released = null;
        var inst = NormalizeMarketReentryExecutionInstrument(instrument);

        lock (_marketReentryExecutionLatchLock)
        {
            if (!string.IsNullOrEmpty(inst) &&
                _marketReentryExecutionLatchByInstrument.TryGetValue(inst, out var active) &&
                MarketReentryLatchMatches(active, intentId, correlationId))
            {
                _marketReentryExecutionLatchByInstrument.Remove(inst);
                released = active;
            }
            else
            {
                string? releaseKey = null;
                foreach (var kvp in _marketReentryExecutionLatchByInstrument)
                {
                    if (!MarketReentryLatchMatches(kvp.Value, intentId, correlationId))
                        continue;

                    releaseKey = kvp.Key;
                    released = kvp.Value;
                    break;
                }

                if (!string.IsNullOrEmpty(releaseKey))
                    _marketReentryExecutionLatchByInstrument.Remove(releaseKey);
            }
        }

        if (released == null)
            return;

        _log.Write(RobotEvents.ExecutionBase(utcNow, released.IntentId, released.InstrumentKey, "REENTRY_BATCH_LATCH_RELEASED",
            new
            {
                reentry_intent_id = released.IntentId,
                stream = released.Stream,
                correlation_id = released.CorrelationId,
                reason,
                held_ms = (utcNow - released.AcquiredAtUtc).TotalMilliseconds,
                deferral_count = released.DeferralCount,
                note = "Same-instrument market reentry latch released; next queued sibling may now be evaluated"
            }));
    }

    private bool IsMarketReentryIntentForLatch(string intentId)
    {
        return !string.IsNullOrEmpty(intentId) &&
               IntentMap.TryGetValue(intentId, out var intent) &&
               intent != null &&
               string.Equals(intent.TriggerReason, "SUBMIT_MARKET_REENTRY", StringComparison.OrdinalIgnoreCase);
    }

    private void ReleaseMarketReentryExecutionLatchIfProtectionFailed(string intentId, string? instrument, DateTimeOffset utcNow, string reason)
    {
        if (IsMarketReentryIntentForLatch(intentId))
            ReleaseMarketReentryExecutionLatch(intentId, instrument, utcNow, reason);
    }
    
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

    /// <summary>Flatten verification: instrumentKey -> (requestedUtc, verifyDeadline, correlationId, episodeId).
    /// Post-submit flat check uses <see cref="IIEAOrderExecutor.GetBrokerCanonicalExposure"/> (same model as reconciliation).</summary>
    private readonly ConcurrentDictionary<string, (DateTimeOffset RequestedUtc, DateTimeOffset VerifyDeadlineUtc, string CorrelationId, string EpisodeId)> _pendingFlattenVerifications = new();
    private const double FLATTEN_VERIFY_WINDOW_SEC = 4.0;
    /// <summary>Aligned with <see cref="FlattenCoordinationTracker.DefaultMaxVerifyRetries"/> (verify-driven retry cap per episode).</summary>
    private const int FLATTEN_VERIFY_MAX_RETRIES = FlattenCoordinationTracker.DefaultMaxVerifyRetries;

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
    /// P2.6.7: Only ExecuteFlattenInstrument (NT command drain) may mint this token.
    /// Blocks arbitrary bypass of FlattenIntentReal pre-submit destructive policy.
    /// </summary>
    public readonly struct NtDestructivePolicyAlreadyAppliedToken
    {
        internal NtDestructivePolicyAlreadyAppliedToken(string correlationId, string expectedInstrumentKey, string precheckPolicyReasonCode)
        {
            CorrelationId = correlationId ?? "";
            ExpectedInstrumentKey = expectedInstrumentKey ?? "";
            PrecheckPolicyReasonCode = precheckPolicyReasonCode ?? "";
        }

        public string CorrelationId { get; }
        public string ExpectedInstrumentKey { get; }
        public string PrecheckPolicyReasonCode { get; }

        internal static NtDestructivePolicyAlreadyAppliedToken ForNtFlattenCommand(NtFlattenInstrumentCommand cmd, string precheckPolicyReasonCode) =>
            new(cmd.CorrelationId, cmd.Instrument, precheckPolicyReasonCode);
    }

    /// <summary>
    /// Guard: ensure we're on strategy thread before calling NT APIs.
    /// If not on strategy thread: emit NT_THREAD_VIOLATION, enqueue action, return false.
    /// In DEBUG: assert. In RELEASE: fail-safe by enqueuing.
    /// </summary>
    /// <returns>true if action ran; false if enqueued (caller should return without touching NT APIs).</returns>
    private bool EnsureStrategyThreadOrEnqueue(string methodName, string? intentId, string? instrument, string? correlationId, Action ntAction)
    {
        if (IsStrategyThreadContext())
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

    private bool IsStrategyThreadContext()
    {
        var currentThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        return _strategyThreadContextCount > 0 && _strategyThreadId == currentThreadId;
    }

    /// <summary>Optional callback for canary tests. Invoked when guard triggers (NT_THREAD_VIOLATION).</summary>
    private Action<string>? _guardViolationCallback;
    internal void SetGuardViolationCallback(Action<string>? callback) => _guardViolationCallback = callback;

    bool IIEAOrderExecutor.EnqueueNtAction(INtAction action)
    {
        if (_ntActionQueue == null) return false;
        if (action is NtFlattenInstrumentCommand fSafety && !TryExecutionSafetyFlattenGuard(fSafety.Instrument ?? "", fSafety.IntentId, fSafety.UtcNow, "IEA_ENQUEUE_FLATTEN", fSafety.CorrelationId, out _))
            return false;
        if (action is NtFlattenInstrumentCommand fCmd && !TryCoordinationGateFlattenEnqueue(fCmd, out _))
            return false;
        var actionIntentId = action.IntentId ?? "";
        if (string.Equals(action.ActionType, "SUBMIT_PROTECTIVES", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(actionIntentId))
        {
            SetProtectionState(actionIntentId, ProtectionState.Enqueued);
            _firstEnqueuedUtcByIntent.TryAdd(actionIntentId, DateTimeOffset.UtcNow);
            if (action is NtSubmitProtectivesCommand protectives)
                NotifyProtectiveSubmitPendingForAction(protectives);
        }
        return _ntActionQueue.EnqueueNtAction(action, out _);
    }

    private void NotifyProtectiveSubmitPendingForAction(NtSubmitProtectivesCommand cmd)
    {
        var utcNow = cmd.UtcNow == default ? DateTimeOffset.UtcNow : cmd.UtcNow;
        var candidates = new[]
        {
            cmd.Instrument,
            cmd.InstrumentKey,
            _iea?.ExecutionInstrumentKey,
            _ieaEngineExecutionInstrument
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var inst = candidate?.Trim();
            if (string.IsNullOrEmpty(inst))
                continue;
            var normalizedInst = inst!;
            if (!seen.Add(normalizedInst))
                continue;
            QuantExecutionControlStore.NotifyProtectiveSubmitPending(normalizedInst, 2, utcNow);
        }
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
        if (Volatile.Read(ref _sessionMismatchBlocked) != 0)
            return false;
        if (_isRecoveryExecutionAllowedCallback == null || _isRecoveryExecutionAllowedCallback())
            return false;
        QueueProtectiveForRecovery(intentId, intent, totalFilledQuantity, utcNow);
        return true;
    }

    /// <summary>
    /// Stage 1: Add to queue. Stage 2/3: ProcessRecoveryQueue submits or fail-safe flattens.
    /// </summary>
    private void QueueProtectiveForRecovery(string intentId, Intent intent, int totalFilledQuantity, DateTimeOffset utcNow)
    {
        if (Volatile.Read(ref _sessionMismatchBlocked) != 0)
            return;
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
        var recoveryOk = _isRecoveryExecutionAllowedCallback == null || _isRecoveryExecutionAllowedCallback();
        var killOff = _isGlobalKillSwitchActive == null || !_isGlobalKillSwitchActive();
        var executionAllowed = recoveryOk && killOff;

        foreach (var pending in snapshot)
        {
            var elapsed = (utcNow - pending.QueuedAtUtc).TotalSeconds;

            if (Volatile.Read(ref _sessionMismatchBlocked) != 0)
                continue;

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
                if (!TrySessionIdentityGate(pending.IntentId, pending.Intent.Instrument ?? "", "recovery", utcNow, pending.Intent.TradingDate, out _))
                    continue;
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
                // Recovery not ready and/or kill switch on — put back in queue for next drain
                lock (_pendingRecoveryLock)
                {
                    _pendingRecoveryProtectives.Add(pending);
                }
            }
        }
    }

    private void EnqueueNtActionInternal(INtAction action)
    {
        if (action is NtFlattenInstrumentCommand flatCmd && !TryExecutionSafetyGateFlattenEnqueue(flatCmd, flatCmd.UtcNow))
            return;
        if (action is NtFlattenInstrumentCommand fCmd && !TryCoordinationGateFlattenEnqueue(fCmd, out _))
            return;
        if (action is NtSubmitProtectivesCommand protectives)
            NotifyProtectiveSubmitPendingForAction(protectives);
        if (_ntActionQueue != null)
            _ntActionQueue.EnqueueNtAction(action, out _);
    }

    private void EnqueueStrategyThreadDeferredAction(
        string correlationId,
        string? intentId,
        string? instrumentKey,
        string reason,
        DateTimeOffset utcNow,
        Action action)
    {
        if (_ntActionQueue != null)
        {
            EnqueueNtActionInternal(new NtDeferredAction(correlationId, intentId, instrumentKey, reason, action));
            return;
        }

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "NT_DEFERRED_ACTION_INLINE_FALLBACK", state: "ENGINE",
            new
            {
                correlation_id = correlationId,
                intent_id = intentId,
                instrument_key = instrumentKey,
                reason,
                note = "NT action queue unavailable; executing deferred action inline."
            }));
        action();
    }

    partial void OnVerifyPendingFlattens();

    private bool HasPendingBEForIntent(string intentId)
    {
        foreach (var p in _pendingBERequests.Values)
            if (string.Equals(p.IntentId, intentId, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

}
