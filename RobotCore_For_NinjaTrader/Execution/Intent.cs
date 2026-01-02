using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Canonical intent representation for execution.
/// Contains all fields needed for intent ID computation and order submission.
/// </summary>
public sealed class Intent
{
    public string TradingDate { get; private set; }
    public string Stream { get; private set; }
    public string Instrument { get; private set; }
    public string Session { get; private set; }
    public string SlotTimeChicago { get; private set; }
    public string? Direction { get; private set; }
    public decimal? EntryPrice { get; private set; }
    public decimal? StopPrice { get; private set; }
    public decimal? TargetPrice { get; private set; }
    public decimal? BeTrigger { get; private set; }
    public DateTimeOffset EntryTimeUtc { get; private set; }
    public string TriggerReason { get; private set; }

    public Intent(
        string tradingDate,
        string stream,
        string instrument,
        string session,
        string slotTimeChicago,
        string? direction,
        decimal? entryPrice,
        decimal? stopPrice,
        decimal? targetPrice,
        decimal? beTrigger,
        DateTimeOffset entryTimeUtc,
        string triggerReason)
    {
        TradingDate = tradingDate;
        Stream = stream;
        Instrument = instrument;
        Session = session;
        SlotTimeChicago = slotTimeChicago;
        Direction = direction;
        EntryPrice = entryPrice;
        StopPrice = stopPrice;
        TargetPrice = targetPrice;
        BeTrigger = beTrigger;
        EntryTimeUtc = entryTimeUtc;
        TriggerReason = triggerReason;
    }

    /// <summary>
    /// Compute intent ID from canonical fields.
    /// </summary>
    public string ComputeIntentId() =>
        ExecutionJournal.ComputeIntentId(
            TradingDate, Stream, Instrument, Session, SlotTimeChicago,
            Direction, EntryPrice, StopPrice, TargetPrice, BeTrigger);
}
