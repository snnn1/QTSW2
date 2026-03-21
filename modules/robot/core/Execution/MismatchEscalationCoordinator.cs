// Gap 4 + P1.5: Persistent mismatch escalation + state-consistency gate (closed-loop).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Periodic mismatch audits + P1.5 instrument-scoped state-consistency gate:
/// engage → gate-safe reconciliation → release readiness → stability window → release.
/// </summary>
public sealed class MismatchEscalationCoordinator
{
    private readonly Func<AccountSnapshot> _getSnapshot;
    private readonly Func<IReadOnlyList<string>> _getActiveInstruments;
    private readonly Func<AccountSnapshot, DateTimeOffset, IReadOnlyList<MismatchObservation>> _getMismatchObservations;
    private readonly Func<string, bool> _isInstrumentBlocked;
    private readonly Func<string, bool> _isFlattenInProgress;
    private readonly Func<string, bool> _isRecoveryInProgress;
    private readonly RobotLogger? _log;
    private readonly ExecutionEventWriter? _eventWriter;
    private readonly Func<string, DateTimeOffset, GateReconciliationResult?>? _runInstrumentGateReconciliation;
    private readonly Func<string, AccountSnapshot, DateTimeOffset, StateConsistencyReleaseReadinessResult>? _evaluateReleaseReadiness;
    private readonly int _stableWindowMs;
    private readonly Timer _auditTimer;
    private DateTimeOffset _lastAuditUtc = DateTimeOffset.MinValue;

    private readonly ConcurrentDictionary<string, MismatchInstrumentState> _stateByInstrument = new(StringComparer.OrdinalIgnoreCase);

    private int _mismatchDetectedCount;
    private int _mismatchPersistentCount;
    private int _mismatchFailClosedCount;
    private int _mismatchClearedCount;
    private int _mismatchBrokerAheadCount;
    private int _mismatchJournalAheadCount;
    private int _mismatchPositionQtyCount;
    private int _mismatchRegistryMissingCount;
    private int _mismatchProtectiveDivergenceCount;

    public MismatchEscalationCoordinator(
        Func<AccountSnapshot> getSnapshot,
        Func<IReadOnlyList<string>> getActiveInstruments,
        Func<AccountSnapshot, DateTimeOffset, IReadOnlyList<MismatchObservation>> getMismatchObservations,
        Func<string, bool> isInstrumentBlocked,
        Func<string, bool> isFlattenInProgress,
        Func<string, bool> isRecoveryInProgress,
        RobotLogger? log,
        ExecutionEventWriter? eventWriter = null,
        Func<string, DateTimeOffset, GateReconciliationResult?>? runInstrumentGateReconciliation = null,
        Func<string, AccountSnapshot, DateTimeOffset, StateConsistencyReleaseReadinessResult>? evaluateReleaseReadiness = null,
        int stateConsistencyStableWindowMs = 0)
    {
        _getSnapshot = getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));
        _getActiveInstruments = getActiveInstruments ?? (() => Array.Empty<string>());
        _getMismatchObservations = getMismatchObservations ?? ((_, _) => Array.Empty<MismatchObservation>());
        _isInstrumentBlocked = isInstrumentBlocked ?? (_ => false);
        _isFlattenInProgress = isFlattenInProgress ?? (_ => false);
        _isRecoveryInProgress = isRecoveryInProgress ?? (_ => false);
        _log = log;
        _eventWriter = eventWriter;
        _runInstrumentGateReconciliation = runInstrumentGateReconciliation;
        _evaluateReleaseReadiness = evaluateReleaseReadiness;
        _stableWindowMs = stateConsistencyStableWindowMs > 0
            ? stateConsistencyStableWindowMs
            : MismatchEscalationPolicy.STATE_CONSISTENCY_STABLE_WINDOW_MS_LIVE;

        _auditTimer = new Timer(OnAuditTick, null, MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS, MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS);
    }

    public bool IsInstrumentBlockedByMismatch(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return false;
        return _stateByInstrument.TryGetValue(instrument.Trim(), out var s) && s.Blocked;
    }

    public string? GetBlockReason(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return null;
        if (!_stateByInstrument.TryGetValue(instrument.Trim(), out var s) || !s.Blocked) return null;
        return string.IsNullOrEmpty(s.BlockReason) ? MismatchEscalationPolicy.BLOCK_REASON_PERSISTENT_MISMATCH : s.BlockReason;
    }

    /// <summary>P1.5: Current gate phase for diagnostics/tests.</summary>
    public GateLifecyclePhase GetGateLifecyclePhase(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return GateLifecyclePhase.None;
        return _stateByInstrument.TryGetValue(instrument.Trim(), out var s) ? s.GateLifecyclePhase : GateLifecyclePhase.None;
    }

    private void OnAuditTick(object? _)
    {
        try
        {
            var utcNow = DateTimeOffset.UtcNow;
            var snapshot = _getSnapshot();
            var instruments = _getActiveInstruments();

            if (instruments.Count == 0 && snapshot.Positions != null)
            {
                var fromPositions = snapshot.Positions
                    .Where(p => (p.Quantity != 0 || !string.IsNullOrWhiteSpace(p.Instrument)) && !string.IsNullOrWhiteSpace(p.Instrument))
                    .Select(p => p.Instrument!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                instruments = fromPositions;
            }

            var observations = _getMismatchObservations(snapshot, utcNow);
            var obsByInstrument = observations
                .Where(o => !string.IsNullOrWhiteSpace(o.Instrument))
                .GroupBy(o => o.Instrument.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var instrumentsSet = new HashSet<string>(instruments.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()), StringComparer.OrdinalIgnoreCase);
            foreach (var k in obsByInstrument.Keys)
                instrumentsSet.Add(k);
            foreach (var kv in _stateByInstrument)
            {
                if (kv.Value.GateLifecyclePhase != GateLifecyclePhase.None)
                    instrumentsSet.Add(kv.Key);
            }

            foreach (var inst in instrumentsSet)
            {
                if (obsByInstrument.TryGetValue(inst, out var obs))
                    ProcessMismatchPresent(obs);
                else
                    ProcessMismatchSignalAbsent(inst, utcNow);
            }

            foreach (var inst in instrumentsSet.Where(IsGateActiveForInstrument))
                AdvanceStateConsistencyGate(inst, snapshot, utcNow);

            _lastAuditUtc = utcNow;
        }
        catch (Exception ex)
        {
            _log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                new { error = ex.Message, context = "MismatchEscalationCoordinator.OnAuditTick" }));
        }
    }

    private bool IsGateActiveForInstrument(string inst)
    {
        if (!_stateByInstrument.TryGetValue(inst, out var s)) return false;
        return s.GateLifecyclePhase != GateLifecyclePhase.None;
    }

    private void EmitCanonical(string inst, string eventType, DateTimeOffset utc, object payload, string severity = "INFO")
    {
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

        if (state.EscalationState == MismatchEscalationState.NONE)
        {
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
            _mismatchDetectedCount++;
            IncrementTypeMetric(obs.MismatchType);
            var detectedPayload = ToGatePayload(state, inst, utcNow, obs, null, null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_DETECTED", detectedPayload));
            EmitCanonical(inst, ExecutionEventTypes.MISMATCH_DETECTED, utcNow, detectedPayload, "WARN");
            var gatePayload = ToGatePayload(state, inst, utcNow, obs, null, null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_ENGAGED", gatePayload));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_ENGAGED", utcNow, gatePayload, "WARN");
            return;
        }

        state.LastDetectedUtc = utcNow;
        state.PersistenceMs = (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds;

        if (state.EscalationState == MismatchEscalationState.DETECTED)
        {
            if (state.PersistenceMs >= MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS)
            {
                state.EscalationState = MismatchEscalationState.PERSISTENT_MISMATCH;
                state.Blocked = true;
                state.BlockReason = MismatchEscalationPolicy.BLOCK_REASON_PERSISTENT_MISMATCH;
                state.GateLifecyclePhase = GateLifecyclePhase.PersistentMismatch;
                _mismatchPersistentCount++;
                var p = ToGatePayload(state, inst, utcNow, obs, null, null);
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_PERSISTENT", p));
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_BLOCKED", p));
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH", p));
                EmitCanonical(inst, "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH", utcNow, p, "ERROR");
            }
            return;
        }

        if (state.EscalationState == MismatchEscalationState.PERSISTENT_MISMATCH)
        {
            state.PersistenceMs = (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds;
            if (state.PersistenceMs >= MismatchEscalationPolicy.MISMATCH_FAIL_CLOSED_THRESHOLD_MS ||
                state.RetryCount >= MismatchEscalationPolicy.MISMATCH_MAX_RETRIES)
            {
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

    private void AdvanceStateConsistencyGate(string inst, AccountSnapshot snapshot, DateTimeOffset utcNow)
    {
        if (!_stateByInstrument.TryGetValue(inst, out var state))
            return;
        if (state.GateLifecyclePhase == GateLifecyclePhase.None)
            return;
        if (state.EscalationState == MismatchEscalationState.FAIL_CLOSED)
            return;

        state.PersistenceMs = state.FirstDetectedUtc != default
            ? (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds
            : 0;

        if (state.EscalationState == MismatchEscalationState.DETECTED &&
            state.PersistenceMs >= MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS)
        {
            state.EscalationState = MismatchEscalationState.PERSISTENT_MISMATCH;
            state.Blocked = true;
            state.BlockReason = MismatchEscalationPolicy.BLOCK_REASON_PERSISTENT_MISMATCH;
            state.GateLifecyclePhase = GateLifecyclePhase.PersistentMismatch;
            _mismatchPersistentCount++;
            var p = ToGatePayload(state, inst, utcNow, null, null, null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH", p));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH", utcNow, p, "ERROR");
        }

        if (state.GateLifecyclePhase == GateLifecyclePhase.DetectedBlocked)
        {
            state.GateLifecyclePhase = GateLifecyclePhase.Reconciling;
            var startPayload = ToGatePayload(state, inst, utcNow, null, null, null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED", startPayload));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED", utcNow, startPayload, "INFO");
        }

        GateReconciliationResult? recon = null;
        try
        {
            recon = _runInstrumentGateReconciliation?.Invoke(inst, utcNow);
        }
        catch (Exception ex)
        {
            _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                new { error = ex.Message, context = "runInstrumentGateReconciliation", instrument = inst }));
        }

        StateConsistencyReleaseReadinessResult readiness;
        try
        {
            readiness = _evaluateReleaseReadiness?.Invoke(inst, snapshot, utcNow)
                        ?? StateConsistencyReleaseEvaluator.Indeterminate(inst);
        }
        catch (Exception ex)
        {
            _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                new { error = ex.Message, context = "evaluateReleaseReadiness", instrument = inst }));
            readiness = StateConsistencyReleaseEvaluator.Indeterminate(inst, "evaluate_exception");
        }

        if (recon != null)
            recon.ReleaseReadyAfter = readiness.ReleaseReady;

        var resultPayload = ToGatePayload(state, inst, utcNow, null, recon, readiness);
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT", resultPayload));
        EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT", utcNow, resultPayload, "INFO");

        if (state.EscalationState == MismatchEscalationState.FAIL_CLOSED ||
            state.GateLifecyclePhase == GateLifecyclePhase.FailClosed)
            return;

        // Stronger block: no auto-release from persistent / fail-closed (P1.5 + existing escalation semantics).
        if (state.EscalationState == MismatchEscalationState.PERSISTENT_MISMATCH ||
            state.GateLifecyclePhase == GateLifecyclePhase.PersistentMismatch)
            return;

        var windowMs = state.StableWindowMsApplied > 0 ? state.StableWindowMsApplied : _stableWindowMs;

        if (!readiness.ReleaseReady)
        {
            if (state.GateLifecyclePhase == GateLifecyclePhase.StablePendingRelease)
            {
                var resetPayload = new
                {
                    gate = ToGatePayload(state, inst, utcNow, null, recon, readiness),
                    stable_reset_reason = readiness.Summary
                };
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RESTABILIZATION_RESET", resetPayload));
                EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RESTABILIZATION_RESET", utcNow, resetPayload, "WARN");
                state.GateLifecyclePhase = GateLifecyclePhase.Reconciling;
                state.FirstConsistentUtc = default;
            }
            return;
        }

        state.LastConsistentUtc = utcNow;

        if (state.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
        {
            state.GateLifecyclePhase = GateLifecyclePhase.StablePendingRelease;
            state.FirstConsistentUtc = utcNow;
            var spPayload = ToGatePayload(state, inst, utcNow, null, recon, readiness);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_STABLE_PENDING_RELEASE", spPayload));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_STABLE_PENDING_RELEASE", utcNow, spPayload, "INFO");
            return;
        }

        var stableMs = state.FirstConsistentUtc != default
            ? (long)(utcNow - state.FirstConsistentUtc).TotalMilliseconds
            : 0;

        if (stableMs < windowMs)
            return;

        state.Blocked = false;
        state.BlockReason = "";
        state.EscalationState = MismatchEscalationState.NONE;
        state.GateLifecyclePhase = GateLifecyclePhase.None;
        state.LastReleaseUtc = utcNow;
        state.FirstConsistentUtc = default;
        state.ConsecutiveCleanPassCount = 0;
        state.MismatchStillPresent = false;
        _mismatchClearedCount++;

        var releasedPayload = new
        {
            gate = ToGatePayload(state, inst, utcNow, null, recon, readiness),
            stable_for_ms = stableMs,
            stable_window_ms = windowMs
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RELEASED", releasedPayload));
        EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RELEASED", utcNow, releasedPayload, "INFO");
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_CLEARED", ToGatePayload(state, inst, utcNow, null, null, null)));
    }

    private object ToGatePayload(
        MismatchInstrumentState state,
        string instrument,
        DateTimeOffset utcNow,
        MismatchObservation? obs,
        GateReconciliationResult? recon,
        StateConsistencyReleaseReadinessResult? readiness)
    {
        var mismatchAge = state.FirstDetectedUtc != default
            ? (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds
            : 0L;
        var stableFor = state.FirstConsistentUtc != default && state.GateLifecyclePhase == GateLifecyclePhase.StablePendingRelease
            ? (long)(utcNow - state.FirstConsistentUtc).TotalMilliseconds
            : 0L;

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
            stable_for_ms = stableFor,
            stable_window_ms = state.StableWindowMsApplied > 0 ? state.StableWindowMsApplied : _stableWindowMs,
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
            mismatch_type = state.MismatchType.ToString(),
            persistence_ms = state.PersistenceMs,
            timestamp_utc = utcNow.ToString("o"),
            gate_action_policy = StateConsistencyGateActionPolicy.PolicyNote
        };
    }

    private void IncrementTypeMetric(MismatchType type)
    {
        switch (type)
        {
            case MismatchType.BROKER_AHEAD: _mismatchBrokerAheadCount++; break;
            case MismatchType.JOURNAL_AHEAD: _mismatchJournalAheadCount++; break;
            case MismatchType.POSITION_QTY_MISMATCH: _mismatchPositionQtyCount++; break;
            case MismatchType.ORDER_REGISTRY_MISSING: _mismatchRegistryMissingCount++; break;
            case MismatchType.PROTECTIVE_STATE_DIVERGENCE: _mismatchProtectiveDivergenceCount++; break;
        }
    }

    internal void ProcessObservationForTest(MismatchObservation obs) => ProcessMismatchPresent(obs);

    internal void ProcessCleanPassForTest(string instrument, DateTimeOffset utcNow) =>
        ProcessMismatchSignalAbsent(instrument, utcNow);

    internal MismatchInstrumentState? GetStateForTest(string instrument) =>
        _stateByInstrument.TryGetValue(instrument, out var s) ? s : null;

    /// <summary>For P1.5 tests: advance gate for one instrument (same as audit tail).</summary>
    internal void AdvanceStateConsistencyGateForTest(string instrument, AccountSnapshot snapshot, DateTimeOffset utcNow) =>
        AdvanceStateConsistencyGate(instrument.Trim(), snapshot, utcNow);

    internal void SetStableWindowForTest(int ms)
    {
        foreach (var kv in _stateByInstrument)
            kv.Value.StableWindowMsApplied = ms;
    }

    public void EmitMetrics(DateTimeOffset utcNow)
    {
        var instruments = _stateByInstrument
            .Where(kv => kv.Value.EscalationState != MismatchEscalationState.NONE || kv.Value.GateLifecyclePhase != GateLifecyclePhase.None)
            .Select(kv => new
            {
                instrument = kv.Key,
                mismatch_type = kv.Value.MismatchType.ToString(),
                escalation_state = kv.Value.EscalationState.ToString(),
                gate_phase = kv.Value.GateLifecyclePhase.ToString(),
                blocked = kv.Value.Blocked,
                persistence_ms = kv.Value.PersistenceMs,
                retry_count = kv.Value.RetryCount,
                first_detected_utc = kv.Value.FirstDetectedUtc.ToString("o"),
                last_detected_utc = kv.Value.LastDetectedUtc.ToString("o"),
                block_reason = kv.Value.BlockReason
            })
            .ToList();

        _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "RECONCILIATION_MISMATCH_METRICS", state: "ENGINE",
            new
            {
                mismatch_detected_count = _mismatchDetectedCount,
                mismatch_persistent_count = _mismatchPersistentCount,
                mismatch_fail_closed_count = _mismatchFailClosedCount,
                mismatch_cleared_count = _mismatchClearedCount,
                mismatch_broker_ahead_count = _mismatchBrokerAheadCount,
                mismatch_journal_ahead_count = _mismatchJournalAheadCount,
                mismatch_position_qty_count = _mismatchPositionQtyCount,
                mismatch_registry_missing_count = _mismatchRegistryMissingCount,
                mismatch_protective_divergence_count = _mismatchProtectiveDivergenceCount,
                last_audit_utc = _lastAuditUtc != DateTimeOffset.MinValue ? _lastAuditUtc.ToString("o") : null,
                instruments
            }));
    }
}
