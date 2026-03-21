// P1.5 state-consistency gate closed-loop tests.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test STATE_CONSISTENCY_GATE

using System;
using System.Collections.Generic;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class StateConsistencyGateTests
{
    public static (bool Pass, string? Error) RunStateConsistencyGateTests()
    {
        var t0 = DateTimeOffset.UtcNow;
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };

        StateConsistencyReleaseReadinessResult Ready(string inst) => new()
        {
            Instrument = inst,
            ReleaseReady = true,
            IsExplainable = true,
            BrokerPositionExplainable = true,
            BrokerWorkingExplainable = true,
            LocalStateCoherent = true,
            PendingAdoptionExists = false,
            Summary = "release_ready"
        };

        StateConsistencyReleaseReadinessResult NotReady(string inst, string why) => new()
        {
            Instrument = inst,
            ReleaseReady = false,
            IsExplainable = false,
            Summary = why,
            Contradictions = new List<string> { why }
        };

        // 1) First mismatch engages immediately + DetectedBlocked
        var coord1 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (i, _) => new GateReconciliationResult
            {
                Instrument = i,
                RunnerInvoked = true,
                OutcomeStatus = ReconciliationOutcomeStatus.Partial
            },
            evaluateReleaseReadiness: (_, _, _) => NotReady("ES", "hold"),
            stateConsistencyStableWindowMs: 500);

        coord1.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ES",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            BrokerQty = 1,
            LocalQty = 0,
            ObservedUtc = t0
        });
        var st = coord1.GetStateForTest("ES");
        if (st == null) return (false, "state missing");
        if (!st.Blocked) return (false, "1: should block on engage");
        if (st.BlockReason != MismatchEscalationPolicy.BLOCK_REASON_STATE_CONSISTENCY_GATE)
            return (false, "1: block reason");
        if (st.GateLifecyclePhase != GateLifecyclePhase.DetectedBlocked)
            return (false, $"1: expected DetectedBlocked, got {st.GateLifecyclePhase}");

        // 2) One ready sample → StablePendingRelease, still blocked
        var coord2 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _) => new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _) => Ready("ES"),
            stateConsistencyStableWindowMs: 100);

        coord2.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ES",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        coord2.SetStableWindowForTest(100);
        coord2.AdvanceStateConsistencyGateForTest("ES", snap, t0);
        st = coord2.GetStateForTest("ES");
        if (st!.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
            return (false, $"2: expected StablePendingRelease, got {st.GateLifecyclePhase}");
        if (!st.Blocked) return (false, "2: still blocked in stable window");

        var tEarly = t0.AddMilliseconds(30);
        coord2.AdvanceStateConsistencyGateForTest("ES", snap, tEarly);
        st = coord2.GetStateForTest("ES");
        if (!st!.Blocked) return (false, "2b: must stay blocked before window elapses");
        if (st.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
            return (false, "2b: phase");

        // 3) Release after continuous stable window
        var tRelease = t0.AddMilliseconds(200);
        coord2.AdvanceStateConsistencyGateForTest("ES", snap, tRelease);
        st = coord2.GetStateForTest("ES");
        if (st!.Blocked) return (false, "3: should unblock after stable window");
        if (st.GateLifecyclePhase != GateLifecyclePhase.None)
            return (false, $"3: gate phase None expected, got {st.GateLifecyclePhase}");
        if (st.EscalationState != MismatchEscalationState.NONE)
            return (false, "3: escalation cleared");

        // 4) Restabilization reset
        var readyToggle = true;
        var coord4 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _) => new GateReconciliationResult { RunnerInvoked = true },
            evaluateReleaseReadiness: (_, _, _) => readyToggle ? Ready("YM") : NotReady("YM", "relapse"),
            stateConsistencyStableWindowMs: 500);

        coord4.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "YM",
            MismatchType = MismatchType.JOURNAL_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        coord4.SetStableWindowForTest(500);
        coord4.AdvanceStateConsistencyGateForTest("YM", snap, t0);
        coord4.AdvanceStateConsistencyGateForTest("YM", snap, t0.AddMilliseconds(50));
        st = coord4.GetStateForTest("YM");
        if (st!.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
            return (false, "4: stable pending");

        readyToggle = false;
        coord4.AdvanceStateConsistencyGateForTest("YM", snap, t0.AddMilliseconds(100));
        st = coord4.GetStateForTest("YM");
        if (!st!.Blocked) return (false, "4: still blocked after reset");
        if (st.GateLifecyclePhase != GateLifecyclePhase.Reconciling)
            return (false, $"4: expected Reconciling after reset, got {st.GateLifecyclePhase}");
        if (st.FirstConsistentUtc != default)
            return (false, "4: stability timer cleared");

        // 5) Null evaluate never releases
        var coord5 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: null,
            evaluateReleaseReadiness: null,
            stateConsistencyStableWindowMs: 50);

        coord5.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "CL",
            MismatchType = MismatchType.POSITION_QTY_MISMATCH,
            Present = true,
            ObservedUtc = t0
        });
        for (int i = 0; i < 5; i++)
            coord5.AdvanceStateConsistencyGateForTest("CL", snap, t0.AddMinutes(i));
        st = coord5.GetStateForTest("CL");
        if (!st!.Blocked) return (false, "5: null callbacks must not release");

        // 6) Instrument scope: NQ untouched
        var coord6 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _) => new GateReconciliationResult { RunnerInvoked = true },
            evaluateReleaseReadiness: (_, _, _) => NotReady("NQ", "nq_clean"),
            stateConsistencyStableWindowMs: 100);

        coord6.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ES",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        if (coord6.IsInstrumentBlockedByMismatch("NQ"))
            return (false, "6: NQ should not be blocked");

        // 7) Pending adoption blocks release (evaluator)
        var inp = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "GC",
            BrokerPositionQty = 0,
            BrokerWorkingCount = 0,
            JournalOpenQty = 0,
            IeaOwnedPlusAdoptedWorking = 0,
            PendingAdoptionCandidateCount = 1,
            SnapshotSufficient = true,
            UseInstrumentExecutionAuthority = true
        };
        var ev = StateConsistencyReleaseEvaluator.Evaluate(inp);
        if (ev.ReleaseReady) return (false, "7: pending adoption must block");

        // 8) Unexplained working blocks release
        inp = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "RTY",
            BrokerPositionQty = 0,
            BrokerWorkingCount = 2,
            JournalOpenQty = 0,
            IeaOwnedPlusAdoptedWorking = 0,
            PendingAdoptionCandidateCount = 0,
            SnapshotSufficient = true,
            UseInstrumentExecutionAuthority = false
        };
        ev = StateConsistencyReleaseEvaluator.Evaluate(inp);
        if (ev.ReleaseReady) return (false, "8: unexplained working");

        // 9) PERSISTENT_MISMATCH: release invariants may pass but auto-release is disabled
        var coord9 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _) => new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _) => Ready("GC"),
            stateConsistencyStableWindowMs: 50);
        coord9.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "GC",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        var tLate = t0.AddMilliseconds(MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS + 200);
        coord9.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "GC",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = tLate
        });
        st = coord9.GetStateForTest("GC");
        if (st?.EscalationState != MismatchEscalationState.PERSISTENT_MISMATCH)
            return (false, "9: expected PERSISTENT_MISMATCH");
        coord9.SetStableWindowForTest(50);
        coord9.AdvanceStateConsistencyGateForTest("GC", snap, tLate.AddMilliseconds(100));
        st = coord9.GetStateForTest("GC");
        if (!st!.Blocked || !coord9.IsInstrumentBlockedByMismatch("GC"))
            return (false, "9: persistent mismatch must stay blocked despite release-ready evaluate");

        return (true, null);
    }
}
