using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Immutable facts captured from NinjaTrader's ExecutionUpdate callback while still on the callback path.
/// IEA workers must use this snapshot instead of reading live NT Order/Execution objects off-thread.
/// </summary>
public sealed class ExecutionUpdateSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; }
    public string ExecutionId { get; init; } = "";
    public string BrokerOrderId { get; init; } = "";
    public string EncodedTag { get; init; } = "";
    public string IntentId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public string ExecutionInstrumentKey { get; init; } = "";
    public string AccountName { get; init; } = "";
    public decimal FillPrice { get; init; }
    public int FillQuantity { get; init; }
    public string OrderState { get; init; } = "";
    public string MarketPosition { get; init; } = "";
    public long ExecutionTimeTicks { get; init; }
    public int OrderQuantity { get; init; }
    public int OrderFilledQuantity { get; init; }
    public decimal? StopPrice { get; init; }
    public decimal? LimitPrice { get; init; }
    public string OrderAction { get; init; } = "";
    public string? OrderTypeFromTag { get; init; }
    public bool IsProtectiveOrder { get; init; }
    public bool IsRobotFlattenOrder { get; init; }

    public string SideFromOrderAction
    {
        get
        {
            if (OrderAction.IndexOf("Buy", StringComparison.OrdinalIgnoreCase) >= 0) return "BUY";
            if (OrderAction.IndexOf("Sell", StringComparison.OrdinalIgnoreCase) >= 0) return "SELL";
            return "";
        }
    }
}
