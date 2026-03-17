using System;

namespace QTSW2.Robot.Core;

/// <summary>
/// Typed recovery action for entry-order reconciliation.
/// Replaces coarse _reconciliationRequestedResubmit with explicit action model.
/// </summary>
public enum EntryOrderRecoveryAction
{
    /// <summary>No action needed.</summary>
    None,

    /// <summary>Submit one clean entry-order set (missing set case).</summary>
    ResubmitClean,

    /// <summary>Cancel all entry orders for stream, wait for confirmation, then submit one clean set (broken set case).</summary>
    CancelAndRebuild
}

/// <summary>
/// Broker-state classification for RANGE_LOCKED streams.
/// Used during Phase A (audit/classify) to assign recovery action.
/// </summary>
public enum BrokerStateClassification
{
    /// <summary>Instrument position is not flat or entry already detected.</summary>
    PositionExists,

    /// <summary>Exactly one correct entry-order set exists on broker.</summary>
    ValidSetPresent,

    /// <summary>No valid entry orders exist and no malformed remnants exist.</summary>
    MissingSet,

    /// <summary>Partial set, duplicate set, wrong-price set, rejected remnants, invalid state mix, or OCO mismatch.</summary>
    BrokenSet
}

/// <summary>
/// Recovery action state for a stream. Lives on stream state; persisted via StreamJournal.
/// </summary>
public sealed class EntryOrderRecoveryState
{
    public EntryOrderRecoveryAction Action { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset IssuedUtc { get; set; }
    public DateTimeOffset? LastClassificationUtc { get; set; }
    public string? LastClassificationResult { get; set; }

    public bool IsPending => Action != EntryOrderRecoveryAction.None;

    public static EntryOrderRecoveryState None() =>
        new() { Action = EntryOrderRecoveryAction.None, Reason = "", IssuedUtc = default };
}
