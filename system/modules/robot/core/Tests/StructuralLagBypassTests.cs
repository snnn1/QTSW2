// Structural layer: pending-alignment bookkeeping lag bypass vs parity/repair deny.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test STRUCTURAL_LAG_BYPASS

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class StructuralLagBypassTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var prevAlign = FeatureFlags.EnablePostFillAlignmentGate;
        var prevLag = FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag;
        try
        {
            FeatureFlags.EnablePostFillAlignmentGate = true;
            FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag = true;

            var e = Case_PositionMismatchWithPendingLedgerAndRepairLatch_AllowsStructural();
            if (e != null) return (false, e);

            e = Case_FlagOff_DeniesDespitePendingLedger();
            if (e != null) return (false, e);

            e = Case_RecoveryExecutionDisallowed_AlwaysDenies();
            if (e != null) return (false, e);

            e = Case_NoPendingAlignment_PositionMismatchStillDenies();
            if (e != null) return (false, e);

            return (true, null);
        }
        finally
        {
            FeatureFlags.EnablePostFillAlignmentGate = prevAlign;
            FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag = prevLag;
            JournalParityPendingLedger.Clear();
        }
    }

    /// <summary>
    /// Under-explained ledger (abs 1) + broker 2 vs journal 0 → POSITION_MISMATCH while pending ledger keeps
    /// <see cref="PendingAlignmentAuthority"/> true — structural submit must allow when journal repair latch is on.
    /// Uses <c>SUBMIT_PROTECTIVE_STOP</c> to avoid the separate no-exposure coordinator veto for entries.
    /// </summary>
    private static string? Case_PositionMismatchWithPendingLedgerAndRepairLatch_AllowsStructural()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.UtcNow;
        var inst = "ES";
        // Gate cumulative = 1; broker-journal delta = 2 → parity classifies POSITION_MISMATCH, not PARITY_PENDING_ALIGNMENT.
        JournalParityPendingLedger.TryRecordTrustedFill(inst, "dedupe-underexplain", 1, "intent-a", utc);

        var root = Path.Combine(Path.GetTempPath(), "struct_lag_" + Guid.NewGuid().ToString("N"));
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

            var parity = JournalParityChecker.CheckJournalParity(inst, snap, journal, new TestRegistryView(false, 0), inst, utc);
            if (parity.Status != JournalParityStatus.POSITION_MISMATCH)
                return $"expected POSITION_MISMATCH for under-explained ledger, got {parity.Status}";
            if (!PendingAlignmentAuthority.IsPendingAlignment(inst, utc))
                return "expected PendingAlignmentAuthority true with pending ledger entry";

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

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out var s))
                return "expected structural allow with repair latch + lag bypass, got deny: " + s.Reason;
            if (!string.IsNullOrEmpty(s.Reason))
                return "expected empty Reason on allow, got " + s.Reason;
            if (s.Detail == null || s.Detail.IndexOf("structural_bookkeeping_lag_bypass", StringComparison.Ordinal) < 0)
                return "expected Detail to mention structural_bookkeeping_lag_bypass, got " + s.Detail;
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

    private static string? Case_FlagOff_DeniesDespitePendingLedger()
    {
        JournalParityPendingLedger.Clear();
        FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag = false;
        var utc = DateTimeOffset.UtcNow;
        var inst = "ES";
        JournalParityPendingLedger.TryRecordTrustedFill(inst, "dedupe2", 1, "intent-b", utc);

        var root = Path.Combine(Path.GetTempPath(), "struct_lag_flag_" + Guid.NewGuid().ToString("N"));
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
                JournalIntegrityOrReconciliationRepairActive = true
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out var s))
                return "expected structural deny when lag bypass flag off";
            if (s.Reason != ExecutionStructuralLayer.StructuralBlocker.RepairActive)
                return "expected repair_active when flag off, got " + s.Reason;
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

    private static string? Case_RecoveryExecutionDisallowed_AlwaysDenies()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.UtcNow;
        var inst = "ES";
        JournalParityPendingLedger.TryRecordTrustedFill(inst, "dedupe3", 1, "intent-c", utc);

        var root = Path.Combine(Path.GetTempPath(), "struct_lag_rec_" + Guid.NewGuid().ToString("N"));
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

            FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag = true;
            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = inst,
                CanonicalInstrument = inst,
                UtcNow = utc,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = true,
                JournalIntegrityOrReconciliationRepairActive = false
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out var s))
                return "expected structural deny when recovery disallows execution";
            if (s.Reason != ExecutionStructuralLayer.StructuralBlocker.RepairActive || s.Detail != "recovery_execution_disallowed")
                return "expected recovery_execution_disallowed repair_active";
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

    private static string? Case_NoPendingAlignment_PositionMismatchStillDenies()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.UtcNow;
        var inst = "MES";

        var root = Path.Combine(Path.GetTempPath(), "struct_lag_nomis_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = 3 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utc
            };

            var parity = JournalParityChecker.CheckJournalParity(inst, snap, journal, new TestRegistryView(false, 0), inst, utc);
            if (parity.Status != JournalParityStatus.POSITION_MISMATCH)
                return "expected POSITION_MISMATCH without ledger";
            if (PendingAlignmentAuthority.IsPendingAlignment(inst, utc))
                return "did not expect pending alignment without ledger/gate";

            FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag = true;
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

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out var s))
                return "expected structural deny without pending alignment lag context";
            if (s.Reason != ExecutionStructuralLayer.StructuralBlocker.ParityNotOk)
                return "expected parity_not_ok, got " + s.Reason;
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

    private sealed class TestRegistryView : IJournalParityRegistryView
    {
        public TestRegistryView(bool useIea, int ieaOwnedPlus)
        {
            UseInstrumentExecutionAuthority = useIea;
            IeaOwnedPlusAdoptedWorking = ieaOwnedPlus;
        }

        public bool UseInstrumentExecutionAuthority { get; }
        public int IeaOwnedPlusAdoptedWorking { get; }
    }
}
