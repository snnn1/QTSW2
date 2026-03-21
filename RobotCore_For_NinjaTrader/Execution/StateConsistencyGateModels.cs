// P1.5: State-consistency gate — release readiness, reconciliation outcomes, action policy.

using System;
using System.Collections.Generic;

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
    public int PendingAdoptionCandidateCount { get; set; }
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
            r.Summary = "snapshot_insufficient";
            r.Contradictions.Add("NO_SNAPSHOT");
            return r;
        }

        var pending = i.PendingAdoptionCandidateCount > 0;
        r.PendingAdoptionExists = pending;
        if (pending)
            r.Contradictions.Add("pending_adoption_candidates");

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

        if (i.BrokerWorkingCount > 0 && ieaWorking == 0 && !pending && ieaOk)
        {
            r.Contradictions.Add("broker_working_without_iea_coverage");
            r.LocalStateCoherent = false;
        }

        r.BrokerWorkingExplainable = ieaOk && unexplainedW == 0;
        if (!r.BrokerWorkingExplainable && ieaOk)
            r.Contradictions.Add($"unexplained_working_{unexplainedW}");

        r.IsExplainable = r.BrokerPositionExplainable && r.BrokerWorkingExplainable && r.LocalStateCoherent && !pending &&
                          r.UnexplainedBrokerPositionQty == 0 && r.UnexplainedBrokerWorkingCount == 0;

        r.ReleaseReady = r.IsExplainable && !pending;
        r.Summary = r.ReleaseReady
            ? "release_ready"
            : string.Join(";", r.Contradictions);
        return r;
    }

    public static StateConsistencyReleaseReadinessResult Indeterminate(string instrument, string reason = "evaluate_callback_unavailable")
    {
        return new StateConsistencyReleaseReadinessResult
        {
            Instrument = instrument?.Trim() ?? "",
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
