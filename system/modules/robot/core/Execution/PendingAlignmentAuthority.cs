using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Single shared predicate for “we are in a bounded post-mapped-fill alignment window” — used by parity pre-check,
/// mismatch coordinator, mismatch assembly, and release input to avoid duplicate escalation from journal lag.
/// Backed by <see cref="PostFillAlignmentGate"/> and <see cref="JournalParityPendingLedger"/>.
/// </summary>
/// <remarks>
/// Target architecture: broker + expected fill state is authoritative in real time; journal catches up asynchronously.
/// Escalation (hard flatten, mismatch block, journal repair escalation) must not fire solely from broker-vs-journal lag
/// while <see cref="IsPendingAlignment"/> is true for explainable mismatch categories
/// (<see cref="IsJournalLagExplainedMismatchType"/>).
/// </remarks>
[Obsolete("P8: Targeted for deprecation once InstrumentOwnershipLedger dual-run is proven.")]
public static class PendingAlignmentAuthority
{
    /// <summary>
    /// True when a mapped trusted fill recently occurred, expected abs delta is armed, and the alignment window has not expired,
    /// or when the parity pending ledger still holds at least one trusted fill entry (journal persistence in flight).
    /// </summary>
    public static bool IsPendingAlignment(string instrument, DateTimeOffset utcNow)
    {
        if (JournalParityPendingLedger.HasPendingTrustedFillEntries(instrument))
            return true;
        return PostFillAlignmentGate.IsActiveAlignmentWindow(instrument, utcNow);
    }

    /// <summary>
    /// Mismatch categories treated as explainable by mapped-fill / journal-lag alignment (not structural/registry faults).
    /// <see cref="MismatchType.ORDER_REGISTRY_MISSING"/> is included because, during <see cref="IsPendingAlignment"/>,
    /// broker working orders may briefly lack local registry/IEA coverage (OCO transitions, stop/target churn) without
    /// indicating a foreign order — first-touch mismatch escalation is suppressed only while pending alignment is active.
    /// </summary>
    public static bool IsJournalLagExplainedMismatchType(MismatchType t) =>
        t is MismatchType.NET_POSITION_MISMATCH or MismatchType.BROKER_AHEAD or MismatchType.JOURNAL_AHEAD
            or MismatchType.GROSS_POSITION_DIVERGENCE or MismatchType.ORDER_REGISTRY_MISSING;
}
