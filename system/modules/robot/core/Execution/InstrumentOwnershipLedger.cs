using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Canonical ownership state for all (account, executionInstrument) pairs.
/// Single write path for ownership mutations; single read path via <see cref="GetOwnershipSnapshot"/>.
/// In-memory materialized view backed by <see cref="ExecutionJournal"/> for durability.
/// </summary>
public sealed class InstrumentOwnershipLedger
{
    private readonly ConcurrentDictionary<(string Account, string ExecutionInstrumentKey), InstrumentOwnershipState> _states = new();
    private readonly RobotLogger _log;
    private readonly Action<OwnershipEvent>? _onClassAEvent;
    private readonly Action<OwnershipEvent>? _onClassBEvent;
    private OwnershipEventJournal? _eventJournal;
    private string? _currentTradingDate;

    public InstrumentOwnershipLedger(
        RobotLogger log,
        Action<OwnershipEvent>? onClassAEvent = null,
        Action<OwnershipEvent>? onClassBEvent = null)
    {
        _log = log;
        _onClassAEvent = onClassAEvent;
        _onClassBEvent = onClassBEvent;
    }

    /// <summary>Wire the durable ownership event journal for monotonic-sequenced persistence (Phase 6).</summary>
    public void SetEventJournal(OwnershipEventJournal? journal, string? tradingDate)
    {
        _eventJournal = journal;
        _currentTradingDate = tradingDate;
    }

    private InstrumentOwnershipState GetOrCreate(string account, string executionInstrumentKey, bool multiIntentAllowed = true)
    {
        var key = (account.Trim(), executionInstrumentKey.Trim());
        return _states.GetOrAdd(key, _ => new InstrumentOwnershipState(account.Trim(), executionInstrumentKey.Trim(), multiIntentAllowed));
    }

    /// <summary>
    /// Record a mapped entry fill. The ownership ledger tracks factual slots and allows multiple
    /// independent streams to own exposure on the same execution instrument.
    /// </summary>
    public LedgerWriteResult RecordMappedEntryFill(
        string account, string executionInstrument, string intentId, string stream,
        SlotDirection direction, int qtyDelta, DateTimeOffset utcNow, long executionSequence)
    {
        if (qtyDelta <= 0) return LedgerWriteResult.Fail("qtyDelta must be positive", OwnershipEventKind.MappedEntryFill);

        var state = GetOrCreate(account, executionInstrument);
        lock (state.Lock)
        {
            state.MultiIntentAllowed = true;

            var targetSlot = FindOrCreateSlot(state, intentId, stream, direction, executionSequence, utcNow);
            targetSlot.EntryFilledQty += qtyDelta;
            targetSlot.LastUpdateUtc = utcNow;
            FinalizeWrite(state, utcNow);
            var finalSnapshot = BuildSnapshotUnderLock(state, utcNow);
            EmitEvent(BuildEvent(OwnershipEventKind.MappedEntryFill, OwnershipEventClass.ClassA,
                state, intentId, qtyDelta, direction, finalSnapshot, utcNow));
            RunInvariantChecks(state, finalSnapshot);
            return LedgerWriteResult.Ok(finalSnapshot);
        }
    }

    public LedgerWriteResult RecordMappedExitFill(
        string account, string executionInstrument, string? intentId, int qtyDelta,
        DateTimeOffset utcNow, long executionSequence)
    {
        if (qtyDelta <= 0) return LedgerWriteResult.Fail("qtyDelta must be positive", OwnershipEventKind.MappedExitFill);

        var state = GetOrCreate(account, executionInstrument);
        lock (state.Lock)
        {
            var applied = ApplyExitToSlots(state, intentId, qtyDelta, utcNow);
            if (!applied.Success)
            {
                FinalizeWrite(state, utcNow);
                var errorSnap = BuildSnapshotUnderLock(state, utcNow);
                EmitEvent(BuildEvent(OwnershipEventKind.ExitOverflow, OwnershipEventClass.ClassA,
                    state, intentId ?? "", qtyDelta, SlotDirection.Long, errorSnap, utcNow, applied.ErrorReason));
                return LedgerWriteResult.Fail(applied.ErrorReason!, OwnershipEventKind.ExitOverflow);
            }

            FinalizeWrite(state, utcNow);
            var snapshot = BuildSnapshotUnderLock(state, utcNow);
            EmitEvent(BuildEvent(OwnershipEventKind.MappedExitFill, OwnershipEventClass.ClassA,
                state, intentId ?? "", qtyDelta, SlotDirection.Long, snapshot, utcNow));
            RunInvariantChecks(state, snapshot);
            return LedgerWriteResult.Ok(snapshot);
        }
    }

    public LedgerWriteResult RecordOrphanFill(
        string account, string executionInstrument, string? brokerOrderId,
        SlotDirection direction, int qtyDelta, DateTimeOffset utcNow, OrphanReason reason)
    {
        if (qtyDelta <= 0) return LedgerWriteResult.Fail("qtyDelta must be positive", OwnershipEventKind.OrphanFill);

        var state = GetOrCreate(account, executionInstrument);
        lock (state.Lock)
        {
            var orphanId = $"ORPHAN_{brokerOrderId ?? Guid.NewGuid().ToString("N").Substring(0, 8)}_{utcNow.Ticks}";
            var slot = new IntentSlot
            {
                IntentId = orphanId,
                StreamId = "",
                Direction = direction,
                EntryFilledQty = qtyDelta,
                ExitFilledQty = 0,
                State = SlotState.Orphan,
                FirstEntryExecutionSequence = 0,
                LastUpdateUtc = utcNow,
                CreatedUtc = utcNow,
                OrphanReason = reason,
                BrokerOrderId = brokerOrderId
            };
            state.Slots.Add(slot);

            FinalizeWrite(state, utcNow);
            var snapshot = BuildSnapshotUnderLock(state, utcNow);
            EmitEvent(BuildEvent(OwnershipEventKind.OrphanFill, OwnershipEventClass.ClassA,
                state, orphanId, qtyDelta, direction, snapshot, utcNow,
                $"reason={reason}, brokerOrderId={brokerOrderId}"));
            RunInvariantChecks(state, snapshot);
            return LedgerWriteResult.OkOrphan(snapshot, orphanId);
        }
    }

    public LedgerWriteResult TransferOwnership(
        string account, string executionInstrument, string fromIntentId, string toIntentId,
        int qty, DateTimeOffset utcNow)
    {
        if (qty <= 0) return LedgerWriteResult.Fail("qty must be positive", OwnershipEventKind.TransferRejected);

        var state = GetOrCreate(account, executionInstrument);
        lock (state.Lock)
        {
            var fromSlot = state.Slots.FirstOrDefault(s => s.IntentId == fromIntentId && s.Remaining > 0);
            if (fromSlot == null || fromSlot.Remaining < qty)
            {
                var snap = BuildSnapshotUnderLock(state, utcNow);
                EmitEvent(BuildEvent(OwnershipEventKind.TransferRejected, OwnershipEventClass.ClassA,
                    state, fromIntentId, qty, fromSlot?.Direction ?? SlotDirection.Long, snap, utcNow,
                    $"from={fromIntentId} insufficient remaining ({fromSlot?.Remaining ?? 0} < {qty})"));
                return LedgerWriteResult.Fail($"Insufficient remaining on {fromIntentId}", OwnershipEventKind.TransferRejected);
            }

            var totalBefore = state.Slots.Sum(s => s.Remaining);

            fromSlot.ExitFilledQty += qty;
            fromSlot.LastUpdateUtc = utcNow;
            if (fromSlot.Remaining == 0)
                fromSlot.State = SlotState.Closed;

            var toSlot = state.Slots.FirstOrDefault(s => s.IntentId == toIntentId && s.State != SlotState.Closed);
            if (toSlot != null)
            {
                toSlot.EntryFilledQty += qty;
                toSlot.LastUpdateUtc = utcNow;
                if (toSlot.State == SlotState.Orphan)
                    toSlot.State = SlotState.Active;
            }
            else
            {
                state.Slots.Add(new IntentSlot
                {
                    IntentId = toIntentId,
                    StreamId = fromSlot.StreamId,
                    Direction = fromSlot.Direction,
                    EntryFilledQty = qty,
                    ExitFilledQty = 0,
                    State = SlotState.Active,
                    FirstEntryExecutionSequence = fromSlot.FirstEntryExecutionSequence,
                    LastUpdateUtc = utcNow,
                    CreatedUtc = utcNow
                });
            }

            var totalAfter = state.Slots.Sum(s => s.Remaining);
            if (totalBefore != totalAfter)
            {
                var errorSnap = BuildSnapshotUnderLock(state, utcNow);
                EmitEvent(BuildEvent(OwnershipEventKind.InvariantViolation, OwnershipEventClass.ClassA,
                    state, fromIntentId, qty, fromSlot.Direction, errorSnap, utcNow,
                    $"Transfer conservation violated: {totalBefore} != {totalAfter}"));
                return LedgerWriteResult.Fail("Transfer conservation violated", OwnershipEventKind.InvariantViolation);
            }

            FinalizeWrite(state, utcNow);
            var snapshot = BuildSnapshotUnderLock(state, utcNow);
            EmitEvent(new OwnershipEvent
            {
                Kind = OwnershipEventKind.OwnershipTransfer,
                EventClass = OwnershipEventClass.ClassA,
                Account = state.Account,
                ExecutionInstrumentKey = state.ExecutionInstrumentKey,
                OwnershipVersion = state.OwnershipVersion,
                IntentId = fromIntentId,
                FromIntentId = fromIntentId,
                ToIntentId = toIntentId,
                QtyDelta = qty,
                Direction = fromSlot.Direction,
                Snapshot = snapshot,
                Utc = utcNow,
                Detail = $"from={fromIntentId} to={toIntentId} qty={qty}"
            });
            RunInvariantChecks(state, snapshot);
            return LedgerWriteResult.Ok(snapshot);
        }
    }

    /// <summary>
    /// Atomic rebuild from journal rows. Replaces the entire slot set for the instrument.
    /// ownershipVersion starts fresh (session-scoped counter, not durable).
    /// </summary>
    public LedgerWriteResult RestoreFromJournal(
        string account, string executionInstrument,
        IReadOnlyList<JournalRestoreRow> journalRows,
        DateTimeOffset utcNow,
        bool multiIntentAllowed = true)
    {
        var state = GetOrCreate(account, executionInstrument, multiIntentAllowed);
        lock (state.Lock)
        {
            state.Slots.Clear();
            state.MultiIntentAllowed = multiIntentAllowed;

            var ordered = journalRows.OrderBy(r => r.ExecutionSequence).ThenBy(r => r.IntentId, StringComparer.Ordinal);
            foreach (var row in ordered)
            {
                if (row.RowType == JournalRowType.Transfer)
                {
                    ApplyTransferDuringRestore(state, row, utcNow);
                    continue;
                }

                var existing = state.Slots.FirstOrDefault(s => s.IntentId == row.IntentId);
                if (existing != null)
                {
                    existing.EntryFilledQty = row.EntryFilledQty;
                    existing.ExitFilledQty = row.ExitFilledQty;
                    existing.LastUpdateUtc = utcNow;
                    if (existing.Remaining == 0)
                        existing.State = SlotState.Closed;
                }
                else
                {
                    var slotState = row.IsOrphan ? SlotState.Orphan : SlotState.Active;
                    if (row.EntryFilledQty - row.ExitFilledQty == 0)
                        slotState = SlotState.Closed;

                    state.Slots.Add(new IntentSlot
                    {
                        IntentId = row.IntentId,
                        StreamId = row.Stream,
                        Direction = ParseDirection(row.Direction),
                        EntryFilledQty = row.EntryFilledQty,
                        ExitFilledQty = row.ExitFilledQty,
                        State = slotState,
                        FirstEntryExecutionSequence = row.ExecutionSequence,
                        LastUpdateUtc = utcNow,
                        CreatedUtc = utcNow,
                        OrphanReason = row.IsOrphan ? OrphanReason.UnknownOrder : OrphanReason.None
                    });
                }
            }

            FinalizeWrite(state, utcNow);
            var snapshot = BuildSnapshotUnderLock(state, utcNow);
            EmitEvent(BuildEvent(OwnershipEventKind.RestoreBaseline, OwnershipEventClass.ClassA,
                state, "", 0, SlotDirection.Long, snapshot, utcNow,
                $"Restored {state.Slots.Count} slots from {journalRows.Count} journal rows"));
            RunInvariantChecks(state, snapshot);
            return LedgerWriteResult.Ok(snapshot);
        }
    }

    /// <summary>
    /// Update the orphan slot state when a flatten result is known.
    /// </summary>
    public void UpdateOrphanFlattenResult(string account, string executionInstrument, string orphanSlotIntentId, bool flattenSucceeded, DateTimeOffset utcNow)
    {
        var state = GetOrCreate(account, executionInstrument);
        lock (state.Lock)
        {
            var slot = state.Slots.FirstOrDefault(s => s.IntentId == orphanSlotIntentId && s.State == SlotState.Orphan);
            if (slot == null) return;

            if (flattenSucceeded)
            {
                slot.ExitFilledQty = slot.EntryFilledQty;
                slot.State = SlotState.Closed;
                slot.LastUpdateUtc = utcNow;
            }

            FinalizeWrite(state, utcNow);
        }
    }

    public InstrumentOwnershipSnapshot GetOwnershipSnapshot(string account, string executionInstrument)
    {
        var key = (account.Trim(), executionInstrument.Trim());
        if (!_states.TryGetValue(key, out var state))
        {
            return new InstrumentOwnershipSnapshot
            {
                Account = account.Trim(),
                ExecutionInstrumentKey = executionInstrument.Trim(),
                OwnershipVersion = 0,
                Slots = Array.Empty<IntentSlot>(),
                SnapshotUtc = DateTimeOffset.UtcNow
            };
        }

        lock (state.Lock)
        {
            return BuildSnapshotUnderLock(state, DateTimeOffset.UtcNow);
        }
    }

    public IReadOnlyList<InstrumentOwnershipSnapshot> SnapshotAll(string account)
    {
        var results = new List<InstrumentOwnershipSnapshot>();
        foreach (var kvp in _states)
        {
            if (!string.Equals(kvp.Key.Account, account.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            var state = kvp.Value;
            lock (state.Lock)
            {
                results.Add(BuildSnapshotUnderLock(state, DateTimeOffset.UtcNow));
            }
        }
        return results;
    }

    /// <summary>
    /// Comparison assertion: returns true if ledger state matches a freshly restored state from journal.
    /// Used during dual-run to verify determinism (Go/No-Go checklist item A).
    /// </summary>
    public bool AssertDeterministicRestore(
        string account, string executionInstrument,
        IReadOnlyList<JournalRestoreRow> journalRows)
    {
        var key = (account.Trim(), executionInstrument.Trim());
        if (!_states.TryGetValue(key, out var liveState))
            return journalRows.Count == 0;

        var tempLedger = new InstrumentOwnershipLedger(_log);
        var restoreResult = tempLedger.RestoreFromJournal(account, executionInstrument, journalRows, DateTimeOffset.UtcNow);
        if (!restoreResult.Success || restoreResult.Snapshot == null) return false;

        lock (liveState.Lock)
        {
            var liveSnapshot = BuildSnapshotUnderLock(liveState, DateTimeOffset.UtcNow);
            return SnapshotsEquivalent(liveSnapshot, restoreResult.Snapshot);
        }
    }

    // --- Internal helpers ---

    private IntentSlot FindOrCreateSlot(InstrumentOwnershipState state, string intentId, string stream,
        SlotDirection direction, long executionSequence, DateTimeOffset utcNow)
    {
        var existing = state.Slots.FirstOrDefault(s => s.IntentId == intentId && s.State != SlotState.Closed);
        if (existing != null) return existing;

        var slot = new IntentSlot
        {
            IntentId = intentId,
            StreamId = stream,
            Direction = direction,
            EntryFilledQty = 0,
            ExitFilledQty = 0,
            State = SlotState.Active,
            FirstEntryExecutionSequence = executionSequence,
            LastUpdateUtc = utcNow,
            CreatedUtc = utcNow
        };
        state.Slots.Add(slot);
        return slot;
    }

    private (bool Success, string? ErrorReason) ApplyExitToSlots(
        InstrumentOwnershipState state, string? intentId, int qtyDelta, DateTimeOffset utcNow)
    {
        var remaining = qtyDelta;

        if (!string.IsNullOrEmpty(intentId))
        {
            var targeted = state.Slots.FirstOrDefault(s => s.IntentId == intentId && s.State != SlotState.Closed && s.Remaining > 0);
            if (targeted != null)
            {
                var take = Math.Min(remaining, targeted.Remaining);
                targeted.ExitFilledQty += take;
                targeted.LastUpdateUtc = utcNow;
                remaining -= take;
                if (targeted.Remaining == 0) targeted.State = SlotState.Closed;
                if (remaining == 0) return (true, null);
            }
        }

        var fifo = state.Slots
            .Where(s => s.State != SlotState.Closed && s.Remaining > 0)
            .OrderBy(s => s.FirstEntryExecutionSequence)
            .ThenBy(s => s.IntentId, StringComparer.Ordinal)
            .ToList();

        foreach (var slot in fifo)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, slot.Remaining);
            slot.ExitFilledQty += take;
            slot.LastUpdateUtc = utcNow;
            remaining -= take;
            if (slot.Remaining == 0) slot.State = SlotState.Closed;
        }

        if (remaining > 0)
            return (false, $"EXIT_OVERFLOW: {remaining} contracts could not be attributed to any slot");

        return (true, null);
    }

    private void ApplyTransferDuringRestore(InstrumentOwnershipState state, JournalRestoreRow row, DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(row.FromIntentId) || string.IsNullOrEmpty(row.ToIntentId)) return;

        var fromSlot = state.Slots.FirstOrDefault(s => s.IntentId == row.FromIntentId && s.Remaining > 0);
        if (fromSlot == null) return;

        var transferQty = Math.Min(row.TransferQty, fromSlot.Remaining);
        fromSlot.ExitFilledQty += transferQty;
        fromSlot.LastUpdateUtc = utcNow;
        if (fromSlot.Remaining == 0) fromSlot.State = SlotState.Closed;

        var toSlot = state.Slots.FirstOrDefault(s => s.IntentId == row.ToIntentId && s.State != SlotState.Closed);
        if (toSlot != null)
        {
            toSlot.EntryFilledQty += transferQty;
            toSlot.LastUpdateUtc = utcNow;
        }
        else
        {
            state.Slots.Add(new IntentSlot
            {
                IntentId = row.ToIntentId,
                StreamId = fromSlot.StreamId,
                Direction = fromSlot.Direction,
                EntryFilledQty = transferQty,
                ExitFilledQty = 0,
                State = SlotState.Active,
                FirstEntryExecutionSequence = row.ExecutionSequence,
                LastUpdateUtc = utcNow,
                CreatedUtc = utcNow
            });
        }
    }

    private void FinalizeWrite(InstrumentOwnershipState state, DateTimeOffset utcNow)
    {
        state.OwnershipVersion++;
        state.LastLedgerWriteTick = Stopwatch.GetTimestamp();
        state.LastWriteUtc = utcNow;
        var snapshot = BuildSnapshotUnderLock(state, utcNow);
        state.RecentSnapshots.Enqueue(snapshot);
        while (state.RecentSnapshots.Count > 50)
            state.RecentSnapshots.TryDequeue(out _);
    }

    private InstrumentOwnershipSnapshot BuildSnapshotUnderLock(InstrumentOwnershipState state, DateTimeOffset utcNow)
    {
        var clonedSlots = state.Slots.Select(s => s.Clone()).ToList();
        return new InstrumentOwnershipSnapshot
        {
            Account = state.Account,
            ExecutionInstrumentKey = state.ExecutionInstrumentKey,
            OwnershipVersion = state.OwnershipVersion,
            LastLedgerWriteTick = state.LastLedgerWriteTick,
            Slots = clonedSlots,
            LedgerSignedNetQty = clonedSlots.Where(s => s.State != SlotState.Closed).Sum(s => s.SignedRemaining),
            ActiveSlotCount = clonedSlots.Count(s => s.State == SlotState.Active),
            OrphanSlotCount = clonedSlots.Count(s => s.State == SlotState.Orphan),
            MultiIntentAllowed = state.MultiIntentAllowed,
            SnapshotUtc = utcNow
        };
    }

    private void RunInvariantChecks(InstrumentOwnershipState state, InstrumentOwnershipSnapshot snapshot)
    {
        foreach (var slot in snapshot.Slots)
        {
            if (slot.ExitFilledQty > slot.EntryFilledQty)
            {
                EmitEvent(BuildEvent(OwnershipEventKind.InvariantViolation, OwnershipEventClass.ClassA,
                    state, slot.IntentId, 0, slot.Direction, snapshot, DateTimeOffset.UtcNow,
                    $"Negative remaining: entry={slot.EntryFilledQty} exit={slot.ExitFilledQty}"));
            }
        }
    }

    private OwnershipEvent BuildEvent(OwnershipEventKind kind, OwnershipEventClass eventClass,
        InstrumentOwnershipState state, string intentId, int qtyDelta, SlotDirection direction,
        InstrumentOwnershipSnapshot snapshot, DateTimeOffset utcNow, string? detail = null)
    {
        return new OwnershipEvent
        {
            Kind = kind,
            EventClass = eventClass,
            Account = state.Account,
            ExecutionInstrumentKey = state.ExecutionInstrumentKey,
            OwnershipVersion = state.OwnershipVersion,
            IntentId = intentId,
            QtyDelta = qtyDelta,
            Direction = direction,
            Snapshot = snapshot,
            Utc = utcNow,
            Detail = detail
        };
    }

    private void EmitEvent(OwnershipEvent evt)
    {
        if (evt.EventClass == OwnershipEventClass.ClassA)
            _onClassAEvent?.Invoke(evt);
        else
            _onClassBEvent?.Invoke(evt);

        PersistToEventJournal(evt);
    }

    /// <summary>
    /// Persist the event to the durable ownership event journal with a monotonic sequence.
    /// Called under the per-instrument lock (via EmitEvent which is called from locked mutation methods).
    /// </summary>
    private void PersistToEventJournal(OwnershipEvent evt)
    {
        if (_eventJournal == null) return;
        try
        {
            var seq = _eventJournal.AssignNextSequence(evt.ExecutionInstrumentKey);
            var record = new OwnershipEventRecord
            {
                OwnershipEventSequence = seq,
                OwnershipVersion = evt.OwnershipVersion,
                Kind = evt.Kind,
                Account = evt.Account,
                Instrument = evt.ExecutionInstrumentKey,
                IntentId = evt.IntentId ?? "",
                FromIntentId = evt.FromIntentId,
                ToIntentId = evt.ToIntentId,
                Qty = evt.QtyDelta,
                Direction = evt.Direction,
                OrphanReason = evt.OrphanReason,
                EventUtc = evt.Utc.ToString("o"),
                Detail = evt.Detail
            };
            _eventJournal.Append(_currentTradingDate ?? evt.Utc.ToString("yyyy-MM-dd"), record);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "OWNERSHIP_EVENT_JOURNAL_EMIT_ERROR", "ENGINE",
                new { error = ex.Message, kind = evt.Kind.ToString() }));
        }
    }

    private static bool SnapshotsEquivalent(InstrumentOwnershipSnapshot a, InstrumentOwnershipSnapshot b)
    {
        if (a.LedgerSignedNetQty != b.LedgerSignedNetQty) return false;
        if (a.Slots.Count != b.Slots.Count) return false;

        var aSlots = a.Slots.OrderBy(s => s.FirstEntryExecutionSequence).ThenBy(s => s.IntentId).ToList();
        var bSlots = b.Slots.OrderBy(s => s.FirstEntryExecutionSequence).ThenBy(s => s.IntentId).ToList();

        for (var i = 0; i < aSlots.Count; i++)
        {
            if (aSlots[i].IntentId != bSlots[i].IntentId) return false;
            if (aSlots[i].EntryFilledQty != bSlots[i].EntryFilledQty) return false;
            if (aSlots[i].ExitFilledQty != bSlots[i].ExitFilledQty) return false;
            if (aSlots[i].Direction != bSlots[i].Direction) return false;
            if (aSlots[i].State != bSlots[i].State) return false;
        }
        return true;
    }

    private static SlotDirection ParseDirection(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return SlotDirection.Long;
        return dir.Equals("Short", StringComparison.OrdinalIgnoreCase) ? SlotDirection.Short : SlotDirection.Long;
    }
}

/// <summary>
/// Per-instrument mutable ownership state. All mutations require holding <see cref="Lock"/>.
/// </summary>
internal sealed class InstrumentOwnershipState
{
    public readonly object Lock = new();
    public readonly string Account;
    public readonly string ExecutionInstrumentKey;
    public readonly List<IntentSlot> Slots = new();
    public readonly ConcurrentQueue<InstrumentOwnershipSnapshot> RecentSnapshots = new();

    public long OwnershipVersion;
    public long LastLedgerWriteTick;
    public DateTimeOffset LastWriteUtc;
    public bool MultiIntentAllowed;

    public InstrumentOwnershipState(string account, string executionInstrumentKey, bool multiIntentAllowed)
    {
        Account = account;
        ExecutionInstrumentKey = executionInstrumentKey;
        MultiIntentAllowed = multiIntentAllowed;
    }
}

/// <summary>
/// Row used for <see cref="InstrumentOwnershipLedger.RestoreFromJournal"/>.
/// Built from <see cref="ExecutionJournalEntry"/> by the caller.
/// </summary>
public sealed class JournalRestoreRow
{
    public string IntentId { get; init; } = "";
    public string Stream { get; init; } = "";
    public string? Direction { get; init; }
    public int EntryFilledQty { get; init; }
    public int ExitFilledQty { get; init; }
    public long ExecutionSequence { get; init; }
    public bool IsOrphan { get; init; }
    public JournalRowType RowType { get; init; } = JournalRowType.Fill;

    // Transfer-specific
    public string? FromIntentId { get; init; }
    public string? ToIntentId { get; init; }
    public int TransferQty { get; init; }
}

public enum JournalRowType { Fill, Transfer }
