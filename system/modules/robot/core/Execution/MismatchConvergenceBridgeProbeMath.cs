using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Phase 3.2: assembly-aligned aggregates for convergence probe only (same gross/net + PendingFillBridge overlay as
/// <c>AssembleMismatchObservations</c>). Does not affect release-readiness evaluation for other callers.
/// </summary>
public static class MismatchConvergenceBridgeProbeMath
{
    /// <summary>
    /// True when broker gross/net match effective journal gross/net (raw journal + bridge overlays), mirroring
    /// <c>brokerQty == effectiveJournalQty &amp;&amp; netBrokerQty == effectiveNetJournalQty</c> in mismatch assembly.
    /// </summary>
    public static bool IsPositionAggregateExplained(
        int brokerGrossAbs,
        int brokerNet,
        int journalGrossRaw,
        int journalNetRaw,
        int pendingGrossOv,
        int pendingNetOv,
        out int effectiveJournalGross,
        out int effectiveNetJournal,
        out int rawPosDiffGross,
        out int effectivePosDiffGross)
    {
        effectiveJournalGross = journalGrossRaw + pendingGrossOv;
        effectiveNetJournal = journalNetRaw + pendingNetOv;
        rawPosDiffGross = Math.Abs(brokerGrossAbs - journalGrossRaw);
        effectivePosDiffGross = Math.Abs(brokerGrossAbs - effectiveJournalGross);
        return brokerGrossAbs == effectiveJournalGross && brokerNet == effectiveNetJournal;
    }

    /// <summary>
    /// Scalar unexplained position exposure for diagnostics: max of gross and net absolute deltas vs effective journal.
    /// </summary>
    public static int EffectiveUnexplainedPositionQty(
        int brokerGrossAbs,
        int brokerNet,
        int effectiveJournalGross,
        int effectiveNetJournal)
    {
        return Math.Max(
            Math.Abs(brokerGrossAbs - effectiveJournalGross),
            Math.Abs(brokerNet - effectiveNetJournal));
    }
}
