// Gap 4 + P1.5: Persistent mismatch escalation + state-consistency gate (closed-loop).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Periodic mismatch audits + P1.5 instrument-scoped state-consistency gate:
/// engage → gate-safe reconciliation → release readiness → stability window → release.
/// Mismatch block state is always published as an execution-authority input; diagnostics continue through release.
/// </summary>
/// <remarks>
/// Escalation layering: <see cref="PendingAlignmentAuthority"/> suppresses lag-explained categories during alignment windows;
/// release-path journal integrity stays parity-only while pending; hard flatten / execution lock remain last resort for structural
/// or persistent divergence outside those windows.
/// </remarks>
public sealed partial class MismatchEscalationCoordinator
{
    private readonly Func<AccountSnapshot> _getSnapshot;
    private readonly Func<IReadOnlyList<string>> _getActiveInstruments;
    private readonly Func<AccountSnapshot, DateTimeOffset, IReadOnlyList<MismatchObservation>> _getMismatchObservations;
    private readonly Func<string, bool> _isInstrumentBlocked;
    private readonly Func<string, bool> _isFlattenInProgress;
    private readonly Func<string, bool> _isRecoveryInProgress;
    private readonly RobotLogger? _log;
    private readonly ExecutionEventWriter? _eventWriter;
    private readonly RuntimeAuditHub? _runtimeAudit;
    private readonly Func<string, DateTimeOffset, int, GateReconciliationResult?>? _runInstrumentGateReconciliation;
    private readonly Func<string, AccountSnapshot, DateTimeOffset, bool, StateConsistencyReleaseReadinessResult>?
        _evaluateReleaseReadiness;
    private readonly Func<ReconciliationForcedConvergenceContext, ReconciliationForcedConvergenceResult>? _runForcedBrokerAlignment;
    private readonly Action<string, ReconciliationForcedConvergenceContext, ReconciliationForcedConvergenceResult>?
        _onForcedConvergenceFailure;
    private readonly Func<long>? _getExecutionActivityGeneration;
    private readonly Action<string, DateTimeOffset>? _onHedgedNetFlatPersistentEscalation;
    /// <summary>G1: When mismatch <see cref="MismatchInstrumentState.Blocked"/> transitions, notifies engine/EPA to own durable authority (not parallel enforcement).</summary>
    private readonly Action<string, bool, string?, DateTimeOffset>? _onMismatchExecutionBlockAuthorityChanged;
    /// <summary>
    /// Phase 8b: When provided and <see cref="FeatureFlags.StructuralLayerUseLedgerOwnership"/> is on,
    /// use ledger-derived net qty instead of journal-derived qty for mismatch qty comparison.
    /// </summary>
    private Func<string, InstrumentOwnershipSnapshot?>? _getLedgerSnapshot;

    /// <summary>When set, mismatch audits and canonical events apply only to instruments managed by this <see cref="RobotEngine"/> (non-committed streams).</summary>
    private readonly Func<string, bool>? _isInstrumentInEngineScope;
    /// <summary>
    /// Narrower late-run scope: true only while an instrument still has live stream or interrupted reentry work.
    /// Used to quiesce stale cleanup loops without dropping active recovery.
    /// </summary>
    private readonly Func<string, bool>? _isInstrumentRecoveryRelevant;
    /// <summary>When non-null and returns &gt;0 for an instrument, mismatch gate work is deferred until IEA execution queue drains for that instrument.</summary>
    private readonly Func<string, int>? _getPendingExecutionWorkloadForInstrument;
    private readonly Func<string?>? _getRunIdForMismatchDiagnostics;
    /// <summary>One-shot hedged convergence escalation per instrument per gate episode.</summary>
    private readonly ConcurrentDictionary<string, byte> _hedgedNetFlatEscalationInvoked = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _auditRunLock = new();
    private long _scheduleFingerprintPrev = long.MinValue;
    private long _scheduleActivityGenPrev = long.MinValue;
    private readonly int _stableWindowMs;
    private readonly Timer _auditTimer;
    private DateTimeOffset _lastAuditUtc = DateTimeOffset.MinValue;
    private ulong _nextConvergenceEpisodeId;

    private readonly ConcurrentDictionary<string, MismatchInstrumentState> _stateByInstrument = new(StringComparer.OrdinalIgnoreCase);

    public MismatchEscalationCoordinator(
        Func<AccountSnapshot> getSnapshot,
        Func<IReadOnlyList<string>> getActiveInstruments,
        Func<AccountSnapshot, DateTimeOffset, IReadOnlyList<MismatchObservation>> getMismatchObservations,
        Func<string, bool> isInstrumentBlocked,
        Func<string, bool> isFlattenInProgress,
        Func<string, bool> isRecoveryInProgress,
        RobotLogger? log,
        ExecutionEventWriter? eventWriter = null,
        Func<string, DateTimeOffset, int, GateReconciliationResult?>? runInstrumentGateReconciliation = null,
        Func<string, AccountSnapshot, DateTimeOffset, bool, StateConsistencyReleaseReadinessResult>?
            evaluateReleaseReadiness = null,
        int stateConsistencyStableWindowMs = 0,
        RuntimeAuditHub? runtimeAudit = null,
        Func<ReconciliationForcedConvergenceContext, ReconciliationForcedConvergenceResult>? runForcedBrokerAlignment = null,
        Action<string, ReconciliationForcedConvergenceContext, ReconciliationForcedConvergenceResult>? onForcedConvergenceFailure =
            null,
        Func<long>? getExecutionActivityGeneration = null,
        Action<string, DateTimeOffset>? onHedgedNetFlatPersistentEscalation = null,
        Action<string, bool, string?, DateTimeOffset>? onMismatchExecutionBlockAuthorityChanged = null,
        Func<string, bool>? isInstrumentInEngineScope = null,
        Func<string, bool>? isInstrumentRecoveryRelevant = null,
        Func<string, int>? getPendingExecutionWorkloadForInstrument = null,
        Func<string?>? getRunIdForMismatchDiagnostics = null,
        Func<string, DateTimeOffset, MismatchConvergenceCanonicalProbeResult>? probeCanonicallyUnexplainedExposure = null)
    {
        _getSnapshot = getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));
        _getActiveInstruments = getActiveInstruments ?? (() => Array.Empty<string>());
        _getMismatchObservations = getMismatchObservations ?? ((_, _) => Array.Empty<MismatchObservation>());
        _isInstrumentBlocked = isInstrumentBlocked ?? (_ => false);
        _isFlattenInProgress = isFlattenInProgress ?? (_ => false);
        _isRecoveryInProgress = isRecoveryInProgress ?? (_ => false);
        _log = log;
        _eventWriter = eventWriter;
        _runtimeAudit = runtimeAudit;
        _runInstrumentGateReconciliation = runInstrumentGateReconciliation;
        _evaluateReleaseReadiness = evaluateReleaseReadiness;
        _runForcedBrokerAlignment = runForcedBrokerAlignment;
        _onForcedConvergenceFailure = onForcedConvergenceFailure;
        _getExecutionActivityGeneration = getExecutionActivityGeneration;
        _onHedgedNetFlatPersistentEscalation = onHedgedNetFlatPersistentEscalation;
        _onMismatchExecutionBlockAuthorityChanged = onMismatchExecutionBlockAuthorityChanged;
        _isInstrumentInEngineScope = isInstrumentInEngineScope;
        _isInstrumentRecoveryRelevant = isInstrumentRecoveryRelevant;
        _getPendingExecutionWorkloadForInstrument = getPendingExecutionWorkloadForInstrument;
        _getRunIdForMismatchDiagnostics = getRunIdForMismatchDiagnostics;
        _probeCanonicallyUnexplainedExposure = probeCanonicallyUnexplainedExposure;
        _stableWindowMs = stateConsistencyStableWindowMs > 0
            ? stateConsistencyStableWindowMs
            : MismatchEscalationPolicy.GATE_RELEASE_QUIET_WINDOW_MS;

        var initialDue = getExecutionActivityGeneration != null
            ? MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_ACTIVE_MS
            : MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS;
        _auditTimer = new Timer(OnAuditTick, null, initialDue, Timeout.Infinite);
    }

    private void PublishMismatchExecutionBlockAuthorityIfChanged(string inst, MismatchInstrumentState state, bool wasBlocked,
        DateTimeOffset utcNow)
    {
        if (wasBlocked == state.Blocked) return;
        NoteMismatchEvalAuthorityPublished(inst);
        _onMismatchExecutionBlockAuthorityChanged?.Invoke(
            inst,
            state.Blocked,
            string.IsNullOrEmpty(state.BlockReason) ? null : state.BlockReason,
            utcNow);
    }

    private void AdvanceStateConsistencyGate(string inst, AccountSnapshot snapshot, DateTimeOffset utcNow,
        MismatchObservation? latestObs)
    {
        if (!_stateByInstrument.TryGetValue(inst, out var state))
            return;
        if (state.GateLifecyclePhase == GateLifecyclePhase.None)
            return;
        if (state.EscalationState == MismatchEscalationState.FAIL_CLOSED ||
            state.GateLifecyclePhase == GateLifecyclePhase.FailClosed)
        {
            if (TryExitFailClosedWhenSafe(inst, snapshot, utcNow, latestObs))
                return;
            return;
        }

        var obsForSig = CoalesceGateObservation(inst, state, latestObs, utcNow);
        var gp = state.GateProgress;

        EnsureConvergenceEpisodeStarted(inst, state, obsForSig, utcNow);

        if (gp.ProgressHardStopped && utcNow >= gp.ProgressHardStopUntilUtc)
        {
            gp.ProgressHardStopped = false;
            gp.ProgressHardStopUntilUtc = DateTimeOffset.MinValue;
        }

        state.PersistenceMs = state.FirstDetectedUtc != default
            ? (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds
            : 0;

        if (state.EscalationState == MismatchEscalationState.DETECTED &&
            state.PersistenceMs >= MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS)
        {
            var wasBlockedA = state.Blocked;
            state.EscalationState = MismatchEscalationState.PERSISTENT_MISMATCH;
            state.Blocked = true;
            state.BlockReason = MismatchEscalationPolicy.BLOCK_REASON_PERSISTENT_MISMATCH;
            if (state.GateLifecyclePhase == GateLifecyclePhase.StablePendingRelease)
            {
                state.FirstConsistentUtc = default;
                state.LastConsistentUtc = default;
                state.ReleaseQuietFingerprintKey = "";
            }
            state.GateLifecyclePhase = GateLifecyclePhase.PersistentMismatch;
            _mismatchPersistentCount++;
            var p = ToGatePayload(state, inst, utcNow, obsForSig, null, null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH", p));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH", utcNow, p, "WARN");
            PublishMismatchExecutionBlockAuthorityIfChanged(inst, state, wasBlockedA, utcNow);
        }

        if (state.GateLifecyclePhase == GateLifecyclePhase.DetectedBlocked)
        {
            state.GateLifecyclePhase = GateLifecyclePhase.Reconciling;
            gp.TotalExpensiveSinceGateEngaged = 0;
            var startPayload = ToGatePayload(state, inst, utcNow, obsForSig, null, null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED", startPayload));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED", utcNow, startPayload, "INFO");
        }

        // --- External fingerprint: reset throttle only on meaningful snapshot/mismatch change (hash can jitter on noise) ---
        GetThrottleBaselineComponents(inst, snapshot, obsForSig, state, out var posQty, out var bwObs, out var lwObs,
            out var mismatchType);
        var fpNow = GateProgressEvaluator.BuildExternalFingerprint(inst, snapshot, obsForSig);
        var externalFingerprintChanged = gp.LastExternalFingerprint != 0 && fpNow != gp.LastExternalFingerprint;
        if (externalFingerprintChanged)
        {
            if (IsMeaningfulThrottleBaselineChange(gp, posQty, bwObs, lwObs, mismatchType))
            {
                EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_THROTTLE_RESET", new
                {
                    reason = "meaningful_external_change",
                    signature_hash = GateProgressEvaluator.ComputeSignatureHash32(
                        GateProgressEvaluator.BuildSignature(state, obsForSig,
                            StateConsistencyReleaseEvaluator.Indeterminate(inst), null)),
                    no_progress_iterations = gp.NoProgressIterations,
                    backoff_level = gp.BackoffLevel
                });
                ResetGateThrottle(gp);
                CaptureThrottleBaseline(gp, posQty, bwObs, lwObs, mismatchType);
            }
            else
            {
                EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_THROTTLE_PRESERVED", new
                {
                    reason = "noise_fingerprint_change",
                    last_external_fingerprint = gp.LastExternalFingerprint,
                    new_external_fingerprint = fpNow,
                    no_progress_iterations = gp.NoProgressIterations,
                    next_allowed_expensive_utc = gp.NextAllowedExpensiveReconciliationUtc != DateTimeOffset.MinValue
                        ? gp.NextAllowedExpensiveReconciliationUtc.ToString("o")
                        : null
                });
            }
        }

        gp.LastExternalFingerprint = fpNow;
        if (!gp.ThrottleBaselineInitialized)
            CaptureThrottleBaseline(gp, posQty, bwObs, lwObs, mismatchType);

        StateConsistencyReleaseReadinessResult readinessProbe;
        try
        {
            readinessProbe = _evaluateReleaseReadiness?.Invoke(inst, snapshot, utcNow, false)
                             ?? StateConsistencyReleaseEvaluator.Indeterminate(inst);
        }
        catch (Exception ex)
        {
            _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                new { error = ex.Message, context = "evaluateReleaseReadiness_probe", instrument = inst }));
            readinessProbe = StateConsistencyReleaseEvaluator.Indeterminate(inst, "evaluate_exception");
        }

        var phaseProbe = GateProgressEvaluator.ProjectGatePhaseForProgressSignature(state, readinessProbe);
        var sigProbe = GateProgressEvaluator.BuildSignature(state, obsForSig, readinessProbe, null, phaseProbe);
        var warmupDone = gp.ExpensivePassesCompleted >= MismatchEscalationPolicy.GATE_PROGRESS_WARMUP_EXPENSIVE_PASSES;
        var refProgressTime = gp.LastMeasurableProgressUtc ?? state.LastGateEngagedUtc;
        var timeSinceProgressMs = refProgressTime != default
            ? (utcNow - refProgressTime).TotalMilliseconds
            : 0;
        var noProgressTimeExceeded = warmupDone && timeSinceProgressMs >= MismatchEscalationPolicy.GATE_NO_PROGRESS_TIME_THRESHOLD_MS;
        var noProgressIterExceeded = warmupDone &&
                                     gp.NoProgressIterations >= MismatchEscalationPolicy.GATE_NO_PROGRESS_ITERATION_THRESHOLD;
        var throttleScheduleActive = noProgressTimeExceeded || noProgressIterExceeded;
        if (warmupDone && throttleScheduleActive && gp.NextAllowedExpensiveReconciliationUtc == DateTimeOffset.MinValue)
        {
            var interval = GateProgressEvaluator.CurrentBackoffIntervalMs(gp.BackoffLevel);
            gp.NextAllowedExpensiveReconciliationUtc = utcNow.AddMilliseconds(interval);
        }

        var inCooldown = gp.NextAllowedExpensiveReconciliationUtc != DateTimeOffset.MinValue &&
                         utcNow < gp.NextAllowedExpensiveReconciliationUtc;
        var throttleScheduleSkip = warmupDone && throttleScheduleActive && inCooldown;
        var hardStopSkip = gp.ProgressHardStopped && utcNow < gp.ProgressHardStopUntilUtc;
        var executionCycleCapReached =
            gp.ReconciliationCyclesThisExecution >= MismatchEscalationPolicy.GATE_EXECUTION_RECONCILIATION_CAP;
        var reentryLoopNested = gp.InReconciliationLoop;
        var reentryTooSoon = gp.LastReconciliationExitUtc != default &&
                             (utcNow - gp.LastReconciliationExitUtc).TotalMilliseconds <
                             MismatchEscalationPolicy.GATE_REENTRY_BLOCK_MS;
        var reentrySkip = reentryLoopNested || reentryTooSoon;

        var hysteresisSkip = state.ReconciliationHysteresisUntilUtc != default &&
                             utcNow < state.ReconciliationHysteresisUntilUtc &&
                             state.HysteresisMismatchTypeAtFreeze.HasValue &&
                             state.HysteresisMismatchTypeAtFreeze.Value == obsForSig.MismatchType;

        var postAlignmentStallSkip = state.ForcedConvergenceSucceeded &&
                                     state.PostForcedConvergenceFingerprint != 0 &&
                                     fpNow == state.PostForcedConvergenceFingerprint;

        var antiOscillationSkip = hysteresisSkip || postAlignmentStallSkip;

        var skipExpensive = throttleScheduleSkip || hardStopSkip || executionCycleCapReached || reentrySkip ||
                            antiOscillationSkip;
        var forceExpensiveForReleaseTransition = readinessProbe.ReleaseReady &&
                                                state.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease &&
                                                !hardStopSkip &&
                                                !executionCycleCapReached &&
                                                !reentrySkip;
        var forceExpensiveForSoftTransitionStall = !readinessProbe.ReleaseReady &&
                                                  state.PersistenceMs >= MismatchEscalationPolicy.MISMATCH_FAIL_CLOSED_THRESHOLD_MS &&
                                                  IsSoftTransitionReleaseReadiness(readinessProbe) &&
                                                  !hardStopSkip &&
                                                  !executionCycleCapReached &&
                                                  !reentrySkip;
        if (skipExpensive &&
            (forceExpensiveForReleaseTransition || forceExpensiveForSoftTransitionStall) &&
            (throttleScheduleSkip || antiOscillationSkip))
        {
            skipExpensive = false;
            throttleScheduleSkip = false;
            hysteresisSkip = false;
            postAlignmentStallSkip = false;
        }

        string? skipReason = null;
        if (skipExpensive)
        {
            if (postAlignmentStallSkip)
                skipReason = "post_alignment_stall";
            else if (hysteresisSkip)
                skipReason = "reconciliation_hysteresis";
            else if (reentryLoopNested || reentryTooSoon)
                skipReason = "reentry_loop_blocked";
            else if (hardStopSkip)
                skipReason = gp.TotalExpensiveSinceGateEngaged >=
                             MismatchEscalationPolicy.GATE_ABSOLUTE_MAX_EXPENSIVE_RECONCILIATIONS
                    ? "absolute_gate_lifetime_cap"
                    : "hard_stop_active";
            else if (executionCycleCapReached)
                skipReason = "execution_cycle_cap_reached";
            else if (throttleScheduleSkip)
                skipReason = "throttle_cooldown";
            else
                skipReason = "skipped";
        }

        if (skipExpensive && executionCycleCapReached)
            EmitExecutionCapIfNeeded(inst, utcNow, state, gp);
        if (skipExpensive && (reentryLoopNested || reentryTooSoon))
            EmitReentryBlockedIfNeeded(inst, utcNow, state, gp, reentryLoopNested, reentryTooSoon);

        GateReconciliationResult? recon = null;
        if (!skipExpensive)
        {
            {
                var epRun = state.ConvergenceEpisode;
                epRun.LastActionAttempted = "gate_recovery_runner_and_adoption_scan";
                epRun.AttemptCount++;
                NoteMismatchEvalEpisodeExtended(inst);
            }

            try
            {
                gp.InReconciliationLoop = true;
                try
                {
                    recon = _runInstrumentGateReconciliation?.Invoke(inst, utcNow, gp.ReconciliationCyclesThisExecution + 1);
                }
                catch (Exception ex)
                {
                    _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                        new { error = ex.Message, context = "runInstrumentGateReconciliation", instrument = inst }));
                }
            }
            finally
            {
                gp.InReconciliationLoop = false;
                gp.LastReconciliationExitUtc = utcNow;
            }

            gp.ExpensivePassesCompleted++;
            gp.LastExpensiveReconciliationUtc = utcNow;
            gp.ReconciliationCyclesThisExecution++;
            gp.TotalExpensiveSinceGateEngaged++;
        }
        else if (throttleScheduleSkip)
        {
            EmitThrottledIfNeeded(inst, utcNow, state, gp, sigProbe);
        }

        StateConsistencyReleaseReadinessResult readiness;
        if (skipExpensive)
            readiness = readinessProbe;
        else
        {
            try
            {
                readiness = _evaluateReleaseReadiness?.Invoke(inst, snapshot, utcNow, false)
                            ?? StateConsistencyReleaseEvaluator.Indeterminate(inst);
            }
            catch (Exception ex)
            {
                _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                    new { error = ex.Message, context = "evaluateReleaseReadiness", instrument = inst }));
                readiness = StateConsistencyReleaseEvaluator.Indeterminate(inst, "evaluate_exception");
            }
        }

        if (recon != null)
            recon.ReleaseReadyAfter = readiness.ReleaseReady;

        GateProgressSignature? sigAfterNullable = null;
        if (!skipExpensive)
        {
            var phaseForProgress = GateProgressEvaluator.ProjectGatePhaseForProgressSignature(state, readiness);
            var sigAfter = GateProgressEvaluator.BuildSignature(state, obsForSig, readiness, recon, phaseForProgress);
            sigAfterNullable = sigAfter;
            UpdateProgressAfterExpensivePass(inst, utcNow, state, gp, sigAfter);
        }

        var refProgressForTelemetry = gp.LastMeasurableProgressUtc ?? state.LastGateEngagedUtc;
        var timeSinceProgressTelemetryMs = refProgressForTelemetry != default
            ? (utcNow - refProgressForTelemetry).TotalMilliseconds
            : (double?)null;
        var progressSigHash = GateProgressEvaluator.ComputeSignatureHash32(
            sigAfterNullable ?? sigProbe);
        var hardStopActive = gp.ProgressHardStopped && utcNow < gp.ProgressHardStopUntilUtc;
        var resultTelemetry = new GateReconciliationResultTelemetry(
            ExpensiveInvoked: !skipExpensive,
            ThrottleSuppressedExpensive: skipExpensive,
            NoProgressIterations: gp.NoProgressIterations,
            TimeSinceLastProgressMs: timeSinceProgressTelemetryMs,
            WarmupDone: warmupDone,
            NextAllowedExpensiveUtc: gp.NextAllowedExpensiveReconciliationUtc != DateTimeOffset.MinValue
                ? gp.NextAllowedExpensiveReconciliationUtc.ToString("o")
                : null,
            ProgressSignatureHash: progressSigHash,
            ExternalFingerprintChanged: externalFingerprintChanged,
            SkipReason: skipReason,
            ExecutionCycleCount: gp.ReconciliationCyclesThisExecution,
            HardStopActive: hardStopActive,
            TotalExpensiveSinceGateEngaged: gp.TotalExpensiveSinceGateEngaged);

        var preSigHash = GateProgressEvaluator.ComputeSignatureHash32(sigProbe);
        var postSig = sigAfterNullable ?? sigProbe;
        var postSigHash = GateProgressEvaluator.ComputeSignatureHash32(postSig);
        var progressChanged = preSigHash != postSigHash;
        var progressChangeType = skipExpensive
            ? $"skip:{skipReason ?? "unspecified"}"
            : StateConsistencyAuditFormat.DescribeProgressChange(sigProbe, postSig);

        var materialReadinessFp = BuildMaterialReadinessFingerprint(readiness, state.MismatchType);
        var pendingIeA = _getPendingExecutionWorkloadForInstrument?.Invoke(inst) ?? 0;
        UpdateResidualCleanupTelemetry(state, readiness, pendingIeA, utcNow);
        var releaseQuietFingerprint = BuildReleaseQuietFingerprint(obsForSig, readiness, pendingIeA, preSigHash, postSigHash,
            fpNow, materialReadinessFp);
        var identicalStallCycle = skipExpensive && !externalFingerprintChanged && !progressChanged &&
                                  IsStallLikeSkipReason(skipReason);
        if (identicalStallCycle)
        {
            var stallQuietFp = BuildStallQuietFingerprint(fpNow, materialReadinessFp, skipReason, postSigHash);
            if (stallQuietFp == gp.LastStallQuietFingerprint && gp.LastStallQuietFingerprint != 0)
                gp.IdenticalSkippedEvaluationCount++;
            else
            {
                gp.LastStallQuietFingerprint = stallQuietFp;
                gp.IdenticalSkippedEvaluationCount = 1;
            }
        }
        else
        {
            gp.IdenticalSkippedEvaluationCount = 0;
            gp.LastStallQuietFingerprint = 0;
        }

        var suppressQuietGateTelemetry = false;
        if (skipExpensive && !progressChanged)
        {
            var quietHash = unchecked((((StringComparer.OrdinalIgnoreCase.GetHashCode(inst) * 397) ^ preSigHash) * 397 ^ postSigHash) * 397 ^
                                      (skipReason != null ? StringComparer.Ordinal.GetHashCode(skipReason) : 0));
            if (_quietGateTelemetry.TryGetValue(inst, out var prevQuiet) && prevQuiet.Hash == quietHash &&
                (utcNow - prevQuiet.Utc).TotalSeconds < QuietGateTelemetrySeconds)
                suppressQuietGateTelemetry = true;
            else
                _quietGateTelemetry[inst] = (quietHash, utcNow);
            if (identicalStallCycle && gp.IdenticalSkippedEvaluationCount > 1)
            {
                suppressQuietGateTelemetry = true;
                if (!gp.IdenticalStallSuppressionDebugLogged)
                {
                    gp.IdenticalStallSuppressionDebugLogged = true;
                    var stallDbg = new
                    {
                        timestamp_utc = utcNow.ToString("o"),
                        instrument = inst,
                        skip_reason = skipReason,
                        identical_skipped_count = gp.IdenticalSkippedEvaluationCount,
                        external_fingerprint = fpNow,
                        stall_quiet_fingerprint = gp.LastStallQuietFingerprint,
                        note = "TEMP_DEBUG: identical skipped cycles suppressing RESULT/delta; remove when stable"
                    };
                    _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "GATE_STALL_SUPPRESSED_IDENTICAL_CYCLE", stallDbg));
                    EmitCanonical(inst, "GATE_STALL_SUPPRESSED_IDENTICAL_CYCLE", utcNow, stallDbg, "INFO");
                }
            }
        }

        if (!suppressQuietGateTelemetry)
        {
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_PROGRESS_DELTA",
                new
                {
                    timestamp_utc = utcNow.ToString("o"),
                    instrument = inst,
                    intent_id = obsForSig.IntentIdsCsv ?? "",
                    pre_state_hash = preSigHash,
                    post_state_hash = postSigHash,
                    state_changed = progressChanged,
                    change_type = progressChangeType,
                    expensive_invoked = !skipExpensive
                }));
            EmitCanonical(inst, "RECONCILIATION_PROGRESS_DELTA", utcNow,
                new
                {
                    pre_state_hash = preSigHash,
                    post_state_hash = postSigHash,
                    state_changed = progressChanged,
                    change_type = progressChangeType
                }, "INFO");
        }

        var attemptSuccess = !skipExpensive && recon != null &&
                             recon.OutcomeStatus == ReconciliationOutcomeStatus.Success;
        string? failReason = skipExpensive
            ? skipReason
            : recon?.Reason;
        if (!skipExpensive && recon != null && recon.OutcomeStatus != ReconciliationOutcomeStatus.Success)
            failReason = string.IsNullOrEmpty(failReason) ? recon.OutcomeStatus.ToString() : failReason;
        if (!suppressQuietGateTelemetry)
        {
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_ATTEMPT_AUDIT",
                new
                {
                    timestamp_utc = utcNow.ToString("o"),
                    instrument = inst,
                    intent_id = obsForSig.IntentIdsCsv ?? "",
                    mismatch_detected_type = state.MismatchType.ToString(),
                    action_attempted = skipExpensive ? "skip_expensive_reconciliation" : "gate_recovery_runner_and_adoption_scan",
                    target_layer = skipExpensive ? "n/a" : "registry+journal_via_runner",
                    expected_result = "converge_working_counts_or_release_ready",
                    actual_result = skipExpensive
                        ? "not_run"
                        : (recon?.OutcomeStatus.ToString() ?? "null_recon"),
                    success = attemptSuccess,
                    reason_if_failed = attemptSuccess ? null : failReason,
                    gate_phase = state.GateLifecyclePhase.ToString(),
                    broker_working_before = recon?.BrokerWorkingCountBefore,
                    broker_working_after = recon?.BrokerWorkingCountAfter,
                    iea_owned_after = recon?.IeaOwnedCountAfter
                }));
            EmitCanonical(inst, "RECONCILIATION_ATTEMPT_AUDIT", utcNow,
                new
                {
                    action_attempted = skipExpensive ? "skip" : "gate_recovery",
                    success = attemptSuccess
                }, !attemptSuccess && !skipExpensive ? "WARN" : "INFO");
        }

        if (!skipExpensive)
        {
            var ep = state.ConvergenceEpisode;
            if (progressChanged)
            {
                ep.NoProgressStreak = 0;
                ep.LastProgressUtc = utcNow;
            }
            else
                ep.NoProgressStreak++;
        }

        TryInvokeForcedConvergenceIfLimitsExceeded(inst, state, gp, obsForSig, utcNow, fpNow);

        var resultPayload = ToGatePayloadWithResultTelemetry(state, inst, utcNow, obsForSig, recon, readiness, skipExpensive,
            resultTelemetry);
        if (!suppressQuietGateTelemetry)
        {
            TestGateResultEmitCount++;
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT", resultPayload));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT", utcNow, resultPayload, "INFO");
        }
        else
            TestGateResultSuppressCount++;

        MaybeEmitGateMaxDwellExceeded(inst, utcNow, state);

        if (state.EscalationState == MismatchEscalationState.FAIL_CLOSED ||
            state.GateLifecyclePhase == GateLifecyclePhase.FailClosed)
            return;

        // Fail-closed unchanged. Persistent mismatch blocks only while not release-ready; when ready, quiet window + release still apply (no stuck-forever).
        if ((state.EscalationState == MismatchEscalationState.PERSISTENT_MISMATCH ||
             state.GateLifecyclePhase == GateLifecyclePhase.PersistentMismatch) &&
            !readiness.ReleaseReady)
            return;

        var quietWindowMs = EffectiveQuietWindowMs(state);
        var releaseConditionsMet = readiness.ReleaseReady && pendingIeA == 0 &&
                                     obsForSig.BrokerWorkingOrderCount == 0 && obsForSig.LocalWorkingOrderCount == 0;

        if (!readiness.ReleaseReady)
        {
            TryEmitReleaseBlockedAfterFlat(inst, utcNow, state, readiness, recon, skipExpensive);
            if (state.GateLifecyclePhase == GateLifecyclePhase.StablePendingRelease)
            {
                EmitGateReleaseBlocked(inst, utcNow, state, "readiness_lost_restabilization", readiness);
                var resetPayload = new
                {
                    gate = ToGatePayload(state, inst, utcNow, obsForSig, recon, readiness),
                    stable_reset_reason = readiness.Summary
                };
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RESTABILIZATION_RESET", resetPayload));
                EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RESTABILIZATION_RESET", utcNow, resetPayload, "WARN");
                state.GateLifecyclePhase = GateLifecyclePhase.Reconciling;
                state.FirstConsistentUtc = default;
                state.ReleaseQuietFingerprintKey = "";
            }
            return;
        }

        state.LastConsistentUtc = utcNow;

        if (state.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
        {
            state.GateLifecyclePhase = GateLifecyclePhase.StablePendingRelease;
            var preserveStableClock = readiness.ReleaseReady && skipExpensive && !externalFingerprintChanged &&
                                      IsStallLikeSkipReason(skipReason) && state.FirstConsistentUtc != default &&
                                      releaseQuietFingerprint == state.ReleaseQuietFingerprintKey &&
                                      !string.IsNullOrEmpty(state.ReleaseQuietFingerprintKey);
            if (!preserveStableClock)
            {
                state.FirstConsistentUtc = utcNow;
                state.ReleaseQuietFingerprintKey = releaseQuietFingerprint;
                var spPayload = ToGatePayload(state, inst, utcNow, obsForSig, recon, readiness);
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_STABLE_PENDING_RELEASE", spPayload));
                EmitCanonical(inst, "STATE_CONSISTENCY_GATE_STABLE_PENDING_RELEASE", utcNow, spPayload, "INFO");
            }
            else
                state.ReleaseQuietFingerprintKey = releaseQuietFingerprint;
        }

        var stableMs = state.FirstConsistentUtc != default
            ? (long)(utcNow - state.FirstConsistentUtc).TotalMilliseconds
            : 0;
        var gateDwellMs = state.LastGateEngagedUtc != default
            ? (long)(utcNow - state.LastGateEngagedUtc).TotalMilliseconds
            : 0L;

        var fpChanged = !string.IsNullOrEmpty(state.ReleaseQuietFingerprintKey) &&
                        releaseQuietFingerprint != state.ReleaseQuietFingerprintKey;
        var softDegradeForce = ShouldForceMaxDwellSoftDegradeRelease(
            gateDwellMs, readiness, pendingIeA, obsForSig, fpChanged, releaseConditionsMet, stableMs, quietWindowMs);

        var immediateBrokerFlatRelease = ShouldAllowImmediateBrokerFlatRelease(
            readiness, pendingIeA, obsForSig, fpChanged, releaseConditionsMet);

        if (fpChanged && !softDegradeForce && !immediateBrokerFlatRelease)
        {
            EmitGateReleaseReset(inst, utcNow, state, readiness, obsForSig, pendingIeA, preSigHash, postSigHash, fpNow,
                "quiet_fingerprint_changed", state.ReleaseQuietFingerprintKey, releaseQuietFingerprint);
            state.FirstConsistentUtc = utcNow;
            state.ReleaseQuietFingerprintKey = releaseQuietFingerprint;
            MaybeEmitGateReleaseProgress(inst, utcNow, state, readiness, 0, quietWindowMs, obsForSig, pendingIeA,
                preSigHash, postSigHash, fpNow, true, "quiet_fingerprint_changed");
            return;
        }

        if (!releaseConditionsMet && !softDegradeForce && !immediateBrokerFlatRelease)
        {
            state.FirstConsistentUtc = utcNow;
            MaybeEmitGateReleaseProgress(inst, utcNow, state, readiness, 0, quietWindowMs, obsForSig, pendingIeA,
                preSigHash, postSigHash, fpNow, false, "release_conditions_not_met");
            return;
        }

        if (stableMs < quietWindowMs && !softDegradeForce && !immediateBrokerFlatRelease)
        {
            MaybeEmitGateReleaseProgress(inst, utcNow, state, readiness, stableMs, quietWindowMs, obsForSig, pendingIeA,
                preSigHash, postSigHash, fpNow, false, null);
            return;
        }

        if (softDegradeForce)
            EmitGateSoftDegradeForceRelease(inst, utcNow, state, readiness, obsForSig, pendingIeA, gateDwellMs, fpChanged,
                releaseConditionsMet, stableMs, quietWindowMs);

        var releaseCompletedUnderStall = skipExpensive && IsStallLikeSkipReason(skipReason);

        var releaseSource = immediateBrokerFlatRelease
            ? "broker_flat_fast_path"
            : softDegradeForce
            ? "max_dwell_soft_degrade"
            : state.EscalationState == MismatchEscalationState.PERSISTENT_MISMATCH
                ? "persistent_recovery"
                : "standard";

        var priorQuietFp = state.ReleaseQuietFingerprintKey;
        EmitGateReleasedDiagnostic(inst, utcNow, readiness, obsForSig, pendingIeA, preSigHash, postSigHash,
            fpNow, stableMs, quietWindowMs, priorQuietFp, softDegradeForce, gateDwellMs);

        var wasBlockedRelease = state.Blocked;
        state.Blocked = false;
        state.BlockReason = "";
        state.EscalationState = MismatchEscalationState.NONE;
        state.GateLifecyclePhase = GateLifecyclePhase.None;
        state.LastReleaseUtc = utcNow;
        state.FirstConsistentUtc = default;
        state.ReleaseQuietFingerprintKey = "";
        state.FirstBrokerFlatResidualUtc = default;
        state.FirstJournalResidualUtc = default;
        state.FirstAdoptionResidualUtc = default;
        state.ConsecutiveCleanPassCount = 0;
        state.MismatchStillPresent = false;
        state.GateProgress.Reset();
        state.ConvergenceEpisode.Clear();
        state.ReconciliationHysteresisUntilUtc = default;
        state.HysteresisMismatchTypeAtFreeze = null;
        state.PostForcedConvergenceFingerprint = 0;
        state.ForcedConvergenceSucceeded = false;
        _mismatchClearedCount++;
        _hedgedNetFlatEscalationInvoked.TryRemove(inst, out _);
        _gateReleaseProgressLastEmit.Remove(inst);
        _gateMaxDwellLastEmit.Remove(inst);

        var releasedPayload = new
        {
            gate = ToGatePayload(state, inst, utcNow, obsForSig, recon, readiness),
            stable_for_ms = stableMs,
            stable_window_ms = quietWindowMs,
            release_source = releaseSource,
            max_dwell_soft_degrade = softDegradeForce,
            gate_dwell_ms = gateDwellMs
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RELEASED", releasedPayload));
        EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RELEASED", utcNow, releasedPayload, "INFO");
        if (releaseCompletedUnderStall)
        {
            var underStallDbg = new
            {
                timestamp_utc = utcNow.ToString("o"),
                instrument = inst,
                skip_reason = skipReason,
                stable_for_ms = stableMs,
                stable_window_ms = quietWindowMs,
                release_source = releaseSource,
                note = "TEMP_DEBUG: gate released while expensive recon skipped (stall); remove when stable"
            };
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "GATE_RELEASE_COMPLETED_UNDER_STALL", underStallDbg));
            EmitCanonical(inst, "GATE_RELEASE_COMPLETED_UNDER_STALL", utcNow, underStallDbg, "INFO");
        }
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_CLEARED", ToGatePayload(state, inst, utcNow, obsForSig, null, null)));
        PublishMismatchExecutionBlockAuthorityIfChanged(inst, state, wasBlockedRelease, utcNow);
    }

}
