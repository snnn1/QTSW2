using System;

namespace QTSW2.Robot.Contracts
{

/// <summary>
/// Tick/MarketData event payload. If tickTimeFromEvent is null, harness synthesizes deterministic time.
/// Reference: IEA_REPLAY_CONTRACT.md §3.5
/// </summary>
public sealed class ReplayTick
{
    public decimal TickPrice { get; set; }
    public DateTimeOffset? TickTimeFromEvent { get; set; }
    public string ExecutionInstrument { get; set; } = "";
}
}
