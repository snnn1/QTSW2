using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class LateSessionCloseJournalRepairTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var e = Case_ReconcileInterruptedSiblingRowsWhenBrokerFlat();
        if (e != null) return (false, e);
        return (true, null);
    }

    private static string? Case_ReconcileInterruptedSiblingRowsWhenBrokerFlat()
    {
        var root = Path.Combine(Path.GetTempPath(), "late_flatten_journal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(root);
            var utcNow = DateTimeOffset.UtcNow;

            WriteEntry(journalDir, "2026-04-13", "NG1", "intent_ng1", CreateOpenEntry("intent_ng1", "MNG", 2));
            WriteEntry(journalDir, "2026-04-13", "NG2", "intent_ng2", CreateOpenEntry("intent_ng2", "MNG", 2));
            WriteEntry(journalDir, "2026-04-13", "NG3", "intent_other", CreateOpenEntry("intent_other", "MNG", 1));

            var closed = journal.ReconcileInterruptedSessionCloseBrokerFlat(
                "MNG",
                "NG",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intent_ng1", "intent_ng2" },
                utcNow,
                "LateSessionCloseFlattenBrokerFlat");

            if (closed != 2)
                return $"expected 2 interrupted journal rows closed, got {closed}";

            var ng1 = journal.GetEntry("intent_ng1", "2026-04-13", "NG1");
            var ng2 = journal.GetEntry("intent_ng2", "2026-04-13", "NG2");
            var other = journal.GetEntry("intent_other", "2026-04-13", "NG3");

            if (ng1 == null || !ng1.TradeCompleted || ng1.CompletionReason != CompletionReasons.RECONCILIATION_BROKER_FLAT)
                return "expected NG1 interrupted row to reconcile complete under broker-flat proof";
            if (ng2 == null || !ng2.TradeCompleted || ng2.CompletionReason != CompletionReasons.RECONCILIATION_BROKER_FLAT)
                return "expected NG2 interrupted row to reconcile complete under broker-flat proof";
            if (other == null || other.TradeCompleted)
                return "expected unrelated MNG row to remain open";
            if (ExecutionJournal.GetEntryRemainingOpenQuantity(other) != 1)
                return "expected unrelated MNG row remaining qty to stay unchanged";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static ExecutionJournalEntry CreateOpenEntry(string intentId, string instrument, int qty) => new()
    {
        IntentId = intentId,
        Instrument = instrument,
        EntrySubmitted = true,
        EntryFilled = true,
        EntryFilledQuantityTotal = qty,
        ExitFilledQuantityTotal = 0,
        TradeCompleted = false
    };

    private static void WriteEntry(string journalDir, string tradingDate, string stream, string intentId, ExecutionJournalEntry entry)
    {
        Directory.CreateDirectory(journalDir);
        var path = Path.Combine(journalDir, $"{tradingDate}_{stream}_{intentId}.json");
        File.WriteAllText(path, JsonUtil.Serialize(entry));
    }
}
