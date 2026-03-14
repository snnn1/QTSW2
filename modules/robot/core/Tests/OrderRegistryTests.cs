// Unit tests for IEA Order Registry (Execution Ownership Phase 1).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test ORDER_REGISTRY
//
// Verifies: registration, direct broker id lookup, alias lookup, adopted status, lifecycle, multiple orders.

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class OrderRegistryTests
{
    public static (bool Pass, string? Error) RunOrderRegistryTests()
    {
        var registry = new OrderRegistry();
        var utc = DateTimeOffset.UtcNow;

        // 1. Entry order registration
        var entryOi = new MinimalOrderInfo { OrderId = "broker-001", Instrument = "MNQ" };
        var entryEntry = new OrderRegistryEntry
        {
            BrokerOrderId = "broker-001",
            IntentId = "intent-A",
            Instrument = "MNQ",
            OrderRole = OrderRole.ENTRY,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.SUBMITTED,
            CreatedUtc = utc,
            OrderInfo = entryOi
        };
        if (!registry.Register(entryEntry))
            return (false, "Entry order registration failed");

        // 2. Stop order registration
        var stopOi = new MinimalOrderInfo { OrderId = "broker-002", Instrument = "MNQ" };
        var stopEntry = new OrderRegistryEntry
        {
            BrokerOrderId = "broker-002",
            IntentId = "intent-A",
            Instrument = "MNQ",
            OrderRole = OrderRole.STOP,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.SUBMITTED,
            CreatedUtc = utc,
            OrderInfo = stopOi
        };
        if (!registry.Register(stopEntry))
            return (false, "Stop order registration failed");

        // 3. Target order registration
        var targetOi = new MinimalOrderInfo { OrderId = "broker-003", Instrument = "MNQ" };
        var targetEntry = new OrderRegistryEntry
        {
            BrokerOrderId = "broker-003",
            IntentId = "intent-A",
            Instrument = "MNQ",
            OrderRole = OrderRole.TARGET,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.SUBMITTED,
            CreatedUtc = utc,
            OrderInfo = targetOi
        };
        if (!registry.Register(targetEntry))
            return (false, "Target order registration failed");

        // 4. Flatten order registration (no intent)
        var flattenOi = new MinimalOrderInfo { OrderId = "broker-004", Instrument = "MNQ" };
        var flattenEntry = new OrderRegistryEntry
        {
            BrokerOrderId = "broker-004",
            IntentId = null,
            Instrument = "MNQ",
            OrderRole = OrderRole.FLATTEN,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.SUBMITTED,
            CreatedUtc = utc,
            OrderInfo = flattenOi
        };
        if (!registry.Register(flattenEntry))
            return (false, "Flatten order registration failed");

        // 5. Direct broker order id lookup
        if (!registry.TryResolveByBrokerOrderId("broker-002", out var resolved) || resolved == null)
            return (false, "Direct broker id lookup failed");
        if (resolved.OrderRole != OrderRole.STOP || resolved.IntentId != "intent-A")
            return (false, $"Direct lookup wrong: role={resolved.OrderRole}, intent={resolved.IntentId}");

        // 6. Alias lookup (intentId:STOP)
        if (!registry.TryResolveByAlias("intent-A:STOP", out var aliasResolved) || aliasResolved == null)
            return (false, "Alias lookup intent-A:STOP failed");
        if (aliasResolved.BrokerOrderId != "broker-002")
            return (false, $"Alias lookup wrong broker id: {aliasResolved.BrokerOrderId}");

        // 7. Unresolved execution update (unknown broker id)
        if (registry.TryResolveByBrokerOrderId("broker-unknown", out _))
            return (false, "Unresolved broker id should not resolve");

        // 8. Adopted order marked ADOPTED
        var adoptedOi = new MinimalOrderInfo { OrderId = "broker-005", Instrument = "MNQ" };
        var adoptedEntry = new OrderRegistryEntry
        {
            BrokerOrderId = "broker-005",
            IntentId = "intent-B",
            Instrument = "MNQ",
            OrderRole = OrderRole.ADOPTED,
            OwnershipStatus = OrderOwnershipStatus.ADOPTED,
            LifecycleState = OrderLifecycleState.WORKING,
            CreatedUtc = utc,
            OrderInfo = adoptedOi
        };
        registry.Register(adoptedEntry);
        registry.AddAlias("intent-B:STOP", "broker-005");
        if (!registry.TryResolveByBrokerOrderId("broker-005", out var adoptedResolved) || adoptedResolved == null)
            return (false, "Adopted order lookup failed");
        if (adoptedResolved.OwnershipStatus != OrderOwnershipStatus.ADOPTED)
            return (false, $"Adopted order should be ADOPTED: {adoptedResolved.OwnershipStatus}");

        // 9. Completed order transitions to TERMINAL
        var fillTime = utc.AddSeconds(1);
        registry.UpdateLifecycle("broker-002", OrderLifecycleState.FILLED, fillTime);
        if (!registry.TryResolveByBrokerOrderId("broker-002", out var filledResolved) || filledResolved == null)
            return (false, "Filled order lookup failed");
        if (filledResolved.LifecycleState != OrderLifecycleState.FILLED || filledResolved.OwnershipStatus != OrderOwnershipStatus.TERMINAL)
            return (false, $"Filled order should be FILLED/TERMINAL: {filledResolved.LifecycleState}/{filledResolved.OwnershipStatus}");

        // 9b. Add another terminal order for cleanup test (terminated 15 min ago)
        var oldTerminalOi = new MinimalOrderInfo { OrderId = "broker-old", Instrument = "MNQ" };
        var oldTerminalEntry = new OrderRegistryEntry
        {
            BrokerOrderId = "broker-old",
            IntentId = "intent-old",
            Instrument = "MNQ",
            OrderRole = OrderRole.ENTRY,
            OwnershipStatus = OrderOwnershipStatus.TERMINAL,
            LifecycleState = OrderLifecycleState.FILLED,
            CreatedUtc = utc.AddMinutes(-20),
            TerminalUtc = utc.AddMinutes(-15),
            OrderInfo = oldTerminalOi
        };
        registry.Register(oldTerminalEntry);

        // 10. Multiple orders for one intent do not overwrite each other
        if (!registry.TryResolveByAlias("intent-A", out var intentAResolved) || intentAResolved == null)
            return (false, "intent-A alias lookup failed");
        if (!registry.TryResolveByAlias("intent-A:STOP", out var intentAStopResolved) || intentAStopResolved == null)
            return (false, "intent-A:STOP alias lookup failed");
        if (!registry.TryResolveByAlias("intent-A:TARGET", out var intentATargetResolved) || intentATargetResolved == null)
            return (false, "intent-A:TARGET alias lookup failed");
        if (intentAResolved.BrokerOrderId == intentAStopResolved.BrokerOrderId && intentAResolved.BrokerOrderId == intentATargetResolved.BrokerOrderId)
            return (false, "Entry, stop, target should have different broker ids");
        if (intentAStopResolved.BrokerOrderId != "broker-002" || intentATargetResolved.BrokerOrderId != "broker-003")
            return (false, $"Stop/target broker ids wrong: stop={intentAStopResolved.BrokerOrderId}, target={intentATargetResolved.BrokerOrderId}");

        // Phase 2 tests
        // 11. Lifecycle validation: WORKING -> CREATED rejected
        if (OrderRegistry.ValidateLifecycleTransition(OrderLifecycleState.WORKING, OrderLifecycleState.CREATED))
            return (false, "WORKING->CREATED should be rejected");
        // 12. Lifecycle validation: SUBMITTED -> WORKING allowed
        if (!OrderRegistry.ValidateLifecycleTransition(OrderLifecycleState.SUBMITTED, OrderLifecycleState.WORKING))
            return (false, "SUBMITTED->WORKING should be allowed");
        // 13. Registry cleanup: terminal orders older than retention window removed
        var cutoff = utc.AddMinutes(-10); // Remove if TerminalUtc < cutoff (terminated >10 min ago)
        var removed = registry.CleanupTerminalOrders(cutoff, null);
        if (removed < 1)
            return (false, $"Cleanup should remove at least 1 terminal order, removed={removed}");
        if (registry.TryResolveByBrokerOrderId("broker-old", out _))
            return (false, "broker-old (terminated 15 min ago) should have been cleaned up");
        if (!registry.TryResolveByBrokerOrderId("broker-002", out _))
            return (false, "broker-002 (recently filled) should NOT have been cleaned up");
        // 14. Adopted order is ADOPTED not OWNED
        if (adoptedResolved.OwnershipStatus != OrderOwnershipStatus.ADOPTED)
            return (false, $"Adopted should be ADOPTED: {adoptedResolved.OwnershipStatus}");
        // 15. Metrics
        var metrics = registry.GetMetrics();
        if (metrics.UnownedOrdersDetected < 0 || metrics.RegistryIntegrityFailures < 0)
            return (false, "Metrics should be non-negative");

        return (true, null);
    }
}
