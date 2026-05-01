using System;
using System.Collections.Generic;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    /// <summary>Coalesce identical gate telemetry when expensive reconciliation is skipped.</summary>
    private readonly Dictionary<string, (int Hash, DateTimeOffset Utc)> _quietGateTelemetry = new(StringComparer.OrdinalIgnoreCase);

    private const int QuietGateTelemetrySeconds = 30;

    /// <summary>Rate-limit identical post-flat release-blocker payloads per instrument.</summary>
    private readonly Dictionary<string, (int Hash, DateTimeOffset Utc)> _releaseBlockedAfterFlatTelemetry =
        new(StringComparer.OrdinalIgnoreCase);

    private const int ReleaseBlockedAfterFlatQuietSeconds = 30;

    /// <summary>Throttle gate-scope retained telemetry while gate is active but engine scope is false.</summary>
    private const int GateScopeRetainedLogQuietSeconds = 30;

    private readonly Dictionary<string, DateTimeOffset> _gateScopeRetainedLogThrottle = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Throttle gate release progress telemetry to prove stable clock without flooding per tick.</summary>
    private const int GateReleaseProgressMinIntervalMs = 1000;

    private readonly Dictionary<string, DateTimeOffset> _gateReleaseProgressLastEmit = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Throttle max-dwell critical telemetry.</summary>
    private readonly Dictionary<string, DateTimeOffset> _gateMaxDwellLastEmit = new(StringComparer.OrdinalIgnoreCase);

    private void MaybeEmitGateScopeRetainedActivePhase(DateTimeOffset utcNow, string instrument, GateLifecyclePhase gatePhase)
    {
        if (_log == null) return;
        if (_gateScopeRetainedLogThrottle.TryGetValue(instrument, out var last) &&
            (utcNow - last).TotalSeconds < GateScopeRetainedLogQuietSeconds)
            return;
        _gateScopeRetainedLogThrottle[instrument] = utcNow;

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "GATE_SCOPE_RETAINED_ACTIVE_PHASE", state: "ENGINE",
            new
            {
                instrument,
                gate_phase = gatePhase.ToString(),
                run_id = _getRunIdForMismatchDiagnostics?.Invoke() ?? "",
                trading_date = "",
                in_engine_scope = false,
                reason = "active_gate_phase"
            }));
    }

    private bool IsInstrumentInEngineScopeForDiagnostics(string instrument)
    {
        if (_isInstrumentInEngineScope == null) return true;
        if (string.IsNullOrWhiteSpace(instrument)) return false;
        return _isInstrumentInEngineScope(instrument.Trim());
    }

    private static void UpdateResidualCleanupTelemetry(
        MismatchInstrumentState state,
        StateConsistencyReleaseReadinessResult readiness,
        int pendingIeA,
        DateTimeOffset utcNow)
    {
        var brokerFlatNoWork = readiness.DiagnosticBrokerPositionQty == 0 &&
                               readiness.DiagnosticBrokerWorkingCount == 0 &&
                               pendingIeA == 0;
        if (!brokerFlatNoWork)
        {
            state.FirstBrokerFlatResidualUtc = default;
            state.FirstJournalResidualUtc = default;
            state.FirstAdoptionResidualUtc = default;
            return;
        }

        if (state.FirstBrokerFlatResidualUtc == default)
            state.FirstBrokerFlatResidualUtc = utcNow;

        if (readiness.DiagnosticJournalOpenQty > 0)
        {
            if (state.FirstJournalResidualUtc == default)
                state.FirstJournalResidualUtc = utcNow;
        }
        else
            state.FirstJournalResidualUtc = default;

        if (readiness.DiagnosticPendingAdoptionCandidateCount > 0)
        {
            if (state.FirstAdoptionResidualUtc == default)
                state.FirstAdoptionResidualUtc = utcNow;
        }
        else
            state.FirstAdoptionResidualUtc = default;
    }

    private void MaybeEmitGateMaxDwellExceeded(string inst, DateTimeOffset utcNow, MismatchInstrumentState state)
    {
        if (_log == null) return;
        if (state.GateLifecyclePhase == GateLifecyclePhase.None) return;
        if (state.LastGateEngagedUtc == default) return;
        var dwellMs = (utcNow - state.LastGateEngagedUtc).TotalMilliseconds;
        if (dwellMs < MismatchEscalationPolicy.GATE_MAX_DWELL_ALERT_MS) return;
        if (_gateMaxDwellLastEmit.TryGetValue(inst, out var last) &&
            (utcNow - last).TotalSeconds < 30)
            return;
        _gateMaxDwellLastEmit[inst] = utcNow;

        var payload = new
        {
            instrument = inst,
            gate_phase = state.GateLifecyclePhase.ToString(),
            gate_dwell_ms = (long)dwellMs,
            max_gate_dwell_ms = MismatchEscalationPolicy.GATE_MAX_DWELL_ALERT_MS,
            note =
                "Fail-safe: gate engaged longer than max_gate_dwell_ms - review; eligible ticks may apply GATE_SOFT_DEGRADE_FORCE_RELEASE when release_ready and only soft blockers remain",
            run_id = _getRunIdForMismatchDiagnostics?.Invoke() ?? ""
        };
        _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "GATE_MAX_DWELL_EXCEEDED", payload));
        if (IsInstrumentInEngineScopeForDiagnostics(inst))
            EmitCanonical(inst, "GATE_MAX_DWELL_EXCEEDED", utcNow, payload, "CRITICAL");
    }

    /// <summary>Quiet-window / fingerprint progression while <see cref="GateLifecyclePhase.StablePendingRelease"/>.</summary>
    private void MaybeEmitGateReleaseProgress(
        string inst,
        DateTimeOffset utcNow,
        MismatchInstrumentState state,
        StateConsistencyReleaseReadinessResult readiness,
        long stableMs,
        int quietWindowMs,
        MismatchObservation obs,
        int pendingIeA,
        int preSigHash,
        int postSigHash,
        ulong externalFp,
        bool fingerprintChanged,
        string? resetReason)
    {
        if (_log == null) return;
        if (state.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease) return;
        if (_gateReleaseProgressLastEmit.TryGetValue(inst, out var last) &&
            (utcNow - last).TotalMilliseconds < GateReleaseProgressMinIntervalMs)
            return;
        _gateReleaseProgressLastEmit[inst] = utcNow;

        var inScope = IsInstrumentInEngineScopeForDiagnostics(inst);
        var payload = new
        {
            instrument = inst,
            gate_phase = state.GateLifecyclePhase.ToString(),
            release_ready = readiness.ReleaseReady,
            readiness_summary = readiness.Summary,
            quiet_window_ms = quietWindowMs,
            stable_for_ms = stableMs,
            journal_open_qty = readiness.DiagnosticJournalOpenQty,
            broker_qty = obs.BrokerQty,
            pending_execution_workload = pendingIeA,
            broker_working_count = obs.BrokerWorkingOrderCount,
            local_working_count = obs.LocalWorkingOrderCount,
            fingerprint_changed = fingerprintChanged,
            reset_reason = resetReason,
            progress_pre_hash = preSigHash,
            progress_post_hash = postSigHash,
            external_fingerprint = externalFp,
            in_engine_scope = inScope,
            run_id = _getRunIdForMismatchDiagnostics?.Invoke() ?? ""
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "GATE_RELEASE_PROGRESS", payload));
        if (inScope)
            EmitCanonical(inst, "GATE_RELEASE_PROGRESS", utcNow, payload, "INFO");
    }

    private void EmitGateReleaseReset(
        string inst,
        DateTimeOffset utcNow,
        MismatchInstrumentState state,
        StateConsistencyReleaseReadinessResult readiness,
        MismatchObservation obs,
        int pendingIeA,
        int preSigHash,
        int postSigHash,
        ulong externalFp,
        string resetReason,
        string? priorFingerprint,
        string newFingerprint)
    {
        if (_log == null) return;
        var stableMs = state.FirstConsistentUtc != default
            ? (long)(utcNow - state.FirstConsistentUtc).TotalMilliseconds
            : 0L;
        var quietMs = EffectiveQuietWindowMs(state);
        var inScope = IsInstrumentInEngineScopeForDiagnostics(inst);
        var payload = new
        {
            instrument = inst,
            gate_phase = state.GateLifecyclePhase.ToString(),
            release_ready = readiness.ReleaseReady,
            readiness_summary = readiness.Summary,
            quiet_window_ms = quietMs,
            stable_for_ms = stableMs,
            journal_open_qty = readiness.DiagnosticJournalOpenQty,
            broker_qty = obs.BrokerQty,
            pending_execution_workload = pendingIeA,
            broker_working_count = obs.BrokerWorkingOrderCount,
            local_working_count = obs.LocalWorkingOrderCount,
            fingerprint_changed = true,
            reset_reason = resetReason,
            prior_fingerprint = priorFingerprint,
            new_fingerprint = newFingerprint,
            progress_pre_hash = preSigHash,
            progress_post_hash = postSigHash,
            external_fingerprint = externalFp,
            in_engine_scope = inScope,
            run_id = _getRunIdForMismatchDiagnostics?.Invoke() ?? ""
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "GATE_RELEASE_RESET", payload));
        if (inScope)
            EmitCanonical(inst, "GATE_RELEASE_RESET", utcNow, payload, "INFO");
    }

    private void EmitGateSoftDegradeForceRelease(
        string inst,
        DateTimeOffset utcNow,
        MismatchInstrumentState state,
        StateConsistencyReleaseReadinessResult readiness,
        MismatchObservation obs,
        int pendingIeA,
        long gateDwellMs,
        bool fingerprintChanged,
        bool releaseConditionsMet,
        long stableMs,
        int quietWindowMs)
    {
        if (_log == null) return;
        var inScope = IsInstrumentInEngineScopeForDiagnostics(inst);
        var payload = new
        {
            instrument = inst,
            gate_phase = state.GateLifecyclePhase.ToString(),
            release_ready = readiness.ReleaseReady,
            readiness_summary = readiness.Summary,
            max_gate_dwell_ms = MismatchEscalationPolicy.GATE_MAX_DWELL_ALERT_MS,
            gate_dwell_ms = gateDwellMs,
            quiet_window_ms = quietWindowMs,
            stable_for_ms = stableMs,
            journal_open_qty = readiness.DiagnosticJournalOpenQty,
            net_broker_qty = obs.NetBrokerQty,
            net_journal_qty = obs.NetJournalQty,
            pending_execution_workload = pendingIeA,
            broker_working_count = obs.BrokerWorkingOrderCount,
            local_working_count = obs.LocalWorkingOrderCount,
            unexplained_broker_position_qty = readiness.UnexplainedBrokerPositionQty,
            unexplained_broker_working_count = readiness.UnexplainedBrokerWorkingCount,
            fingerprint_changed = fingerprintChanged,
            release_conditions_met = releaseConditionsMet,
            note =
                "Max dwell exceeded: nets aligned, no unexplained risk, no IEA workload - forcing release despite working-order noise and/or fingerprint jitter",
            run_id = _getRunIdForMismatchDiagnostics?.Invoke() ?? ""
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "GATE_SOFT_DEGRADE_FORCE_RELEASE", payload));
        if (inScope)
            EmitCanonical(inst, "GATE_SOFT_DEGRADE_FORCE_RELEASE", utcNow, payload, "WARN");
    }

    private void EmitGateReleasedDiagnostic(
        string inst,
        DateTimeOffset utcNow,
        StateConsistencyReleaseReadinessResult readiness,
        MismatchObservation obs,
        int pendingIeA,
        int preSigHash,
        int postSigHash,
        ulong externalFp,
        long stableMs,
        int quietWindowMs,
        string priorFingerprint,
        bool maxDwellSoftDegrade,
        long gateDwellMs)
    {
        if (_log == null) return;
        var inScope = IsInstrumentInEngineScopeForDiagnostics(inst);
        var payload = new
        {
            instrument = inst,
            gate_phase = GateLifecyclePhase.None.ToString(),
            release_ready = readiness.ReleaseReady,
            readiness_summary = readiness.Summary,
            quiet_window_ms = quietWindowMs,
            stable_for_ms = stableMs,
            max_dwell_soft_degrade = maxDwellSoftDegrade,
            gate_dwell_ms = gateDwellMs,
            journal_open_qty = readiness.DiagnosticJournalOpenQty,
            broker_qty = obs.BrokerQty,
            pending_execution_workload = pendingIeA,
            broker_working_count = obs.BrokerWorkingOrderCount,
            local_working_count = obs.LocalWorkingOrderCount,
            fingerprint_changed = false,
            reset_reason = (string?)null,
            prior_quiet_fingerprint = priorFingerprint,
            progress_pre_hash = preSigHash,
            progress_post_hash = postSigHash,
            external_fingerprint = externalFp,
            in_engine_scope = inScope,
            run_id = _getRunIdForMismatchDiagnostics?.Invoke() ?? ""
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "GATE_RELEASED", payload));
        if (inScope)
            EmitCanonical(inst, "GATE_RELEASED", utcNow, payload, "INFO");
    }

    private void EmitGateReleaseBlocked(
        string inst,
        DateTimeOffset utcNow,
        MismatchInstrumentState state,
        string reason,
        StateConsistencyReleaseReadinessResult readiness)
    {
        if (_log == null) return;
        var inScope = IsInstrumentInEngineScopeForDiagnostics(inst);
        var payload = new
        {
            instrument = inst,
            gate_phase = state.GateLifecyclePhase.ToString(),
            reason,
            release_ready = readiness.ReleaseReady,
            in_engine_scope = inScope,
            run_id = _getRunIdForMismatchDiagnostics?.Invoke() ?? ""
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "GATE_RELEASE_BLOCKED", payload));
        if (inScope)
            EmitCanonical(inst, "GATE_RELEASE_BLOCKED", utcNow, payload, "WARN");
    }

    /// <summary>
    /// Broker is flat (per release diagnostics) but gate cannot release; log structured blockers for ops.
    /// </summary>
    private void TryEmitReleaseBlockedAfterFlat(
        string inst,
        DateTimeOffset utcNow,
        MismatchInstrumentState state,
        StateConsistencyReleaseReadinessResult readiness,
        GateReconciliationResult? recon,
        bool skipExpensive)
    {
        if (_log == null) return;
        if (readiness.ReleaseReady || !readiness.SnapshotSufficient) return;
        if (readiness.DiagnosticBrokerPositionQty != 0) return;
        if (!HasResidualReleaseBlocker(readiness)) return;

        var contradictions = string.Join(";", readiness.Contradictions ?? new List<string>());
        var quietHash = unchecked((StringComparer.OrdinalIgnoreCase.GetHashCode(inst) * 397) ^
                                  (contradictions != null ? StringComparer.Ordinal.GetHashCode(contradictions) : 0));
        if (_releaseBlockedAfterFlatTelemetry.TryGetValue(inst, out var prev) && prev.Hash == quietHash &&
            (utcNow - prev.Utc).TotalSeconds < ReleaseBlockedAfterFlatQuietSeconds)
            return;
        _releaseBlockedAfterFlatTelemetry[inst] = (quietHash, utcNow);

        EmitGateReleaseBlocked(inst, utcNow, state, "release_not_ready_while_broker_flat", readiness);

        var payload = new
        {
            instrument = inst,
            gate_phase = state.GateLifecyclePhase.ToString(),
            broker_position_qty = readiness.DiagnosticBrokerPositionQty,
            journal_open_qty = readiness.DiagnosticJournalOpenQty,
            broker_working_count = readiness.DiagnosticBrokerWorkingCount,
            iea_owned_plus_adopted_working = readiness.DiagnosticIeaOwnedPlusAdoptedWorking,
            pending_adoption_candidate_count = readiness.DiagnosticPendingAdoptionCandidateCount,
            release_ready = readiness.ReleaseReady,
            contradictions,
            release_summary = readiness.Summary,
            safe_cleanup_attempted = !skipExpensive,
            gate_reconciliation_outcome = recon?.OutcomeStatus.ToString(),
            gate_reconciliation_release_ready_after = recon?.ReleaseReadyAfter,
            gate_reconciliation_reason = recon?.Reason,
            gate_reconciliation_duration_ms = recon?.DurationMs,
            note = "Gate active, broker flat per release probe, but ReleaseReady false - residual journal/adoption/working/IEA coherence"
        };
        _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RELEASE_BLOCKED_AFTER_FLAT", payload));
        EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RELEASE_BLOCKED_AFTER_FLAT", utcNow, payload, "WARN");
    }

    private void EmitMismatchPendingIeaExecution(string inst, DateTimeOffset utcNow, MismatchObservation obs, int pendingIeA)
    {
        if (_mismatchPendingConvergenceLastEmit.TryGetValue(inst, out var last) &&
            (utcNow - last).TotalSeconds < MismatchPendingConvergenceLogSeconds)
            return;
        _mismatchPendingConvergenceLastEmit[inst] = utcNow;

        var payload = new
        {
            instrument = inst,
            mismatch_type = obs.MismatchType.ToString(),
            summary = obs.Summary,
            pending_execution_count = pendingIeA,
            gate_escalation_deferred = true,
            note = "IEA execution queue still has work for this instrument; deferring first mismatch gate engagement until execution updates drain"
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "MISMATCH_PENDING_IEA_EXECUTION", payload));
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_DEFERRED_PENDING_IEA_EXECUTION", payload));
        EmitCanonical(inst, "STATE_CONSISTENCY_GATE_DEFERRED_PENDING_IEA_EXECUTION", utcNow, payload, "INFO");
    }
}
