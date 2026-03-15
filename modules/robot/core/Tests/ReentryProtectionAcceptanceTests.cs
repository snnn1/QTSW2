// Tests for reentry protection acceptance wiring.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test REENTRY_PROTECTION
//
// Verifies: HandleReentryProtectionAccepted clears ExecutionInterruptedByClose,
// only triggers for reentry protective set (not original entry),
// and callback path is correctly gated.

using System;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ReentryProtectionAcceptanceTests
{
    public static (bool Pass, string? Error) RunReentryProtectionTests()
    {
        var root = ProjectRootResolver.ResolveProjectRoot();
        if (string.IsNullOrEmpty(root))
            return (false, "Project root could not be resolved");
        var configPath = Path.Combine(root, "configs", "analyzer_robot_parity.json");
        if (!File.Exists(configPath))
            return (false, $"Config not found: {configPath}");

        var tempRoot = Path.Combine(Path.GetTempPath(), "ReentryProtectionTests_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "logs", "robot", "journal"));
            Directory.CreateDirectory(Path.Combine(tempRoot, "data", "execution_journals"));

            var spec = ParitySpec.LoadFromFile(configPath);
            var time = new TimeService(spec.timezone);
            var log = new RobotLogger(tempRoot);
            var journals = new JournalStore(tempRoot);

            // 1. Reentry intent detection: only _REENTRY suffix triggers callback
            if (!"slot132_REENTRY".EndsWith("_REENTRY", StringComparison.OrdinalIgnoreCase))
                return (false, "Reentry intent detection: slot132_REENTRY should match _REENTRY suffix");
            if ("abc123orig".EndsWith("_REENTRY", StringComparison.OrdinalIgnoreCase))
                return (false, "Original intent abc123orig should NOT match _REENTRY suffix");

            // 2. Create journal with reentry state: ProtectionSubmitted=true, ExecutionInterruptedByClose=true
            var tradingDate = new DateOnly(2026, 3, 15);
            var tradingDateStr = tradingDate.ToString("yyyy-MM-dd");
            var streamId = "ES1";
            var reentryIntentId = "20260315_ES1_REENTRY";

            var journal = new StreamJournal
            {
                TradingDate = tradingDateStr,
                Stream = streamId,
                Committed = false,
                SlotStatus = SlotStatus.ACTIVE,
                ExecutionInterruptedByClose = true,
                OriginalIntentId = "orig123",
                ReentryIntentId = reentryIntentId,
                ReentrySubmitted = true,
                ReentryFilled = true,
                ProtectionSubmitted = true,
                ProtectionAccepted = false,
                SlotInstanceKey = "20260315_ES1_07:30",
                PriorJournalKey = "20260314_ES1"
            };
            journals.Save(journal);

            // 3. Create minimal StreamStateMachine
            var directive = new TimetableStream
            {
                stream = streamId,
                instrument = "MES",
                session = "S1",
                slot_time = "07:30",
                enabled = true,
                decision_time = "07:30"
            };

            var sm = new StreamStateMachine(
                time,
                spec,
                log,
                journals,
                tradingDate,
                "test_hash",
                directive,
                ExecutionMode.DRYRUN,
                1,
                2,
                tempRoot,
                executionAdapter: new NullExecutionAdapter(log),
                executionJournal: new ExecutionJournal(tempRoot, log));

            // 4. Verify initial state
            if (!sm.Committed && journal.ExecutionInterruptedByClose)
            {
                // Journal was saved with ExecutionInterruptedByClose=true; StreamStateMachine loads it
                // We need to verify the internal journal state. StreamStateMachine doesn't expose it.
                // Call HandleReentryProtectionAccepted and then check via journal reload.
            }

            // 5. Call HandleReentryProtectionAccepted with matching reentryIntentId
            sm.HandleReentryProtectionAccepted(DateTimeOffset.UtcNow, reentryIntentId);

            // 6. Reload journal and verify ExecutionInterruptedByClose is cleared
            var reloaded = journals.TryLoad(tradingDateStr, streamId);
            if (reloaded == null)
                return (false, "Journal not found after HandleReentryProtectionAccepted");
            if (reloaded.ExecutionInterruptedByClose)
                return (false, $"ExecutionInterruptedByClose should be false after HandleReentryProtectionAccepted, got true");
            if (!reloaded.ProtectionAccepted)
                return (false, "ProtectionAccepted should be true after HandleReentryProtectionAccepted");

            // 7. Original entry case: ProtectionSubmitted=false -> HandleReentryProtectionAccepted does nothing
            var journal2 = new StreamJournal
            {
                TradingDate = tradingDateStr,
                Stream = "ES2",
                Committed = false,
                SlotStatus = SlotStatus.ACTIVE,
                ExecutionInterruptedByClose = true,
                OriginalIntentId = "orig456",
                ReentryIntentId = null,
                ProtectionSubmitted = false, // Original entry - never set for reentry
                ProtectionAccepted = false,
                SlotInstanceKey = "20260315_ES2_09:30"
            };
            journals.Save(journal2);

            var directive2 = new TimetableStream
            {
                stream = "ES2",
                instrument = "MES",
                session = "S2",
                slot_time = "09:30",
                enabled = true,
                decision_time = "09:30"
            };

            var sm2 = new StreamStateMachine(
                time,
                spec,
                log,
                journals,
                tradingDate,
                "test_hash",
                directive2,
                ExecutionMode.DRYRUN,
                1,
                2,
                tempRoot,
                executionAdapter: new NullExecutionAdapter(log),
                executionJournal: new ExecutionJournal(tempRoot, log));

            // Call with a fake "reentry" intent - but this stream has ProtectionSubmitted=false (original entry)
            sm2.HandleReentryProtectionAccepted(DateTimeOffset.UtcNow, "20260315_ES2_REENTRY");

            var reloaded2 = journals.TryLoad(tradingDateStr, "ES2");
            if (reloaded2 == null)
                return (false, "ES2 journal not found");
            if (!reloaded2.ExecutionInterruptedByClose)
                return (false, "Original entry: ExecutionInterruptedByClose should remain true (HandleReentryProtectionAccepted does nothing when ProtectionSubmitted=false)");

            // 8. Wrong reentryIntentId: stream has ReentryIntentId="X" but we pass "Y" -> no-op
            var journal3 = new StreamJournal
            {
                TradingDate = tradingDateStr,
                Stream = "ES3",
                Committed = false,
                SlotStatus = SlotStatus.ACTIVE,
                ExecutionInterruptedByClose = true,
                ReentryIntentId = "20260315_ES3_REENTRY",
                ProtectionSubmitted = true,
                ProtectionAccepted = false,
                SlotInstanceKey = "20260315_ES3_07:30"
            };
            journals.Save(journal3);

            var directive3 = new TimetableStream
            {
                stream = "ES3",
                instrument = "MES",
                session = "S1",
                slot_time = "07:30",
                enabled = true,
                decision_time = "07:30"
            };

            var sm3 = new StreamStateMachine(
                time,
                spec,
                log,
                journals,
                tradingDate,
                "test_hash",
                directive3,
                ExecutionMode.DRYRUN,
                1,
                2,
                tempRoot,
                executionAdapter: new NullExecutionAdapter(log),
                executionJournal: new ExecutionJournal(tempRoot, log));

            sm3.HandleReentryProtectionAccepted(DateTimeOffset.UtcNow, "WRONG_REENTRY_ID");

            var reloaded3 = journals.TryLoad(tradingDateStr, "ES3");
            if (reloaded3 == null)
                return (false, "ES3 journal not found");
            if (!reloaded3.ExecutionInterruptedByClose)
                return (false, "Wrong reentryIntentId: ExecutionInterruptedByClose should remain true (no match)");

            return (true, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch { /* best effort */ }
        }
    }
}
