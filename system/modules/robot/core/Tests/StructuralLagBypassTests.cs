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

            e = Case_MarketReentry_CanonicalFlatRunnerJournalLag_AllowsStructural();
            if (e != null) return (false, e);

            e = Case_MarketReentry_SiblingTransitionNonFlatLag_AllowsStructural();
            if (e != null) return (false, e);

            e = Case_RiskCoverage_Target_PendingAlignmentPartialJournalLag_AllowsStructural();
            if (e != null) return (false, e);

            e = Case_Flatten_BrokerFlatJournalLag_IsNoOpNotParityDeny();
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

    private static string? Case_MarketReentry_CanonicalFlatRunnerJournalLag_AllowsStructural()
    {
        JournalParityPendingLedger.Clear();
        var prevLedger = FeatureFlags.StructuralLayerUseLedgerOwnership;
        FeatureFlags.StructuralLayerUseLedgerOwnership = true;
        var utc = DateTimeOffset.Parse("2099-07-07T12:00:00Z");
        var inst = "MES_REENTRY_LAG";
        JournalParityPendingLedger.TryRecordTrustedFill(inst, "dedupe-reentry-flat", 1, "intent-flat", utc);

        var root = Path.Combine(Path.GetTempPath(), "struct_lag_reentry_flat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            journal.RecordSubmission("intent-open", "2099-07-07", "S2", inst, "ENTRY", "broker-entry", utc);
            journal.RecordEntryFill("intent-open", "2099-07-07", "S2", 100m, 2, utc, 1m, "Short", inst, inst);

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utc
            };

            var parity = JournalParityChecker.CheckJournalParity(inst, snap, journal, new TestRegistryView(false, 0), inst, utc);
            if (parity.Status != JournalParityStatus.POSITION_MISMATCH)
                return $"expected POSITION_MISMATCH for broker-flat runner-journal lag, got {parity.Status}";
            if (!PendingAlignmentAuthority.IsPendingAlignment(inst, utc))
                return "expected PendingAlignmentAuthority true for trusted flat catch-up";

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
                JournalIntegrityOrReconciliationRepairActive = false,
                LedgerOwnershipSnapshot = new InstrumentOwnershipSnapshot
                {
                    Account = "default",
                    ExecutionInstrumentKey = inst,
                    OwnershipVersion = 1,
                    LedgerSignedNetQty = 0,
                    SnapshotUtc = utc
                }
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var denyEntry))
                return "expected plain SUBMIT_ENTRY deny under broker-flat journal lag";
            if (denyEntry.Reason != ExecutionStructuralLayer.StructuralBlocker.ParityNotOk)
                return "expected SUBMIT_ENTRY parity_not_ok, got " + denyEntry.Reason;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_MARKET_REENTRY", false, out var ok))
                return "expected market reentry allow under canonical-flat runner-journal lag, got " + ok.Reason;
            if (ok.Detail == null || ok.Detail.IndexOf("market_reentry_structural_bookkeeping_lag_bypass_canonical_flat_runner_journal_lag", StringComparison.Ordinal) < 0)
                return "expected canonical-flat runner-journal lag detail, got " + ok.Detail;
            return null;
        }
        finally
        {
            FeatureFlags.StructuralLayerUseLedgerOwnership = prevLedger;
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

    private static string? Case_MarketReentry_SiblingTransitionNonFlatLag_AllowsStructural()
    {
        JournalParityPendingLedger.Clear();
        var prevLedger = FeatureFlags.StructuralLayerUseLedgerOwnership;
        FeatureFlags.StructuralLayerUseLedgerOwnership = true;
        var utc = DateTimeOffset.Parse("2099-07-07T12:00:00Z");
        var inst = "MES_REENTRY_SIBLING";
        JournalParityPendingLedger.TryRecordTrustedFill(inst, "dedupe-reentry-sibling", 1, "intent-sibling", utc);

        var root = Path.Combine(Path.GetTempPath(), "struct_lag_reentry_sibling_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = -2 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utc
            };

            var parity = JournalParityChecker.CheckJournalParity(inst, snap, journal, new TestRegistryView(false, 0), inst, utc);
            if (parity.Status != JournalParityStatus.POSITION_MISMATCH)
                return $"expected POSITION_MISMATCH for sibling-transition non-flat lag, got {parity.Status}";
            if (!PendingAlignmentAuthority.IsPendingAlignment(inst, utc))
                return "expected PendingAlignmentAuthority true for sibling-transition non-flat lag";

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
                JournalIntegrityOrReconciliationRepairActive = false,
                LedgerOwnershipSnapshot = new InstrumentOwnershipSnapshot
                {
                    Account = "default",
                    ExecutionInstrumentKey = inst,
                    OwnershipVersion = 1,
                    LedgerSignedNetQty = 0,
                    SnapshotUtc = utc
                }
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var denyEntry))
                return "expected plain SUBMIT_ENTRY deny under sibling-transition non-flat lag";
            if (denyEntry.Reason != ExecutionStructuralLayer.StructuralBlocker.ParityNotOk)
                return "expected SUBMIT_ENTRY parity_not_ok, got " + denyEntry.Reason;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_MARKET_REENTRY", false, out var ok))
                return "expected market reentry allow under sibling-transition non-flat lag, got " + ok.Reason;
            if (ok.Detail == null || ok.Detail.IndexOf("market_reentry_structural_bookkeeping_lag_bypass_sibling_transition_nonflat", StringComparison.Ordinal) < 0)
                return "expected sibling-transition non-flat lag detail, got " + ok.Detail;
            return null;
        }
        finally
        {
            FeatureFlags.StructuralLayerUseLedgerOwnership = prevLedger;
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

    private static string? Case_RiskCoverage_Target_PendingAlignmentPartialJournalLag_AllowsStructural()
    {
        JournalParityPendingLedger.Clear();
        var prevLedger = FeatureFlags.StructuralLayerUseLedgerOwnership;
        FeatureFlags.StructuralLayerUseLedgerOwnership = true;
        var utc = DateTimeOffset.Parse("2099-07-07T14:00:00Z");
        var inst = "MES_REENTRY_TARGET";
        JournalParityPendingLedger.TryRecordTrustedFill(inst, "dedupe-risk-coverage-partial", 2, "intent-pending", utc);

        var root = Path.Combine(Path.GetTempPath(), "struct_lag_risk_coverage_target_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            journal.RecordSubmission("intent-live", "2099-07-07", "S3", inst, "ENTRY", "broker-entry", utc);
            journal.RecordEntryFill("intent-live", "2099-07-07", "S3", 100m, 2, utc, 1m, "Short", inst, inst);

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = inst, Quantity = 4 } },
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utc
            };

            var parity = JournalParityChecker.CheckJournalParity(inst, snap, journal, new TestRegistryView(false, 0), inst, utc);
            if (parity.Status != JournalParityStatus.POSITION_MISMATCH)
                return $"expected POSITION_MISMATCH for broker-ahead partial journal lag, got {parity.Status}";
            if (!PendingAlignmentAuthority.IsPendingAlignment(inst, utc))
                return "expected PendingAlignmentAuthority true for trusted second-fill catch-up";

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
                JournalIntegrityOrReconciliationRepairActive = false,
                LedgerOwnershipSnapshot = new InstrumentOwnershipSnapshot
                {
                    Account = "default",
                    ExecutionInstrumentKey = inst,
                    OwnershipVersion = 1,
                    LedgerSignedNetQty = -2,
                    SnapshotUtc = utc
                }
            };

            if (ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_ENTRY", false, out var denyEntry))
                return "expected plain SUBMIT_ENTRY deny under partial journal lag";
            if (denyEntry.Reason != ExecutionStructuralLayer.StructuralBlocker.ParityNotOk)
                return "expected SUBMIT_ENTRY parity_not_ok, got " + denyEntry.Reason;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_TARGET", false, out var okTgt))
                return "expected target allow during pending-alignment partial journal lag, got " + okTgt.Reason;
            if (okTgt.Detail == null || okTgt.Detail.IndexOf("risk_coverage_pending_alignment_position_mismatch_bypass_partial_journal", StringComparison.Ordinal) < 0)
                return "expected pending-alignment risk coverage detail, got " + okTgt.Detail;
            if (okTgt.Detail.IndexOf("risk_coverage_authority_unknown_bypass_partial_journal", StringComparison.Ordinal) < 0)
                return "expected authority bypass detail for target, got " + okTgt.Detail;
            if (okTgt.Detail.IndexOf("no_active_exposures_bypass_submittable_target_with_broker_position", StringComparison.Ordinal) < 0)
                return "expected no-active-exposures target bypass detail, got " + okTgt.Detail;

            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(req, "SUBMIT_PROTECTIVE_STOP", false, out var okProt))
                return "expected protective stop allow during pending-alignment partial journal lag, got " + okProt.Reason;
            if (okProt.Detail == null || okProt.Detail.IndexOf("risk_coverage_pending_alignment_position_mismatch_bypass_partial_journal", StringComparison.Ordinal) < 0)
                return "expected protective pending-alignment risk coverage detail, got " + okProt.Detail;

            return null;
        }
        finally
        {
            FeatureFlags.StructuralLayerUseLedgerOwnership = prevLedger;
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

    private static string? Case_Flatten_BrokerFlatJournalLag_IsNoOpNotParityDeny()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.Parse("2099-07-07T15:00:00Z");
        const string inst = "MES_FLAT_NOOP";
        var root = Path.Combine(Path.GetTempPath(), "struct_flat_noop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            journal.RecordSubmission("intent-flat-noop", "2099-07-07", "S4", inst, "ENTRY", "broker-entry", utc);
            journal.RecordEntryFill("intent-flat-noop", "2099-07-07", "S4", 100m, 2, utc, 1m, "Long", inst, inst);

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
                JournalIntegrityOrReconciliationRepairActive = false
            };

            if (ExecutionStructuralLayer.TryEvaluateFlattenStructure(req, out var s, out var reason))
                return "expected flatten no-op when broker is flat";
            if (reason != "broker_flat" || s.Reason != "broker_flat")
                return $"expected broker_flat no-op, got reason={reason} snap={s.Reason}";
            if (s.BrokerQty != 0 || s.JournalQty != 2)
                return $"expected broker 0 / journal 2 facts, got broker={s.BrokerQty} journal={s.JournalQty}";
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
