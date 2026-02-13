using System;
using System.Collections.Generic;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Risk gate: fail-closed checks before ANY order submission.
/// All gates must pass for execution to proceed.
/// 
/// RiskGate enforces execution blocking during recovery states.
/// Emergency flatten operations are explicitly exempt and may bypass the gate.
/// 
/// During DISCONNECT_FAIL_CLOSED and RECONNECTED_RECOVERY_PENDING:
/// - Entry orders go through CheckGates() → blocked (via IExecutionRecoveryGuard)
/// - Protective orders go through CheckGates() → blocked
/// - Order modifications go through CheckGates() → blocked
/// - Flatten operations call adapter's Flatten() directly → permitted (bypasses RiskGate)
/// </summary>
public sealed class RiskGate
{
    private readonly ParitySpec _spec;
    private readonly TimeService _time;
    private readonly RobotLogger _log;
    private readonly KillSwitch _killSwitch;
    private readonly IExecutionRecoveryGuard? _guard;

    public RiskGate(ParitySpec spec, TimeService time, RobotLogger log, KillSwitch killSwitch, IExecutionRecoveryGuard? guard = null)
    {
        _spec = spec;
        _time = time;
        _log = log;
        _killSwitch = killSwitch;
        _guard = guard;
    }

    /// <summary>
    /// Check all risk gates. Returns (allowed, reason, failedGates) tuple.
    /// failedGates is a list of gate names that failed (for comprehensive logging).
    /// </summary>
    public (bool Allowed, string? Reason, List<string> FailedGates) CheckGates(
        ExecutionMode executionMode,
        string? tradingDate,
        string stream,
        string instrument,
        string session,
        string? slotTimeChicago,
        bool timetableValidated,
        bool streamArmed,
        DateTimeOffset utcNow)
    {
        var failedGates = new List<string>();
        
        // Gate 0: Recovery state guard (blocks execution during disconnect recovery)
        if (_guard != null && !_guard.IsExecutionAllowed())
        {
            failedGates.Add("RECOVERY_STATE");
            LogRiskCheck(stream, instrument, session, slotTimeChicago, tradingDate, false, failedGates, utcNow);
            return (false, _guard.GetRecoveryStateReason(), failedGates);
        }
        
        // Gate 1: Kill switch
        var killSwitchOk = !_killSwitch.IsEnabled();
        if (!killSwitchOk)
        {
            failedGates.Add("KILL_SWITCH");
            LogRiskCheck(stream, instrument, session, slotTimeChicago, tradingDate, false, failedGates, utcNow);
            return (false, "KILL_SWITCH_ACTIVE", failedGates);
        }

        // Gate 2: Timetable validated
        if (!timetableValidated)
        {
            failedGates.Add("TIMETABLE_VALIDATED");
            LogRiskCheck(stream, instrument, session, slotTimeChicago, tradingDate, false, failedGates, utcNow);
            return (false, "TIMETABLE_NOT_VALIDATED", failedGates);
        }

        // Gate 3: Stream armed
        if (!streamArmed)
        {
            failedGates.Add("STREAM_ARMED");
            LogRiskCheck(stream, instrument, session, slotTimeChicago, tradingDate, false, failedGates, utcNow);
            return (false, "STREAM_NOT_ARMED", failedGates);
        }

        // Gate 4: Within allowed session window
        if (string.IsNullOrEmpty(slotTimeChicago) || !_spec.sessions.ContainsKey(session))
        {
            failedGates.Add("SESSION_OR_SLOT_TIME");
            LogRiskCheck(stream, instrument, session, slotTimeChicago, tradingDate, false, failedGates, utcNow);
            return (false, "INVALID_SESSION_OR_SLOT_TIME", failedGates);
        }

        var allowedSlots = _spec.sessions[session].slot_end_times;
        var slotTimeAllowed = allowedSlots.Contains(slotTimeChicago);
        if (!slotTimeAllowed)
        {
            failedGates.Add("SLOT_TIME_ALLOWED");
            LogRiskCheck(stream, instrument, session, slotTimeChicago, tradingDate, false, failedGates, utcNow);
            return (false, "SLOT_TIME_NOT_ALLOWED", failedGates);
        }

        // Gate 5: Replay invariant (if in replay mode)
        // This is checked at bar processing level, but verify trading_date is set
        if (string.IsNullOrEmpty(tradingDate))
        {
            failedGates.Add("TRADING_DATE_SET");
            LogRiskCheck(stream, instrument, session, slotTimeChicago, tradingDate, false, failedGates, utcNow);
            return (false, "TRADING_DATE_NOT_SET", failedGates);
        }

        // All gates passed
        LogRiskCheck(stream, instrument, session, slotTimeChicago, tradingDate, true, failedGates, utcNow);
        return (true, null, failedGates);
    }
    
    /// <summary>
    /// Log risk check evaluation (only if diagnostics_enabled).
    /// Called from CheckGates() for both pass and fail cases.
    /// </summary>
    private void LogRiskCheck(
        string stream,
        string instrument,
        string session,
        string? slotTimeChicago,
        string? tradingDate,
        bool allPassed,
        List<string> failedGates,
        DateTimeOffset utcNow)
    {
        // Only log if diagnostics enabled (check via LoggingConfig)
        var config = LoggingConfig.LoadFromFile(ProjectRootResolver.ResolveProjectRoot() ?? "");
        if (!config.diagnostics_enabled)
        {
            return;
        }
        
        var nowChicago = _time.ConvertUtcToChicago(utcNow);
        _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "RISK_CHECK_EVALUATED", new
        {
            stream_id = stream,
            instrument = instrument,
            trading_date = tradingDate ?? "NOT_SET",
            session = session,
            slot_time_chicago = slotTimeChicago ?? "NOT_SET",
            current_time_chicago = nowChicago.ToString("o"),
            all_gates_passed = allPassed,
            failed_gates = failedGates,
            gate_count = failedGates.Count
        }));
    }

    /// <summary>
    /// Log execution blocked event with comprehensive gate failure details.
    /// </summary>
    public void LogBlocked(
        string intentId,
        string instrument,
        string stream,
        string session,
        string? slotTimeChicago,
        string? tradingDate,
        string reason,
        List<string> failedGates,
        bool streamArmed,
        bool timetableValidated,
        DateTimeOffset utcNow)
    {
        var nowChicago = _time.ConvertUtcToChicago(utcNow);
        
        // Check if reason is risk-related (not recovery/kill switch)
        var isRiskRelated = !reason.Contains("RECOVERY") && 
                           reason != "KILL_SWITCH_ACTIVE" &&
                           failedGates.Count > 0 &&
                           !failedGates.Contains("RECOVERY_STATE") &&
                           !failedGates.Contains("KILL_SWITCH");
        
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_BLOCKED", new
        {
            instrument = instrument,
            stream_id = stream,
            trading_date = tradingDate ?? "NOT_SET",
            session = session,
            slot_time_chicago = slotTimeChicago ?? "NOT_SET",
            current_time_chicago = nowChicago.ToString("o"),
            reason = reason,
            failed_gates = failedGates,
            gate_status = new
            {
                kill_switch = failedGates.Contains("KILL_SWITCH") ? "FAILED" : "PASSED",
                timetable_validated = failedGates.Contains("TIMETABLE_VALIDATED") ? "FAILED" : (timetableValidated ? "PASSED" : "UNKNOWN"),
                stream_armed = failedGates.Contains("STREAM_ARMED") ? "FAILED" : (streamArmed ? "PASSED" : "UNKNOWN"),
                session_or_slot_time = failedGates.Contains("SESSION_OR_SLOT_TIME") ? "FAILED" : "PASSED",
                slot_time_allowed = failedGates.Contains("SLOT_TIME_ALLOWED") ? "FAILED" : "PASSED",
                trading_date_set = failedGates.Contains("TRADING_DATE_SET") ? "FAILED" : (string.IsNullOrEmpty(tradingDate) ? "UNKNOWN" : "PASSED")
            },
            note = "Order submission blocked by risk gate - all gates must pass for execution"
        }));
        
        // Also log ENTRY_BLOCKED_RISK if risk-related
        if (isRiskRelated)
        {
            var allowedSlots = _spec.sessions.ContainsKey(session) ? new HashSet<string>(_spec.sessions[session].slot_end_times) : new HashSet<string>();
            var slotTimeAllowed = !string.IsNullOrEmpty(slotTimeChicago) && allowedSlots.Contains(slotTimeChicago);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_BLOCKED_RISK", new
            {
                stream_id = stream,
                instrument = instrument,
                trading_date = tradingDate ?? "NOT_SET",
                session = session,
                slot_time_chicago = slotTimeChicago ?? "NOT_SET",
                current_time_chicago = nowChicago.ToString("o"),
                reason = reason,
                failed_gates = failedGates,
                risk_context = new
                {
                    timetable_validated = timetableValidated,
                    stream_armed = streamArmed,
                    slot_time_allowed = slotTimeAllowed,
                    trading_date_set = !string.IsNullOrEmpty(tradingDate)
                }
            }));
        }
    }
}
