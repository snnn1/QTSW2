#if NINJATRADER
using System;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// IEA Execution Command Layer: Strategy/runtime layers emit commands; IEA dispatches to existing logic.
/// All broker interaction flows through Executor.EnqueueNtAction (strategy thread). No direct NT API calls from worker.
///
/// Per-instrument serialization: Each IEA instance owns a single _executionQueue. All commands for that instrument
/// are enqueued to the same queue and processed sequentially by the worker. This guarantees CancelIntentOrdersCommand
/// and FlattenIntentCommand (and any other commands) execute in enqueue order for the same instrument.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    /// <summary>
    /// Enqueue an execution command for serialized processing. Commands are dispatched on the IEA worker;
    /// handlers translate to NtActions and enqueue via Executor for strategy-thread execution.
    /// </summary>
    public void EnqueueExecutionCommand(ExecutionCommandBase command)
    {
        if (command == null) return;
        if (Executor == null)
        {
            var rejectedAudit = GetCommandAuditIdentity(command);
            Log?.Write(RobotEvents.ExecutionBase(command.TimestampUtc, rejectedAudit.IntentId, rejectedAudit.Instrument, "EXECUTION_COMMAND_REJECTED",
                new
                {
                    commandId = command.CommandId,
                    reason = "Executor not set",
                    commandType = command.GetType().Name,
                    stream_key = rejectedAudit.StreamKey,
                    order_role = rejectedAudit.OrderRole,
                    request_id = command.CommandId,
                    correlation_id = rejectedAudit.CorrelationId
                }));
            _eventWriter?.Emit(new CanonicalExecutionEvent
            {
                TimestampUtc = command.TimestampUtc.ToString("o"),
                Instrument = rejectedAudit.Instrument,
                StreamKey = rejectedAudit.StreamKey,
                IntentId = rejectedAudit.IntentId,
                CommandId = command.CommandId,
                EventType = ExecutionEventTypes.COMMAND_REJECTED,
                Source = "IEA",
                Severity = "ERROR",
                Payload = new
                {
                    commandId = command.CommandId,
                    reason = "Executor not set",
                    commandType = command.GetType().Name,
                    instrument = rejectedAudit.Instrument,
                    stream_key = rejectedAudit.StreamKey,
                    intent_id = rejectedAudit.IntentId,
                    order_role = rejectedAudit.OrderRole,
                    request_id = command.CommandId,
                    correlation_id = rejectedAudit.CorrelationId
                }
            });
            return;
        }
        EnqueueCore(() => DispatchCommand(command), "ExecutionCommand");
    }

    private void DispatchCommand(ExecutionCommandBase command)
    {
        var utcNow = command.TimestampUtc;
        var audit = GetCommandAuditIdentity(command);
        var instrument = audit.Instrument;
        var intentId = audit.IntentId;
        var commandType = command.GetType().Name;

        Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_COMMAND_RECEIVED",
            new
            {
                commandType,
                reason = command.Reason,
                callerContext = command.CallerContext,
                instrument,
                stream_key = audit.StreamKey,
                intent_id = intentId,
                order_role = audit.OrderRole,
                request_id = command.CommandId,
                correlation_id = audit.CorrelationId
            }));
        _eventWriter?.Emit(new CanonicalExecutionEvent
        {
            TimestampUtc = utcNow.ToString("o"),
            Instrument = instrument,
            StreamKey = audit.StreamKey,
            IntentId = intentId,
            CommandId = command.CommandId,
            EventType = ExecutionEventTypes.COMMAND_RECEIVED,
            Source = "IEA",
            Payload = new
            {
                commandType,
                reason = command.Reason,
                callerContext = command.CallerContext,
                instrument,
                stream_key = audit.StreamKey,
                intent_id = intentId,
                order_role = audit.OrderRole,
                request_id = command.CommandId,
                correlation_id = audit.CorrelationId
            }
        });

        try
        {
            switch (command)
            {
                case FlattenIntentCommand flatten:
                    HandleFlattenIntentCommand(flatten);
                    break;
                case CancelIntentOrdersCommand cancel:
                    HandleCancelIntentOrdersCommand(cancel);
                    break;
                case SubmitEntryIntentCommand submit:
                    HandleSubmitEntryIntentCommand(submit);
                    break;
                case SubmitMarketReentryCommand reentry:
                    HandleSubmitMarketReentryCommand(reentry);
                    break;
                default:
                    Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_COMMAND_UNKNOWN",
                        new { commandId = command.CommandId, commandType }));
                    _eventWriter?.Emit(new CanonicalExecutionEvent
                    {
                        TimestampUtc = utcNow.ToString("o"),
                        Instrument = instrument,
                        IntentId = intentId,
                        CommandId = command.CommandId,
                        EventType = ExecutionEventTypes.COMMAND_SKIPPED,
                        Source = "IEA",
                        Payload = new { commandId = command.CommandId, commandType }
                    });
                    return;
            }

            Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_COMMAND_DISPATCHED",
                new { commandType, reason = command.Reason }));
        }
        catch (Exception ex)
        {
            Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_COMMAND_ERROR",
                new { commandId = command.CommandId, commandType, error = ex.Message, exception_type = ex.GetType().Name }));
            _eventWriter?.Emit(new CanonicalExecutionEvent
            {
                TimestampUtc = utcNow.ToString("o"),
                Instrument = instrument,
                IntentId = intentId,
                CommandId = command.CommandId,
                EventType = ExecutionEventTypes.COMMAND_ERROR,
                Source = "IEA",
                Severity = "ERROR",
                Payload = new { commandId = command.CommandId, commandType, error = ex.Message, exception_type = ex.GetType().Name }
            });
        }
    }

    private static (string Instrument, string IntentId, string? StreamKey, string OrderRole, string? CorrelationId) GetCommandAuditIdentity(ExecutionCommandBase command)
    {
        if (command is SubmitMarketReentryCommand reentry)
        {
            var intentId = (reentry.ReentryIntentId ?? reentry.IntentId ?? "").Trim();
            return (
                (reentry.ExecutionInstrument ?? reentry.Instrument ?? "").Trim(),
                intentId,
                string.IsNullOrWhiteSpace(reentry.Stream) ? null : reentry.Stream.Trim(),
                "ENTRY",
                string.IsNullOrWhiteSpace(intentId) ? null : $"REENTRY:{intentId}:{command.TimestampUtc:yyyyMMddHHmmssfff}");
        }

        if (command is SubmitEntryIntentCommand submit)
        {
            var intentId = (submit.LongIntentId ?? submit.IntentId ?? "").Trim();
            return (
                (submit.ExecutionInstrument ?? submit.Instrument ?? "").Trim(),
                intentId,
                string.IsNullOrWhiteSpace(submit.Stream) ? null : submit.Stream.Trim(),
                "ENTRY",
                string.IsNullOrWhiteSpace(intentId) ? null : $"SUBMIT_ENTRY:{intentId}:{command.TimestampUtc:yyyyMMddHHmmssfff}");
        }

        var baseInstrument = (command.Instrument ?? "").Trim();
        var baseIntentId = (command.IntentId ?? "").Trim();
        var role = command switch
        {
            FlattenIntentCommand => "FLATTEN",
            CancelIntentOrdersCommand => "CANCEL",
            _ => command.GetType().Name
        };
        var correlationId = command switch
        {
            FlattenIntentCommand => $"FLATTEN:{(string.IsNullOrEmpty(baseIntentId) ? "CMD" : baseIntentId)}:{command.TimestampUtc:yyyyMMddHHmmssfff}",
            CancelIntentOrdersCommand => $"CANCEL:{baseIntentId}:{command.TimestampUtc:yyyyMMddHHmmssfff}",
            _ => null
        };

        return (baseInstrument, baseIntentId, null, role, correlationId);
    }

    private void HandleFlattenIntentCommand(FlattenIntentCommand cmd)
    {
        if (Executor == null) return;
        var instrument = (cmd.Instrument ?? "").Trim();
        if (string.IsNullOrEmpty(instrument)) return;

        var intentId = cmd.IntentId ?? "";
        var state = GetIntentLifecycleState(intentId);
        if (!IntentLifecycleValidator.IsFlattenIntentAllowed(state))
        {
            Log?.Write(RobotEvents.ExecutionBase(cmd.TimestampUtc, intentId, instrument, "EXECUTION_COMMAND_REJECTED",
                new { commandId = cmd.CommandId, commandType = nameof(FlattenIntentCommand), reason = "Intent already TERMINAL", currentState = state.ToString() }));
            return;
        }

        if (!string.IsNullOrEmpty(intentId))
            TryTransitionIntentLifecycle(intentId, IntentLifecycleTransition.EXIT_STARTED, cmd.CommandId, cmd.TimestampUtc);

        var reason = string.IsNullOrEmpty(cmd.Reason) ? cmd.FlattenReason.ToString() : cmd.Reason;
        var correlationId = $"FLATTEN:{(string.IsNullOrEmpty(intentId) ? "CMD" : intentId)}:{cmd.TimestampUtc:yyyyMMddHHmmssfff}";
        var ntCmd = new NtFlattenInstrumentCommand(correlationId, cmd.IntentId, instrument, reason, cmd.TimestampUtc,
            DestructiveActionSource.COMMAND, DestructiveTriggerReason.MANUAL);
        Executor.EnqueueNtAction(ntCmd);

        Log?.Write(RobotEvents.ExecutionBase(cmd.TimestampUtc, intentId, instrument, "EXECUTION_COMMAND_COMPLETED",
            new { commandId = cmd.CommandId, commandType = nameof(FlattenIntentCommand), flattenReason = cmd.FlattenReason.ToString() }));
        _eventWriter?.Emit(new CanonicalExecutionEvent
        {
            TimestampUtc = cmd.TimestampUtc.ToString("o"),
            Instrument = instrument,
            IntentId = intentId,
            CommandId = cmd.CommandId,
            EventType = ExecutionEventTypes.COMMAND_COMPLETED,
            Source = "IEA",
            Payload = new { commandId = cmd.CommandId, commandType = nameof(FlattenIntentCommand), flattenReason = cmd.FlattenReason.ToString() }
        });
    }

    private void HandleCancelIntentOrdersCommand(CancelIntentOrdersCommand cmd)
    {
        if (Executor == null) return;
        var intentId = cmd.IntentId ?? "";
        var instrument = (cmd.Instrument ?? "").Trim();
        if (string.IsNullOrEmpty(intentId) && string.IsNullOrEmpty(instrument)) return;

        var state = GetIntentLifecycleState(intentId);
        if (!IntentLifecycleValidator.IsCancelIntentOrdersAllowed(state))
        {
            Log?.Write(RobotEvents.ExecutionBase(cmd.TimestampUtc, intentId, instrument, "EXECUTION_COMMAND_REJECTED",
                new { commandId = cmd.CommandId, commandType = nameof(CancelIntentOrdersCommand), reason = "Intent state does not allow cancel", currentState = state.ToString() }));
            _eventWriter?.Emit(new CanonicalExecutionEvent
            {
                TimestampUtc = cmd.TimestampUtc.ToString("o"),
                Instrument = instrument,
                IntentId = intentId,
                CommandId = cmd.CommandId,
                EventType = ExecutionEventTypes.COMMAND_REJECTED,
                Source = "IEA",
                Severity = "WARN",
                Payload = new { commandId = cmd.CommandId, commandType = nameof(CancelIntentOrdersCommand), reason = "Intent state does not allow cancel", currentState = state.ToString() }
            });
            return;
        }

        TryTransitionIntentLifecycle(intentId, IntentLifecycleTransition.EXIT_STARTED, cmd.CommandId, cmd.TimestampUtc);

        var correlationId = $"CANCEL:{intentId}:{cmd.TimestampUtc:yyyyMMddHHmmssfff}";
        var ntCmd = new NtCancelOrdersCommand(correlationId, intentId, instrument, false, cmd.Reason ?? "CANCEL_INTENT_ORDERS", cmd.TimestampUtc);
        Executor.EnqueueNtAction(ntCmd);

        Log?.Write(RobotEvents.ExecutionBase(cmd.TimestampUtc, intentId, instrument, "EXECUTION_COMMAND_COMPLETED",
            new { commandId = cmd.CommandId, commandType = nameof(CancelIntentOrdersCommand) }));
        _eventWriter?.Emit(new CanonicalExecutionEvent
        {
            TimestampUtc = cmd.TimestampUtc.ToString("o"),
            Instrument = instrument,
            IntentId = intentId,
            CommandId = cmd.CommandId,
            EventType = ExecutionEventTypes.COMMAND_COMPLETED,
            Source = "IEA",
            Payload = new { commandId = cmd.CommandId, commandType = nameof(CancelIntentOrdersCommand) }
        });
    }

    private void HandleSubmitEntryIntentCommand(SubmitEntryIntentCommand cmd)
    {
        if (Executor == null) return;
        var longIntentId = cmd.LongIntentId ?? "";
        var shortIntentId = cmd.ShortIntentId ?? "";
        var instrument = cmd.Instrument ?? "";

        EnsureIntentLifecycleCreated(longIntentId);
        EnsureIntentLifecycleCreated(shortIntentId);

        var longState = GetIntentLifecycleState(longIntentId);
        var shortState = GetIntentLifecycleState(shortIntentId);
        if (!IntentLifecycleValidator.IsSubmitEntryIntentAllowed(longState) || !IntentLifecycleValidator.IsSubmitEntryIntentAllowed(shortState))
        {
            Log?.Write(RobotEvents.ExecutionBase(cmd.TimestampUtc, longIntentId, instrument, "EXECUTION_COMMAND_REJECTED",
                new { commandId = cmd.CommandId, commandType = nameof(SubmitEntryIntentCommand), reason = "Intent not in CREATED state", longState = longState.ToString(), shortState = shortState.ToString() }));
            return;
        }

        var correlationId = $"SUBMIT_ENTRY:{longIntentId}:{cmd.TimestampUtc:yyyyMMddHHmmssfff}";
        var ntCmd = new NtSubmitEntryIntentCommand(correlationId, cmd);
        TryTransitionIntentLifecycle(longIntentId, IntentLifecycleTransition.SUBMIT_ENTRY, cmd.CommandId, cmd.TimestampUtc);
        TryTransitionIntentLifecycle(shortIntentId, IntentLifecycleTransition.SUBMIT_ENTRY, cmd.CommandId, cmd.TimestampUtc);
        Executor.EnqueueNtAction(ntCmd);

        Log?.Write(RobotEvents.ExecutionBase(cmd.TimestampUtc, longIntentId, instrument, "EXECUTION_COMMAND_COMPLETED",
            new { commandId = cmd.CommandId, commandType = nameof(SubmitEntryIntentCommand) }));
    }

    private void HandleSubmitMarketReentryCommand(SubmitMarketReentryCommand cmd)
    {
        if (Executor == null) return;
        var reentryIntentId = cmd.ReentryIntentId ?? "";
        var instrument = (cmd.ExecutionInstrument ?? cmd.Instrument ?? "").Trim();
        var direction = cmd.Direction ?? "";
        var quantity = cmd.Quantity;
        if (string.IsNullOrEmpty(reentryIntentId) || string.IsNullOrEmpty(instrument) || string.IsNullOrEmpty(direction) || quantity <= 0)
        {
            Log?.Write(RobotEvents.ExecutionBase(cmd.TimestampUtc, reentryIntentId, instrument, "EXECUTION_COMMAND_REJECTED",
                new { commandId = cmd.CommandId, commandType = nameof(SubmitMarketReentryCommand), reason = "Missing required fields" }));
            return;
        }

        EnsureIntentLifecycleCreated(reentryIntentId);
        var state = GetIntentLifecycleState(reentryIntentId);
        if (!IntentLifecycleValidator.IsSubmitEntryIntentAllowed(state))
        {
            Log?.Write(RobotEvents.ExecutionBase(cmd.TimestampUtc, reentryIntentId, instrument, "EXECUTION_COMMAND_REJECTED",
                new { commandId = cmd.CommandId, commandType = nameof(SubmitMarketReentryCommand), reason = "Intent not in CREATED state", currentState = state.ToString() }));
            return;
        }

        var correlationId = $"REENTRY:{reentryIntentId}:{cmd.TimestampUtc:yyyyMMddHHmmssfff}";
        var ntCmd = new NtSubmitMarketReentryCommand(correlationId, cmd);
        TryTransitionIntentLifecycle(reentryIntentId, IntentLifecycleTransition.SUBMIT_ENTRY, cmd.CommandId, cmd.TimestampUtc);
        Executor.EnqueueNtAction(ntCmd);

        Log?.Write(RobotEvents.ExecutionBase(cmd.TimestampUtc, reentryIntentId, instrument, "EXECUTION_COMMAND_COMPLETED",
            new { commandId = cmd.CommandId, commandType = nameof(SubmitMarketReentryCommand), lifecycle_transition_before_nt_enqueue = true }));
        _eventWriter?.Emit(new CanonicalExecutionEvent
        {
            TimestampUtc = cmd.TimestampUtc.ToString("o"),
            Instrument = instrument,
            IntentId = reentryIntentId,
            CommandId = cmd.CommandId,
            EventType = ExecutionEventTypes.COMMAND_COMPLETED,
            Source = "IEA",
            Payload = new { commandId = cmd.CommandId, commandType = nameof(SubmitMarketReentryCommand), direction, quantity }
        });
    }
}
#endif
