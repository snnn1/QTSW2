using System;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    /// <summary>P1.5-H: Clean signal absent does not clear gate; only stability release clears.</summary>
    private void ProcessMismatchSignalAbsent(string instrument, DateTimeOffset utcNow)
    {
        if (!_stateByInstrument.TryGetValue(instrument, out var state))
            return;

        state.LastSeenUtc = utcNow;
        state.MismatchStillPresent = false;

        if (state.GateLifecyclePhase != GateLifecyclePhase.None)
            return;

        if (state.EscalationState == MismatchEscalationState.NONE)
            return;
        if (state.EscalationState == MismatchEscalationState.FAIL_CLOSED)
            return;

        state.ConsecutiveCleanPassCount++;
    }

    private void ProcessMismatchPresent(MismatchObservation obs)
    {
        var inst = obs.Instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return;

        var state = _stateByInstrument.GetOrAdd(inst, _ => new MismatchInstrumentState());
        var utcNow = obs.ObservedUtc;

        if (!obs.Present)
        {
            ProcessMismatchSignalAbsent(inst, utcNow);
            return;
        }

        state.MismatchStillPresent = true;
        state.LastSeenUtc = utcNow;
        state.LastSummary = obs.Summary;

        TryRemoveExpiredConvergence(inst, utcNow);

        var pendingIeAForFirstGate = _getPendingExecutionWorkloadForInstrument?.Invoke(inst) ?? 0;
        if (state.EscalationState == MismatchEscalationState.NONE && pendingIeAForFirstGate > 0)
        {
            EmitMismatchPendingIeaExecution(inst, utcNow, obs, pendingIeAForFirstGate);
            return;
        }

        if (state.EscalationState == MismatchEscalationState.NONE &&
            PendingAlignmentAuthority.IsJournalLagExplainedMismatchType(obs.MismatchType) &&
            QuantExecutionControlStore.IsAnyBrokerJournalAlignmentWindowActive(inst, utcNow))
        {
            EmitMismatchPendingConvergence(inst, utcNow, obs);
            return;
        }

        if (state.EscalationState == MismatchEscalationState.NONE &&
            obs.MismatchType == MismatchType.WORKING_ORDER_COUNT_CONVERGENCE &&
            QuantExecutionControlStore.IsAnyBrokerJournalAlignmentWindowActive(inst, utcNow))
        {
            EmitMismatchPendingConvergence(inst, utcNow, obs);
            return;
        }

        if (state.EscalationState == MismatchEscalationState.NONE &&
            PendingAlignmentAuthority.IsPendingAlignment(inst, utcNow) &&
            PendingAlignmentAuthority.IsJournalLagExplainedMismatchType(obs.MismatchType))
            return;

        if (state.EscalationState == MismatchEscalationState.NONE)
        {
            if (ShouldSuppressFirstMismatchEscalationForConvergence(inst, utcNow, obs, out var probeResult,
                    out var suppressionReason, out var convEntry) &&
                convEntry != null)
            {
                TestConvergenceFirstEscalationSuppressedCount++;
                EmitConvergenceSuppressedFirstEscalation(inst, utcNow, obs, probeResult, suppressionReason, convEntry);
                return;
            }

            var wasBlocked = state.Blocked;
            state.EscalationState = MismatchEscalationState.DETECTED;
            state.MismatchType = obs.MismatchType;
            state.FirstDetectedUtc = utcNow;
            state.LastDetectedUtc = utcNow;
            state.Blocked = true;
            state.BlockReason = MismatchEscalationPolicy.BLOCK_REASON_STATE_CONSISTENCY_GATE;
            state.GateLifecyclePhase = GateLifecyclePhase.DetectedBlocked;
            state.LastGateEngagedUtc = utcNow;
            state.StableWindowMsApplied = _stableWindowMs;
            state.FirstConsistentUtc = default;
            state.LastConsistentUtc = default;
            state.GateProgress.Reset();
            state.ConvergenceEpisode.Clear();
            state.ReconciliationHysteresisUntilUtc = default;
            state.HysteresisMismatchTypeAtFreeze = null;
            state.PostForcedConvergenceFingerprint = 0;
            state.ForcedConvergenceSucceeded = false;
            _mismatchDetectedCount++;
            IncrementTypeMetric(obs.MismatchType);
            var detectedPayload = ToGatePayload(state, inst, utcNow, obs, null, null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_DETECTED", detectedPayload));
            EmitCanonical(inst, ExecutionEventTypes.MISMATCH_DETECTED, utcNow, detectedPayload, "WARN");
            var gatePayload = ToGatePayload(state, inst, utcNow, obs, null, null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_ENGAGED", gatePayload));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_ENGAGED", utcNow, gatePayload, "WARN");
            var tsv = StateConsistencyAuditFormat.BuildFourStateTsvLine(utcNow, inst, obs, state.MismatchType);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_FOUR_STATE_TSV",
                new { four_state_tsv = tsv, note = "Tab-separated; columns match STATE_CONSISTENCY_GATE audit spec" }));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_FOUR_STATE_TSV", utcNow, new { four_state_tsv = tsv }, "INFO");
            PublishMismatchExecutionBlockAuthorityIfChanged(inst, state, wasBlocked, utcNow);
            return;
        }

        state.LastDetectedUtc = utcNow;
        state.PersistenceMs = (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds;

        TryHedgedNetFlatPersistentEscalation(inst, obs, utcNow, state);

        if (state.EscalationState == MismatchEscalationState.DETECTED)
        {
            if (state.PersistenceMs >= MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS)
            {
                var wasBlocked = state.Blocked;
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
                var p = ToGatePayload(state, inst, utcNow, obs, null, null);
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_PERSISTENT", p));
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_BLOCKED", p));
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH", p));
                EmitCanonical(inst, "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH", utcNow, p, "WARN");
                PublishMismatchExecutionBlockAuthorityIfChanged(inst, state, wasBlocked, utcNow);
            }
            return;
        }

        if (state.EscalationState == MismatchEscalationState.PERSISTENT_MISMATCH)
        {
            state.PersistenceMs = (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds;
            var failClosedEligible = obs.MismatchType == MismatchType.NET_POSITION_MISMATCH;
            if (failClosedEligible &&
                (state.PersistenceMs >= MismatchEscalationPolicy.MISMATCH_FAIL_CLOSED_THRESHOLD_MS ||
                 state.RetryCount >= MismatchEscalationPolicy.MISMATCH_MAX_RETRIES))
            {
                if (TryDeferFailClosedForSoftTransition(inst, state, obs, utcNow, "persistent_mismatch", null,
                        state.GateProgress))
                    return;

                state.EscalationState = MismatchEscalationState.FAIL_CLOSED;
                state.GateLifecyclePhase = GateLifecyclePhase.FailClosed;
                _mismatchFailClosedCount++;
                var failClosedPayload = ToGatePayload(state, inst, utcNow, obs, null, null);
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_FAIL_CLOSED", failClosedPayload));
                EmitCanonical(inst, ExecutionEventTypes.MISMATCH_FAIL_CLOSED, utcNow, failClosedPayload, "CRITICAL");
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RECOVERY_FAILED", failClosedPayload));
                EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RECOVERY_FAILED", utcNow, failClosedPayload, "CRITICAL");
            }
        }
    }

    /// <summary>
    /// Hedged net-flat / gross-open is not fail-closed, but persistent state must converge â€” escalate monitoring + optional gate recovery.
    /// </summary>
    private void TryHedgedNetFlatPersistentEscalation(string inst, MismatchObservation obs, DateTimeOffset utcNow,
        MismatchInstrumentState state)
    {
        if (obs.MismatchType != MismatchType.HEDGED_NET_FLAT_GROSS_OPEN) return;
        if (_onHedgedNetFlatPersistentEscalation == null) return;
        if (state.PersistenceMs < MismatchEscalationPolicy.HEDGED_NET_FLAT_CONVERGENCE_ESCALATION_MS) return;
        if (!_hedgedNetFlatEscalationInvoked.TryAdd(inst, 0)) return;

        var payload = new
        {
            instrument = inst,
            persistence_ms = state.PersistenceMs,
            threshold_ms = MismatchEscalationPolicy.HEDGED_NET_FLAT_CONVERGENCE_ESCALATION_MS,
            gross_journal_qty = obs.LocalQty,
            net_broker_qty = obs.NetBrokerQty,
            net_journal_qty = obs.NetJournalQty,
            note = "Hedged structural net-flat gross-open persisted â€” invoke convergence hook (not fail-closed)"
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "HEDGED_NET_FLAT_GROSS_OPEN_CONVERGENCE_ESCALATION", payload));
        EmitCanonical(inst, "HEDGED_NET_FLAT_GROSS_OPEN_CONVERGENCE_ESCALATION", utcNow, payload, "WARN");
        _onHedgedNetFlatPersistentEscalation(inst, utcNow);
    }
}
