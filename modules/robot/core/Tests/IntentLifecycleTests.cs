// Intent Lifecycle State Machine tests.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test INTENT_LIFECYCLE
//
// Verifies: IntentLifecycleValidator transitions, command legality (IsSubmitEntryIntentAllowed,
// IsCancelIntentOrdersAllowed, IsFlattenIntentAllowed), invalid transitions rejected.

using System;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Core.Tests;

public static class IntentLifecycleTests
{
    public static (bool Pass, string? Error) RunIntentLifecycleTests()
    {
        // --- Valid transitions ---
        if (!ValidTransition(IntentLifecycleState.CREATED, IntentLifecycleTransition.SUBMIT_ENTRY, IntentLifecycleState.ENTRY_SUBMITTED))
            return (false, "CREATED -> SUBMIT_ENTRY should yield ENTRY_SUBMITTED");

        if (!ValidTransition(IntentLifecycleState.ENTRY_SUBMITTED, IntentLifecycleTransition.ENTRY_ACCEPTED, IntentLifecycleState.ENTRY_WORKING))
            return (false, "ENTRY_SUBMITTED -> ENTRY_ACCEPTED should yield ENTRY_WORKING");

        if (!ValidTransition(IntentLifecycleState.ENTRY_SUBMITTED, IntentLifecycleTransition.ENTRY_PARTIALLY_FILLED, IntentLifecycleState.ENTRY_PARTIALLY_FILLED))
            return (false, "ENTRY_SUBMITTED -> ENTRY_PARTIALLY_FILLED should yield ENTRY_PARTIALLY_FILLED");

        if (!ValidTransition(IntentLifecycleState.ENTRY_SUBMITTED, IntentLifecycleTransition.ENTRY_FILLED, IntentLifecycleState.ENTRY_FILLED))
            return (false, "ENTRY_SUBMITTED -> ENTRY_FILLED should yield ENTRY_FILLED");

        if (!ValidTransition(IntentLifecycleState.ENTRY_WORKING, IntentLifecycleTransition.ENTRY_PARTIALLY_FILLED, IntentLifecycleState.ENTRY_PARTIALLY_FILLED))
            return (false, "ENTRY_WORKING -> ENTRY_PARTIALLY_FILLED should yield ENTRY_PARTIALLY_FILLED");

        if (!ValidTransition(IntentLifecycleState.ENTRY_WORKING, IntentLifecycleTransition.ENTRY_FILLED, IntentLifecycleState.ENTRY_FILLED))
            return (false, "ENTRY_WORKING -> ENTRY_FILLED should yield ENTRY_FILLED");

        if (!ValidTransition(IntentLifecycleState.ENTRY_PARTIALLY_FILLED, IntentLifecycleTransition.ENTRY_FILLED, IntentLifecycleState.ENTRY_FILLED))
            return (false, "ENTRY_PARTIALLY_FILLED -> ENTRY_FILLED should yield ENTRY_FILLED");

        if (!ValidTransition(IntentLifecycleState.ENTRY_FILLED, IntentLifecycleTransition.PROTECTIVES_PLACED, IntentLifecycleState.PROTECTIVES_ACTIVE))
            return (false, "ENTRY_FILLED -> PROTECTIVES_PLACED should yield PROTECTIVES_ACTIVE");

        if (!ValidTransition(IntentLifecycleState.PROTECTIVES_ACTIVE, IntentLifecycleTransition.EXIT_STARTED, IntentLifecycleState.EXIT_PENDING))
            return (false, "PROTECTIVES_ACTIVE -> EXIT_STARTED should yield EXIT_PENDING");

        if (!ValidTransition(IntentLifecycleState.EXIT_PENDING, IntentLifecycleTransition.INTENT_COMPLETED, IntentLifecycleState.TERMINAL))
            return (false, "EXIT_PENDING -> INTENT_COMPLETED should yield TERMINAL");

        if (!ValidTransition(IntentLifecycleState.ENTRY_WORKING, IntentLifecycleTransition.EXIT_STARTED, IntentLifecycleState.EXIT_PENDING))
            return (false, "ENTRY_WORKING -> EXIT_STARTED should yield EXIT_PENDING");

        if (!ValidTransition(IntentLifecycleState.CREATED, IntentLifecycleTransition.EXIT_STARTED, IntentLifecycleState.EXIT_PENDING))
            return (false, "CREATED -> EXIT_STARTED should yield EXIT_PENDING");

        // --- Invalid transitions ---
        if (!InvalidTransition(IntentLifecycleState.CREATED, IntentLifecycleTransition.INTENT_COMPLETED))
            return (false, "CREATED -> INTENT_COMPLETED should be invalid");

        if (!InvalidTransition(IntentLifecycleState.ENTRY_WORKING, IntentLifecycleTransition.SUBMIT_ENTRY))
            return (false, "ENTRY_WORKING -> SUBMIT_ENTRY should be invalid");

        if (!InvalidTransition(IntentLifecycleState.TERMINAL, IntentLifecycleTransition.SUBMIT_ENTRY))
            return (false, "TERMINAL -> SUBMIT_ENTRY should be invalid");

        if (!InvalidTransition(IntentLifecycleState.TERMINAL, IntentLifecycleTransition.ENTRY_ACCEPTED))
            return (false, "TERMINAL -> ENTRY_ACCEPTED should be invalid");

        if (!InvalidTransition(IntentLifecycleState.TERMINAL, IntentLifecycleTransition.EXIT_STARTED))
            return (false, "TERMINAL -> EXIT_STARTED should be invalid");

        // --- Command legality ---
        if (!IntentLifecycleValidator.IsSubmitEntryIntentAllowed(IntentLifecycleState.CREATED))
            return (false, "SubmitEntryIntent allowed in CREATED");
        if (IntentLifecycleValidator.IsSubmitEntryIntentAllowed(IntentLifecycleState.ENTRY_SUBMITTED))
            return (false, "SubmitEntryIntent not allowed in ENTRY_SUBMITTED");
        if (IntentLifecycleValidator.IsSubmitEntryIntentAllowed(IntentLifecycleState.TERMINAL))
            return (false, "SubmitEntryIntent not allowed in TERMINAL");

        if (!IntentLifecycleValidator.IsCancelIntentOrdersAllowed(IntentLifecycleState.ENTRY_WORKING))
            return (false, "CancelIntentOrders allowed in ENTRY_WORKING");
        if (!IntentLifecycleValidator.IsCancelIntentOrdersAllowed(IntentLifecycleState.ENTRY_PARTIALLY_FILLED))
            return (false, "CancelIntentOrders allowed in ENTRY_PARTIALLY_FILLED");
        if (!IntentLifecycleValidator.IsCancelIntentOrdersAllowed(IntentLifecycleState.PROTECTIVES_ACTIVE))
            return (false, "CancelIntentOrders allowed in PROTECTIVES_ACTIVE");
        if (IntentLifecycleValidator.IsCancelIntentOrdersAllowed(IntentLifecycleState.CREATED))
            return (false, "CancelIntentOrders not allowed in CREATED");
        if (IntentLifecycleValidator.IsCancelIntentOrdersAllowed(IntentLifecycleState.TERMINAL))
            return (false, "CancelIntentOrders not allowed in TERMINAL");

        if (!IntentLifecycleValidator.IsFlattenIntentAllowed(IntentLifecycleState.CREATED))
            return (false, "FlattenIntent allowed in CREATED");
        if (!IntentLifecycleValidator.IsFlattenIntentAllowed(IntentLifecycleState.ENTRY_FILLED))
            return (false, "FlattenIntent allowed in ENTRY_FILLED");
        if (!IntentLifecycleValidator.IsFlattenIntentAllowed(IntentLifecycleState.EXIT_PENDING))
            return (false, "FlattenIntent allowed in EXIT_PENDING");
        if (IntentLifecycleValidator.IsFlattenIntentAllowed(IntentLifecycleState.TERMINAL))
            return (false, "FlattenIntent not allowed in TERMINAL");

        return (true, null);
    }

    private static bool ValidTransition(IntentLifecycleState current, IntentLifecycleTransition transition, IntentLifecycleState expectedNew)
    {
        var (valid, newState) = IntentLifecycleValidator.TryGetTransition(current, transition);
        return valid && newState == expectedNew;
    }

    private static bool InvalidTransition(IntentLifecycleState current, IntentLifecycleTransition transition)
    {
        var (valid, _) = IntentLifecycleValidator.TryGetTransition(current, transition);
        return !valid;
    }
}
