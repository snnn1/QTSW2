using System;

namespace QTSW2.Robot.Core;

/// <summary>
/// Represents a single OHLC bar.
/// Used throughout Robot.Core for bar data representation.
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
