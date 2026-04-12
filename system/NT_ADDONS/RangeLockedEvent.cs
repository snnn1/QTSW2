using System;

namespace QTSW2.Robot.Core;

/// <summary>
/// Immutable event representing a locked trading range for a stream on a trading day.
/// Emitted exactly once per (trading_day, stream_id) pair when range locks.
/// </summary>
public sealed class RangeLockedEvent
{
    // Event metadata
    public string event_type { get; } = "RANGE_LOCKED";
    public string event_id { get; } // Deterministic key: "{trading_day}:{stream_id}:RANGE_LOCKED"
    public string trading_day { get; }
    public string stream_id { get; }
    public string canonical_instrument { get; }
    public string execution_instrument { get; }
    
    // Range data (raw values)
    public decimal range_high { get; }
    public decimal range_low { get; }
    public decimal range_size { get; }
    public decimal freeze_close { get; }
    
    // Range data (rounded values - for execution clarity)
    public decimal range_high_rounded { get; }
    public decimal range_low_rounded { get; }
    
    // Breakout levels (rounded, used for execution)
    public decimal breakout_long { get; }
    public decimal breakout_short { get; }
    
    // Time boundaries
    public string range_start_time_chicago { get; }
    public string range_start_time_utc { get; }
    public string range_end_time_chicago { get; }
    public string range_end_time_utc { get; }
    public string locked_at_chicago { get; }
    public string locked_at_utc { get; }
    
    public RangeLockedEvent(
        string tradingDay,
        string streamId,
        string canonicalInstrument,
        string executionInstrument,
        decimal rangeHigh,
        decimal rangeLow,
        decimal rangeSize,
        decimal freezeClose,
        decimal rangeHighRounded,
        decimal rangeLowRounded,
        decimal breakoutLong,
        decimal breakoutShort,
        string rangeStartTimeChicago,
        string rangeStartTimeUtc,
        string rangeEndTimeChicago,
        string rangeEndTimeUtc,
        string lockedAtChicago,
        string lockedAtUtc
    )
    {
        trading_day = tradingDay;
        stream_id = streamId;
        canonical_instrument = canonicalInstrument;
        execution_instrument = executionInstrument;
        range_high = rangeHigh;
        range_low = rangeLow;
        range_size = rangeSize;
        freeze_close = freezeClose;
        range_high_rounded = rangeHighRounded;
        range_low_rounded = rangeLowRounded;
        breakout_long = breakoutLong;
        breakout_short = breakoutShort;
        range_start_time_chicago = rangeStartTimeChicago;
        range_start_time_utc = rangeStartTimeUtc;
        range_end_time_chicago = rangeEndTimeChicago;
        range_end_time_utc = rangeEndTimeUtc;
        locked_at_chicago = lockedAtChicago;
        locked_at_utc = lockedAtUtc;
        
        // Compute deterministic event_id
        event_id = $"{trading_day}:{stream_id}:RANGE_LOCKED";
    }
}
