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
    /// 
    /// CRITICAL: This method assumes barExchangeTime is in Chicago local time (exchange time).
    /// If Times[0][0] is already UTC, use the detection logic in RobotSimStrategy.OnBarUpdate instead.
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
        // GetUtcOffset returns the offset FROM UTC (negative for timezones behind UTC)
        // For Chicago in January: GetUtcOffset returns -06:00:00
        var chicagoOffset = chicagoTz.GetUtcOffset(barExchangeTimeUnspecified);
        var barChicagoOffset = new DateTimeOffset(barExchangeTimeUnspecified, chicagoOffset);
        return barChicagoOffset.ToUniversalTime();
    }
}
