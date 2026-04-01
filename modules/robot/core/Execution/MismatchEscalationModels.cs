// Gap 4: Persistent Mismatch Escalation — models and policy thresholds.
// Detects when broker truth, journals, registry, lifecycle, or recovery state fail to converge.
// Bounded detection and escalation only; no retry loops or auto-repair in first pass.

using System;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Explicit mismatch categories. Do not collapse into one generic mismatch.</summary>
public enum MismatchType
{
    /// <summary>Broker position/order state indicates exposure or working orders not reflected in local journal/intent state.</summary>
    BROKER_AHEAD,

    /// <summary>Local journal/intent state claims exposure or progress that broker state does not support.</summary>
    JOURNAL_AHEAD,

    /// <summary>Broker quantity and reconstructed local quantity differ (both nonzero).</summary>
    POSITION_QTY_MISMATCH,

    /// <summary>Broker working order exists but local registry/order map does not reliably own or classify it.</summary>
    ORDER_REGISTRY_MISSING,

    /// <summary>Lifecycle or local state says protectives exist or are active, but broker-side protection is missing or contradictory.</summary>
    PROTECTIVE_STATE_DIVERGENCE,

    /// <summary>Execution could not be resolved and mismatch remains beyond acceptable window.</summary>
    UNKNOWN_EXECUTION_PERSISTENT,

    /// <summary>Startup or restart reconstruction could not classify the instrument into a clean stable state.</summary>
    RESTART_RECONCILIATION_UNRESOLVED,

    /// <summary>Lifecycle state and broker-observed state are materially inconsistent.</summary>
    LIFECYCLE_BROKER_DIVERGENCE,

    /// <summary>Fallback category for severe mismatch that cannot be classified more specifically.</summary>
    UNCLASSIFIED_CRITICAL_MISMATCH
}

/// <summary>Per-instrument mismatch escalation state machine.</summary>
public enum MismatchEscalationState
{
    NONE,
    DETECTED,
    PERSISTENT_MISMATCH,
    FAIL_CLOSED
}

/// <summary>Observation payload supplied by RobotEngine to the coordinator.</summary>
public sealed class MismatchObservation
{
    public string Instrument { get; set; } = "";
    public MismatchType MismatchType { get; set; }
    public bool Present { get; set; }
    public string? Summary { get; set; }
    public int BrokerQty { get; set; }
    public int LocalQty { get; set; }
    public int BrokerWorkingOrderCount { get; set; }
    public int LocalWorkingOrderCount { get; set; }
    /// <summary>Open journal rows (intents) for this instrument keys; for audit "journal_orders".</summary>
    public int JournalOpenEntryCount { get; set; }
    /// <summary>Comma-separated intent ids from open journal for this instrument (audit linkage).</summary>
    public string? IntentIdsCsv { get; set; }
    /// <summary>Optional: IEA-derived or engine position when plumbed; else omit for TSV "na".</summary>
    public int? RegistryPositionQty { get; set; }
    /// <summary>Optional: stream/runtime expected position when plumbed; else TSV "na".</summary>
    public int? RuntimePositionQty { get; set; }
    /// <summary>Optional: runtime working-order proxy when plumbed; else TSV "na".</summary>
    public int? RuntimeOrderCount { get; set; }
    public string? LifecycleState { get; set; }
    public string? JournalState { get; set; }
    public DateTimeOffset ObservedUtc { get; set; }
    public string Severity { get; set; } = "CRITICAL";
}

/// <summary>Per-instrument mismatch state (coordinator-owned).</summary>
public sealed class MismatchInstrumentState
{
    public MismatchType MismatchType { get; set; }
    public MismatchEscalationState EscalationState { get; set; }
    public DateTimeOffset FirstDetectedUtc { get; set; }
    public DateTimeOffset LastDetectedUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public int RetryCount { get; set; }
    public bool Blocked { get; set; }
    public string BlockReason { get; set; } = "";
    public int ConsecutiveCleanPassCount { get; set; }
    public DateTimeOffset LastResolutionAttemptUtc { get; set; }
    public long PersistenceMs { get; set; }
    public string? LastSummary { get; set; }
    public bool MismatchStillPresent { get; set; }

    /// <summary>P1.5-A: Gate lifecycle (instrument-scoped). <see cref="GateLifecyclePhase.None"/> when gate not active.</summary>
    public GateLifecyclePhase GateLifecyclePhase { get; set; }

    /// <summary>Start of continuous release-ready window (UTC).</summary>
    public DateTimeOffset FirstConsistentUtc { get; set; }

    public DateTimeOffset LastConsistentUtc { get; set; }
    public DateTimeOffset LastReleaseUtc { get; set; }
    public DateTimeOffset LastGateEngagedUtc { get; set; }
    public int StableWindowMsApplied { get; set; }

    /// <summary>Progress-aware throttling for expensive gate reconciliation (local to coordinator).</summary>
    public GateReconciliationProgressState GateProgress { get; } = new GateReconciliationProgressState();

    /// <summary>Bounded convergence episode for this instrument (broker-truth escalation).</summary>
    public ReconciliationConvergenceEpisodeTracker ConvergenceEpisode { get; } = new();

    /// <summary>Suppress expensive gate reconciliation until this UTC (post forced alignment hysteresis).</summary>
    public DateTimeOffset ReconciliationHysteresisUntilUtc { get; set; }

    /// <summary>Mismatch class at hysteresis start — new class clears hysteresis early.</summary>
    public MismatchType? HysteresisMismatchTypeAtFreeze { get; set; }

    /// <summary>Broker snapshot fingerprint after successful forced alignment; stall expensive while unchanged.</summary>
    public ulong PostForcedConvergenceFingerprint { get; set; }

    public bool ForcedConvergenceSucceeded { get; set; }
}

/// <summary>Per-instrument reconciliation episode (attempt / no-progress bounds).</summary>
public sealed class ReconciliationConvergenceEpisodeTracker
{
    public ulong EpisodeId { get; private set; }
    public int AttemptCount { get; set; }
    public int NoProgressStreak { get; set; }
    public DateTimeOffset FirstSeenUtc { get; private set; }
    public DateTimeOffset? LastProgressUtc { get; set; }
    public string? LastActionAttempted { get; set; }
    /// <summary>Intent scope for this episode (<see cref="MismatchObservation.IntentIdsCsv"/>).</summary>
    public string? EpisodeIntentKey { get; private set; }

    public void StartNew(ulong episodeId, DateTimeOffset utcNow, string? intentKey = null)
    {
        EpisodeId = episodeId;
        AttemptCount = 0;
        NoProgressStreak = 0;
        FirstSeenUtc = utcNow;
        LastProgressUtc = null;
        LastActionAttempted = null;
        EpisodeIntentKey = intentKey;
    }

    public void Clear()
    {
        EpisodeId = 0;
        AttemptCount = 0;
        NoProgressStreak = 0;
        FirstSeenUtc = default;
        LastProgressUtc = null;
        LastActionAttempted = null;
        EpisodeIntentKey = null;
    }
}

/// <summary>Input to broker-truth forced alignment (engine provides implementation).</summary>
public sealed class ReconciliationForcedConvergenceContext
{
    public string Instrument { get; init; } = "";
    public string? IntentIdsCsv { get; init; }
    public string LimitReason { get; init; } = "";
    public int Attempts { get; init; }
    public int NoProgressCount { get; init; }
}

/// <summary>Result of broker-authoritative convergence attempt.</summary>
public sealed class ReconciliationForcedConvergenceResult
{
    public bool AlignedWithBroker { get; init; }
    public string? FailureReason { get; init; }
    public ulong PostAlignmentFingerprint { get; init; }

    public static ReconciliationForcedConvergenceResult Succeeded(ulong fingerprint) =>
        new() { AlignedWithBroker = true, PostAlignmentFingerprint = fingerprint };

    public static ReconciliationForcedConvergenceResult Failed(string reason) =>
        new() { AlignedWithBroker = false, FailureReason = reason };

    /// <summary>Harness / no handler: soft success — no fail-closed.</summary>
    public static ReconciliationForcedConvergenceResult NoHandler() =>
        new() { AlignedWithBroker = true, FailureReason = "no_alignment_handler", PostAlignmentFingerprint = 0 };
}

/// <summary>Policy thresholds for mismatch escalation.</summary>
public static class MismatchEscalationPolicy
{
    public const int MISMATCH_AUDIT_INTERVAL_MS = 5000;

    /// <summary>Full mismatch audit cadence when gate/mismatch/snapshot/activity indicates work in flight.</summary>
    public const int MISMATCH_AUDIT_INTERVAL_ACTIVE_MS = 5000;

    /// <summary>Idle cadence when broker snapshot + execution generation are stable and no gate/escalation.</summary>
    public const int MISMATCH_AUDIT_INTERVAL_IDLE_MS = 30_000;

    /// <summary>Upper bound on wall time between audits in idle mode (wake + mandatory full pass).</summary>
    public const int MISMATCH_AUDIT_MANDATORY_MAX_GAP_MS = 90_000;
    public const int MISMATCH_PERSISTENT_THRESHOLD_MS = 10000;
    public const int MISMATCH_FAIL_CLOSED_THRESHOLD_MS = 30000;
    public const int MISMATCH_CLEAR_CONSECUTIVE_CLEAN_PASSES = 2;
    public const int MISMATCH_MAX_RETRIES = 3;

    /// <summary>Block reason for persistent mismatch. Distinguishable from protective, queue poison, supervisory.</summary>
    public const string BLOCK_REASON_PERSISTENT_MISMATCH = "PERSISTENT_RECONCILIATION_MISMATCH";

    /// <summary>
    /// Block new risk immediately on first mismatch detection (before persistence threshold).
    /// Escalation still advances to PERSISTENT_MISMATCH for logging/metrics; block reason then upgrades.
    /// </summary>
    public const string BLOCK_REASON_STATE_CONSISTENCY_GATE = "STATE_CONSISTENCY_GATE";

    /// <summary>P1.5-C: Minimum continuous time release invariants must hold before unblock (live default).</summary>
    public const int STATE_CONSISTENCY_STABLE_WINDOW_MS_LIVE = 10_000;

    /// <summary>P1.5-C: Slightly longer window when snapshots/noise warrant (sim / harness).</summary>
    public const int STATE_CONSISTENCY_STABLE_WINDOW_MS_SIM = 12_000;

    // --- Progress-aware gate reconciliation throttle (no zero-progress CPU loops) ---

    /// <summary>First N expensive gate reconciliation passes run at full audit frequency (no iteration-based throttle).</summary>
    public const int GATE_PROGRESS_WARMUP_EXPENSIVE_PASSES = 2;

    /// <summary>Consecutive no-progress expensive passes before iteration-based throttle can apply (after warmup).</summary>
    public const int GATE_NO_PROGRESS_ITERATION_THRESHOLD = 3;

    /// <summary>Time without measurable progress (ms) before throttle can apply.</summary>
    public const int GATE_NO_PROGRESS_TIME_THRESHOLD_MS = 12_000;

    /// <summary>Minimum spacing between expensive reconciliations once throttled (base; multiplied by backoff).</summary>
    public const int GATE_RECONCILIATION_MIN_INTERVAL_MS = 6_000;

    /// <summary>Backoff multiplier applied per active throttle step (stepped exponential).</summary>
    public const double GATE_RECONCILIATION_BACKOFF_MULTIPLIER = 1.5;

    /// <summary>Maximum spacing cap for throttled reconciliation (ms).</summary>
    public const int GATE_RECONCILIATION_MAX_INTERVAL_MS = 60_000;

    // --- Progress-bounded execution + hard reconciliation limits ---

    /// <summary>Max expensive gate reconciliations per execution burst (reset when execution gap exceeds <see cref="GATE_EXECUTION_TRIGGER_RESET_GAP_MS"/>).</summary>
    public const int GATE_EXECUTION_RECONCILIATION_CAP = 5;

    /// <summary>Fallback: reset execution burst counter when no trigger for this long (ms) and no structured details supplied.</summary>
    public const int GATE_EXECUTION_TRIGGER_RESET_GAP_MS = 1000;

    /// <summary>Max expensive gate reconciliations for the whole time the gate is engaged (progress-independent ceiling).</summary>
    public const int GATE_ABSOLUTE_MAX_EXPENSIVE_RECONCILIATIONS = 10;

    /// <summary>Hard stop when no measurable progress for this many consecutive expensive passes (after warmup).</summary>
    public const int GATE_HARD_STOP_NO_PROGRESS_ITERATIONS = 5;

    /// <summary>Hard stop also requires at least this long since last measurable progress (ms).</summary>
    public const int GATE_HARD_STOP_NO_PROGRESS_TIME_MS = 15_000;

    /// <summary>Duration to force skip expensive reconciliation after hard stop (ms).</summary>
    public const int GATE_HARD_STOP_DURATION_MS = 30_000;

    /// <summary>Minimum gap between expensive passes to block hot re-entry (ms).</summary>
    public const int GATE_REENTRY_BLOCK_MS = 50;

    // --- Reconciliation convergence engine (bounded episodes; broker as authority) ---

    /// <summary>Max expensive gate reconciliations per episode before broker-truth escalation.</summary>
    public const int RECONCILIATION_CONVERGENCE_MAX_ATTEMPTS = 5;

    /// <summary>Max consecutive expensive passes without progress-signature change before escalation.</summary>
    public const int RECONCILIATION_CONVERGENCE_MAX_NO_PROGRESS = 3;

    /// <summary>Max episode wall duration before escalation (ms).</summary>
    public const int RECONCILIATION_CONVERGENCE_MAX_DURATION_MS = 2000;

    /// <summary>After forced convergence, block expensive reconciliation briefly (anti-oscillation).</summary>
    public const int RECONCILIATION_CONVERGENCE_HYSTERESIS_MS = 1500;
}

/// <summary>
/// Structured execution activity for <see cref="MismatchEscalationCoordinator.NotifyExecutionTrigger"/>.
/// Resets the per-burst execution reconciliation counter on intent change, entry→protectives transition,
/// non-zero fill delta, or broker position quantity change — not only on time gaps.
/// </summary>
public readonly struct MismatchExecutionTriggerDetails : IEquatable<MismatchExecutionTriggerDetails>
{
    public string? IntentId { get; init; }
    /// <summary>Signed fill delta for this execution event (0 if N/A).</summary>
    public int FillDelta { get; init; }
    /// <summary>Current account position quantity for the instrument (optional; used to detect net position changes).</summary>
    public int? InstrumentPositionQty { get; init; }
    /// <summary>True once when protective stop+target are successfully submitted after an entry fill.</summary>
    public bool EntryToProtectivesTransition { get; init; }

    public static MismatchExecutionTriggerDetails Default => default;

    public bool Equals(MismatchExecutionTriggerDetails other) =>
        IntentId == other.IntentId &&
        FillDelta == other.FillDelta &&
        InstrumentPositionQty == other.InstrumentPositionQty &&
        EntryToProtectivesTransition == other.EntryToProtectivesTransition;

    public override bool Equals(object? obj) => obj is MismatchExecutionTriggerDetails o && Equals(o);

    public override int GetHashCode()
    {
        unchecked
        {
            var h = IntentId != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(IntentId) : 0;
            h = (h * 397) ^ FillDelta;
            h = (h * 397) ^ (InstrumentPositionQty?.GetHashCode() ?? 0);
            h = (h * 397) ^ (EntryToProtectivesTransition ? 1 : 0);
            return h;
        }
    }
}

/// <summary>Compact, stable progress signature for gate reconciliation (no timestamps).</summary>
public readonly struct GateProgressSignature : IEquatable<GateProgressSignature>
{
    public GateProgressSignature(
        GateLifecyclePhase gatePhase,
        MismatchType mismatchType,
        int brokerWorking,
        int localOwnedWorking,
        bool releaseReady,
        int unexplainedWorkingGap)
    {
        GatePhase = gatePhase;
        MismatchType = mismatchType;
        BrokerWorking = brokerWorking;
        LocalOwnedWorking = localOwnedWorking;
        ReleaseReady = releaseReady;
        UnexplainedWorkingGap = unexplainedWorkingGap;
    }

    public GateLifecyclePhase GatePhase { get; }
    public MismatchType MismatchType { get; }
    public int BrokerWorking { get; }
    public int LocalOwnedWorking { get; }
    public bool ReleaseReady { get; }
    /// <summary>max(0, brokerWorking - localOwnedWorking) when both meaningful.</summary>
    public int UnexplainedWorkingGap { get; }

    public bool Equals(GateProgressSignature other) =>
        GatePhase == other.GatePhase &&
        MismatchType == other.MismatchType &&
        BrokerWorking == other.BrokerWorking &&
        LocalOwnedWorking == other.LocalOwnedWorking &&
        ReleaseReady == other.ReleaseReady &&
        UnexplainedWorkingGap == other.UnexplainedWorkingGap;

    public override bool Equals(object? obj) => obj is GateProgressSignature o && Equals(o);

    public override int GetHashCode()
    {
        unchecked
        {
            var h = (int)GatePhase;
            h = (h * 397) ^ (int)MismatchType;
            h = (h * 397) ^ BrokerWorking;
            h = (h * 397) ^ LocalOwnedWorking;
            h = (h * 397) ^ (ReleaseReady ? 1 : 0);
            h = (h * 397) ^ UnexplainedWorkingGap;
            return h;
        }
    }

    public static bool operator ==(GateProgressSignature a, GateProgressSignature b) => a.Equals(b);
    public static bool operator !=(GateProgressSignature a, GateProgressSignature b) => !a.Equals(b);
}

/// <summary>Per-instrument progress throttle state (coordinator-owned; gate path only).</summary>
public sealed class GateReconciliationProgressState
{
    public GateProgressSignature? LastProgressSignature { get; set; }
    public int NoProgressIterations { get; set; }
    public DateTimeOffset NextAllowedExpensiveReconciliationUtc { get; set; } = DateTimeOffset.MinValue;
    public int BackoffLevel { get; set; }
    public ulong LastExternalFingerprint { get; set; }
    public DateTimeOffset LastExpensiveReconciliationUtc { get; set; } = DateTimeOffset.MinValue;
    public int ExpensivePassesCompleted { get; set; }
    public DateTimeOffset LastThrottleEmitUtc { get; set; } = DateTimeOffset.MinValue;
    public string? LastEmittedThrottleKind { get; set; }

    /// <summary>UTC of last measurable progress; null until first progress event.</summary>
    public DateTimeOffset? LastMeasurableProgressUtc { get; set; }

    public DateTimeOffset LastNoProgressEmitUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Expensive reconciliations since last execution burst boundary (see <see cref="LastExecutionTriggerUtc"/>).</summary>
    public int ReconciliationCyclesThisExecution { get; set; }

    /// <summary>Last execution/fill activity UTC for this instrument (gate path).</summary>
    public DateTimeOffset LastExecutionTriggerUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>When true and before <see cref="ProgressHardStopUntilUtc"/>, expensive reconciliation is skipped.</summary>
    public bool ProgressHardStopped { get; set; }

    public DateTimeOffset ProgressHardStopUntilUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>True while inside expensive gate reconciliation (reentrancy guard).</summary>
    public bool InReconciliationLoop { get; set; }

    public DateTimeOffset LastReconciliationExitUtc { get; set; } = DateTimeOffset.MinValue;

    public DateTimeOffset LastExecutionCapEmitUtc { get; set; } = DateTimeOffset.MinValue;

    public DateTimeOffset LastReentryBlockEmitUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Expensive reconciliations since this gate engagement (not reset by throttle backoff; reset on gate release / new engagement).</summary>
    public int TotalExpensiveSinceGateEngaged { get; set; }

    /// <summary>Last intent id seen for execution-cap burst tracking.</summary>
    public string? LastTriggerIntentId { get; set; }

    /// <summary>Last broker position qty snapshot for execution-cap burst tracking.</summary>
    public int? LastTriggerPositionQty { get; set; }

    /// <summary>Baseline for meaningful external drift (throttle reset only when these change).</summary>
    public bool ThrottleBaselineInitialized { get; set; }
    public int ThrottleBaselinePositionQty { get; set; }
    public int ThrottleBaselineBrokerWorking { get; set; }
    public int ThrottleBaselineLocalWorking { get; set; }
    public MismatchType ThrottleBaselineMismatchType { get; set; }

    public void Reset()
    {
        LastProgressSignature = null;
        NoProgressIterations = 0;
        NextAllowedExpensiveReconciliationUtc = DateTimeOffset.MinValue;
        BackoffLevel = 0;
        LastExternalFingerprint = 0;
        LastExpensiveReconciliationUtc = DateTimeOffset.MinValue;
        ExpensivePassesCompleted = 0;
        LastThrottleEmitUtc = DateTimeOffset.MinValue;
        LastEmittedThrottleKind = null;
        LastMeasurableProgressUtc = null;
        LastNoProgressEmitUtc = DateTimeOffset.MinValue;
        ReconciliationCyclesThisExecution = 0;
        LastExecutionTriggerUtc = DateTimeOffset.MinValue;
        ProgressHardStopped = false;
        ProgressHardStopUntilUtc = DateTimeOffset.MinValue;
        InReconciliationLoop = false;
        LastReconciliationExitUtc = DateTimeOffset.MinValue;
        LastExecutionCapEmitUtc = DateTimeOffset.MinValue;
        LastReentryBlockEmitUtc = DateTimeOffset.MinValue;
        TotalExpensiveSinceGateEngaged = 0;
        LastTriggerIntentId = null;
        LastTriggerPositionQty = null;
        ThrottleBaselineInitialized = false;
        ThrottleBaselinePositionQty = 0;
        ThrottleBaselineBrokerWorking = 0;
        ThrottleBaselineLocalWorking = 0;
        ThrottleBaselineMismatchType = default;
    }
}

/// <summary>Builds fingerprints and decides measurable progress for gate reconciliation (stable fields only).</summary>
public static class GateProgressEvaluator
{
    /// <summary>Lower rank = less severe mismatch type for throttle purposes.</summary>
    public static int MismatchSeverityRank(MismatchType t) => t switch
    {
        MismatchType.BROKER_AHEAD => 10,
        MismatchType.JOURNAL_AHEAD => 20,
        MismatchType.POSITION_QTY_MISMATCH => 30,
        MismatchType.ORDER_REGISTRY_MISSING => 40,
        MismatchType.PROTECTIVE_STATE_DIVERGENCE => 35,
        MismatchType.UNKNOWN_EXECUTION_PERSISTENT => 50,
        MismatchType.RESTART_RECONCILIATION_UNRESOLVED => 45,
        MismatchType.LIFECYCLE_BROKER_DIVERGENCE => 42,
        MismatchType.UNCLASSIFIED_CRITICAL_MISMATCH => 60,
        _ => 55
    };

    public static ulong BuildExternalFingerprint(string instrument, AccountSnapshot snapshot, MismatchObservation? obs)
    {
        var inst = instrument?.Trim() ?? "";
        var pos = 0;
        if (snapshot.Positions != null)
        {
            var p = snapshot.Positions.FirstOrDefault(x =>
                string.Equals(x.Instrument?.Trim(), inst, StringComparison.OrdinalIgnoreCase));
            if (p != null)
                pos = p.Quantity;
        }

        var wo = 0;
        if (snapshot.WorkingOrders != null)
            wo = snapshot.WorkingOrders.Count(w =>
                string.Equals(w.Instrument?.Trim(), inst, StringComparison.OrdinalIgnoreCase));

        var oBQ = obs?.BrokerQty ?? 0;
        var oLQ = obs?.LocalQty ?? 0;
        var oBW = obs?.BrokerWorkingOrderCount ?? 0;
        var oLW = obs?.LocalWorkingOrderCount ?? 0;
        unchecked
        {
            ulong h = 1469598103934665603UL;
            void Mix(ulong x)
            {
                h ^= x;
                h *= 1099511628211UL;
            }

            Mix((ulong)(uint)pos);
            Mix((ulong)(uint)wo);
            Mix((ulong)(uint)oBQ);
            Mix((ulong)(uint)oLQ);
            Mix((ulong)(uint)oBW);
            Mix((ulong)(uint)oLW);
            return h;
        }
    }

    /// <param name="overrideGatePhase">When set, used instead of <see cref="MismatchInstrumentState.GateLifecyclePhase"/> for the signature (projected phase after readiness evaluation).</param>
    public static GateProgressSignature BuildSignature(
        MismatchInstrumentState state,
        MismatchObservation? obs,
        StateConsistencyReleaseReadinessResult readiness,
        GateReconciliationResult? recon,
        GateLifecyclePhase? overrideGatePhase = null)
    {
        var phase = overrideGatePhase ?? state.GateLifecyclePhase;
        var mt = state.MismatchType;
        var bw = obs?.BrokerWorkingOrderCount ?? 0;
        var lw = obs?.LocalWorkingOrderCount ?? 0;
        if (recon != null)
        {
            bw = recon.BrokerWorkingCountAfter;
            lw = recon.IeaOwnedCountAfter;
        }

        var rr = readiness.ReleaseReady;
        var gap = Math.Max(0, bw - lw);
        return new GateProgressSignature(phase, mt, bw, lw, rr, gap);
    }

    public static bool IsMeasurableProgress(GateProgressSignature prev, GateProgressSignature next)
    {
        if (next.ReleaseReady && !prev.ReleaseReady)
            return true;

        if (IsForwardGatePhaseMove(prev.GatePhase, next.GatePhase))
            return true;

        if (MismatchSeverityRank(next.MismatchType) < MismatchSeverityRank(prev.MismatchType))
            return true;

        if (next.UnexplainedWorkingGap < prev.UnexplainedWorkingGap)
            return true;

        if (next.LocalOwnedWorking > prev.LocalOwnedWorking && next.BrokerWorking <= prev.BrokerWorking + 1)
            return true;

        if (next.BrokerWorking < prev.BrokerWorking && next.UnexplainedWorkingGap <= prev.UnexplainedWorkingGap)
            return true;

        return false;
    }

    private static bool IsForwardGatePhaseMove(GateLifecyclePhase a, GateLifecyclePhase b)
    {
        if (a == b)
            return false;
        if (b == GateLifecyclePhase.PersistentMismatch || b == GateLifecyclePhase.FailClosed)
            return false;
        return b == GateLifecyclePhase.Reconciling && a == GateLifecyclePhase.DetectedBlocked
               || (b == GateLifecyclePhase.StablePendingRelease &&
                   (a == GateLifecyclePhase.Reconciling || a == GateLifecyclePhase.DetectedBlocked));
    }

    public static int ComputeSignatureHash32(GateProgressSignature s) => s.GetHashCode();

    public static int CurrentBackoffIntervalMs(int backoffLevel)
    {
        var min = MismatchEscalationPolicy.GATE_RECONCILIATION_MIN_INTERVAL_MS;
        var mult = MismatchEscalationPolicy.GATE_RECONCILIATION_BACKOFF_MULTIPLIER;
        var max = MismatchEscalationPolicy.GATE_RECONCILIATION_MAX_INTERVAL_MS;
        double acc = min;
        for (var i = 0; i < backoffLevel; i++)
            acc = Math.Min(max, acc * mult);
        return (int)Math.Round(acc);
    }

    /// <summary>
    /// Project gate phase after this tick's release readiness (mirrors AdvanceStateConsistencyGate release branch, without mutating state).
    /// </summary>
    public static GateLifecyclePhase ProjectGatePhaseForProgressSignature(
        MismatchInstrumentState st,
        StateConsistencyReleaseReadinessResult readiness)
    {
        if (st.EscalationState == MismatchEscalationState.FAIL_CLOSED ||
            st.GateLifecyclePhase == GateLifecyclePhase.FailClosed)
            return GateLifecyclePhase.FailClosed;

        if (st.EscalationState == MismatchEscalationState.PERSISTENT_MISMATCH ||
            st.GateLifecyclePhase == GateLifecyclePhase.PersistentMismatch)
            return GateLifecyclePhase.PersistentMismatch;

        if (!readiness.ReleaseReady)
        {
            if (st.GateLifecyclePhase == GateLifecyclePhase.StablePendingRelease)
                return GateLifecyclePhase.Reconciling;
            return st.GateLifecyclePhase;
        }

        if (st.GateLifecyclePhase == GateLifecyclePhase.StablePendingRelease)
            return GateLifecyclePhase.StablePendingRelease;

        return GateLifecyclePhase.StablePendingRelease;
    }
}
