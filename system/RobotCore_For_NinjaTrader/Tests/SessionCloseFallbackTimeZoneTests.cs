// Unit tests for session close fallback timezone correctness.
// CRITICAL: spec.entry_cutoff.market_close_time is America/Chicago local time (DST-aware).
// Run: dotnet test or via Robot.Harness.
//
// DST boundaries (America/Chicago):
// - Spring forward: 2nd Sunday in March (e.g. 2026-03-08)
// - Fall back: 1st Sunday in November (e.g. 2026-11-01)

using System;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Tests;

public static class SessionCloseFallbackTimeZoneTests
{
    /// <summary>
    /// Verify 16:00 Chicago converts to correct UTC across DST boundary weeks.
    /// March 2026: Before DST (CST, UTC-6) → 16:00 CT = 22:00 UTC
    /// March 2026: After DST (CDT, UTC-5)  → 16:00 CT = 21:00 UTC
    /// November 2026: Before fall back (CDT) → 16:00 CT = 21:00 UTC
    /// November 2026: After fall back (CST)  → 16:00 CT = 22:00 UTC
    /// </summary>
    public static (bool Pass, string? Error) RunDstBoundaryTests()
    {
        var time = new TimeService("America/Chicago");

        // Week before DST (March 4, 2026 = Wednesday, still CST)
        var mar4 = new DateOnly(2026, 3, 4);
        var mar4Chicago = time.ConstructChicagoTime(mar4, "16:00");
        var mar4Utc = time.ConvertChicagoToUtc(mar4Chicago);
        // 16:00 CST = 22:00 UTC
        if (mar4Utc.UtcDateTime.Hour != 22 || mar4Utc.UtcDateTime.Minute != 0)
            return (false, $"Mar 4 16:00 CT: expected 22:00 UTC, got {mar4Utc.UtcDateTime:HH:mm} UTC");

        // Week after DST (March 16, 2026 = Monday, CDT)
        var mar16 = new DateOnly(2026, 3, 16);
        var mar16Chicago = time.ConstructChicagoTime(mar16, "16:00");
        var mar16Utc = time.ConvertChicagoToUtc(mar16Chicago);
        // 16:00 CDT = 21:00 UTC
        if (mar16Utc.UtcDateTime.Hour != 21 || mar16Utc.UtcDateTime.Minute != 0)
            return (false, $"Mar 16 16:00 CT: expected 21:00 UTC, got {mar16Utc.UtcDateTime:HH:mm} UTC");

        // November: before fall back (Oct 31, 2026 = Saturday, CDT)
        var oct31 = new DateOnly(2026, 10, 31);
        var oct31Chicago = time.ConstructChicagoTime(oct31, "16:00");
        var oct31Utc = time.ConvertChicagoToUtc(oct31Chicago);
        // 16:00 CDT = 21:00 UTC
        if (oct31Utc.UtcDateTime.Hour != 21 || oct31Utc.UtcDateTime.Minute != 0)
            return (false, $"Oct 31 16:00 CT: expected 21:00 UTC, got {oct31Utc.UtcDateTime:HH:mm} UTC");

        // November: after fall back (Nov 2, 2026 = Monday, CST)
        var nov2 = new DateOnly(2026, 11, 2);
        var nov2Chicago = time.ConstructChicagoTime(nov2, "16:00");
        var nov2Utc = time.ConvertChicagoToUtc(nov2Chicago);
        // 16:00 CST = 22:00 UTC
        if (nov2Utc.UtcDateTime.Hour != 22 || nov2Utc.UtcDateTime.Minute != 0)
            return (false, $"Nov 2 16:00 CT: expected 22:00 UTC, got {nov2Utc.UtcDateTime:HH:mm} UTC");

        // Flatten trigger: 16:00 - 5 min = 15:55 CT
        var mar4FlattenChicago = time.ConstructChicagoTime(mar4, "15:55");
        var mar4FlattenUtc = time.ConvertChicagoToUtc(mar4FlattenChicago);
        if (mar4FlattenUtc.UtcDateTime.Hour != 21 || mar4FlattenUtc.UtcDateTime.Minute != 55)
            return (false, $"Mar 4 15:55 CT: expected 21:55 UTC, got {mar4FlattenUtc.UtcDateTime:HH:mm} UTC");

        return (true, null);
    }
}
