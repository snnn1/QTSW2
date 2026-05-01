using System;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    private void ResetGateThrottle(GateReconciliationProgressState gp)
    {
        gp.NoProgressIterations = 0;
        gp.BackoffLevel = 0;
        gp.NextAllowedExpensiveReconciliationUtc = DateTimeOffset.MinValue;
        gp.LastProgressSignature = null;
        gp.ReconciliationCyclesThisExecution = 0;
        gp.ProgressHardStopped = false;
        gp.ProgressHardStopUntilUtc = DateTimeOffset.MinValue;
    }

    private void EmitExecutionCapIfNeeded(string inst, DateTimeOffset utcNow, MismatchInstrumentState state,
        GateReconciliationProgressState gp)
    {
        const int minEmitGapMs = 5000;
        if (gp.LastExecutionCapEmitUtc != DateTimeOffset.MinValue &&
            (utcNow - gp.LastExecutionCapEmitUtc).TotalMilliseconds < minEmitGapMs)
            return;
        EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_EXECUTION_CAP_REACHED", new
        {
            cap = MismatchEscalationPolicy.GATE_EXECUTION_RECONCILIATION_CAP,
            reconciliation_cycles_this_execution = gp.ReconciliationCyclesThisExecution
        });
        gp.LastExecutionCapEmitUtc = utcNow;
    }

    private void EmitReentryBlockedIfNeeded(string inst, DateTimeOffset utcNow, MismatchInstrumentState state,
        GateReconciliationProgressState gp, bool nested, bool tooSoon)
    {
        const int minEmitGapMs = 2000;
        if (gp.LastReentryBlockEmitUtc != DateTimeOffset.MinValue &&
            (utcNow - gp.LastReentryBlockEmitUtc).TotalMilliseconds < minEmitGapMs)
            return;
        EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_REENTRY_BLOCKED", new
        {
            nested_reconciliation_loop = nested,
            within_reentry_window_ms = tooSoon,
            reentry_block_ms = MismatchEscalationPolicy.GATE_REENTRY_BLOCK_MS
        });
        gp.LastReentryBlockEmitUtc = utcNow;
    }

    private void EmitThrottledIfNeeded(string inst, DateTimeOffset utcNow, MismatchInstrumentState state,
        GateReconciliationProgressState gp, GateProgressSignature sigProbe)
    {
        const int minEmitGapMs = 5000;
        if (gp.LastEmittedThrottleKind == "RECONCILIATION_THROTTLED" &&
            (utcNow - gp.LastThrottleEmitUtc).TotalMilliseconds < minEmitGapMs)
            return;

        var nextMs = (gp.NextAllowedExpensiveReconciliationUtc - utcNow).TotalMilliseconds;
        var payload = new
        {
            instrument = inst,
            gate_phase = state.GateLifecyclePhase.ToString(),
            signature_hash = GateProgressEvaluator.ComputeSignatureHash32(sigProbe),
            no_progress_iterations = gp.NoProgressIterations,
            time_since_last_progress_ms = gp.LastMeasurableProgressUtc != null
                ? (utcNow - gp.LastMeasurableProgressUtc.Value).TotalMilliseconds
                : (double?)null,
            backoff_interval_ms = GateProgressEvaluator.CurrentBackoffIntervalMs(gp.BackoffLevel),
            next_allowed_in_ms = nextMs,
            next_allowed_expensive_utc = gp.NextAllowedExpensiveReconciliationUtc != DateTimeOffset.MinValue
                ? gp.NextAllowedExpensiveReconciliationUtc.ToString("o")
                : null,
            backoff_level = gp.BackoffLevel
        };
        EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_THROTTLED", payload);
        gp.LastThrottleEmitUtc = utcNow;
        gp.LastEmittedThrottleKind = "RECONCILIATION_THROTTLED";
    }

    private void UpdateProgressAfterExpensivePass(string inst, DateTimeOffset utcNow, MismatchInstrumentState state,
        GateReconciliationProgressState gp, GateProgressSignature sigAfter)
    {
        var hash = GateProgressEvaluator.ComputeSignatureHash32(sigAfter);
        if (gp.LastProgressSignature == null)
        {
            gp.LastProgressSignature = sigAfter;
            return;
        }

        var prev = gp.LastProgressSignature.Value;
        if (GateProgressEvaluator.IsMeasurableProgress(prev, sigAfter))
        {
            gp.LastMeasurableProgressUtc = utcNow;
            gp.NoProgressIterations = 0;
            gp.BackoffLevel = 0;
            gp.NextAllowedExpensiveReconciliationUtc = DateTimeOffset.MinValue;
            gp.ProgressHardStopped = false;
            gp.ProgressHardStopUntilUtc = DateTimeOffset.MinValue;
            gp.LastProgressSignature = sigAfter;
            EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_PROGRESS_OBSERVED", new
            {
                kind = "measurable",
                signature_hash = hash,
                gate_phase = state.GateLifecyclePhase.ToString(),
                no_progress_iterations = 0,
                prior_signature_hash = GateProgressEvaluator.ComputeSignatureHash32(prev)
            });
            EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_THROTTLE_BACKOFF_UPDATED", new
            {
                backoff_level = 0,
                next_interval_ms = MismatchEscalationPolicy.GATE_RECONCILIATION_MIN_INTERVAL_MS,
                reason = "progress"
            });
            return;
        }

        gp.NoProgressIterations++;
        gp.LastProgressSignature = sigAfter;
        var warmupDone = gp.ExpensivePassesCompleted >= MismatchEscalationPolicy.GATE_PROGRESS_WARMUP_EXPENSIVE_PASSES;
        var refProgressTime = gp.LastMeasurableProgressUtc ?? state.LastGateEngagedUtc;
        var timeSinceProgressMs = refProgressTime != default
            ? (utcNow - refProgressTime).TotalMilliseconds
            : 0;
        var noProgressTimeExceeded = warmupDone && timeSinceProgressMs >= MismatchEscalationPolicy.GATE_NO_PROGRESS_TIME_THRESHOLD_MS;
        var noProgressIterExceeded = warmupDone &&
                                     gp.NoProgressIterations >= MismatchEscalationPolicy.GATE_NO_PROGRESS_ITERATION_THRESHOLD;

        const int noProgressEmitMinGapMs = 4000;
        if (gp.LastNoProgressEmitUtc == DateTimeOffset.MinValue ||
            (utcNow - gp.LastNoProgressEmitUtc).TotalMilliseconds >= noProgressEmitMinGapMs)
        {
            EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_NO_PROGRESS_DETECTED", new
            {
                signature_hash = hash,
                no_progress_iterations = gp.NoProgressIterations,
                time_since_last_progress_ms = timeSinceProgressMs,
                iteration_threshold_hit = noProgressIterExceeded,
                time_threshold_hit = noProgressTimeExceeded
            });
            gp.LastNoProgressEmitUtc = utcNow;
        }

        if (warmupDone && (noProgressIterExceeded || noProgressTimeExceeded))
        {
            var interval = GateProgressEvaluator.CurrentBackoffIntervalMs(gp.BackoffLevel);
            gp.NextAllowedExpensiveReconciliationUtc = utcNow.AddMilliseconds(interval);
            EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_THROTTLE_BACKOFF_UPDATED", new
            {
                backoff_level = gp.BackoffLevel,
                next_interval_ms = interval,
                next_allowed_utc = gp.NextAllowedExpensiveReconciliationUtc.ToString("o"),
                reason = noProgressIterExceeded ? "no_progress_iterations" : "no_progress_time"
            });
            gp.BackoffLevel = Math.Min(gp.BackoffLevel + 1, 12);
        }

        if (warmupDone &&
            gp.NoProgressIterations >= MismatchEscalationPolicy.GATE_HARD_STOP_NO_PROGRESS_ITERATIONS &&
            timeSinceProgressMs >= MismatchEscalationPolicy.GATE_HARD_STOP_NO_PROGRESS_TIME_MS)
        {
            if (!gp.ProgressHardStopped)
            {
                gp.ProgressHardStopped = true;
                gp.ProgressHardStopUntilUtc =
                    utcNow.AddMilliseconds(MismatchEscalationPolicy.GATE_HARD_STOP_DURATION_MS);
                EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_HARD_STOPPED", new
                {
                    no_progress_iterations = gp.NoProgressIterations,
                    time_since_last_progress_ms = timeSinceProgressMs,
                    hard_stop_until_utc = gp.ProgressHardStopUntilUtc.ToString("o"),
                    duration_ms = MismatchEscalationPolicy.GATE_HARD_STOP_DURATION_MS
                });
            }
        }
    }

    private static void GetThrottleBaselineComponents(string inst, AccountSnapshot snapshot, MismatchObservation? obs,
        MismatchInstrumentState st, out int posQty, out int bw, out int lw, out MismatchType mt)
    {
        posQty = 0;
        if (snapshot.Positions != null)
        {
            var p = snapshot.Positions.FirstOrDefault(x =>
                string.Equals(x.Instrument?.Trim(), inst, StringComparison.OrdinalIgnoreCase));
            if (p != null)
                posQty = p.Quantity;
        }

        bw = obs?.BrokerWorkingOrderCount ?? 0;
        lw = obs?.LocalWorkingOrderCount ?? 0;
        mt = st.MismatchType;
    }

    private static bool IsMeaningfulThrottleBaselineChange(GateReconciliationProgressState gp, int posQty, int bw, int lw,
        MismatchType mt)
    {
        if (!gp.ThrottleBaselineInitialized)
            return false;
        return posQty != gp.ThrottleBaselinePositionQty
            || bw != gp.ThrottleBaselineBrokerWorking
            || lw != gp.ThrottleBaselineLocalWorking
            || mt != gp.ThrottleBaselineMismatchType;
    }

    private static void CaptureThrottleBaseline(GateReconciliationProgressState gp, int posQty, int bw, int lw,
        MismatchType mt)
    {
        gp.ThrottleBaselinePositionQty = posQty;
        gp.ThrottleBaselineBrokerWorking = bw;
        gp.ThrottleBaselineLocalWorking = lw;
        gp.ThrottleBaselineMismatchType = mt;
        gp.ThrottleBaselineInitialized = true;
    }
}
