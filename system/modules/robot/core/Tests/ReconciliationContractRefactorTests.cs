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
}
