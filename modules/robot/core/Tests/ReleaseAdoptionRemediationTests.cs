using System;
using System.Collections.Generic;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

/// <summary>PR5 regression: release vs adoption contract + evaluator semantics.</summary>
public static class ReleaseAdoptionRemediationTests
{
    public static (bool Pass, string? Error) RunReleaseAdoptionRemediationTests()
    {
        var (a, ea) = CaseA_BrokerVisibleAdoptable_CountsAsGenericPendingAdoption();
        if (!a) return (false, ea);
        var (b, eb) = CaseB_JournalOnly_DoesNotUseGenericPendingAdoption_ButBlocksRelease();
        if (!b) return (false, eb);
        var (c, ec) = CaseE_UnknownClassification_EmitsHardContradiction();
        if (!c) return (false, ec);
        return (true, null);
    }

    private static (bool, string?) CaseA_BrokerVisibleAdoptable_CountsAsGenericPendingAdoption()
    {
        if (!ReleaseAdoptionDispositionMapper.ShouldCountAsGenericPendingAdoption(ReleaseAdoptionDisposition.AdoptableAndRetryable))
            return (false, "CaseA: adoptable should count as generic pending");
        return (true, null);
    }

    private static (bool, string?) CaseB_JournalOnly_DoesNotUseGenericPendingAdoption_ButBlocksRelease()
    {
        var input = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "MES",
            SnapshotSufficient = true,
            UseInstrumentExecutionAuthority = true,
            BrokerPositionQty = 0,
            BrokerWorkingCount = 0,
            JournalOpenQty = 0,
            IeaOwnedPlusAdoptedWorking = 0,
            PendingAdoptionCandidateCount = 1,
            AdoptablePendingAdoptionCandidateCount = 0,
            BlockingCandidateDiagnostics = new List<ReleaseBlockingCandidateDiagnostic>
            {
                new()
                {
                    IntentId = "j1",
                    BlocksRelease = true,
                    Category = BlockingCandidateCategory.JournalOnly,
                    Disposition = ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane,
                    RecoveryAdoptionShouldConsume = false,
                    NonAdoptionReason = "test"
                }
            }
        };
        var r = StateConsistencyReleaseEvaluator.Evaluate(input);
        if (r.ReleaseReady) return (false, "CaseB: expected not release ready");
        if (r.PendingAdoptionExists) return (false, "CaseB: expected no generic pending_adoption");
        var found = false;
        foreach (var c in r.Contradictions ?? new List<string>())
        {
            if (c.StartsWith("blocker_alternate_lane:", StringComparison.Ordinal))
                found = true;
        }
        if (!found) return (false, "CaseB: expected blocker_alternate_lane contradiction");
        return (true, null);
    }

    private static (bool, string?) CaseE_UnknownClassification_EmitsHardContradiction()
    {
        var input = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = "MES",
            SnapshotSufficient = true,
            UseInstrumentExecutionAuthority = true,
            BrokerPositionQty = 0,
            BrokerWorkingCount = 0,
            JournalOpenQty = 0,
            IeaOwnedPlusAdoptedWorking = 0,
            PendingAdoptionCandidateCount = 1,
            AdoptablePendingAdoptionCandidateCount = 0,
            BlockingCandidateDiagnostics = new List<ReleaseBlockingCandidateDiagnostic>
            {
                new()
                {
                    IntentId = "u1",
                    BlocksRelease = true,
                    Category = BlockingCandidateCategory.Unknown,
                    Disposition = ReleaseAdoptionDisposition.UnknownTreatAsHardFailure,
                    RecoveryAdoptionShouldConsume = false,
                    NonAdoptionReason = "test"
                }
            }
        };
        var r = StateConsistencyReleaseEvaluator.Evaluate(input);
        var found = false;
        foreach (var c in r.Contradictions ?? new List<string>())
        {
            if (c.StartsWith("blocker_escalate:", StringComparison.Ordinal))
                found = true;
        }
        if (!found) return (false, "CaseE: expected blocker_escalate contradiction");
        if (r.ReleaseReady) return (false, "CaseE: expected not release ready");
        return (true, null);
    }
}
