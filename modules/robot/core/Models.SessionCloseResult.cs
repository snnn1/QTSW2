using System;

namespace QTSW2.Robot.Core;

/// <summary>
/// Result of session close resolution for a (TradingDay, sessionClass).
/// Engine-owned; populated by SessionCloseResolver (strategy layer) or HistoricalReplay harness.
/// </summary>
public sealed class SessionCloseResult
{
    /// <summary>True if today has an eligible session; false for holiday (IsHoliday = !HasSession).</summary>
    public bool HasSession { get; set; }

    /// <summary>UTC time at which forced flatten should trigger (session close minus buffer).</summary>
    public DateTimeOffset? FlattenTriggerUtc { get; set; }

    /// <summary>Resolved session close time in UTC.</summary>
    public DateTimeOffset? ResolvedSessionCloseUtc { get; set; }

    /// <summary>Next session begin time in UTC (market reopen). Used for reentry time gate.</summary>
    public DateTimeOffset? NextSessionBeginUtc { get; set; }

    /// <summary>Buffer seconds before close (e.g., 300).</summary>
    public int BufferSeconds { get; set; }

    /// <summary>Instrument name for logging (e.g., ES, NQ).</summary>
    public string? BarsInstrument { get; set; }
}
