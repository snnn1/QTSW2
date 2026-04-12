#if NINJATRADER
using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Real NinjaTrader API implementation for LIVE adapter FlattenEmergency.
/// Uses Account.Flatten(ICollection&lt;Instrument&gt;) for safe live flatten.
/// </summary>
public sealed partial class NinjaTraderLiveAdapter
{
    private FlattenResult FlattenEmergencyReal(string instrument, DateTimeOffset utcNow)
    {
        if (_ntAccount == null)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_EMERGENCY_LIVE_NO_CONTEXT", state: "ENGINE",
                new { instrument, note = "LIVE adapter: SetNTContext not called - cannot flatten" }));
            return FlattenResult.FailureResult("LIVE adapter: NT context not set", utcNow);
        }

        var account = _ntAccount as Account;
        if (account == null)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_EMERGENCY_LIVE_ACCOUNT_NULL", state: "ENGINE",
                new { instrument, note = "LIVE adapter: Account type mismatch" }));
            return FlattenResult.FailureResult("LIVE adapter: Account type mismatch", utcNow);
        }

        Instrument? ntInstrument = null;
        try
        {
            ntInstrument = Instrument.GetInstrument(instrument);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_EMERGENCY_LIVE_INSTRUMENT_ERROR", state: "ENGINE",
                new { instrument, error = ex.Message, note = "LIVE adapter: Instrument.GetInstrument failed" }));
            return FlattenResult.FailureResult($"LIVE adapter: Instrument.GetInstrument failed: {ex.Message}", utcNow);
        }

        if (ntInstrument == null)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_EMERGENCY_LIVE_INSTRUMENT_NULL", state: "ENGINE",
                new { instrument, note = "LIVE adapter: Instrument not found" }));
            return FlattenResult.FailureResult($"LIVE adapter: Instrument not found: {instrument}", utcNow);
        }

        try
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_EMERGENCY_LIVE", state: "ENGINE",
                new { instrument, note = "LIVE adapter: Calling Account.Flatten for unmatched position policy" }));
            account.Flatten(new List<Instrument> { ntInstrument });
            return FlattenResult.SuccessResult(utcNow);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_EMERGENCY_LIVE_FAILED", state: "ENGINE",
                new { instrument, error = ex.Message, exception_type = ex.GetType().Name, note = "LIVE adapter: Account.Flatten threw" }));
            return FlattenResult.FailureResult($"LIVE adapter: Flatten failed: {ex.Message}", utcNow);
        }
    }

    /// <inheritdoc cref="IExecutionAdapter.TryTriggerHardFlatten"/>
    public bool TryTriggerHardFlatten(string instrument, string reason, DateTimeOffset utcNow)
    {
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return false;
        if (!HardFailClosedExecutionModel.TryArmOneShotBrokerFlatten(inst))
            return true;

        _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "CRITICAL_UNSAFE_STATE_DETECTED",
            new { instrument = inst, reason, model = "hard_fail_closed" }));
        _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "HARD_FLATTEN_TRIGGERED",
            new { instrument = inst, reason, path = "account_flatten_broker_only" }));

        if (_ntAccount == null)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "HARD_FLATTEN_BROKER_CONTEXT_MISSING", "ENGINE",
                new { instrument = inst, reason, note = "LIVE: no account" }));
            ExecutionSafetyGate.ApplyUnmappedExecutionKillSwitch(inst, reason ?? "hard_flatten_no_context", utcNow);
            HardFailClosedExecutionModel.MarkExecutionLocked(inst);
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "EXECUTION_LOCKED_UNSAFE_STATE",
                new { instrument = inst, broker_context_missing = true }));
            return false;
        }

        var account = _ntAccount as Account;
        if (account == null)
        {
            ExecutionSafetyGate.ApplyUnmappedExecutionKillSwitch(inst, reason ?? "hard_flatten_no_account", utcNow);
            HardFailClosedExecutionModel.MarkExecutionLocked(inst);
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "EXECUTION_LOCKED_UNSAFE_STATE",
                new { instrument = inst, broker_context_missing = true }));
            return false;
        }

        Instrument? ntInst = null;
        try
        {
            ntInst = Instrument.GetInstrument(inst);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "HARD_FLATTEN_INSTRUMENT_ERROR", "ENGINE",
                new { instrument = inst, error = ex.Message }));
            ntInst = null;
        }

        if (ntInst == null)
        {
            ExecutionSafetyGate.ApplyUnmappedExecutionKillSwitch(inst, reason ?? "hard_flatten_no_instrument", utcNow);
            HardFailClosedExecutionModel.MarkExecutionLocked(inst);
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "EXECUTION_LOCKED_UNSAFE_STATE",
                new { instrument = inst, broker_context_missing = true }));
            return false;
        }

        try
        {
            account.Flatten(new List<Instrument> { ntInst });
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "HARD_FLATTEN_BROKER_FAILED", "ENGINE",
                new { instrument = inst, error = ex.Message, exception_type = ex.GetType().Name }));
        }

        ExecutionSafetyGate.ApplyUnmappedExecutionKillSwitch(inst, reason ?? "hard_fail_closed_flatten", utcNow);
        HardFailClosedExecutionModel.MarkExecutionLocked(inst);
        _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "EXECUTION_LOCKED_UNSAFE_STATE",
            new { instrument = inst, reason }));
        return true;
    }
}
#endif
