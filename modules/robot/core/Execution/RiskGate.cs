using System;

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

    public RiskGate(ParitySpec spec, TimeService time, RobotLogger log, KillSwitch killSwitch)
    {
        _spec = spec;
        _time = time;
        _log = log;
        _killSwitch = killSwitch;
    }

    /// <summary>
    /// Check all risk gates. Returns (allowed, reason) tuple.
    /// </summary>
    public (bool Allowed, string? Reason) CheckGates(
        ExecutionMode executionMode,
        string? tradingDate,
        string stream,
        string instrument,
        string session,
        string? slotTimeChicago,
        bool timetableValidated,
        bool streamArmed,
        string completenessFlag,
        DateTimeOffset utcNow)
    {
        // Gate 1: Kill switch
        var killSwitchOk = !_killSwitch.IsEnabled();
        if (!killSwitchOk)
        {
            return (false, "KILL_SWITCH_ACTIVE");
        }

        // Gate 2: Timetable validated
        if (!timetableValidated)
        {
            return (false, "TIMETABLE_NOT_VALIDATED");
        }

        // Gate 3: Stream armed
        if (!streamArmed)
        {
            return (false, "STREAM_NOT_ARMED");
        }

        // Gate 4: Within allowed session window
        if (string.IsNullOrEmpty(slotTimeChicago) || !_spec.Sessions.ContainsKey(session))
        {
            return (false, "INVALID_SESSION_OR_SLOT_TIME");
        }

        var allowedSlots = _spec.Sessions[session].SlotEndTimes;
        var slotTimeAllowed = allowedSlots.Contains(slotTimeChicago);
        if (!slotTimeAllowed)
        {
            return (false, "SLOT_TIME_NOT_ALLOWED");
        }

        // Gate 5: Intent completeness
        if (completenessFlag != "COMPLETE")
        {
            return (false, "INTENT_INCOMPLETE");
        }

        // Gate 6: Replay invariant (if in replay mode)
        // This is checked at bar processing level, but verify trading_date is set
        if (string.IsNullOrEmpty(tradingDate))
        {
            return (false, "TRADING_DATE_NOT_SET");
        }

        // Gate 7: Execution mode validation
        var executionModeOk = executionMode != ExecutionMode.LIVE; // SIM and DRYRUN can execute
        if (!executionModeOk)
        {
            // LIVE mode requires additional checks (see Phase C)
            // For now, fail closed
            return (false, "LIVE_MODE_NOT_ENABLED");
        }

        // All gates passed
        return (true, null);
    }

    /// <summary>
    /// Log execution blocked event.
    /// </summary>
    public void LogBlocked(
        string intentId,
        string instrument,
        string reason,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_BLOCKED", new
        {
            reason,
            note = "Order submission blocked by risk gate"
        }));
    }
}
