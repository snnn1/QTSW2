using System;

namespace QTSW2.Robot.Contracts
{

/// <summary>
/// ExecutionUpdate event payload. Minimal deterministic replay shape.
/// executionTime is the ONLY authoritative time input.
/// Reference: IEA_REPLAY_CONTRACT.md §3.3
/// </summary>
public sealed class ReplayExecutionUpdate
{
    public string? ExecutionId { get; set; }
    public string OrderId { get; set; } = "";
    public decimal FillPrice { get; set; }
    public int FillQuantity { get; set; }
    public string MarketPosition { get; set; } = "";
    public DateTimeOffset ExecutionTime { get; set; }
    public string? Tag { get; set; }
    public string? IntentId { get; set; }
    public string ExecutionInstrumentKey { get; set; } = "";
}
}
