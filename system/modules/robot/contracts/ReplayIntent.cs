using System;

namespace QTSW2.Robot.Contracts
{

/// <summary>
/// Branch-relevant intent fields for replay. Independent of NinjaTrader types.
/// Mirrors Intent shape from IEA_REPLAY_CONTRACT.md §3.1.
/// </summary>
public sealed class ReplayIntent
{
    public string TradingDate { get; set; } = "";
    public string Stream { get; set; } = "";
    public string Instrument { get; set; } = "";
    public string ExecutionInstrument { get; set; } = "";
    public string Session { get; set; } = "";
    public string SlotTimeChicago { get; set; } = "";
    public string? Direction { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? StopPrice { get; set; }
    public decimal? TargetPrice { get; set; }
    public decimal? BeTrigger { get; set; }
    public DateTimeOffset EntryTimeUtc { get; set; }
    public string TriggerReason { get; set; } = "";
}
}
