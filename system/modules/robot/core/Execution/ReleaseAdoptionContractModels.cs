// Contract-alignment: recovery adoption scheduling, release blocking vs adoption eligibility (2026-04 remediation).

using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Release-blocking adoption candidate class (journal + broker context).</summary>
public enum BlockingCandidateCategory
{
    Unknown = 0,
    NotOnAccount = 1,
    WrongInstrument = 2,
    TagMismatch = 3,
    AlreadyOwnedElsewhere = 4,
    JournalOnly = 5,
    ProtectiveOnly = 6,
    SupervisoryBlocked = 7,
    StaleSnapshot = 8,
    UnsupportedCandidateShape = 9,
    BrokerVisibleAdoptable = 10
}

/// <summary>Maps a blocking row to release vs adoption lanes (PR3).</summary>
public enum ReleaseAdoptionDisposition
{
    UnknownTreatAsHardFailure = 0,
    AdoptableAndRetryable = 1,
    NonAdoptableHardFailure = 2,
    NonAdoptableNonBlocking = 3,
    NeedsDifferentReconciliationLane = 4
}

/// <summary>Per-intent release/adoption diagnostic for audits and zero-delta recovery scans.</summary>
public sealed class ReleaseBlockingCandidateDiagnostic
{
    public string IntentId { get; init; } = "";
    public bool BlocksRelease { get; init; }
    public string? JournalExclusionReason { get; init; }
    public BlockingCandidateCategory Category { get; init; }
    public ReleaseAdoptionDisposition Disposition { get; init; }
    public bool RecoveryAdoptionShouldConsume { get; init; }
    public string NonAdoptionReason { get; init; } = "";
}

/// <summary>Maps categories to release disposition (pure).</summary>
public static class ReleaseAdoptionDispositionMapper
{
    public static ReleaseAdoptionDisposition Map(BlockingCandidateCategory c) =>
        c switch
        {
            BlockingCandidateCategory.BrokerVisibleAdoptable => ReleaseAdoptionDisposition.AdoptableAndRetryable,
            BlockingCandidateCategory.JournalOnly => ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane,
            BlockingCandidateCategory.WrongInstrument => ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane,
            BlockingCandidateCategory.TagMismatch => ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane,
            BlockingCandidateCategory.StaleSnapshot => ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane,
            BlockingCandidateCategory.NotOnAccount => ReleaseAdoptionDisposition.NonAdoptableHardFailure,
            BlockingCandidateCategory.ProtectiveOnly => ReleaseAdoptionDisposition.NonAdoptableHardFailure,
            BlockingCandidateCategory.SupervisoryBlocked => ReleaseAdoptionDisposition.NonAdoptableHardFailure,
            BlockingCandidateCategory.UnsupportedCandidateShape => ReleaseAdoptionDisposition.NonAdoptableHardFailure,
            BlockingCandidateCategory.AlreadyOwnedElsewhere => ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane,
            BlockingCandidateCategory.Unknown => ReleaseAdoptionDisposition.UnknownTreatAsHardFailure,
            _ => ReleaseAdoptionDisposition.UnknownTreatAsHardFailure
        };

    public static bool ShouldCountAsGenericPendingAdoption(ReleaseAdoptionDisposition d) =>
        d == ReleaseAdoptionDisposition.AdoptableAndRetryable;
}
