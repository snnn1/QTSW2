using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

/// <summary>Phase 3: convergence window at mismatch escalation boundary.</summary>
public static class MismatchConvergenceContractTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var (a, ea) = TransientSuppressedWhenConvergenceArmedAndProbeClean();
        if (!a) return (false, ea);
        var (b, eb) = CanonicalUnexplainedStillEscalates();
        if (!b) return (false, eb);
        var (c, ec) = SeriousMismatchNeverSuppressed();
        if (!c) return (false, ec);
        var (d, ed) = ExpiryRestoresNormalEscalation();
        if (!d) return (false, ed);
        return (true, null);
    }

    private static MismatchEscalationCoordinator CreateCoordinator(bool probeUnexplained)
    {
        var unexplained = probeUnexplained;
        var snap = new AccountSnapshot
        {
            Positions = new System.Collections.Generic.List<PositionSnapshot>(),
            WorkingOrders = new System.Collections.Generic.List<WorkingOrderSnapshot>()
        };
        return new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            probeCanonicallyUnexplainedExposure: (_, _) => new MismatchConvergenceCanonicalProbeResult
            {
                HasUnexplainedBrokerExposure = unexplained,
                UnexplainedBrokerPositionQty = unexplained ? 1 : 0,
                UnexplainedBrokerWorkingCount = 0
            });
    }

    private static (bool, string?) TransientSuppressedWhenConvergenceArmedAndProbeClean()
    {
        var coord = CreateCoordinator(false);
        coord.ResetConvergenceTestCountersForTest();
        var t0 = DateTimeOffset.UtcNow;
        coord.ArmConvergence("ES", "test_arm", t0);
        if (!coord.HasActiveConvergenceForTest("ES", t0))
            return (false, "Convergence1: expected active convergence");

        coord.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ES",
            MismatchType = MismatchType.ORDER_REGISTRY_MISSING,
            Present = true,
            ObservedUtc = t0,
            Severity = "CRITICAL"
        });

        if (coord.TestConvergenceFirstEscalationSuppressedCount != 1)
            return (false, "Convergence1: expected suppression count 1");
        var st = coord.GetStateForTest("ES");
        if (st == null || st.EscalationState != MismatchEscalationState.NONE)
            return (false, "Convergence1: escalation should not start when suppressed");
        return (true, null);
    }

    private static (bool, string?) CanonicalUnexplainedStillEscalates()
    {
        var coord = CreateCoordinator(true);
        coord.ResetConvergenceTestCountersForTest();
        var t0 = DateTimeOffset.UtcNow;
        coord.ArmConvergence("ES", "test_arm", t0);
        coord.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ES",
            MismatchType = MismatchType.ORDER_REGISTRY_MISSING,
            Present = true,
            ObservedUtc = t0
        });
        if (coord.TestConvergenceFirstEscalationSuppressedCount != 0)
            return (false, "Convergence2: must not suppress when probe reports unexplained exposure");
        var st = coord.GetStateForTest("ES");
        if (st == null || st.EscalationState != MismatchEscalationState.DETECTED || !st.Blocked)
            return (false, "Convergence2: expected DETECTED + blocked");
        return (true, null);
    }

    private static (bool, string?) SeriousMismatchNeverSuppressed()
    {
        var coord = CreateCoordinator(false);
        coord.ResetConvergenceTestCountersForTest();
        var t0 = DateTimeOffset.UtcNow;
        coord.ArmConvergence("ES", "test_arm", t0);
        coord.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ES",
            MismatchType = MismatchType.NET_POSITION_MISMATCH,
            Present = true,
            ObservedUtc = t0
        });
        if (coord.TestConvergenceFirstEscalationSuppressedCount != 0)
            return (false, "Convergence3: must not suppress serious mismatch type");
        var st = coord.GetStateForTest("ES");
        if (st == null || st.EscalationState != MismatchEscalationState.DETECTED)
            return (false, "Convergence3: expected DETECTED");
        return (true, null);
    }

    private static (bool, string?) ExpiryRestoresNormalEscalation()
    {
        var coord = CreateCoordinator(false);
        coord.ResetConvergenceTestCountersForTest();
        var t0 = DateTimeOffset.UtcNow;
        coord.ArmConvergence("ES", "test_arm", t0);
        var tLate = t0.AddMilliseconds(MismatchEscalationPolicy.MISMATCH_CONVERGENCE_WINDOW_MS + 2000);
        if (coord.HasActiveConvergenceForTest("ES", tLate))
            return (false, "Convergence4: convergence should expire");

        coord.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ES",
            MismatchType = MismatchType.ORDER_REGISTRY_MISSING,
            Present = true,
            ObservedUtc = tLate
        });
        if (coord.TestConvergenceFirstEscalationSuppressedCount != 0)
            return (false, "Convergence4: after expiry suppression must not apply");
        var st = coord.GetStateForTest("ES");
        if (st == null || st.EscalationState != MismatchEscalationState.DETECTED)
            return (false, "Convergence4: expected normal escalation after expiry");
        return (true, null);
    }
}
