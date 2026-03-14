using System.Collections.Generic;

namespace QTSW2.Robot.Contracts;

/// <summary>
/// Validates intent lifecycle transitions. Invalid transitions are rejected.
/// </summary>
public static class IntentLifecycleValidator
{
    private static readonly Dictionary<(IntentLifecycleState, IntentLifecycleTransition), IntentLifecycleState> ValidTransitions = new()
    {
        { (IntentLifecycleState.CREATED, IntentLifecycleTransition.SUBMIT_ENTRY), IntentLifecycleState.ENTRY_SUBMITTED },
        { (IntentLifecycleState.ENTRY_SUBMITTED, IntentLifecycleTransition.ENTRY_ACCEPTED), IntentLifecycleState.ENTRY_WORKING },
        { (IntentLifecycleState.ENTRY_SUBMITTED, IntentLifecycleTransition.ENTRY_PARTIALLY_FILLED), IntentLifecycleState.ENTRY_PARTIALLY_FILLED },
        { (IntentLifecycleState.ENTRY_SUBMITTED, IntentLifecycleTransition.ENTRY_FILLED), IntentLifecycleState.ENTRY_FILLED },
        { (IntentLifecycleState.ENTRY_WORKING, IntentLifecycleTransition.ENTRY_PARTIALLY_FILLED), IntentLifecycleState.ENTRY_PARTIALLY_FILLED },
        { (IntentLifecycleState.ENTRY_WORKING, IntentLifecycleTransition.ENTRY_FILLED), IntentLifecycleState.ENTRY_FILLED },
        { (IntentLifecycleState.ENTRY_PARTIALLY_FILLED, IntentLifecycleTransition.ENTRY_FILLED), IntentLifecycleState.ENTRY_FILLED },
        { (IntentLifecycleState.ENTRY_FILLED, IntentLifecycleTransition.PROTECTIVES_PLACED), IntentLifecycleState.PROTECTIVES_ACTIVE },
        { (IntentLifecycleState.PROTECTIVES_ACTIVE, IntentLifecycleTransition.EXIT_STARTED), IntentLifecycleState.EXIT_PENDING },
        { (IntentLifecycleState.ENTRY_WORKING, IntentLifecycleTransition.EXIT_STARTED), IntentLifecycleState.EXIT_PENDING },
        { (IntentLifecycleState.ENTRY_PARTIALLY_FILLED, IntentLifecycleTransition.EXIT_STARTED), IntentLifecycleState.EXIT_PENDING },
        { (IntentLifecycleState.ENTRY_FILLED, IntentLifecycleTransition.EXIT_STARTED), IntentLifecycleState.EXIT_PENDING },
        { (IntentLifecycleState.CREATED, IntentLifecycleTransition.EXIT_STARTED), IntentLifecycleState.EXIT_PENDING },
        { (IntentLifecycleState.ENTRY_SUBMITTED, IntentLifecycleTransition.EXIT_STARTED), IntentLifecycleState.EXIT_PENDING },
        { (IntentLifecycleState.EXIT_PENDING, IntentLifecycleTransition.INTENT_COMPLETED), IntentLifecycleState.TERMINAL },
    };

    /// <summary>Check if a transition is valid. Returns (true, newState) or (false, currentState).</summary>
    public static (bool Valid, IntentLifecycleState NewState) TryGetTransition(IntentLifecycleState current, IntentLifecycleTransition transition)
    {
        var key = (current, transition);
        if (ValidTransitions.TryGetValue(key, out var newState))
            return (true, newState);
        return (false, current);
    }

    /// <summary>Check if SubmitEntryIntentCommand is allowed in the given state.</summary>
    public static bool IsSubmitEntryIntentAllowed(IntentLifecycleState state) => state == IntentLifecycleState.CREATED;

    /// <summary>Check if CancelIntentOrdersCommand is allowed in the given state.</summary>
    public static bool IsCancelIntentOrdersAllowed(IntentLifecycleState state) =>
        state == IntentLifecycleState.ENTRY_WORKING ||
        state == IntentLifecycleState.ENTRY_PARTIALLY_FILLED ||
        state == IntentLifecycleState.PROTECTIVES_ACTIVE;

    /// <summary>Check if FlattenIntentCommand is allowed in the given state.</summary>
    public static bool IsFlattenIntentAllowed(IntentLifecycleState state) => state != IntentLifecycleState.TERMINAL;
}
