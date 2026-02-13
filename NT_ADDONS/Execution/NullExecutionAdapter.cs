using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Null adapter for DRYRUN mode.
/// Logs all execution attempts but does not place orders.
/// </summary>
public sealed class NullExecutionAdapter : IExecutionAdapter
{
    private readonly RobotLogger _log;

    public NullExecutionAdapter(RobotLogger log)
    {
        _log = log;
    }

    public OrderSubmissionResult SubmitEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal? entryPrice,
        int quantity,
        string? entryOrderType,
        DateTimeOffset utcNow)
    {
        // DRYRUN: Log but do not place order
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_ORDER_DRYRUN", new
        {
            direction,
            entry_price = entryPrice,
            entry_order_type = entryOrderType,
            quantity,
            note = "DRYRUN mode - order not placed"
        }));

        return OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);
    }

    public OrderSubmissionResult SubmitStopEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        // DRYRUN: Log but do not place order
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_ENTRY_ORDER_DRYRUN", new
        {
            direction,
            stop_price = stopPrice,
            quantity,
            oco_group = ocoGroup,
            note = "DRYRUN mode - stop entry order not placed"
        }));

        return OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);
    }

    public OrderSubmissionResult SubmitProtectiveStop(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "PROTECTIVE_STOP_DRYRUN", new
        {
            direction,
            stop_price = stopPrice,
            quantity,
            note = "DRYRUN mode - order not placed"
        }));

        return OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);
    }

    public OrderSubmissionResult SubmitTargetOrder(
        string intentId,
        string instrument,
        string direction,
        decimal targetPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "TARGET_ORDER_DRYRUN", new
        {
            direction,
            target_price = targetPrice,
            quantity,
            note = "DRYRUN mode - order not placed"
        }));

        return OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);
    }

    public OrderModificationResult ModifyStopToBreakEven(
        string intentId,
        string instrument,
        decimal beStopPrice,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "BE_MODIFY_DRYRUN", new
        {
            be_stop_price = beStopPrice,
            note = "DRYRUN mode - modification not placed"
        }));

        return OrderModificationResult.SuccessResult(utcNow);
    }

    public FlattenResult Flatten(
        string intentId,
        string instrument,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_DRYRUN", new
        {
            note = "DRYRUN mode - flatten not executed"
        }));

        return FlattenResult.SuccessResult(utcNow);
    }
    
    public AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow)
    {
        // DRYRUN: Return empty snapshot
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ACCOUNT_SNAPSHOT_DRYRUN", state: "ENGINE",
            new { note = "DRYRUN mode - returning empty snapshot" }));
        
        return new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
    }
    
    public void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow)
    {
        // DRYRUN: Log what would be cancelled, but do nothing
        var robotOwnedOrders = snap.WorkingOrders?.Where(o => 
            (!string.IsNullOrEmpty(o.Tag) && o.Tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(o.OcoGroup) && o.OcoGroup.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase))
        ).ToList() ?? new List<WorkingOrderSnapshot>();
        
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_ROBOT_ORDERS_DRYRUN", state: "ENGINE",
            new
            {
                robot_owned_count = robotOwnedOrders.Count,
                robot_owned_order_ids = robotOwnedOrders.Select(o => o.OrderId).ToList(),
                note = "DRYRUN mode - would cancel robot-owned orders but not executing"
            }));
    }
}
