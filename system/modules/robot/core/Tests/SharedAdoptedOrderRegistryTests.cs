// Unit tests for SharedAdoptedOrderRegistry (cross-instance adopted-order fill journaling).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test SHARED_ADOPTED_ORDER_REGISTRY
//
// Verifies: register/resolve, idempotent registration, unknown order fail-closed, repeated resolve does not double-journal.

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class SharedAdoptedOrderRegistryTests
{
    public static (bool Pass, string? Error) RunSharedAdoptedOrderRegistryTests()
    {
        try
        {
            // 1. Register and resolve - adopted order resolved via shared lookup
            SharedAdoptedOrderRegistry.Register("broker-001", "intent-A", "M2K", "2026-03-17", "RTY2", isEntryOrder: true);
            if (!SharedAdoptedOrderRegistry.TryResolve("broker-001", out var r1) || r1 == null)
                return (false, "Register+resolve failed");
            if (r1.IntentId != "intent-A" || r1.Instrument != "M2K" || !r1.IsEntryOrder)
                return (false, $"Resolved record mismatch: intent={r1.IntentId} inst={r1.Instrument} isEntry={r1.IsEntryOrder}");

            // 2. Unknown order - TryResolve returns false (fail-closed)
            if (SharedAdoptedOrderRegistry.TryResolve("unknown-broker-999", out _))
                return (false, "Unknown order should not resolve");

            // 3. Idempotent registration - repeated registration overwrites
            SharedAdoptedOrderRegistry.Register("broker-002", "intent-B", "MNQ", null, null, isEntryOrder: true);
            SharedAdoptedOrderRegistry.Register("broker-002", "intent-B", "MNQ", null, null, isEntryOrder: true);
            if (!SharedAdoptedOrderRegistry.TryResolve("broker-002", out var r2) || r2 == null)
                return (false, "Idempotent registration failed");

            // 4. Repeated resolve - same result (no double-journal; caller dedupes)
            if (!SharedAdoptedOrderRegistry.TryResolve("broker-002", out var r2b) || r2b == null)
                return (false, "Repeated resolve failed");
            if (r2b.IntentId != r2.IntentId)
                return (false, "Repeated resolve returned different record");

            // 5. Case-insensitive broker order id
            SharedAdoptedOrderRegistry.Register("Broker-003", "intent-C", "MES", null, null, isEntryOrder: false);
            if (!SharedAdoptedOrderRegistry.TryResolve("broker-003", out var r3) || r3 == null)
                return (false, "Case-insensitive resolve failed");

            // 6. Empty broker order id - Register no-op
            SharedAdoptedOrderRegistry.Register("", "intent-D", "MES", null, null, isEntryOrder: true);
            if (SharedAdoptedOrderRegistry.TryResolve("", out _))
                return (false, "Empty broker order id should not resolve");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
