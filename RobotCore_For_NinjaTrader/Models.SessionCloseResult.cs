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
    /// Structured taxonomy: NO_BARS, EMPTY_BARS, TRADING_HOURS_MISSING, TIMEZONE_ERROR,
    /// SESSION_ITERATOR_ERROR, SESSION_CALCULATION_ERROR, UNHANDLED_EXCEPTION.
    /// Legacy: HOLIDAY, NO_ELIGIBLE_SEGMENTS, ITERATION_ERROR.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>Exception message when failure involves an exception (for diagnostics).</summary>
    public string? ExceptionMessage { get; set; }

    /// <summary>Full name of the exception type (e.g. System.InvalidOperationException).</summary>
    public string? ExceptionType { get; set; }

    /// <summary>Stack trace truncated to 1500 chars for diagnostics.</summary>
    public string? StackTraceTruncated { get; set; }

    /// <summary>Number of bars in the Bars collection at resolution time.</summary>
    public int BarsCount { get; set; }

    /// <summary>Instrument name from Bars (e.g. NQ 03-26).</summary>
    public string? BarsInstrument { get; set; }

    /// <summary>Trading hours template name from Bars.</summary>
    public string? TradingHoursName { get; set; }

    /// <summary>Strategy instance ID when available (for multi-instance correlation).</summary>
    public string? StrategyInstanceId { get; set; }
}
