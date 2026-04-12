// Unit tests for Phase 3: Deterministic Recovery.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test RECOVERY_PHASE3
//
// Verifies: RecoveryReconstructor classification, decision rules.

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class RecoveryPhase3Tests
{
    public static (bool Pass, string? Error) RunRecoveryPhase3Tests()
    {
        // 1. Clean: broker flat, journal flat, no unowned
        var reg = new RecoveryRegistrySnapshot { UnownedLiveCount = 0, WorkingOrderIds = new System.Collections.Generic.List<string>() };
        var runtime = new RecoveryRuntimeIntentSnapshot { ActiveIntentIds = new System.Collections.Generic.List<string>() };
        var r = RecoveryReconstructor.Reconstruct("MES", 0, 0, 0, reg, runtime);
        if (r.Classification != ReconstructionClassification.CLEAN)
            return (false, $"Clean case: expected CLEAN, got {r.Classification}");

        // 2. Position-only mismatch: broker 2, journal 2 (no mismatch) -> CLEAN
        r = RecoveryReconstructor.Reconstruct("MES", 2, 0, 2, reg, runtime);
        if (r.Classification != ReconstructionClassification.CLEAN)
            return (false, $"Broker=2 journal=2: expected CLEAN, got {r.Classification}");

        // 3. Position mismatch: broker 2, journal 0
        r = RecoveryReconstructor.Reconstruct("MES", 2, 0, 0, reg, runtime);
        if (r.Classification != ReconstructionClassification.POSITION_ONLY_MISMATCH)
            return (false, $"Broker=2 journal=0: expected POSITION_ONLY_MISMATCH, got {r.Classification}");

        // 4. Unowned live -> LIVE_ORDER_OWNERSHIP_MISMATCH
        reg = new RecoveryRegistrySnapshot { UnownedLiveCount = 1, WorkingOrderIds = new System.Collections.Generic.List<string> { "o1" } };
        r = RecoveryReconstructor.Reconstruct("MES", 0, 1, 0, reg, runtime);
        if (r.Classification != ReconstructionClassification.LIVE_ORDER_OWNERSHIP_MISMATCH)
            return (false, $"Unowned live: expected LIVE_ORDER_OWNERSHIP_MISMATCH, got {r.Classification}");

        // 5. Trigger MANUAL -> MANUAL_INTERVENTION_DETECTED
        reg = new RecoveryRegistrySnapshot { UnownedLiveCount = 0 };
        r = RecoveryReconstructor.Reconstruct("MES", 1, 0, 0, reg, runtime, "MANUAL_OR_EXTERNAL_ORDER");
        if (r.Classification != ReconstructionClassification.MANUAL_INTERVENTION_DETECTED)
            return (false, $"Trigger MANUAL: expected MANUAL_INTERVENTION_DETECTED, got {r.Classification}");

        // 6. Trigger RECONCILIATION -> JOURNAL_BROKER_MISMATCH
        r = RecoveryReconstructor.Reconstruct("MES", 2, 0, 0, reg, runtime, "RECONCILIATION_QTY_MISMATCH");
        if (r.Classification != ReconstructionClassification.JOURNAL_BROKER_MISMATCH)
            return (false, $"Trigger RECONCILIATION: expected JOURNAL_BROKER_MISMATCH, got {r.Classification}");

        // 7. Trigger REGISTRY -> LIVE_ORDER_OWNERSHIP_MISMATCH
        r = RecoveryReconstructor.Reconstruct("MES", 0, 0, 0, reg, runtime, "REGISTRY_BROKER_DIVERGENCE");
        if (r.Classification != ReconstructionClassification.LIVE_ORDER_OWNERSHIP_MISMATCH)
            return (false, $"Trigger REGISTRY: expected LIVE_ORDER_OWNERSHIP_MISMATCH, got {r.Classification}");

        // 8. Adoption possible: adopted count > 0, no unowned, no mismatch
        reg = new RecoveryRegistrySnapshot { UnownedLiveCount = 0, AdoptedCount = 1, WorkingOrderIds = new System.Collections.Generic.List<string> { "o1" } };
        r = RecoveryReconstructor.Reconstruct("MES", 1, 1, 1, reg, runtime);
        if (r.Classification != ReconstructionClassification.ADOPTION_POSSIBLE)
            return (false, $"Adoption possible: expected ADOPTION_POSSIBLE, got {r.Classification}");

        return (true, null);
    }
}
