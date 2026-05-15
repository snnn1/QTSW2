namespace QTSW2.Robot.Core.Execution;

public static class FlattenVerifierLifecycleRules
{
    public static bool ShouldRetireForLinkedProtectedReentry(
        bool originalTradeCompleted,
        int originalOpenQty,
        bool streamOriginalMatches,
        string? reentryIntentId,
        bool reentrySubmitted,
        bool reentryFilled,
        bool protectionAccepted,
        bool reentryTradeCompleted,
        int reentryOpenQty)
    {
        if (!originalTradeCompleted || originalOpenQty > 0)
            return false;

        if (!streamOriginalMatches)
            return false;

        if (string.IsNullOrWhiteSpace(reentryIntentId))
            return false;

        if (!reentrySubmitted || !reentryFilled || !protectionAccepted)
            return false;

        if (reentryTradeCompleted || reentryOpenQty <= 0)
            return false;

        return true;
    }
}
