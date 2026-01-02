using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// NinjaTrader Live adapter: places orders in real brokerage account.
/// Stub implementation - actual NT integration to be added in Phase C.
/// Requires explicit two-key enable (CLI flag + config).
/// </summary>
public sealed class NinjaTraderLiveAdapter : IExecutionAdapter
{
    private readonly RobotLogger _log;
    private readonly TimeService _time;

    public NinjaTraderLiveAdapter(RobotLogger log, TimeService time)
    {
        _log = log;
        _time = time;
    }

    public OrderSubmissionResult SubmitEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal? entryPrice,
        int quantity,
        DateTimeOffset utcNow)
    {
        // TODO: Phase C - Implement actual NT Live order placement with guardrails
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "ENTRY",
            direction,
            entry_price = entryPrice,
            quantity,
            account = "LIVE",
            note = "Stub - actual NT integration pending"
        }));

        return OrderSubmissionResult.FailureResult("LIVE adapter not yet implemented", utcNow);
    }

    public OrderSubmissionResult SubmitProtectiveStop(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "PROTECTIVE_STOP",
            direction,
            stop_price = stopPrice,
            quantity,
            account = "LIVE",
            note = "Stub - actual NT integration pending"
        }));

        return OrderSubmissionResult.FailureResult("LIVE adapter not yet implemented", utcNow);
    }

    public OrderSubmissionResult SubmitTargetOrder(
        string intentId,
        string instrument,
        string direction,
        decimal targetPrice,
        int quantity,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "TARGET",
            direction,
            target_price = targetPrice,
            quantity,
            account = "LIVE",
            note = "Stub - actual NT integration pending"
        }));

        return OrderSubmissionResult.FailureResult("LIVE adapter not yet implemented", utcNow);
    }

    public OrderModificationResult ModifyStopToBreakEven(
        string intentId,
        string instrument,
        decimal beStopPrice,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_ATTEMPT", new
        {
            be_stop_price = beStopPrice,
            account = "LIVE",
            note = "Stub - actual NT integration pending"
        }));

        return OrderModificationResult.FailureResult("LIVE adapter not yet implemented", utcNow);
    }

    public FlattenResult Flatten(
        string intentId,
        string instrument,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_ATTEMPT", new
        {
            account = "LIVE",
            note = "Stub - actual NT integration pending"
        }));

        return FlattenResult.FailureResult("LIVE adapter not yet implemented", utcNow);
    }
}
