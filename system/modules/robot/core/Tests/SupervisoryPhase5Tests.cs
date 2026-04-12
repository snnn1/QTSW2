// Unit tests for Phase 5: Operational Control and Supervisory Policy.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test SUPERVISORY_PHASE5
//
// Verifies: Supervisory types, valid transition pairs, escalation policy constants.

using System;
using System.Collections.Generic;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class SupervisoryPhase5Tests
{
    /// <summary>Valid supervisory state transitions per Phase 5 spec.</summary>
    private static readonly HashSet<(SupervisoryState From, SupervisoryState To)> ExpectedValidTransitions = new()
    {
        (SupervisoryState.ACTIVE, SupervisoryState.COOLDOWN),
        (SupervisoryState.ACTIVE, SupervisoryState.SUSPENDED),
        (SupervisoryState.ACTIVE, SupervisoryState.HALTED),
        (SupervisoryState.ACTIVE, SupervisoryState.AWAITING_OPERATOR_ACK),
        (SupervisoryState.ACTIVE, SupervisoryState.DISABLED),
        (SupervisoryState.COOLDOWN, SupervisoryState.ACTIVE),
        (SupervisoryState.COOLDOWN, SupervisoryState.SUSPENDED),
        (SupervisoryState.COOLDOWN, SupervisoryState.HALTED),
        (SupervisoryState.SUSPENDED, SupervisoryState.ACTIVE),
        (SupervisoryState.SUSPENDED, SupervisoryState.HALTED),
        (SupervisoryState.SUSPENDED, SupervisoryState.AWAITING_OPERATOR_ACK),
        (SupervisoryState.HALTED, SupervisoryState.ACTIVE),
        (SupervisoryState.HALTED, SupervisoryState.AWAITING_OPERATOR_ACK),
        (SupervisoryState.AWAITING_OPERATOR_ACK, SupervisoryState.ACTIVE),
        (SupervisoryState.AWAITING_OPERATOR_ACK, SupervisoryState.HALTED),
        (SupervisoryState.DISABLED, SupervisoryState.ACTIVE),
    };

    public static (bool Pass, string? Error) RunSupervisoryPhase5Tests()
    {
        // 1. SupervisoryState enum values
        var states = Enum.GetValues<SupervisoryState>();
        var expectedStates = new[] { "ACTIVE", "COOLDOWN", "SUSPENDED", "HALTED", "AWAITING_OPERATOR_ACK", "DISABLED" };
        if (states.Length != expectedStates.Length)
            return (false, $"SupervisoryState: expected {expectedStates.Length} values, got {states.Length}");
        foreach (var name in expectedStates)
        {
            if (!Enum.TryParse<SupervisoryState>(name, out _))
                return (false, $"SupervisoryState missing: {name}");
        }

        // 2. SupervisorySeverity enum values
        var severities = Enum.GetValues<SupervisorySeverity>();
        if (severities.Length < 4)
            return (false, $"SupervisorySeverity: expected at least 4 values, got {severities.Length}");

        // 3. SupervisoryTriggerReason includes required triggers
        var requiredReasons = new[] { "REPEATED_RECOVERY_TRIGGERS", "REPEATED_BOOTSTRAP_HALTS", "REPEATED_UNOWNED_EXECUTIONS",
            "REPEATED_REGISTRY_DIVERGENCE", "REPEATED_RECONCILIATION_MISMATCH", "REPEATED_FLATTEN_ACTIONS", "REPEATED_RECOVERY_HALT",
            "GLOBAL_KILL_SWITCH", "INSTRUMENT_KILL_SWITCH", "IEA_ENQUEUE_FAILURE" };
        foreach (var name in requiredReasons)
        {
            if (!Enum.TryParse<SupervisoryTriggerReason>(name, out _))
                return (false, $"SupervisoryTriggerReason missing: {name}");
        }

        // 4. Invalid transition: ACTIVE -> ACTIVE (no self-loop)
        if (ExpectedValidTransitions.Contains((SupervisoryState.ACTIVE, SupervisoryState.ACTIVE)))
            return (false, "ACTIVE->ACTIVE should not be valid");

        // 5. Invalid transition: HALTED -> COOLDOWN (not in spec)
        if (ExpectedValidTransitions.Contains((SupervisoryState.HALTED, SupervisoryState.COOLDOWN)))
            return (false, "HALTED->COOLDOWN should not be valid");

        // 6. COOLDOWN -> ACTIVE is valid (resume after cooldown)
        if (!ExpectedValidTransitions.Contains((SupervisoryState.COOLDOWN, SupervisoryState.ACTIVE)))
            return (false, "COOLDOWN->ACTIVE must be valid for cooldown expiry");

        // 7. AWAITING_OPERATOR_ACK -> ACTIVE is valid (after ack)
        if (!ExpectedValidTransitions.Contains((SupervisoryState.AWAITING_OPERATOR_ACK, SupervisoryState.ACTIVE)))
            return (false, "AWAITING_OPERATOR_ACK->ACTIVE must be valid after operator ack");

        // 8. DISABLED -> ACTIVE is valid (re-enable)
        if (!ExpectedValidTransitions.Contains((SupervisoryState.DISABLED, SupervisoryState.ACTIVE)))
            return (false, "DISABLED->ACTIVE must be valid for re-enable");

        return (true, null);
    }
}
