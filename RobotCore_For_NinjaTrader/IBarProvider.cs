using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core;

/// <summary>
/// Represents a single OHLC bar.
/// </summary>
public readonly struct Bar
{
    public DateTimeOffset TimestampUtc { get; }
    public decimal Open { get; }
    public decimal High { get; }
    public decimal Low { get; }
    public decimal Close { get; }
    public decimal? Volume { get; }

    public Bar(DateTimeOffset timestampUtc, decimal open, decimal high, decimal low, decimal close, decimal? volume = null)
    {
        TimestampUtc = timestampUtc;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
    }
}

/// <summary>
/// Interface for providing historical bars to the Robot engine.
/// Used in DRYRUN mode to replay historical data from snapshot files.
/// </summary>
public interface IBarProvider
{
    /// <summary>
    /// Get bars for the specified instrument and time range.
    /// Bars are returned in strict chronological order (oldest first).
    /// </summary>
    /// <param name="instrument">Instrument code (e.g., "ES", "NQ")</param>
    /// <param name="startUtc">Start timestamp (inclusive, UTC)</param>
    /// <param name="endUtc">End timestamp (exclusive, UTC)</param>
    /// <returns>Enumerable sequence of bars in chronological order</returns>
    IEnumerable<Bar> GetBars(string instrument, DateTimeOffset startUtc, DateTimeOffset endUtc);
}
