// P1.5: State-consistency gate — release readiness, reconciliation outcomes, action policy.

using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>P1.5-A: Explicit gate lifecycle (instrument-scoped). Releases only via STABLE_PENDING_RELEASE window.</summary>
public enum GateLifecyclePhase
{
    None,
    /// <summary>First mismatch: blocked, gate engaged.</summary>
    DetectedBlocked,
    /// <summary>Gate-safe reconciliation / inspection pass running or just completed.</summary>
    Reconciling,
    /// <summary>Release invariants pass; accumulating continuous stability time.</summary>
    StablePendingRelease,
    /// <summary>Past persistence threshold; stronger blocked semantics (maps to escalation PERSISTENT_MISMATCH).</summary>
    PersistentMismatch,
    /// <summary>Terminal fail-closed for this path.</summary>
    FailClosed
}

/// <summary>Reconciliation execution mode (P1.5-D).</summary>
public enum ReconciliationRunMode
{
    Normal,
    GateRecovery
}

/// <summary>P1.5-E: Outcome classification for a gate reconciliation pass.</summary>
public enum ReconciliationOutcomeStatus
{
    Success,
    Partial,
    Failed,
    NoDataOptional
}

/// <summary>Options for a single reconciliation run (gate-aware).</summary>
public sealed class ReconciliationRunOptions
{
    /// <summary>When set, qty-mismatch callbacks and destructive orphan journal closure are skipped for this instrument.</summary>
    public string? GateRecoveryInstrument { get; set; }

    /// <summary>When true, periodic redundancy fast-path does not skip this pass (e.g. <see cref="ReconciliationRunner.ForceRunNow"/>).</summary>
    public bool BypassRedundancySuppression { get; set; }

    public ReconciliationRunMode Mode =>
        string.IsNullOrWhiteSpace(GateRecoveryInstrument) ? ReconciliationRunMode.Normal : ReconciliationRunMode.GateRecovery;
}

/// <summary>Inputs for <see cref="StateConsistencyReleaseEvaluator.Evaluate"/> (built by RobotEngine).</summary>
public sealed class StateConsistencyReleaseEvaluationInput
{
    public string Instrument { get; set; } = "";
    public int BrokerPositionQty { get; set; }
    public int BrokerWorkingCount { get; set; }
    public int PendingExecutionWorkload { get; set; }
    /// <summary>Open journal remaining quantity (same semantics as mismatch assembly).</summary>
    public int JournalOpenQty { get; set; }
    /// <summary>True when release evaluation had a canonical ownership snapshot for this instrument.</summary>
    public bool OwnershipSnapshotAvailable { get; set; }
    /// <summary>Gross non-closed ownership quantity across active/orphan/manual slots.</summary>
    public int OwnershipGrossOpenQty { get; set; }
    /// <summary>Signed net quantity from the ownership ledger.</summary>
    public int OwnershipSignedNetQty { get; set; }
    /// <summary>Active ownership slots for this execution instrument.</summary>
    public int OwnershipActiveSlotCount { get; set; }
    /// <summary>Orphan ownership slots for this execution instrument.</summary>
    public int OwnershipOrphanSlotCount { get; set; }
    /// <summary>IEA owned+adopted working count, or -1 if unavailable / fail-closed.</summary>
    public int IeaOwnedPlusAdoptedWorking { get; set; }
    /// <summary>
    /// Release-blocking pending adoption rows (excludes stale journal intents:
    /// broker flat, no QTSW2 working ref for intent, no open journal qty). Built by RobotEngine from ExecutionJournal.
    /// </summary>
    public int PendingAdoptionCandidateCount { get; set; }

    /// <summary>
    /// Subset of <see cref="PendingAdoptionCandidateCount"/> that recovery adoption is designed to clear
    /// (<see cref="ReleaseAdoptionDisposition.AdoptableAndRetryable"/>). When diagnostics are unavailable, mirrors raw count.
    /// </summary>
    public int AdoptablePendingAdoptionCandidateCount { get; set; }

    /// <summary>Optional per-candidate classification (PR2/PR3). Null when engine did not run journal diagnostics pass.</summary>
    public IReadOnlyList<ReleaseBlockingCandidateDiagnostic>? BlockingCandidateDiagnostics { get; set; }

    /// <summary>Canonical blocker list (classification layer). Preferred over raw pending counts.</summary>
    public IReadOnlyList<ReconciliationBlocker>? ReconciliationBlockers { get; set; }

    /// <summary>Fingerprint for redundancy suppression (from <see cref="ReconciliationBlockerFingerprint"/>).</summary>
    public long ReconciliationBlockersFingerprint { get; set; }

    /// <summary>64-bit sorted blocking adoption intent set surrogate (release suppression; not logged as raw ids).</summary>
    public long BlockingAdoptionIntentSetHash { get; set; }

    /// <summary>64-bit mismatch-trusted registry intent set surrogate.</summary>
    public long RegistryMismatchTrustedIntentSetHash { get; set; }

    /// <summary>64-bit open journal intent set surrogate (remaining qty &gt; 0).</summary>
    public long JournalOpenIntentSetHash { get; set; }

    public bool SnapshotSufficient { get; set; } = true;
    public bool UseInstrumentExecutionAuthority { get; set; }

    /// <summary>
    /// True when <see cref="PendingAlignmentAuthority.IsPendingAlignment"/> for this instrument — release evaluation may
    /// treat expected broker-vs-journal qty lag as informational rather than a blocking contradiction.
    /// </summary>
    public bool PendingAlignmentActive { get; set; }
}

/// <summary>P1.5-B: Structured release readiness (single evaluation function per instrument).</summary>
public sealed class StateConsistencyReleaseReadinessResult
{
    public string Instrument { get; set; } = "";
    public bool IsExplainable { get; set; }
    /// <summary>True when all invariants pass AND no pending adoption AND no unexplained risk.</summary>
    public bool ReleaseReady { get; set; }
    public bool BrokerPositionExplainable { get; set; }
    public bool BrokerWorkingExplainable { get; set; }
    public bool LocalStateCoherent { get; set; }
    public bool PendingAdoptionExists { get; set; }
    public int UnexplainedBrokerPositionQty { get; set; }
    public int UnexplainedBrokerWorkingCount { get; set; }
    public List<string> Contradictions { get; set; } = new();
    public string Summary { get; set; } = "";

    /// <summary>
    /// True only when UnifiedExecutionAuthority explicitly allowed MISMATCH_RELEASE for the same readiness frame.
    /// The mismatch coordinator must not clear block state from ReleaseReady alone.
    /// </summary>
    public bool CanonicalReleaseAuthorityAllowed { get; set; }
    public string CanonicalReleaseAuthorityGate { get; set; } = "";
    public string CanonicalReleaseAuthorityDenyReason { get; set; } = "";
    public string CanonicalReleaseAuthorityFrameId { get; set; } = "";

    /// <summary>False when evaluation returned before full inputs (e.g. snapshot missing).</summary>
    public bool SnapshotSufficient { get; set; }

    /// <summary>Mirrors inputs used for Evaluate (diagnostics / gate telemetry).</summary>
    public int DiagnosticBrokerPositionQty { get; set; }
    public int DiagnosticJournalOpenQty { get; set; }
    public bool DiagnosticOwnershipSnapshotAvailable { get; set; }
    public int DiagnosticOwnershipGrossOpenQty { get; set; }
    public int DiagnosticOwnershipSignedNetQty { get; set; }
    public int DiagnosticOwnershipActiveSlotCount { get; set; }
    public int DiagnosticOwnershipOrphanSlotCount { get; set; }
    public int DiagnosticBrokerWorkingCount { get; set; }
    public int DiagnosticIeaOwnedPlusAdoptedWorking { get; set; }
    public int DiagnosticPendingAdoptionCandidateCount { get; set; }

    /// <summary>Count of blockers whose domain decision is <see cref="ReconciliationDecision.ADOPT"/>.</summary>
    public int DiagnosticAdoptDecisionCount { get; set; }

    /// <summary>Pending execution workload used to distinguish active risk from residual cleanup lag.</summary>
    public int DiagnosticPendingExecutionWorkload { get; set; }

    /// <summary>True when blocker list could not be fully resolved (fail-closed signal).</summary>
    public bool BlockerInvariantViolation { get; set; }

    /// <summary>Per-blocker domain decisions (for audits).</summary>
    public IReadOnlyList<(ReconciliationBlocker Blocker, ReconciliationDecision Decision)>? ResolvedBlockers { get; set; }

    /// <summary>True when pending-adoption counts exist but classifier produced no diagnostics/blockers (modeling gap). Informational — does not by itself veto <see cref="ReleaseReady"/>.</summary>
    public bool LegacyClassifierGap { get; set; }

    /// <summary>True when broker/working exposure is already flat and only journal/adoption retirement residue remains.</summary>
    public bool ResidualCleanupOnly { get; set; }

    /// <summary>Structured residual cleanup class for audits.</summary>
    public string ResidualCleanupClass { get; set; } = ResidualCleanupMismatchClass.None.ToString();
}

public enum ResidualCleanupMismatchClass
{
    None = 0,
    MISMATCH_RESIDUAL_JOURNAL_RETIREMENT = 1,
    MISMATCH_RESIDUAL_ADOPTION_RETIREMENT = 2,
    MISMATCH_RESIDUAL_JOURNAL_AND_ADOPTION_RETIREMENT = 3
}

/// <summary>P1.5-E: Structured result after an instrument-scoped gate reconciliation pass.</summary>
public sealed class GateReconciliationResult
{
    public ReconciliationRunMode Mode { get; set; } = ReconciliationRunMode.GateRecovery;
    public string Instrument { get; set; } = "";
    public ReconciliationOutcomeStatus OutcomeStatus { get; set; }
    public int BrokerWorkingCountBefore { get; set; }
    public int BrokerWorkingCountAfter { get; set; }
    public int IeaOwnedCountBefore { get; set; }
    public int IeaOwnedCountAfter { get; set; }
    public int AdoptionCandidateCountBefore { get; set; }
    public int AdoptionCandidateCountAfter { get; set; }
    public int UnexplainedWorkingCountAfter { get; set; }
    public int UnexplainedPositionQtyAfter { get; set; }
    public bool ReleaseReadyAfter { get; set; }
    public string Reason { get; set; } = "";
    public long DurationMs { get; set; }
    public bool RunnerInvoked { get; set; }
}

/// <summary>P1.5-B: Single release evaluation function (pure; engine supplies inputs).</summary>
public static class StateConsistencyReleaseEvaluator
{
    public static StateConsistencyReleaseReadinessResult Evaluate(StateConsistencyReleaseEvaluationInput i)
    {
        var inst = i.Instrument?.Trim() ?? "";
        var r = new StateConsistencyReleaseReadinessResult { Instrument = inst };

        if (!i.SnapshotSufficient)
        {
            r.SnapshotSufficient = false;
            r.Summary = "snapshot_insufficient";
            r.Contradictions.Add("NO_SNAPSHOT");
            return r;
        }

        var utcSynth = DateTimeOffset.UtcNow;
        IReadOnlyList<ReconciliationBlocker>? blockers = i.ReconciliationBlockers;
        if (blockers == null && i.BlockingCandidateDiagnostics != null && i.BlockingCandidateDiagnostics.Count > 0)
        {
            blockers = i.BlockingCandidateDiagnostics
                .Where(d => d.BlocksRelease)
                .Select(d => ReconciliationBlockerFactory.FromDiagnostic(d, utcSynth))
                .ToList();
        }

        IReadOnlyList<(ReconciliationBlocker Blocker, ReconciliationDecision Decision)> resolved =
            ReconciliationDecisionResolver.ResolveAll(blockers, utcSynth);
        r.ResolvedBlockers = resolved;

        if (!ReconciliationDecisionResolver.ValidateAllBlockersResolved(blockers, resolved, out var invErr) ||
            invErr != null)
        {
            r.BlockerInvariantViolation = true;
            r.Contradictions.Add($"blocker_invariant:{invErr ?? "unresolved"}");
        }

        var adoptCount = resolved.Count(x => x.Decision == ReconciliationDecision.ADOPT);
        r.DiagnosticAdoptDecisionCount = adoptCount;
        r.PendingAdoptionExists = adoptCount > 0;
        if (r.PendingAdoptionExists)
            r.Contradictions.Add("pending_adoption_adopt_decision");

        foreach (var x in resolved)
        {
            var b = x.Blocker;
            var d = x.Decision;
            switch (d)
            {
                case ReconciliationDecision.ESCALATE:
                    if (ReconciliationDecisionResolver.IsTransientRetryExhausted(b, utcSynth))
                        r.Contradictions.Add($"blocker_escalate_transient_retry_exhaustion:{b.ReasonCode}:{b.IntentId}");
                    else
                        r.Contradictions.Add($"blocker_escalate:{b.ReasonCode}:{b.IntentId}");
                    break;
                case ReconciliationDecision.ALTERNATE_LANE:
                    r.Contradictions.Add(
                        $"blocker_alternate_lane:{b.ReasonCode}:{b.IntentId}:lane={(int)b.LaneType}:owner={b.ResolutionOwner}:terminal={b.IsTerminal?.ToString() ?? "unknown"}");
                    break;
                case ReconciliationDecision.ADOPT:
                    r.Contradictions.Add($"blocker_adopt:{b.ReasonCode}:{b.IntentId}");
                    break;
                case ReconciliationDecision.RETRY:
                    r.Contradictions.Add($"blocker_retry:{b.ReasonCode}:{b.IntentId}");
                    break;
            }
        }

        static bool BlocksReleaseByDecision(ReconciliationDecision d) =>
            d is ReconciliationDecision.ADOPT
                or ReconciliationDecision.RETRY
                or ReconciliationDecision.ESCALATE
                or ReconciliationDecision.ALTERNATE_LANE;

        // Phase 2 policy: classifier-backed blockers (resolved ADOPT/RETRY/ESCALATE/ALTERNATE_LANE) hold release.
        // Legacy "pending adoption count without diagnostics" is observability only — not a structural release veto.
        var legacyClassifierGap = (blockers == null || blockers.Count == 0) &&
                                  (i.BlockingCandidateDiagnostics == null ||
                                   i.BlockingCandidateDiagnostics.Count == 0) &&
                                  i.PendingAdoptionCandidateCount > 0;
        r.LegacyClassifierGap = legacyClassifierGap;
        var hasStructuralBlocker = resolved.Any(x => BlocksReleaseByDecision(x.Decision));
        if (legacyClassifierGap)
        {
            r.Contradictions.Add("info_legacy_classifier_gap_pending_adoption");
            if (i.AdoptablePendingAdoptionCandidateCount > 0)
                r.PendingAdoptionExists = true;
        }

        var ieaOk = i.IeaOwnedPlusAdoptedWorking >= 0;
        if (!ieaOk && i.UseInstrumentExecutionAuthority)
        {
            r.Contradictions.Add("iea_unavailable");
            r.LocalStateCoherent = false;
        }
        else
            r.LocalStateCoherent = true;

        if (i.PendingExecutionWorkload > 0)
        {
            r.LocalStateCoherent = false;
            r.Contradictions.Add($"pending_execution_workload_{i.PendingExecutionWorkload}");
        }

        if (HasActiveOwnershipGross(i) && i.BrokerPositionQty == 0)
        {
            r.LocalStateCoherent = false;
            r.Contradictions.Add(
                $"ownership_gross_open_on_broker_net_flat:qty={i.OwnershipGrossOpenQty}:active_slots={i.OwnershipActiveSlotCount}:orphan_slots={i.OwnershipOrphanSlotCount}");
        }

        var posDiff = Math.Abs(i.BrokerPositionQty - i.JournalOpenQty);
        r.UnexplainedBrokerPositionQty = posDiff;
        var positionToleratedPending = i.PendingAlignmentActive && ieaOk && posDiff > 0;
        r.BrokerPositionExplainable = posDiff == 0 || positionToleratedPending;
        if (posDiff > 0)
        {
            if (positionToleratedPending)
                r.Contradictions.Add($"info_pending_alignment_position_qty_delta_{posDiff}");
            else
                r.Contradictions.Add($"position_qty_delta_{posDiff}");
        }

        int ieaWorking = ieaOk ? i.IeaOwnedPlusAdoptedWorking : 0;
        var unexplainedW = ieaOk ? Math.Max(0, i.BrokerWorkingCount - ieaWorking) : i.BrokerWorkingCount;
        r.UnexplainedBrokerWorkingCount = unexplainedW;

        var adoptOrRetryPending = resolved.Any(x =>
            x.Decision is ReconciliationDecision.ADOPT or ReconciliationDecision.RETRY);
        if (i.BrokerWorkingCount > 0 && ieaWorking == 0 && !adoptOrRetryPending && ieaOk)
        {
            r.Contradictions.Add("broker_working_without_iea_coverage");
            r.LocalStateCoherent = false;
        }

        r.BrokerWorkingExplainable = ieaOk && unexplainedW == 0;
        if (!r.BrokerWorkingExplainable && ieaOk)
            r.Contradictions.Add($"unexplained_working_{unexplainedW}");

        r.IsExplainable = r.BrokerPositionExplainable && r.BrokerWorkingExplainable && r.LocalStateCoherent &&
                          r.UnexplainedBrokerWorkingCount == 0;

        if (r.BlockerInvariantViolation)
            r.IsExplainable = false;

        var releaseReady = r.IsExplainable && !hasStructuralBlocker && !r.BlockerInvariantViolation;
        var residualCleanupClass = ClassifyResidualCleanup(i, r, resolved);
        r.ResidualCleanupOnly = residualCleanupClass != ResidualCleanupMismatchClass.None;
        r.ResidualCleanupClass = residualCleanupClass.ToString();
        if (!releaseReady && r.ResidualCleanupOnly)
        {
            releaseReady = true;
            r.Contradictions.Add($"info_residual_cleanup:{r.ResidualCleanupClass}");
        }
        if (!releaseReady && IsBrokerFlatSoftTransitionOnlyRelease(i, r, resolved))
        {
            releaseReady = true;
            r.Contradictions.Add("info_soft_transition_broker_flat_release");
        }

        r.ReleaseReady = releaseReady;
        r.Summary = r.ReleaseReady
            ? (r.ResidualCleanupOnly
                ? $"release_ready_residual_cleanup:{r.ResidualCleanupClass}"
                : r.Contradictions.Contains("info_soft_transition_broker_flat_release")
                ? "release_ready_soft_transition_broker_flat"
                : "release_ready")
            : string.Join(";", r.Contradictions);

        r.SnapshotSufficient = true;
        r.DiagnosticBrokerPositionQty = i.BrokerPositionQty;
        r.DiagnosticJournalOpenQty = i.JournalOpenQty;
        r.DiagnosticOwnershipSnapshotAvailable = i.OwnershipSnapshotAvailable;
        r.DiagnosticOwnershipGrossOpenQty = i.OwnershipGrossOpenQty;
        r.DiagnosticOwnershipSignedNetQty = i.OwnershipSignedNetQty;
        r.DiagnosticOwnershipActiveSlotCount = i.OwnershipActiveSlotCount;
        r.DiagnosticOwnershipOrphanSlotCount = i.OwnershipOrphanSlotCount;
        r.DiagnosticBrokerWorkingCount = i.BrokerWorkingCount;
        r.DiagnosticIeaOwnedPlusAdoptedWorking = i.IeaOwnedPlusAdoptedWorking;
        r.DiagnosticPendingAdoptionCandidateCount = adoptCount;
        r.DiagnosticAdoptDecisionCount = adoptCount;
        r.DiagnosticPendingExecutionWorkload = i.PendingExecutionWorkload;
        return r;
    }

    public static StateConsistencyReleaseReadinessResult Indeterminate(string instrument, string reason = "evaluate_callback_unavailable")
    {
        return new StateConsistencyReleaseReadinessResult
        {
            Instrument = instrument?.Trim() ?? "",
            SnapshotSufficient = false,
            Summary = reason,
            Contradictions = new List<string> { reason }
        };
    }

    private static bool IsBrokerFlatSoftTransitionOnlyRelease(
        StateConsistencyReleaseEvaluationInput input,
        StateConsistencyReleaseReadinessResult readiness,
        IReadOnlyList<(ReconciliationBlocker Blocker, ReconciliationDecision Decision)> resolved)
    {
        if (resolved.Count == 0 || readiness.BlockerInvariantViolation)
            return false;

        if (input.BrokerPositionQty != 0 || input.BrokerWorkingCount != 0)
            return false;

        if (input.UseInstrumentExecutionAuthority && input.IeaOwnedPlusAdoptedWorking > 0)
            return false;
        if (HasActiveOwnershipGross(input))
            return false;

        if (!readiness.BrokerPositionExplainable || !readiness.BrokerWorkingExplainable ||
            !readiness.LocalStateCoherent || readiness.UnexplainedBrokerPositionQty != 0 ||
            readiness.UnexplainedBrokerWorkingCount != 0)
            return false;

        foreach (var x in resolved)
        {
            switch (x.Decision)
            {
                case ReconciliationDecision.IGNORE:
                    continue;
                case ReconciliationDecision.ADOPT:
                case ReconciliationDecision.RETRY:
                    if (!IsSoftTransitionBlocker(x.Blocker.ReasonCode))
                        return false;
                    if (x.Blocker.ReasonCode == ReconciliationBlockerReasonCode.JournalOnlyBrokerFlat &&
                        input.JournalOpenQty <= 0)
                        return false;
                    continue;
                case ReconciliationDecision.ALTERNATE_LANE:
                    if (x.Blocker.IsTerminal == true || !IsSoftTransitionBlocker(x.Blocker.ReasonCode))
                        return false;
                    if (x.Blocker.ReasonCode == ReconciliationBlockerReasonCode.JournalOnlyBrokerFlat &&
                        input.JournalOpenQty <= 0)
                        return false;
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    private static ResidualCleanupMismatchClass ClassifyResidualCleanup(
        StateConsistencyReleaseEvaluationInput input,
        StateConsistencyReleaseReadinessResult readiness,
        IReadOnlyList<(ReconciliationBlocker Blocker, ReconciliationDecision Decision)> resolved)
    {
        if (input.PendingExecutionWorkload != 0)
            return ResidualCleanupMismatchClass.None;
        if (input.BrokerPositionQty != 0 || input.BrokerWorkingCount != 0)
            return ResidualCleanupMismatchClass.None;
        if (input.UseInstrumentExecutionAuthority && input.IeaOwnedPlusAdoptedWorking > 0)
            return ResidualCleanupMismatchClass.None;
        if (HasActiveOwnershipGross(input))
            return ResidualCleanupMismatchClass.None;
        var brokerFlatJournalResidual = input.BrokerPositionQty == 0 && input.JournalOpenQty > 0;
        if ((!readiness.BrokerPositionExplainable && !brokerFlatJournalResidual) ||
            !readiness.BrokerWorkingExplainable ||
            !readiness.LocalStateCoherent)
            return ResidualCleanupMismatchClass.None;
        if ((readiness.UnexplainedBrokerPositionQty != 0 && !brokerFlatJournalResidual) ||
            readiness.UnexplainedBrokerWorkingCount != 0)
            return ResidualCleanupMismatchClass.None;

        var hasJournalResidual = input.JournalOpenQty > 0;
        var hasAdoptionResidual = false;
        foreach (var x in resolved)
        {
            switch (x.Decision)
            {
                case ReconciliationDecision.IGNORE:
                    continue;
                case ReconciliationDecision.ESCALATE:
                    return ResidualCleanupMismatchClass.None;
                case ReconciliationDecision.ADOPT:
                case ReconciliationDecision.RETRY:
                case ReconciliationDecision.ALTERNATE_LANE:
                    if (!IsSoftTransitionBlocker(x.Blocker.ReasonCode))
                        return ResidualCleanupMismatchClass.None;
                    if (x.Blocker.ReasonCode == ReconciliationBlockerReasonCode.JournalOnlyBrokerFlat)
                    {
                        if (input.JournalOpenQty <= 0)
                            return ResidualCleanupMismatchClass.None;
                        hasJournalResidual = true;
                    }
                    else
                        hasAdoptionResidual = true;
                    continue;
                default:
                    return ResidualCleanupMismatchClass.None;
            }
        }

        if (!hasJournalResidual && !hasAdoptionResidual)
            return ResidualCleanupMismatchClass.None;

        if (hasJournalResidual && hasAdoptionResidual)
            return ResidualCleanupMismatchClass.MISMATCH_RESIDUAL_JOURNAL_AND_ADOPTION_RETIREMENT;
        if (hasJournalResidual)
            return ResidualCleanupMismatchClass.MISMATCH_RESIDUAL_JOURNAL_RETIREMENT;
        return ResidualCleanupMismatchClass.MISMATCH_RESIDUAL_ADOPTION_RETIREMENT;
    }

    private static bool IsSoftTransitionBlocker(ReconciliationBlockerReasonCode reasonCode) =>
        reasonCode is ReconciliationBlockerReasonCode.BrokerVisibleAdoptableExposure
            or ReconciliationBlockerReasonCode.TagMismatchExposure
            or ReconciliationBlockerReasonCode.JournalOnlyBrokerFlat
            or ReconciliationBlockerReasonCode.AlreadyOwnedElsewhere
            or ReconciliationBlockerReasonCode.StaleSnapshot;

    private static bool HasActiveOwnershipGross(StateConsistencyReleaseEvaluationInput input) =>
        input.OwnershipGrossOpenQty > 0 ||
        input.OwnershipActiveSlotCount > 0 ||
        input.OwnershipOrphanSlotCount > 0;
}

/// <summary>Policy for whether forced-convergence failure should also persist a restart-durable risk latch.</summary>
public static class ForcedConvergenceRiskLatchPolicy
{
    public static bool ShouldPersistDurableRiskLatch(StateConsistencyReleaseReadinessResult? readiness)
    {
        if (readiness == null || readiness.ReleaseReady || !readiness.SnapshotSufficient)
            return false;

        if (readiness.LegacyClassifierGap)
            return false;

        if (readiness.BlockerInvariantViolation)
            return true;

        var resolved = readiness.ResolvedBlockers;
        if (resolved != null && resolved.Count > 0)
        {
            foreach (var x in resolved)
            {
                if (x.Decision == ReconciliationDecision.ESCALATE)
                    return true;

                if (x.Decision == ReconciliationDecision.ALTERNATE_LANE && x.Blocker.IsTerminal == true)
                    return true;
            }

            var onlyRecoverableBlockers = resolved.All(x =>
                x.Decision is ReconciliationDecision.IGNORE
                    or ReconciliationDecision.ADOPT
                    or ReconciliationDecision.RETRY ||
                (x.Decision == ReconciliationDecision.ALTERNATE_LANE && x.Blocker.IsTerminal != true));
            if (onlyRecoverableBlockers)
                return false;
        }

        var hasUnexplainedPositionWithoutBridge = readiness.UnexplainedBrokerPositionQty > 0 &&
                                                 readiness.DiagnosticBrokerWorkingCount == 0 &&
                                                 readiness.DiagnosticIeaOwnedPlusAdoptedWorking == 0 &&
                                                 !readiness.PendingAdoptionExists;
        if (hasUnexplainedPositionWithoutBridge)
            return true;

        var hasUnexplainedWorkingWithoutResolutionLane = readiness.UnexplainedBrokerWorkingCount > 0 &&
                                                         !readiness.PendingAdoptionExists &&
                                                         (resolved == null || resolved.Count == 0 ||
                                                          resolved.All(x => x.Decision == ReconciliationDecision.IGNORE));
        if (hasUnexplainedWorkingWithoutResolutionLane)
            return true;

        return false;
    }
}

/// <summary>P1.5-G: Allowed vs blocked actions while gate engaged (documentation + event tags).</summary>
public static class StateConsistencyGateActionPolicy
{
    public const string CategoryBlockedNewRisk = "BLOCK_NEW_RISK";
    public const string CategoryAllowedReadOnly = "ALLOW_READ_ONLY";
    public const string CategoryAllowedOwnership = "ALLOW_OWNERSHIP_ESTABLISHMENT";
    public const string CategoryEmergencyOnly = "EMERGENCY_ONLY";

    /// <summary>Human-readable policy reference for logs.</summary>
    public const string PolicyNote =
        "While STATE_CONSISTENCY_GATE engaged: block new entries/resubmits; allow snapshot refresh, adoption scan, gate reconciliation (non-destructive), stability evaluation; destructive flatten/cancel only via emergency path.";
}
