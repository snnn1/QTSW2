using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core;

/// <summary>
/// Recoverable snapshot of partial RANGE_BUILDING state for restart recovery.
/// Persisted when a stream is building its range; restored on restart to resume instead of resetting.
/// </summary>
public sealed class RangeBuildingSnapshot
{
    public const string SourceMarker = "RANGE_BUILDING_SNAPSHOT";

    public string source { get; set; } = SourceMarker;
    public string trading_date { get; set; } = "";
    public string stream_id { get; set; } = "";
    public string instrument { get; set; } = "";
    public string session { get; set; } = "";
    public string slot_time { get; set; } = "";
    public string range_start_chicago { get; set; } = "";
    public string range_start_utc { get; set; } = "";
    public string last_processed_bar_time_utc { get; set; } = "";
    public int bar_count { get; set; }
    public decimal? range_high { get; set; }
    public decimal? range_low { get; set; }
    public decimal? freeze_close { get; set; }
    public string freeze_close_source { get; set; } = "";
    public decimal tick_size { get; set; }
    public string snapshot_timestamp_utc { get; set; } = "";

    /// <summary>
    /// Bars in the range window at snapshot time. Required for deterministic restore and bar deduplication.
    /// </summary>
    public List<RangeBuildingSnapshotBar> bars { get; set; } = new();
}

/// <summary>
/// Serializable bar for RangeBuildingSnapshot.
/// </summary>
public sealed class RangeBuildingSnapshotBar
{
    public string timestamp_utc { get; set; } = "";
    public decimal open { get; set; }
    public decimal high { get; set; }
    public decimal low { get; set; }
    public decimal close { get; set; }
}
