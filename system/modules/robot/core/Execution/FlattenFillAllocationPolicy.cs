using System;

namespace QTSW2.Robot.Core.Execution;

internal static class FlattenFillAllocationPolicy
{
    public const string SessionForcedFlattenReason = "SESSION_FORCED_FLATTEN";

    public static bool ShouldPreferOpenJournalAllocationForRegistryLink(
        string? flattenReason,
        int fillQuantity,
        int originalIntentRemainingQty,
        int openJournalAllocationCount,
        int openJournalAllocationQty)
    {
        if (!string.Equals(flattenReason?.Trim(), SessionForcedFlattenReason, StringComparison.OrdinalIgnoreCase))
            return false;
        if (fillQuantity <= 0 || openJournalAllocationCount <= 0 || openJournalAllocationQty <= 0)
            return false;

        // Session forced flatten is an instrument-level broker close. If more than one stream is open,
        // the registry's original intent is audit context, not exclusive fill ownership.
        if (openJournalAllocationCount > 1)
            return true;

        // If the registry-linked original row is not open but the instrument journal is, use the
        // remaining journal row rather than dropping into a stale single-intent link.
        if (originalIntentRemainingQty <= 0)
            return true;

        return fillQuantity > originalIntentRemainingQty && openJournalAllocationQty > originalIntentRemainingQty;
    }
}
