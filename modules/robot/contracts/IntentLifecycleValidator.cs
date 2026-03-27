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

    /// <summary>
    /// Maps broker/registry truth for an adopted ENTRY order to the intent lifecycle state.
    /// Used only for adoption reconstruction (direct set), not for normal FSM transitions.
    /// </summary>
    public static IntentLifecycleState MapAdoptionEntryReconstructionState(int filledQty, int orderQty, bool hasProtectives)
    {
        if (filledQty <= 0)
            return IntentLifecycleState.ENTRY_WORKING;
        if (orderQty > 0 && filledQty < orderQty)
            return IntentLifecycleState.ENTRY_PARTIALLY_FILLED;
        if (orderQty > 0 && filledQty >= orderQty)
            return hasProtectives ? IntentLifecycleState.PROTECTIVES_ACTIVE : IntentLifecycleState.ENTRY_FILLED;
        if (filledQty > 0)
            return hasProtectives ? IntentLifecycleState.PROTECTIVES_ACTIVE : IntentLifecycleState.ENTRY_FILLED;
        return IntentLifecycleState.ENTRY_WORKING;
    }

    /// <summary>
    /// True when a transition event is a duplicate replay: already at the post-transition state.
    /// Suppresses INTENT_LIFECYCLE_TRANSITION_INVALID after adoption reconstruction or fill replay.
    /// </summary>
    public static bool IsIdempotentIntentReplay(IntentLifecycleState current, IntentLifecycleTransition transition) =>
        (current, transition) switch
        {
            (IntentLifecycleState.ENTRY_FILLED, IntentLifecycleTransition.ENTRY_FILLED) => true,
            (IntentLifecycleState.ENTRY_PARTIALLY_FILLED, IntentLifecycleTransition.ENTRY_PARTIALLY_FILLED) => true,
            (IntentLifecycleState.ENTRY_WORKING, IntentLifecycleTransition.ENTRY_ACCEPTED) => true,
            (IntentLifecycleState.PROTECTIVES_ACTIVE, IntentLifecycleTransition.PROTECTIVES_PLACED) => true,
            (IntentLifecycleState.EXIT_PENDING, IntentLifecycleTransition.EXIT_STARTED) => true,
            (IntentLifecycleState.TERMINAL, IntentLifecycleTransition.INTENT_COMPLETED) => true,
            _ => false
        };
}
