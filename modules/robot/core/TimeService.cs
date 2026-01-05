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

    public DateTimeOffset ConvertChicagoLocalToUtc(DateOnly tradingDate, string hhmm)
    {
        // tradingDate + hhmm interpreted as America/Chicago local time (DST-aware)
        var parts = hhmm.Split(':');
        if (parts.Length != 2) throw new FormatException($"Invalid HH:MM time: '{hhmm}'");
        parts[0] = parts[0].Trim();
        parts[1] = parts[1].Trim();

        var hour = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minute = int.Parse(parts[1], CultureInfo.InvariantCulture);

        var localUnspecified = new DateTime(tradingDate.Year, tradingDate.Month, tradingDate.Day, hour, minute, 0, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(localUnspecified, _chicagoTz);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    public static bool TryParseDateOnly(string yyyyMmDd, out DateOnly date)
        => DateOnly.TryParseExact(yyyyMmDd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

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
}

