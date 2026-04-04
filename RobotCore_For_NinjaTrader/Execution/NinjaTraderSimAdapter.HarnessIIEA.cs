using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// When <c>NinjaTraderSimAdapter.NT.cs</c> is excluded, explicit <see cref="IIEAOrderExecutor"/> / <see cref="INtActionExecutor"/>
/// members are satisfied for harness compilation; real NT work lives in the NinjaTrader project.
/// </summary>
public sealed partial class NinjaTraderSimAdapter
{
    private readonly ReplayExecutor _harnessIiea = new();

    /// <summary>No-op: full ingress draining is NT-only (<c>CallbackIngress</c> partial).</summary>
    public void DrainCallbackIngress(DateTimeOffset utcNow)
    {
    }

    /// <summary>Harness: no ingress queues when NT partial excluded.</summary>
    public bool TryGetCallbackIngressQueueLengths(string logicalInstrument, out int executionQueueLength, out int orderQueueLength)
    {
        executionQueueLength = 0;
        orderQueueLength = 0;
        return false;
    }

    /// <summary>Harness: no ingress when NT partial excluded.</summary>
    public void GetTotalCallbackIngressQueueLengths(out int executionTotal, out int orderTotal)
    {
        executionTotal = 0;
        orderTotal = 0;
    }

    object IIEAOrderExecutor.CreateStopMarketOrder(string instrument, string direction, int quantity, decimal stopPrice, string tag, string? ocoGroup) =>
        _harnessIiea.CreateStopMarketOrder(instrument, direction, quantity, stopPrice, tag, ocoGroup);

    void IIEAOrderExecutor.CancelOrders(IReadOnlyList<object> orders) => _harnessIiea.CancelOrders(orders);

    void IIEAOrderExecutor.SubmitOrders(IReadOnlyList<object> orders) => _harnessIiea.SubmitOrders(orders);

    void IIEAOrderExecutor.SetOrderTag(object order, string tag) => _harnessIiea.SetOrderTag(order, tag);

    string IIEAOrderExecutor.GetOrderTag(object order) => _harnessIiea.GetOrderTag(order);

    string IIEAOrderExecutor.GetOrderId(object order) => _harnessIiea.GetOrderId(order);

    void IIEAOrderExecutor.RecordSubmission(string intentId, string tradingDate, string stream, string instrument, string orderType, string brokerOrderId, DateTimeOffset utcNow) =>
        _harnessIiea.RecordSubmission(intentId, tradingDate, stream, instrument, orderType, brokerOrderId, utcNow);

    (string tradingDate, string stream, decimal? entryPrice, decimal? stopPrice, decimal? targetPrice, string? direction, string? ocoGroup) IIEAOrderExecutor.GetIntentInfo(string intentId) =>
        _harnessIiea.GetIntentInfo(intentId);

    object IIEAOrderExecutor.GetInstrument() => _harnessIiea.GetInstrument();

    object IIEAOrderExecutor.GetAccount() => _harnessIiea.GetAccount();

    OrderSubmissionResult IIEAOrderExecutor.SubmitProtectiveStop(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow) =>
        _harnessIiea.SubmitProtectiveStop(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);

    OrderSubmissionResult IIEAOrderExecutor.SubmitTargetOrder(string intentId, string instrument, string direction, decimal targetPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow) =>
        _harnessIiea.SubmitTargetOrder(intentId, instrument, direction, targetPrice, quantity, ocoGroup, utcNow);

    bool IIEAOrderExecutor.CanSubmitExit(string intentId, int quantity) => _harnessIiea.CanSubmitExit(intentId, quantity);

    bool IIEAOrderExecutor.HasWorkingProtectivesForIntent(string intentId) => _harnessIiea.HasWorkingProtectivesForIntent(intentId);

    (decimal? stopPrice, decimal? targetPrice) IIEAOrderExecutor.GetWorkingProtectivePrices(string intentId) =>
        _harnessIiea.GetWorkingProtectivePrices(intentId);

    (decimal? stopPrice, decimal? targetPrice, int? stopQty, int? targetQty) IIEAOrderExecutor.GetWorkingProtectiveState(string intentId) =>
        _harnessIiea.GetWorkingProtectiveState(intentId);

    bool IIEAOrderExecutor.IsExecutionAllowed() => _harnessIiea.IsExecutionAllowed();

    IReadOnlyList<(string intentId, Intent intent, decimal beTriggerPrice, decimal entryPrice, decimal? actualFillPrice, string direction)> IIEAOrderExecutor.GetActiveIntentsForBEMonitoring(string? executionInstrument) =>
        _harnessIiea.GetActiveIntentsForBEMonitoring(executionInstrument);

    IReadOnlyCollection<string> IIEAOrderExecutor.GetAdoptionCandidateIntentIds(string? executionInstrument) =>
        _harnessIiea.GetAdoptionCandidateIntentIds(executionInstrument);

    (string JournalDir, int FileCount, bool DirectoryExists) IIEAOrderExecutor.GetJournalDiagnostics(string? executionInstrument) =>
        _harnessIiea.GetJournalDiagnostics(executionInstrument);

    void IIEAOrderExecutor.EvaluateBreakEvenCore(decimal tickPrice, DateTimeOffset eventTime, string executionInstrument) =>
        _harnessIiea.EvaluateBreakEvenCore(tickPrice, eventTime, executionInstrument);

    OrderModificationResult IIEAOrderExecutor.ModifyStopToBreakEven(string intentId, string instrument, decimal beStopPrice, DateTimeOffset utcNow) =>
        _harnessIiea.ModifyStopToBreakEven(intentId, instrument, beStopPrice, utcNow);

    decimal IIEAOrderExecutor.GetTickSize() => _harnessIiea.GetTickSize();

    FlattenResult IIEAOrderExecutor.Flatten(string intentId, string instrument, DateTimeOffset utcNow) =>
        _harnessIiea.Flatten(intentId, instrument, utcNow);

    FlattenResult IIEAOrderExecutor.EmergencyFlatten(string instrument, DateTimeOffset utcNow) =>
        _harnessIiea.EmergencyFlatten(instrument, utcNow);

    (int quantity, string direction) IIEAOrderExecutor.GetAccountPositionForInstrument(string instrument) =>
        _harnessIiea.GetAccountPositionForInstrument(instrument);

    BrokerCanonicalExposure IIEAOrderExecutor.GetBrokerCanonicalExposure(string instrument) =>
        _harnessIiea.GetBrokerCanonicalExposure(instrument);

    OrderSubmissionResult IIEAOrderExecutor.SubmitFlattenOrder(string instrument, string side, int quantity, FlattenDecisionSnapshot snapshot, DateTimeOffset utcNow, object? nativeInstrumentForBrokerOrder = null) =>
        _harnessIiea.SubmitFlattenOrder(instrument, side, quantity, snapshot, utcNow, nativeInstrumentForBrokerOrder);

    void IIEAOrderExecutor.StandDownStream(string streamId, DateTimeOffset utcNow, string reason) =>
        _harnessIiea.StandDownStream(streamId, utcNow, reason);

    void IIEAOrderExecutor.FailClosed(string intentId, Intent intent, string failureReason, string eventType, string notificationKey, string notificationTitle, string notificationMessage, OrderSubmissionResult? stopResult, OrderSubmissionResult? targetResult, object? additionalData, DateTimeOffset utcNow) =>
        _harnessIiea.FailClosed(intentId, intent, failureReason, eventType, notificationKey, notificationTitle, notificationMessage, stopResult, targetResult, additionalData, utcNow);

    void IIEAOrderExecutor.ProcessExecutionUpdate(object execution, object order) =>
        _harnessIiea.ProcessExecutionUpdate(execution, order);

    void IIEAOrderExecutor.ProcessOrderUpdate(object order, object orderUpdate) =>
        _harnessIiea.ProcessOrderUpdate(order, orderUpdate);

    // INtActionExecutor is implemented in NinjaTraderSimAdapter.NT.cs when building RobotCore_For_NinjaTrader.
}
