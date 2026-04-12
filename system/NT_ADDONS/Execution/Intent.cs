using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Canonical intent representation for execution.
/// Contains all fields needed for intent ID computation and order submission.
/// </summary>
public sealed class Intent
{
    /// <summary>Session calendar day for this stream (typically Chicago <c>yyyy-MM-dd</c>). Used by session-identity execution gate; must match engine active day when the engine publishes a trading date.</summary>
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
    /// Compute intent ID from canonical fields (delegates to <see cref="ExecutionJournal.ComputeIntentId"/>).
    /// </summary>
    /// <remarks>Call sites must supply the same field normalization as registration (see <see cref="ExecutionJournal.ComputeIntentId"/> remarks).</remarks>
    public string ComputeIntentId() =>
        ExecutionJournal.ComputeIntentId(
            TradingDate, Stream, Instrument, Session, SlotTimeChicago,
            Direction, EntryPrice, StopPrice, TargetPrice, BeTrigger);

    /// <summary>
    /// Validates identity fields required before an intent may enter <see cref="NinjaTraderSimAdapter.RegisterIntent"/>. Fails closed so invalid intents do not propagate to submission.
    /// Direction must be Long or Short (case-insensitive); use canonical casing at construction when stable intent IDs matter.
    /// </summary>
    public static bool TryValidateRegistrationPrerequisites(Intent intent, out string failureReason)
    {
        failureReason = "";
        if (intent is null)
        {
            failureReason = "intent_null";
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.TradingDate))
        {
            failureReason = "trading_date_empty";
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.Stream))
        {
            failureReason = "stream_empty";
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.Instrument))
        {
            failureReason = "instrument_empty";
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.Session))
        {
            failureReason = "session_empty";
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.SlotTimeChicago))
        {
            failureReason = "slot_time_empty";
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.Direction))
        {
            failureReason = "direction_empty";
            return false;
        }

        if (!string.Equals(intent.Direction, "Long", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(intent.Direction, "Short", StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "direction_invalid";
            return false;
        }

        return true;
    }
}
