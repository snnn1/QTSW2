using System;

namespace QTSW2.Robot.Contracts;

/// <summary>
/// Explicit lifecycle states for a trading intent. Owned by IEA; validated on transitions.
/// </summary>
public enum IntentLifecycleState
{
    CREATED,
    ENTRY_SUBMITTED,
    ENTRY_WORKING,
    ENTRY_PARTIALLY_FILLED,
    ENTRY_FILLED,
    PROTECTIVES_ACTIVE,
    EXIT_PENDING,
    TERMINAL
}

/// <summary>
/// Logical transitions between lifecycle states. Represents events, not broker operations.
/// </summary>
public enum IntentLifecycleTransition
{
    SUBMIT_ENTRY,
    ENTRY_ACCEPTED,
    ENTRY_PARTIALLY_FILLED,
    ENTRY_FILLED,
    PROTECTIVES_PLACED,
    EXIT_STARTED,
    INTENT_COMPLETED
}
