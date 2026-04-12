// Single-flight adoption scan gate invariants (see AdoptionScanSingleFlightGate.cs, InstrumentExecutionAuthority.NT.cs).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test IEA_ADOPTION_GATE

using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class IeaAdoptionScanSingleFlightGateTests
{
    public static (bool Pass, string? Error) RunIeaAdoptionScanGateTests()
    {
        var g = new AdoptionScanSingleFlightGate();
        if (g.State != AdoptionScanGateState.Idle)
            return (false, "New gate must be Idle");

        if (g.TryReserveQueuedSlot() != AdoptionScanEnqueueAttemptOutcome.ReservedQueued)
            return (false, "First reserve should succeed");
        if (g.State != AdoptionScanGateState.Queued)
            return (false, "State should be Queued after reserve");

        if (g.TryReserveQueuedSlot() != AdoptionScanEnqueueAttemptOutcome.AlreadyQueued)
            return (false, "Second reserve should be AlreadyQueued");
        if (g.TryBeginInlineRun())
            return (false, "Inline run must fail while Queued");

        if (!g.TryBeginQueuedRun())
            return (false, "TryBeginQueuedRun should succeed from Queued");
        if (g.State != AdoptionScanGateState.Running)
            return (false, "State should be Running");

        if (g.TryReserveQueuedSlot() != AdoptionScanEnqueueAttemptOutcome.AlreadyRunning)
            return (false, "Reserve while Running should be AlreadyRunning");

        g.EndRun();
        if (g.State != AdoptionScanGateState.Idle)
            return (false, "EndRun should return to Idle");

        if (!g.TryBeginInlineRun())
            return (false, "Inline run should succeed from Idle");
        if (g.TryReserveQueuedSlot() != AdoptionScanEnqueueAttemptOutcome.AlreadyRunning)
            return (false, "Reserve while Running (inline) should be AlreadyRunning");
        g.EndRun();

        var g2 = new AdoptionScanSingleFlightGate();
        _ = g2.TryReserveQueuedSlot();
        g2.AbortQueuedReservation();
        if (g2.State != AdoptionScanGateState.Idle)
            return (false, "AbortQueuedReservation should reset Queued → Idle");

        if (g2.TryReserveQueuedSlot() != AdoptionScanEnqueueAttemptOutcome.ReservedQueued)
            return (false, "After abort, reserve should work again");

        return (true, null);
    }
}
