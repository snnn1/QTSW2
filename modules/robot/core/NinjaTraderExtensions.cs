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
    /// </summary>
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
    /// Convert NinjaTrader bar exchange time to UTC DateTimeOffset.
    /// Handles Chicago timezone conversion with DST awareness.
    /// </summary>
    /// <param name="barExchangeTime">Bar time from NinjaTrader (DateTimeKind.Unspecified, Chicago time)</param>
    /// <returns>UTC DateTimeOffset</returns>
    public static DateTimeOffset ConvertBarTimeToUtc(DateTime barExchangeTime)
    {
        // NinjaTrader typically provides DateTimeKind.Unspecified for exchange-local timestamps,
        // but some APIs/callbacks can return Local or Utc kinds. DateTimeOffset requires:
        // - If Kind==Utc, offset MUST be 00:00 or it throws.
        // - If Kind==Unspecified, any offset is allowed.
        if (barExchangeTime.Kind == DateTimeKind.Utc)
        {
            return new DateTimeOffset(barExchangeTime, TimeSpan.Zero);
        }

        if (barExchangeTime.Kind == DateTimeKind.Local)
        {
            // Local machine time -> normalize to UTC.
            return new DateTimeOffset(barExchangeTime).ToUniversalTime();
        }

        // Treat Unspecified as exchange-local Chicago time and convert to UTC (DST-aware).
        var chicagoTz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        var unspecified = DateTime.SpecifyKind(barExchangeTime, DateTimeKind.Unspecified);
        var barChicagoOffset = new DateTimeOffset(unspecified, chicagoTz.GetUtcOffset(unspecified));
        return barChicagoOffset.ToUniversalTime();
    }
}
