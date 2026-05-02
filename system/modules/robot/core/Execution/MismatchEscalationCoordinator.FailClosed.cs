using System;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    private const int FailClosedSoftDeferHysteresisMs = 5_000;

    private bool TryDeferFailClosedForSoftTransition(string inst, MismatchInstrumentState state, MismatchObservation obs,
        DateTimeOffset utcNow, string source, string? failureReason, GateReconciliationProgressState? gp)
    {
        if (_evaluateReleaseReadiness == null)
            return false;

        AccountSnapshot snap;
        try
        {
            snap = _getSnapshot();
        }
        catch (Exception ex)
        {
            _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                new { error = ex.Message, context = "TryDeferFailClosedForSoftTransition_snapshot", instrument = inst }));
            return false;
        }

        StateConsistencyReleaseReadinessResult readiness;
        try
        {
            readiness = _evaluateReleaseReadiness.Invoke(inst, snap, utcNow, true);
        }
        catch (Exception ex)
        {
            _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                new { error = ex.Message, context = "TryDeferFailClosedForSoftTransition_readiness", instrument = inst }));
            return false;
        }

        var pendingIeA = _getPendingExecutionWorkloadForInstrument?.Invoke(inst) ?? 0;
        var releaseReady = readiness.SnapshotSufficient && readiness.ReleaseReady;
        var softTransition = IsSoftTransitionReleaseReadiness(readiness);
        var fillBoundary = IsFillBoundaryPositionDelta(readiness, obs, pendingIeA);
        if (!releaseReady && !softTransition && !fillBoundary)
            return false;

        state.EscalationState = MismatchEscalationState.PERSISTENT_MISMATCH;
        if (state.GateLifecyclePhase == GateLifecyclePhase.FailClosed || state.GateLifecyclePhase == GateLifecyclePhase.None)
            state.GateLifecyclePhase = GateLifecyclePhase.PersistentMismatch;
        state.Blocked = true;
        if (string.IsNullOrWhiteSpace(state.BlockReason))
            state.BlockReason = MismatchEscalationPolicy.BLOCK_REASON_PERSISTENT_MISMATCH;
        state.ForcedConvergenceSucceeded = true;
        state.ReconciliationHysteresisUntilUtc = utcNow.AddMilliseconds(FailClosedSoftDeferHysteresisMs);
        state.HysteresisMismatchTypeAtFreeze = state.MismatchType;
        state.PostForcedConvergenceFingerprint = GateProgressEvaluator.BuildExternalFingerprint(inst, snap, obs);
        state.ConvergenceEpisode.StartNew(++_nextConvergenceEpisodeId, utcNow, obs.IntentIdsCsv);
        if (gp != null)
            ResetGateThrottle(gp);

        var payload = new
        {
            instrument = inst,
            intent_id = obs.IntentIdsCsv ?? "",
            source,
            failure_reason = failureReason,
            release_ready = readiness.ReleaseReady,
            soft_transition = softTransition,
            fill_boundary_position_delta = fillBoundary,
            readiness_summary = readiness.Summary,
            broker_position_qty = readiness.DiagnosticBrokerPositionQty,
            broker_working_count = readiness.DiagnosticBrokerWorkingCount,
            journal_open_qty = readiness.DiagnosticJournalOpenQty,
            iea_owned_plus_adopted_working = readiness.DiagnosticIeaOwnedPlusAdoptedWorking,
            pending_execution_workload = pendingIeA,
            hysteresis_ms = FailClosedSoftDeferHysteresisMs,
            note = "Fail-close deferred because broker exposure is either release-ready, a known soft transition, or a fill/order bridge boundary."
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_FORCED_CONVERGENCE_SOFT_DEFERRED", payload));
        EmitCanonical(inst, "RECONCILIATION_FORCED_CONVERGENCE_SOFT_DEFERRED", utcNow, payload, "INFO");
        return true;
    }

    private bool CanBypassMismatchExecutionBlockForSubmit(
        string inst,
        MismatchInstrumentState state,
        string? submitPath,
        DateTimeOffset utcNow)
    {
        if (!string.Equals(submitPath, "SUBMIT_ENTRY_STOP", StringComparison.Ordinal))
            return false;
        if (state.GateLifecyclePhase == GateLifecyclePhase.FailClosed ||
            state.EscalationState == MismatchEscalationState.FAIL_CLOSED)
            return false;
        if (_evaluateReleaseReadiness == null)
            return false;

        StateConsistencyReleaseReadinessResult readiness;
        try
        {
            readiness = _evaluateReleaseReadiness(inst, _getSnapshot(), utcNow, false);
        }
        catch
        {
            return false;
        }

        if (!readiness.SnapshotSufficient || readiness.BlockerInvariantViolation)
            return false;
        if (readiness.DiagnosticBrokerWorkingCount != 0)
            return false;
        if (readiness.DiagnosticIeaOwnedPlusAdoptedWorking > 0)
            return false;
        if ((_getPendingExecutionWorkloadForInstrument?.Invoke(inst) ?? 0) != 0)
            return false;
        if (!readiness.BrokerPositionExplainable || !readiness.BrokerWorkingExplainable || !readiness.LocalStateCoherent)
            return false;
        if (readiness.UnexplainedBrokerPositionQty != 0 || readiness.UnexplainedBrokerWorkingCount != 0)
            return false;

        return readiness.ReleaseReady || IsSoftTransitionReleaseReadiness(readiness);
    }

    /// <summary>
    /// When fail-closed but the mismatch observation is absent and release invariants pass, clear the gate and
    /// mismatch execution block authority â€” avoids a permanent deadlock without skipping structural release checks.
    /// When <see cref="FeatureFlags.FailClosedStrictReleaseConfirmationEnabled"/>, requires multi-snapshot stability,
    /// full (non-cached) release readiness, and a mandatory post-release recheck; re-engages fail-closed if the recheck fails.
    /// </summary>
    private bool TryExitFailClosedWhenSafe(string inst, AccountSnapshot snapshot, DateTimeOffset utcNow,
        MismatchObservation? latestObs)
    {
        if (!_stateByInstrument.TryGetValue(inst, out var state))
            return false;
        if (state.EscalationState != MismatchEscalationState.FAIL_CLOSED &&
            state.GateLifecyclePhase != GateLifecyclePhase.FailClosed)
            return false;

        var obsForSig = CoalesceGateObservation(inst, state, latestObs, utcNow);
        if (obsForSig.Present)
            return false;

        StateConsistencyReleaseReadinessResult readiness;
        if (FeatureFlags.FailClosedStrictReleaseConfirmationEnabled)
        {
            if (_evaluateReleaseReadiness == null ||
                !TryEvaluateFailClosedStrictSnapshots(inst, utcNow, out readiness))
                return false;
        }
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
                    new { error = ex.Message, context = "TryExitFailClosedWhenSafe", instrument = inst }));
                return false;
            }

            if (!readiness.SnapshotSufficient || !readiness.ReleaseReady)
                return false;
        }

        var wasBlockedRelease = state.Blocked;
        state.Blocked = false;
        state.BlockReason = "";
        state.EscalationState = MismatchEscalationState.NONE;
        state.GateLifecyclePhase = GateLifecyclePhase.None;
        state.LastReleaseUtc = utcNow;
        state.FirstConsistentUtc = default;
        state.ReleaseQuietFingerprintKey = "";
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
            gate = ToGatePayload(state, inst, utcNow, obsForSig, null, readiness),
            release_source = "fail_closed_safe_recovery",
            release_ready = readiness.ReleaseReady
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RELEASED", releasedPayload));
        EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RELEASED", utcNow, releasedPayload, "INFO");
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_CLEARED",
            ToGatePayload(state, inst, utcNow, obsForSig, null, null)));
        PublishMismatchExecutionBlockAuthorityIfChanged(inst, state, wasBlockedRelease, utcNow);

        if (FeatureFlags.FailClosedStrictReleaseConfirmationEnabled && _evaluateReleaseReadiness != null)
        {
            AccountSnapshot postSnap;
            try
            {
                postSnap = _getSnapshot();
            }
            catch (Exception ex)
            {
                _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                    new { error = ex.Message, context = "TryExitFailClosedWhenSafe_post_snapshot", instrument = inst }));
                ReengageFailClosedAfterPostReleaseRecheckFailed(inst, utcNow,
                    StateConsistencyReleaseEvaluator.Indeterminate(inst, "post_release_snapshot_failed"));
                return false;
            }

            StateConsistencyReleaseReadinessResult postR;
            try
            {
                postR = _evaluateReleaseReadiness.Invoke(inst, postSnap, DateTimeOffset.UtcNow, true);
            }
            catch (Exception ex)
            {
                _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                    new { error = ex.Message, context = "TryExitFailClosedWhenSafe_post_readiness", instrument = inst }));
                ReengageFailClosedAfterPostReleaseRecheckFailed(inst, utcNow,
                    StateConsistencyReleaseEvaluator.Indeterminate(inst, "post_release_readiness_exception"));
                return false;
            }

            if (!postR.SnapshotSufficient || !postR.ReleaseReady)
            {
                ReengageFailClosedAfterPostReleaseRecheckFailed(inst, utcNow, postR);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Full release readiness (forceFullEvaluation) on <see cref="MismatchEscalationPolicy.FAIL_CLOSED_STRICT_SNAPSHOT_COUNT"/>
    /// fresh snapshots; stability key must match across all passes; gaps cover bounded quiet window.
    /// </summary>
    private bool TryEvaluateFailClosedStrictSnapshots(string inst, DateTimeOffset utcNow,
        out StateConsistencyReleaseReadinessResult lastReadiness)
    {
        lastReadiness = StateConsistencyReleaseEvaluator.Indeterminate(inst);
        if (_evaluateReleaseReadiness == null)
            return false;

        var n = MismatchEscalationPolicy.FAIL_CLOSED_STRICT_SNAPSHOT_COUNT;
        var gapMs = MismatchEscalationPolicy.FAIL_CLOSED_STRICT_SNAPSHOT_GAP_MS;
        string? prevKey = null;

        for (var i = 0; i < n; i++)
        {
            AccountSnapshot snap;
            try
            {
                snap = _getSnapshot();
            }
            catch (Exception ex)
            {
                _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                    new { error = ex.Message, context = "TryEvaluateFailClosedStrictSnapshots", instrument = inst, pass = i }));
                return false;
            }

            StateConsistencyReleaseReadinessResult r;
            try
            {
                r = _evaluateReleaseReadiness.Invoke(inst, snap, DateTimeOffset.UtcNow, true);
            }
            catch (Exception ex)
            {
                _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                    new { error = ex.Message, context = "TryEvaluateFailClosedStrictSnapshots_eval", instrument = inst, pass = i }));
                return false;
            }

            if (!r.SnapshotSufficient || !r.ReleaseReady)
            {
                lastReadiness = r;
                return false;
            }

            var key = BuildFailClosedStrictStabilityKey(r);
            if (prevKey != null && !string.Equals(prevKey, key, StringComparison.Ordinal))
                return false;
            prevKey = key;
            lastReadiness = r;

            if (i < n - 1 && gapMs > 0)
                Thread.Sleep(gapMs);
        }

        return true;
    }

    private void ReengageFailClosedAfterPostReleaseRecheckFailed(string inst, DateTimeOffset utcNow,
        StateConsistencyReleaseReadinessResult postReadiness)
    {
        if (!_stateByInstrument.TryGetValue(inst, out var state))
            return;

        var wasBlockedBeforeReengage = state.Blocked;
        state.EscalationState = MismatchEscalationState.FAIL_CLOSED;
        state.GateLifecyclePhase = GateLifecyclePhase.FailClosed;
        state.Blocked = true;
        state.BlockReason =
            $"{MismatchEscalationPolicy.BLOCK_REASON_POST_RELEASE_RECHECK_FAILED}:{postReadiness.Summary}";
        _mismatchFailClosedCount++;

        var payload = new
        {
            instrument = inst,
            post_release_summary = postReadiness.Summary,
            post_release_contradictions = postReadiness.Contradictions != null
                ? string.Join(";", postReadiness.Contradictions)
                : "",
            note = "Gate had released via fail_closed_safe_recovery; mandatory post-readiness recheck failed â€” re-engaged FAIL_CLOSED"
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_FAIL_CLOSED_POST_RELEASE_RECHECK_FAILED",
            payload));
        EmitCanonical(inst, "STATE_CONSISTENCY_GATE_FAIL_CLOSED_POST_RELEASE_RECHECK_FAILED", utcNow, payload, "CRITICAL");

        PublishMismatchExecutionBlockAuthorityIfChanged(inst, state, wasBlockedBeforeReengage, utcNow);
    }
}
