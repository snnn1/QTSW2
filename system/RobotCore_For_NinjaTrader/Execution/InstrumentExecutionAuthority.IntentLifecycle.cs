#if NINJATRADER
using System;
using System.Collections.Generic;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// IEA Intent Lifecycle: Explicit state machine for intent lifecycle.
/// Tracks state per intent; validates transitions; rejects invalid commands.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    private readonly Dictionary<string, IntentLifecycleState> _intentLifecycleByIntentId = new();
    private readonly object _lifecycleLock = new();

    /// <summary>Get current lifecycle state for intent. Returns CREATED if unknown (new intent).</summary>
    public IntentLifecycleState GetIntentLifecycleState(string intentId)
    {
        if (string.IsNullOrEmpty(intentId)) return IntentLifecycleState.CREATED;
        lock (_lifecycleLock)
        {
            return _intentLifecycleByIntentId.TryGetValue(intentId, out var state) ? state : IntentLifecycleState.CREATED;
        }
    }

    /// <summary>Try to transition intent lifecycle. Returns true if valid and applied.</summary>
    public bool TryTransitionIntentLifecycle(string intentId, IntentLifecycleTransition transition, string? commandId, DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(intentId)) return false;
        lock (_lifecycleLock)
        {
            var current = _intentLifecycleByIntentId.TryGetValue(intentId, out var s) ? s : IntentLifecycleState.CREATED;
            var (valid, newState) = IntentLifecycleValidator.TryGetTransition(current, transition);
            if (!valid)
            {
                if (IntentLifecycleValidator.IsIdempotentIntentReplay(current, transition))
                    return true;
                Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId, ExecutionInstrumentKey, "INTENT_LIFECYCLE_TRANSITION_INVALID",
                    new { intentId, currentState = current.ToString(), attemptedTransition = transition.ToString(), commandId, timestampUtc = utcNow }));
                return false;
            }
            _intentLifecycleByIntentId[intentId] = newState;
            Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId, ExecutionInstrumentKey, "INTENT_LIFECYCLE_TRANSITION",
                new { intentId, previousState = current.ToString(), newState = newState.ToString(), transition = transition.ToString(), commandId, timestampUtc = utcNow }));
            return true;
        }
    }

    /// <summary>Ensure intent is in CREATED state (for new intents). Idempotent if already CREATED.</summary>
    internal void EnsureIntentLifecycleCreated(string intentId)
    {
        if (string.IsNullOrEmpty(intentId)) return;
        lock (_lifecycleLock)
        {
            if (!_intentLifecycleByIntentId.ContainsKey(intentId))
                _intentLifecycleByIntentId[intentId] = IntentLifecycleState.CREATED;
        }
    }

    /// <summary>
    /// After ENTRY adoption, sets intent lifecycle from broker/registry truth (not implicit CREATED).
    /// Idempotent: same reconstructed state leaves storage unchanged and does not re-log.
    /// </summary>
    internal void ReconstructIntentLifecycleAfterEntryAdoption(string intentId, string instrument, OrderInfo orderInfo, DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(intentId) || orderInfo == null) return;

        var filledQty = orderInfo.FilledQuantity;
        var orderQty = orderInfo.Quantity > 0 ? orderInfo.Quantity : orderInfo.ExpectedQuantity;
        var hasProtectives = HasLiveProtectiveRegistryOrdersForIntent(intentId);
        var target = IntentLifecycleValidator.MapAdoptionEntryReconstructionState(filledQty, orderQty, hasProtectives);
        SetIntentLifecycleStateDirect(intentId, target, instrument, filledQty, orderQty, hasProtectives, utcNow);
    }

    private static bool IsLiveProtectiveOrderEntry(OrderRegistryEntry? e)
    {
        if (e == null) return false;
        return e.LifecycleState == OrderLifecycleState.CREATED ||
               e.LifecycleState == OrderLifecycleState.SUBMITTED ||
               e.LifecycleState == OrderLifecycleState.WORKING ||
               e.LifecycleState == OrderLifecycleState.PART_FILLED;
    }

    private bool HasLiveProtectiveRegistryOrdersForIntent(string intentId)
    {
        var stopLive = TryResolveByAlias($"{intentId}:STOP", out var stop) && IsLiveProtectiveOrderEntry(stop);
        var targetLive = TryResolveByAlias($"{intentId}:TARGET", out var tgt) && IsLiveProtectiveOrderEntry(tgt);
        return stopLive || targetLive;
    }

    /// <summary>Direct lifecycle set for adoption reconstruction only. Does not validate FSM edges.</summary>
    private void SetIntentLifecycleStateDirect(
        string intentId,
        IntentLifecycleState targetState,
        string instrument,
        int filledQty,
        int orderQty,
        bool hasProtectives,
        DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(intentId)) return;
        lock (_lifecycleLock)
        {
            var cur = _intentLifecycleByIntentId.TryGetValue(intentId, out var s) ? s : IntentLifecycleState.CREATED;
            if (cur == targetState)
                return;
            _intentLifecycleByIntentId[intentId] = targetState;
        }

        Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId, ExecutionInstrumentKey, "INTENT_LIFECYCLE_RECONSTRUCTED",
            new
            {
                intent_id = intentId,
                instrument,
                reconstructed_state = targetState.ToString(),
                reason = "adoption_reconstruction",
                source = "adoption",
                filled_qty = filledQty,
                order_qty = orderQty,
                has_protectives = hasProtectives,
                timestampUtc = utcNow
            }));
    }
}
#endif
