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
        e = Case_ReconcileLingeringBrokerFlatJournalRowsWithoutLiveOwnership();
        if (e != null) return (false, e);
        e = Case_ReconcileLingeringBrokerFlatJournalRowsDespiteRegistryTrust();
        if (e != null) return (false, e);
        e = Case_SuppressBrokerFlatReleaseWhileFlattenPending();
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

    private static string? Case_ReconcileLingeringBrokerFlatJournalRowsWithoutLiveOwnership()
    {
        var root = Path.Combine(Path.GetTempPath(), "broker_flat_release_journal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(root);
            var utcNow = DateTimeOffset.UtcNow;

            WriteEntry(journalDir, "2026-04-13", "NG1", "intent_lingering", CreateOpenEntry("intent_lingering", "MNG", 2));
            WriteEntry(journalDir, "2026-04-13", "NG2", "intent_live", CreateOpenEntry("intent_live", "MNG", 2));

            var closed = journal.ReconcileBrokerFlatJournalRowsForRelease(
                "MNG",
                "NG",
                brokerPositionQtyAbs: 0,
                brokerWorkingCount: 0,
                robotTaggedIntentIdsOnInstrument: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intent_live" },
                registryMismatchTrustedIntentIds: null,
                utcNow,
                "BrokerFlatReleaseReadiness");

            if (closed != 1)
                return $"expected 1 lingering broker-flat journal row closed, got {closed}";

            var lingering = journal.GetEntry("intent_lingering", "2026-04-13", "NG1");
            var live = journal.GetEntry("intent_live", "2026-04-13", "NG2");

            if (lingering == null || !lingering.TradeCompleted || lingering.CompletionReason != CompletionReasons.RECONCILIATION_BROKER_FLAT)
                return "expected lingering broker-flat row to reconcile complete";
            if (live == null || live.TradeCompleted)
                return "expected tagged live row to remain open";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string? Case_ReconcileLingeringBrokerFlatJournalRowsDespiteRegistryTrust()
    {
        var root = Path.Combine(Path.GetTempPath(), "broker_flat_release_registry_trust_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(root);
            var utcNow = DateTimeOffset.UtcNow;

            WriteEntry(journalDir, "2026-04-13", "NG1", "intent_registry_only", CreateOpenEntry("intent_registry_only", "MNG", 2));
            WriteEntry(journalDir, "2026-04-13", "NG2", "intent_live", CreateOpenEntry("intent_live", "MNG", 2));

            var closed = journal.ReconcileBrokerFlatJournalRowsForRelease(
                "MNG",
                "NG",
                brokerPositionQtyAbs: 0,
                brokerWorkingCount: 0,
                robotTaggedIntentIdsOnInstrument: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intent_live" },
                registryMismatchTrustedIntentIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intent_registry_only" },
                utcNow,
                "BrokerFlatReleaseReadiness");

            if (closed != 1)
                return $"expected registry-only broker-flat journal row to close, got {closed}";

            var registryOnly = journal.GetEntry("intent_registry_only", "2026-04-13", "NG1");
            var live = journal.GetEntry("intent_live", "2026-04-13", "NG2");

            if (registryOnly == null || !registryOnly.TradeCompleted || registryOnly.CompletionReason != CompletionReasons.RECONCILIATION_BROKER_FLAT)
                return "expected registry-only lingering row to reconcile complete";
            if (live == null || live.TradeCompleted)
                return "expected tagged live row to remain open when broker-flat cleanup runs";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string? Case_SuppressBrokerFlatReleaseWhileFlattenPending()
    {
        var root = Path.Combine(Path.GetTempPath(), "broker_flat_release_suppressed_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(root);
            var utcNow = DateTimeOffset.UtcNow;

            WriteEntry(journalDir, "2026-04-13", "NG1", "intent_pending_flatten", CreateOpenEntry("intent_pending_flatten", "MNG", 2));

            var closed = journal.ReconcileBrokerFlatJournalRowsForRelease(
                "MNG",
                "NG",
                brokerPositionQtyAbs: 0,
                brokerWorkingCount: 0,
                robotTaggedIntentIdsOnInstrument: Array.Empty<string>(),
                registryMismatchTrustedIntentIds: null,
                utcNow,
                "BrokerFlatReleaseReadiness",
                suppressWhileFlattenPending: true);

            if (closed != 0)
                return $"expected broker-flat release suppression to close 0 rows, got {closed}";

            var pending = journal.GetEntry("intent_pending_flatten", "2026-04-13", "NG1");
            if (pending == null || pending.TradeCompleted)
                return "expected flatten-pending broker-flat row to remain open";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
