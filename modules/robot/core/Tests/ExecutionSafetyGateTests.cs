// Execution safety overlay + structural layer tests.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test EXECUTION_SAFETY_GATE

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ExecutionSafetyGateTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        ExecutionSafetyGate.ClearAllUnsafeLocksForTests();
        var e = Case_KillSwitchThenOverlayUnlock();
        if (e != null) return (false, e);
        ExecutionSafetyGate.ClearAllUnsafeLocksForTests();
        e = Case_StaleSnapshotBlocksOverlay();
        if (e != null) return (false, e);
        ExecutionSafetyGate.ClearAllUnsafeLocksForTests();
        e = Case_RecoveryBlocksStructuralLayer();
        if (e != null) return (false, e);
        ExecutionSafetyGate.ClearAllUnsafeLocksForTests();
        e = Case_DuplicateFlattenIdempotency();
        if (e != null) return (false, e);
        ExecutionSafetyGate.ClearAllUnsafeLocksForTests();
        e = Case_ManualUnlockDeniedWhenStaleSnapshot();
        if (e != null) return (false, e);
        ExecutionSafetyGate.ClearAllUnsafeLocksForTests();
        return (true, null);
    }

    private static ExecutionSafetyEvaluationRequest BaseReq(ExecutionJournal journal, DateTimeOffset utc, AccountSnapshot snap)
    {
        return new ExecutionSafetyEvaluationRequest
        {
            Instrument = "MES",
            CanonicalInstrument = "MES",
            UtcNow = utc,
            Journal = journal,
            AccountSnapshot = snap,
            UseInstrumentExecutionAuthority = false,
            IeaOwnedPlusAdoptedWorking = 0,
            RecoveryExecutionDisallowed = false,
            JournalIntegrityOrReconciliationRepairActive = false
        };
    }

    private static string? Case_KillSwitchThenOverlayUnlock()
    {
        var utc = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "esg_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = new RobotLogger(root);
        var journal = new ExecutionJournal(root, log);
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>(),
            CapturedAtUtc = utc
        };
        var req = BaseReq(journal, utc, snap);

        if (!ExecutionSafetyGate.TryEvaluateExecutionOverlay(req, out var clear1) || clear1.IsBlocked)
            return "expected overlay clear before kill switch";

        ExecutionSafetyGate.ApplyUnmappedExecutionKillSwitch("MES", "NO_ACTIVE_EXPOSURES", utc);
        if (ExecutionSafetyGate.TryEvaluateExecutionOverlay(req, out var oKill) || !oKill.IsBlocked ||
            oKill.BlockReason != ExecutionOverlayBlockReason.UNMAPPED_FILL)
            return "expected UNMAPPED_FILL overlay after kill switch";

        if (!ExecutionSafetyGate.TryManualClearUnsafeLockWhenOverlayAllows(req, out var denyUnlock))
            return "manual unlock should succeed with fresh snapshot: " + denyUnlock;

        if (!ExecutionSafetyGate.TryEvaluateExecutionOverlay(req, out var clear2) || clear2.IsBlocked)
            return "expected overlay clear after manual unlock";
        return null;
    }

    private static string? Case_ManualUnlockDeniedWhenStaleSnapshot()
    {
        var utc = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "esg_mm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = new RobotLogger(root);
        var journal = new ExecutionJournal(root, log);
        var staleTime = utc.AddMilliseconds(-(ExecutionSafetyGate.MaxBrokerSnapshotAgeMilliseconds + 500));
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>(),
            CapturedAtUtc = staleTime
        };
        var req = BaseReq(journal, utc, snap);
        ExecutionSafetyGate.ApplyUnmappedExecutionKillSwitch("MES", "TEST", utc);
        if (ExecutionSafetyGate.TryManualClearUnsafeLockWhenOverlayAllows(req, out _))
            return "unlock should fail when snapshot stale";
        return null;
    }

    private static string? Case_StaleSnapshotBlocksOverlay()
    {
        var utc = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "esg_stale_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = new RobotLogger(root);
        var journal = new ExecutionJournal(root, log);
        var staleTime = utc.AddMilliseconds(-(ExecutionSafetyGate.MaxBrokerSnapshotAgeMilliseconds + 500));
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>(),
            CapturedAtUtc = staleTime
        };
        var req = BaseReq(journal, utc, snap);
        if (ExecutionSafetyGate.TryEvaluateExecutionOverlay(req, out var o) || !o.IsBlocked ||
            o.BlockReason != ExecutionOverlayBlockReason.STALE_SNAPSHOT)
            return "expected STALE_SNAPSHOT overlay";
        return null;
    }

    private static string? Case_RecoveryBlocksStructuralLayer()
    {
        var utc = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "esg_rec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = new RobotLogger(root);
        var journal = new ExecutionJournal(root, log);
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>(),
            CapturedAtUtc = utc
        };
        var req = BaseReq(journal, utc, snap);
        req.RecoveryExecutionDisallowed = true;
        if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var s) ||
            s.Reason != "repair_active")
            return "expected repair_active from structural layer";
        return null;
    }

    private static string? Case_DuplicateFlattenIdempotency()
    {
        var utc = DateTimeOffset.UtcNow;
        ExecutionSafetyGate.RecordFlattenSubmitted("MES", 3, utc);
        if (!ExecutionSafetyGate.ShouldBlockRepeatedFlatten("MES", 3, utc.AddSeconds(1)))
            return "expected duplicate flatten block same broker abs";
        if (ExecutionSafetyGate.ShouldBlockRepeatedFlatten("MES", 2, utc.AddSeconds(1)))
            return "did not expect block when broker qty changed";
        return null;
    }
}
