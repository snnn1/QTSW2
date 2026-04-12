using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Tracks exposure per intent (not per instrument).
/// Each intent maintains its own entry/exit fill tracking independently.
/// </summary>
public class IntentExposure
{
    public string IntentId { get; set; } = "";
    public string StreamId { get; set; } = "";  // e.g., "NQ1"
    public string Instrument { get; set; } = "";  // e.g., "NQ"
    public string Direction { get; set; } = "";  // "Long" or "Short"
    public int Quantity { get; set; }  // Original intended quantity
    public int EntryFilledQty { get; set; }  // Cumulative entry fills
    public int ExitFilledQty { get; set; }  // Cumulative exit fills
    public IntentExposureState State { get; set; }  // ACTIVE | CLOSED | STANDING_DOWN
    
    /// <summary>
    /// Calculate remaining exposure: entry fills minus exit fills.
    /// </summary>
    public int RemainingExposure => EntryFilledQty - ExitFilledQty;
}

/// <summary>
/// State of intent exposure.
/// </summary>
public enum IntentExposureState
{
    ACTIVE,        // Intent has active exposure (entry filled, exit not fully filled)
    CLOSED,        // Intent exposure fully closed (exit fills >= entry fills)
    STANDING_DOWN  // Intent standing down due to protective failure or error
}
