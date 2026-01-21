using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Risk gate: fail-closed checks before ANY order submission.
/// All gates must pass for execution to proceed.
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
            return (false, _guard.GetRecoveryStateReason(), failedGates);
        }
        
        // Gate 1: Kill switch
        var killSwitchOk = !_killSwitch.IsEnabled();
        if (!killSwitchOk)
        {
            failedGates.Add("KILL_SWITCH");
            return (false, "KILL_SWITCH_ACTIVE", failedGates);
        }

        // Gate 2: Timetable validated
        if (!timetableValidated)
        {
            failedGates.Add("TIMETABLE_VALIDATED");
            return (false, "TIMETABLE_NOT_VALIDATED", failedGates);
        }

        // Gate 3: Stream armed
        if (!streamArmed)
        {
            failedGates.Add("STREAM_ARMED");
            return (false, "STREAM_NOT_ARMED", failedGates);
        }

        // Gate 4: Within allowed session window
        if (string.IsNullOrEmpty(slotTimeChicago) || !_spec.sessions.ContainsKey(session))
        {
            failedGates.Add("SESSION_OR_SLOT_TIME");
            return (false, "INVALID_SESSION_OR_SLOT_TIME", failedGates);
        }

        var allowedSlots = _spec.sessions[session].slot_end_times;
        var slotTimeAllowed = allowedSlots.Contains(slotTimeChicago);
        if (!slotTimeAllowed)
        {
            failedGates.Add("SLOT_TIME_ALLOWED");
            return (false, "SLOT_TIME_NOT_ALLOWED", failedGates);
        }

        // Gate 5: Replay invariant (if in replay mode)
        // This is checked at bar processing level, but verify trading_date is set
        if (string.IsNullOrEmpty(tradingDate))
        {
            failedGates.Add("TRADING_DATE_SET");
            return (false, "TRADING_DATE_NOT_SET", failedGates);
        }

        // All gates passed
        return (true, null, failedGates);
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
    }
}
