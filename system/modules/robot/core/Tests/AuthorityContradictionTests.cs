// Contradiction / single-truth tests: staged conflicts must not yield two incompatible authoritative outcomes.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test AUTHORITY_CONTRADICTIONS

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class AuthorityContradictionTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var e = Case_MismatchBlockVsBrokerFlattenTruth();
        if (e != null) return (false, e);
        e = Case_RecoveryVsStructuralExecution();
        if (e != null) return (false, e);
        e = Case_BrokerVsJournalSingleParityClassification();
        if (e != null) return (false, e);
        return (true, null);
    }

    /// <summary>
    /// Mismatch block (G1/EPA) denies risk-increasing submits; broker canonical exposure still shows risk — do not
    /// treat journal-only or log-only “flat” as overriding reconciliation abs quantity.
    /// </summary>
    private static string? Case_MismatchBlockVsBrokerFlattenTruth()
    {
        if (ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight(() => false, _ => true, _ => false, "MES", out var deny) ||
            deny != "MISMATCH_EXECUTION_BLOCK")
            return "expected EPA deny MISMATCH_EXECUTION_BLOCK when mismatch callback true";

        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "MES", Quantity = 2 } },
            WorkingOrders = new List<WorkingOrderSnapshot>(),
            CapturedAtUtc = DateTimeOffset.UtcNow
        };
        var exposure = BrokerPositionResolver.ResolveFromSnapshots(snap.Positions, "MES");
        // Official flatten-complete (G4) is broker-model zero; non-zero => not complete — single normative story.
        if (exposure.ReconciliationAbsQuantityTotal == 0)
            return "expected non-zero reconciliation abs for open broker position";
        return null;
    }

    /// <summary>
    /// Recovery disallows structural RI path — single denial reason (repair_active), not a competing “allow” from parity OK.
    /// </summary>
    private static string? Case_RecoveryVsStructuralExecution()
    {
        var utc = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "auth_contra_rec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utc
            };
            var req = new ExecutionSafetyEvaluationRequest
            {
                Instrument = "MES",
                CanonicalInstrument = "MES",
                UtcNow = utc,
                Journal = journal,
                AccountSnapshot = snap,
                UseInstrumentExecutionAuthority = false,
                IeaOwnedPlusAdoptedWorking = 0,
                RecoveryExecutionDisallowed = true,
                JournalIntegrityOrReconciliationRepairActive = false
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var s) ||
                s.Reason != "repair_active")
                return "expected structural deny repair_active when recovery disallows execution";

            // EPA layer alone would not encode recovery; structural outcome is the single RI submit story here.
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Broker shows position and journal is empty — exactly one parity classification (POSITION_MISMATCH), not PARITY_OK.
    /// </summary>
    private static string? Case_BrokerVsJournalSingleParityClassification()
    {
        var utc = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), "auth_contra_j_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 3 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utc
            };
            var reg = new TestRegistryView(false, 0);
            var r = JournalParityChecker.CheckJournalParity("ES", snap, journal, reg, "ES", utc);
            if (r.Status != JournalParityStatus.POSITION_MISMATCH)
                return "expected single classification POSITION_MISMATCH when broker open and journal empty";
            if (r.IsOk || r.IsOkOrPendingAlignment)
                return "did not expect OK or pending-alignment flags for raw position mismatch";
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch { /* best effort */ }
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
