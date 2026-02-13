using System;
using System.Globalization;

namespace QTSW2.Robot.Core;

public sealed class TimeService
{
    private readonly TimeZoneInfo _chicagoTz;

    public TimeService(string parityTimezone)
    {
        if (parityTimezone != "America/Chicago")
            throw new ArgumentException("Only America/Chicago is supported in Step-2 skeleton.", nameof(parityTimezone));

        _chicagoTz = ResolveChicagoTimeZone();
    }

    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;

    /// <summary>
    /// Convert UTC DateTimeOffset to Chicago timezone.
    /// CRITICAL: This is a pure timezone conversion, not "now" - input must be UTC.
    /// </summary>
    /// <param name="utcTime">UTC DateTimeOffset to convert</param>
    /// <returns>DateTimeOffset in Chicago timezone</returns>
    public DateTimeOffset ConvertUtcToChicago(DateTimeOffset utcTime)
        => TimeZoneInfo.ConvertTime(utcTime, _chicagoTz);

    /// <summary>
    /// DEPRECATED: Use ConvertUtcToChicago instead. This name is misleading.
    /// </summary>
    [Obsolete("Use ConvertUtcToChicago instead - this name implies 'now' but it's a converter")]
    public DateTimeOffset GetChicagoNow(DateTimeOffset utcNow)
        => ConvertUtcToChicago(utcNow);

    public DateOnly GetChicagoDateToday(DateTimeOffset utcNow)
        => DateOnly.FromDateTime(ConvertUtcToChicago(utcNow).DateTime);

    /// <summary>
    /// Construct a DateTimeOffset in Chicago timezone directly (authoritative).
    /// This is the primary method for creating Chicago times - UTC is derived from this.
    /// </summary>
    /// <param name="tradingDate">Trading date</param>
    /// <param name="hhmm">Time in HH:MM format</param>
    /// <returns>DateTimeOffset in Chicago timezone with correct DST offset</returns>
    public DateTimeOffset ConstructChicagoTime(DateOnly tradingDate, string hhmm)
    {
        // tradingDate + hhmm interpreted as America/Chicago local time (DST-aware)
        var parts = hhmm.Split(':');
        if (parts.Length != 2) throw new FormatException($"Invalid HH:MM time: '{hhmm}'");
        parts[0] = parts[0].Trim();
        parts[1] = parts[1].Trim();

        var hour = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minute = int.Parse(parts[1], CultureInfo.InvariantCulture);

        // Construct DateTime in Chicago timezone with DST-aware offset
        var localDateTime = new DateTime(tradingDate.Year, tradingDate.Month, tradingDate.Day, hour, minute, 0, DateTimeKind.Unspecified);
        var chicagoOffset = _chicagoTz.GetUtcOffset(localDateTime);
        return new DateTimeOffset(localDateTime, chicagoOffset);
    }

    /// <summary>
    /// Convert Chicago time to UTC (derived representation).
    /// Use ConstructChicagoTime first, then call this method.
    /// CRITICAL: Uses TimeZoneInfo.ConvertTime to ensure correct timezone conversion.
    /// </summary>
    /// <param name="chicagoTime">DateTimeOffset in Chicago timezone</param>
    /// <returns>DateTimeOffset in UTC</returns>
    public DateTimeOffset ConvertChicagoToUtc(DateTimeOffset chicagoTime)
        => TimeZoneInfo.ConvertTime(chicagoTime, TimeZoneInfo.Utc);

    /// <summary>
    /// DEPRECATED: Use ConstructChicagoTime + ConvertChicagoToUtc instead.
    /// This method constructs Chicago time then converts to UTC, which is fine,
    /// but the round-trip pattern (Chicago→UTC→Chicago) should be avoided.
    /// CRITICAL: Fixed to use TimeZoneInfo.ConvertTime instead of ToUniversalTime() for correctness.
    /// </summary>
    [Obsolete("Prefer ConstructChicagoTime + ConvertChicagoToUtc for clarity. This method is kept for backward compatibility.")]
    public DateTimeOffset ConvertChicagoLocalToUtc(DateOnly tradingDate, string hhmm)
    {
        var chicagoTime = ConstructChicagoTime(tradingDate, hhmm);
        return TimeZoneInfo.ConvertTime(chicagoTime, TimeZoneInfo.Utc);
    }

    public static bool TryParseDateOnly(string yyyyMmDd, out DateOnly date)
        => DateOnly.TryParseExact(yyyyMmDd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>
    /// Static helper for timezone conversion when TimeService instance is not available.
    /// Used by RobotEvents static methods. Prefer instance methods when TimeService is available.
    /// </summary>
    public static DateTimeOffset ConvertUtcToChicagoStatic(DateTimeOffset utcNow)
    {
        var tz = ResolveChicagoTimeZone();
        return TimeZoneInfo.ConvertTime(utcNow, tz);
    }

    private static TimeZoneInfo ResolveChicagoTimeZone()
    {
        // Windows uses "Central Standard Time"; Linux/macOS commonly use "America/Chicago".
        var candidates = new[] { "America/Chicago", "Central Standard Time" };
        foreach (var id in candidates)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* try next */ }
        }
        throw new TimeZoneNotFoundException("Could not resolve Chicago timezone (America/Chicago / Central Standard Time).");
    }

    /// <summary>
    /// Format DateTimeOffset as ISO 8601 string (replaces .ToString("o") pattern).
    /// </summary>
    public static string FormatIso8601(DateTimeOffset dt) => dt.ToString("o");

    /// <summary>
    /// Format DateOnly as yyyy-MM-dd string (replaces .ToString("yyyy-MM-dd") pattern).
    /// </summary>
    public static string FormatDateOnly(DateOnly date) => date.ToString("yyyy-MM-dd");
}

