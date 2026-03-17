// IEA alignment tests for forced flatten, slot expiry flatten, and reentry.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test IEA_ALIGNMENT
//
// Verifies: FlattenIntentCommand path, FlattenReason, block checks, emergency fallback.

using System;
using System.Collections.Generic;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ForcedFlattenSlotExpiryReentryAlignmentTests
{
    public static (bool Pass, string? Error) RunIeaAlignmentTests()
    {
        var utcNow = DateTimeOffset.UtcNow;

        // 1. FlattenIntentCommand with FORCED_FLATTEN reason
        var forcedFlattenCmd = new FlattenIntentCommand
        {
            Instrument = "MES",
            IntentId = "intent-1",
            FlattenReason = FlattenReason.FORCED_FLATTEN,
            Reason = "SESSION_FORCED_FLATTEN",
            CallerContext = "HandleForcedFlatten",
            TimestampUtc = utcNow
        };
        if (forcedFlattenCmd.FlattenReason != FlattenReason.FORCED_FLATTEN)
            return (false, "FlattenIntentCommand FORCED_FLATTEN reason not set");

        // 2. FlattenIntentCommand with SLOT_EXPIRED reason
        var slotExpiryCmd = new FlattenIntentCommand
        {
            Instrument = "MNQ",
            IntentId = "intent-2",
            FlattenReason = FlattenReason.SLOT_EXPIRED,
            Reason = "SLOT_EXPIRED",
            CallerContext = "HandleSlotExpiry",
            TimestampUtc = utcNow
        };
        if (slotExpiryCmd.FlattenReason != FlattenReason.SLOT_EXPIRED)
            return (false, "FlattenIntentCommand SLOT_EXPIRED reason not set");

        // 3. FlattenReason enum has required values
        if (FlattenReason.FORCED_FLATTEN.ToString() != "FORCED_FLATTEN")
            return (false, "FlattenReason.FORCED_FLATTEN string mismatch");
        if (FlattenReason.SLOT_EXPIRED.ToString() != "SLOT_EXPIRED")
            return (false, "FlattenReason.SLOT_EXPIRED string mismatch");
        if (FlattenReason.EMERGENCY.ToString() != "EMERGENCY")
            return (false, "FlattenReason.EMERGENCY string mismatch");

        // 4. SubmitMarketReentryCommand has all required fields for reentry
        var reentryCmd = new SubmitMarketReentryCommand
        {
            ReentryIntentId = "slot1_REENTRY",
            OriginalIntentId = "orig-1",
            Direction = "Short",
            Quantity = 2,
            ExecutionInstrument = "MES",
            Instrument = "MES",
            Stream = "ES1",
            Session = "RTH",
            SlotTimeChicago = "09:30",
            TradingDate = "2026-03-12",
            Reason = "MARKET_REENTRY",
            CallerContext = "CheckMarketOpenReentry",
            TimestampUtc = utcNow
        };
        if (string.IsNullOrEmpty(reentryCmd.ReentryIntentId) || string.IsNullOrEmpty(reentryCmd.Direction) || reentryCmd.Quantity <= 0)
            return (false, "SubmitMarketReentryCommand missing required fields");

        // 5. NullExecutionAdapter accepts SubmitMarketReentryCommand (no-op)
        var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IeaAlignmentTests_" + Guid.NewGuid().ToString("N")[..8]);
        var log = new RobotLogger(tempRoot);
        var nullAdapter = new NullExecutionAdapter(log);
        try
        {
            nullAdapter.EnqueueExecutionCommand(forcedFlattenCmd);
            nullAdapter.EnqueueExecutionCommand(slotExpiryCmd);
            nullAdapter.EnqueueExecutionCommand(reentryCmd);
        }
        catch (Exception ex)
        {
            return (false, $"NullExecutionAdapter.EnqueueExecutionCommand threw: {ex.Message}");
        }

        // 5b. CapturingExecutionAdapter records SubmitMarketReentryCommand when enqueued
        var capturingAdapter = new CapturingExecutionAdapter(log);
        capturingAdapter.EnqueueExecutionCommand(reentryCmd);
        if (!capturingAdapter.TryGetLastCommand<SubmitMarketReentryCommand>(out var capturedReentry))
            return (false, "CapturingExecutionAdapter did not record SubmitMarketReentryCommand");
        if (capturedReentry.ReentryIntentId != "slot1_REENTRY" || capturedReentry.Direction != "Short" || capturedReentry.Quantity != 2)
            return (false, "CapturingExecutionAdapter recorded incorrect SubmitMarketReentryCommand fields");

        // 6. RequestSessionCloseFlattenImmediate: NullExecutionAdapter returns success (same-cycle path)
        var immediateResult = nullAdapter.RequestSessionCloseFlattenImmediate("intent-1", "MES", utcNow);
        if (immediateResult == null || !immediateResult.Success)
            return (false, $"NullExecutionAdapter.RequestSessionCloseFlattenImmediate should return success: result={immediateResult?.Success}, error={immediateResult?.ErrorMessage}");

        // 7. RequestSessionCloseFlattenImmediate: NinjaTraderSimAdapter (harness stub) returns null → fallback path
        var simAdapter = new NinjaTraderSimAdapter(tempRoot, log, new ExecutionJournal(tempRoot, log));
        var simImmediateResult = simAdapter.RequestSessionCloseFlattenImmediate("intent-1", "MES", utcNow);
        if (simImmediateResult != null)
            return (false, $"NinjaTraderSimAdapter (harness) RequestSessionCloseFlattenImmediate should return null for fallback: got {simImmediateResult}");

        // 8. RequestSessionCloseFlattenImmediate: NinjaTraderLiveAdapter returns null → fallback path
        var liveAdapter = new NinjaTraderLiveAdapter(log, new TimeService("America/Chicago"));
        var liveImmediateResult = liveAdapter.RequestSessionCloseFlattenImmediate("intent-1", "MES", utcNow);
        if (liveImmediateResult != null)
            return (false, $"NinjaTraderLiveAdapter RequestSessionCloseFlattenImmediate should return null for fallback: got {liveImmediateResult}");

        return (true, null);
    }
}

/// <summary>
/// Test adapter that records EnqueueExecutionCommand calls for verification.
/// </summary>
internal sealed class CapturingExecutionAdapter : IExecutionAdapter
{
    private readonly RobotLogger _log;
    private readonly List<ExecutionCommandBase> _commands = new();
    private readonly object _lock = new();

    public CapturingExecutionAdapter(RobotLogger log)
    {
        _log = log;
    }

    public bool TryGetLastCommand<T>(out T? cmd) where T : ExecutionCommandBase
    {
        lock (_lock)
        {
            for (var i = _commands.Count - 1; i >= 0; i--)
            {
                if (_commands[i] is T t)
                {
                    cmd = t;
                    return true;
                }
            }
        }
        cmd = null;
        return false;
    }

    public void EnqueueExecutionCommand(ExecutionCommandBase command)
    {
        lock (_lock)
        {
            _commands.Add(command);
        }
    }

    public OrderSubmissionResult SubmitEntryOrder(string intentId, string instrument, string direction, decimal? entryPrice, int quantity, string? entryOrderType, string? ocoGroup, DateTimeOffset utcNow)
        => OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);
    public OrderSubmissionResult SubmitStopEntryOrder(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
        => OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);
    public OrderSubmissionResult SubmitProtectiveStop(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
        => OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);
    public OrderSubmissionResult SubmitTargetOrder(string intentId, string instrument, string direction, decimal targetPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
        => OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);
    public OrderModificationResult ModifyStopToBreakEven(string intentId, string instrument, decimal beStopPrice, DateTimeOffset utcNow)
        => OrderModificationResult.SuccessResult(utcNow);
    public FlattenResult Flatten(string intentId, string instrument, DateTimeOffset utcNow)
        => FlattenResult.SuccessResult(utcNow);
    public AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow)
        => new AccountSnapshot { Positions = new List<PositionSnapshot>(), WorkingOrders = new List<WorkingOrderSnapshot>() };
    public (decimal? Bid, decimal? Ask) GetCurrentMarketPrice(string instrument, DateTimeOffset utcNow)
        => (null, null);
    public void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow) { }
    public void CancelOrders(IEnumerable<string> orderIds, DateTimeOffset utcNow) { }
    public FlattenResult? RequestSessionCloseFlattenImmediate(string intentId, string instrument, DateTimeOffset utcNow)
        => FlattenResult.SuccessResult(utcNow);
}
