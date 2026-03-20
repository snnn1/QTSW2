using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// No-op IIEAOrderExecutor for replay. Broker calls no-op; GetActiveIntentsForBEMonitoring uses IEA state.
/// </summary>
public sealed class ReplayExecutor : IIEAOrderExecutor
{
    private InstrumentExecutionAuthority? _iea;

    public void SetIEA(InstrumentExecutionAuthority iea) => _iea = iea;

    public object CreateStopMarketOrder(string instrument, string direction, int quantity, decimal stopPrice, string tag, string? ocoGroup) => new object();
    public void CancelOrders(IReadOnlyList<object> orders) { }
    public void SubmitOrders(IReadOnlyList<object> orders) { }
    public void SetOrderTag(object order, string tag) { }
    public string GetOrderTag(object order) => "";
    public string GetOrderId(object order) => "";
    public void RecordSubmission(string intentId, string tradingDate, string stream, string instrument, string orderType, string brokerOrderId, DateTimeOffset utcNow) { }
    public (string, string, decimal?, decimal?, decimal?, string?, string?) GetIntentInfo(string intentId) => ("", "", null, null, null, null, null);
    public object GetInstrument() => new object();
    public object GetAccount() => new object();
    public OrderSubmissionResult SubmitProtectiveStop(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow) => OrderSubmissionResult.SuccessResult("replay", utcNow);
    public OrderSubmissionResult SubmitTargetOrder(string intentId, string instrument, string direction, decimal targetPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow) => OrderSubmissionResult.SuccessResult("replay", utcNow);
    public bool CanSubmitExit(string intentId, int quantity) => true;
    public bool HasWorkingProtectivesForIntent(string intentId) => false;
    public (decimal?, decimal?) GetWorkingProtectivePrices(string intentId) => (null, null);
    public (decimal?, decimal?, int?, int?) GetWorkingProtectiveState(string intentId) => (null, null, null, null);
    public bool IsExecutionAllowed() => true;
    public void EvaluateBreakEvenCore(decimal tickPrice, DateTimeOffset eventTime, string executionInstrument) { }

    public OrderModificationResult ModifyStopToBreakEven(string intentId, string instrument, decimal beStopPrice, DateTimeOffset utcNow) => OrderModificationResult.SuccessResult(utcNow);
    public decimal GetTickSize() => 0.25m;
    public FlattenResult Flatten(string intentId, string instrument, DateTimeOffset utcNow) => FlattenResult.SuccessResult(utcNow);
    public FlattenResult EmergencyFlatten(string instrument, DateTimeOffset utcNow) => FlattenResult.SuccessResult(utcNow);
    public (int quantity, string direction) GetAccountPositionForInstrument(string instrument) => (0, "");
    public OrderSubmissionResult SubmitFlattenOrder(string instrument, string side, int quantity, FlattenDecisionSnapshot snapshot, DateTimeOffset utcNow) => OrderSubmissionResult.SuccessResult("replay", utcNow);
    public void StandDownStream(string streamId, DateTimeOffset utcNow, string reason) { }
    public void FailClosed(string intentId, Intent intent, string failureReason, string eventType, string notificationKey, string notificationTitle, string notificationMessage, OrderSubmissionResult? stopResult, OrderSubmissionResult? targetResult, object? additionalData, DateTimeOffset utcNow) { }

    public bool TryQueueProtectiveForRecovery(string intentId, Intent intent, int totalFilledQuantity, DateTimeOffset utcNow) => false;
    public void ProcessExecutionUpdate(object execution, object order) { }
    public void ProcessOrderUpdate(object order, object orderUpdate) { }
    public bool EnqueueNtAction(INtAction action) => false;
    public object? GetEntrySubmissionLock() => _iea?.EntrySubmissionLock;
    public void DrainNtActions() { }
    public void EnterStrategyThreadContext() { }
    public void ExitStrategyThreadContext() { }
    public void SetProtectionStateWorkingForAdoptedStop(string intentId) { }

    public IReadOnlyList<(string intentId, Intent intent, decimal beTriggerPrice, decimal entryPrice, decimal? actualFillPrice, string direction)> GetActiveIntentsForBEMonitoring(string? executionInstrument)
    {
        if (_iea == null) return Array.Empty<(string, Intent, decimal, decimal, decimal?, string)>();
        var list = new List<(string, Intent, decimal, decimal, decimal?, string)>();
        foreach (var kvp in _iea.IntentMap.OrderBy(k => k.Key))
        {
            var intentId = kvp.Key;
            var intent = kvp.Value;
            if (!string.IsNullOrEmpty(executionInstrument) && !string.Equals(intent.ExecutionInstrument ?? "", executionInstrument, StringComparison.OrdinalIgnoreCase))
                continue;
            if (intent.BeTrigger == null || intent.EntryPrice == null || intent.Direction == null)
                continue;
            if (!_iea.OrderMap.TryGetValue(intentId, out var oi) || !oi.IsEntryOrder || !oi.EntryFillTime.HasValue)
                continue;
            list.Add((intentId, intent, intent.BeTrigger.Value, intent.EntryPrice.Value, oi.Price, intent.Direction));
        }
        return list;
    }

    public IReadOnlyCollection<string> GetAdoptionCandidateIntentIds(string? executionInstrument) =>
        Array.Empty<string>();

    public (string JournalDir, int FileCount, bool DirectoryExists) GetJournalDiagnostics(string? executionInstrument) =>
        ("", 0, false);
}
