// Unit tests for Phase 4: Crash/Disconnect Determinism.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test BOOTSTRAP_PHASE4
//
// Verifies: BootstrapClassifier, BootstrapDecider, BootstrapSnapshot.

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class BootstrapPhase4Tests
{
    public static (bool Pass, string? Error) RunBootstrapPhase4Tests()
    {
        // 1. Clean startup (no position, no orders) -> RESUME
        var snap = new BootstrapSnapshot
        {
            Instrument = "MES",
            BrokerPositionQty = 0,
            BrokerWorkingOrderCount = 0,
            JournalQty = 0,
            UnownedLiveOrderCount = 0,
            RegistrySnapshot = new RecoveryRegistrySnapshot { UnownedLiveCount = 0, WorkingOrderIds = new System.Collections.Generic.List<string>() }
        };
        var c = BootstrapClassifier.Classify(snap);
        if (c != BootstrapClassification.CLEAN_START)
            return (false, $"Clean: expected CLEAN_START, got {c}");
        var d = BootstrapDecider.Decide(c, snap);
        if (d != BootstrapDecision.RESUME)
            return (false, $"Clean: expected RESUME, got {d}");

        // 2. Resume with no position no orders (journal has qty but broker flat)
        snap = new BootstrapSnapshot
        {
            Instrument = "MES",
            BrokerPositionQty = 0,
            BrokerWorkingOrderCount = 0,
            JournalQty = 1,
            UnownedLiveOrderCount = 0,
            RegistrySnapshot = new RecoveryRegistrySnapshot { UnownedLiveCount = 0 }
        };
        c = BootstrapClassifier.Classify(snap);
        if (c != BootstrapClassification.RESUME_WITH_NO_POSITION_NO_ORDERS)
            return (false, $"Journal qty no broker: expected RESUME_WITH_NO_POSITION_NO_ORDERS, got {c}");
        d = BootstrapDecider.Decide(c, snap);
        if (d != BootstrapDecision.RESUME)
            return (false, $"Resume no position: expected RESUME, got {d}");

        // 3. Manual intervention (unowned live) -> HALT
        snap = new BootstrapSnapshot
        {
            Instrument = "MES",
            BrokerPositionQty = 1,
            BrokerWorkingOrderCount = 1,
            JournalQty = 1,
            UnownedLiveOrderCount = 1,
            RegistrySnapshot = new RecoveryRegistrySnapshot { UnownedLiveCount = 1 }
        };
        c = BootstrapClassifier.Classify(snap);
        if (c != BootstrapClassification.MANUAL_INTERVENTION_PRESENT)
            return (false, $"Unowned live: expected MANUAL_INTERVENTION_PRESENT, got {c}");
        d = BootstrapDecider.Decide(c, snap);
        if (d != BootstrapDecision.HALT)
            return (false, $"Manual: expected HALT, got {d}");

        // 4. Position present no owned orders (broker matches journal, no working orders) -> FLATTEN_THEN_RECONSTRUCT
        snap = new BootstrapSnapshot
        {
            Instrument = "MES",
            BrokerPositionQty = 2,
            BrokerWorkingOrderCount = 0,
            JournalQty = 2,
            UnownedLiveOrderCount = 0,
            RegistrySnapshot = new RecoveryRegistrySnapshot { UnownedLiveCount = 0 }
        };
        c = BootstrapClassifier.Classify(snap);
        if (c != BootstrapClassification.POSITION_PRESENT_NO_OWNED_ORDERS)
            return (false, $"Position no orders: expected POSITION_PRESENT_NO_OWNED_ORDERS, got {c}");
        d = BootstrapDecider.Decide(c, snap);
        if (d != BootstrapDecision.FLATTEN_THEN_RECONSTRUCT)
            return (false, $"Position no orders: expected FLATTEN_THEN_RECONSTRUCT, got {d}");

        // 5. Adoption required (broker working, journal qty, no unowned, no owned/adopted in registry)
        snap = new BootstrapSnapshot
        {
            Instrument = "MES",
            BrokerPositionQty = 1,
            BrokerWorkingOrderCount = 1,
            JournalQty = 1,
            UnownedLiveOrderCount = 0,
            RegistrySnapshot = new RecoveryRegistrySnapshot { UnownedLiveCount = 0, OwnedCount = 0, AdoptedCount = 0, WorkingOrderIds = new System.Collections.Generic.List<string> { "o1" } }
        };
        c = BootstrapClassifier.Classify(snap);
        if (c != BootstrapClassification.ADOPTION_REQUIRED)
            return (false, $"Adoption required: expected ADOPTION_REQUIRED, got {c}");
        d = BootstrapDecider.Decide(c, snap);
        if (d != BootstrapDecision.ADOPT)
            return (false, $"Adoption: expected ADOPT, got {d}");

        // 6. Journal runtime divergence -> FLATTEN_THEN_RECONSTRUCT
        snap = new BootstrapSnapshot
        {
            Instrument = "MES",
            BrokerPositionQty = 2,
            BrokerWorkingOrderCount = 0,
            JournalQty = 1,
            UnownedLiveOrderCount = 0,
            RegistrySnapshot = new RecoveryRegistrySnapshot { UnownedLiveCount = 0 }
        };
        c = BootstrapClassifier.Classify(snap);
        if (c != BootstrapClassification.JOURNAL_RUNTIME_DIVERGENCE)
            return (false, $"Journal divergence: expected JOURNAL_RUNTIME_DIVERGENCE, got {c}");
        d = BootstrapDecider.Decide(c, snap);
        if (d != BootstrapDecision.FLATTEN_THEN_RECONSTRUCT)
            return (false, $"Journal divergence: expected FLATTEN_THEN_RECONSTRUCT, got {d}");

        // 7. Live orders present no position (no slot journal) -> FLATTEN_THEN_RECONSTRUCT
        snap = new BootstrapSnapshot
        {
            Instrument = "MES",
            BrokerPositionQty = 0,
            BrokerWorkingOrderCount = 1,
            JournalQty = 0,
            UnownedLiveOrderCount = 0,
            RegistrySnapshot = new RecoveryRegistrySnapshot { UnownedLiveCount = 0 },
            SlotJournalShowsEntryStopsExpected = false
        };
        c = BootstrapClassifier.Classify(snap);
        if (c != BootstrapClassification.LIVE_ORDERS_PRESENT_NO_POSITION)
            return (false, $"Live orders no position: expected LIVE_ORDERS_PRESENT_NO_POSITION, got {c}");
        d = BootstrapDecider.Decide(c, snap);
        if (d != BootstrapDecision.FLATTEN_THEN_RECONSTRUCT)
            return (false, $"Live orders no position: expected FLATTEN_THEN_RECONSTRUCT, got {d}");

        // 7b. Live orders present no position + slot journal shows RANGE_LOCKED+StopBracketsSubmittedAtLock -> ADOPT (restart fix)
        snap = new BootstrapSnapshot
        {
            Instrument = "MES",
            BrokerPositionQty = 0,
            BrokerWorkingOrderCount = 1,
            JournalQty = 0,
            UnownedLiveOrderCount = 0,
            RegistrySnapshot = new RecoveryRegistrySnapshot { UnownedLiveCount = 0 },
            SlotJournalShowsEntryStopsExpected = true
        };
        c = BootstrapClassifier.Classify(snap);
        if (c != BootstrapClassification.LIVE_ORDERS_PRESENT_NO_POSITION)
            return (false, $"Live orders + slot journal: expected LIVE_ORDERS_PRESENT_NO_POSITION, got {c}");
        d = BootstrapDecider.Decide(c, snap);
        if (d != BootstrapDecision.ADOPT)
            return (false, $"Live orders + slot journal: expected ADOPT (restart fix), got {d}");

        // 8. Unknown requires supervisor (ambiguous state) -> HALT
        snap = new BootstrapSnapshot
        {
            Instrument = "MES",
            BrokerPositionQty = 1,
            BrokerWorkingOrderCount = 1,
            JournalQty = 1,
            UnownedLiveOrderCount = 0,
            RegistrySnapshot = null
        };
        c = BootstrapClassifier.Classify(snap);
        if (c != BootstrapClassification.UNKNOWN_REQUIRES_SUPERVISOR)
            return (false, $"Ambiguous: expected UNKNOWN_REQUIRES_SUPERVISOR, got {c}");
        d = BootstrapDecider.Decide(c, snap);
        if (d != BootstrapDecision.HALT)
            return (false, $"Unknown requires supervisor: expected HALT, got {d}");

        return (true, null);
    }
}
