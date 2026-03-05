// Stub implementations when NinjaTraderSimAdapter.NT.cs is excluded (e.g. modules/robot/core standalone build).
// The .NT.cs partial provides real implementations when compiled with NinjaTrader.

using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Stub partial for NinjaTraderSimAdapter when NT-specific implementation is excluded.
/// Throws on any real execution - used for harness/non-NT builds.
/// </summary>
public sealed partial class NinjaTraderSimAdapter
{
    private void VerifySimAccountReal()
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");

    private OrderSubmissionResult SubmitEntryOrderReal(string intentId, string instrument, string direction, decimal? entryPrice, int quantity, string? entryOrderType, DateTimeOffset utcNow)
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");

    private OrderSubmissionResult SubmitStopEntryOrderReal(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");

    private void HandleOrderUpdateReal(object order, object orderUpdate)
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");

    private void HandleExecutionUpdateReal(object execution, object order)
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");

    private OrderSubmissionResult SubmitProtectiveStopReal(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");

    private OrderSubmissionResult SubmitTargetOrderReal(string intentId, string instrument, string direction, decimal targetPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");

    private OrderModificationResult ModifyStopToBreakEvenReal(string intentId, string instrument, decimal beStopPrice, DateTimeOffset utcNow)
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");

    private FlattenResult FlattenIntentReal(string intentId, string instrument, DateTimeOffset utcNow)
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");

    private int GetCurrentPositionReal(string instrument)
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");

    private AccountSnapshot GetAccountSnapshotReal(DateTimeOffset utcNow)
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");

    private void CancelRobotOwnedWorkingOrdersReal(AccountSnapshot snap, DateTimeOffset utcNow)
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");

    private bool CancelIntentOrdersReal(string intentId, DateTimeOffset utcNow)
        => throw new NotImplementedException("NinjaTraderSimAdapter.NT.cs excluded - no real NT implementation.");
}
