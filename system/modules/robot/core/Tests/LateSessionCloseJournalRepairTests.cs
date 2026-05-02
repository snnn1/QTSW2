using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class LateSessionCloseJournalRepairTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var e = Case_ReconcileInterruptedSiblingRowsWhenBrokerFlat();
        if (e != null) return (false, e);
        e = Case_ReconcileInterruptedSiblingRowsWhenBrokerFlat_MirrorsOwnershipIdempotently();
        if (e != null) return (false, e);
        e = Case_ReconcileLingeringBrokerFlatJournalRowsWithoutLiveOwnership();
        if (e != null) return (false, e);
        e = Case_ReconcileLingeringBrokerFlatJournalRows_MirrorsOwnershipIdempotently();
        if (e != null) return (false, e);
        e = Case_DeferLateSessionCloseConfirmationUntilFlattenAllocationsFinish();
        if (e != null) return (false, e);
        e = Case_ReconcileLingeringBrokerFlatJournalRowsDespiteRegistryTrust();
        if (e != null) return (false, e);
        e = Case_SuppressBrokerFlatReleaseWhileFlattenPending();
        if (e != null) return (false, e);
        e = Case_AllowFinalTaggedResidualBrokerFlatRetirement();
        if (e != null) return (false, e);
        e = Case_ReconciliationRunnerDoesNotCloseJournalsFromNonAuthoritativeSnapshot();
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
            var utcNow = DateTimeOffset.UtcNow;

            WriteEntry(journal, "2026-04-13", "NG1", "intent-ng1", CreateOpenEntry("intent-ng1", "MNG", 2));
            WriteEntry(journal, "2026-04-13", "NG2", "intent-ng2", CreateOpenEntry("intent-ng2", "MNG", 2));
            WriteEntry(journal, "2026-04-13", "NG3", "intent-other", CreateOpenEntry("intent-other", "MNG", 1));

            var closed = journal.ReconcileInterruptedSessionCloseBrokerFlat(
                "MNG",
                "NG",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intent-ng1", "intent-ng2" },
                utcNow,
                "LateSessionCloseFlattenBrokerFlat");

            if (closed != 2)
            {
                var open = journal.GetOpenJournalEntriesByInstrument();
                var diag = journal.GetJournalDiagnostics();
                var buckets = string.Join("; ", open.Select(kvp => $"{kvp.Key}:{kvp.Value.Count} [{string.Join(",", kvp.Value.Select(x => x.IntentId))}]"));
                return $"expected 2 interrupted journal rows closed, got {closed}; journal_dir={diag.JournalDir}; files={diag.FileCount}; open_buckets={buckets}";
            }

            var ng1 = journal.GetEntry("intent-ng1", "2026-04-13", "NG1");
            var ng2 = journal.GetEntry("intent-ng2", "2026-04-13", "NG2");
            var other = journal.GetEntry("intent-other", "2026-04-13", "NG3");

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

    private static string? Case_ReconcileInterruptedSiblingRowsWhenBrokerFlat_MirrorsOwnershipIdempotently()
    {
        var root = Path.Combine(Path.GetTempPath(), "late_flatten_journal_ownership_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var ledger = new InstrumentOwnershipLedger(log);
            var utcNow = DateTimeOffset.UtcNow;
            const string account = "Playback101";
            var callbackCount = 0;

            WriteEntry(journal, "2026-04-13", "NG2", "intent-ng2", CreateOpenEntry("intent-ng2", "MNG", 2));
            var entryWrite = ledger.RecordMappedEntryFill(account, "MNG", "intent-ng2", "NG2", SlotDirection.Short, 2, utcNow.AddMilliseconds(-1), 1);
            if (!entryWrite.Success)
                return "expected ownership mapped entry fill to succeed for interrupted row";

            var closed = journal.ReconcileInterruptedSessionCloseBrokerFlat(
                "MNG",
                "NG",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intent-ng2" },
                utcNow,
                "LateSessionCloseFlattenBrokerFlat",
                (tradingDate, stream, intentId, openQty) =>
                {
                    var snap = ledger.GetOwnershipSnapshot(account, "MNG");
                    var remaining = snap?.Slots
                        .Where(s => string.Equals(s.IntentId, intentId, StringComparison.OrdinalIgnoreCase) && s.Remaining > 0)
                        .Sum(s => s.Remaining) ?? 0;
                    if (remaining <= 0)
                        return;

                    callbackCount++;
                    var qtyToClose = Math.Min(openQty, remaining);
                    var closeWrite = ledger.RecordMappedExitFill(account, "MNG", intentId, qtyToClose, utcNow, 0);
                    if (!closeWrite.Success)
                        throw new InvalidOperationException($"ownership close failed: {closeWrite.ErrorReason}");
                });

            if (closed != 1)
                return $"expected 1 interrupted journal row closed, got {closed}";
            if (callbackCount != 1)
                return $"expected ownership mirror callback once, got {callbackCount}";

            var afterFirst = ledger.GetOwnershipSnapshot(account, "MNG");
            if (afterFirst.ActiveSlotCount != 0)
                return $"expected ownership to be flat after mirror close, active slots={afterFirst.ActiveSlotCount}";

            var closedAgain = journal.ReconcileInterruptedSessionCloseBrokerFlat(
                "MNG",
                "NG",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intent-ng2" },
                utcNow.AddMilliseconds(1),
                "LateSessionCloseFlattenBrokerFlat",
                (_, _, _, _) => callbackCount++);

            if (closedAgain != 0)
                return $"expected repeat late broker-flat reconciliation to close 0 rows, got {closedAgain}";
            if (callbackCount != 1)
                return $"expected repeat reconciliation not to invoke callback again, got {callbackCount}";

            var afterSecond = ledger.GetOwnershipSnapshot(account, "MNG");
            if (afterSecond.ActiveSlotCount != 0)
                return $"expected ownership to remain flat after repeat reconcile, active slots={afterSecond.ActiveSlotCount}";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string? Case_DeferLateSessionCloseConfirmationUntilFlattenAllocationsFinish()
    {
        var oldInterleaving = ExerciseSessionFlattenAllocationOrdering(notifyAfterFirstAllocation: true);
        if (!oldInterleaving.SawExitOverflow)
            return "expected old per-allocation late-session confirmation ordering to reproduce ownership exit overflow";

        var deferred = ExerciseSessionFlattenAllocationOrdering(notifyAfterFirstAllocation: false);
        if (deferred.SawExitOverflow)
            return "expected deferred late-session confirmation ordering to avoid ownership exit overflow";
        if (deferred.ReconciledInterruptedJournals != 0)
            return $"expected deferred confirmation to reconcile 0 already-closed rows, got {deferred.ReconciledInterruptedJournals}";
        if (deferred.CallbackCount != 0)
            return $"expected deferred confirmation not to mirror ownership after normal allocations, callbackCount={deferred.CallbackCount}";
        if (deferred.ActiveSlotCount != 0)
            return $"expected ownership to be flat after deferred allocation batch, active slots={deferred.ActiveSlotCount}";

        return null;
    }

    private static (bool SawExitOverflow, int ReconciledInterruptedJournals, int CallbackCount, int ActiveSlotCount)
        ExerciseSessionFlattenAllocationOrdering(bool notifyAfterFirstAllocation)
    {
        var root = Path.Combine(Path.GetTempPath(), "late_flatten_ordering_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sawExitOverflow = false;
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var ledger = new InstrumentOwnershipLedger(log, onClassAEvent: e =>
            {
                if (e.Kind == OwnershipEventKind.ExitOverflow)
                    sawExitOverflow = true;
            });
            var utcNow = DateTimeOffset.Parse("2026-04-26T22:41:21.7518511+00:00");
            const string account = "Playback101";
            const string tradingDate = "2026-04-13";
            const string instrument = "MNG";
            const string ng1 = "intent-ng1";
            const string ng2 = "intent-ng2";

            WriteEntry(journal, tradingDate, "NG1", ng1, CreateOpenEntry(ng1, instrument, 2));
            WriteEntry(journal, tradingDate, "NG2", ng2, CreateOpenEntry(ng2, instrument, 2));
            journal.RecordExitFill(ng1, tradingDate, "NG1", 2.621m, 1, "FLATTEN", utcNow.AddMilliseconds(-100));

            ledger.RecordMappedEntryFill(account, instrument, ng1, "NG1", SlotDirection.Short, 2, utcNow.AddMilliseconds(-200), 1);
            ledger.RecordMappedEntryFill(account, instrument, ng2, "NG2", SlotDirection.Short, 2, utcNow.AddMilliseconds(-199), 2);
            ledger.RecordMappedExitFill(account, instrument, ng1, 1, utcNow.AddMilliseconds(-100), 3);

            var callbackCount = 0;
            int ReconcileInterrupted()
            {
                return journal.ReconcileInterruptedSessionCloseBrokerFlat(
                    instrument,
                    "NG",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ng1, ng2 },
                    utcNow,
                    "LateSessionCloseFlattenBrokerFlat",
                    (td, stream, id, openQty) =>
                    {
                        callbackCount++;
                        var snap = ledger.GetOwnershipSnapshot(account, instrument);
                        var remaining = snap?.Slots
                            .Where(s => string.Equals(s.IntentId, id, StringComparison.OrdinalIgnoreCase) && s.Remaining > 0)
                            .Sum(s => s.Remaining) ?? 0;
                        if (remaining <= 0)
                            return;

                        var qtyToClose = Math.Min(openQty, remaining);
                        ledger.RecordMappedExitFill(account, instrument, id, qtyToClose, utcNow, 0);
                    });
            }

            var reconciled = 0;

            journal.RecordExitFill(ng1, tradingDate, "NG1", 2.622m, 1, "FLATTEN", utcNow);
            ledger.RecordMappedExitFill(account, instrument, ng1, 1, utcNow, 4);

            if (notifyAfterFirstAllocation)
                reconciled = ReconcileInterrupted();

            journal.RecordExitFill(ng2, tradingDate, "NG2", 2.622m, 2, "FLATTEN", utcNow);
            ledger.RecordMappedExitFill(account, instrument, ng2, 2, utcNow, 4);

            if (!notifyAfterFirstAllocation)
                reconciled = ReconcileInterrupted();

            var activeSlotCount = ledger.GetOwnershipSnapshot(account, instrument).ActiveSlotCount;
            return (sawExitOverflow, reconciled, callbackCount, activeSlotCount);
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

    private static void WriteEntry(ExecutionJournal journal, string tradingDate, string stream, string intentId, ExecutionJournalEntry entry)
    {
        var instrument = string.IsNullOrWhiteSpace(entry.Instrument) ? "UNKNOWN" : entry.Instrument;
        var qty = entry.EntryFilledQuantityTotal > 0 ? entry.EntryFilledQuantityTotal : Math.Max(1, entry.FillQuantity ?? 1);
        var direction = string.IsNullOrWhiteSpace(entry.Direction) ? "Short" : entry.Direction;
        journal.RecordSubmission(intentId, tradingDate, stream, instrument, "ENTRY", intentId + "-entry", DateTimeOffset.UtcNow, direction: direction);
        journal.RecordEntryFill(intentId, tradingDate, stream, 1m, qty, DateTimeOffset.UtcNow, 1m, direction, instrument, instrument);
    }

    private static string? Case_ReconciliationRunnerDoesNotCloseJournalsFromNonAuthoritativeSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "broker_flat_non_authoritative_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var utcNow = DateTimeOffset.UtcNow;
            const string tradingDate = "2026-04-29";
            const string stream = "NG2";
            const string intentId = "intent-platform-disabled";

            WriteEntry(journal, tradingDate, stream, intentId, CreateOpenEntry(intentId, "MNG", 2));

            var adapter = new SnapshotExecutionAdapter(new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utcNow,
                IsAuthoritative = false,
                NonAuthoritativeReason = "PLAYBACK_STALL_NT_CALL_BLOCKED"
            });

            var runner = new ReconciliationRunner(adapter, journal, log);
            runner.ForceRunNow(utcNow);

            var entry = journal.GetEntry(intentId, tradingDate, stream);
            if (entry == null)
                return "expected platform-disabled journal row to remain readable";
            if (entry.TradeCompleted)
                return "non-authoritative empty broker snapshot must not close open journal rows as broker-flat";
            if (adapter.SnapshotCallCount != 1)
                return $"expected one account snapshot read, got {adapter.SnapshotCallCount}";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string? Case_ReconcileLingeringBrokerFlatJournalRowsWithoutLiveOwnership()
    {
        var root = Path.Combine(Path.GetTempPath(), "broker_flat_release_journal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var utcNow = DateTimeOffset.UtcNow;

            WriteEntry(journal, "2026-04-13", "NG1", "intent-lingering", CreateOpenEntry("intent-lingering", "MNG", 2));
            WriteEntry(journal, "2026-04-13", "NG2", "intent-live", CreateOpenEntry("intent-live", "MNG", 2));

            var closed = journal.ReconcileBrokerFlatJournalRowsForRelease(
                "MNG",
                "NG",
                brokerPositionQtyAbs: 0,
                brokerWorkingCount: 0,
                robotTaggedIntentIdsOnInstrument: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intent-live" },
                registryMismatchTrustedIntentIds: null,
                utcNow,
                "BrokerFlatReleaseReadiness");

            if (closed != 1)
                return $"expected 1 lingering broker-flat journal row closed, got {closed}";

            var lingering = journal.GetEntry("intent-lingering", "2026-04-13", "NG1");
            var live = journal.GetEntry("intent-live", "2026-04-13", "NG2");

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

    private static string? Case_ReconcileLingeringBrokerFlatJournalRows_MirrorsOwnershipIdempotently()
    {
        var root = Path.Combine(Path.GetTempPath(), "broker_flat_release_ownership_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var ledger = new InstrumentOwnershipLedger(log);
            var utcNow = DateTimeOffset.UtcNow;
            const string account = "Playback101";
            const string tradingDate = "2026-04-13";
            const string stream = "NG2";
            const string intentId = "intent-lingering";
            const string instrument = "MNG";
            var callbackCount = 0;

            WriteEntry(journal, tradingDate, stream, intentId, CreateOpenEntry(intentId, instrument, 2));
            var entryWrite = ledger.RecordMappedEntryFill(account, instrument, intentId, stream, SlotDirection.Short, 2, utcNow.AddMilliseconds(-1), 1);
            if (!entryWrite.Success)
                return "expected ownership mapped entry fill to succeed for lingering broker-flat row";

            var closed = journal.ReconcileBrokerFlatJournalRowsForRelease(
                instrument,
                "NG",
                brokerPositionQtyAbs: 0,
                brokerWorkingCount: 0,
                robotTaggedIntentIdsOnInstrument: Array.Empty<string>(),
                registryMismatchTrustedIntentIds: null,
                utcNow,
                "BrokerFlatReleaseReadiness",
                onReconciled: (td, st, id, openQty) =>
                {
                    var snap = ledger.GetOwnershipSnapshot(account, instrument);
                    var remaining = snap?.Slots
                        .Where(s => string.Equals(s.IntentId, id, StringComparison.OrdinalIgnoreCase) && s.Remaining > 0)
                        .Sum(s => s.Remaining) ?? 0;
                    if (remaining <= 0)
                        return;

                    callbackCount++;
                    var qtyToClose = Math.Min(openQty, remaining);
                    var closeWrite = ledger.RecordMappedExitFill(account, instrument, id, qtyToClose, utcNow, 0);
                    if (!closeWrite.Success)
                        throw new InvalidOperationException($"ownership close failed: {closeWrite.ErrorReason}");
                });

            if (closed != 1)
                return $"expected 1 lingering broker-flat journal row closed, got {closed}";
            if (callbackCount != 1)
                return $"expected ownership mirror callback once, got {callbackCount}";

            var afterFirst = ledger.GetOwnershipSnapshot(account, instrument);
            if (afterFirst.ActiveSlotCount != 0)
                return $"expected ownership to be flat after release mirror close, active slots={afterFirst.ActiveSlotCount}";

            var closedAgain = journal.ReconcileBrokerFlatJournalRowsForRelease(
                instrument,
                "NG",
                brokerPositionQtyAbs: 0,
                brokerWorkingCount: 0,
                robotTaggedIntentIdsOnInstrument: Array.Empty<string>(),
                registryMismatchTrustedIntentIds: null,
                utcNow.AddMilliseconds(1),
                "BrokerFlatReleaseReadiness",
                onReconciled: (_, _, _, _) => callbackCount++);

            if (closedAgain != 0)
                return $"expected repeat release reconciliation to close 0 rows, got {closedAgain}";
            if (callbackCount != 1)
                return $"expected repeat release reconciliation not to invoke callback again, got {callbackCount}";

            var afterSecond = ledger.GetOwnershipSnapshot(account, instrument);
            if (afterSecond.ActiveSlotCount != 0)
                return $"expected ownership to remain flat after repeat release reconcile, active slots={afterSecond.ActiveSlotCount}";

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
            var utcNow = DateTimeOffset.UtcNow;

            WriteEntry(journal, "2026-04-13", "NG1", "intent-registry-only", CreateOpenEntry("intent-registry-only", "MNG", 2));
            WriteEntry(journal, "2026-04-13", "NG2", "intent-live", CreateOpenEntry("intent-live", "MNG", 2));

            var closed = journal.ReconcileBrokerFlatJournalRowsForRelease(
                "MNG",
                "NG",
                brokerPositionQtyAbs: 0,
                brokerWorkingCount: 0,
                robotTaggedIntentIdsOnInstrument: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intent-live" },
                registryMismatchTrustedIntentIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intent-registry-only" },
                utcNow,
                "BrokerFlatReleaseReadiness");

            if (closed != 1)
                return $"expected registry-only broker-flat journal row to close, got {closed}";

            var registryOnly = journal.GetEntry("intent-registry-only", "2026-04-13", "NG1");
            var live = journal.GetEntry("intent-live", "2026-04-13", "NG2");

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
            var utcNow = DateTimeOffset.UtcNow;

            WriteEntry(journal, "2026-04-13", "NG1", "intent-pending-flatten", CreateOpenEntry("intent-pending-flatten", "MNG", 2));

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

            var pending = journal.GetEntry("intent-pending-flatten", "2026-04-13", "NG1");
            if (pending == null || pending.TradeCompleted)
                return "expected flatten-pending broker-flat row to remain open";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string? Case_AllowFinalTaggedResidualBrokerFlatRetirement()
    {
        var root = Path.Combine(Path.GetTempPath(), "broker_flat_release_final_pulse_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var utcNow = DateTimeOffset.UtcNow;

            WriteEntry(journal, "2026-04-13", "NG1", "intent-tagged-residual", CreateOpenEntry("intent-tagged-residual", "MNG", 2));

            var normalClosed = journal.ReconcileBrokerFlatJournalRowsForRelease(
                "MNG",
                "NG",
                brokerPositionQtyAbs: 0,
                brokerWorkingCount: 0,
                robotTaggedIntentIdsOnInstrument: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intent-tagged-residual" },
                registryMismatchTrustedIntentIds: null,
                utcNow,
                "BrokerFlatReleaseReadiness");

            if (normalClosed != 0)
                return $"expected tagged residual row to survive normal broker-flat pass, got {normalClosed}";

            var finalClosed = journal.ReconcileBrokerFlatJournalRowsForRelease(
                "MNG",
                "NG",
                brokerPositionQtyAbs: 0,
                brokerWorkingCount: 0,
                robotTaggedIntentIdsOnInstrument: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "intent-tagged-residual" },
                registryMismatchTrustedIntentIds: null,
                utcNow,
                "ResidualBrokerFlatCleanupPulse",
                suppressWhileFlattenPending: false,
                allowTaggedResidualRetirement: true);

            if (finalClosed != 1)
                return $"expected final residual cleanup pulse to close tagged broker-flat row, got {finalClosed}";

            var entry = journal.GetEntry("intent-tagged-residual", "2026-04-13", "NG1");
            if (entry == null || !entry.TradeCompleted || entry.CompletionReason != CompletionReasons.RECONCILIATION_BROKER_FLAT)
                return "expected tagged broker-flat residual row to reconcile complete on final cleanup pulse";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class SnapshotExecutionAdapter : IExecutionAdapter
    {
        private readonly AccountSnapshot _snapshot;

        public SnapshotExecutionAdapter(AccountSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public int SnapshotCallCount { get; private set; }

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

        public FlattenResult FlattenEmergency(string instrument, DateTimeOffset utcNow)
            => FlattenResult.SuccessResult(utcNow);

        public bool TryEnqueueEmergencyFlattenProtective(string instrument, DateTimeOffset utcNow) => true;

        public AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow)
        {
            SnapshotCallCount++;
            return _snapshot;
        }

        public (decimal? Bid, decimal? Ask) GetCurrentMarketPrice(string instrument, DateTimeOffset utcNow) => (null, null);

        public void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow) { }

        public void CancelOrders(IEnumerable<string> orderIds, DateTimeOffset utcNow) { }

        public void EnqueueExecutionCommand(ExecutionCommandBase command) { }

        public bool TryRepairTaggedBrokerWithoutJournal(string instrument, int accountQtyAbs, int journalOpenQtySum, DateTimeOffset utcNow, out string resultCode, out string? detail)
        {
            resultCode = "NOT_NEEDED";
            detail = null;
            return false;
        }

        public FlattenResult? RequestSessionCloseFlattenImmediate(string intentId, string instrument, DateTimeOffset utcNow)
            => FlattenResult.SuccessResult(utcNow);

        public bool IsExecutionContextReady => true;
    }
}
