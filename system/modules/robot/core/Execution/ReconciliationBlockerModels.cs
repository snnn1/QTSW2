// Contract extraction: blocker classification, domain decisions, and scheduler outcomes are separate layers.

using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Stable reason codes for blockers (no free-text semantics in contracts).</summary>
public enum ReconciliationBlockerReasonCode
{
    None = 0,
    UnknownIntent,
    JournalOnlyBrokerFlat,
    BrokerVisibleAdoptableExposure,
    TagMismatchExposure,
    RegistryTrustedTagNotVisible,
    AlreadyOwnedElsewhere,
    UnsupportedBlockingShape,
    NotOnAccount,
    WrongInstrument,
    StaleSnapshot,
    ProtectiveOnly,
    SupervisoryBlocked
}

public enum ReconciliationBlockerSource
{
    Journal = 0,
    Broker = 1,
    Registry = 2,
    Derived = 3
}

/// <summary>Non-adoption reconciliation path for <see cref="ReconciliationDecision.ALTERNATE_LANE"/> (audit / ops).</summary>
public enum ReconciliationLaneType
{
    Unspecified = 0,
    JournalOnlyReconciliation,
    GateRecovery,
    RegistryMismatch,
    TagMismatchLane,
    ProtectiveSupervisory,
    StaleSnapshotRetry,
    Other
}

/// <summary>Single source of truth for a release-relevant reconciliation blocker.</summary>
public sealed class ReconciliationBlocker
{
    public string BlockerId { get; init; } = "";
    public BlockingCandidateCategory Category { get; init; }
    public ReleaseAdoptionDisposition Disposition { get; init; }
    /// <summary>True when recovery adoption scan is designed to consume this shape (broker-visible adoptable).</summary>
    public bool ShouldAdopt { get; init; }
    public bool BlocksRelease { get; init; }
    public ReconciliationBlockerReasonCode ReasonCode { get; init; }
    public ReconciliationBlockerSource Source { get; init; }
    public bool IsTransient { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string IntentId { get; init; } = "";

    /// <summary>Observed evaluation epochs for transient RETRY path (filled by coordinator when known).</summary>
    public int RetryAttemptCount { get; init; }

    /// <summary>First observation of this blocker key in transient-retry tracking (coordinator).</summary>
    public DateTimeOffset? FirstSeenUtc { get; init; }

    /// <summary>What non-adoption lane owns resolution for <see cref="ReconciliationDecision.ALTERNATE_LANE"/>.</summary>
    public ReconciliationLaneType LaneType { get; init; }

    /// <summary>Subsystem or role responsible (e.g. JOURNAL_RECONCILIATION, IEA); empty if unspecified.</summary>
    public string ResolutionOwner { get; init; } = "";

    /// <summary>True when this lane does not schedule recovery adoption by design; null if unknown.</summary>
    public bool? IsTerminal { get; init; }

    /// <summary>Copy with transient-retry telemetry (engine merge).</summary>
    public ReconciliationBlocker WithTransientRetryTelemetry(int retryAttemptCount, DateTimeOffset? firstSeenUtc) =>
        new()
        {
            BlockerId = BlockerId,
            Category = Category,
            Disposition = Disposition,
            ShouldAdopt = ShouldAdopt,
            BlocksRelease = BlocksRelease,
            ReasonCode = ReasonCode,
            Source = Source,
            IsTransient = IsTransient,
            CreatedAtUtc = CreatedAtUtc,
            IntentId = IntentId,
            RetryAttemptCount = retryAttemptCount,
            FirstSeenUtc = firstSeenUtc,
            LaneType = LaneType,
            ResolutionOwner = ResolutionOwner,
            IsTerminal = IsTerminal
        };
}

/// <summary>Domain-only resolution for a blocker (no scheduler/throttle).</summary>
public enum ReconciliationDecision
{
    ADOPT = 0,
    IGNORE = 1,
    RETRY = 2,
    ESCALATE = 3,
    ALTERNATE_LANE = 4
}

/// <summary>Execution-only: what RequestAdoptionScan / IEA scheduler did. Does not imply problem solved.</summary>
public enum ReconciliationScheduleOutcome
{
    ACCEPTED = 0,
    ALREADY_RUNNING = 1,
    ALREADY_QUEUED = 2,
    THROTTLED = 3,
    COMPLETED_RECENTLY = 4,
    SKIPPED = 5,
    REJECTED = 6
}

/// <summary>Pure resolver: maps a classified blocker to a domain decision.</summary>
public static class ReconciliationDecisionResolver
{
    /// <summary>Consecutive evaluation observations while transient — escalate to avoid infinite RETRY.</summary>
    public const int MaxTransientRetryAttemptsBeforeEscalate = 32;

    /// <summary>Wall-clock ceiling for transient RETRY when <see cref="ReconciliationBlocker.FirstSeenUtc"/> is set.</summary>
    public static readonly TimeSpan MaxTransientRetryWallClockBeforeEscalate = TimeSpan.FromMinutes(30);

    public static ReconciliationDecision ResolveBlocker(ReconciliationBlocker blocker, DateTimeOffset? evaluationUtc = null)
    {
        if (!blocker.BlocksRelease)
            return ReconciliationDecision.IGNORE;

        if (blocker.Disposition == ReleaseAdoptionDisposition.NonAdoptableNonBlocking)
            return ReconciliationDecision.IGNORE;

        if (blocker.ShouldAdopt && blocker.IsTransient)
        {
            if (IsTransientRetryExhausted(blocker, evaluationUtc))
                return ReconciliationDecision.ESCALATE;
            return ReconciliationDecision.RETRY;
        }

        switch (blocker.Disposition)
        {
            case ReleaseAdoptionDisposition.AdoptableAndRetryable:
                return ReconciliationDecision.ADOPT;
            case ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane:
                return ReconciliationDecision.ALTERNATE_LANE;
            case ReleaseAdoptionDisposition.NonAdoptableHardFailure:
            case ReleaseAdoptionDisposition.UnknownTreatAsHardFailure:
                return ReconciliationDecision.ESCALATE;
            case ReleaseAdoptionDisposition.NonAdoptableNonBlocking:
                return ReconciliationDecision.IGNORE;
            default:
                return ReconciliationDecision.ESCALATE;
        }
    }

    /// <summary>True when transient RETRY would become ESCALATE due to count or wall-clock (mirrors <see cref="ResolveBlocker"/>).</summary>
    public static bool IsTransientRetryExhausted(ReconciliationBlocker blocker, DateTimeOffset? evaluationUtc)
    {
        if (!blocker.ShouldAdopt || !blocker.IsTransient)
            return false;
        if (blocker.RetryAttemptCount >= MaxTransientRetryAttemptsBeforeEscalate)
            return true;
        if (evaluationUtc != null && blocker.FirstSeenUtc != null &&
            evaluationUtc.Value - blocker.FirstSeenUtc.Value >= MaxTransientRetryWallClockBeforeEscalate)
            return true;
        return false;
    }

    public static IReadOnlyList<(ReconciliationBlocker Blocker, ReconciliationDecision Decision)> ResolveAll(
        IReadOnlyList<ReconciliationBlocker>? blockers, DateTimeOffset? evaluationUtc = null)
    {
        if (blockers == null || blockers.Count == 0)
            return Array.Empty<(ReconciliationBlocker, ReconciliationDecision)>();

        var list = new List<(ReconciliationBlocker, ReconciliationDecision)>(blockers.Count);
        foreach (var b in blockers)
            list.Add((b, ResolveBlocker(b, evaluationUtc)));
        return list;
    }

    public static bool AnyDecision(IReadOnlyList<(ReconciliationBlocker Blocker, ReconciliationDecision Decision)> resolved,
        ReconciliationDecision d)
    {
        foreach (var x in resolved)
        {
            if (x.Decision == d)
                return true;
        }

        return false;
    }

    /// <summary>True when recovery adoption scan should be invoked (ADOPT or RETRY, excluding exhausted transient → ESCALATE).</summary>
    public static bool ShouldScheduleRecoveryAdoption(IReadOnlyList<ReconciliationBlocker>? blockers,
        DateTimeOffset? evaluationUtc = null)
    {
        if (blockers == null || blockers.Count == 0)
            return false;
        foreach (var b in blockers)
        {
            var d = ResolveBlocker(b, evaluationUtc);
            if (d == ReconciliationDecision.ADOPT || d == ReconciliationDecision.RETRY)
                return true;
        }

        return false;
    }

    public static void CountDecisions(IReadOnlyList<(ReconciliationBlocker Blocker, ReconciliationDecision Decision)> resolved,
        out int adopt, out int escalate, out int retry, out int alternateLane, out int ignore)
    {
        adopt = escalate = retry = alternateLane = ignore = 0;
        foreach (var x in resolved)
        {
            switch (x.Decision)
            {
                case ReconciliationDecision.ADOPT:
                    adopt++;
                    break;
                case ReconciliationDecision.ESCALATE:
                    escalate++;
                    break;
                case ReconciliationDecision.RETRY:
                    retry++;
                    break;
                case ReconciliationDecision.ALTERNATE_LANE:
                    alternateLane++;
                    break;
                case ReconciliationDecision.IGNORE:
                    ignore++;
                    break;
            }
        }
    }

    /// <summary>
    /// Validates that every blocker received a decision (exhaustive resolver).
    /// Returns false only if invariant extension fails (should not happen).
    /// </summary>
    public static bool ValidateAllBlockersResolved(IReadOnlyList<ReconciliationBlocker>? blockers,
        IReadOnlyList<(ReconciliationBlocker Blocker, ReconciliationDecision Decision)> resolved, out string? error)
    {
        error = null;
        if (blockers == null || blockers.Count == 0)
            return true;
        if (resolved.Count != blockers.Count)
        {
            error = "blocker_count_mismatch";
            return false;
        }

        return true;
    }
}

/// <summary>Interprets scheduler outcomes without implying domain success.</summary>
public static class ReconciliationScheduleSignals
{
    /// <summary>New work was accepted (queued or ran inline).</summary>
    public static bool NewWorkAccepted(ReconciliationScheduleOutcome o) =>
        o == ReconciliationScheduleOutcome.ACCEPTED;

    /// <summary>Scan may be in flight or already scheduled — mismatch classification often defers.</summary>
    public static bool AdoptionWorkOrQueueInflight(ReconciliationScheduleOutcome o) =>
        o is ReconciliationScheduleOutcome.ACCEPTED
            or ReconciliationScheduleOutcome.ALREADY_RUNNING
            or ReconciliationScheduleOutcome.ALREADY_QUEUED;

    public static bool ThrottleBlockedNewSchedule(ReconciliationScheduleOutcome o) =>
        o is ReconciliationScheduleOutcome.THROTTLED or ReconciliationScheduleOutcome.COMPLETED_RECENTLY;
}

/// <summary>Maps journal diagnostics into <see cref="ReconciliationBlocker"/> (classification layer).</summary>
public static class ReconciliationBlockerFactory
{
    public static ReconciliationBlocker FromDiagnostic(ReleaseBlockingCandidateDiagnostic d, DateTimeOffset createdAtUtc)
    {
        var (laneType, owner, isTerminal) = MapAlternateLaneContract(d);
        return new ReconciliationBlocker
        {
            BlockerId = $"{(int)d.Category}:{d.IntentId}",
            IntentId = d.IntentId ?? "",
            Category = d.Category,
            Disposition = d.Disposition,
            ShouldAdopt = d.RecoveryAdoptionShouldConsume,
            BlocksRelease = d.BlocksRelease,
            ReasonCode = MapReasonCode(d),
            Source = ReconciliationBlockerSource.Journal,
            IsTransient = d.Category == BlockingCandidateCategory.StaleSnapshot,
            CreatedAtUtc = createdAtUtc,
            RetryAttemptCount = 0,
            FirstSeenUtc = null,
            LaneType = laneType,
            ResolutionOwner = owner,
            IsTerminal = isTerminal
        };
    }

    /// <summary>Partial contract for <see cref="ReconciliationDecision.ALTERNATE_LANE"/> — what happens next / who owns it.</summary>
    private static (ReconciliationLaneType LaneType, string ResolutionOwner, bool? IsTerminal) MapAlternateLaneContract(
        ReleaseBlockingCandidateDiagnostic d)
    {
        if (d.Disposition != ReleaseAdoptionDisposition.NeedsDifferentReconciliationLane)
            return (ReconciliationLaneType.Unspecified, "", null);

        return d.Category switch
        {
            BlockingCandidateCategory.JournalOnly => (ReconciliationLaneType.JournalOnlyReconciliation, "JOURNAL_RECONCILIATION",
                false),
            BlockingCandidateCategory.TagMismatch => (ReconciliationLaneType.TagMismatchLane, "REGISTRY_TAG_RECONCILIATION", false),
            BlockingCandidateCategory.ProtectiveOnly => (ReconciliationLaneType.ProtectiveSupervisory, "IEA_PROTECTIVE_LANE", false),
            BlockingCandidateCategory.SupervisoryBlocked => (ReconciliationLaneType.ProtectiveSupervisory, "SUPERVISORY_HALT", true),
            BlockingCandidateCategory.StaleSnapshot => (ReconciliationLaneType.StaleSnapshotRetry, "STALE_SNAPSHOT_REFRESH", false),
            BlockingCandidateCategory.NotOnAccount or BlockingCandidateCategory.WrongInstrument => (
                ReconciliationLaneType.RegistryMismatch, "ACCOUNT_OR_INSTRUMENT_ALIGNMENT", false),
            BlockingCandidateCategory.AlreadyOwnedElsewhere => (ReconciliationLaneType.Other, "OWNERSHIP_ELSEWHERE", false),
            BlockingCandidateCategory.UnsupportedCandidateShape => (ReconciliationLaneType.Other, "UNSUPPORTED_SHAPE_REVIEW", true),
            _ => (ReconciliationLaneType.Other, "ALTERNATE_LANE", false)
        };
    }

    private static ReconciliationBlockerReasonCode MapReasonCode(ReleaseBlockingCandidateDiagnostic d)
    {
        return d.Category switch
        {
            BlockingCandidateCategory.Unknown => ReconciliationBlockerReasonCode.UnknownIntent,
            BlockingCandidateCategory.NotOnAccount => ReconciliationBlockerReasonCode.NotOnAccount,
            BlockingCandidateCategory.WrongInstrument => ReconciliationBlockerReasonCode.WrongInstrument,
            BlockingCandidateCategory.TagMismatch => ReconciliationBlockerReasonCode.TagMismatchExposure,
            BlockingCandidateCategory.AlreadyOwnedElsewhere => ReconciliationBlockerReasonCode.AlreadyOwnedElsewhere,
            BlockingCandidateCategory.JournalOnly => ReconciliationBlockerReasonCode.JournalOnlyBrokerFlat,
            BlockingCandidateCategory.ProtectiveOnly => ReconciliationBlockerReasonCode.ProtectiveOnly,
            BlockingCandidateCategory.SupervisoryBlocked => ReconciliationBlockerReasonCode.SupervisoryBlocked,
            BlockingCandidateCategory.StaleSnapshot => ReconciliationBlockerReasonCode.StaleSnapshot,
            BlockingCandidateCategory.UnsupportedCandidateShape => ReconciliationBlockerReasonCode.UnsupportedBlockingShape,
            BlockingCandidateCategory.BrokerVisibleAdoptable => ReconciliationBlockerReasonCode.BrokerVisibleAdoptableExposure,
            _ => ReconciliationBlockerReasonCode.UnknownIntent
        };
    }
}

/// <summary>Fingerprint for suppression: stable hash over blocker ids + decisions material.</summary>
public static class ReconciliationBlockerFingerprint
{
    private const ulong Fnv1a64Offset = 14695981039346656037UL;
    private const ulong Fnv1a64Prime = 1099511628211UL;

    public static long Compute(IReadOnlyList<ReconciliationBlocker>? blockers)
    {
        if (blockers == null || blockers.Count == 0)
            return 0;
        var keys = blockers.Select(b =>
                $"{b.BlockerId}|{(int)b.Category}|{(int)b.Disposition}|{b.BlocksRelease}|r{b.RetryAttemptCount}")
            .ToArray();
        Array.Sort(keys, StringComparer.OrdinalIgnoreCase);
        return HashSortedStringList64(keys);
    }

    /// <summary>Matches <see cref="ReleaseReconciliationRedundancySuppression.ComputeStableOrderedStringSetHashFromSortedList"/> (caller sorts ordinal ignore-case).</summary>
    private static long HashSortedStringList64(IReadOnlyList<string> sorted)
    {
        var h = Fnv1a64Offset;
        for (var j = 0; j < sorted.Count; j++)
        {
            var s = sorted[j];
            if (!string.IsNullOrEmpty(s))
            {
                foreach (var c in s)
                {
                    h ^= char.ToUpperInvariant(c);
                    h *= Fnv1a64Prime;
                }
            }

            h ^= 0x7DUL;
            h *= Fnv1a64Prime;
        }

        h ^= (ulong)(uint)sorted.Count;
        h *= Fnv1a64Prime;
        return unchecked((long)h);
    }
}
