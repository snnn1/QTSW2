// QTSW2 OrderMap hydration rules + ORDER_REGISTRY_MISSING defer predicate (Phase 1/2 fixes).
// Run: dotnet run --project RobotCore_For_NinjaTrader/SiblingProtectiveCancelQueue.Test/SiblingProtectiveCancelQueue.Test.csproj -- QTSW2_HYDRATION_DEFER

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class Qtsw2HydrationDeferPolicyTests
{
    public static (bool Pass, string? Error) RunQtsw2HydrationDeferPolicyTests()
    {
        var d = RunDeferPredicateTests();
        if (!d.Pass) return d;
        var h = RunHydrationMatchTests();
        if (!h.Pass) return h;
        return RunUntaggedTagTests();
    }

    private static (bool Pass, string? Error) RunDeferPredicateTests()
    {
        if (ReconciliationDeferPolicy.ShouldDeferOrderRegistryMissingFailClosed(false, 2, 2))
            return (false, "defer: brokerWorking=2, robotTagged=2, ambiguous=false must NOT defer");

        if (!ReconciliationDeferPolicy.ShouldDeferOrderRegistryMissingFailClosed(false, 2, 1))
            return (false, "defer: robotTaggedWorking < brokerWorking must defer");

        if (!ReconciliationDeferPolicy.ShouldDeferOrderRegistryMissingFailClosed(true, 2, 2))
            return (false, "defer: ieaOwnershipAmbiguous=true must defer when brokerWorking>0");

        if (ReconciliationDeferPolicy.ShouldDeferOrderRegistryMissingFailClosed(false, 0, 0))
            return (false, "defer: brokerWorking=0 should not defer via robotTagged gap");

        return (true, null);
    }

    private static (bool Pass, string? Error) RunHydrationMatchTests()
    {
        var utc = DateTimeOffset.UtcNow;
        var workingStop = new OrderRegistryEntry
        {
            BrokerOrderId = "pre-ack",
            IntentId = "intent-X",
            Instrument = "MNQ",
            OrderRole = OrderRole.STOP,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.WORKING,
            CreatedUtc = utc,
            OrderInfo = new OrderInfo { OrderId = "pre-ack", Instrument = "MNQ", IntentId = "intent-X" }
        };

        if (!Qtsw2OrderUpdateHydrationPolicy.RegistryRowMatchesTaggedIntentAndLeg(workingStop, "intent-X", "STOP"))
            return (false, "hydration: STOP row + STOP leg should match");

        if (Qtsw2OrderUpdateHydrationPolicy.RegistryRowMatchesTaggedIntentAndLeg(workingStop, "intent-X", "TARGET"))
            return (false, "hydration: STOP row must not match TARGET leg");

        var entryRow = new OrderRegistryEntry
        {
            BrokerOrderId = "e1",
            IntentId = "intent-X",
            Instrument = "MNQ",
            OrderRole = OrderRole.ENTRY,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.WORKING,
            CreatedUtc = utc,
            OrderInfo = new OrderInfo { OrderId = "e1", Instrument = "MNQ", IntentId = "intent-X" }
        };

        if (Qtsw2OrderUpdateHydrationPolicy.RegistryRowMatchesTaggedIntentAndLeg(entryRow, "intent-X", "STOP"))
            return (false, "hydration: ENTRY row must not hydrate STOP-tagged update (alias fall-through guard)");

        if (!Qtsw2OrderUpdateHydrationPolicy.RegistryRowMatchesTaggedIntentAndLeg(entryRow, "intent-X", null))
            return (false, "hydration: base tag (null leg) should match ENTRY row");

        if (!Qtsw2OrderUpdateHydrationPolicy.RegistryRowMatchesTaggedIntentAndLeg(entryRow, "intent-X", "ENTRY"))
            return (false, "hydration: ENTRY leg should match ENTRY row");

        if (!Qtsw2OrderUpdateHydrationPolicy.IsTerminalRegistryRow(new OrderRegistryEntry
            {
                LifecycleState = OrderLifecycleState.FILLED,
                OwnershipStatus = OrderOwnershipStatus.TERMINAL,
                OrderInfo = entryRow.OrderInfo
            }))
            return (false, "terminal: FILLED + TERMINAL ownership should be terminal");

        if (Qtsw2OrderUpdateHydrationPolicy.IsTerminalRegistryRow(workingStop))
            return (false, "terminal: WORKING OWNED row must not be terminal");

        var filledOnly = new OrderRegistryEntry
        {
            BrokerOrderId = "f",
            IntentId = "i",
            Instrument = "MNQ",
            OrderRole = OrderRole.STOP,
            OwnershipStatus = OrderOwnershipStatus.OWNED,
            LifecycleState = OrderLifecycleState.FILLED,
            CreatedUtc = utc,
            OrderInfo = workingStop.OrderInfo
        };
        if (!Qtsw2OrderUpdateHydrationPolicy.IsTerminalRegistryRow(filledOnly))
            return (false, "terminal: FILLED lifecycle must be terminal for hydration gate");

        return (true, null);
    }

    private static (bool Pass, string? Error) RunUntaggedTagTests()
    {
        if (RobotOrderIds.DecodeIntentId("MANUAL:foo") != null)
            return (false, "untagged: non-QTSW2 tag must not yield intent id");

        if (RobotOrderIds.ParseTag("MANUAL:foo").IntentId != null)
            return (false, "untagged: ParseTag should not assign intent for non-QTSW2");

        return (true, null);
    }
}
