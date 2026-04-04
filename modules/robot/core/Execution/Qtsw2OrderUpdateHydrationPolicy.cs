using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Pure rules for QTSW2-tagged order updates: when a registry row may hydrate <see cref="NinjaTraderSimAdapter"/> OrderMap
/// before the manual/external miss path (Sim id remap / race vs submit).
/// </summary>
public static class Qtsw2OrderUpdateHydrationPolicy
{
    public static bool IsTerminalRegistryRow(OrderRegistryEntry e)
    {
        if (e == null) return true;
        if (e.OwnershipStatus == OrderOwnershipStatus.TERMINAL) return true;
        return e.LifecycleState is OrderLifecycleState.FILLED or OrderLifecycleState.CANCELED or OrderLifecycleState.REJECTED;
    }

    /// <summary>
    /// Requires intent match and order role aligned with tag leg (base tag → ENTRY).
    /// </summary>
    public static bool RegistryRowMatchesTaggedIntentAndLeg(OrderRegistryEntry e, string intentId, string? parsedLeg)
    {
        if (e == null || string.IsNullOrEmpty(intentId)) return false;
        if (!string.Equals(e.IntentId, intentId, StringComparison.OrdinalIgnoreCase)) return false;

        var expectedRole = OrderRole.ENTRY;
        if (string.Equals(parsedLeg, "STOP", StringComparison.OrdinalIgnoreCase)) expectedRole = OrderRole.STOP;
        else if (string.Equals(parsedLeg, "TARGET", StringComparison.OrdinalIgnoreCase)) expectedRole = OrderRole.TARGET;
        else if (string.Equals(parsedLeg, "ENTRY", StringComparison.OrdinalIgnoreCase)) expectedRole = OrderRole.ENTRY;

        return e.OrderRole == expectedRole;
    }
}
