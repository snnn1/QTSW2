using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace QTSW2.Robot.Core.SessionAuthority;

/// <summary>Root JSON for <c>configs/robot/session_calendar.json</c>.</summary>
public sealed class SessionCalendarDocument
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("days")]
    public List<SessionCalendarDayRow>? Days { get; set; }
}

/// <summary>Per-(trading_date, calendar_group) override.</summary>
public sealed class SessionCalendarDayRow
{
    [JsonPropertyName("trading_date")]
    public string? TradingDate { get; set; }

    [JsonPropertyName("calendar_group")]
    public string? CalendarGroup { get; set; }

    /// <summary>HOLIDAY | EARLY_CLOSE | NORMAL (optional explicit normal).</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("market_close_chicago")]
    public string? MarketCloseChicago { get; set; }

    [JsonPropertyName("market_reopen_chicago")]
    public string? MarketReopenChicago { get; set; }
}
