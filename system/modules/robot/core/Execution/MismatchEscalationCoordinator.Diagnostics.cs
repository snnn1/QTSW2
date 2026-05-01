using System;
using System.Collections.Generic;
using System.Reflection;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    private readonly record struct GateReconciliationResultTelemetry(
        bool ExpensiveInvoked,
        bool ThrottleSuppressedExpensive,
        int NoProgressIterations,
        double? TimeSinceLastProgressMs,
        bool WarmupDone,
        string? NextAllowedExpensiveUtc,
        int ProgressSignatureHash,
        bool ExternalFingerprintChanged,
        string? SkipReason,
        int ExecutionCycleCount,
        bool HardStopActive,
        int TotalExpensiveSinceGateEngaged);

    private void EmitCanonical(string inst, string eventType, DateTimeOffset utc, object payload, string severity = "INFO")
    {
        if (_isInstrumentInEngineScope != null &&
            (string.IsNullOrWhiteSpace(inst) || !_isInstrumentInEngineScope(inst.Trim())))
            return;

        _eventWriter?.Emit(new CanonicalExecutionEvent
        {
            TimestampUtc = utc.ToString("o"),
            Instrument = inst,
            EventType = eventType,
            Severity = severity,
            Source = "MismatchEscalationCoordinator",
            Payload = payload
        });
    }

    private void EmitGateProgressControlEvent(string inst, DateTimeOffset utcNow, MismatchInstrumentState state,
        string eventType, object payload)
    {
        var merged = new Dictionary<string, object?>
        {
            ["instrument"] = inst,
            ["gate_phase"] = state.GateLifecyclePhase.ToString(),
            ["timestamp_utc"] = utcNow.ToString("o")
        };
        foreach (var p in payload.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            merged[p.Name] = p.GetValue(payload);

        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, eventType, merged));
        EmitCanonical(inst, eventType, utcNow, merged, "INFO");
    }

    private object ToGatePayload(
        MismatchInstrumentState state,
        string instrument,
        DateTimeOffset utcNow,
        MismatchObservation? obs,
        GateReconciliationResult? recon,
        StateConsistencyReleaseReadinessResult? readiness,
        bool gateReconciliationThrottled = false)
    {
        var mismatchAge = state.FirstDetectedUtc != default
            ? (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds
            : 0L;
        var stableFor = state.FirstConsistentUtc != default && state.GateLifecyclePhase == GateLifecyclePhase.StablePendingRelease
            ? (long)(utcNow - state.FirstConsistentUtc).TotalMilliseconds
            : 0L;
        var mismatchUntilBrokerFlatMs = state.LastGateEngagedUtc != default && state.FirstBrokerFlatResidualUtc != default
            ? (long)(state.FirstBrokerFlatResidualUtc - state.LastGateEngagedUtc).TotalMilliseconds
            : (long?)null;
        var mismatchAfterBrokerFlatMs = state.FirstBrokerFlatResidualUtc != default
            ? (long)(utcNow - state.FirstBrokerFlatResidualUtc).TotalMilliseconds
            : (long?)null;
        var journalRetirementLagMs = state.FirstJournalResidualUtc != default
            ? (long)(utcNow - state.FirstJournalResidualUtc).TotalMilliseconds
            : (long?)null;
        var adoptionRetirementLagMs = state.FirstAdoptionResidualUtc != default
            ? (long)(utcNow - state.FirstAdoptionResidualUtc).TotalMilliseconds
            : (long?)null;

        return new
        {
            instrument,
            gate_state = state.GateLifecyclePhase.ToString(),
            escalation_state = state.EscalationState.ToString(),
            mismatch_age_ms = mismatchAge,
            blocked = state.Blocked,
            block_reason = state.BlockReason,
            broker_position_qty = obs?.BrokerQty ?? 0,
            broker_working_count = obs?.BrokerWorkingOrderCount ?? 0,
            iea_owned_count = obs?.LocalWorkingOrderCount,
            iea_adopted_count = (int?)null,
            pending_adoption_count = (int?)null,
            unexplained_working_count = readiness?.UnexplainedBrokerWorkingCount,
            unexplained_position_qty = readiness?.UnexplainedBrokerPositionQty,
            release_ready = readiness?.ReleaseReady,
            residual_cleanup_only = readiness?.ResidualCleanupOnly,
            residual_cleanup_class = readiness?.ResidualCleanupClass,
            stable_for_ms = stableFor,
            stable_window_ms = state.StableWindowMsApplied > 0 ? state.StableWindowMsApplied : _stableWindowMs,
            mismatch_time_until_broker_flat_ms = mismatchUntilBrokerFlatMs,
            mismatch_time_after_broker_flat_ms = mismatchAfterBrokerFlatMs,
            journal_retirement_lag_ms = journalRetirementLagMs,
            adoption_retirement_lag_ms = adoptionRetirementLagMs,
            reconciliation_outcome = recon == null
                ? null
                : new
                {
                    recon.Mode,
                    recon.OutcomeStatus,
                    recon.BrokerWorkingCountBefore,
                    recon.BrokerWorkingCountAfter,
                    recon.IeaOwnedCountBefore,
                    recon.IeaOwnedCountAfter,
                    recon.AdoptionCandidateCountBefore,
                    recon.AdoptionCandidateCountAfter,
                    recon.UnexplainedWorkingCountAfter,
                    recon.UnexplainedPositionQtyAfter,
                    recon.ReleaseReadyAfter,
                    recon.Reason,
                    recon.DurationMs,
                    recon.RunnerInvoked
                },
            readiness_summary = readiness?.Summary,
            readiness_contradictions = readiness?.Contradictions,
            broker_qty = obs?.BrokerQty ?? 0,
            local_qty = obs?.LocalQty ?? 0,
            payload_local_qty = obs?.LocalQty ?? 0,
            diagnostic_journal_open_qty = readiness?.DiagnosticJournalOpenQty,
            mismatch_type = state.MismatchType.ToString(),
            persistence_ms = state.PersistenceMs,
            timestamp_utc = utcNow.ToString("o"),
            gate_action_policy = StateConsistencyGateActionPolicy.PolicyNote,
            gate_reconciliation_throttled = gateReconciliationThrottled
        };
    }

    private object ToGatePayloadWithResultTelemetry(
        MismatchInstrumentState state,
        string instrument,
        DateTimeOffset utcNow,
        MismatchObservation? obs,
        GateReconciliationResult? recon,
        StateConsistencyReleaseReadinessResult? readiness,
        bool gateReconciliationThrottled,
        GateReconciliationResultTelemetry telemetry)
    {
        var mismatchAge = state.FirstDetectedUtc != default
            ? (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds
            : 0L;
        var stableFor = state.FirstConsistentUtc != default && state.GateLifecyclePhase == GateLifecyclePhase.StablePendingRelease
            ? (long)(utcNow - state.FirstConsistentUtc).TotalMilliseconds
            : 0L;
        var mismatchUntilBrokerFlatMs = state.LastGateEngagedUtc != default && state.FirstBrokerFlatResidualUtc != default
            ? (long)(state.FirstBrokerFlatResidualUtc - state.LastGateEngagedUtc).TotalMilliseconds
            : (long?)null;
        var mismatchAfterBrokerFlatMs = state.FirstBrokerFlatResidualUtc != default
            ? (long)(utcNow - state.FirstBrokerFlatResidualUtc).TotalMilliseconds
            : (long?)null;
        var journalRetirementLagMs = state.FirstJournalResidualUtc != default
            ? (long)(utcNow - state.FirstJournalResidualUtc).TotalMilliseconds
            : (long?)null;
        var adoptionRetirementLagMs = state.FirstAdoptionResidualUtc != default
            ? (long)(utcNow - state.FirstAdoptionResidualUtc).TotalMilliseconds
            : (long?)null;

        return new
        {
            instrument,
            gate_state = state.GateLifecyclePhase.ToString(),
            escalation_state = state.EscalationState.ToString(),
            mismatch_age_ms = mismatchAge,
            blocked = state.Blocked,
            block_reason = state.BlockReason,
            broker_position_qty = obs?.BrokerQty ?? 0,
            broker_working_count = obs?.BrokerWorkingOrderCount ?? 0,
            iea_owned_count = obs?.LocalWorkingOrderCount,
            iea_adopted_count = (int?)null,
            pending_adoption_count = (int?)null,
            unexplained_working_count = readiness?.UnexplainedBrokerWorkingCount,
            unexplained_position_qty = readiness?.UnexplainedBrokerPositionQty,
            release_ready = readiness?.ReleaseReady,
            residual_cleanup_only = readiness?.ResidualCleanupOnly,
            residual_cleanup_class = readiness?.ResidualCleanupClass,
            stable_for_ms = stableFor,
            stable_window_ms = state.StableWindowMsApplied > 0 ? state.StableWindowMsApplied : _stableWindowMs,
            mismatch_time_until_broker_flat_ms = mismatchUntilBrokerFlatMs,
            mismatch_time_after_broker_flat_ms = mismatchAfterBrokerFlatMs,
            journal_retirement_lag_ms = journalRetirementLagMs,
            adoption_retirement_lag_ms = adoptionRetirementLagMs,
            reconciliation_outcome = recon == null
                ? null
                : new
                {
                    recon.Mode,
                    recon.OutcomeStatus,
                    recon.BrokerWorkingCountBefore,
                    recon.BrokerWorkingCountAfter,
                    recon.IeaOwnedCountBefore,
                    recon.IeaOwnedCountAfter,
                    recon.AdoptionCandidateCountBefore,
                    recon.AdoptionCandidateCountAfter,
                    recon.UnexplainedWorkingCountAfter,
                    recon.UnexplainedPositionQtyAfter,
                    recon.ReleaseReadyAfter,
                    recon.Reason,
                    recon.DurationMs,
                    recon.RunnerInvoked
                },
            readiness_summary = readiness?.Summary,
            readiness_contradictions = readiness?.Contradictions,
            broker_qty = obs?.BrokerQty ?? 0,
            local_qty = obs?.LocalQty ?? 0,
            payload_local_qty = obs?.LocalQty ?? 0,
            diagnostic_journal_open_qty = readiness?.DiagnosticJournalOpenQty,
            mismatch_type = state.MismatchType.ToString(),
            persistence_ms = state.PersistenceMs,
            timestamp_utc = utcNow.ToString("o"),
            gate_action_policy = StateConsistencyGateActionPolicy.PolicyNote,
            gate_reconciliation_throttled = gateReconciliationThrottled,
            expensive_invoked = telemetry.ExpensiveInvoked,
            throttle_suppressed_expensive = telemetry.ThrottleSuppressedExpensive,
            skip_reason = telemetry.SkipReason,
            no_progress_iterations = telemetry.NoProgressIterations,
            time_since_last_progress_ms = telemetry.TimeSinceLastProgressMs,
            warmup_done = telemetry.WarmupDone,
            next_allowed_expensive_utc = telemetry.NextAllowedExpensiveUtc,
            progress_signature_hash = telemetry.ProgressSignatureHash,
            external_fingerprint_changed = telemetry.ExternalFingerprintChanged,
            execution_cycle_count = telemetry.ExecutionCycleCount,
            hard_stop_active = telemetry.HardStopActive,
            total_expensive_since_gate_engaged = telemetry.TotalExpensiveSinceGateEngaged
        };
    }
}
