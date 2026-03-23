// Strategy-thread NT action queue. All account.Change/Cancel/Flatten must run on strategy thread.
// IEA worker enqueues commands; strategy drains under EntrySubmissionLock.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// NT action to be executed on strategy thread. Worker computes; strategy executes.
/// </summary>
public interface INtAction
{
    string CorrelationId { get; }
    string ActionType { get; }
    string? IntentId { get; }
    string? InstrumentKey { get; }
    string Reason { get; }
    void Execute(INtActionExecutor executor);
}

/// <summary>
/// Executor that runs NT API calls. Implemented by NinjaTraderSimAdapter.
/// </summary>
public interface INtActionExecutor
{
    void ExecuteSubmitProtectives(NtSubmitProtectivesCommand cmd);
    void ExecuteCancelOrders(NtCancelOrdersCommand cmd);
    void ExecuteFlattenInstrument(NtFlattenInstrumentCommand cmd);
    void ExecuteSubmitEntryIntent(NtSubmitEntryIntentCommand cmd);
    void ExecuteSubmitMarketReentry(NtSubmitMarketReentryCommand cmd);
}

/// <summary>
/// Command: submit or update protective stop and target orders.
/// </summary>
public sealed class NtSubmitProtectivesCommand : INtAction
{
    public string CorrelationId { get; }
    public string ActionType => "SUBMIT_PROTECTIVES";
    public string? IntentId { get; }
    public string? InstrumentKey { get; }
    public string Reason { get; }
    public string Instrument { get; }
    public string Direction { get; }
    public decimal StopPrice { get; }
    public decimal TargetPrice { get; }
    public int Quantity { get; }
    public string? OcoGroup { get; }
    public DateTimeOffset UtcNow { get; }

    public NtSubmitProtectivesCommand(
        string correlationId,
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        decimal targetPrice,
        int quantity,
        string? ocoGroup,
        string reason,
        DateTimeOffset utcNow)
    {
        CorrelationId = correlationId;
        IntentId = intentId;
        InstrumentKey = instrument;
        Instrument = instrument;
        Direction = direction;
        StopPrice = stopPrice;
        TargetPrice = targetPrice;
        Quantity = quantity;
        OcoGroup = ocoGroup;
        Reason = reason;
        UtcNow = utcNow;
    }

    public void Execute(INtActionExecutor executor) => executor.ExecuteSubmitProtectives(this);
}

/// <summary>
/// Command: cancel orders by intent ID and/or tags.
/// </summary>
public sealed class NtCancelOrdersCommand : INtAction
{
    public string CorrelationId { get; }
    public string ActionType => "CANCEL_ORDERS";
    public string? IntentId { get; }
    public string? InstrumentKey { get; }
    public string Reason { get; }
    public bool ProtectiveOrdersOnly { get; }
    public DateTimeOffset UtcNow { get; }

    public NtCancelOrdersCommand(
        string correlationId,
        string? intentId,
        string? instrumentKey,
        bool protectiveOrdersOnly,
        string reason,
        DateTimeOffset utcNow)
    {
        CorrelationId = correlationId;
        IntentId = intentId;
        InstrumentKey = instrumentKey;
        ProtectiveOrdersOnly = protectiveOrdersOnly;
        Reason = reason;
        UtcNow = utcNow;
    }

    public void Execute(INtActionExecutor executor) => executor.ExecuteCancelOrders(this);
}

/// <summary>
/// Command: flatten instrument position.
/// P2.6: carries destructive policy metadata so ExecuteFlattenInstrument can enforce a single policy surface + cancel scope.
/// </summary>
public sealed class NtFlattenInstrumentCommand : INtAction
{
    public string CorrelationId { get; }
    public string ActionType => "FLATTEN_INSTRUMENT";
    public string? IntentId { get; }
    public string? InstrumentKey { get; }
    public string Reason { get; }
    public string Instrument { get; }
    public DateTimeOffset UtcNow { get; }
    public DestructiveActionSource DestructiveSource { get; }
    public DestructiveTriggerReason? ExplicitPolicyTrigger { get; }
    public bool AllowAccountWideCancelFallback { get; }
    public bool HasRecoveryPolicySeal { get; }
    public bool RecoveryPolicySealAllowInstrument { get; }
    public string? RecoveryPolicySealCode { get; }
    public string? RecoveryPolicySealAttributionScope { get; }
    public IReadOnlyList<string>? ExplicitCancelBrokerOrderIds { get; }

    /// <summary>True when this command is a post-flatten verify retry (coordination + debounce).</summary>
    public bool IsVerifyRetryFlatten { get; }

    public NtFlattenInstrumentCommand(
        string correlationId,
        string? intentId,
        string instrument,
        string reason,
        DateTimeOffset utcNow,
        DestructiveActionSource destructiveSource = DestructiveActionSource.RECOVERY,
        DestructiveTriggerReason? explicitPolicyTrigger = null,
        bool allowAccountWideCancelFallback = false,
        bool hasRecoveryPolicySeal = false,
        bool recoveryPolicySealAllowInstrument = false,
        string? recoveryPolicySealCode = null,
        string? recoveryPolicySealAttributionScope = null,
        IReadOnlyList<string>? explicitCancelBrokerOrderIds = null,
        bool isVerifyRetryFlatten = false)
    {
        CorrelationId = correlationId;
        IntentId = intentId;
        InstrumentKey = instrument;
        Instrument = instrument;
        Reason = reason;
        UtcNow = utcNow;
        DestructiveSource = destructiveSource;
        ExplicitPolicyTrigger = explicitPolicyTrigger;
        AllowAccountWideCancelFallback = allowAccountWideCancelFallback;
        HasRecoveryPolicySeal = hasRecoveryPolicySeal;
        RecoveryPolicySealAllowInstrument = recoveryPolicySealAllowInstrument;
        RecoveryPolicySealCode = recoveryPolicySealCode;
        RecoveryPolicySealAttributionScope = recoveryPolicySealAttributionScope;
        ExplicitCancelBrokerOrderIds = explicitCancelBrokerOrderIds;
        IsVerifyRetryFlatten = isVerifyRetryFlatten;
    }

    public void Execute(INtActionExecutor executor) => executor.ExecuteFlattenInstrument(this);
}

/// <summary>
/// Command: submit entry intent (stop brackets). Dispatched from IEA execution command layer.
/// </summary>
public sealed class NtSubmitEntryIntentCommand : INtAction
{
    public string CorrelationId { get; }
    public string ActionType => "SUBMIT_ENTRY_INTENT";
    public string? IntentId => _command.IntentId;
    public string? InstrumentKey => _command.Instrument;
    public string Reason => _command.Reason ?? "SUBMIT_ENTRY_INTENT";
    public SubmitEntryIntentCommand Command => _command;
    private readonly SubmitEntryIntentCommand _command;

    public NtSubmitEntryIntentCommand(string correlationId, SubmitEntryIntentCommand command)
    {
        CorrelationId = correlationId;
        _command = command ?? throw new ArgumentNullException(nameof(command));
    }

    public void Execute(INtActionExecutor executor) => executor.ExecuteSubmitEntryIntent(this);
}

/// <summary>
/// Command: submit market reentry (post-forced-flatten at market open). Single-direction market order.
/// </summary>
public sealed class NtSubmitMarketReentryCommand : INtAction
{
    public string CorrelationId { get; }
    public string ActionType => "SUBMIT_MARKET_REENTRY";
    public string? IntentId => Command.ReentryIntentId;
    public string? InstrumentKey => Command.ExecutionInstrument ?? Command.Instrument;
    public string Reason => Command.Reason ?? "MARKET_REENTRY";
    public SubmitMarketReentryCommand Command { get; }

    public NtSubmitMarketReentryCommand(string correlationId, SubmitMarketReentryCommand command)
    {
        CorrelationId = correlationId;
        Command = command ?? throw new ArgumentNullException(nameof(command));
    }

    public void Execute(INtActionExecutor executor) => executor.ExecuteSubmitMarketReentry(this);
}

/// <summary>
/// Deferred action: wraps a delegate for strategy-thread execution (guard fallback).
/// </summary>
public sealed class NtDeferredAction : INtAction
{
    private readonly Action _action;
    public string CorrelationId { get; }
    public string ActionType => "DEFERRED";
    public string? IntentId { get; }
    public string? InstrumentKey { get; }
    public string Reason { get; }

    public NtDeferredAction(string correlationId, string? intentId, string? instrumentKey, string reason, Action action)
    {
        CorrelationId = correlationId;
        IntentId = intentId;
        InstrumentKey = instrumentKey;
        Reason = reason;
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Execute(INtActionExecutor executor) => _action();
}

/// <summary>
/// FIFO queue of NT actions. Drained only on strategy thread under EntrySubmissionLock.
/// Idempotency: drops duplicates by correlationId.
/// </summary>
public sealed class StrategyThreadExecutor
{
    private readonly ConcurrentQueue<INtAction> _queue = new();
    private readonly HashSet<string> _pendingCorrelationIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingLock = new();
    private readonly RobotLogger _log;
    private readonly Func<DateTimeOffset> _utcNow;

    public StrategyThreadExecutor(RobotLogger log, Func<DateTimeOffset>? utcNow = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Enqueue NT action. Callable from any thread (e.g. IEA worker).
    /// If correlationId already pending, action is dropped (idempotency).
    /// </summary>
    public bool EnqueueNtAction(INtAction action, out bool droppedAsDuplicate)
    {
        droppedAsDuplicate = false;
        lock (_pendingLock)
        {
            if (_pendingCorrelationIds.Contains(action.CorrelationId))
            {
                droppedAsDuplicate = true;
                _log.Write(RobotEvents.EngineBase(_utcNow(), tradingDate: "", eventType: "NT_ACTION_DUPLICATE_DROPPED", state: "ENGINE",
                    new
                    {
                        correlation_id = action.CorrelationId,
                        action_type = action.ActionType,
                        intent_id = action.IntentId,
                        instrument_key = action.InstrumentKey,
                        reason = action.Reason,
                        note = "Idempotency: duplicate correlationId dropped"
                    }));
                return false;
            }
            _pendingCorrelationIds.Add(action.CorrelationId);
        }

        _queue.Enqueue(action);
        _log.Write(RobotEvents.EngineBase(_utcNow(), tradingDate: "", eventType: "NT_ACTION_ENQUEUED", state: "ENGINE",
            new
            {
                correlation_id = action.CorrelationId,
                action_type = action.ActionType,
                intent_id = action.IntentId,
                instrument_key = action.InstrumentKey,
                reason = action.Reason,
                queue_depth = _queue.Count,
                note = "NT action enqueued for strategy thread execution"
            }));
        return true;
    }

    /// <summary>
    /// Drain and execute all pending actions. MUST be called from strategy thread under EntrySubmissionLock.
    /// </summary>
    public void DrainNtActions(INtActionExecutor executor)
    {
        var executed = 0;
        while (_queue.TryDequeue(out var action))
        {
            var utcNow = _utcNow();
            try
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "NT_ACTION_START", state: "ENGINE",
                    new
                    {
                        correlation_id = action.CorrelationId,
                        action_type = action.ActionType,
                        intent_id = action.IntentId,
                        instrument_key = action.InstrumentKey,
                        reason = action.Reason
                    }));
                action.Execute(executor);
                if (action is NtFlattenInstrumentCommand flattenCmd)
                {
                    _log.Write(RobotEvents.EngineBase(_utcNow(), tradingDate: "", eventType: "FLATTEN_COMMAND_COMPLETED", state: "ENGINE",
                        new
                        {
                            correlation_id = flattenCmd.CorrelationId,
                            action_type = flattenCmd.ActionType,
                            intent_id = flattenCmd.IntentId,
                            instrument_key = flattenCmd.InstrumentKey,
                            note = "NT flatten delegate completed without exception — not broker-flat confirmation; see FLATTEN_BROKER_FLAT_CONFIRMED / FLATTEN_BROKER_POSITION_REMAINS"
                        }));
                }
                _log.Write(RobotEvents.EngineBase(_utcNow(), tradingDate: "", eventType: "NT_ACTION_SUCCESS", state: "ENGINE",
                    new
                    {
                        correlation_id = action.CorrelationId,
                        action_type = action.ActionType,
                        intent_id = action.IntentId,
                        instrument_key = action.InstrumentKey,
                        note = action is NtFlattenInstrumentCommand
                            ? "Still emitted for compatibility; flatten semantics split — FLATTEN_COMMAND_COMPLETED + broker verify events"
                            : null
                    }));
                executed++;
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.EngineBase(_utcNow(), tradingDate: "", eventType: "NT_ACTION_FAIL", state: "ENGINE",
                    new
                    {
                        correlation_id = action.CorrelationId,
                        action_type = action.ActionType,
                        intent_id = action.IntentId,
                        instrument_key = action.InstrumentKey,
                        error = ex.Message,
                        exception_type = ex.GetType().Name,
                        stack_trace = ex.StackTrace
                    }));
            }
            finally
            {
                lock (_pendingLock)
                    _pendingCorrelationIds.Remove(action.CorrelationId);
            }
        }
    }

    public int PendingCount => _queue.Count;
}
