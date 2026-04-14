// End-to-end: SUBMIT_PROTECTIVE_STOP through NinjaTraderSimAdapter order-submit safety gate during pending-alignment lag.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test EXECUTION_SAFETY_PROTECTIVE_INTEGRATION

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class TryExecutionSafetyGateProtectiveIntegrationTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var prevAlign = FeatureFlags.EnablePostFillAlignmentGate;
        var prevLag = FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag;
        try
        {
            FeatureFlags.EnablePostFillAlignmentGate = true;
            FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag = true;
            ExecutionSafetyGate.ClearAllUnsafeLocksForTests();
            JournalParityPendingLedger.Clear();

            var utc = DateTimeOffset.UtcNow;
            const string inst = "ES";
            // Under-explained trusted fill: gate cumulative 1 vs broker-journal delta 2 → POSITION_MISMATCH; ledger non-empty → pending alignment.
            JournalParityPendingLedger.TryRecordTrustedFill(inst, "dedupe-integ", 1, "intent-integ", utc);

            var root = Path.Combine(Path.GetTempPath(), "nt_exec_safe_integ_" + Guid.NewGuid().ToString("N"));
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

                var parityProbe = JournalParityChecker.CheckJournalParity(inst, snap, journal,
                    new TestRegistry(false, 0), inst, utc);
                if (parityProbe.Status != JournalParityStatus.POSITION_MISMATCH)
                    return (false, $"precondition: expected POSITION_MISMATCH, got {parityProbe.Status}");
                if (!PendingAlignmentAuthority.IsPendingAlignment(inst, utc))
                    return (false, "precondition: expected pending alignment");

                var adapter = new NinjaTraderSimAdapter(root, root, log, journal);
                // Positional only: modules names this isExecutionAllowedCallback; RobotCore uses isRecoveryExecutionAllowedCallback — same slot.
                adapter.SetEngineCallbacks(
                    null,
                    null,
                    () => true,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    _ => true,
                    () => false,
                    _ => false,
                    (_, _) => false);

                adapter.SetExecutionSafetyTestAccountSnapshotFactory(_ => snap);

                if (!adapter.TryExecutionSafetyStructuralEvaluationFromAdapterRequest(
                        "intent-x", inst, "SUBMIT_PROTECTIVE_STOP", utc, out var structSnap))
                    return (false, "structural-only evaluation unexpectedly failed");

                if (!structSnap.NoActiveExposuresWithBrokerPosition)
                    return (false, "expected NoActiveExposuresWithBrokerPosition (no coordinator + broker open)");
                if (structSnap.Detail == null ||
                    structSnap.Detail.IndexOf("structural_bookkeeping_lag_bypass", StringComparison.Ordinal) < 0)
                    return (false, "expected Detail to contain structural_bookkeeping_lag_bypass, got " + structSnap.Detail);
                if (structSnap.Detail.IndexOf("no_active_exposures_bypass_submittable_protective_with_broker_position", StringComparison.Ordinal) < 0)
                    return (false, "expected protective exposure bypass token in Detail, got " + structSnap.Detail);

                if (!adapter.TryExecutionSafetyGateForOrderSubmitIntegration(
                        "intent-x", inst, "SUBMIT_PROTECTIVE_STOP", utc, out var failOk))
                    return (false, "full gate unexpectedly denied: " + failOk?.ErrorMessage);
                if (failOk != null)
                    return (false, "expected null failure OrderSubmissionResult on allow");

                FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag = false;
                if (adapter.TryExecutionSafetyGateForOrderSubmitIntegration(
                        "intent-x", inst, "SUBMIT_PROTECTIVE_STOP", utc, out var failDeny))
                    return (false, "expected full gate deny when lag bypass flag off");
                if (failDeny == null || string.IsNullOrEmpty(failDeny.ErrorMessage))
                    return (false, "expected failure payload when denied");
                if (failDeny.ErrorMessage.IndexOf("EXECUTION_BLOCKED_UNSAFE_STATE", StringComparison.Ordinal) < 0)
                    return (false, "expected EXECUTION_BLOCKED_UNSAFE_STATE when denied, got " + failDeny.ErrorMessage);

                return (true, null);
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
        finally
        {
            FeatureFlags.EnablePostFillAlignmentGate = prevAlign;
            FeatureFlags.StructuralLayerAllowSubmitDuringPendingAlignmentLag = prevLag;
            JournalParityPendingLedger.Clear();
            ExecutionSafetyGate.ClearAllUnsafeLocksForTests();
        }
    }

    private sealed class TestRegistry : IJournalParityRegistryView
    {
        public TestRegistry(bool useIea, int ieaOwnedPlus)
        {
            UseInstrumentExecutionAuthority = useIea;
            IeaOwnedPlusAdoptedWorking = ieaOwnedPlus;
        }

        public bool UseInstrumentExecutionAuthority { get; }
        public int IeaOwnedPlusAdoptedWorking { get; }
    }
}
