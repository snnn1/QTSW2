// Gap 3: Protective Coverage Audit — coordinator and timer-based audits.
// Phase 3: Per-instrument recovery state, immediate block on critical failure, 2-pass clear.
// Phase 4: Bounded corrective workflow — one corrective attempt, AWAITING_CONFIRMATION, timeout escalation.
// Phase 5: Emergency flatten escalation when corrective times out or NO_SAFE_STOP_PRICE.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Runs periodic protective coverage audits across active instruments.
/// Phase 3: Blocks instruments on critical protective failure; requires 2 consecutive clean passes to clear.
/// </summary>
public sealed class ProtectiveCoverageCoordinator
{
    private readonly Func<AccountSnapshot> _getSnapshot;
    private readonly Func<IReadOnlyList<string>> _getActiveInstruments;
    private readonly Func<string, IReadOnlyCollection<string>>? _getActiveIntentIdsForInstrument;
    private readonly Func<string, bool> _isFlattenInProgress;
    private readonly Func<string, bool> _isRecoveryInProgress;
    private readonly Func<string, bool> _isInstrumentBlockedExternal;
    private readonly Func<ProtectiveCorrectiveRequest, ProtectiveCorrectiveResult>? _submitCorrective;
    private readonly Func<string, DateTimeOffset, FlattenResult>? _emergencyFlatten;
    private readonly RobotLogger? _log;
    private readonly ExecutionEventWriter? _eventWriter;
    private readonly Timer _auditTimer;
    private DateTimeOffset _lastAuditUtc = DateTimeOffset.MinValue;

    /// <summary>Per-instrument state. Key: instrument (case-insensitive).</summary>
    private readonly ConcurrentDictionary<string, ProtectiveInstrumentState> _stateByInstrument = new(StringComparer.OrdinalIgnoreCase);

    // Metrics
    private int _auditOkCount;
    private int _auditFailureCount;
    private int _missingStopCount;
    private int _qtyMismatchCount;

    public ProtectiveCoverageCoordinator(
        Func<AccountSnapshot> getSnapshot,
        Func<IReadOnlyList<string>> getActiveInstruments,
        Func<string, bool> isFlattenInProgress,
        Func<string, bool> isRecoveryInProgress,
        Func<string, bool> isInstrumentBlocked,
        RobotLogger? log,
        Func<ProtectiveCorrectiveRequest, ProtectiveCorrectiveResult>? submitCorrective = null,
        Func<string, DateTimeOffset, FlattenResult>? emergencyFlatten = null,
        ExecutionEventWriter? eventWriter = null,
        Func<string, IReadOnlyCollection<string>>? getActiveIntentIdsForInstrument = null)
    {
        _getSnapshot = getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));
        _getActiveInstruments = getActiveInstruments ?? (() => Array.Empty<string>());
        _getActiveIntentIdsForInstrument = getActiveIntentIdsForInstrument;
        _isFlattenInProgress = isFlattenInProgress ?? (_ => false);
        _isRecoveryInProgress = isRecoveryInProgress ?? (_ => false);
        _isInstrumentBlockedExternal = isInstrumentBlocked ?? (_ => false);
        _submitCorrective = submitCorrective;
        _emergencyFlatten = emergencyFlatten;
        _log = log;
        _eventWriter = eventWriter;

        _auditTimer = new Timer(OnAuditTick, null, ProtectiveAuditPolicy.PROTECTIVE_AUDIT_INTERVAL_ACTIVE_MS, ProtectiveAuditPolicy.PROTECTIVE_AUDIT_INTERVAL_ACTIVE_MS);
    }

    /// <summary>True if instrument is blocked by protective failure (distinct from queue poison, IEA timeout).</summary>
    public bool IsInstrumentBlockedByProtective(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return false;
        return _stateByInstrument.TryGetValue(instrument.Trim(), out var s) && s.Blocked;
    }

    /// <summary>Block reason when blocked by protective. Null if not blocked by protective.</summary>
    public string? GetBlockReason(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return null;
        if (!_stateByInstrument.TryGetValue(instrument.Trim(), out var s) || !s.Blocked) return null;
        return string.IsNullOrEmpty(s.BlockReason) ? ProtectiveAuditPolicy.BLOCK_REASON_PROTECTIVE_FAILURE : s.BlockReason;
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
                    .Where(p => p.Quantity != 0 && !string.IsNullOrWhiteSpace(p.Instrument))
                    .Select(p => p.Instrument!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                instruments = fromPositions;
            }

            foreach (var instrument in instruments)
            {
                if (string.IsNullOrWhiteSpace(instrument)) continue;

                var inst = instrument.Trim();
                var blockedForAudit = _isInstrumentBlockedExternal(inst) || IsInstrumentBlockedByProtective(inst);
                var activeIntentIds = _getActiveIntentIdsForInstrument?.Invoke(inst);

                var result = ProtectiveCoverageAudit.Audit(
                    inst,
                    snapshot,
                    activeIntentIds,
                    flattenInProgress: _isFlattenInProgress(inst),
                    recoveryInProgress: _isRecoveryInProgress(inst),
                    instrumentBlocked: blockedForAudit,
                    utcNow);

                ProcessResult(result, activeIntentIds, utcNow);
            }

            _lastAuditUtc = utcNow;
        }
        catch (Exception ex)
        {
            _log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "PROTECTIVE_AUDIT_FAILED", state: "ENGINE",
                new { error = ex.Message, context = "ProtectiveCoverageCoordinator.OnAuditTick" }));
        }
    }

    private ProtectiveInstrumentState GetOrAddState(string instrument)
    {
        return _stateByInstrument.GetOrAdd(instrument, _ => new ProtectiveInstrumentState());
    }

    private void EmitCanonical(string inst, string eventType, DateTimeOffset utc, object payload, string severity = "INFO")
    {
        _eventWriter?.Emit(new CanonicalExecutionEvent
        {
            TimestampUtc = utc.ToString("o"),
            Instrument = inst,
            EventType = eventType,
            Severity = severity,
            Source = "ProtectiveCoverageCoordinator",
            Payload = payload
        });
    }

    private void ProcessResult(ProtectiveAuditResult result, IReadOnlyCollection<string>? activeIntentIds, DateTimeOffset utcNow)
    {
        var inst = result.Instrument;
        if (string.IsNullOrWhiteSpace(inst)) return;

        // Diagnostic: log audit context for protective_missing_stop and critical failures
        if (ProtectiveCoverageAudit.IsCritical(result.Status) || result.Status == ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP)
        {
            var expectedCount = activeIntentIds?.Count ?? -1;
            var foundStopQty = result.StopQty;
            var reason = activeIntentIds == null ? "expected_intent_set_null" : (activeIntentIds.Count == 0 ? "expected_intent_set_empty" : null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "PROTECTIVE_AUDIT_CONTEXT", new
            {
                instrument = inst,
                audit_status = result.Status.ToString(),
                expected_active_intent_ids_count = expectedCount,
                found_protective_stop_qty = foundStopQty,
                broker_position_qty = result.BrokerPositionQty,
                context_wiring_reason = reason ?? "ok"
            }));
        }

        var state = GetOrAddState(inst);

        // Clean or suppress (flatten/recovery in progress)
        if (result.Status == ProtectiveAuditStatus.PROTECTIVE_OK ||
            result.Status == ProtectiveAuditStatus.PROTECTIVE_FLATTEN_IN_PROGRESS ||
            result.Status == ProtectiveAuditStatus.PROTECTIVE_RECOVERY_IN_PROGRESS)
        {
            _auditOkCount++;
            if (result.Status == ProtectiveAuditStatus.PROTECTIVE_OK && result.BrokerPositionQty != 0)
            {
                _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, "PROTECTIVE_AUDIT_OK", ToPayload(result)));
            }

            // Phase 5: FLATTEN_IN_PROGRESS + broker flat → LOCKED_FAIL_CLOSED (remain blocked until explicit clear)
            if (result.Status == ProtectiveAuditStatus.PROTECTIVE_OK && state.RecoveryState == ProtectiveRecoveryState.FLATTEN_IN_PROGRESS)
            {
                state.RecoveryState = ProtectiveRecoveryState.LOCKED_FAIL_CLOSED;
                _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, "PROTECTIVE_FLATTEN_COMPLETED", ToPayload(result)));
                return;
            }

            // Clean pass: if in DETECTED or AWAITING_CONFIRMATION and blocked, require 2 consecutive clean passes to clear
            if (result.Status == ProtectiveAuditStatus.PROTECTIVE_OK &&
                (state.RecoveryState == ProtectiveRecoveryState.DETECTED || state.RecoveryState == ProtectiveRecoveryState.AWAITING_CONFIRMATION) &&
                state.Blocked)
            {
                var newCount = state.ConsecutiveCleanPassCount + 1;
                state.ConsecutiveCleanPassCount = newCount;
                state.LastAuditStatus = result.Status;

                if (newCount >= ProtectiveAuditPolicy.PROTECTIVE_CLEAR_CONSECUTIVE_CLEAN_PASSES)
                {
                    state.RecoveryState = ProtectiveRecoveryState.NONE;
                    state.Blocked = false;
                    state.BlockReason = "";
                    state.ConsecutiveCleanPassCount = 0;
                    state.AwaitingConfirmationUntilUtc = default;
                    _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, "PROTECTIVE_RECOVERY_CONFIRMED", ToPayload(result)));
                    EmitCanonical(inst, ExecutionEventTypes.PROTECTIVE_RECOVERY_CONFIRMED, result.AuditUtc, ToPayload(result));
                }
            }
            return;
        }

        // Non-critical (missing target, target qty mismatch): emit events, do NOT block
        if (!ProtectiveCoverageAudit.IsCritical(result.Status))
        {
            _auditFailureCount++;
            var eventType = GetEventTypeForStatus(result.Status);
            _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, eventType, ToPayload(result)));
            EmitCanonical(inst, ExecutionEventTypes.PROTECTIVE_AUDIT_RESULT, result.AuditUtc, ToPayload(result), "WARN");
            return;
        }

        // Phase 5: ESCALATE_TO_FLATTEN → trigger emergency flatten once, transition to FLATTEN_IN_PROGRESS
        if (state.RecoveryState == ProtectiveRecoveryState.ESCALATE_TO_FLATTEN)
        {
            if (!state.EmergencyFlattenTriggered && _emergencyFlatten != null)
            {
                state.EmergencyFlattenTriggered = true;
                var flattenResult = _emergencyFlatten(inst, result.AuditUtc);
                state.RecoveryState = ProtectiveRecoveryState.FLATTEN_IN_PROGRESS;
                _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, "PROTECTIVE_EMERGENCY_FLATTEN_TRIGGERED", new
                {
                    instrument = inst,
                    success = flattenResult.Success,
                    error = flattenResult.ErrorMessage
                }));
            }
            else if (!state.EmergencyFlattenTriggered && _emergencyFlatten == null)
            {
                state.RecoveryState = ProtectiveRecoveryState.FLATTEN_IN_PROGRESS;
                _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, "PROTECTIVE_ESCALATE_NO_CALLBACK", ToPayload(result)));
            }
            return;
        }

        // Phase 5: FLATTEN_IN_PROGRESS + critical — stay, do NOT retrigger flatten
        if (state.RecoveryState == ProtectiveRecoveryState.FLATTEN_IN_PROGRESS)
        {
            state.LastDetectedUtc = result.AuditUtc;
            state.LastAuditStatus = result.Status;
            return;
        }

        // Critical: guard — do NOT resubmit while in CORRECTIVE_SUBMITTING or AWAITING_CONFIRMATION
        if (state.RecoveryState == ProtectiveRecoveryState.CORRECTIVE_SUBMITTING ||
            state.RecoveryState == ProtectiveRecoveryState.AWAITING_CONFIRMATION)
        {
            state.LastDetectedUtc = result.AuditUtc;
            state.LastAuditStatus = result.Status;

            // AWAITING_CONFIRMATION + timeout expired + still critical → ESCALATE_TO_FLATTEN
            if (state.RecoveryState == ProtectiveRecoveryState.AWAITING_CONFIRMATION &&
                result.AuditUtc >= state.AwaitingConfirmationUntilUtc &&
                state.AwaitingConfirmationUntilUtc != default)
            {
                state.RecoveryState = ProtectiveRecoveryState.ESCALATE_TO_FLATTEN;
                _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, "PROTECTIVE_ESCALATE_TO_FLATTEN", ToPayload(result)));
            }
            return;
        }

        _auditFailureCount++;
        if (result.Status == ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP) _missingStopCount++;
        if (result.Status == ProtectiveAuditStatus.PROTECTIVE_STOP_QTY_MISMATCH ||
            result.Status == ProtectiveAuditStatus.PROTECTIVE_TARGET_QTY_MISMATCH) _qtyMismatchCount++;

        var eventTypeCritical = GetEventTypeForStatus(result.Status);
        _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, eventTypeCritical, ToPayload(result)));

        if (state.RecoveryState == ProtectiveRecoveryState.NONE)
        {
            state.RecoveryState = ProtectiveRecoveryState.DETECTED;
            state.FirstDetectedUtc = result.AuditUtc;
            state.LastDetectedUtc = result.AuditUtc;
            state.LastAuditStatus = result.Status;
            state.AttemptCount = 0;
            state.ConsecutiveCleanPassCount = 0;
            _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, "PROTECTIVE_RECOVERY_STARTED", ToPayload(result)));
            EmitCanonical(inst, ExecutionEventTypes.PROTECTIVE_RECOVERY_STARTED, result.AuditUtc, ToPayload(result), "WARN");
        }
        else
        {
            state.LastDetectedUtc = result.AuditUtc;
            state.LastAuditStatus = result.Status;
        }

        if (!state.Blocked)
        {
            state.Blocked = true;
            state.BlockReason = ProtectiveAuditPolicy.BLOCK_REASON_PROTECTIVE_FAILURE;
            _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, "PROTECTIVE_INSTRUMENT_BLOCKED", ToPayload(result)));
            EmitCanonical(inst, ExecutionEventTypes.PROTECTIVE_INSTRUMENT_BLOCKED, result.AuditUtc, ToPayload(result), "ERROR");
        }

        // Phase 4: Bounded corrective — one attempt for missing stop, stop qty mismatch, invalid price
        if (state.RecoveryState == ProtectiveRecoveryState.DETECTED &&
            state.AttemptCount < ProtectiveAuditPolicy.PROTECTIVE_MAX_CORRECTIVE_ATTEMPTS &&
            IsCorrectiveEligible(result.Status) &&
            _submitCorrective != null)
        {
            state.RecoveryState = ProtectiveRecoveryState.CORRECTIVE_SUBMITTING;
            var req = new ProtectiveCorrectiveRequest
            {
                Instrument = inst,
                BrokerPositionQty = result.BrokerPositionQty,
                BrokerDirection = result.BrokerDirection,
                Status = result.Status,
                AuditUtc = result.AuditUtc
            };
            var correctiveResult = _submitCorrective(req);
            state.AttemptCount++;
            _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, "PROTECTIVE_RECOVERY_SUBMITTED", new
            {
                instrument = inst,
                intent_id = correctiveResult.IntentId,
                submitted = correctiveResult.Submitted,
                failure_reason = correctiveResult.FailureReason,
                attempt_count = state.AttemptCount
            }));

            // Phase 5: NO_SAFE_STOP_PRICE → escalate to flatten immediately (non-flat + no safe stop = flatten problem)
            if (!correctiveResult.Submitted && string.Equals(correctiveResult.FailureReason, "NO_SAFE_STOP_PRICE", StringComparison.OrdinalIgnoreCase))
            {
                _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, "PROTECTIVE_ESCALATE_TO_FLATTEN", new
                {
                    instrument = inst,
                    reason = "NO_SAFE_STOP_PRICE",
                    note = "Corrective failed - no deterministic stop price; escalating to flatten"
                }));
                if (_emergencyFlatten != null && !state.EmergencyFlattenTriggered)
                {
                    state.EmergencyFlattenTriggered = true;
                    var flattenResult = _emergencyFlatten(inst, result.AuditUtc);
                    state.RecoveryState = ProtectiveRecoveryState.FLATTEN_IN_PROGRESS;
                    var noSafePayload = new { instrument = inst, success = flattenResult.Success, error = flattenResult.ErrorMessage, trigger_reason = "NO_SAFE_STOP_PRICE" };
                    _log?.Write(RobotEvents.ExecutionBase(result.AuditUtc, "", inst, "PROTECTIVE_EMERGENCY_FLATTEN_TRIGGERED", noSafePayload));
                    EmitCanonical(inst, ExecutionEventTypes.PROTECTIVE_EMERGENCY_FLATTEN_TRIGGERED, result.AuditUtc, noSafePayload);
                }
                else
                {
                    state.RecoveryState = _emergencyFlatten != null ? ProtectiveRecoveryState.FLATTEN_IN_PROGRESS : ProtectiveRecoveryState.ESCALATE_TO_FLATTEN;
                }
            }
            else
            {
                state.AwaitingConfirmationUntilUtc = result.AuditUtc.AddMilliseconds(ProtectiveAuditPolicy.PROTECTIVE_AWAITING_CONFIRMATION_MS);
                state.RecoveryState = ProtectiveRecoveryState.AWAITING_CONFIRMATION;
            }
        }
    }

    /// <summary>Phase 4: Only these statuses are eligible for corrective stop submission.</summary>
    private static bool IsCorrectiveEligible(ProtectiveAuditStatus status)
    {
        return status == ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP ||
               status == ProtectiveAuditStatus.PROTECTIVE_STOP_QTY_MISMATCH ||
               status == ProtectiveAuditStatus.PROTECTIVE_STOP_PRICE_INVALID;
    }

    private static string GetEventTypeForStatus(ProtectiveAuditStatus status)
    {
        return status switch
        {
            ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP => "PROTECTIVE_MISSING_STOP",
            ProtectiveAuditStatus.PROTECTIVE_STOP_QTY_MISMATCH => "PROTECTIVE_STOP_QTY_MISMATCH",
            ProtectiveAuditStatus.PROTECTIVE_STOP_PRICE_INVALID => "PROTECTIVE_STOP_PRICE_INVALID",
            ProtectiveAuditStatus.PROTECTIVE_MISSING_TARGET => "PROTECTIVE_MISSING_TARGET",
            ProtectiveAuditStatus.PROTECTIVE_TARGET_QTY_MISMATCH => "PROTECTIVE_TARGET_QTY_MISMATCH",
            ProtectiveAuditStatus.PROTECTIVE_CONFLICTING_ORDERS => "PROTECTIVE_CONFLICTING_ORDERS",
            ProtectiveAuditStatus.PROTECTIVE_UNRESOLVED_POSITION => "PROTECTIVE_AUDIT_FAILED",
            _ => "PROTECTIVE_AUDIT_FAILED"
        };
    }

    private static object ToPayload(ProtectiveAuditResult r)
    {
        return new
        {
            instrument = r.Instrument,
            intent_id = r.IntentId,
            broker_position_qty = r.BrokerPositionQty,
            broker_direction = r.BrokerDirection,
            stop_qty = r.StopQty,
            target_qty = r.TargetQty,
            stop_price = r.StopPrice,
            target_price = r.TargetPrice,
            audit_status = r.Status.ToString(),
            recovery_state = r.RecoveryState.ToString(),
            attempt_count = r.AttemptCount,
            instrument_blocked = r.InstrumentBlocked,
            flatten_in_progress = r.FlattenInProgress,
            recovery_in_progress = r.RecoveryInProgress,
            detail = r.Detail,
            timestamp_utc = r.AuditUtc.ToString("o")
        };
    }

    /// <summary>For tests: process a result directly (bypasses timer).</summary>
    internal void ProcessResultForTest(ProtectiveAuditResult result)
    {
        ProcessResult(result, null, result.AuditUtc);
    }

    /// <summary>For tests: get current state (read-only).</summary>
    internal ProtectiveInstrumentState? GetStateForTest(string instrument)
    {
        return _stateByInstrument.TryGetValue(instrument, out var s) ? s : null;
    }

    /// <summary>Emit metrics. Call periodically (e.g. from heartbeat).</summary>
    public void EmitMetrics(DateTimeOffset utcNow)
    {
        _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "PROTECTIVE_AUDIT_METRICS", state: "ENGINE",
            new
            {
                protective_audit_ok_count = _auditOkCount,
                protective_audit_failure_count = _auditFailureCount,
                protective_missing_stop_count = _missingStopCount,
                protective_qty_mismatch_count = _qtyMismatchCount,
                last_audit_utc = _lastAuditUtc != DateTimeOffset.MinValue ? _lastAuditUtc.ToString("o") : null
            }));
    }
}
