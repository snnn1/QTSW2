// Unit tests for high-risk restart scenarios (reconciliation recovery).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test RECONCILIATION_RECOVERY_SCENARIOS
//
// Validates: delayed broker visibility, partial adoption, repeated failed attempts, decision logic.
// Pure unit coverage — no mocks. Tests ReconciliationRecoveryOutcome which mirrors AssembleMismatchObservations.

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ReconciliationRecoveryScenarioTests
{
    public static (bool Pass, string? Error) RunReconciliationRecoveryScenarioTests()
    {
        var (p1, e1) = TestDelayedBrokerVisibility();
        if (!p1) return (false, e1);

        var (p2, e2) = TestPartialAdoptionSuccess();
        if (!p2) return (false, e2);

        var (p3, e3) = TestRepeatedFailedRecoveryAttempts();
        if (!p3) return (false, e3);

        var (p4, e4) = TestPreconditionsSkipRecovery();
        if (!p4) return (false, e4);

        return (true, null);
    }

    /// <summary>Delayed visibility: bootstrap sees brokerWorking=0, later tick sees brokerWorking=2, localWorking=0.
    /// Late adoption runs, adopts 2, localAfter=2 → mismatch resolves without fail-close.</summary>
    private static (bool Pass, string? Error) TestDelayedBrokerVisibility()
    {
        var outcome = ReconciliationRecoveryOutcome.Evaluate(brokerWorking: 2, localBefore: 0, adopted: 2, localAfter: 2);
        if (!outcome.SkipMismatch)
            return (false, $"Delayed visibility: expected SkipMismatch=true when localAfter==brokerWorking, got {outcome.SkipMismatch}");
        if (!outcome.LogSuccess)
            return (false, $"Delayed visibility: expected LogSuccess=true when adopted=2, got {outcome.LogSuccess}");
        if (outcome.LogPartial)
            return (false, $"Delayed visibility: expected LogPartial=false when fully adopted, got {outcome.LogPartial}");
        return (true, null);
    }

    /// <summary>Partial adoption: brokerWorking=2, only one adoptable. Partial logged, mismatch remains, fail-closed path still occurs.</summary>
    private static (bool Pass, string? Error) TestPartialAdoptionSuccess()
    {
        var outcome = ReconciliationRecoveryOutcome.Evaluate(brokerWorking: 2, localBefore: 0, adopted: 1, localAfter: 1);
        if (outcome.SkipMismatch)
            return (false, $"Partial adoption: expected SkipMismatch=false when localAfter!=brokerWorking, got {outcome.SkipMismatch}");
        if (!outcome.LogPartial)
            return (false, $"Partial adoption: expected LogPartial=true when adopted>0 but still mismatched, got {outcome.LogPartial}");
        if (!outcome.LogSuccess)
            return (false, $"Partial adoption: expected LogSuccess=true when adopted=1, got {outcome.LogSuccess}");
        return (true, null);
    }

    /// <summary>Repeated failed recovery: persistent mismatch across ticks. adopted=0 each time. No success, no partial. Idempotent.</summary>
    private static (bool Pass, string? Error) TestRepeatedFailedRecoveryAttempts()
    {
        var outcome = ReconciliationRecoveryOutcome.Evaluate(brokerWorking: 2, localBefore: 0, adopted: 0, localAfter: 0);
        if (outcome.SkipMismatch)
            return (false, $"Repeated failed: expected SkipMismatch=false when localAfter!=brokerWorking, got {outcome.SkipMismatch}");
        if (outcome.LogSuccess)
            return (false, $"Repeated failed: expected LogSuccess=false when adopted=0, got {outcome.LogSuccess}");
        if (outcome.LogPartial)
            return (false, $"Repeated failed: expected LogPartial=false when adopted=0, got {outcome.LogPartial}");
        return (true, null);
    }

    /// <summary>Preconditions: when localBefore>0, recovery block is not entered; skip.</summary>
    private static (bool Pass, string? Error) TestPreconditionsSkipRecovery()
    {
        var outcome = ReconciliationRecoveryOutcome.Evaluate(brokerWorking: 2, localBefore: 1, adopted: 0, localAfter: 1);
        if (outcome.LogSuccess || outcome.LogPartial)
            return (false, $"Preconditions: when localBefore>0, recovery block skipped; LogSuccess/LogPartial should be false");
        return (true, null);
    }
}
