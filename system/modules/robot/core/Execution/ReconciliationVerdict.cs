using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Immutable classification record from <see cref="ReconciliationClassifier"/>.
/// Carries <see cref="OwnershipVersion"/> so all downstream consumers can correlate the verdict
/// to the exact ledger state that produced it.
/// </summary>
public sealed class ReconciliationVerdict
{
    public string Instrument { get; init; } = "";
    /// <summary>Raw broker quantity as reported by the adapter. May be absolute on some broker adapters.</summary>
    public int BrokerQty { get; init; }
    /// <summary>Broker quantity normalized to the ledger's signed direction for mismatch math.</summary>
    public int BrokerSignedQty { get; init; }
    public int LedgerQty { get; init; }
    /// <summary>
    /// Open quantity derived from ledger slots (not from <see cref="ExecutionJournal"/> directly).
    /// Named JournalOpenQty for schema stability; represents the ledger's view of
    /// open position quantity (sum of non-closed slot remaining). Compare to broker qty for mismatch classification.
    /// </summary>
    public int JournalOpenQty { get; init; }
    public int UnexplainedQty => BrokerSignedQty - LedgerQty;
    public long OwnershipVersion { get; init; }
    public MismatchTier MismatchTier { get; init; }
    public double MismatchAgeMs { get; init; }
    public bool IsStale { get; init; }
    public VerdictConfidence Confidence { get; init; }
    public DateTimeOffset ClassifiedUtc { get; init; }
    public int ActiveSlotCount { get; init; }
    public int OrphanSlotCount { get; init; }
    public VerdictSubType SubType { get; init; } = VerdictSubType.None;
    public string? Detail { get; init; }
}

public enum VerdictConfidence
{
    High,
    Medium,
    Low
}

public enum VerdictSubType
{
    None,
    CoverageGapPerSlot,
    OrphanPersistsBeyondWindow,
    ExitOverflow,
    TransferRejected
}
