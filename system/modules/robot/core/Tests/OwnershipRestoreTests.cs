using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class OwnershipRestoreTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var e = Case_EventJournalEntryAndExitRowsAccumulate();
        if (e != null) return (false, e);

        e = Case_MultipleExitRowsAccumulateWithoutNegativeRemaining();
        if (e != null) return (false, e);

        e = Case_AggregateFallbackRowStillRestoresOpenQuantity();
        if (e != null) return (false, e);

        return (true, null);
    }

    private static string? Case_EventJournalEntryAndExitRowsAccumulate()
    {
        var root = Path.Combine(Path.GetTempPath(), "ownership_restore_event_rows_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var emitted = new List<OwnershipEvent>();
            var ledger = new InstrumentOwnershipLedger(new RobotLogger(root), onClassAEvent: e => emitted.Add(e));
            var utc = DateTimeOffset.Parse("2026-05-01T22:34:05Z");

            var rows = new[]
            {
                new JournalRestoreRow
                {
                    IntentId = "intent-mes-test",
                    Stream = "TEST_INJECT",
                    Direction = "Long",
                    EntryFilledQty = 1,
                    ExitFilledQty = 0,
                    ExecutionSequence = 1
                },
                new JournalRestoreRow
                {
                    IntentId = "intent-mes-test",
                    Stream = "TEST_INJECT",
                    Direction = "Long",
                    EntryFilledQty = 0,
                    ExitFilledQty = 1,
                    ExecutionSequence = 2
                }
            };

            var result = ledger.RestoreFromJournal("Playback101", "MES", rows, utc);
            if (!result.Success || result.Snapshot == null)
                return "event rows: restore should succeed";

            if (emitted.Any(e => e.Kind == OwnershipEventKind.InvariantViolation))
                return "event rows: restore emitted invariant violation";

            var slot = result.Snapshot.Slots.SingleOrDefault(s => s.IntentId == "intent-mes-test");
            if (slot == null)
                return "event rows: expected restored TEST_INJECT slot";
            if (slot.EntryFilledQty != 1 || slot.ExitFilledQty != 1)
                return $"event rows: expected entry=1 exit=1, got entry={slot.EntryFilledQty} exit={slot.ExitFilledQty}";
            if (slot.State != SlotState.Closed || result.Snapshot.ActiveSlotCount != 0 || result.Snapshot.LedgerSignedNetQty != 0)
                return $"event rows: expected closed/flat snapshot, state={slot.State}, active={result.Snapshot.ActiveSlotCount}, net={result.Snapshot.LedgerSignedNetQty}";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string? Case_MultipleExitRowsAccumulateWithoutNegativeRemaining()
    {
        var root = Path.Combine(Path.GetTempPath(), "ownership_restore_multi_exit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var emitted = new List<OwnershipEvent>();
            var ledger = new InstrumentOwnershipLedger(new RobotLogger(root), onClassAEvent: e => emitted.Add(e));
            var utc = DateTimeOffset.Parse("2026-05-01T22:45:00Z");

            var rows = new[]
            {
                new JournalRestoreRow
                {
                    IntentId = "intent-ng2",
                    Stream = "NG2",
                    Direction = "Short",
                    EntryFilledQty = 2,
                    ExitFilledQty = 0,
                    ExecutionSequence = 1
                },
                new JournalRestoreRow
                {
                    IntentId = "intent-ng2",
                    Stream = "NG2",
                    Direction = "Short",
                    EntryFilledQty = 0,
                    ExitFilledQty = 1,
                    ExecutionSequence = 2
                },
                new JournalRestoreRow
                {
                    IntentId = "intent-ng2",
                    Stream = "NG2",
                    Direction = "Short",
                    EntryFilledQty = 0,
                    ExitFilledQty = 1,
                    ExecutionSequence = 3
                }
            };

            var result = ledger.RestoreFromJournal("Playback101", "MNG", rows, utc);
            if (!result.Success || result.Snapshot == null)
                return "multi-exit rows: restore should succeed";
            if (emitted.Any(e => e.Kind == OwnershipEventKind.InvariantViolation))
                return "multi-exit rows: restore emitted invariant violation";

            var slot = result.Snapshot.Slots.SingleOrDefault(s => s.IntentId == "intent-ng2");
            if (slot == null)
                return "multi-exit rows: expected restored NG2 slot";
            if (slot.EntryFilledQty != 2 || slot.ExitFilledQty != 2 || slot.State != SlotState.Closed)
                return $"multi-exit rows: expected entry=2 exit=2 closed, got entry={slot.EntryFilledQty} exit={slot.ExitFilledQty} state={slot.State}";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string? Case_AggregateFallbackRowStillRestoresOpenQuantity()
    {
        var root = Path.Combine(Path.GetTempPath(), "ownership_restore_aggregate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var emitted = new List<OwnershipEvent>();
            var ledger = new InstrumentOwnershipLedger(new RobotLogger(root), onClassAEvent: e => emitted.Add(e));
            var utc = DateTimeOffset.Parse("2026-05-01T22:50:00Z");

            var rows = new[]
            {
                new JournalRestoreRow
                {
                    IntentId = "intent-open",
                    Stream = "MES1",
                    Direction = "Long",
                    EntryFilledQty = 2,
                    ExitFilledQty = 1,
                    ExecutionSequence = 1
                }
            };

            var result = ledger.RestoreFromJournal("Playback101", "MES", rows, utc);
            if (!result.Success || result.Snapshot == null)
                return "aggregate row: restore should succeed";
            if (emitted.Any(e => e.Kind == OwnershipEventKind.InvariantViolation))
                return "aggregate row: restore emitted invariant violation";

            var slot = result.Snapshot.Slots.SingleOrDefault(s => s.IntentId == "intent-open");
            if (slot == null)
                return "aggregate row: expected restored open slot";
            if (slot.EntryFilledQty != 2 || slot.ExitFilledQty != 1 || slot.State != SlotState.Active || slot.Remaining != 1)
                return $"aggregate row: expected entry=2 exit=1 active remaining=1, got entry={slot.EntryFilledQty} exit={slot.ExitFilledQty} state={slot.State} remaining={slot.Remaining}";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
