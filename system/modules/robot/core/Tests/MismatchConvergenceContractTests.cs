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
        var (e, ee) = WorkingOrderSubmitTriggerArmsAndSuppressesConvergence();
        if (!e) return (false, ee);
        var (f, ef) = WorkingOrderSubmitWindowSuppressesFirstGateWithoutConvergenceArm();
        if (!f) return (false, ef);
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

    private static (bool, string?) WorkingOrderSubmitTriggerArmsAndSuppressesConvergence()
    {
        var coord = CreateCoordinator(false);
        coord.ResetConvergenceTestCountersForTest();
        var t0 = DateTimeOffset.UtcNow;
        coord.NotifyExecutionTrigger("ES", t0, new MismatchExecutionTriggerDetails
        {
            IntentId = "submit-ahead",
            WorkingOrderSubmitTransition = true
        });
        if (!coord.HasActiveConvergenceForTest("ES", t0))
            return (false, "Convergence5: working-order submit should arm convergence");

        coord.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ES",
            MismatchType = MismatchType.WORKING_ORDER_COUNT_CONVERGENCE,
            Present = true,
            ObservedUtc = t0
        });
        if (coord.TestConvergenceFirstEscalationSuppressedCount != 1)
            return (false, "Convergence5: expected working-order convergence suppression count 1");
        var st = coord.GetStateForTest("ES");
        if (st == null || st.EscalationState != MismatchEscalationState.NONE)
            return (false, "Convergence5: escalation should not start for local-working-ahead convergence");
        return (true, null);
    }

    private static (bool, string?) WorkingOrderSubmitWindowSuppressesFirstGateWithoutConvergenceArm()
    {
        var prevStore = FeatureFlags.QuantExecutionControlStoreEnabled;
        var prevWindow = FeatureFlags.PostFillAlignmentWindowMs;
        QuantExecutionControlStore.Clear();
        try
        {
            FeatureFlags.QuantExecutionControlStoreEnabled = true;
            FeatureFlags.PostFillAlignmentWindowMs = 5000;

            var coord = CreateCoordinator(false);
            var t0 = DateTimeOffset.UtcNow;
            QuantExecutionControlStore.NotifyWorkingOrderSubmitTransition("MES", 2, t0);

            coord.ProcessObservationForTest(new MismatchObservation
            {
                Instrument = "MES",
                MismatchType = MismatchType.WORKING_ORDER_COUNT_CONVERGENCE,
                Present = true,
                BrokerWorkingOrderCount = 1,
                LocalWorkingOrderCount = 2,
                ObservedUtc = t0.AddMilliseconds(100)
            });

            var st = coord.GetStateForTest("MES");
            if (st == null || st.EscalationState != MismatchEscalationState.NONE)
                return (false, "Convergence6: QEC working-order window should defer first WOC gate engagement");

            coord.ProcessObservationForTest(new MismatchObservation
            {
                Instrument = "MES",
                MismatchType = MismatchType.WORKING_ORDER_COUNT_CONVERGENCE,
                Present = true,
                BrokerWorkingOrderCount = 1,
                LocalWorkingOrderCount = 2,
                ObservedUtc = t0.AddMilliseconds(FeatureFlags.PostFillAlignmentWindowMs + 500)
            });

            st = coord.GetStateForTest("MES");
            if (st == null || st.EscalationState != MismatchEscalationState.DETECTED)
                return (false, "Convergence6: WOC gate should engage after bounded working-order window expires");

            return (true, null);
        }
        finally
        {
            QuantExecutionControlStore.Clear();
            FeatureFlags.QuantExecutionControlStoreEnabled = prevStore;
            FeatureFlags.PostFillAlignmentWindowMs = prevWindow;
        }
    }
}
