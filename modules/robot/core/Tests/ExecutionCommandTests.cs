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

        // 5. Command types are ExecutionCommandBase
        if (!(flattenCmd is ExecutionCommandBase))
            return (false, "FlattenIntentCommand should inherit ExecutionCommandBase");
        if (!(cancelCmd is ExecutionCommandBase))
            return (false, "CancelIntentOrdersCommand should inherit ExecutionCommandBase");
        if (!(submitCmd is ExecutionCommandBase))
            return (false, "SubmitEntryIntentCommand should inherit ExecutionCommandBase");

        return (true, null);
    }
}
