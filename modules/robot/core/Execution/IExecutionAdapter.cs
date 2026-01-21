using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Execution adapter interface for broker-agnostic order placement.
/// RobotEngine calls adapters when it wants to place/modify orders.
/// Adapters handle broker-specific details.
/// </summary>
public interface IExecutionAdapter
{
    /// <summary>
    /// Place an entry order (market or limit).
    /// </summary>
    /// <param name="intentId">Unique intent identifier (hash of canonical fields)</param>
    /// <param name="instrument">Instrument symbol</param>
    /// <param name="direction">"Long" or "Short"</param>
    /// <param name="entryPrice">Entry price (null for market order)</param>
    /// <param name="quantity">Number of contracts</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Order submission result</returns>
    OrderSubmissionResult SubmitEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal? entryPrice,
        int quantity,
        DateTimeOffset utcNow);

    /// <summary>
    /// Place a stop-market entry order (breakout stop).
    /// Intended for placing bracketed stop entries immediately after RANGE_LOCKED.
    /// </summary>
    /// <param name="intentId">Unique intent identifier (hash of canonical fields)</param>
    /// <param name="instrument">Instrument symbol</param>
    /// <param name="direction">"Long" or "Short"</param>
    /// <param name="stopPrice">Stop trigger price</param>
    /// <param name="quantity">Number of contracts</param>
    /// <param name="ocoGroup">Optional OCO group string to link long/short entries</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Order submission result</returns>
    OrderSubmissionResult SubmitStopEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow);

    /// <summary>
    /// Place protective stop order (must be placed immediately after entry submission or fill).
    /// </summary>
    /// <param name="intentId">Intent identifier</param>
    /// <param name="instrument">Instrument symbol</param>
    /// <param name="direction">"Long" or "Short"</param>
    /// <param name="stopPrice">Stop loss price</param>
    /// <param name="quantity">Number of contracts</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Order submission result</returns>
    OrderSubmissionResult SubmitProtectiveStop(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        DateTimeOffset utcNow);

    /// <summary>
    /// Place target order (profit target).
    /// </summary>
    /// <param name="intentId">Intent identifier</param>
    /// <param name="instrument">Instrument symbol</param>
    /// <param name="direction">"Long" or "Short"</param>
    /// <param name="targetPrice">Target price</param>
    /// <param name="quantity">Number of contracts</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Order submission result</returns>
    OrderSubmissionResult SubmitTargetOrder(
        string intentId,
        string instrument,
        string direction,
        decimal targetPrice,
        int quantity,
        DateTimeOffset utcNow);

    /// <summary>
    /// Modify stop to break-even (when BE trigger is reached).
    /// </summary>
    /// <param name="intentId">Intent identifier</param>
    /// <param name="instrument">Instrument symbol</param>
    /// <param name="beStopPrice">Break-even stop price (entry ± 1 tick)</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Order modification result</returns>
    OrderModificationResult ModifyStopToBreakEven(
        string intentId,
        string instrument,
        decimal beStopPrice,
        DateTimeOffset utcNow);

    /// <summary>
    /// Cancel all orders and flatten position for this intent.
    /// </summary>
    /// <param name="intentId">Intent identifier</param>
    /// <param name="instrument">Instrument symbol</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Flatten result</returns>
    FlattenResult Flatten(
        string intentId,
        string instrument,
        DateTimeOffset utcNow);
    
    /// <summary>
    /// Get account snapshot (positions and working orders) for recovery reconciliation.
    /// </summary>
    /// <param name="utcNow">Current UTC timestamp</param>
    /// <returns>Account snapshot</returns>
    AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow);
    
    /// <summary>
    /// Cancel robot-owned working orders only (strict prefix matching: "QTSW2:").
    /// </summary>
    /// <param name="snap">Account snapshot from GetAccountSnapshot</param>
    /// <param name="utcNow">Current UTC timestamp</param>
    void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow);
}

/// <summary>
/// Account snapshot for recovery reconciliation.
/// </summary>
public class AccountSnapshot
{
    public List<PositionSnapshot>? Positions { get; set; }
    public List<WorkingOrderSnapshot>? WorkingOrders { get; set; }
}

/// <summary>
/// Position snapshot from broker account.
/// </summary>
public class PositionSnapshot
{
    public string Instrument { get; set; } = "";
    public int Quantity { get; set; }
    public decimal AveragePrice { get; set; }
}

/// <summary>
/// Working order snapshot from broker account.
/// </summary>
public class WorkingOrderSnapshot
{
    public string OrderId { get; set; } = "";
    public string Instrument { get; set; } = "";
    public string? Tag { get; set; }
    public string? OcoGroup { get; set; }
    public string? OrderType { get; set; }
    public decimal? Price { get; set; }
    public decimal? StopPrice { get; set; }
    public int Quantity { get; set; }
}
