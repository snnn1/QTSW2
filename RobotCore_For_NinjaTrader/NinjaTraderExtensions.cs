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
        // Times[0][0] is DateTimeKind.Unspecified, representing exchange local time (Chicago)
        // We need to interpret it as Chicago time and convert to UTC (DST-aware)
        // Ensure DateTimeKind is Unspecified before creating DateTimeOffset (prevents UTC offset errors)
        var barExchangeTimeUnspecified = barExchangeTime.Kind == DateTimeKind.Unspecified 
            ? barExchangeTime 
            : DateTime.SpecifyKind(barExchangeTime, DateTimeKind.Unspecified);
        
        var chicagoTz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        var barChicagoOffset = new DateTimeOffset(barExchangeTimeUnspecified, chicagoTz.GetUtcOffset(barExchangeTimeUnspecified));
        return barChicagoOffset.ToUniversalTime();
    }
}
