using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Interface for NT order operations. Implemented by adapter; used by IEA for aggregation and order submission.
/// Abstracts NinjaTrader Account/Order/Instrument so IEA can perform broker operations without direct NT references.
/// </summary>
public interface IIEAOrderExecutor
{
    /// <summary>Create stop-market order. Returns order object (for NTOrder storage).</summary>
    object CreateStopMarketOrder(string instrument, string direction, int quantity, decimal stopPrice, string tag, string? ocoGroup);

    /// <summary>Cancel orders.</summary>
    void CancelOrders(IReadOnlyList<object> orders);

    /// <summary>Submit orders to broker.</summary>
    void SubmitOrders(IReadOnlyList<object> orders);

    /// <summary>Set order tag/name.</summary>
    void SetOrderTag(object order, string tag);

    /// <summary>Get order tag/name.</summary>
    string GetOrderTag(object order);

    /// <summary>Get broker order id from order object.</summary>
    string GetOrderId(object order);

    /// <summary>Record submission in execution journal.</summary>
    void RecordSubmission(string intentId, string tradingDate, string stream, string instrument, string orderType, string brokerOrderId, DateTimeOffset utcNow);

    /// <summary>Get intent info for logging.</summary>
    (string tradingDate, string stream, decimal? entryPrice, decimal? stopPrice, decimal? targetPrice, string? direction, string? ocoGroup) GetIntentInfo(string intentId);

    /// <summary>Get NT instrument for order creation (strategy's instrument).</summary>
    object GetInstrument();

    /// <summary>Get NT account for order operations.</summary>
    object GetAccount();

    /// <summary>Submit protective stop order (Phase 2: IEA delegates to adapter).</summary>
    OrderSubmissionResult SubmitProtectiveStop(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow);

    /// <summary>Submit protective target order (Phase 2: IEA delegates to adapter).</summary>
    OrderSubmissionResult SubmitTargetOrder(string intentId, string instrument, string direction, decimal targetPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow);

    /// <summary>Check if exit can be submitted (coordinator validation).</summary>
    bool CanSubmitExit(string intentId, int quantity);

    /// <summary>Idempotency: true if working protective stop and target already exist for intent.</summary>
    bool HasWorkingProtectivesForIntent(string intentId);

    /// <summary>Idempotency: get working protective prices for adoption validation. (null, null) if none.</summary>
    (decimal? stopPrice, decimal? targetPrice) GetWorkingProtectivePrices(string intentId);

    /// <summary>Gap 2: Get working protective state (prices + quantities) for adoption quantity validation.</summary>
    (decimal? stopPrice, decimal? targetPrice, int? stopQty, int? targetQty) GetWorkingProtectiveState(string intentId);

    /// <summary>Check if execution is allowed (recovery state).</summary>
    bool IsExecutionAllowed();

    /// <summary>Phase 3: Get active intents eligible for BE monitoring (entry filled, BE not yet applied).</summary>
    IReadOnlyList<(string intentId, Intent intent, decimal beTriggerPrice, decimal entryPrice, decimal? actualFillPrice, string direction)> GetActiveIntentsForBEMonitoring(string? executionInstrument);

    /// <summary>Phase 3: Evaluate BE triggers and dispatch modify. Single evaluation function; branch only at mutation.</summary>
    void EvaluateBreakEvenCore(decimal tickPrice, DateTimeOffset eventTime, string executionInstrument);

    /// <summary>Phase 3: Modify stop order to break-even price.</summary>
    OrderModificationResult ModifyStopToBreakEven(string intentId, string instrument, decimal beStopPrice, DateTimeOffset utcNow);

    /// <summary>Phase 3: Get tick size for instrument (for BE stop calculation).</summary>
    decimal GetTickSize();

    /// <summary>Phase 3: Flatten position for intent (BE failure recovery).</summary>
    FlattenResult Flatten(string intentId, string instrument, DateTimeOffset utcNow);

    /// <summary>Phase 3: Stand down stream (BE failure recovery).</summary>
    void StandDownStream(string streamId, DateTimeOffset utcNow, string reason);

    /// <summary>Fail-closed: flatten, stand down, alert, persist incident.</summary>
    void FailClosed(string intentId, Intent intent, string failureReason, string eventType, string notificationKey, string notificationTitle, string notificationMessage, OrderSubmissionResult? stopResult, OrderSubmissionResult? targetResult, object? additionalData, DateTimeOffset utcNow);

    /// <summary>Process execution update (called by IEA worker when queue serialization enabled).</summary>
    void ProcessExecutionUpdate(object execution, object order);

    /// <summary>Gap 2: Process order update (called by IEA worker when queue serialization enabled).</summary>
    void ProcessOrderUpdate(object order, object orderUpdate);

    /// <summary>Enqueue NT action for strategy-thread execution. Callable from worker. Returns false if duplicate dropped.</summary>
    bool EnqueueNtAction(INtAction action);

    /// <summary>Get lock for strategy-thread operations. Strategy holds this when draining NT actions.</summary>
    object? GetEntrySubmissionLock();

    /// <summary>Drain NT action queue. MUST be called from strategy thread while holding EntrySubmissionLock.</summary>
    void DrainNtActions();

    /// <summary>Set ProtectionState to Working when adopting an existing stop from account.Orders on restart.</summary>
    void SetProtectionStateWorkingForAdoptedStop(string intentId);

    /// <summary>Mark strategy-thread context. Call at start of OnMarketData/OnBarUpdate before any adapter work.</summary>
    void EnterStrategyThreadContext();

    /// <summary>Clear strategy-thread context. Call in finally at end of OnMarketData/OnBarUpdate.</summary>
    void ExitStrategyThreadContext();
}
