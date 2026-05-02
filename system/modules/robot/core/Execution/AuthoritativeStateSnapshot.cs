using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Trigger reason for an authoritative state snapshot.
/// </summary>
public enum SnapshotTrigger
{
    Periodic,
    Fill,
    MismatchDetected,
    SupervisorEscalation,
    OrphanFill,
    ReconciliationVerdict,
    RestoreBaseline,
    FailClosed,
    OwnershipConflictRejected,
    ExitOverflow,
    TransferRejected,
    InstrumentFreeze,
    InstrumentUnfreeze,
    EngineStop
}

/// <summary>
/// Full authoritative state snapshot for one instrument at a point in time.
/// Written as JSONL by <see cref="AuthoritativeStateEmitter"/>.
/// </summary>
public sealed class AuthoritativeInstrumentSnapshot
{
    public string Account { get; init; } = "";
    public string Instrument { get; init; } = "";
    public long OwnershipVersion { get; init; }
    public int LedgerSignedNetQty { get; init; }
    public int ActiveSlotCount { get; init; }
    public int OrphanSlotCount { get; init; }
    public IReadOnlyList<SlotSummary>? Slots { get; init; }

    public int BrokerPositionQty { get; init; }
    public int BrokerWorkingOrderCount { get; init; }
    public int JournalOpenQty { get; init; }
    public int JournalRowCount { get; init; }

    public int UnexplainedQty { get; init; }
    public bool IsExplainable { get; init; }
    public string DerivedAuthority { get; init; } = "";

    public string? SupervisoryState { get; init; }
    public string? MismatchEscalationState { get; init; }
    public bool IsFrozen { get; init; }
    public bool IsKillSwitchActive { get; init; }

    public SnapshotTrigger SnapshotTrigger { get; init; }
    public long SnapshotSequence { get; init; }
    public string SnapshotUtc { get; init; } = "";
}

public sealed class SlotSummary
{
    public string IntentId { get; init; } = "";
    public string StreamId { get; init; } = "";
    public string Direction { get; init; } = "";
    public int Remaining { get; init; }
    public string State { get; init; } = "";
}

/// <summary>
/// Full snapshot across all instruments for one emission.
/// </summary>
public sealed class AuthoritativeStateSnapshotEnvelope
{
    public string Account { get; init; } = "";
    public long SnapshotSequence { get; init; }
    public SnapshotTrigger Trigger { get; init; }
    public string EmittedUtc { get; init; } = "";
    public IReadOnlyList<AuthoritativeInstrumentSnapshot> Instruments { get; init; } = Array.Empty<AuthoritativeInstrumentSnapshot>();
}
