using System;
using System.Collections.Generic;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Non–NinjaTrader harness: registry/latch/dedup helpers that live in NT partials when building RobotCore_For_NinjaTrader.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    private readonly Dictionary<string, IntentLifecycleState> _harnessLifecycleByIntentId = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _harnessLifecycleLock = new();

    public bool IsSupervisorilyBlocked => false;

    public SupervisoryState CurrentSupervisoryState => SupervisoryState.ACTIVE;

    public bool HasDeferredAdoptionScanPending => false;

    internal void SetOnRecoveryRequestedCallback(Action<string, string, object, DateTimeOffset>? callback)
    {
    }

    internal void SetOnRecoveryFlattenRequestedCallback(Action<string, string, string, DateTimeOffset>? callback)
    {
    }

    internal void SetOnBootstrapSnapshotRequestedCallback(Action<string, BootstrapReason, DateTimeOffset>? callback)
    {
    }

    internal void SetOnSupervisoryCriticalCallback(Action<string, string, object>? callback)
    {
    }

    internal void SetOnP2StreamContainmentCallback(Action<StateOwnershipAttributionResult, DateTimeOffset>? callback)
    {
    }

    public IntentLifecycleState GetIntentLifecycleState(string intentId)
    {
        if (string.IsNullOrEmpty(intentId)) return IntentLifecycleState.CREATED;
        lock (_harnessLifecycleLock)
        {
            return _harnessLifecycleByIntentId.TryGetValue(intentId, out var state)
                ? state
                : IntentLifecycleState.CREATED;
        }
    }

    public bool TryTransitionIntentLifecycle(string intentId, IntentLifecycleTransition transition, string? commandId, DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(intentId)) return false;
        lock (_harnessLifecycleLock)
        {
            var current = _harnessLifecycleByIntentId.TryGetValue(intentId, out var s)
                ? s
                : IntentLifecycleState.CREATED;
            var (valid, next) = IntentLifecycleValidator.TryGetTransition(current, transition);
            if (!valid)
                return IntentLifecycleValidator.IsIdempotentIntentReplay(current, transition);
            _harnessLifecycleByIntentId[intentId] = next;
            return true;
        }
    }

    internal void EnsureIntentLifecycleCreated(string intentId)
    {
        if (string.IsNullOrEmpty(intentId)) return;
        lock (_harnessLifecycleLock)
        {
            if (!_harnessLifecycleByIntentId.ContainsKey(intentId))
                _harnessLifecycleByIntentId[intentId] = IntentLifecycleState.CREATED;
        }
    }

    internal void ReconstructIntentLifecycleAfterEntryAdoption(string intentId, string instrument, OrderInfo orderInfo, DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(intentId) || orderInfo == null) return;
        var orderQty = orderInfo.Quantity > 0 ? orderInfo.Quantity : orderInfo.ExpectedQuantity;
        var target = IntentLifecycleValidator.MapAdoptionEntryReconstructionState(orderInfo.FilledQuantity, orderQty, hasProtectives: false);
        lock (_harnessLifecycleLock)
        {
            _harnessLifecycleByIntentId[intentId] = target;
        }
    }

    public void EnqueueExecutionCommand(ExecutionCommandBase command)
    {
        if (command == null) return;
        EnqueueCore(() =>
        {
            var intentId = command.IntentId ?? "";
            switch (command)
            {
                case FlattenIntentCommand:
                case CancelIntentOrdersCommand:
                    if (!string.IsNullOrEmpty(intentId))
                        TryTransitionIntentLifecycle(intentId, IntentLifecycleTransition.EXIT_STARTED, command.CommandId, command.TimestampUtc);
                    break;
                case SubmitEntryIntentCommand submit:
                    EnsureIntentLifecycleCreated(submit.LongIntentId ?? "");
                    EnsureIntentLifecycleCreated(submit.ShortIntentId ?? "");
                    TryTransitionIntentLifecycle(submit.LongIntentId ?? "", IntentLifecycleTransition.SUBMIT_ENTRY, command.CommandId, command.TimestampUtc);
                    TryTransitionIntentLifecycle(submit.ShortIntentId ?? "", IntentLifecycleTransition.SUBMIT_ENTRY, command.CommandId, command.TimestampUtc);
                    break;
                case SubmitMarketReentryCommand reentry:
                    EnsureIntentLifecycleCreated(reentry.ReentryIntentId ?? "");
                    TryTransitionIntentLifecycle(reentry.ReentryIntentId ?? "", IntentLifecycleTransition.SUBMIT_ENTRY, command.CommandId, command.TimestampUtc);
                    break;
            }
        }, "ExecutionCommandHarness");
    }

    public void EnqueueExecutionUpdate(object execution, object order)
    {
        if (Executor == null) return;
        EnqueueRecoveryEssential(() => Executor.ProcessExecutionUpdate(execution, order), "ExecutionUpdate");
    }

    public void BeginBootstrapForInstrument(string instrument, BootstrapReason reason, DateTimeOffset utcNow)
    {
    }

    public void BeginReconnectRecovery(string instrument, DateTimeOffset utcNow)
    {
    }

    public void RequestRecovery(string instrument, string reason, object context, DateTimeOffset utcNow)
    {
    }

    internal void PrepareRegistryForMismatchAssemblyFromSnapshot(string instrument, AccountSnapshot snap, DateTimeOffset utcNow)
    {
    }

    internal bool TryRetryDeferredAdoptionScanIfDeferred() => false;

    public ReconciliationScheduleOutcome TryScheduleRecoveryAdoptionScan(out int adoptedCountIfRanSynchronously)
    {
        adoptedCountIfRanSynchronously = 0;
        return ReconciliationScheduleOutcome.SKIPPED;
    }

    internal void RecordDeferralHeartbeatRetryForProof(DateTimeOffset utcNow)
    {
    }

    internal object GetAdoptionDeferralRetryProofPayload() => new
    {
        harness = true,
        execution_instrument_key = ExecutionInstrumentKey
    };

    public void RequestSupervisoryAction(string instrument, SupervisoryTriggerReason reason, SupervisorySeverity severity, object? context, DateTimeOffset utcNow)
    {
    }

    internal void CheckFlattenLatchTimeouts()
    {
    }

    internal int RunRegistryCleanup(DateTimeOffset utcNow, Func<string, bool>? intentIsActive = null) => 0;

    internal void VerifyRegistryIntegrity(DateTimeOffset utcNow)
    {
    }

    internal void EmitRegistryMetrics(DateTimeOffset utcNow)
    {
    }

    internal void EmitExecutionOrderingMetrics(DateTimeOffset utcNow)
    {
    }

    private void EvictDedupEntries()
    {
    }
}
