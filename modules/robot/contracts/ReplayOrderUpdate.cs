using System;

namespace QTSW2.Robot.Contracts
{

/// <summary>
/// OrderUpdate event payload. Raw NT objects must not influence branching.
/// Reference: IEA_REPLAY_CONTRACT.md §3.4
/// </summary>
public sealed class ReplayOrderUpdate
{
    public string OrderId { get; set; } = "";
    public string OrderState { get; set; } = "";
    public int? Filled { get; set; }
    public int? Quantity { get; set; }
    public string? Tag { get; set; }
    public string? IntentId { get; set; }
    public DateTimeOffset? UpdateTime { get; set; }
}
}
