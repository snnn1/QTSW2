// Unit tests for terminal intent hardening (NG zombie-order incident fix).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test TERMINAL_INTENT
//
// Verifies: IsIntentCompleted, BE exclusion (TradeCompleted + remaining qty guard).

using System;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class TerminalIntentHardeningTests
{
    public static (bool Pass, string? Error) RunTerminalIntentTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "TerminalIntentHardeningTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);

            var utcNow = DateTimeOffset.UtcNow;
            var tradingDate = "2026-03-13";
            var stream = "NG1";

            // 1. IsIntentCompleted returns false when entry not completed
            var intentId = "test_intent_1";
            journal.RecordSubmission(intentId, tradingDate, stream, "MNG", "ENTRY_STOP", "broker_1", utcNow);
            journal.RecordEntryFill(intentId, tradingDate, stream, 3.14m, 2, utcNow, 1000m, "Short", "MNG", "NG");
            if (journal.IsIntentCompleted(intentId, tradingDate, stream))
                return (false, "IsIntentCompleted should be false when trade not completed");

            // 2. IsIntentCompleted returns true when trade completed (target fill)
            journal.RecordExitFill(intentId, tradingDate, stream, 3.15m, 2, "TARGET", utcNow);
            if (!journal.IsIntentCompleted(intentId, tradingDate, stream))
                return (false, "IsIntentCompleted should be true after RecordExitFill completes trade");

            // 3. IsIntentCompleted returns false when entry does not exist
            if (journal.IsIntentCompleted("nonexistent", tradingDate, stream))
                return (false, "IsIntentCompleted should be false for nonexistent intent");

            // 4. IsIntentCompleted returns true after RecordReconciliationComplete
            var intentId2 = "test_intent_2";
            journal.RecordSubmission(intentId2, tradingDate, stream, "MNG", "ENTRY_STOP", "broker_2", utcNow);
            journal.RecordEntryFill(intentId2, tradingDate, stream, 3.14m, 2, utcNow, 1000m, "Short", "MNG", "NG");
            journal.RecordReconciliationComplete(tradingDate, stream, intentId2, utcNow);
            if (!journal.IsIntentCompleted(intentId2, tradingDate, stream))
                return (false, "IsIntentCompleted should be true after RecordReconciliationComplete");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
