using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class MarketReentrySubmitPathTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var prevWindowMs = FeatureFlags.PostFillAlignmentWindowMs;
        try
        {
            FeatureFlags.PostFillAlignmentWindowMs = 100;
            JournalParityPendingLedger.Clear();
            QuantExecutionControlStore.Clear();

            var utc = DateTimeOffset.Parse("2099-07-03T12:00:00Z");
            const string inst = "QM_REENTRY_GATE";

            JournalParityPendingLedger.TryRecordTrustedFill(inst, "rk", 1, "intent-seed", utc);
            QuantExecutionControlStore.EvaluateEscalationAndApplyIfRequired(inst, utc.AddSeconds(1), 0, null);

            var qSnap = QuantExecutionControlStore.GetSnapshot(inst);
            if (qSnap.Phase != QuantExecutionInstrumentPhase.RecoveryRequired)
                return (false, "setup failed: expected RecoveryRequired phase");
            if (!string.Equals(qSnap.RecoveryRequiredReason, "pending_alignment_expired_broker_gross_mismatch", StringComparison.Ordinal))
                return (false, "setup failed: wrong RecoveryRequired reason " + qSnap.RecoveryRequiredReason);

            var root = Path.Combine(Path.GetTempPath(), "market_reentry_submit_path_" + Guid.NewGuid().ToString("N"));
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

                var adapter = new NinjaTraderSimAdapter(root, root, log, journal);
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

                if (adapter.TryExecutionSafetyGateForOrderSubmitIntegration("intent-entry", inst, "SUBMIT_ENTRY", utc, out var entryFailure))
                    return (false, "expected SUBMIT_ENTRY deny under RecoveryRequired");
                if (entryFailure?.ErrorMessage == null ||
                    entryFailure.ErrorMessage.IndexOf("quant_recovery_required", StringComparison.Ordinal) < 0)
                    return (false, "expected quant_recovery_required for SUBMIT_ENTRY, got " + entryFailure?.ErrorMessage);

                if (!adapter.TryExecutionSafetyGateForOrderSubmitIntegration("intent-reentry", inst, "SUBMIT_MARKET_REENTRY", utc, out var reentryFailure))
                    return (false, "expected SUBMIT_MARKET_REENTRY allow, got " + reentryFailure?.ErrorMessage);
                if (reentryFailure != null)
                    return (false, "expected null failure payload on reentry allow");

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
            FeatureFlags.PostFillAlignmentWindowMs = prevWindowMs;
            JournalParityPendingLedger.Clear();
            QuantExecutionControlStore.Clear();
        }
    }
}
