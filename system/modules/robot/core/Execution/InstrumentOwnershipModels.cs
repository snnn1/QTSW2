using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

public enum SlotDirection { Long, Short }

public enum SlotState { Active, Closed, Orphan, Manual }

public enum OrphanReason { None, UnknownOrder, ContextResolutionFailed, IntentLostAfterContext }

public enum MismatchTier { Convergence, TransientMismatch, HardMismatch }

public enum OwnershipEventClass { ClassA, ClassB }

public enum OwnershipEventKind
{
    MappedEntryFill,
    MappedExitFill,
    OrphanFill,
    OwnershipTransfer,
    TransferRejected,
    InvariantViolation,
    RestoreBaseline,
    ExitOverflow,
    OwnershipConflictRejected,
    SnapshotNotification,
    SlotClosed
}

/// <summary>
/// Single intent slot within an instrument's ownership state.
/// Slots from different intentIds are never merged (Rule 7).
/// </summary>
public sealed class IntentSlot
{
    public string IntentId { get; set; } = "";
    public string StreamId { get; set; } = "";
    public SlotDirection Direction { get; set; }
    public int EntryFilledQty { get; set; }
    public int ExitFilledQty { get; set; }
    public SlotState State { get; set; } = SlotState.Active;
    public long FirstEntryExecutionSequence { get; set; }
    public DateTimeOffset LastUpdateUtc { get; set; }
    public OrphanReason OrphanReason { get; set; } = OrphanReason.None;
    public string? BrokerOrderId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }

    public int Remaining => EntryFilledQty - ExitFilledQty;

    public int DirectionSign => Direction == SlotDirection.Long ? 1 : -1;

    public int SignedRemaining => Remaining * DirectionSign;

    public IntentSlot Clone() => new()
    {
        IntentId = IntentId,
        StreamId = StreamId,
        Direction = Direction,
        EntryFilledQty = EntryFilledQty,
        ExitFilledQty = ExitFilledQty,
        State = State,
        FirstEntryExecutionSequence = FirstEntryExecutionSequence,
        LastUpdateUtc = LastUpdateUtc,
        OrphanReason = OrphanReason,
        BrokerOrderId = BrokerOrderId,
        CreatedUtc = CreatedUtc
    };
}

/// <summary>
/// Immutable snapshot of an instrument's ownership state at a specific version.
/// Produced under the per-instrument lock; safe to read without holding any lock.
/// </summary>
public sealed class InstrumentOwnershipSnapshot
{
    public string Account { get; init; } = "";
    public string ExecutionInstrumentKey { get; init; } = "";
    public long OwnershipVersion { get; init; }
    public long LastLedgerWriteTick { get; init; }
    public IReadOnlyList<IntentSlot> Slots { get; init; } = Array.Empty<IntentSlot>();
    public int LedgerSignedNetQty { get; init; }
    public int ActiveSlotCount { get; init; }
    public int OrphanSlotCount { get; init; }
    public bool MultiIntentAllowed { get; init; }
    public DateTimeOffset SnapshotUtc { get; init; }

    public double ComputeMismatchAgeMs()
    {
        if (LastLedgerWriteTick == 0) return double.MaxValue;
        var elapsed = Stopwatch.GetTimestamp() - LastLedgerWriteTick;
        return (double)elapsed / Stopwatch.Frequency * 1000.0;
    }

    public int ComputeUnexplainedQty(int brokerSignedNetQty) => brokerSignedNetQty - LedgerSignedNetQty;
}

/// <summary>
/// Event emitted by the ledger after every mutation.
/// Class A events must never be dropped; Class B events may be coalesced/dropped.
/// </summary>
public sealed class OwnershipEvent
{
    public OwnershipEventKind Kind { get; init; }
    public OwnershipEventClass EventClass { get; init; }
    public string Account { get; init; } = "";
    public string ExecutionInstrumentKey { get; init; } = "";
    public long OwnershipVersion { get; init; }
    public string IntentId { get; init; } = "";
    public string? FromIntentId { get; init; }
    public string? ToIntentId { get; init; }
    public int QtyDelta { get; init; }
    public SlotDirection Direction { get; init; }
    public OrphanReason OrphanReason { get; init; }
    public InstrumentOwnershipSnapshot? Snapshot { get; init; }
    public DateTimeOffset Utc { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// Result from ledger write operations indicating success or specific failure.
/// </summary>
public sealed class LedgerWriteResult
{
    public bool Success { get; init; }
    public InstrumentOwnershipSnapshot? Snapshot { get; init; }
    public string? ErrorReason { get; init; }
    public OwnershipEventKind? ErrorKind { get; init; }
    /// <summary>Set by <see cref="InstrumentOwnershipLedger.RecordOrphanFill"/> so callers can reference the slot for flatten-result updates.</summary>
    public string? OrphanSlotId { get; init; }

    public static LedgerWriteResult Ok(InstrumentOwnershipSnapshot snapshot) => new() { Success = true, Snapshot = snapshot };
    public static LedgerWriteResult OkOrphan(InstrumentOwnershipSnapshot snapshot, string orphanSlotId) => new() { Success = true, Snapshot = snapshot, OrphanSlotId = orphanSlotId };
    public static LedgerWriteResult Fail(string reason, OwnershipEventKind kind) => new() { Success = false, ErrorReason = reason, ErrorKind = kind };
}
