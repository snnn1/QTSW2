// Gap 4: First-pass mismatch classification heuristics.
// Compact helper; coordinator can own logic. Kept separate for clarity.

namespace QTSW2.Robot.Core.Execution;

/// <summary>Simple explicit rules for classifying broker vs local divergence.</summary>
public static class MismatchClassification
{
    /// <summary>Classify based on broker qty, local qty, and optional registry/lifecycle flags.</summary>
    public static MismatchType Classify(
        int brokerQty,
        int localQty,
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

        if (brokerQty != 0 && localQty == 0)
            return MismatchType.BROKER_AHEAD;

        if (brokerQty == 0 && localQty != 0)
            return MismatchType.JOURNAL_AHEAD;

        if (brokerQty != localQty && brokerQty != 0 && localQty != 0)
            return MismatchType.POSITION_QTY_MISMATCH;

        return MismatchType.UNCLASSIFIED_CRITICAL_MISMATCH;
    }
}
