// Basic harness tests for IEA Execution Command Layer.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test EXECUTION_COMMANDS
//
// Verifies: ExecutionCommandTypes, EnqueueExecutionCommand contract, command model.

using System;
using System.IO;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ExecutionCommandTests
{
    public static (bool Pass, string? Error) RunExecutionCommandTests()
    {
        var utcNow = DateTimeOffset.UtcNow;

        // 1. FlattenIntentCommand can be created and has required fields (including commandId)
        var flattenCmd = new FlattenIntentCommand
        {
            Instrument = "MNQ",
            IntentId = "test-intent-1",
            Reason = "SLOT_EXPIRED",
            FlattenReason = FlattenReason.SLOT_EXPIRED,
            CallerContext = "HandleSlotExpiry",
            TimestampUtc = utcNow
        };
        if (flattenCmd.Instrument != "MNQ" || flattenCmd.FlattenReason != FlattenReason.SLOT_EXPIRED)
            return (false, "FlattenIntentCommand fields not set correctly");
        if (string.IsNullOrEmpty(flattenCmd.CommandId))
            return (false, "FlattenIntentCommand.CommandId should be auto-generated");

        // 2. CancelIntentOrdersCommand can be created
        var cancelCmd = new CancelIntentOrdersCommand
        {
            Instrument = "MNQ",
            IntentId = "test-intent-1",
            Reason = "FORCED_FLATTEN_CANCEL",
            CallerContext = "HandleForcedFlatten",
            TimestampUtc = utcNow
        };
        if (cancelCmd.IntentId != "test-intent-1")
            return (false, "CancelIntentOrdersCommand fields not set correctly");

        // 3. SubmitEntryIntentCommand can be created with bracket fields
        var submitCmd = new SubmitEntryIntentCommand
        {
            Instrument = "MNQ",
            LongIntentId = "long-1",
            ShortIntentId = "short-1",
            BreakLong = 21000m,
            BreakShort = 20950m,
            Quantity = 1,
            OcoGroup = "oco-1",
            TimestampUtc = utcNow
        };
        if (submitCmd.LongIntentId != "long-1" || submitCmd.BreakLong != 21000m)
            return (false, "SubmitEntryIntentCommand fields not set correctly");

        // 4. NullExecutionAdapter.EnqueueExecutionCommand is no-op (does not throw)
        var tempRoot = Path.Combine(Path.GetTempPath(), "ExecutionCommandTests_" + Guid.NewGuid().ToString("N")[..8]);
        var log = new RobotLogger(tempRoot);
        var nullAdapter = new NullExecutionAdapter(log);
        try
        {
            nullAdapter.EnqueueExecutionCommand(flattenCmd);
            nullAdapter.EnqueueExecutionCommand(cancelCmd);
            nullAdapter.EnqueueExecutionCommand(submitCmd);
        }
        catch (Exception ex)
        {
            return (false, $"NullExecutionAdapter.EnqueueExecutionCommand threw: {ex.Message}");
        }

        // 5. SubmitMarketReentryCommand can be created (IEA alignment); ReentryIntentId = canonical hash
        var reentryCmd = new SubmitMarketReentryCommand
        {
            Instrument = "MNQ",
            ExecutionInstrument = "MNQ",
            OriginalIntentId = "original-1",
            Direction = "Long",
            EntryPrice = 20123.75m,
            StopPrice = 20143.75m,
            TargetPrice = 20083.75m,
            BeTrigger = 20097.75m,
            Quantity = 1,
            Stream = "NQ1",
            Session = "RTH",
            SlotTimeChicago = "09:30",
            TradingDate = "2026-03-12",
            Reason = "MARKET_REENTRY",
            CallerContext = "CheckMarketOpenReentry",
            TimestampUtc = utcNow
        };
        {
            var instrumentRaw = reentryCmd.ExecutionInstrument ?? reentryCmd.Instrument ?? "";
            var execInstUpper = string.IsNullOrEmpty(instrumentRaw) ? "" : instrumentRaw.Trim().ToUpperInvariant();
            reentryCmd.ReentryIntentId = new Intent(
                reentryCmd.TradingDate ?? "",
                reentryCmd.Stream ?? "",
                instrumentRaw,
                execInstUpper,
                reentryCmd.Session ?? "",
                reentryCmd.SlotTimeChicago ?? "",
                reentryCmd.Direction ?? "Long",
                reentryCmd.EntryPrice,
                reentryCmd.StopPrice,
                reentryCmd.TargetPrice,
                reentryCmd.BeTrigger,
                reentryCmd.TimestampUtc,
                "SUBMIT_MARKET_REENTRY").ComputeIntentId();
        }
        if (string.IsNullOrEmpty(reentryCmd.ReentryIntentId) || reentryCmd.Direction != "Long" || reentryCmd.Quantity != 1)
            return (false, "SubmitMarketReentryCommand fields not set correctly");
        if (reentryCmd.EntryPrice != 20123.75m || reentryCmd.BeTrigger != 20097.75m)
            return (false, "SubmitMarketReentryCommand should preserve reentry BE metadata");
        if (!(reentryCmd is ExecutionCommandBase))
            return (false, "SubmitMarketReentryCommand should inherit ExecutionCommandBase");

        return (true, null);
    }
}
