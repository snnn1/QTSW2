using System;

namespace QTSW2.Robot.Contracts;

/// <summary>
/// Execution command layer: Strategy/runtime layers request execution outcomes via commands.
/// Commands flow to IEA; IEA orchestrates execution. Adapters remain transport-only.
/// </summary>

/// <summary>Base metadata for all execution commands.</summary>
public abstract class ExecutionCommandBase
{
    /// <summary>Deterministic identifier for correlating command lifecycle (RECEIVED → DISPATCHED → COMPLETED).</summary>
    public string CommandId { get; set; } = Guid.NewGuid().ToString();

    public string Instrument { get; set; } = "";
    public string? IntentId { get; set; }
    public string Reason { get; set; } = "";
    public string? CallerContext { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
}

/// <summary>Flatten reason classification for audit and policy.</summary>
public enum FlattenReason
{
    SLOT_EXPIRED,
    FORCED_FLATTEN,
    EMERGENCY,
    RECOVERY,
    BOOTSTRAP,
    IEA_BLOCK
}

/// <summary>Request to flatten an intent's position. Strategy layer emits this; IEA performs flatten lifecycle.</summary>
public sealed class FlattenIntentCommand : ExecutionCommandBase
{
    public FlattenReason FlattenReason { get; set; }
}

/// <summary>Request to cancel working orders for an intent. Strategy layer emits this; IEA performs cancellation.</summary>
public sealed class CancelIntentOrdersCommand : ExecutionCommandBase
{
}

/// <summary>Request to submit market reentry (post-forced-flatten at market open). Single-direction market order.</summary>
public sealed class SubmitMarketReentryCommand : ExecutionCommandBase
{
    public string? Stream { get; set; }
    public string? Session { get; set; }
    public string? SlotTimeChicago { get; set; }
    public string? TradingDate { get; set; }
    public string? ExecutionInstrument { get; set; }
    public string? ReentryIntentId { get; set; }
    public string? OriginalIntentId { get; set; }
    public string? Direction { get; set; }
    public int Quantity { get; set; }
}

/// <summary>Request to submit entry intent (stop brackets at range lock). Strategy layer emits this; IEA routes to entry submission.</summary>
public sealed class SubmitEntryIntentCommand : ExecutionCommandBase
{
    public string? Stream { get; set; }
    public string? Session { get; set; }
    public string? SlotTimeChicago { get; set; }
    public string? TradingDate { get; set; }
    public string? ExecutionInstrument { get; set; }
    public string? LongIntentId { get; set; }
    public string? ShortIntentId { get; set; }
    public decimal? BreakLong { get; set; }
    public decimal? BreakShort { get; set; }
    public decimal? LongStopPrice { get; set; }
    public decimal? LongTargetPrice { get; set; }
    public decimal? LongBeTrigger { get; set; }
    public decimal? ShortStopPrice { get; set; }
    public decimal? ShortTargetPrice { get; set; }
    public decimal? ShortBeTrigger { get; set; }
    public int? Quantity { get; set; }
    public int? MaxQuantity { get; set; }
    public string? CanonicalInstrument { get; set; }
    public string? OcoGroup { get; set; }
}
