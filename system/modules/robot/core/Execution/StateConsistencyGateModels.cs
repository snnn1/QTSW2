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
    /// <summary>Open journal remaining quantity (same semantics as mismatch assembly).</summary>
    public int JournalOpenQty { get; set; }
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

    /// <summary>False when evaluation returned before full inputs (e.g. snapshot missing).</summary>
    public bool SnapshotSufficient { get; set; }

    /// <summary>Mirrors inputs used for Evaluate (diagnostics / gate telemetry).</summary>
    public int DiagnosticBrokerPositionQty { get; set; }
    public int DiagnosticJournalOpenQty { get; set; }
    public int DiagnosticBrokerWorkingCount { get; set; }
    public int DiagnosticIeaOwnedPlusAdoptedWorking { get; set; }
    public int DiagnosticPendingAdoptionCandidateCount { get; set; }

    /// <summary>Count of blockers whose domain decision is <see cref="ReconciliationDecision.ADOPT"/>.</summary>
    public int DiagnosticAdoptDecisionCount { get; set; }

    /// <summary>True when blocker list could not be fully resolved (fail-closed signal).</summary>
    public bool BlockerInvariantViolation { get; set; }

    /// <summary>Per-blocker domain decisions (for audits).</summary>
    public IReadOnlyList<(ReconciliationBlocker Blocker, ReconciliationDecision Decision)>? ResolvedBlockers { get; set; }

    /// <summary>True when pending-adoption counts exist but classifier produced no diagnostics/blockers (modeling gap).</summary>
    public bool LegacyClassifierGap { get; set; }
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

        var legacyClassifierGap = (blockers == null || blockers.Count == 0) &&
                                  (i.BlockingCandidateDiagnostics == null ||
                                   i.BlockingCandidateDiagnostics.Count == 0) &&
                                  i.PendingAdoptionCandidateCount > 0;
        r.LegacyClassifierGap = legacyClassifierGap;
        var hasStructuralBlocker = resolved.Any(x => BlocksReleaseByDecision(x.Decision)) || legacyClassifierGap;
        if (legacyClassifierGap)
        {
            r.Contradictions.Add("release_blocker_legacy_count_without_classifier");
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

        var posDiff = Math.Abs(i.BrokerPositionQty - i.JournalOpenQty);
        r.UnexplainedBrokerPositionQty = posDiff;
        r.BrokerPositionExplainable = posDiff == 0;
        if (!r.BrokerPositionExplainable)
            r.Contradictions.Add($"position_qty_delta_{posDiff}");

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
                          r.UnexplainedBrokerPositionQty == 0 && r.UnexplainedBrokerWorkingCount == 0;

        if (r.BlockerInvariantViolation)
            r.IsExplainable = false;

        r.ReleaseReady = r.IsExplainable && !hasStructuralBlocker && !r.BlockerInvariantViolation;
        r.Summary = r.ReleaseReady
            ? "release_ready"
            : string.Join(";", r.Contradictions);

        r.SnapshotSufficient = true;
        r.DiagnosticBrokerPositionQty = i.BrokerPositionQty;
        r.DiagnosticJournalOpenQty = i.JournalOpenQty;
        r.DiagnosticBrokerWorkingCount = i.BrokerWorkingCount;
        r.DiagnosticIeaOwnedPlusAdoptedWorking = i.IeaOwnedPlusAdoptedWorking;
        r.DiagnosticPendingAdoptionCandidateCount = adoptCount;
        r.DiagnosticAdoptDecisionCount = adoptCount;
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
