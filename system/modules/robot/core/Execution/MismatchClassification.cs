// Gap 4: First-pass mismatch classification heuristics.
// Compact helper; coordinator can own logic. Kept separate for clarity.

namespace QTSW2.Robot.Core.Execution;

/// <summary>Simple explicit rules for classifying broker vs local divergence.</summary>
public static class MismatchClassification
{
    /// <summary>
    /// Classify using gross journal, broker abs sum, and signed nets. Replaces single POSITION_QTY_MISMATCH with
    /// net vs gross distinctions; see <see cref="MismatchType.NET_POSITION_MISMATCH"/>.
    /// </summary>
    public static MismatchType Classify(
        int brokerQtyAbs,
        int grossJournalQty,
        int netBrokerQty,
        int netJournalQty,
        bool opposingMultiIntentOpen,
        int brokerWorkingOrderCount = 0,
        int localWorkingOrderCount = 0,
        bool lifecycleSaysProtected = false,
        bool brokerStopAbsent = false,
        bool startupUnresolved = false)
    {
        if (startupUnresolved)
            return MismatchType.RESTART_RECONCILIATION_UNRESOLVED;

        if (lifecycleSaysProtected && brokerStopAbsent)
            return MismatchType.PROTECTIVE_STATE_DIVERGENCE;

        if (brokerWorkingOrderCount > 0 && localWorkingOrderCount == 0)
            return MismatchType.ORDER_REGISTRY_MISSING;

        if (brokerQtyAbs != 0 && grossJournalQty == 0)
            return MismatchType.BROKER_AHEAD;

        if (brokerQtyAbs == 0 && grossJournalQty != 0)
            return MismatchType.JOURNAL_AHEAD;

        if (netBrokerQty != netJournalQty)
            return MismatchType.NET_POSITION_MISMATCH;

        if (grossJournalQty != brokerQtyAbs)
        {
            if (opposingMultiIntentOpen)
                return MismatchType.STRUCTURAL_MULTI_INTENT;
            return MismatchType.GROSS_POSITION_DIVERGENCE;
        }

        return MismatchType.UNCLASSIFIED_CRITICAL_MISMATCH;
    }
}
