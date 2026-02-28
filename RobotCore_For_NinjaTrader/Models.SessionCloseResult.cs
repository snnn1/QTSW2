using System;

namespace QTSW2.Robot.Core;

/// <summary>
/// Result of session close resolution for a (TradingDay, sessionClass).
/// Engine-owned; populated by SessionCloseResolver (strategy layer) or HistoricalReplay harness.
/// </summary>
public sealed class SessionCloseResult
{
    /// <summary>True if today has an eligible session; false when resolution failed.</summary>
    public bool HasSession { get; set; }

    /// <summary>UTC time at which forced flatten should trigger (session close minus buffer).</summary>
    public DateTimeOffset? FlattenTriggerUtc { get; set; }

    /// <summary>Resolved session close time in UTC.</summary>
    public DateTimeOffset? ResolvedSessionCloseUtc { get; set; }

    /// <summary>Buffer seconds before close (e.g., 300).</summary>
    public int BufferSeconds { get; set; }

    /// <summary>
    /// Failure reason when HasSession=false. Drives event taxonomy for audit clarity.
    /// HOLIDAY: Exchange holiday per TradingHours template (SessionIterator skipped day).
    /// NO_ELIGIBLE_SEGMENTS: Target day seen but no segments (flatten) or none overlap timetable (slot classification).
    /// ITERATION_ERROR: Date resolution failed (parse, invalid input, max iterations).
    /// EXCEPTION: Resolver threw.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>Exception message when FailureReason=EXCEPTION (for diagnostics).</summary>
    public string? ExceptionMessage { get; set; }
}
