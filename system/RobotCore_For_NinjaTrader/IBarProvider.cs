using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core;

/// <summary>
/// Interface for providing historical bars to the Robot engine.
/// Used in DRYRUN mode to replay historical data from snapshot files.
/// Bar type is defined in Bar.cs (separate file).
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
