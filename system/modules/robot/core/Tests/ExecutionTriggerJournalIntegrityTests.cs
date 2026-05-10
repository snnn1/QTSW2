using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ExecutionTriggerJournalIntegrityTests
{
    public static (bool Pass, string? Error) RunExecutionTriggerJournalIntegrityTests()
    {
        if (!RobotEngine.ShouldRunImmediateJournalIntegrityForExecutionTrigger(default))
            return (false, "Default trigger must preserve immediate journal-integrity behavior.");

        if (!RobotEngine.ShouldRunImmediateJournalIntegrityForExecutionTrigger(new MismatchExecutionTriggerDetails { FillDelta = 1 }))
            return (false, "Fill deltas must run immediate journal integrity.");

        if (!RobotEngine.ShouldRunImmediateJournalIntegrityForExecutionTrigger(new MismatchExecutionTriggerDetails { InstrumentPositionQty = 1 }))
            return (false, "Position quantity evidence must run immediate journal integrity.");

        if (!RobotEngine.ShouldRunImmediateJournalIntegrityForExecutionTrigger(new MismatchExecutionTriggerDetails { EntryToProtectivesTransition = true }))
            return (false, "Entry-to-protectives transitions must run immediate journal integrity.");

        if (!RobotEngine.ShouldRunImmediateJournalIntegrityForExecutionTrigger(new MismatchExecutionTriggerDetails { WorkingOrderSubmitTransition = true }))
            return (false, "Working-order submit transitions must run immediate journal integrity.");

        var orderStateOnly = new MismatchExecutionTriggerDetails
        {
            IntentId = "entry-intent",
            SuppressHardJournalIntegrityActions = false
        };
        if (RobotEngine.ShouldRunImmediateJournalIntegrityForExecutionTrigger(orderStateOnly))
            return (false, "Order-state-only triggers should wake mismatch audit without immediate account snapshot.");

        return (true, null);
    }
}
