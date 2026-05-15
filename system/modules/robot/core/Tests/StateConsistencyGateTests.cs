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
        var prevParityFlatten = FeatureFlags.ControlPlaneParityHardFlattenFromTryEnsureJournalIntegrity;
        FeatureFlags.ControlPlaneParityHardFlattenFromTryEnsureJournalIntegrity = true;
        try
        {
        var t0 = DateTimeOffset.UtcNow;
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };

        StateConsistencyReleaseReadinessResult WithMismatchReleaseAuthority(StateConsistencyReleaseReadinessResult r)
        {
            r.SnapshotSufficient = true;
            r.CanonicalReleaseAuthorityAllowed = true;
            r.CanonicalReleaseAuthorityGate = "AuthorityMismatchRelease";
            r.CanonicalReleaseAuthorityFrameId = "test-authority-frame";
            return r;
        }

        StateConsistencyReleaseReadinessResult Ready(string inst) => WithMismatchReleaseAuthority(new StateConsistencyReleaseReadinessResult
        {
            Instrument = inst,
            SnapshotSufficient = true,
            ReleaseReady = true,
            IsExplainable = true,
            BrokerPositionExplainable = true,
            BrokerWorkingExplainable = true,
            LocalStateCoherent = true,
            PendingAdoptionExists = false,
            DiagnosticBrokerPositionQty = 1,
            DiagnosticJournalOpenQty = 1,
            DiagnosticBrokerWorkingCount = 0,
            DiagnosticIeaOwnedPlusAdoptedWorking = 0,
            Summary = "release_ready"
        });

        StateConsistencyReleaseReadinessResult ReadyWithoutCanonicalAuthority(string inst) => new()
        {
            Instrument = inst,
            SnapshotSufficient = true,
            ReleaseReady = true,
            IsExplainable = true,
            BrokerPositionExplainable = true,
            BrokerWorkingExplainable = true,
            LocalStateCoherent = true,
            PendingAdoptionExists = false,
            Summary = "release_ready_no_authority"
        };

        StateConsistencyReleaseReadinessResult ImmediateBrokerFlatReady(string inst) => WithMismatchReleaseAuthority(new StateConsistencyReleaseReadinessResult
        {
            Instrument = inst,
            SnapshotSufficient = true,
            ReleaseReady = true,
            IsExplainable = true,
            BrokerPositionExplainable = true,
            BrokerWorkingExplainable = true,
            LocalStateCoherent = true,
            PendingAdoptionExists = false,
            DiagnosticBrokerPositionQty = 0,
            DiagnosticJournalOpenQty = 0,
            DiagnosticBrokerWorkingCount = 0,
            DiagnosticIeaOwnedPlusAdoptedWorking = 0,
            Summary = "release_ready"
        });

        StateConsistencyReleaseReadinessResult ImmediateBrokerFlatReadyUnknownIea(string inst) => WithMismatchReleaseAuthority(new StateConsistencyReleaseReadinessResult
        {
            Instrument = inst,
            SnapshotSufficient = true,
            ReleaseReady = true,
            IsExplainable = true,
            BrokerPositionExplainable = true,
            BrokerWorkingExplainable = true,
            LocalStateCoherent = true,
            PendingAdoptionExists = false,
            DiagnosticBrokerPositionQty = 0,
            DiagnosticJournalOpenQty = 0,
            DiagnosticBrokerWorkingCount = 0,
            DiagnosticIeaOwnedPlusAdoptedWorking = -1,
            Summary = "release_ready"
        });

        StateConsistencyReleaseReadinessResult ImmediateBrokerFlatReadyStaleDiagnosticWorking(string inst) => WithMismatchReleaseAuthority(new StateConsistencyReleaseReadinessResult
        {
            Instrument = inst,
            SnapshotSufficient = true,
            ReleaseReady = true,
            IsExplainable = true,
            BrokerPositionExplainable = true,
            BrokerWorkingExplainable = true,
            LocalStateCoherent = true,
            PendingAdoptionExists = false,
            DiagnosticBrokerPositionQty = 0,
            DiagnosticJournalOpenQty = 0,
            DiagnosticBrokerWorkingCount = 2,
            DiagnosticIeaOwnedPlusAdoptedWorking = 0,
            Summary = "release_ready"
        });

        StateConsistencyReleaseReadinessResult NotReady(string inst, string why) => new()
        {
            Instrument = inst,
            ReleaseReady = false,
            IsExplainable = false,
            Summary = why,
            Contradictions = new List<string> { why }
        };

        StateConsistencyReleaseReadinessResult SoftTransitionFlat(string inst) => new()
        {
            Instrument = inst,
            SnapshotSufficient = true,
            ReleaseReady = false,
            IsExplainable = true,
            BrokerPositionExplainable = true,
            BrokerWorkingExplainable = true,
            LocalStateCoherent = true,
            PendingAdoptionExists = true,
            DiagnosticBrokerPositionQty = 0,
            DiagnosticJournalOpenQty = 0,
            DiagnosticBrokerWorkingCount = 0,
            DiagnosticIeaOwnedPlusAdoptedWorking = 0,
            DiagnosticPendingAdoptionCandidateCount = 1,
            DiagnosticAdoptDecisionCount = 1,
            Summary = "pending_adoption_adopt_decision;blocker_adopt:BrokerVisibleAdoptableExposure:test",
            Contradictions = new List<string> { "pending_adoption_adopt_decision", "blocker_adopt:BrokerVisibleAdoptableExposure:test" },
            ResolvedBlockers = new List<(ReconciliationBlocker Blocker, ReconciliationDecision Decision)>
            {
                (new ReconciliationBlocker
                {
                    ReasonCode = ReconciliationBlockerReasonCode.BrokerVisibleAdoptableExposure,
                    IntentId = "soft-transition-test",
                    IsTerminal = false
                }, ReconciliationDecision.ADOPT)
            }
        };

        StateConsistencyReleaseReadinessResult SoftTransitionCoherentNonFlat(string inst) => new()
        {
            Instrument = inst,
            SnapshotSufficient = true,
            ReleaseReady = false,
            IsExplainable = true,
            BrokerPositionExplainable = true,
            BrokerWorkingExplainable = true,
            LocalStateCoherent = true,
            PendingAdoptionExists = true,
            DiagnosticBrokerPositionQty = 2,
            DiagnosticJournalOpenQty = 2,
            DiagnosticBrokerWorkingCount = 0,
            DiagnosticIeaOwnedPlusAdoptedWorking = 0,
            DiagnosticPendingAdoptionCandidateCount = 1,
            DiagnosticAdoptDecisionCount = 1,
            UnexplainedBrokerPositionQty = 0,
            UnexplainedBrokerWorkingCount = 0,
            Summary = "pending_adoption_adopt_decision;blocker_adopt:BrokerVisibleAdoptableExposure:test-nonflat",
            Contradictions = new List<string> { "pending_adoption_adopt_decision", "blocker_adopt:BrokerVisibleAdoptableExposure:test-nonflat" },
            ResolvedBlockers = new List<(ReconciliationBlocker Blocker, ReconciliationDecision Decision)>
            {
                (new ReconciliationBlocker
                {
                    ReasonCode = ReconciliationBlockerReasonCode.BrokerVisibleAdoptableExposure,
                    IntentId = "soft-transition-nonflat-test",
                    IsTerminal = false
                }, ReconciliationDecision.ADOPT)
            }
        };

        StateConsistencyReleaseReadinessResult ResidualCleanupReady(string inst) => WithMismatchReleaseAuthority(new StateConsistencyReleaseReadinessResult
        {
            Instrument = inst,
            SnapshotSufficient = true,
            ReleaseReady = true,
            ResidualCleanupOnly = true,
            ResidualCleanupClass = ResidualCleanupMismatchClass.MISMATCH_RESIDUAL_JOURNAL_AND_ADOPTION_RETIREMENT.ToString(),
            IsExplainable = true,
            BrokerPositionExplainable = true,
            BrokerWorkingExplainable = true,
            LocalStateCoherent = true,
            PendingAdoptionExists = true,
            DiagnosticBrokerPositionQty = 0,
            DiagnosticJournalOpenQty = 2,
            DiagnosticBrokerWorkingCount = 0,
            DiagnosticIeaOwnedPlusAdoptedWorking = 0,
            DiagnosticPendingAdoptionCandidateCount = 2,
            Summary = "release_ready_residual_cleanup:" + ResidualCleanupMismatchClass.MISMATCH_RESIDUAL_JOURNAL_AND_ADOPTION_RETIREMENT
        });

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
            evaluateReleaseReadiness: (_, _, _, _) => NotReady("ES", "hold"),
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
            evaluateReleaseReadiness: (_, _, _, _) => Ready("ES"),
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
            evaluateReleaseReadiness: (_, _, _, _) => readyToggle ? Ready("YM") : NotReady("YM", "relapse"),
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
            MismatchType = MismatchType.NET_POSITION_MISMATCH,
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
            evaluateReleaseReadiness: (_, _, _, _) => NotReady("NQ", "nq_clean"),
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

        // 7) Raw pending-adoption count without classifier evidence is observability only.
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
        if (!ev.ReleaseReady) return (false, "7: legacy pending adoption residue without blockers must not veto release");
        if (!ev.LegacyClassifierGap) return (false, "7: legacy classifier gap should be surfaced diagnostically");

        // 7b) Classifier-backed adoption still blocks release.
        inp = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "GC",
            BrokerPositionQty = 0,
            BrokerWorkingCount = 1,
            JournalOpenQty = 0,
            IeaOwnedPlusAdoptedWorking = 0,
            PendingAdoptionCandidateCount = 1,
            SnapshotSufficient = true,
            UseInstrumentExecutionAuthority = true,
            ReconciliationBlockers = new List<ReconciliationBlocker>
            {
                new()
                {
                    ReasonCode = ReconciliationBlockerReasonCode.BrokerVisibleAdoptableExposure,
                    IntentId = "adopt-blocker-test",
                    BlocksRelease = true,
                    ShouldAdopt = true,
                    Disposition = ReleaseAdoptionDisposition.AdoptableAndRetryable,
                    IsTerminal = false
                }
            }
        };
        ev = StateConsistencyReleaseEvaluator.Evaluate(inp);
        if (ev.ReleaseReady) return (false, "7b: classifier-backed pending adoption must block");

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

        // 8b) Broker-flat cleanup lag should classify as residual cleanup, not active mismatch.
        inp = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "MNG",
            BrokerPositionQty = 0,
            BrokerWorkingCount = 0,
            PendingExecutionWorkload = 0,
            JournalOpenQty = 2,
            IeaOwnedPlusAdoptedWorking = 0,
            PendingAdoptionCandidateCount = 2,
            SnapshotSufficient = true,
            UseInstrumentExecutionAuthority = true,
            ReconciliationBlockers = new List<ReconciliationBlocker>
            {
                new()
                {
                    ReasonCode = ReconciliationBlockerReasonCode.JournalOnlyBrokerFlat,
                    IntentId = "journal-row",
                    Disposition = ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane,
                    BlocksRelease = true,
                    ShouldAdopt = false
                },
                new()
                {
                    ReasonCode = ReconciliationBlockerReasonCode.BrokerVisibleAdoptableExposure,
                    IntentId = "adoption-row",
                    Disposition = ReleaseAdoptionDisposition.AdoptableAndRetryable,
                    BlocksRelease = true,
                    ShouldAdopt = false
                }
            }
        };
        ev = StateConsistencyReleaseEvaluator.Evaluate(inp);
        if (!ev.ReleaseReady || !ev.ResidualCleanupOnly)
            return (false, "8b: residual cleanup should release");
        if (ev.ResidualCleanupClass != ResidualCleanupMismatchClass.MISMATCH_RESIDUAL_JOURNAL_AND_ADOPTION_RETIREMENT.ToString())
            return (false, "8b: residual cleanup class");

        // 8c) Broker-net flat is not clean-flat while ownership still has gross stream exposure.
        inp = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "MNG",
            BrokerPositionQty = 0,
            BrokerWorkingCount = 0,
            PendingExecutionWorkload = 0,
            JournalOpenQty = 2,
            OwnershipSnapshotAvailable = true,
            OwnershipGrossOpenQty = 2,
            OwnershipSignedNetQty = 0,
            OwnershipActiveSlotCount = 1,
            OwnershipOrphanSlotCount = 0,
            IeaOwnedPlusAdoptedWorking = 0,
            PendingAdoptionCandidateCount = 1,
            SnapshotSufficient = true,
            UseInstrumentExecutionAuthority = true,
            ReconciliationBlockers = new List<ReconciliationBlocker>
            {
                new()
                {
                    ReasonCode = ReconciliationBlockerReasonCode.JournalOnlyBrokerFlat,
                    IntentId = "active-gross-row",
                    Disposition = ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane,
                    BlocksRelease = true,
                    ShouldAdopt = false
                }
            }
        };
        ev = StateConsistencyReleaseEvaluator.Evaluate(inp);
        if (ev.ReleaseReady || ev.ResidualCleanupOnly)
            return (false, "8c: broker-net flat with ownership gross open must not release as cleanup");
        if (!ev.Contradictions.Any(c => c.StartsWith("ownership_gross_open_on_broker_net_flat", StringComparison.Ordinal)))
            return (false, "8c: ownership gross-open contradiction missing");

        // 8d) Coherent active exposure may release the mismatch gate; this does not declare the stream flat.
        inp = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "M2K",
            BrokerPositionQty = 2,
            BrokerWorkingCount = 2,
            PendingExecutionWorkload = 0,
            JournalOpenQty = 2,
            OwnershipSnapshotAvailable = true,
            OwnershipGrossOpenQty = 2,
            OwnershipSignedNetQty = 2,
            OwnershipActiveSlotCount = 1,
            OwnershipOrphanSlotCount = 0,
            IeaOwnedPlusAdoptedWorking = 2,
            PendingAdoptionCandidateCount = 0,
            SnapshotSufficient = true,
            UseInstrumentExecutionAuthority = true,
            ReconciliationBlockers = Array.Empty<ReconciliationBlocker>()
        };
        ev = StateConsistencyReleaseEvaluator.Evaluate(inp);
        if (!ev.ReleaseReady || !ev.IsExplainable)
            return (false, "8d: coherent active protected exposure should release the mismatch gate");
        if (ev.ResidualCleanupOnly)
            return (false, "8d: active exposure must not be classified as residual cleanup");

        // 8e) Coherent exposure is not release-ready while IEA still has pending execution work.
        inp = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "M2K",
            BrokerPositionQty = 2,
            BrokerWorkingCount = 2,
            PendingExecutionWorkload = 2,
            JournalOpenQty = 2,
            OwnershipSnapshotAvailable = true,
            OwnershipGrossOpenQty = 2,
            OwnershipSignedNetQty = 2,
            OwnershipActiveSlotCount = 1,
            OwnershipOrphanSlotCount = 0,
            IeaOwnedPlusAdoptedWorking = 2,
            PendingAdoptionCandidateCount = 0,
            SnapshotSufficient = true,
            UseInstrumentExecutionAuthority = true,
            ReconciliationBlockers = Array.Empty<ReconciliationBlocker>()
        };
        ev = StateConsistencyReleaseEvaluator.Evaluate(inp);
        if (ev.ReleaseReady || ev.IsExplainable)
            return (false, "8e: pending IEA execution workload must block mismatch release");
        if (!ev.Contradictions.Any(c => c == "pending_execution_workload_2"))
            return (false, "8e: pending execution workload contradiction missing");

        var suppression = new ReleaseReconciliationRedundancySuppression();
        var cachedReadyInput = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "M2K",
            BrokerPositionQty = 0,
            BrokerWorkingCount = 0,
            JournalOpenQty = 0,
            PendingExecutionWorkload = 0,
            IeaOwnedPlusAdoptedWorking = 0,
            SnapshotSufficient = true,
            UseInstrumentExecutionAuthority = true
        };
        suppression.RecordReleaseFullEvaluation("M2K", cachedReadyInput, StateConsistencyReleaseEvaluator.Evaluate(cachedReadyInput), t0, suppression.ExecutionActivityGeneration);
        var pendingProbe = new ReleaseReadinessSuppressionProbe
        {
            BrokerPositionQty = 0,
            BrokerWorkingCount = 0,
            JournalOpenQty = 0,
            PendingExecutionWorkload = 1,
            IeaTrustedWorkingCount = 0,
            UseIea = true
        };
        if (suppression.TryGetCachedReleaseReadiness("M2K", in pendingProbe, suppression.ExecutionActivityGeneration, t0.AddMilliseconds(10), out _, out var pendingCacheReason))
            return (false, "8f: pending execution workload must force full release-readiness evaluation, not cached release-ready");
        if (pendingCacheReason != "forced_eval_non_idle_pending")
            return (false, $"8f: expected forced_eval_non_idle_pending, got {pendingCacheReason}");

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
            evaluateReleaseReadiness: (_, _, _, _) => Ready("GC"),
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

        // 9c) ReleaseReady is not enough; mismatch release mutation requires an explicit UEA authority token.
        var coord9c = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) => new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _, _) => ReadyWithoutCanonicalAuthority("GCA"),
            stateConsistencyStableWindowMs: 50);
        coord9c.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "GCA",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        var tGca = t0.AddMilliseconds(MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS + 200);
        coord9c.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "GCA",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = tGca
        });
        coord9c.SetStableWindowForTest(50);
        coord9c.AdvanceStateConsistencyGateForTest("GCA", snap, tGca);
        coord9c.AdvanceStateConsistencyGateForTest("GCA", snap, tGca.AddMilliseconds(100));
        st = coord9c.GetStateForTest("GCA");
        if (st == null || !st.Blocked || st.EscalationState == MismatchEscalationState.NONE)
            return (false, "9c: release-ready without canonical authority token must remain blocked");

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
            evaluateReleaseReadiness: (_, _, _, _) => NotReady("HG", "not_clean"),
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

        // 10b) Residual cleanup should release immediately once broker-flat/no-work is already coherent.
        var coord10b = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) => new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _, _) => ResidualCleanupReady("MNG"),
            getPendingExecutionWorkloadForInstrument: _ => 0,
            stateConsistencyStableWindowMs: 500);
        coord10b.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "MNG",
            MismatchType = MismatchType.JOURNAL_AHEAD,
            Present = true,
            BrokerQty = 0,
            LocalQty = 2,
            BrokerWorkingOrderCount = 0,
            LocalWorkingOrderCount = 0,
            ObservedUtc = t0
        });
        coord10b.AdvanceStateConsistencyGateForTest("MNG", snap, t0);
        st = coord10b.GetStateForTest("MNG");
        if (st!.Blocked || st.GateLifecyclePhase != GateLifecyclePhase.None)
            return (false, "10b: residual cleanup should fast-release");

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
            evaluateReleaseReadiness: (_, _, _, _) => Ready("SI"),
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
            evaluateReleaseReadiness: (_, _, _, _) => Ready("MZ"),
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
            evaluateReleaseReadiness: (_, _, _, _) => Ready("ST1"),
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
            coord13.AdvanceStateConsistencyGateForTest("ST1", snapStall, t0.AddMilliseconds(i * 1_000), st13);
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
            evaluateReleaseReadiness: (_, _, _, _) => Ready("ST2"),
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
            evaluateReleaseReadiness: (_, _, _, _) => ready15 ? Ready("ST3") : NotReady("ST3", "relapse"),
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
            evaluateReleaseReadiness: (_, _, _, _) => Ready("ST4"),
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
        coord16.AdvanceStateConsistencyGateForTest("ST4", snapStall, t0.AddMilliseconds(5_000), st16a);
        coord16.ResetTestGateTelemetryCounters();
        var st16b = StallObs("ST4", 2, 0, t0.AddMilliseconds(10_000));
        coord16.AdvanceStateConsistencyGateForTest("ST4", snapStall, t0.AddMilliseconds(10_000), st16b);
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
            evaluateReleaseReadiness: (_, _, _, _) => MclLike("ST5"),
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

        // 18) Broker-flat release-ready state can release promptly without waiting for the full quiet window.
        var coord18 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
                new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _, _) => ImmediateBrokerFlatReady("ST6"),
            stateConsistencyStableWindowMs: 500);
        coord18.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST6",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0,
            NetBrokerQty = 0,
            NetJournalQty = 0
        });
        coord18.AdvanceStateConsistencyGateForTest("ST6", snap, t0, new MismatchObservation
        {
            Instrument = "ST6",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0,
            NetBrokerQty = 0,
            NetJournalQty = 0,
            BrokerWorkingOrderCount = 0,
            LocalWorkingOrderCount = 0
        });
        coord18.AdvanceStateConsistencyGateForTest("ST6", snap, t0.AddMilliseconds(10), new MismatchObservation
        {
            Instrument = "ST6",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0.AddMilliseconds(10),
            NetBrokerQty = 0,
            NetJournalQty = 0,
            BrokerWorkingOrderCount = 0,
            LocalWorkingOrderCount = 0
        });
        st = coord18.GetStateForTest("ST6");
        if (st == null || st.Blocked)
            return (false, "18: broker-flat release-ready state should release promptly");

        // 19) Soft-transition broker-flat cleanup can bypass mismatch authority for lock-time entry stops only.
        var coord19 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
                new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Partial },
            evaluateReleaseReadiness: (_, _, _, _) => SoftTransitionFlat("ST7"),
            stateConsistencyStableWindowMs: 500);
        coord19.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST7",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        if (!coord19.IsSubmitBlockedByMismatch("ST7", "SUBMIT_ENTRY"))
            return (false, "19: generic opening entry should remain mismatch-blocked");
        if (coord19.IsSubmitBlockedByMismatch("ST7", "SUBMIT_ENTRY_STOP"))
            return (false, "19: soft-transition lock entry stop should bypass mismatch block");

        // 19b) Coherent non-flat soft-transition state should also bypass mismatch authority for lock-time entry stops.
        var coord19b = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
                new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Partial },
            evaluateReleaseReadiness: (_, _, _, _) => SoftTransitionCoherentNonFlat("ST7B"),
            stateConsistencyStableWindowMs: 500);
        coord19b.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST7B",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            BrokerQty = 2,
            LocalQty = 2,
            ObservedUtc = t0
        });
        if (!coord19b.IsSubmitBlockedByMismatch("ST7B", "SUBMIT_ENTRY"))
            return (false, "19b: generic non-flat opening entry should remain mismatch-blocked");
        if (coord19b.IsSubmitBlockedByMismatch("ST7B", "SUBMIT_ENTRY_STOP"))
            return (false, "19b: coherent non-flat lock entry stop should bypass mismatch block");

        // 20) Immediate broker-flat release should not require a second evaluation tick.
        var coord20 = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
                new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _, _) => ImmediateBrokerFlatReady("ST8"),
            stateConsistencyStableWindowMs: 500);
        coord20.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST8",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        coord20.AdvanceStateConsistencyGateForTest("ST8", snap, t0, new MismatchObservation
        {
            Instrument = "ST8",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0,
            BrokerWorkingOrderCount = 0,
            LocalWorkingOrderCount = 0,
            NetBrokerQty = 0,
            NetJournalQty = 0
        });
        st = coord20.GetStateForTest("ST8");
        if (st == null || st.Blocked)
            return (false, "20: immediate broker-flat readiness should release on first stable tick");
        if (st.GateLifecyclePhase != GateLifecyclePhase.None)
            return (false, $"20: expected gate phase None after same-tick broker-flat release, got {st.GateLifecyclePhase}");

        // 20b) Immediate broker-flat release should still fire when the coherent broker-flat state arrives on a later tick
        // even if the quiet fingerprint changes between the first stable sample and the now-clean sample.
        var coord20b = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
                new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _, _) => ImmediateBrokerFlatReady("ST8B"),
            stateConsistencyStableWindowMs: 500);
        coord20b.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST8B",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        coord20b.AdvanceStateConsistencyGateForTest("ST8B", snap, t0, new MismatchObservation
        {
            Instrument = "ST8B",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0,
            BrokerWorkingOrderCount = 1,
            LocalWorkingOrderCount = 0,
            NetBrokerQty = 0,
            NetJournalQty = 0
        });
        st = coord20b.GetStateForTest("ST8B");
        if (st == null || !st.Blocked || st.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
            return (false, "20b: first stable sample with remaining broker working should still be pending release");
        coord20b.AdvanceStateConsistencyGateForTest("ST8B", snap, t0.AddMilliseconds(100), new MismatchObservation
        {
            Instrument = "ST8B",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0.AddMilliseconds(100),
            BrokerWorkingOrderCount = 0,
            LocalWorkingOrderCount = 0,
            NetBrokerQty = 0,
            NetJournalQty = 0
        });
        st = coord20b.GetStateForTest("ST8B");
        if (st == null || st.Blocked)
            return (false, "20c: coherent broker-flat state should release immediately even after fingerprint change");
        if (st.GateLifecyclePhase != GateLifecyclePhase.None)
            return (false, $"20c: expected gate phase None after later broker-flat release, got {st.GateLifecyclePhase}");

        // 20d) Immediate broker-flat release should not be blocked by unknown IEA diagnostics when readiness is already coherent.
        var coord20d = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
                new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _, _) => ImmediateBrokerFlatReadyUnknownIea("ST8C"),
            stateConsistencyStableWindowMs: 500);
        coord20d.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST8C",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        coord20d.AdvanceStateConsistencyGateForTest("ST8C", snap, t0, new MismatchObservation
        {
            Instrument = "ST8C",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0,
            BrokerWorkingOrderCount = 1,
            LocalWorkingOrderCount = 0,
            NetBrokerQty = 0,
            NetJournalQty = 0
        });
        st = coord20d.GetStateForTest("ST8C");
        if (st == null || !st.Blocked || st.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
            return (false, "20d: first pre-clean sample should still be pending release");
        coord20d.AdvanceStateConsistencyGateForTest("ST8C", snap, t0.AddMilliseconds(100), new MismatchObservation
        {
            Instrument = "ST8C",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0.AddMilliseconds(100),
            BrokerWorkingOrderCount = 0,
            LocalWorkingOrderCount = 0,
            NetBrokerQty = 0,
            NetJournalQty = 0
        });
        st = coord20d.GetStateForTest("ST8C");
        if (st == null || st.Blocked)
            return (false, "20e: unknown-but-nonpositive IEA diagnostics should not block immediate broker-flat release");
        if (st.GateLifecyclePhase != GateLifecyclePhase.None)
            return (false, $"20e: expected gate phase None after unknown-IEA broker-flat release, got {st.GateLifecyclePhase}");

        // 20f) Stale broker-working diagnostics inside readiness should not strand an otherwise coherent flat release.
        var coord20f = new MismatchEscalationCoordinator(
            getSnapshot: () => snap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
                new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Success },
            evaluateReleaseReadiness: (_, _, _, _) => ImmediateBrokerFlatReadyStaleDiagnosticWorking("ST8D"),
            stateConsistencyStableWindowMs: 500);
        coord20f.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST8D",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        coord20f.AdvanceStateConsistencyGateForTest("ST8D", snap, t0, new MismatchObservation
        {
            Instrument = "ST8D",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0,
            BrokerWorkingOrderCount = 0,
            LocalWorkingOrderCount = 0,
            NetBrokerQty = 0,
            NetJournalQty = 0
        });
        st = coord20f.GetStateForTest("ST8D");
        if (st == null || st.Blocked)
            return (false, "20f: stale readiness broker-working diagnostics should not block immediate broker-flat release");
        if (st.GateLifecyclePhase != GateLifecyclePhase.None)
            return (false, $"20f: expected gate phase None after stale-diagnostic broker-flat release, got {st.GateLifecyclePhase}");

        // 21) Soft-transition persistent mismatch should force an expensive reconciliation pass even under throttle stall.
        var softTransitionReconCalls = 0;
        var coord21 = new MismatchEscalationCoordinator(
            getSnapshot: () => snapStall,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: (_, _) => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null,
            runInstrumentGateReconciliation: (_, _, _) =>
            {
                softTransitionReconCalls++;
                return new GateReconciliationResult { RunnerInvoked = true, OutcomeStatus = ReconciliationOutcomeStatus.Partial };
            },
            evaluateReleaseReadiness: (_, _, _, _) => SoftTransitionFlat("ST9"),
            stateConsistencyStableWindowMs: 200);
        var tPersist21 = t0.AddMilliseconds(MismatchEscalationPolicy.MISMATCH_FAIL_CLOSED_THRESHOLD_MS + 200);
        coord21.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST9",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = t0
        });
        coord21.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ST9",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = tPersist21
        });
        st = coord21.GetStateForTest("ST9");
        if (st == null)
            return (false, "21: expected ST9 state");
        st.GateProgress.NextAllowedExpensiveReconciliationUtc = tPersist21.AddMinutes(1);
        softTransitionReconCalls = 0;
        coord21.AdvanceStateConsistencyGateForTest("ST9", snapStall, tPersist21, new MismatchObservation
        {
            Instrument = "ST9",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            ObservedUtc = tPersist21,
            BrokerWorkingOrderCount = 0,
            LocalWorkingOrderCount = 0,
            NetBrokerQty = 0,
            NetJournalQty = 0
        });
        if (softTransitionReconCalls == 0)
            return (false, "21: soft-transition persistent mismatch should force a reconciliation pass under throttle stall");

        return (true, null);
        }
        finally
        {
            FeatureFlags.ControlPlaneParityHardFlattenFromTryEnsureJournalIntegrity = prevParityFlatten;
        }
    }
}
