using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Threading;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Execution;

public sealed partial class StreamStateMachine
{

    private void ComputeAndLogProtectiveOrders(DateTimeOffset utcNow)
    {
        if (_intendedDirection == null || !_intendedEntryPrice.HasValue)
            return;

        var direction = _intendedDirection;
        var entryPrice = _intendedEntryPrice.Value;
        
        // CRITICAL FIX: BE trigger can be computed without range (only needs entry price and base target)
        // Always compute BE trigger even if range isn't available
        var beTriggerPts = _baseTarget * 0.65m; // 65% of target
        var beTriggerPrice = direction == "Long" ? entryPrice + beTriggerPts : entryPrice - beTriggerPts;
        var beStopPrice = direction == "Long" ? entryPrice - _tickSize : entryPrice + _tickSize;
        
        // Store BE trigger immediately (required for break-even detection)
        _intendedBeTrigger = beTriggerPrice;

        // Stop and target require range - only compute if range is available
        if (!RangeHigh.HasValue || !RangeLow.HasValue)
        {
            // Range not available - log warning but still set BE trigger (critical for break-even detection)
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                "PROTECTIVE_ORDERS_PARTIAL_COMPUTE", State.ToString(),
                new
                {
                    direction = direction,
                    entry_price = entryPrice,
                    be_trigger_price = beTriggerPrice,
                    be_stop_price = beStopPrice,
                    range_high_available = RangeHigh.HasValue,
                    range_low_available = RangeLow.HasValue,
                    warning = "Range not available - BE trigger computed but stop/target prices not set",
                    note = "BE trigger is set for break-even detection. Stop and target will be computed when range is available or from lock snapshot."
                }));
            return; // Exit early - stop/target can't be computed without range
        }

        var rangeSize = RangeHigh.Value - RangeLow.Value;

        // Compute target
        var targetPrice = direction == "Long" ? entryPrice + _baseTarget : entryPrice - _baseTarget;

        // Compute stop loss: min(range_size, 3 * target_pts)
        var maxSlPoints = 3 * _baseTarget;
        var slPoints = Math.Min(rangeSize, maxSlPoints);
        var stopPrice = direction == "Long" ? entryPrice - slPoints : entryPrice + slPoints;

        // Store computed values for execution
        _intendedStopPrice = stopPrice;
        _intendedTargetPrice = targetPrice;

        // Log protective orders (always log for DRYRUN parity)
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "DRYRUN_INTENDED_PROTECTIVE", State.ToString(),
            new
            {
                target_pts = _baseTarget,
                target_price = targetPrice,
                sl_points = slPoints,
                stop_price = stopPrice
            }));

        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
            "DRYRUN_INTENDED_BE", State.ToString(),
            new
            {
                be_trigger_pts = beTriggerPts,
                be_trigger_price = beTriggerPrice,
                be_stop_price = beStopPrice,
                be_triggered = false, // Will be set if triggered during price tracking (future step)
                be_trigger_time_utc = (string?)null
            }));
    }

    /// <summary>
    /// Pure, lock-snapshot-driven protective computation (no reliance on mutable interim fields).
    /// Uses the same math as the normal entry path:
    /// - target = entry ± target_pts
    /// - stop distance = min(range_size, 3 * target_pts)
    /// - BE trigger = 65% of target distance
    /// </summary>
    private (decimal stopPrice, decimal targetPrice, decimal beTriggerPrice) ComputeProtectivesFromLockSnapshot(
        string direction,
        decimal entryPrice,
        decimal rangeHigh,
        decimal rangeLow)
    {
        var rangeSize = rangeHigh - rangeLow;

        // Target
        var targetPrice = direction == "Long" ? entryPrice + _baseTarget : entryPrice - _baseTarget;

        // Stop loss: min(range_size, 3 * target_pts)
        var maxSlPoints = 3 * _baseTarget;
        var slPoints = Math.Min(rangeSize, maxSlPoints);
        var stopPrice = direction == "Long" ? entryPrice - slPoints : entryPrice + slPoints;

        // BE trigger: 65% of target distance
        var beTriggerPts = _baseTarget * 0.65m;
        var beTriggerPrice = direction == "Long" ? entryPrice + beTriggerPts : entryPrice - beTriggerPts;

        return (stopPrice, targetPrice, beTriggerPrice);
    }
}
