using System;
using System.Collections.Generic;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

/// <summary>Contract refactor: blocker → decision → schedule outcome separation.</summary>
public static class ReconciliationContractRefactorTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var (a, ea) = Case1_AdoptableDecision();
        if (!a) return (false, ea);
        var (b, eb) = Case2_NonAdoptableNoAdoptSchedule();
        if (!b) return (false, eb);
        var (c, ec) = Case3_MixedBlockers();
        if (!c) return (false, ec);
        var (d, ed) = Case4_LegacyClassifierGapInformational();
        if (!d) return (false, ed);
        var (e, ee) = Case5_ScheduleSignals();
        if (!e) return (false, ee);
        var (f, ef) = Case6_TransientRetryExhaustionEscalates();
        if (!f) return (false, ef);
        var (g, eg) = Case7_AdoptOnlyFailureStaysGateScoped();
        if (!g) return (false, eg);
        var (h, eh) = Case8_HardFailurePersistsDurableLatch();
        if (!h) return (false, eh);
        var (i, ei) = Case9_UnexplainedPositionDeltaPersistsDurableLatch();
        if (!i) return (false, ei);
        var (j, ej) = Case10_BrokerFlatSoftTransitionBlockersRelease();
        if (!j) return (false, ej);
        return (true, null);
    }

    private static (bool, string?) Case1_AdoptableDecision()
    {
        var b = new ReconciliationBlocker
        {
            BlockerId = "t",
            Category = BlockingCandidateCategory.BrokerVisibleAdoptable,
            Disposition = ReleaseAdoptionDisposition.AdoptableAndRetryable,
            ShouldAdopt = true,
            BlocksRelease = true,
            ReasonCode = ReconciliationBlockerReasonCode.BrokerVisibleAdoptableExposure,
            Source = ReconciliationBlockerSource.Journal,
            IsTransient = false,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IntentId = "x"
        };
        if (ReconciliationDecisionResolver.ResolveBlocker(b) != ReconciliationDecision.ADOPT)
            return (false, "Case1: expected ADOPT");
        return (true, null);
    }

    private static (bool, string?) Case2_NonAdoptableNoAdoptSchedule()
    {
        var b = new ReconciliationBlocker
        {
            BlockerId = "j",
            Category = BlockingCandidateCategory.JournalOnly,
            Disposition = ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane,
            ShouldAdopt = false,
            BlocksRelease = true,
            ReasonCode = ReconciliationBlockerReasonCode.JournalOnlyBrokerFlat,
            Source = ReconciliationBlockerSource.Journal,
            IsTransient = false,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IntentId = "y"
        };
        var d = ReconciliationDecisionResolver.ResolveBlocker(b);
        if (d != ReconciliationDecision.ALTERNATE_LANE)
            return (false, "Case2: expected ALTERNATE_LANE");
        if (ReconciliationDecisionResolver.ShouldScheduleRecoveryAdoption(new List<ReconciliationBlocker> { b }))
            return (false, "Case2: adoption must not schedule for lane-only");
        return (true, null);
    }

    private static (bool, string?) Case3_MixedBlockers()
    {
        var adopt = new ReconciliationBlocker
        {
            BlockerId = "a",
            Category = BlockingCandidateCategory.BrokerVisibleAdoptable,
            Disposition = ReleaseAdoptionDisposition.AdoptableAndRetryable,
            ShouldAdopt = true,
            BlocksRelease = true,
            ReasonCode = ReconciliationBlockerReasonCode.BrokerVisibleAdoptableExposure,
            Source = ReconciliationBlockerSource.Journal,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IntentId = "i1"
        };
        var lane = new ReconciliationBlocker
        {
            BlockerId = "l",
            Category = BlockingCandidateCategory.JournalOnly,
            Disposition = ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane,
            ShouldAdopt = false,
            BlocksRelease = true,
            ReasonCode = ReconciliationBlockerReasonCode.JournalOnlyBrokerFlat,
            Source = ReconciliationBlockerSource.Journal,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IntentId = "i2"
        };
        var list = new List<ReconciliationBlocker> { adopt, lane };
        if (!ReconciliationDecisionResolver.ShouldScheduleRecoveryAdoption(list))
            return (false, "Case3: should schedule when ADOPT present");
        var inp = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "ES",
            SnapshotSufficient = true,
            BrokerPositionQty = 1,
            BrokerWorkingCount = 1,
            JournalOpenQty = 1,
            IeaOwnedPlusAdoptedWorking = 1,
            ReconciliationBlockers = list,
            UseInstrumentExecutionAuthority = true
        };
        var r = StateConsistencyReleaseEvaluator.Evaluate(inp);
        if (r.ReleaseReady)
            return (false, "Case3: release must be blocked");
        if (!r.PendingAdoptionExists)
            return (false, "Case3: PendingAdoptionExists for ADOPT");
        return (true, null);
    }

    private static (bool, string?) Case4_LegacyClassifierGapInformational()
    {
        var inp = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "ES",
            SnapshotSufficient = true,
            BrokerPositionQty = 0,
            BrokerWorkingCount = 0,
            JournalOpenQty = 0,
            IeaOwnedPlusAdoptedWorking = 0,
            PendingAdoptionCandidateCount = 1,
            ReconciliationBlockers = null,
            BlockingCandidateDiagnostics = null,
            UseInstrumentExecutionAuthority = false
        };
        var r = StateConsistencyReleaseEvaluator.Evaluate(inp);
        if (!r.LegacyClassifierGap)
            return (false, "Case4: expected LegacyClassifierGap flag");
        if (!r.ReleaseReady)
            return (false, "Case4: legacy gap without classifier is informational — release when broker/journal/IEA explainable");
        if (!r.Contradictions.Contains("info_legacy_classifier_gap_pending_adoption"))
            return (false, "Case4: expected info_legacy_classifier_gap_pending_adoption contradiction");
        return (true, null);
    }

    private static (bool, string?) Case5_ScheduleSignals()
    {
        if (!ReconciliationScheduleSignals.AdoptionWorkOrQueueInflight(ReconciliationScheduleOutcome.ALREADY_RUNNING))
            return (false, "Case5: ALREADY_RUNNING inflight");
        if (ReconciliationScheduleSignals.NewWorkAccepted(ReconciliationScheduleOutcome.ALREADY_RUNNING))
            return (false, "Case5: ALREADY_RUNNING is not new work");
        if (!ReconciliationScheduleSignals.NewWorkAccepted(ReconciliationScheduleOutcome.ACCEPTED))
            return (false, "Case5: ACCEPTED is new work");
        return (true, null);
    }

    private static (bool, string?) Case6_TransientRetryExhaustionEscalates()
    {
        var b = new ReconciliationBlocker
        {
            BlockerId = "t",
            Category = BlockingCandidateCategory.BrokerVisibleAdoptable,
            Disposition = ReleaseAdoptionDisposition.AdoptableAndRetryable,
            ShouldAdopt = true,
            BlocksRelease = true,
            ReasonCode = ReconciliationBlockerReasonCode.StaleSnapshot,
            Source = ReconciliationBlockerSource.Journal,
            IsTransient = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IntentId = "z",
            RetryAttemptCount = ReconciliationDecisionResolver.MaxTransientRetryAttemptsBeforeEscalate,
            FirstSeenUtc = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var utc = DateTimeOffset.UtcNow;
        if (ReconciliationDecisionResolver.ResolveBlocker(b, utc) != ReconciliationDecision.ESCALATE)
            return (false, "Case6: exhausted transient must ESCALATE");
        if (ReconciliationDecisionResolver.ShouldScheduleRecoveryAdoption(new List<ReconciliationBlocker> { b }, utc))
            return (false, "Case6: must not schedule adoption after exhaustion");
        return (true, null);
    }

    private static (bool, string?) Case7_AdoptOnlyFailureStaysGateScoped()
    {
        var b = new ReconciliationBlocker
        {
            BlockerId = "a",
            Category = BlockingCandidateCategory.BrokerVisibleAdoptable,
            Disposition = ReleaseAdoptionDisposition.AdoptableAndRetryable,
            ShouldAdopt = true,
            BlocksRelease = true,
            ReasonCode = ReconciliationBlockerReasonCode.BrokerVisibleAdoptableExposure,
            Source = ReconciliationBlockerSource.Journal,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IntentId = "adopt-me"
        };
        var r = StateConsistencyReleaseEvaluator.Evaluate(new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "MNQ",
            SnapshotSufficient = true,
            BrokerPositionQty = 1,
            BrokerWorkingCount = 1,
            JournalOpenQty = 1,
            IeaOwnedPlusAdoptedWorking = 1,
            ReconciliationBlockers = new List<ReconciliationBlocker> { b },
            UseInstrumentExecutionAuthority = true
        });
        if (ForcedConvergenceRiskLatchPolicy.ShouldPersistDurableRiskLatch(r))
            return (false, "Case7: ADOPT-only blocker should remain gate-scoped, not durable");
        return (true, null);
    }

    private static (bool, string?) Case8_HardFailurePersistsDurableLatch()
    {
        var b = new ReconciliationBlocker
        {
            BlockerId = "hard",
            Category = BlockingCandidateCategory.Unknown,
            Disposition = ReleaseAdoptionDisposition.UnknownTreatAsHardFailure,
            ShouldAdopt = false,
            BlocksRelease = true,
            ReasonCode = ReconciliationBlockerReasonCode.UnknownIntent,
            Source = ReconciliationBlockerSource.Broker,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IntentId = "unknown"
        };
        var r = StateConsistencyReleaseEvaluator.Evaluate(new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "MCL",
            SnapshotSufficient = true,
            BrokerPositionQty = 1,
            BrokerWorkingCount = 0,
            JournalOpenQty = 1,
            IeaOwnedPlusAdoptedWorking = 0,
            ReconciliationBlockers = new List<ReconciliationBlocker> { b },
            UseInstrumentExecutionAuthority = false
        });
        if (!ForcedConvergenceRiskLatchPolicy.ShouldPersistDurableRiskLatch(r))
            return (false, "Case8: hard failure should persist a durable risk latch");
        return (true, null);
    }

    private static (bool, string?) Case9_UnexplainedPositionDeltaPersistsDurableLatch()
    {
        var r = StateConsistencyReleaseEvaluator.Evaluate(new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "MNG",
            SnapshotSufficient = true,
            BrokerPositionQty = 4,
            BrokerWorkingCount = 0,
            JournalOpenQty = 0,
            IeaOwnedPlusAdoptedWorking = 0,
            ReconciliationBlockers = null,
            BlockingCandidateDiagnostics = null,
            UseInstrumentExecutionAuthority = false
        });
        if (!ForcedConvergenceRiskLatchPolicy.ShouldPersistDurableRiskLatch(r))
            return (false, "Case9: unexplained broker/journal position delta should persist a durable risk latch");
        return (true, null);
    }

    private static (bool, string?) Case10_BrokerFlatSoftTransitionBlockersRelease()
    {
        var adopt = new ReconciliationBlocker
        {
            BlockerId = "adopt-flat",
            Category = BlockingCandidateCategory.BrokerVisibleAdoptable,
            Disposition = ReleaseAdoptionDisposition.AdoptableAndRetryable,
            ShouldAdopt = true,
            BlocksRelease = true,
            ReasonCode = ReconciliationBlockerReasonCode.BrokerVisibleAdoptableExposure,
            Source = ReconciliationBlockerSource.Broker,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IntentId = "intent-a"
        };
        var lane = new ReconciliationBlocker
        {
            BlockerId = "lane-flat",
            Category = BlockingCandidateCategory.AlreadyOwnedElsewhere,
            Disposition = ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane,
            ShouldAdopt = false,
            BlocksRelease = true,
            ReasonCode = ReconciliationBlockerReasonCode.AlreadyOwnedElsewhere,
            Source = ReconciliationBlockerSource.Registry,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IntentId = "intent-b",
            LaneType = ReconciliationLaneType.Other,
            ResolutionOwner = "OWNERSHIP_ELSEWHERE",
            IsTerminal = false
        };

        var r = StateConsistencyReleaseEvaluator.Evaluate(new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "MNG",
            SnapshotSufficient = true,
            BrokerPositionQty = 0,
            BrokerWorkingCount = 0,
            JournalOpenQty = 0,
            IeaOwnedPlusAdoptedWorking = 0,
            ReconciliationBlockers = new List<ReconciliationBlocker> { adopt, lane },
            UseInstrumentExecutionAuthority = true
        });

        if (!r.ReleaseReady)
            return (false, "Case10: broker-flat soft-transition leftovers should release");
        if (!string.Equals(r.Summary, "release_ready_residual_cleanup:MISMATCH_RESIDUAL_ADOPTION_RETIREMENT", StringComparison.Ordinal))
            return (false, "Case10: expected broker-flat residual adoption cleanup release summary, got " + r.Summary);
        if (!r.PendingAdoptionExists)
            return (false, "Case10: pending adoption should remain visible in diagnostics");
        return (true, null);
    }
}
