using System;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    private static MismatchObservation CoalesceGateObservation(string inst, MismatchInstrumentState state,
        MismatchObservation? latest, DateTimeOffset utcNow)
    {
        if (latest != null)
            return latest;
        return new MismatchObservation
        {
            Instrument = inst,
            MismatchType = state.MismatchType,
            Present = state.MismatchStillPresent,
            ObservedUtc = utcNow,
            BrokerWorkingOrderCount = 0,
            LocalWorkingOrderCount = 0
        };
    }

    private void EnsureConvergenceEpisodeStarted(string instrument, MismatchInstrumentState state, MismatchObservation obs,
        DateTimeOffset utcNow)
    {
        if (state.GateLifecyclePhase == GateLifecyclePhase.None)
            return;
        var key = obs.IntentIdsCsv ?? "";
        var ep = state.ConvergenceEpisode;
        if (ep.EpisodeId == 0)
        {
            ep.StartNew(++_nextConvergenceEpisodeId, utcNow, key);
            NoteMismatchEvalEpisodeExtended(instrument);
            return;
        }

        if (!string.Equals(ep.EpisodeIntentKey, key, StringComparison.Ordinal))
        {
            ep.StartNew(++_nextConvergenceEpisodeId, utcNow, key);
            NoteMismatchEvalEpisodeExtended(instrument);
        }
    }

    private void TryInvokeForcedConvergenceIfLimitsExceeded(
        string inst,
        MismatchInstrumentState state,
        GateReconciliationProgressState gp,
        MismatchObservation obsForSig,
        DateTimeOffset utcNow,
        ulong fpNow)
    {
        if (state.EscalationState == MismatchEscalationState.FAIL_CLOSED)
            return;
        if (state.GateLifecyclePhase == GateLifecyclePhase.None)
            return;
        var ep = state.ConvergenceEpisode;
        if (ep.EpisodeId == 0)
            return;

        var durationMs = ep.FirstSeenUtc != default
            ? (utcNow - ep.FirstSeenUtc).TotalMilliseconds
            : 0;
        string? limitReason = null;
        if (ep.AttemptCount >= MismatchEscalationPolicy.RECONCILIATION_CONVERGENCE_MAX_ATTEMPTS)
            limitReason = "max_attempts";
        else if (ep.NoProgressStreak >= MismatchEscalationPolicy.RECONCILIATION_CONVERGENCE_MAX_NO_PROGRESS)
            limitReason = "no_progress";
        else if (durationMs >= MismatchEscalationPolicy.RECONCILIATION_CONVERGENCE_MAX_DURATION_MS)
            limitReason = "timeout";

        if (limitReason == null)
            return;

        var ctx = new ReconciliationForcedConvergenceContext
        {
            Instrument = inst,
            IntentIdsCsv = obsForSig.IntentIdsCsv,
            LimitReason = limitReason,
            Attempts = ep.AttemptCount,
            NoProgressCount = ep.NoProgressStreak
        };

        var result = _runForcedBrokerAlignment?.Invoke(ctx) ?? ReconciliationForcedConvergenceResult.NoHandler();

        var eventPayload = new
        {
            instrument = inst,
            intent_id = obsForSig.IntentIdsCsv ?? "",
            reason = limitReason,
            attempts = ep.AttemptCount,
            no_progress_count = ep.NoProgressStreak,
            final_action = "broker_alignment",
            aligned = result.AlignedWithBroker,
            failure_reason = result.FailureReason
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_FORCED_CONVERGENCE", eventPayload));
        EmitCanonical(inst, "RECONCILIATION_FORCED_CONVERGENCE", utcNow, eventPayload,
            result.AlignedWithBroker ? "WARN" : "CRITICAL");

        if (result.AlignedWithBroker)
        {
            ep.StartNew(++_nextConvergenceEpisodeId, utcNow, obsForSig.IntentIdsCsv);
            NoteMismatchEvalEpisodeExtended(inst);
            state.ForcedConvergenceSucceeded = true;
            state.PostForcedConvergenceFingerprint = result.PostAlignmentFingerprint != 0
                ? result.PostAlignmentFingerprint
                : fpNow;
            state.ReconciliationHysteresisUntilUtc =
                utcNow.AddMilliseconds(MismatchEscalationPolicy.RECONCILIATION_CONVERGENCE_HYSTERESIS_MS);
            state.HysteresisMismatchTypeAtFreeze = state.MismatchType;
            ResetGateThrottle(gp);
            gp.LastExternalFingerprint = fpNow;
            return;
        }

        if (TryDeferFailClosedForSoftTransition(inst, state, obsForSig, utcNow, "forced_convergence",
                result.FailureReason, gp))
            return;

        var wasBlockedFc = state.Blocked;
        state.EscalationState = MismatchEscalationState.FAIL_CLOSED;
        state.GateLifecyclePhase = GateLifecyclePhase.FailClosed;
        state.Blocked = true;
        state.BlockReason = string.IsNullOrEmpty(result.FailureReason)
            ? MismatchEscalationPolicy.BLOCK_REASON_PERSISTENT_MISMATCH
            : $"FORCED_CONVERGENCE:{result.FailureReason}";
        _mismatchFailClosedCount++;
        var failPayload = ToGatePayload(state, inst, utcNow, obsForSig, null, null);
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_FAIL_CLOSED", failPayload));
        EmitCanonical(inst, ExecutionEventTypes.MISMATCH_FAIL_CLOSED, utcNow, failPayload, "CRITICAL");
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RECOVERY_FAILED", failPayload));
        EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RECOVERY_FAILED", utcNow, failPayload, "CRITICAL");
        PublishMismatchExecutionBlockAuthorityIfChanged(inst, state, wasBlockedFc, utcNow);
        _onForcedConvergenceFailure?.Invoke(inst, ctx, result);
    }
}
