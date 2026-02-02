using System;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core;

/// <summary>
/// Extension methods for NinjaTrader integration.
/// </summary>
public static class NinjaTraderExtensions
{
    /// <summary>
    /// Convert NinjaTrader ConnectionStatus to HealthMonitor ConnectionStatus.
    /// Uses strongly-typed NinjaTrader.Cbi.ConnectionStatus (no reflection needed).
    /// </summary>
    /// <param name="ntStatus">NinjaTrader connection status</param>
    /// <returns>Internal ConnectionStatus enum</returns>
    public static ConnectionStatus ToHealthMonitorStatus(this NinjaTrader.Cbi.ConnectionStatus ntStatus)
    {
        // Map NinjaTrader connection status to internal enum
        // Default branch maps unknown to ConnectionError (safe fallback)
        return ntStatus switch
        {
            NinjaTrader.Cbi.ConnectionStatus.Connected => ConnectionStatus.Connected,
            NinjaTrader.Cbi.ConnectionStatus.ConnectionLost => ConnectionStatus.ConnectionLost,
            NinjaTrader.Cbi.ConnectionStatus.Disconnected => ConnectionStatus.Disconnected,
            _ => ConnectionStatus.ConnectionError // Default: unknown status → ConnectionError
        };
    }
    
    /// <summary>
    /// Convert NinjaTrader ConnectionStatus to HealthMonitor ConnectionStatus (backward compatibility overload).
    /// Uses reflection for object parameter (for backward compatibility with old code).
    /// </summary>
    [Obsolete("Use strongly-typed overload: ToHealthMonitorStatus(NinjaTrader.Cbi.ConnectionStatus)")]
    public static ConnectionStatus ToHealthMonitorStatus(this object ntConnectionStatus)
    {
        // Use reflection to access NinjaTrader.Cbi.ConnectionStatus enum values
        // This avoids direct dependency on NinjaTrader types in core library
        var statusType = ntConnectionStatus.GetType();
        var statusName = ntConnectionStatus.ToString();
        
        // Map NinjaTrader connection status to our enum
        return statusName switch
        {
            "Connected" => ConnectionStatus.Connected,
            "ConnectionLost" => ConnectionStatus.ConnectionLost,
            "Disconnected" => ConnectionStatus.Disconnected,
            _ => ConnectionStatus.ConnectionError
        };
    }
    
    /// <summary>
    /// Resolve Chicago timezone (shared helper for consistency).
    /// </summary>
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
    /// Convert NinjaTrader bar exchange time to UTC DateTimeOffset.
    /// CRITICAL: NinjaTrader provides bars in UTC (DateTimeKind.Unspecified but semantically UTC).
    /// This method simply creates a DateTimeOffset with UTC offset - no conversion needed.
    /// </summary>
    /// <param name="barExchangeTime">Bar time from NinjaTrader (UTC, DateTimeKind.Unspecified)</param>
    /// <returns>UTC DateTimeOffset</returns>
    public static DateTimeOffset ConvertBarTimeToUtc(DateTime barExchangeTime)
    {
        // NinjaTrader provides bars in UTC, but with DateTimeKind.Unspecified.
        // Simply create DateTimeOffset with UTC kind and zero offset - no conversion needed.
        if (barExchangeTime.Kind == DateTimeKind.Utc)
        {
            return new DateTimeOffset(barExchangeTime, TimeSpan.Zero);
        }

        if (barExchangeTime.Kind == DateTimeKind.Local)
        {
            // Local machine time -> normalize to UTC using explicit conversion.
            // CRITICAL: Even if local timezone is UTC, use explicit conversion for correctness.
            var localOffset = new DateTimeOffset(barExchangeTime);
            return TimeZoneInfo.ConvertTime(localOffset, TimeZoneInfo.Utc);
        }

        // CRITICAL: NinjaTrader bars are UTC (even though Kind is Unspecified).
        // No conversion needed - just create DateTimeOffset with UTC kind and zero offset.
        return new DateTimeOffset(DateTime.SpecifyKind(barExchangeTime, DateTimeKind.Utc), TimeSpan.Zero);
    }
}
