// Phase 3: parity_not_ok and repair-active submit demotion (feature-flagged).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test STRUCTURAL_PHASE3_DEMOTION

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class StructuralPhase3DemotionTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var prevParity = FeatureFlags.StructuralLayerPhase3DemoteParityNotOkSubmitDeny;
        var prevRepair = FeatureFlags.StructuralLayerPhase3DemoteRepairActiveSubmitDeny;
        var prevLag = FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag;
        try
        {
            FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag = false;

            var e = Case_ParityMismatch_DemoteFlag_AllowsSubmit();
            if (e != null) return (false, e);

            e = Case_RepairLatch_DemoteFlag_AllowsSubmit();
            if (e != null) return (false, e);

            e = Case_FlagsOff_StillDenies();
            if (e != null) return (false, e);

            return (true, null);
        }
        finally
        {
            FeatureFlags.StructuralLayerPhase3DemoteParityNotOkSubmitDeny = prevParity;
            FeatureFlags.StructuralLayerPhase3DemoteRepairActiveSubmitDeny = prevRepair;
            FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag = prevLag;
            JournalParityPendingLedger.Clear();
        }
    }

    /// <summary>POSITION_MISMATCH with no pending-alignment bypass — demotion allows structural pass.</summary>
    private static string? Case_ParityMismatch_DemoteFlag_AllowsSubmit()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.Parse("2099-08-01T12:00:00Z");
        var inst = "PH3_P";
        var root = Path.Combine(Path.GetTempPath(), "ph3_p_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = 2 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utc
            };

            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = inst,
                CanonicalInstrument = inst,
                UtcNow = utc,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = false,
                JournalIntegrityOrReconciliationRepairActive = false
            };

            FeatureFlags.StructuralLayerPhase3DemoteParityNotOkSubmitDeny = false;
            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out _))
                return "expected deny parity when demote off";

            FeatureFlags.StructuralLayerPhase3DemoteParityNotOkSubmitDeny = true;
            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out var ok))
                return "expected allow when parity demote on, got " + ok.Reason;
            if (ok.Detail == null || ok.Detail.IndexOf("phase3_parity_not_ok_demoted", StringComparison.Ordinal) < 0)
                return "expected Detail to mention phase3_parity_not_ok_demoted, got " + ok.Detail;
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static string? Case_RepairLatch_DemoteFlag_AllowsSubmit()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.Parse("2099-08-02T12:00:00Z");
        var inst = "PH3_R";
        var root = Path.Combine(Path.GetTempPath(), "ph3_r_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = 0 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utc
            };

            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = inst,
                CanonicalInstrument = inst,
                UtcNow = utc,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = false,
                JournalIntegrityOrReconciliationRepairActive = true
            };

            FeatureFlags.StructuralLayerPhase3DemoteRepairActiveSubmitDeny = false;
            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out _))
                return "expected deny repair when demote off";

            FeatureFlags.StructuralLayerPhase3DemoteRepairActiveSubmitDeny = true;
            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out var ok))
                return "expected allow when repair demote on, got " + ok.Reason;
            if (ok.Detail == null || ok.Detail.IndexOf("phase3_repair_active_demoted", StringComparison.Ordinal) < 0)
                return "expected Detail to mention phase3_repair_active_demoted, got " + ok.Detail;
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static string? Case_FlagsOff_StillDenies()
    {
        JournalParityPendingLedger.Clear();
        FeatureFlags.StructuralLayerPhase3DemoteParityNotOkSubmitDeny = false;
        FeatureFlags.StructuralLayerPhase3DemoteRepairActiveSubmitDeny = false;
        var utc = DateTimeOffset.Parse("2099-08-03T12:00:00Z");
        var inst = "PH3_OFF";
        var root = Path.Combine(Path.GetTempPath(), "ph3_off_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = 2 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utc
            };
            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = inst,
                CanonicalInstrument = inst,
                UtcNow = utc,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = false,
                JournalIntegrityOrReconciliationRepairActive = false
            };
            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out var s))
            {
                if (s.Reason != ExecutionStructuralLayer.StructuralBlocker.ParityNotOk)
                    return "flags off: expected parity_not_ok, got " + s.Reason;
                return null;
            }

            return "flags off: expected structural deny";
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                /* best effort */
            }
        }
    }
}
