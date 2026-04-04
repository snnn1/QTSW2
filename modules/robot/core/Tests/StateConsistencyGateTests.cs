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
            runInstrumentGateReconciliation: (i, _, _) => new GateReconciliationResult
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
            runInstrumentGateReconciliation: (_, _, _) => new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
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
            runInstrumentGateReconciliation: (_, _, _) => new GateReconciliationResult { RunnerInvoked = true },
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
            runInstrumentGateReconciliation: (_, _, _) => new GateReconciliationResult { RunnerInvoked = true },
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

        // 9) PERSISTENT_MISMATCH + release_ready: after stability window, gate releases (no stuck-forever)
        var coord9 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) => new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
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
        coord9.SetStableWindowForTest(80);
        coord9.AdvanceStateConsistencyGateForTest("GC", snap, tLate);
        st = coord9.GetStateForTest("GC");
        if (st!.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
            return (false, $"9a: expected StablePendingRelease after first advance, got {st.GateLifecyclePhase}");
        if (!st.Blocked) return (false, "9a: still blocked until window elapses");
        coord9.AdvanceStateConsistencyGateForTest("GC", snap, tLate.AddMilliseconds(200));
        st = coord9.GetStateForTest("GC");
        if (st!.Blocked || st.EscalationState != MismatchEscalationState.NONE || st.GateLifecyclePhase != GateLifecyclePhase.None)
            return (false, "9b: should release from persistent after stable window");

        // 10) PERSISTENT_MISMATCH + !release_ready: never releases, stays blocked
        var coord10 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) => new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _) => NotReady("HG", "not_clean"),
            stateConsistencyStableWindowMs: 50);
        coord10.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "HG",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        var tHg = t0.AddMilliseconds(MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS + 200);
        coord10.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "HG",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = tHg
        });
        coord10.SetStableWindowForTest(50);
        for (var i = 0; i < 5; i++)
            coord10.AdvanceStateConsistencyGateForTest("HG", snap, tHg.AddMilliseconds(100 * i));
        st = coord10.GetStateForTest("HG");
        if (!st!.Blocked || st.EscalationState != MismatchEscalationState.PERSISTENT_MISMATCH)
            return (false, "10: must stay blocked on persistent when not release-ready");

        // 11) PERSISTENT_MISMATCH + release_ready but stableMs < window: no release
        var coord11 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) => new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _) => Ready("SI"),
            stateConsistencyStableWindowMs: 200);
        coord11.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "SI",
            MismatchType = MismatchType.JOURNAL_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        var tSi = t0.AddMilliseconds(MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS + 100);
        coord11.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "SI",
            MismatchType = MismatchType.JOURNAL_AHEAD,
            Present = true,
            ObservedUtc = tSi
        });
        coord11.SetStableWindowForTest(200);
        coord11.AdvanceStateConsistencyGateForTest("SI", snap, tSi);
        coord11.AdvanceStateConsistencyGateForTest("SI", snap, tSi.AddMilliseconds(50));
        st = coord11.GetStateForTest("SI");
        if (!st!.Blocked || st.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
            return (false, "11: must remain blocked inside stability window");

        // 12) DETECTED → StablePendingRelease → Persistent before window → later release after full window
        var coord12 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) => new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _) => Ready("MZ"),
            stateConsistencyStableWindowMs: 100);
        var tMz0 = t0.AddMinutes(30);
        coord12.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "MZ",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = tMz0
        });
        coord12.SetStableWindowForTest(100);
        coord12.AdvanceStateConsistencyGateForTest("MZ", snap, tMz0);
        st = coord12.GetStateForTest("MZ");
        if (st!.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
            return (false, $"12a: expected StablePendingRelease, got {st.GateLifecyclePhase}");
        var tMzPersist = tMz0.AddMilliseconds(MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS + 50);
        coord12.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "MZ",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = tMzPersist
        });
        if (coord12.GetStateForTest("MZ")?.EscalationState != MismatchEscalationState.PERSISTENT_MISMATCH)
            return (false, "12b: expected escalation to persistent");
        coord12.AdvanceStateConsistencyGateForTest("MZ", snap, tMzPersist);
        st = coord12.GetStateForTest("MZ");
        if (st!.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
            return (false, $"12c: re-enter StablePendingRelease from persistent, got {st.GateLifecyclePhase}");
        coord12.AdvanceStateConsistencyGateForTest("MZ", snap, tMzPersist.AddMilliseconds(150));
        st = coord12.GetStateForTest("MZ");
        if (st!.Blocked || st.GateLifecyclePhase != GateLifecyclePhase.None)
            return (false, "12d: should release after persistence + stability");

        // Stall / fingerprint helpers for 13+
        var snapStall = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        MismatchObservation StallObs(string inst, int bw, int lw, DateTimeOffset u) => new()
        {
            Instrument = inst,
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = u,
            BrokerQty = 0,
            LocalQty = 0,
            BrokerWorkingOrderCount = bw,
            LocalWorkingOrderCount = lw
        };

        // 13) post_alignment_stall + unchanged fingerprint: repeated RESULT bounded (identical-skip suppression)
        var st13 = StallObs("ST1", 1, 0, t0);
        var fp13 = GateProgressEvaluator.BuildExternalFingerprint("ST1", snapStall, st13);
        var coord13 = new MismatchEscalationCoordinator(
            getSnapshot: () => snapStall,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
                new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _) => Ready("ST1"),
            stateConsistencyStableWindowMs: 50);
        coord13.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST1",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        coord13.SetStableWindowForTest(50);
        coord13.SetForcedConvergenceStallForTest("ST1", true, fp13);
        coord13.ResetTestGateTelemetryCounters();
        for (var i = 0; i < 10; i++)
            coord13.AdvanceStateConsistencyGateForTest("ST1", snapStall, t0.AddMilliseconds(i * 35_000), st13);
        if (coord13.TestGateResultEmitCount > 2)
            return (false, $"13: at most 2 RESULT emits expected, got {coord13.TestGateResultEmitCount}");
        if (coord13.TestGateResultSuppressCount < 8)
            return (false, $"13: expected heavy suppression, emit={coord13.TestGateResultEmitCount} suppress={coord13.TestGateResultSuppressCount}");

        // 14) Stable release completes when expensive work is skipped (post_alignment_stall) and window elapses
        var st14 = StallObs("ST2", 0, 0, t0);
        var fp14 = GateProgressEvaluator.BuildExternalFingerprint("ST2", snapStall, st14);
        var coord14 = new MismatchEscalationCoordinator(
            getSnapshot: () => snapStall,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
                new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _) => Ready("ST2"),
            stateConsistencyStableWindowMs: 80);
        coord14.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST2",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        coord14.SetStableWindowForTest(80);
        coord14.SetForcedConvergenceStallForTest("ST2", true, fp14);
        coord14.AdvanceStateConsistencyGateForTest("ST2", snapStall, t0, st14);
        st = coord14.GetStateForTest("ST2");
        if (st!.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
            return (false, $"14a: expected StablePendingRelease, got {st.GateLifecyclePhase}");
        coord14.AdvanceStateConsistencyGateForTest("ST2", snapStall, t0.AddMilliseconds(100), st14);
        st = coord14.GetStateForTest("ST2");
        if (st!.Blocked || st.GateLifecyclePhase != GateLifecyclePhase.None || st.EscalationState != MismatchEscalationState.NONE)
            return (false, "14b: should release after stable window with stall-only evaluations");

        // 15) Release-ready false clears stability timer (FirstConsistentUtc)
        var ready15 = true;
        var st15 = StallObs("ST3", 0, 0, t0);
        var fp15 = GateProgressEvaluator.BuildExternalFingerprint("ST3", snapStall, st15);
        var coord15 = new MismatchEscalationCoordinator(
            getSnapshot: () => snapStall,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
                new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _) => ready15 ? Ready("ST3") : NotReady("ST3", "relapse"),
            stateConsistencyStableWindowMs: 200);
        coord15.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST3",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        coord15.SetStableWindowForTest(200);
        coord15.SetForcedConvergenceStallForTest("ST3", true, fp15);
        coord15.AdvanceStateConsistencyGateForTest("ST3", snapStall, t0, st15);
        st = coord15.GetStateForTest("ST3");
        if (st!.FirstConsistentUtc == default)
            return (false, "15a: stability clock should start when entering stable pending");
        ready15 = false;
        coord15.AdvanceStateConsistencyGateForTest("ST3", snapStall, t0.AddMilliseconds(50), st15);
        st = coord15.GetStateForTest("ST3");
        if (st!.FirstConsistentUtc != default)
            return (false, "15b: FirstConsistentUtc should clear when readiness drops");

        // 16) Material fingerprint change resumes RESULT emission after stalled suppression
        var st16a = StallObs("ST4", 1, 0, t0);
        var fp16 = GateProgressEvaluator.BuildExternalFingerprint("ST4", snapStall, st16a);
        var coord16 = new MismatchEscalationCoordinator(
            getSnapshot: () => snapStall,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
                new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _) => Ready("ST4"),
            stateConsistencyStableWindowMs: 50);
        coord16.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST4",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        coord16.SetForcedConvergenceStallForTest("ST4", true, fp16);
        coord16.AdvanceStateConsistencyGateForTest("ST4", snapStall, t0, st16a);
        coord16.AdvanceStateConsistencyGateForTest("ST4", snapStall, t0.AddMilliseconds(35_000), st16a);
        coord16.ResetTestGateTelemetryCounters();
        var st16b = StallObs("ST4", 2, 0, t0.AddMilliseconds(70_000));
        coord16.AdvanceStateConsistencyGateForTest("ST4", snapStall, t0.AddMilliseconds(70_000), st16b);
        if (coord16.TestGateResultEmitCount < 1)
            return (false, "16: external/state change should emit at least one RESULT");

        // 17) Unexplained working + not release-ready: stay blocked (suppression does not clear gate)
        StateConsistencyReleaseReadinessResult MclLike(string inst) => new()
        {
            Instrument = inst,
            ReleaseReady = false,
            IsExplainable = false,
            UnexplainedBrokerWorkingCount = 1,
            Summary = "unexplained_working",
            Contradictions = new List<string> { "unexplained_working" }
        };
        var coord17 = new MismatchEscalationCoordinator(
            getSnapshot: () => snapStall,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
                new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _) => MclLike("ST5"),
            stateConsistencyStableWindowMs: 50);
        coord17.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST5",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        var tPersist17 = t0.AddMilliseconds(MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS + 50);
        coord17.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST5",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = tPersist17
        });
        var st17 = StallObs("ST5", 1, 0, tPersist17);
        for (var i = 0; i < 6; i++)
            coord17.AdvanceStateConsistencyGateForTest("ST5", snapStall, tPersist17.AddMilliseconds(i * 35_000), st17);
        st = coord17.GetStateForTest("ST5");
        if (st == null || !st.Blocked || st.EscalationState != MismatchEscalationState.PERSISTENT_MISMATCH)
            return (false, "17: must remain blocked on persistent mismatch with unexplained working");
        if (st.GateLifecyclePhase == GateLifecyclePhase.None)
            return (false, "17: gate should not release while not release-ready");

        return (true, null);
    }
}
