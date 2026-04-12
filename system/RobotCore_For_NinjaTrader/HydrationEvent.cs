using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core;

/// <summary>
/// Immutable event representing hydration lifecycle events for a stream on a trading day.
/// Logs edge events only: STREAM_INITIALIZED, PRE_HYDRATION_COMPLETE, ARMED, RANGE_BUILDING_START, RANGE_LOCKED.
/// </summary>
public sealed class HydrationEvent
{
    // Event metadata
    public string event_type { get; }
    public string trading_day { get; }
    public string stream_id { get; }
    public string canonical_instrument { get; }
    public string execution_instrument { get; }
    public string session { get; }
    public string slot_time_chicago { get; }
    public string timestamp_utc { get; }
    public string timestamp_chicago { get; }
    public string state { get; }
    
    // Event-specific data (varies by event type)
    public Dictionary<string, object> data { get; }
    
    public HydrationEvent(
        string eventType,
        string tradingDay,
        string streamId,
        string canonicalInstrument,
        string executionInstrument,
        string session,
        string slotTimeChicago,
        DateTimeOffset timestampUtc,
        DateTimeOffset timestampChicago,
        string state,
        Dictionary<string, object> data
    )
    {
        event_type = eventType;
        trading_day = tradingDay;
        stream_id = streamId;
        canonical_instrument = canonicalInstrument;
        execution_instrument = executionInstrument;
        session = session;
        slot_time_chicago = slotTimeChicago;
        timestamp_utc = timestampUtc.ToString("o");
        timestamp_chicago = timestampChicago.ToString("o");
        this.state = state;
        this.data = data ?? new Dictionary<string, object>();
    }
}
