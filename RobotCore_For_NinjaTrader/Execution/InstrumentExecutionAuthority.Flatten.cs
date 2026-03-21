#if NINJATRADER
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// IEA Flatten Authority: Single canonical path for all flatten operations.
/// Account/broker position is the sole source of truth for flatten side and quantity.
/// THREAD: RequestFlatten must be called from strategy thread. Position query + decision + order submission
/// run as one critical section on the same thread.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    private const int FLATTEN_LATCH_TIMEOUT_SEC = 60;

    /// <summary>Per-instrument flatten latch. State: Idle | Acquired | Sent | Resolved | Rejected | Aborted | Timeout.</summary>
    private readonly ConcurrentDictionary<string, FlattenLatchEntry> _flattenLatchByInstrument = new();

    private sealed class FlattenLatchEntry
    {
        public DateTimeOffset StartedUtc { get; set; }
        public string Reason { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string? OrderId { get; set; }
        public FlattenLatchState State { get; set; }
    }

    internal enum FlattenLatchState
    {
        Idle,
        Acquired,
        Sent,
        Resolved,
        Rejected,
        Aborted,
        Timeout
    }

    /// <summary>
    /// Canonical flatten request. Call only from strategy thread.
    /// Derives side and quantity from account position at decision time.
    /// P2.6.6: Final gate — DestructiveActionPolicy must allow before any flatten order is submitted.
    /// </summary>
    public FlattenResult RequestFlatten(string instrument, string reason, string? callerContext, DateTimeOffset utcNow, FlattenPolicyExecutionContext? policyContext = null)
    {
        if (Executor == null || Log == null)
            return FlattenResult.FailureResult("IEA executor or log not set", utcNow);

        var instrumentKey = (instrument ?? "").Trim();
        if (string.IsNullOrEmpty(instrumentKey))
            return FlattenResult.FailureResult("Instrument is empty", utcNow);

        var requestId = $"FLATTEN:{instrumentKey}:{utcNow:yyyyMMddHHmmssfff}";

        Log.Write(RobotEvents.ExecutionBase(utcNow, "", instrumentKey, "FLATTEN_REQUEST_RECEIVED", new
        {
            instrument = instrumentKey,
            requested_reason = reason,
            caller_context = callerContext,
            latch_request_id = requestId
        }));

        // 2.1.1: Try acquire latch
        var entry = new FlattenLatchEntry
        {
            StartedUtc = utcNow,
            Reason = reason,
            RequestId = requestId,
            State = FlattenLatchState.Acquired
        };
        if (!_flattenLatchByInstrument.TryAdd(instrumentKey, entry))
        {
            var existing = _flattenLatchByInstrument.TryGetValue(instrumentKey, out var e) ? e : null;
            Log.Write(RobotEvents.ExecutionBase(utcNow, "", instrumentKey, "FLATTEN_ALREADY_IN_PROGRESS", new
            {
                instrument = instrumentKey,
                requested_reason = reason,
                caller_context = callerContext,
                existing_reason = existing?.Reason,
                existing_request_id = existing?.RequestId,
                note = "Duplicate flatten rejected - first request will handle"
            }));
            return FlattenResult.SuccessResult(utcNow);
        }

        try
        {
            // Position query + decision + submission as one critical section (strategy thread)
            // Architecture: Account position check FIRST. If flat, skip and never run validator or submit.
            // Validator runs only when qty != 0; it cannot influence order submission when flat.
            var (quantity, direction) = Executor.GetAccountPositionForInstrument(instrumentKey);

            if (quantity == 0 || string.IsNullOrEmpty(direction))
            {
                ReleaseFlattenLatch(instrumentKey, FlattenLatchState.Aborted, utcNow);
                Log.Write(RobotEvents.ExecutionBase(utcNow, "", instrumentKey, "FLATTEN_SKIPPED_ACCOUNT_FLAT", new
                {
                    instrument = instrumentKey,
                    requested_reason = reason,
                    caller_context = callerContext,
                    latch_request_id = requestId,
                    note = "Account flat - no order sent"
                }));
                return FlattenResult.SuccessResult(utcNow);
            }

            var absQty = Math.Abs(quantity);
            var chosenSide = direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY";

            // Exposure-reduction invariant
            var (valid, invariantError) = FlattenInvariantValidator.ValidateFlattenReducesExposure(quantity, chosenSide, absQty);
            if (!valid)
            {
                ReleaseFlattenLatch(instrumentKey, FlattenLatchState.Aborted, utcNow);
                Log.Write(RobotEvents.ExecutionBase(utcNow, "", instrumentKey, "FLATTEN_DIRECTION_INVALID", new
                {
                    instrument = instrumentKey,
                    account_quantity = quantity,
                    account_direction = direction,
                    chosen_side = chosenSide,
                    chosen_quantity = absQty,
                    error = invariantError,
                    latch_request_id = requestId
                }));
                return FlattenResult.FailureResult(invariantError ?? "FLATTEN_DIRECTION_INVALID", utcNow);
            }

            var pc = policyContext ?? FlattenPolicyExecutionContext.CreateDefault(reason);

            if (pc.PrecheckAllowInstrument.HasValue && !pc.PrecheckAllowInstrument.Value)
            {
                ReleaseFlattenLatch(instrumentKey, FlattenLatchState.Aborted, utcNow);
                Log.Write(RobotEvents.EngineBase(utcNow, "", instrumentKey, "DESTRUCTIVE_POLICY_INVARIANT_VIOLATION", new
                {
                    note = "P2.6.7: RequestFlatten received context where adapter pre-check disallowed instrument scope",
                    correlation_id = pc.PrecheckCorrelationId,
                    execution_instrument_key = ExecutionInstrumentKey
                }));
                return FlattenResult.FailureResult("P2.6.7: invalid flatten policy context (precheck disallowed)", utcNow);
            }

            // P2.6.7: Drift detection — pre-cancel snapshot (adapter) vs live read at final gate
            if (pc.PrecheckBrokerPositionQtyAbs.HasValue && pc.PrecheckBrokerPositionQtyAbs.Value != absQty)
            {
                Log.Write(RobotEvents.EngineBase(utcNow, "", instrumentKey, "DESTRUCTIVE_POLICY_PREPOST_DRIFT", new
                {
                    drift_kind = "broker_abs_qty",
                    precheck_abs = pc.PrecheckBrokerPositionQtyAbs.Value,
                    final_abs = absQty,
                    correlation_id = pc.PrecheckCorrelationId,
                    precheck_policy_reason = pc.PrecheckPolicyReasonCode,
                    execution_instrument_key = ExecutionInstrumentKey,
                    note = "Position size changed between ExecuteFlattenInstrument pre-check and RequestFlatten"
                }));
            }

            if (pc.PrecheckBrokerQtySigned.HasValue && quantity != 0 && pc.PrecheckBrokerQtySigned.Value != 0 &&
                Math.Sign(pc.PrecheckBrokerQtySigned.Value) != Math.Sign(quantity))
            {
                Log.Write(RobotEvents.EngineBase(utcNow, "", instrumentKey, "DESTRUCTIVE_POLICY_PREPOST_DRIFT", new
                {
                    drift_kind = "position_side_flip",
                    precheck_signed_qty = pc.PrecheckBrokerQtySigned.Value,
                    final_signed_qty = quantity,
                    correlation_id = pc.PrecheckCorrelationId,
                    execution_instrument_key = ExecutionInstrumentKey,
                    severity = "CRITICAL",
                    note = "Long/short sign changed between pre-check and submit — final policy still applies"
                }));
            }

            var triggerResolved = DestructiveTriggerParser.Resolve(pc.ExplicitTrigger, reason);
            var polInput = pc.ToPolicyInput(ExecutionInstrumentKey, absQty, reason);
            Log.Write(RobotEvents.EngineBase(utcNow, "", instrumentKey, "DESTRUCTIVE_ACTION_REQUESTED", new
            {
                source = pc.Source.ToString(),
                trigger = triggerResolved.ToString(),
                execution_instrument_key = ExecutionInstrumentKey,
                instrument = instrumentKey,
                caller_context = callerContext,
                phase = "pre_submit_request_flatten"
            }));
            var submitDecision = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(polInput);
            Log.Write(RobotEvents.EngineBase(utcNow, "", instrumentKey, "DESTRUCTIVE_ACTION_DECISION", new
            {
                allowed = submitDecision.AllowInstrumentScope,
                reason = submitDecision.ReasonCode,
                scope = submitDecision.CancelScopeMode,
                policy_path = submitDecision.PolicyPath,
                phase = "pre_submit_request_flatten"
            }));
            if (!submitDecision.AllowInstrumentScope)
            {
                ReleaseFlattenLatch(instrumentKey, FlattenLatchState.Aborted, utcNow);
                Log.Write(RobotEvents.EngineBase(utcNow, "", instrumentKey, "DESTRUCTIVE_ACTION_BLOCKED", new
                {
                    execution_instrument_key = ExecutionInstrumentKey,
                    instrument = instrumentKey,
                    reason_code = submitDecision.ReasonCode,
                    policy_path = submitDecision.PolicyPath,
                    caller_context = callerContext,
                    note = "P2.6.6: RequestFlatten blocked by destructive policy (no order submitted)"
                }));
                return FlattenResult.FailureResult($"Destructive policy denied flatten ({submitDecision.ReasonCode})", utcNow);
            }

            var snapshot = new FlattenDecisionSnapshot
            {
                Instrument = instrumentKey,
                AccountQuantityAtDecision = quantity,
                AccountDirectionAtDecision = direction,
                RequestedReason = reason,
                CallerContext = callerContext,
                ChosenSide = chosenSide,
                ChosenQuantity = absQty,
                LatchRequestId = requestId,
                LatchState = "Acquired",
                DecisionUtc = utcNow
            };

            var submitResult = Executor.SubmitFlattenOrder(instrumentKey, chosenSide, absQty, snapshot, utcNow);
            if (!submitResult.Success)
            {
                ReleaseFlattenLatch(instrumentKey, FlattenLatchState.Rejected, utcNow);
                Log.Write(RobotEvents.ExecutionBase(utcNow, "", instrumentKey, "FLATTEN_ORDER_REJECTED", new
                {
                    instrument = instrumentKey,
                    error = submitResult.ErrorMessage,
                    latch_request_id = requestId
                }));
                return FlattenResult.FailureResult(submitResult.ErrorMessage ?? "Flatten order rejected", utcNow);
            }

            entry.State = FlattenLatchState.Sent;
            entry.OrderId = submitResult.BrokerOrderId;
            if (!string.IsNullOrEmpty(submitResult.BrokerOrderId))
                RegisterFlattenOrder(submitResult.BrokerOrderId, instrumentKey, OrderOwnershipStatus.OWNED, "RequestFlatten", utcNow);
            Log.Write(RobotEvents.EngineBase(utcNow, "", instrumentKey, "DESTRUCTIVE_ACTION_EXECUTED", new
            {
                execution_instrument_key = ExecutionInstrumentKey,
                instrument = instrumentKey,
                source = pc.Source.ToString(),
                trigger = triggerResolved.ToString(),
                broker_order_id = submitResult.BrokerOrderId,
                phase = "flatten_order_submitted"
            }));
            return FlattenResult.SuccessResult(utcNow);
        }
        catch (Exception ex)
        {
            ReleaseFlattenLatch(instrumentKey, FlattenLatchState.Aborted, utcNow);
            Log.Write(RobotEvents.ExecutionBase(utcNow, "", instrumentKey, "FLATTEN_ABORTED_INVALID_POSITION_STATE", new
            {
                instrument = instrumentKey,
                error = ex.Message,
                latch_request_id = requestId
            }));
            return FlattenResult.FailureResult($"Flatten aborted: {ex.Message}", utcNow);
        }
    }

    /// <summary>Release latch and optionally emit FLATTEN_RESOLVED. Call when fill received or on abort/reject.</summary>
    internal void ReleaseFlattenLatch(string instrumentKey, FlattenLatchState newState, DateTimeOffset utcNow)
    {
        if (_flattenLatchByInstrument.TryRemove(instrumentKey, out var entry))
        {
            Log.Write(RobotEvents.ExecutionBase(utcNow, "", instrumentKey, "FLATTEN_RESOLVED", new
            {
                instrument = instrumentKey,
                latch_state = newState.ToString(),
                request_id = entry.RequestId
            }));
        }
    }

    /// <summary>Check if flatten order fill matches our latch. Call from ProcessExecutionUpdate.</summary>
    internal void OnFlattenFillReceived(string instrumentKey, string? orderId, DateTimeOffset utcNow)
    {
        if (_flattenLatchByInstrument.TryGetValue(instrumentKey, out var entry) &&
            (string.IsNullOrEmpty(orderId) || orderId == entry.OrderId))
        {
            if (!string.IsNullOrEmpty(orderId))
                UpdateOrderLifecycle(orderId, OrderLifecycleState.FILLED, utcNow);
            ReleaseFlattenLatch(instrumentKey, FlattenLatchState.Resolved, utcNow);
            OnRecoveryFlattenResolved(instrumentKey, utcNow);
            Log.Write(RobotEvents.ExecutionBase(utcNow, "", instrumentKey, "FLATTEN_FILL_RECEIVED", new
            {
                instrument = instrumentKey,
                order_id = orderId,
                request_id = entry.RequestId
            }));
        }
    }

    /// <summary>Check for latch timeouts. Call periodically (e.g. from heartbeat or BE tick).</summary>
    internal void CheckFlattenLatchTimeouts()
    {
        var utcNow = DateTimeOffset.UtcNow;
        foreach (var kvp in _flattenLatchByInstrument.ToArray())
        {
            var entry = kvp.Value;
            if (entry.State == FlattenLatchState.Sent &&
                (utcNow - entry.StartedUtc).TotalSeconds >= FLATTEN_LATCH_TIMEOUT_SEC)
            {
                if (_flattenLatchByInstrument.TryRemove(kvp.Key, out _))
                {
                    Log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_LATCH_TIMEOUT", state: "CRITICAL",
                        new
                        {
                            instrument = kvp.Key,
                            request_id = entry.RequestId,
                            started_utc = entry.StartedUtc.ToString("o"),
                            timeout_sec = FLATTEN_LATCH_TIMEOUT_SEC,
                            note = "Latch held beyond timeout - released; position may still be open"
                        }));
                }
            }
        }
    }
}
#endif
