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
}
#endif
