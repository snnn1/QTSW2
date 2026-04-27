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

            // 5. RecordSubmission persists BE trigger metadata up front for original entries
            var intentId3 = "test_intent_3";
            journal.RecordSubmission(
                intentId3,
                tradingDate,
                stream,
                "MNG",
                "ENTRY_STOP",
                "broker_3",
                utcNow,
                expectedEntryPrice: 2.635m,
                entryPrice: 2.635m,
                stopPrice: 2.710m,
                targetPrice: 2.585m,
                beTriggerPrice: 2.6025m,
                direction: "Short",
                ocoGroup: "QTSW2:OCO_ENTRY:2026-04-13:NG2:11:00:test");
            var entry3 = journal.GetEntry(intentId3, tradingDate, stream);
            if (entry3 == null)
                return (false, "Expected journal entry for test_intent_3");
            if (entry3.BETriggerPrice != 2.6025m)
                return (false, $"Expected BETriggerPrice 2.6025 for test_intent_3, got {entry3.BETriggerPrice?.ToString() ?? "null"}");

            // 6. Live tagged unfilled entries do not block broker-flat release, and cancel keeps them terminal.
            var cancelRoot = Path.Combine(Path.GetTempPath(), "TerminalIntentCancel_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(cancelRoot);
            try
            {
                var cancelJournal = new ExecutionJournal(cancelRoot, new RobotLogger(cancelRoot));
                var cancelIntent = "test_intent_cancel";
                cancelJournal.RecordSubmission(cancelIntent, tradingDate, stream, "MNG", "ENTRY_STOP", "broker_4", utcNow, direction: "Long");
                var beforeCancelBlockers = cancelJournal.CountReleaseBlockingAdoptionCandidates("MNG", "NG", 0, 0, Array.Empty<string>(), null);
                if (beforeCancelBlockers != 0)
                    return (false, $"Live tagged unfilled entry should not block broker-flat release before cancel, got {beforeCancelBlockers} blocker(s)");
                if (!cancelJournal.RecordCancelledUnfilledEntry(cancelIntent, tradingDate, stream, utcNow))
                    return (false, "Expected RecordCancelledUnfilledEntry to terminalize unfilled canceled entry");
                var canceledEntry = cancelJournal.GetEntry(cancelIntent, tradingDate, stream);
                if (canceledEntry == null || !canceledEntry.TradeCompleted)
                    return (false, "Canceled entry should be marked TradeCompleted");
                if (canceledEntry.CompletionReason != CompletionReasons.ENTRY_CANCELLED_UNFILLED)
                    return (false, $"Canceled entry completion reason mismatch: {canceledEntry.CompletionReason ?? "null"}");
                var afterCancelBlockers = cancelJournal.CountReleaseBlockingAdoptionCandidates("MNG", "NG", 0, 0, Array.Empty<string>(), null);
                if (afterCancelBlockers != 0)
                    return (false, $"Canceled unfilled entry should stop blocking release adoption, got {afterCancelBlockers} blocker(s)");
            }
            finally
            {
                try { Directory.Delete(cancelRoot, recursive: true); } catch { }
            }

            // 7. Late terminal fill after reconciliation broker-flat completion should not double-count into overfill.
            var lateFillIntent = "test_intent_late_fill";
            var completedAt = utcNow.AddSeconds(5);
            journal.RecordSubmission(lateFillIntent, tradingDate, stream, "MES", "ENTRY_STOP", "broker_5", utcNow, direction: "Long");
            journal.RecordEntryFill(lateFillIntent, tradingDate, stream, 4500m, 2, utcNow, 5m, "Long", "MES", "ES");
            journal.RecordReconciliationComplete(tradingDate, stream, lateFillIntent, completedAt);
            journal.RecordExitFill(lateFillIntent, tradingDate, stream, 4501m, 2, "STOP", completedAt.AddMilliseconds(75));
            var lateFillEntry = journal.GetEntry(lateFillIntent, tradingDate, stream);
            if (lateFillEntry == null)
                return (false, "Expected journal entry for late terminal fill test");
            if (lateFillEntry.ExitFilledQuantityTotal != 2)
                return (false, $"Late terminal fill should not overfill completed intent, got exit qty {lateFillEntry.ExitFilledQuantityTotal}");
            if (!lateFillEntry.TradeCompleted)
                return (false, "Late terminal fill should preserve completed state");
            if (lateFillEntry.CompletionReason != "STOP")
                return (false, $"Late terminal fill should reconcile completion reason to STOP, got {lateFillEntry.CompletionReason ?? "null"}");
            if (lateFillEntry.ExitAvgFillPrice != 4501m)
                return (false, $"Late terminal fill should upgrade exit avg price to 4501, got {lateFillEntry.ExitAvgFillPrice?.ToString() ?? "null"}");
            if (lateFillEntry.RealizedPnLGross != 10m)
                return (false, $"Late terminal fill should upgrade realized gross PnL to 10, got {lateFillEntry.RealizedPnLGross?.ToString() ?? "null"}");
            if (lateFillEntry.RealizedPnLNet != 10m)
                return (false, $"Late terminal fill should upgrade realized net PnL to 10, got {lateFillEntry.RealizedPnLNet?.ToString() ?? "null"}");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
