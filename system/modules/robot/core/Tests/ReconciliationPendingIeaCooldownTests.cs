// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test RECONCILIATION_PENDING_IEA_COOLDOWN

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ReconciliationPendingIeaCooldownTests
{
    public static (bool Pass, string? Error) RunReconciliationPendingIeaCooldownTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "qtsw2_reconciliation_pending_iea_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var adapter = new SnapshotCountingExecutionAdapter();
            var pending = 18;

            var runner = new ReconciliationRunner(
                adapter,
                journal,
                log,
                getPendingExecutionWorkloadForInstrument: _ => pending);

            var t0 = new DateTimeOffset(2026, 4, 21, 20, 0, 0, TimeSpan.Zero);
            runner.ForceRunGateRecoveryForInstrument(t0, "MCL");
            if (adapter.SnapshotCallCount != 1)
                return (false, $"First gate recovery should take one snapshot, got {adapter.SnapshotCallCount}");

            runner.ForceRunGateRecoveryForInstrument(t0.AddSeconds(11), "MCL");
            if (adapter.SnapshotCallCount != 2)
                return (false, $"Stable pending signature should allow one cooled run before suppression, got {adapter.SnapshotCallCount}");

            runner.ForceRunGateRecoveryForInstrument(t0.AddSeconds(12), "MCL");
            if (adapter.SnapshotCallCount != 2)
                return (false, $"Cooldown should suppress repeated gate recovery snapshot, got {adapter.SnapshotCallCount}");

            pending = 19;
            runner.ForceRunGateRecoveryForInstrument(t0.AddSeconds(13), "MCL");
            if (adapter.SnapshotCallCount != 3)
                return (false, $"Pending signature change should bypass cooldown, got {adapter.SnapshotCallCount}");

            pending = 0;
            runner.ForceRunGateRecoveryForInstrument(t0.AddSeconds(14), "MCL");
            if (adapter.SnapshotCallCount != 4)
                return (false, $"Drained pending workload should clear cooldown state and run normally, got {adapter.SnapshotCallCount}");

            return (true, null);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }

    private sealed class SnapshotCountingExecutionAdapter : IExecutionAdapter
    {
        public int SnapshotCallCount { get; private set; }

        public OrderSubmissionResult SubmitEntryOrder(string intentId, string instrument, string direction, decimal? entryPrice, int quantity, string? entryOrderType, string? ocoGroup, DateTimeOffset utcNow)
            => OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);

        public OrderSubmissionResult SubmitStopEntryOrder(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
            => OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);

        public OrderSubmissionResult SubmitProtectiveStop(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
            => OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);

        public OrderSubmissionResult SubmitTargetOrder(string intentId, string instrument, string direction, decimal targetPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
            => OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);

        public OrderModificationResult ModifyStopToBreakEven(string intentId, string instrument, decimal beStopPrice, DateTimeOffset utcNow)
            => OrderModificationResult.SuccessResult(utcNow);

        public FlattenResult Flatten(string intentId, string instrument, DateTimeOffset utcNow)
            => FlattenResult.SuccessResult(utcNow);

        public FlattenResult FlattenEmergency(string instrument, DateTimeOffset utcNow)
            => FlattenResult.SuccessResult(utcNow);

        public bool TryEnqueueEmergencyFlattenProtective(string instrument, DateTimeOffset utcNow) => true;

        public AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow)
        {
            SnapshotCallCount++;
            return new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };
        }

        public (decimal? Bid, decimal? Ask) GetCurrentMarketPrice(string instrument, DateTimeOffset utcNow) => (null, null);

        public void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow) { }

        public void CancelOrders(IEnumerable<string> orderIds, DateTimeOffset utcNow) { }

        public void EnqueueExecutionCommand(ExecutionCommandBase command) { }

        public bool TryRepairTaggedBrokerWithoutJournal(string instrument, int accountQtyAbs, int journalOpenQtySum, DateTimeOffset utcNow, out string resultCode, out string? detail)
        {
            resultCode = "NOT_NEEDED";
            detail = null;
            return false;
        }

        public FlattenResult? RequestSessionCloseFlattenImmediate(string intentId, string instrument, DateTimeOffset utcNow)
            => FlattenResult.SuccessResult(utcNow);

        public bool IsExecutionContextReady => true;
    }
}
