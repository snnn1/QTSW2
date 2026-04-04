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

        // ORDER_REGISTRY_MISSING fix tests
        // 16. GetOwnedPlusAdoptedWorkingCount: SUBMITTED entry orders count (pre-entry, no false mismatch)
        var reg2 = new OrderRegistry();
        var oi1 = new MinimalOrderInfo { OrderId = "e1", Instrument = "MYM" };
        reg2.Register(new OrderRegistryEntry { BrokerOrderId = "e1", IntentId = "i1", Instrument = "MYM", OrderRole = OrderRole.ENTRY, OwnershipStatus = OrderOwnershipStatus.OWNED, LifecycleState = OrderLifecycleState.SUBMITTED, CreatedUtc = utc, OrderInfo = oi1 });
        var oi2 = new MinimalOrderInfo { OrderId = "e2", Instrument = "MYM" };
        reg2.Register(new OrderRegistryEntry { BrokerOrderId = "e2", IntentId = "i2", Instrument = "MYM", OrderRole = OrderRole.ENTRY, OwnershipStatus = OrderOwnershipStatus.OWNED, LifecycleState = OrderLifecycleState.SUBMITTED, CreatedUtc = utc, OrderInfo = oi2 });
        var count = reg2.GetOwnedPlusAdoptedWorkingCount();
        if (count != 2)
            return (false, $"Pre-entry: expected 2 (SUBMITTED), got {count}");

        // 17. Transition state: SUBMITTED + WORKING both count
        reg2.UpdateLifecycle("e1", OrderLifecycleState.WORKING, null);
        count = reg2.GetOwnedPlusAdoptedWorkingCount();
        if (count != 2)
            return (false, $"Transition: expected 2 (SUBMITTED+WORKING), got {count}");

        // 18. True mismatch scenario: broker has 2, IEA has 0 → empty registry returns 0
        var reg3 = new OrderRegistry();
        if (reg3.GetOwnedPlusAdoptedWorkingCount() != 0)
            return (false, "Empty registry should return 0");

        // 19. Duplicate adoption prevention: Register same broker order id twice overwrites
        var reg4 = new OrderRegistry();
        var dupOi = new MinimalOrderInfo { OrderId = "broker-dup", Instrument = "MNQ" };
        reg4.Register(new OrderRegistryEntry { BrokerOrderId = "broker-dup", IntentId = "i-dup", Instrument = "MNQ", OrderRole = OrderRole.ADOPTED, OwnershipStatus = OrderOwnershipStatus.ADOPTED, LifecycleState = OrderLifecycleState.WORKING, CreatedUtc = utc, OrderInfo = dupOi });
        reg4.Register(new OrderRegistryEntry { BrokerOrderId = "broker-dup", IntentId = "i-dup", Instrument = "MNQ", OrderRole = OrderRole.ADOPTED, OwnershipStatus = OrderOwnershipStatus.ADOPTED, LifecycleState = OrderLifecycleState.WORKING, CreatedUtc = utc, OrderInfo = dupOi });
        if (reg4.GetOwnedPlusAdoptedWorkingCount() != 1)
            return (false, $"Duplicate adoption: Register same broker id twice should overwrite, count=1, got {reg4.GetOwnedPlusAdoptedWorkingCount()}");

        // 20. FILLED -> FILLED lifecycle update is idempotent (no invalid transition)
        var reg5 = new OrderRegistry();
        var fillOi = new MinimalOrderInfo { OrderId = "broker-fill-twice", Instrument = "MNQ" };
        reg5.Register(new OrderRegistryEntry
        {
            BrokerOrderId = "broker-fill-twice",
            IntentId = "i-fill",
            Instrument = "MNQ",
            OrderRole = OrderRole.ENTRY,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.WORKING,
            CreatedUtc = utc,
            OrderInfo = fillOi
        });
        reg5.UpdateLifecycle("broker-fill-twice", OrderLifecycleState.FILLED, utc.AddSeconds(1));
        if (!reg5.TryResolveByBrokerOrderId("broker-fill-twice", out var fillEntry) || fillEntry == null || fillEntry.LifecycleState != OrderLifecycleState.FILLED)
            return (false, "First FILLED transition failed");
        if (!reg5.UpdateLifecycle("broker-fill-twice", OrderLifecycleState.FILLED, utc.AddSeconds(2)))
            return (false, "Duplicate FILLED -> FILLED should be no-op success, not rejected");
        if (!reg5.TryResolveByBrokerOrderId("broker-fill-twice", out fillEntry) || fillEntry == null || fillEntry.LifecycleState != OrderLifecycleState.FILLED)
            return (false, "State should remain FILLED after duplicate update");

        // 21–23. Non-terminal transitions with non-null event time must not force TERMINAL ownership (UpdateOrderLifecycle always passes utcNow).
        var ts = utc.AddMinutes(5);
        var rNt = new OrderRegistry();
        var oWs = new MinimalOrderInfo { OrderId = "nt-w", Instrument = "MNQ" };
        rNt.Register(new OrderRegistryEntry
        {
            BrokerOrderId = "nt-w",
            IntentId = "i-nt",
            Instrument = "MNQ",
            OrderRole = OrderRole.ENTRY,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.SUBMITTED,
            CreatedUtc = utc,
            OrderInfo = oWs
        });
        rNt.UpdateLifecycle("nt-w", OrderLifecycleState.WORKING, ts);
        if (!rNt.TryResolveByBrokerOrderId("nt-w", out var eNt) || eNt == null)
            return (false, "21: resolve nt-w failed");
        if (eNt.LifecycleState != OrderLifecycleState.WORKING || eNt.OwnershipStatus != OrderOwnershipStatus.OWNED || eNt.TerminalUtc != null)
            return (false, $"21: SUBMITTED->WORKING+ts must stay OWNED, no TerminalUtc: life={eNt.LifecycleState} own={eNt.OwnershipStatus} tu={eNt.TerminalUtc}");

        var oPf1 = new MinimalOrderInfo { OrderId = "nt-pf1", Instrument = "MNQ" };
        rNt.Register(new OrderRegistryEntry
        {
            BrokerOrderId = "nt-pf1",
            IntentId = "i-pf1",
            Instrument = "MNQ",
            OrderRole = OrderRole.ENTRY,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.SUBMITTED,
            CreatedUtc = utc,
            OrderInfo = oPf1
        });
        rNt.UpdateLifecycle("nt-pf1", OrderLifecycleState.PART_FILLED, ts);
        if (!rNt.TryResolveByBrokerOrderId("nt-pf1", out eNt) || eNt == null)
            return (false, "22: resolve nt-pf1 failed");
        if (eNt.LifecycleState != OrderLifecycleState.PART_FILLED || eNt.OwnershipStatus != OrderOwnershipStatus.OWNED || eNt.TerminalUtc != null)
            return (false, $"22: SUBMITTED->PART_FILLED+ts must stay OWNED: own={eNt.OwnershipStatus} tu={eNt.TerminalUtc}");

        var oPf2 = new MinimalOrderInfo { OrderId = "nt-pf2", Instrument = "MNQ" };
        rNt.Register(new OrderRegistryEntry
        {
            BrokerOrderId = "nt-pf2",
            IntentId = "i-pf2",
            Instrument = "MNQ",
            OrderRole = OrderRole.ENTRY,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.WORKING,
            CreatedUtc = utc,
            OrderInfo = oPf2
        });
        rNt.UpdateLifecycle("nt-pf2", OrderLifecycleState.PART_FILLED, ts);
        if (!rNt.TryResolveByBrokerOrderId("nt-pf2", out eNt) || eNt == null)
            return (false, "23: resolve nt-pf2 failed");
        if (eNt.LifecycleState != OrderLifecycleState.PART_FILLED || eNt.OwnershipStatus != OrderOwnershipStatus.OWNED || eNt.TerminalUtc != null)
            return (false, $"23: WORKING->PART_FILLED+ts must stay OWNED: own={eNt.OwnershipStatus} tu={eNt.TerminalUtc}");

        // 24. Terminal states with non-null timestamp → TERMINAL + TerminalUtc
        var rT = new OrderRegistry();
        void RegT(string bid, string intent)
        {
            rT.Register(new OrderRegistryEntry
            {
                BrokerOrderId = bid,
                IntentId = intent,
                Instrument = "MNQ",
                OrderRole = OrderRole.ENTRY,
                OwnershipStatus = OrderOwnershipStatus.OWNED,
                LifecycleState = OrderLifecycleState.SUBMITTED,
                CreatedUtc = utc,
                OrderInfo = new MinimalOrderInfo { OrderId = bid, Instrument = "MNQ" }
            });
        }
        RegT("term-fill", "i-tf");
        var tFill = ts.AddSeconds(1);
        rT.UpdateLifecycle("term-fill", OrderLifecycleState.FILLED, tFill);
        if (!rT.TryResolveByBrokerOrderId("term-fill", out var eT) || eT == null || eT.OwnershipStatus != OrderOwnershipStatus.TERMINAL || eT.TerminalUtc != tFill)
            return (false, "24a: FILLED should set TERMINAL and TerminalUtc");

        RegT("term-can", "i-tc");
        var tCan = ts.AddSeconds(2);
        rT.UpdateLifecycle("term-can", OrderLifecycleState.CANCELED, tCan);
        if (!rT.TryResolveByBrokerOrderId("term-can", out eT) || eT == null || eT.OwnershipStatus != OrderOwnershipStatus.TERMINAL || eT.TerminalUtc != tCan)
            return (false, "24b: CANCELED should set TERMINAL and TerminalUtc");

        RegT("term-rej", "i-tr");
        var tRej = ts.AddSeconds(3);
        rT.UpdateLifecycle("term-rej", OrderLifecycleState.REJECTED, tRej);
        if (!rT.TryResolveByBrokerOrderId("term-rej", out eT) || eT == null || eT.OwnershipStatus != OrderOwnershipStatus.TERMINAL || eT.TerminalUtc != tRej)
            return (false, "24c: REJECTED should set TERMINAL and TerminalUtc");

        // 25. Mismatch-trusted count includes OWNED WORKING / PART_FILLED after non-terminal updates with timestamp
        var rM = new OrderRegistry();
        rM.Register(new OrderRegistryEntry
        {
            BrokerOrderId = "m1",
            IntentId = "im1",
            Instrument = "MNQ",
            OrderRole = OrderRole.ENTRY,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.SUBMITTED,
            CreatedUtc = utc,
            OrderInfo = new MinimalOrderInfo { OrderId = "m1", Instrument = "MNQ" }
        });
        rM.Register(new OrderRegistryEntry
        {
            BrokerOrderId = "m2",
            IntentId = "im2",
            Instrument = "MNQ",
            OrderRole = OrderRole.ENTRY,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.WORKING,
            CreatedUtc = utc,
            OrderInfo = new MinimalOrderInfo { OrderId = "m2", Instrument = "MNQ" }
        });
        rM.UpdateLifecycle("m1", OrderLifecycleState.WORKING, ts);
        rM.UpdateLifecycle("m2", OrderLifecycleState.PART_FILLED, ts);
        var mismatchTrusted = rM.GetMismatchTrustedWorkingCount();
        if (mismatchTrusted != 2)
            return (false, $"25: GetMismatchTrustedWorkingCount expected 2 (WORKING+PART_FILLED OWNED), got {mismatchTrusted}");

        // 26. OCO-style integration: two legs, linked native ids, non-null timestamps on lifecycle (production path).
        // Regression: pre-fix SUBMITTED→WORKING + utcNow falsely set TERMINAL so one leg dropped from trusted count (MCL 2 vs 1).
        var rOco = new OrderRegistry();
        var ocoTs = utc.AddMinutes(7);
        const string ocoCanonLong = "oco-canon-long";
        const string ocoCanonShort = "oco-canon-short";
        const string ocoNativeLong = "oco-native-long-850";
        const string ocoNativeShort = "oco-native-short-853";
        rOco.Register(new OrderRegistryEntry
        {
            BrokerOrderId = ocoCanonLong,
            IntentId = "intent-oco-long",
            Instrument = "MCL",
            OrderRole = OrderRole.ENTRY,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.SUBMITTED,
            CreatedUtc = utc,
            OrderInfo = new MinimalOrderInfo { OrderId = ocoCanonLong, Instrument = "MCL" }
        });
        rOco.Register(new OrderRegistryEntry
        {
            BrokerOrderId = ocoCanonShort,
            IntentId = "intent-oco-short",
            Instrument = "MCL",
            OrderRole = OrderRole.ENTRY,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.SUBMITTED,
            CreatedUtc = utc,
            OrderInfo = new MinimalOrderInfo { OrderId = ocoCanonShort, Instrument = "MCL" }
        });
        if (!rOco.LinkBrokerOrderIdAlias(ocoNativeLong, ocoCanonLong))
            return (false, "26: LinkBrokerOrderIdAlias long leg failed");
        if (!rOco.LinkBrokerOrderIdAlias(ocoNativeShort, ocoCanonShort))
            return (false, "26: LinkBrokerOrderIdAlias short leg failed");
        // Drive updates via native ids (as after ORDER_REGISTRY_BROKER_ID_LINKED in production).
        rOco.UpdateLifecycle(ocoNativeLong, OrderLifecycleState.WORKING, ocoTs);
        rOco.UpdateLifecycle(ocoNativeShort, OrderLifecycleState.WORKING, ocoTs);
        var ocoTrusted = rOco.GetMismatchTrustedWorkingCount();
        if (ocoTrusted != 2)
            return (false, $"26a: OCO both WORKING (linked natives + non-null ts): expected trusted count 2, got {ocoTrusted}");
        rOco.UpdateLifecycle(ocoNativeLong, OrderLifecycleState.PART_FILLED, ocoTs.AddMilliseconds(1));
        ocoTrusted = rOco.GetMismatchTrustedWorkingCount();
        if (ocoTrusted != 2)
            return (false, $"26b: OCO one PART_FILLED + one WORKING: expected trusted count 2, got {ocoTrusted}");

        return (true, null);
    }
}
