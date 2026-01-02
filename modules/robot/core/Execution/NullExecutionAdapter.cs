using System;

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
        DateTimeOffset utcNow)
    {
        // DRYRUN: Log but do not place order
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_ORDER_DRYRUN", new
        {
            direction,
            entry_price = entryPrice,
            quantity,
            note = "DRYRUN mode - order not placed"
        }));

        return OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);
    }

    public OrderSubmissionResult SubmitProtectiveStop(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
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
}
