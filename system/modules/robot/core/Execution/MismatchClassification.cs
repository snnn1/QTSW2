// Gap 4: First-pass mismatch classification heuristics.
// Compact helper; coordinator can own logic. Kept separate for clarity.

namespace QTSW2.Robot.Core.Execution;

/// <summary>Simple explicit rules for classifying broker vs local divergence.</summary>
public static class MismatchClassification
{
    /// <summary>
    /// True when the broker presents a net-flat instrument row while journal/ownership still have
    /// valid opposing gross exposure and the live working-order count agrees with local authority.
    /// This is not clean-flat release evidence; it is coherent gross-active exposure.
    /// </summary>
    public static bool IsExplainedHedgedNetFlatGrossOpen(
        int brokerQtyAbs,
        int grossJournalQty,
        int netBrokerQty,
        int netJournalQty,
        bool opposingMultiIntentOpen,
        int brokerWorkingOrderCount,
        int localWorkingOrderCount) =>
        opposingMultiIntentOpen &&
        brokerQtyAbs == 0 &&
        grossJournalQty > 0 &&
        netBrokerQty == 0 &&
        netJournalQty == 0 &&
        brokerWorkingOrderCount > 0 &&
        brokerWorkingOrderCount == localWorkingOrderCount;

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

        if (opposingMultiIntentOpen && brokerQtyAbs == 0 && grossJournalQty > 0 &&
            netBrokerQty == 0 && netJournalQty == 0)
            return MismatchType.HEDGED_NET_FLAT_GROSS_OPEN;

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

        if (brokerWorkingOrderCount > localWorkingOrderCount)
            return MismatchType.ORDER_REGISTRY_MISSING;

        if (localWorkingOrderCount > brokerWorkingOrderCount)
            return MismatchType.WORKING_ORDER_COUNT_CONVERGENCE;

        return MismatchType.UNCLASSIFIED_CRITICAL_MISMATCH;
    }
}
