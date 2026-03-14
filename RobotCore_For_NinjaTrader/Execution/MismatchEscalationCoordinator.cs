// Gap 4: Persistent Mismatch Escalation — timer-based coordinator.
// Detects when broker, journals, registry, lifecycle fail to converge.
// Bounded detection and escalation only; no retry loops.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Runs periodic mismatch audits across active instruments.
/// Escalates DETECTED -> PERSISTENT_MISMATCH -> FAIL_CLOSED by policy.
/// Requires 2 consecutive clean passes to clear (FAIL_CLOSED does not auto-clear).
/// </summary>
public sealed class MismatchEscalationCoordinator
{
    private readonly Func<AccountSnapshot> _getSnapshot;
    private readonly Func<IReadOnlyList<string>> _getActiveInstruments;
    private readonly Func<IReadOnlyList<MismatchObservation>> _getMismatchObservations;
    private readonly Func<string, bool> _isInstrumentBlocked;
    private readonly Func<string, bool> _isFlattenInProgress;
    private readonly Func<string, bool> _isRecoveryInProgress;
    private readonly RobotLogger? _log;
    private readonly ExecutionEventWriter? _eventWriter;
    private readonly Timer _auditTimer;
    private DateTimeOffset _lastAuditUtc = DateTimeOffset.MinValue;

    private readonly ConcurrentDictionary<string, MismatchInstrumentState> _stateByInstrument = new(StringComparer.OrdinalIgnoreCase);

    // Metrics
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
        Func<IReadOnlyList<MismatchObservation>> getMismatchObservations,
        Func<string, bool> isInstrumentBlocked,
        Func<string, bool> isFlattenInProgress,
        Func<string, bool> isRecoveryInProgress,
        RobotLogger? log,
        ExecutionEventWriter? eventWriter = null)
    {
        _getSnapshot = getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));
        _getActiveInstruments = getActiveInstruments ?? (() => Array.Empty<string>());
        _getMismatchObservations = getMismatchObservations ?? (() => Array.Empty<MismatchObservation>());
        _isInstrumentBlocked = isInstrumentBlocked ?? (_ => false);
        _isFlattenInProgress = isFlattenInProgress ?? (_ => false);
        _isRecoveryInProgress = isRecoveryInProgress ?? (_ => false);
        _log = log;
        _eventWriter = eventWriter;

        _auditTimer = new Timer(OnAuditTick, null, MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS, MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS);
    }

    /// <summary>True if instrument is blocked by persistent mismatch.</summary>
    public bool IsInstrumentBlockedByMismatch(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return false;
        return _stateByInstrument.TryGetValue(instrument.Trim(), out var s) && s.Blocked;
    }

    /// <summary>Block reason when blocked by mismatch. Null if not blocked.</summary>
    public string? GetBlockReason(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return null;
        if (!_stateByInstrument.TryGetValue(instrument.Trim(), out var s) || !s.Blocked) return null;
        return string.IsNullOrEmpty(s.BlockReason) ? MismatchEscalationPolicy.BLOCK_REASON_PERSISTENT_MISMATCH : s.BlockReason;
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

            var observations = _getMismatchObservations();
            var obsByInstrument = observations
                .Where(o => !string.IsNullOrWhiteSpace(o.Instrument))
                .GroupBy(o => o.Instrument.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var instrumentsSet = new HashSet<string>(instruments.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()), StringComparer.OrdinalIgnoreCase);
            foreach (var k in obsByInstrument.Keys)
                instrumentsSet.Add(k);

            foreach (var inst in instrumentsSet)
            {
                if (obsByInstrument.TryGetValue(inst, out var obs))
                    ProcessObservation(obs);
                else
                    ProcessCleanPass(inst, utcNow);
            }

            _lastAuditUtc = utcNow;
        }
        catch (Exception ex)
        {
            _log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                new { error = ex.Message, context = "MismatchEscalationCoordinator.OnAuditTick" }));
        }
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

    private void ProcessCleanPass(string instrument, DateTimeOffset utcNow)
    {
        if (!_stateByInstrument.TryGetValue(instrument, out var state))
            return;

        if (state.EscalationState == MismatchEscalationState.NONE)
            return;
        if (state.EscalationState == MismatchEscalationState.FAIL_CLOSED)
            return;

        var newCount = state.ConsecutiveCleanPassCount + 1;
        state.ConsecutiveCleanPassCount = newCount;
        state.LastSeenUtc = utcNow;
        state.MismatchStillPresent = false;

        if (newCount >= MismatchEscalationPolicy.MISMATCH_CLEAR_CONSECUTIVE_CLEAN_PASSES)
        {
            state.EscalationState = MismatchEscalationState.NONE;
            state.Blocked = false;
            state.BlockReason = "";
            state.ConsecutiveCleanPassCount = 0;
            _mismatchClearedCount++;
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "RECONCILIATION_MISMATCH_CLEARED", ToPayload(state, instrument, utcNow)));
        }
    }

    private void ProcessObservation(MismatchObservation obs)
    {
        var inst = obs.Instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return;

        var state = _stateByInstrument.GetOrAdd(inst, _ => new MismatchInstrumentState());
        var utcNow = obs.ObservedUtc;

        if (!obs.Present)
        {
            ProcessCleanPass(inst, utcNow);
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
            _mismatchDetectedCount++;
            IncrementTypeMetric(obs.MismatchType);
            var detectedPayload = ToPayload(state, inst, utcNow, obs);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_DETECTED", detectedPayload));
            EmitCanonical(inst, ExecutionEventTypes.MISMATCH_DETECTED, utcNow, detectedPayload, "WARN");
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
                _mismatchPersistentCount++;
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_PERSISTENT", ToPayload(state, inst, utcNow, obs)));
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_BLOCKED", ToPayload(state, inst, utcNow, obs)));
            }
            return;
        }

        if (state.EscalationState == MismatchEscalationState.PERSISTENT_MISMATCH)
        {
            if (state.PersistenceMs >= MismatchEscalationPolicy.MISMATCH_FAIL_CLOSED_THRESHOLD_MS ||
                state.RetryCount >= MismatchEscalationPolicy.MISMATCH_MAX_RETRIES)
            {
                state.EscalationState = MismatchEscalationState.FAIL_CLOSED;
                _mismatchFailClosedCount++;
                var failClosedPayload = ToPayload(state, inst, utcNow, obs);
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_FAIL_CLOSED", failClosedPayload));
                EmitCanonical(inst, ExecutionEventTypes.MISMATCH_FAIL_CLOSED, utcNow, failClosedPayload, "CRITICAL");
            }
        }
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

    private static object ToPayload(MismatchInstrumentState state, string instrument, DateTimeOffset utcNow, MismatchObservation? obs = null)
    {
        return new
        {
            instrument,
            mismatch_type = state.MismatchType.ToString(),
            escalation_state = state.EscalationState.ToString(),
            persistence_ms = state.PersistenceMs,
            retry_count = state.RetryCount,
            block_reason = state.BlockReason,
            broker_qty = obs?.BrokerQty ?? 0,
            local_qty = obs?.LocalQty ?? 0,
            summary = state.LastSummary ?? obs?.Summary,
            timestamp_utc = utcNow.ToString("o")
        };
    }

    /// <summary>For tests: process an observation directly (bypasses timer).</summary>
    internal void ProcessObservationForTest(MismatchObservation obs)
    {
        ProcessObservation(obs);
    }

    /// <summary>For tests: process clean pass directly.</summary>
    internal void ProcessCleanPassForTest(string instrument, DateTimeOffset utcNow)
    {
        ProcessCleanPass(instrument, utcNow);
    }

    /// <summary>For tests: get current state (read-only).</summary>
    internal MismatchInstrumentState? GetStateForTest(string instrument)
    {
        return _stateByInstrument.TryGetValue(instrument, out var s) ? s : null;
    }

    /// <summary>Emit metrics. Call periodically (e.g. from heartbeat).</summary>
    public void EmitMetrics(DateTimeOffset utcNow)
    {
        var instruments = _stateByInstrument
            .Where(kv => kv.Value.EscalationState != MismatchEscalationState.NONE)
            .Select(kv => new
            {
                instrument = kv.Key,
                mismatch_type = kv.Value.MismatchType.ToString(),
                escalation_state = kv.Value.EscalationState.ToString(),
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
