#if NINJATRADER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NinjaTrader.Cbi;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Phase 2: IEA owns aggregation and order submission. Phase 3: BE evaluation.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    /// <summary>P2.1-F: block aggregation that would cancel sibling working orders when unsafe.</summary>
    private bool IsAggregationSiblingCancelBlockedForP2()
    {
        if (_aggregationSiblingCancelGuard != null && _aggregationSiblingCancelGuard(ExecutionInstrumentKey))
            return true;
        foreach (var e in _orderRegistry.GetAllEntries())
        {
            if (e.LifecycleState == OrderLifecycleState.WORKING &&
                (e.OwnershipStatus == OrderOwnershipStatus.UNOWNED ||
                 e.OwnershipStatus == OrderOwnershipStatus.RECOVERABLE_ROBOT_OWNED))
                return true;
        }
        return IsInRecovery;
    }

    private static DateTimeOffset? TryGetBrokerOrderTimeUtc(Order o)
    {
        if (o == null) return null;
        try
        {
            var t = o.Time;
            if (t == default) return null;
            return t.Kind == DateTimeKind.Utc ? new DateTimeOffset(t) : new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Local));
        }
        catch
        {
            return null;
        }
    }

    // Phase 3: Trap G - queue serialization (replaces BE-specific lock; queue handles all mutations)
    /// <summary>
    /// Submit stop entry order. Tries aggregation first; falls back to single order.
    /// Returns null if executor not set (caller should use adapter path).
    /// </summary>
    public OrderSubmissionResult? SubmitStopEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        if (Executor == null || Log == null) return null;

        var account = Executor.GetAccount() as Account;
        var ntInstrument = Executor.GetInstrument() as Instrument;
        if (account == null || ntInstrument == null) return null;

        var aggregateResult = TryAggregateWithExistingOrders(intentId, instrument, direction, stopPrice, quantity, ocoGroup, account, ntInstrument, utcNow);
        if (aggregateResult != null)
            return aggregateResult;

        return null; // Caller (adapter) does single-order path when null
    }

    /// <summary>
    /// When multiple streams have entry stops at same price, aggregate into one broker order.
    /// Returns non-null if aggregation was attempted (success or failure); null to continue with normal flow.
    /// </summary>
    internal OrderSubmissionResult? TryAggregateWithExistingOrders(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        Account account,
        Instrument ntInstrument,
        DateTimeOffset utcNow)
    {
        if (Executor == null || Log == null) return null;
        if (IsAggregationSiblingCancelBlockedForP2())
        {
            Log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "AGGREGATION_CANCEL_BLOCKED_DUE_TO_ATTRIBUTION", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    execution_instrument_key = ExecutionInstrumentKey,
                    iea_instance_id = InstanceId,
                    note = "P2.1-F: sibling cancel/aggregate blocked while ownership degraded, recovery active, or mismatch gate engaged"
                }));
            return null;
        }
        if (!IntentMap.TryGetValue(intentId, out var currentIntent))
            return null;

        var toAggregate = new List<(string intentId, string stream, int qty, string? oco)>();
        foreach (var kvp in IntentMap.OrderBy(k => k.Key))
        {
            var other = kvp.Value;
            if (kvp.Key == intentId) continue;
            if (other.Direction != direction) continue;
            if (other.EntryPrice != stopPrice) continue;
            var execInst = other.ExecutionInstrument ?? other.Instrument ?? "";
            if (string.Compare(execInst, instrument, StringComparison.OrdinalIgnoreCase) != 0 &&
                string.Compare(other.Instrument ?? "", instrument, StringComparison.OrdinalIgnoreCase) != 0)
                continue;
            if (other.StopPrice != currentIntent.StopPrice) continue;
            if (other.TargetPrice != currentIntent.TargetPrice) continue;
            if (other.TradingDate != currentIntent.TradingDate) continue;
            if (!OrderMap.TryGetValue(kvp.Key, out var orderInfo) || !orderInfo.IsEntryOrder) continue;
            if (orderInfo.State != "SUBMITTED" && orderInfo.State != "ACCEPTED" && orderInfo.State != "WORKING") continue;
            var policyQty = IntentPolicy.TryGetValue(kvp.Key, out var pol) ? pol.ExpectedQuantity : 1;
            var oco = orderInfo.OcoGroup ?? (orderInfo.NTOrder is Order o ? o.Oco ?? "" : "");
            toAggregate.Add((kvp.Key, other.Stream ?? "", policyQty, oco));
        }

        if (toAggregate.Count == 0)
            return null;

        var totalQty = quantity;
        foreach (var (_, _, q, _) in toAggregate)
            totalQty += q;

        var allIntentIds = new List<string> { intentId };
        foreach (var (id, _, _, _) in toAggregate)
            allIntentIds.Add(id);

        var qtyPerIntent = new List<object> { new { id = intentId, qty = quantity } };
        foreach (var (id, _, q, _) in toAggregate)
            qtyPerIntent.Add(new { id, qty = q });

        string? failedStep = null;
        var replacedOrderIds = new List<string>();
        var resubmittedOrderIds = new List<string>();
        try
        {
            failedStep = "CANCEL_EXISTING";
            var ordersToCancel = new List<object>();
            foreach (var (existingIntentId, _, _, _) in toAggregate)
            {
                if (OrderMap.TryGetValue(existingIntentId, out var oi) && oi.NTOrder != null)
                {
                    ordersToCancel.Add(oi.NTOrder);
                    replacedOrderIds.Add(Executor.GetOrderId(oi.NTOrder));
                }
            }
            if (ordersToCancel.Count > 0)
            {
                Executor.CancelOrders(ordersToCancel);
                foreach (var (existingIntentId, _, _, _) in toAggregate)
                {
                    if (OrderMap.TryGetValue(existingIntentId, out var oi))
                        oi.State = "CANCELLED";
                }
            }

            failedStep = "CANCEL_CURRENT_OPPOSITE";
            var oppositeIntentId = FindOppositeEntryIntentId(intentId);
            if (oppositeIntentId != null && OrderMap.TryGetValue(oppositeIntentId, out var oppOrderInfo) && oppOrderInfo.NTOrder != null)
            {
                if (oppOrderInfo.State == "SUBMITTED" || oppOrderInfo.State == "ACCEPTED" || oppOrderInfo.State == "WORKING")
                {
                    Executor.CancelOrders(new[] { oppOrderInfo.NTOrder });
                    oppOrderInfo.State = "CANCELLED";
                    replacedOrderIds.Add(Executor.GetOrderId(oppOrderInfo.NTOrder));
                }
            }

            failedStep = "SUBMIT_AGGREGATED";
            var newOcoGroup = RobotOrderIds.EncodeEntryOco(currentIntent.TradingDate ?? "", $"AGG_{string.Join("_", allIntentIds.Take(2))}", currentIntent.SlotTimeChicago ?? "");
            var aggregatedTag = RobotOrderIds.EncodeAggregatedTag(allIntentIds);

            var order = Executor.CreateStopMarketOrder(instrument, direction, totalQty, stopPrice, aggregatedTag, newOcoGroup);
            Executor.SetOrderTag(order, aggregatedTag);
            if (order is Order ntOrder) ntOrder.Oco = newOcoGroup;

            var primaryIntentId = allIntentIds[0];
            var orderInfo = new OrderInfo
            {
                IntentId = primaryIntentId,
                Instrument = instrument,
                OrderId = Executor.GetOrderId(order),
                OrderType = "ENTRY_STOP",
                Direction = direction,
                Quantity = totalQty,
                Price = stopPrice,
                State = "SUBMITTED",
                NTOrder = order,
                IsEntryOrder = true,
                FilledQuantity = 0,
                AggregatedIntentIds = allIntentIds,
                AggregatedFilledByIntent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            };
            if (IntentPolicy.TryGetValue(primaryIntentId, out var exp))
            {
                orderInfo.ExpectedQuantity = exp.ExpectedQuantity * allIntentIds.Count;
                orderInfo.MaxQuantity = exp.MaxQuantity * allIntentIds.Count;
            }

            foreach (var id in allIntentIds)
                OrderMap[id] = orderInfo;
            RegisterOrder(Executor.GetOrderId(order), primaryIntentId, instrument, currentIntent.Stream, OrderRole.ENTRY, OrderOwnershipStatus.OWNED, "TryAggregateWithExistingOrders", orderInfo, utcNow);

            var ordersToSubmit = new List<object> { order };

            var oppositeDirection = direction == "Long" ? "Short" : "Long";
            foreach (var id in allIntentIds)
            {
                var oppId = FindOppositeEntryIntentId(id);
                if (oppId == null || !IntentMap.TryGetValue(oppId, out var oppIntent)) continue;
                var oppPrice = oppIntent.EntryPrice ?? 0;
                var oppQty = IntentPolicy.TryGetValue(oppId, out var oppPol) ? oppPol.ExpectedQuantity : 1;
                var oppOrder = Executor.CreateStopMarketOrder(instrument, oppositeDirection, oppQty, oppPrice, RobotOrderIds.EncodeTag(oppId), newOcoGroup);
                if (oppOrder is Order oppNtOrder) oppNtOrder.Oco = newOcoGroup;
                ordersToSubmit.Add(oppOrder);
                resubmittedOrderIds.Add(Executor.GetOrderId(oppOrder));
                var oppOi = new OrderInfo
                {
                    IntentId = oppId,
                    Instrument = instrument,
                    OrderId = Executor.GetOrderId(oppOrder),
                    OrderType = "ENTRY_STOP",
                    Direction = oppositeDirection,
                    Quantity = oppQty,
                    Price = oppPrice,
                    State = "SUBMITTED",
                    NTOrder = oppOrder,
                    IsEntryOrder = true,
                    FilledQuantity = 0
                };
                OrderMap[oppId] = oppOi;
                RegisterOrder(Executor.GetOrderId(oppOrder), oppId, instrument, oppIntent.Stream, OrderRole.ENTRY, OrderOwnershipStatus.OWNED, "TryAggregateWithExistingOrders_Opposite", oppOi, utcNow);
                Executor.RecordSubmission(oppId, oppIntent.TradingDate ?? "", oppIntent.Stream ?? "", instrument, $"ENTRY_STOP_{oppositeDirection}", Executor.GetOrderId(oppOrder), utcNow);
                TryTransitionIntentLifecycle(oppId, IntentLifecycleTransition.SUBMIT_ENTRY, null, utcNow);
            }

            Executor.SubmitOrders(ordersToSubmit);

            foreach (var id in allIntentIds)
            {
                if (IntentMap.TryGetValue(id, out var intent))
                    Executor.RecordSubmission(id, intent.TradingDate ?? "", intent.Stream ?? "", instrument, $"ENTRY_STOP_{direction}", Executor.GetOrderId(order), utcNow);
            }

            foreach (var id in allIntentIds)
                TryTransitionIntentLifecycle(id, IntentLifecycleTransition.SUBMIT_ENTRY, null, utcNow);

            Log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENTRY_AGGREGATION_SUCCESS", state: "ENGINE",
                new
                {
                    agg_tag = aggregatedTag,
                    broker_order_id = Executor.GetOrderId(order),
                    aggregated_intents = allIntentIds,
                    total_quantity = totalQty,
                    oco_group = newOcoGroup,
                    replaced_order_ids = replacedOrderIds,
                    resubmitted_order_ids = resubmittedOrderIds,
                    iea_instance_id = InstanceId,
                    note = "IEA: One broker order for multiple streams"
                }));

            return OrderSubmissionResult.SuccessResult(Executor.GetOrderId(order), utcNow);
        }
        catch (Exception ex)
        {
            var exposureAtFailure = 0;
            try
            {
                var pos = account.Positions?.FirstOrDefault(p =>
                    string.Equals(p.Instrument?.MasterInstrument?.Name ?? "", instrument, StringComparison.OrdinalIgnoreCase));
                if (pos != null)
                    exposureAtFailure = Math.Abs(pos.Quantity);
            }
            catch { /* best-effort */ }

            Log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENTRY_AGGREGATION_FAILED", state: "ENGINE",
                new
                {
                    failed_step = failedStep ?? "UNKNOWN",
                    nt_error = ex.Message,
                    action_taken = "STAND_DOWN",
                    exposure_at_failure = exposureAtFailure,
                    existing_intents = toAggregate.Select(x => x.intentId).ToList(),
                    iea_instance_id = InstanceId,
                    note = "IEA: Aggregation failed"
                }));
            return OrderSubmissionResult.FailureResult($"Entry aggregation failed at {failedStep}: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// Phase 2: Allocate fill quantity to intents deterministically (sorted intentId order).
    /// Updates orderInfo.AggregatedFilledByIntent. Used for aggregated entry fills.
    /// </summary>
    internal List<(string allocIntentId, int allocQty)> AllocateFillToIntents(
        IReadOnlyList<string> intentIds,
        int fillQuantity,
        OrderInfo orderInfo)
    {
        var result = new List<(string, int)>();
        if (fillQuantity <= 0 || intentIds == null || intentIds.Count == 0)
            return result;

        if (intentIds.Count == 1)
        {
            result.Add((intentIds[0], fillQuantity));
            return result;
        }

        var sorted = intentIds.OrderBy(id => id, StringComparer.Ordinal).ToList();
        orderInfo.AggregatedFilledByIntent ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var remaining = fillQuantity;
        foreach (var id in sorted)
        {
            if (remaining <= 0) break;
            var policyQty = IntentPolicy.TryGetValue(id, out var pol) ? pol.ExpectedQuantity : 1;
            var current = orderInfo.AggregatedFilledByIntent.TryGetValue(id, out var cur) ? cur : 0;
            var needed = policyQty - current;
            if (needed <= 0) continue;
            var toAlloc = Math.Min(needed, remaining);
            if (toAlloc > 0)
            {
                result.Add((id, toAlloc));
                orderInfo.AggregatedFilledByIntent[id] = current + toAlloc;
                remaining -= toAlloc;
            }
        }
        return result;
    }

    /// <summary>
    /// Phase 2 / Trap F: OCO group generation owned by IEA, deterministic: executionInstrumentKey + trading_date + intentId.
    /// trading_date: intent.TradingDate from engine (America/Chicago session resolution). intentId: hash of canonical fields (globally unique).
    /// </summary>
    private string GenerateProtectiveOcoGroup(string intentId, int attempt, Intent intent)
    {
        var tradingDate = intent.TradingDate ?? "";
        return $"QTSW2:{ExecutionInstrumentKey}_{tradingDate}_{intentId}_PROTECTIVE_A{attempt}";
    }

    private string? FindOppositeEntryIntentId(string intentId)
    {
        if (!IntentMap.TryGetValue(intentId, out var intent))
            return null;
        var oppositeDirection = intent.Direction == "Long" ? "Short" : "Long";
        var oppositeTrigger = oppositeDirection == "Long" ? "ENTRY_STOP_BRACKET_LONG" : "ENTRY_STOP_BRACKET_SHORT";
        foreach (var kvp in IntentMap.OrderBy(k => k.Key))
        {
            var other = kvp.Value;
            if (other.Stream == intent.Stream &&
                other.TradingDate == intent.TradingDate &&
                other.Direction == oppositeDirection &&
                other.TriggerReason != null &&
                other.TriggerReason.Contains(oppositeTrigger))
                return kvp.Key;
        }
        return null;
    }

    /// <summary>
    /// Phase 2: Handle entry fill and submit protective orders. IEA owns the flow; delegates NT ops to executor.
    /// </summary>
    public void HandleEntryFill(string intentId, Intent intent, decimal fillPrice, int fillQuantity, int totalFilledQuantity, DateTimeOffset utcNow)
    {
        if (Executor == null || Log == null) return;

        // Idempotency: if protectives already working, adopt if match policy else fail-close
        // Gap 2: Quantity mismatch after restart → fail-close (not resize). Risk coverage is ambiguous.
        if (Executor.HasWorkingProtectivesForIntent(intentId))
        {
            var (existingStop, existingTarget, stopQty, targetQty) = Executor.GetWorkingProtectiveState(intentId);
            if (existingStop.HasValue && existingTarget.HasValue && intent.StopPrice.HasValue && intent.TargetPrice.HasValue)
            {
                var tickSize = Executor.GetTickSize();
                var tolTicks = AggregationPolicy?.BracketToleranceTicks ?? 0;
                var tolerance = tickSize * tolTicks;
                var stopMatch = Math.Abs(existingStop.Value - intent.StopPrice.Value) <= tolerance;
                var targetMatch = Math.Abs(existingTarget.Value - intent.TargetPrice.Value) <= tolerance;
                if (stopMatch && targetMatch)
                {
                    // Gap 2: Validate quantity. Quantity mismatch after restart → fail-close (not resize).
                    if (stopQty.HasValue && stopQty.Value != totalFilledQuantity)
                    {
                        var stopReason = $"Protective stop quantity mismatch: stopQty={stopQty}, totalFilledQuantity={totalFilledQuantity}";
                        Log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_DRIFT_DETECTED", new
                        {
                            drift_type = "stop_quantity_mismatch",
                            position_qty = totalFilledQuantity,
                            expected_protective_qty = totalFilledQuantity,
                            actual_protective_qty = stopQty,
                            expected_stop_price = intent.StopPrice,
                            actual_stop_price = existingStop,
                            expected_target_price = intent.TargetPrice,
                            actual_target_price = existingTarget,
                            stream_key = intent.Stream,
                            intent_id = intentId,
                            instrument = intent.Instrument,
                            iea_instance_id = InstanceId
                        }));
                        Log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_QUANTITY_MISMATCH_FAIL_CLOSE",
                            new { error = stopReason, intent_id = intentId, stop_qty = stopQty, total_filled_quantity = totalFilledQuantity, iea_instance_id = InstanceId }));
                        Executor.FailClosed(intentId, intent, stopReason, "PROTECTIVE_QUANTITY_MISMATCH_FLATTENED", $"PROTECTIVE_QUANTITY_MISMATCH:{intentId}",
                            $"CRITICAL: Protective Quantity Mismatch - {intent.Instrument}",
                            $"Protective stop quantity does not match journal. Position flattened. Stream: {intent.Stream}, Intent: {intentId}. {stopReason}",
                            null, null, new { stop_qty = stopQty, total_filled_quantity = totalFilledQuantity }, utcNow);
                        return;
                    }
                    if (targetQty.HasValue && targetQty.Value != totalFilledQuantity)
                    {
                        var targetReason = $"Protective target quantity mismatch: targetQty={targetQty}, totalFilledQuantity={totalFilledQuantity}";
                        Log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_DRIFT_DETECTED", new
                        {
                            drift_type = "target_quantity_mismatch",
                            position_qty = totalFilledQuantity,
                            expected_protective_qty = totalFilledQuantity,
                            actual_protective_qty = targetQty,
                            expected_stop_price = intent.StopPrice,
                            actual_stop_price = existingStop,
                            expected_target_price = intent.TargetPrice,
                            actual_target_price = existingTarget,
                            stream_key = intent.Stream,
                            intent_id = intentId,
                            instrument = intent.Instrument,
                            iea_instance_id = InstanceId
                        }));
                        Log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_QUANTITY_MISMATCH_FAIL_CLOSE",
                            new { error = targetReason, intent_id = intentId, target_qty = targetQty, total_filled_quantity = totalFilledQuantity, iea_instance_id = InstanceId }));
                        Executor.FailClosed(intentId, intent, targetReason, "PROTECTIVE_QUANTITY_MISMATCH_FLATTENED", $"PROTECTIVE_QUANTITY_MISMATCH:{intentId}",
                            $"CRITICAL: Protective Quantity Mismatch - {intent.Instrument}",
                            $"Protective target quantity does not match journal. Position flattened. Stream: {intent.Stream}, Intent: {intentId}. {targetReason}",
                            null, null, new { target_qty = targetQty, total_filled_quantity = totalFilledQuantity }, utcNow);
                        return;
                    }
                    if (OrderMap.TryGetValue(intentId, out var oi))
                        oi.EntryFillTime = utcNow;
                    return;
                }
                var reason = $"Existing protectives mismatch policy: stop {existingStop} vs {intent.StopPrice}, target {existingTarget} vs {intent.TargetPrice}";
                Log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_DRIFT_DETECTED", new
                {
                    drift_type = "price_mismatch",
                    position_qty = totalFilledQuantity,
                    expected_stop_price = intent.StopPrice,
                    actual_stop_price = existingStop,
                    expected_target_price = intent.TargetPrice,
                    actual_target_price = existingTarget,
                    expected_protective_qty = totalFilledQuantity,
                    actual_protective_qty = stopQty ?? targetQty,
                    stream_key = intent.Stream,
                    intent_id = intentId,
                    instrument = intent.Instrument,
                    iea_instance_id = InstanceId
                }));
                Log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_MISMATCH_FAIL_CLOSE",
                    new { error = reason, intent_id = intentId, existing_stop = existingStop, existing_target = existingTarget, expected_stop = intent.StopPrice, expected_target = intent.TargetPrice, iea_instance_id = InstanceId }));
                Executor.FailClosed(intentId, intent, reason, "PROTECTIVE_MISMATCH_FLATTENED", $"PROTECTIVE_MISMATCH:{intentId}",
                    $"CRITICAL: Protective Mismatch - {intent.Instrument}",
                    $"Existing protectives do not match policy. Position flattened. Stream: {intent.Stream}, Intent: {intentId}. {reason}",
                    null, null, new { existing_stop = existingStop, existing_target = existingTarget }, utcNow);
                return;
            }
            // Has working protectives but couldn't read prices - adopt anyway (conservative)
            if (OrderMap.TryGetValue(intentId, out var oi2))
                oi2.EntryFillTime = utcNow;
            return;
        }

        if (OrderMap.TryGetValue(intentId, out var entryOrderInfo))
        {
            entryOrderInfo.EntryFillTime = utcNow;
            entryOrderInfo.ProtectiveStopAcknowledged = false;
            entryOrderInfo.ProtectiveTargetAcknowledged = false;
        }

        var missingFields = new List<string>();
        if (intent.Direction == null) missingFields.Add("Direction");
        if (intent.StopPrice == null) missingFields.Add("StopPrice");
        if (intent.TargetPrice == null) missingFields.Add("TargetPrice");

        if (missingFields.Count > 0)
        {
            var failureReason = $"Intent incomplete - missing fields: {string.Join(", ", missingFields)}";
            Log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "INTENT_INCOMPLETE_UNPROTECTED_POSITION",
                new { error = failureReason, intent_id = intentId, missing_fields = missingFields, iea_instance_id = InstanceId }));
            Executor.FailClosed(intentId, intent, failureReason, "INTENT_INCOMPLETE_FLATTENED", $"INTENT_INCOMPLETE:{intentId}",
                $"CRITICAL: Intent Incomplete - Unprotected Position - {intent.Instrument}",
                $"Entry filled but intent incomplete (missing: {string.Join(", ", missingFields)}). Position flattened. Stream: {intent.Stream}, Intent: {intentId}.",
                null, null, new { missing_fields = missingFields }, utcNow);
            return;
        }

        // Stage 1: Entry fill during recovery — queue protective submission (three-stage safety model)
        if (!Executor.IsExecutionAllowed())
        {
            if (Executor.TryQueueProtectiveForRecovery(intentId, intent, totalFilledQuantity, utcNow))
            {
                Log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_ORDERS_QUEUED_RECOVERY",
                    new { intent_id = intentId, total_filled_quantity = totalFilledQuantity, iea_instance_id = InstanceId }));
                return;
            }
            var error = "Execution blocked - recovery state guard active.";
            Log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_ORDERS_BLOCKED_RECOVERY",
                new { error, intent_id = intentId, iea_instance_id = InstanceId }));
            Executor.FailClosed(intentId, intent, error, "PROTECTIVE_ORDERS_BLOCKED_RECOVERY_FLATTENED", $"PROTECTIVE_BLOCKED_RECOVERY:{intentId}",
                $"CRITICAL: Protective Orders Blocked - Recovery State - {intent.Instrument}",
                $"Entry filled but protective orders blocked due to recovery state. Position flattened. Stream: {intent.Stream}, Intent: {intentId}.",
                null, null, new { error }, utcNow);
            return;
        }

        if (!Executor.CanSubmitExit(intentId, totalFilledQuantity))
        {
            Log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "EXECUTION_ERROR",
                new { error = "Exit validation failed", intent_id = intentId, total_filled_quantity = totalFilledQuantity, iea_instance_id = InstanceId }));
            return;
        }

        // NT THREADING FIX: Worker MUST NOT call account.Change/Cancel/Flatten. Enqueue for strategy thread.
        var correlationId = $"PROTECTIVES:{intentId}:{utcNow:yyyyMMddHHmmssfff}";
        var cmd = new NtSubmitProtectivesCommand(
            correlationId,
            intentId,
            intent.Instrument ?? "",
            intent.Direction!,
            intent.StopPrice!.Value,
            intent.TargetPrice!.Value,
            totalFilledQuantity,
            null, // OcoGroup generated in ExecuteSubmitProtectives
            "ENTRY_FILL",
            utcNow);
        Executor.EnqueueNtAction(cmd);
    }

    /// <summary>Enqueue execution update for serialized processing. Uses EnqueueRecoveryEssential. Runs <see cref="IIEAOrderExecutor.ProcessExecutionUpdate"/> before first-adoption <c>RequestAdoptionScan</c> so the live event updates state first.</summary>
    public void EnqueueExecutionUpdate(object execution, object order)
    {
        if (Executor == null)
            return;
        EnqueueRecoveryEssential(() =>
        {
            // Phase 4: Critical event during bootstrap marks snapshot stale (fill or order state change)
            if (IsInBootstrap && IsCriticalBootstrapEvent(execution, order))
            {
                MarkBootstrapSnapshotStale(NowEvent());
            }
            // Process broker execution FIRST so fills/order state advance immediately; adoption is a follow-on consistency pass.
            Executor.ProcessExecutionUpdate(execution, order);
            // Phase 4: ScanAndAdopt after bootstrap complete (or was run in ADOPT path). Single-flight gate; runs on IEA worker after real execution handling.
            if (!_hasScannedForAdoption && !IsInBootstrap && (CurrentRecoveryState == RecoveryState.NORMAL || CurrentRecoveryState == RecoveryState.RESOLVED))
                _ = RequestAdoptionScan(AdoptionScanRequestSource.FirstExecutionUpdate, applyRecoveryThrottle: false, postScanOnWorker: null);
        }, "ExecutionUpdate");
    }

    /// <summary>Phase 4: True if execution/order represents a critical event (fill or order state change to Working/Filled/Cancelled).</summary>
    private static bool IsCriticalBootstrapEvent(object execution, object order)
    {
        try
        {
            dynamic dynExec = execution;
            var qty = (int)(dynExec.Quantity ?? 0);
            if (qty != 0) return true; // Fill
            dynamic dynOrder = order;
            var state = (dynOrder.OrderState ?? "").ToString();
            if (state.IndexOf("Working", StringComparison.OrdinalIgnoreCase) >= 0 ||
                state.IndexOf("Filled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                state.IndexOf("Cancelled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                state.IndexOf("Rejected", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        catch { }
        return false;
    }

    private enum AdoptionScanRequestSource
    {
        FirstExecutionUpdate,
        DeferredRetry,
        RecoveryAdoption,
        Bootstrap,
        Other
    }

    private enum AdoptionScanRequestLogOutcome
    {
        AcceptedAndQueued,
        AcceptedInline,
        SkippedAlreadyRunning,
        SkippedAlreadyQueued,
        SkippedThrottled
    }

    private readonly struct AdoptionScanRequestDispatchResult
    {
        public AdoptionScanRequestLogOutcome Outcome { get; }
        public int AdoptedDeltaIfInline { get; }
        public int QueueDepthAtDecision { get; }

        public AdoptionScanRequestDispatchResult(AdoptionScanRequestLogOutcome outcome, int adoptedDeltaIfInline = 0, int queueDepthAtDecision = 0)
        {
            Outcome = outcome;
            AdoptedDeltaIfInline = adoptedDeltaIfInline;
            QueueDepthAtDecision = queueDepthAtDecision;
        }
    }

    private bool _hasScannedForAdoption;
    private DateTimeOffset? _firstAdoptionScanUtc;
    private int _adoptionDeferredCount;
    private bool _adoptionDeferred;
    private readonly AdoptionScanSingleFlightGate _adoptionSingleFlightGate = new();
    private string? _adoptionScanExecutionEpisodeId;
    private readonly Dictionary<string, DateTimeOffset> _adoptionScanSkipLogLastUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _adoptionScanSkipLogLock = new();

    /// <summary>Recovery adoption: skip redundant full scans when fingerprint unchanged, last run adopted nothing, within cooldown, and no IEA mutations since last scan.</summary>
    private const double RecoveryAdoptionNoProgressCooldownSeconds = 20.0;
    private bool _hasLastRecoveryScanSnapshot;
    private AdoptionScanRecoveryFingerprint _lastRecoveryScanFingerprint;
    private int _lastRecoveryScanAdoptedDelta;
    private DateTimeOffset _lastRecoveryScanCompletedUtc = DateTimeOffset.MinValue;

    /// <summary>Per-phase wall times for IEA_ADOPTION_SCAN_PHASE_TIMING (hot-path audit).</summary>
    private struct AdoptionScanPhaseTelemetry
    {
        public long FingerprintBuildMs;
        public long CandidatesMs;
        public long PrecountMs;
        public long JournalDiagMs;
        public long PreLoopLogMs;
        public long MainLoopMs;
        public long SummaryMs;
        public int AccountOrdersTotal;
        public int CandidateIntentCount;
        public int Qtsw2SameInstrumentWorking;
        public int MainLoopOrdersSeen;
        public int ScanAdoptedInLoop;
    }

    // --- Adoption episode + same-state proof (IEA_CPU_PROOF_INSTRUMENTATION_PLAN_2026-03-23.md)
    private string? _adoptionEpisodeId;
    private readonly Dictionary<string, DateTimeOffset> _expensivePathThreadLastLogUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _expensivePathThreadLock = new();
    private string? _lastAdoptionStateFingerprint;
    private int _adoptionFingerprintRepeatCount;
    private DateTimeOffset _adoptionFingerprintWindowStartUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSameStateRetryEmitUtc = DateTimeOffset.MinValue;

    private void EnsureAdoptionEpisode(string reason)
    {
        _ = reason;
        if (!string.IsNullOrEmpty(_adoptionEpisodeId)) return;
        _adoptionEpisodeId = Guid.NewGuid().ToString("N");
    }

    private void EndAdoptionEpisode(string reason)
    {
        _ = reason;
        _adoptionEpisodeId = null;
    }

    /// <summary>Payload for <see cref="InstrumentExecutionAuthorityRegistry.RetryDeferredAdoptionScansForAccount"/> rows.</summary>
    internal object GetAdoptionDeferralRetryProofPayload() => new
    {
        adoption_episode_id = _adoptionEpisodeId,
        deferred = _adoptionDeferred,
        adoption_scan_gate = _adoptionSingleFlightGate.State.ToString(),
        adoption_scan_execution_episode_id = _adoptionScanExecutionEpisodeId,
        iea_instance_id = InstanceId,
        execution_instrument_key = ExecutionInstrumentKey
    };

    internal void RecordDeferralHeartbeatRetryForProof(DateTimeOffset utcNow)
    {
        var fp = $"deferral_retry|{ExecutionInstrumentKey}|{_adoptionDeferred}|{_adoptionSingleFlightGate.State}|{_adoptionEpisodeId ?? ""}";
        TrackAdoptionSameStateRetry(utcNow, fp);
    }

    private void MaybeLogExpensivePathThread(DateTimeOffset utcNow, string path)
    {
        if (Log == null) return;
        lock (_expensivePathThreadLock)
        {
            if (_expensivePathThreadLastLogUtc.TryGetValue(path, out var last) &&
                (utcNow - last).TotalSeconds < 60)
                return;
            _expensivePathThreadLastLogUtc[path] = utcNow;
        }
        var t = Thread.CurrentThread;
        LogIeaEngine(utcNow, "IEA_EXPENSIVE_PATH_THREAD", new
        {
            path,
            thread_id = t.ManagedThreadId,
            thread_name = t.Name,
            on_iea_worker = t == _workerThread,
            iea_instance_id = InstanceId,
            execution_instrument_key = ExecutionInstrumentKey
        });
    }

    private void TrackAdoptionSameStateRetry(DateTimeOffset utcNow, string fingerprint)
    {
        if (Log == null) return;
        const int repeatThreshold = 5;
        const double windowSec = 90;
        if (_adoptionFingerprintWindowStartUtc == DateTimeOffset.MinValue ||
            (utcNow - _adoptionFingerprintWindowStartUtc).TotalSeconds > windowSec)
        {
            _adoptionFingerprintWindowStartUtc = utcNow;
            _lastAdoptionStateFingerprint = fingerprint;
            _adoptionFingerprintRepeatCount = 1;
            return;
        }
        if (string.Equals(_lastAdoptionStateFingerprint, fingerprint, StringComparison.Ordinal))
            _adoptionFingerprintRepeatCount++;
        else
        {
            _lastAdoptionStateFingerprint = fingerprint;
            _adoptionFingerprintRepeatCount = 1;
        }
        if (_adoptionFingerprintRepeatCount < repeatThreshold) return;
        if (_lastSameStateRetryEmitUtc != DateTimeOffset.MinValue &&
            (utcNow - _lastSameStateRetryEmitUtc).TotalSeconds < 60)
            return;
        _lastSameStateRetryEmitUtc = utcNow;
        LogIeaEngine(utcNow, "ADOPTION_SAME_STATE_RETRY_WINDOW", new
        {
            fingerprint,
            repeat_count = _adoptionFingerprintRepeatCount,
            window_sec = windowSec,
            iea_instance_id = InstanceId,
            execution_instrument_key = ExecutionInstrumentKey,
            adoption_episode_id = _adoptionEpisodeId,
            note = "Same adoption/recovery fingerprint repeated within rolling window — no material state change"
        });
    }

    private void MaybeLogAdoptionScanRequestSkipped(DateTimeOffset utcNow, AdoptionScanRequestSource source, AdoptionScanRequestLogOutcome outcome)
    {
        if (Log == null) return;
        var reason = outcome switch
        {
            AdoptionScanRequestLogOutcome.SkippedAlreadyRunning => "skipped_already_running",
            AdoptionScanRequestLogOutcome.SkippedAlreadyQueued => "skipped_already_queued",
            AdoptionScanRequestLogOutcome.SkippedThrottled => "skipped_throttled",
            _ => "unknown"
        };
        var key = $"{source}|{reason}";
        lock (_adoptionScanSkipLogLock)
        {
            if (_adoptionScanSkipLogLastUtc.TryGetValue(key, out var last) && (utcNow - last).TotalSeconds < 60)
                return;
            _adoptionScanSkipLogLastUtc[key] = utcNow;
        }
        var t = Thread.CurrentThread;
        LogIeaEngine(utcNow, "IEA_ADOPTION_SCAN_REQUEST_SKIPPED", new
        {
            scan_request_source = source.ToString(),
            disposition = reason,
            adoption_scan_gate = _adoptionSingleFlightGate.State.ToString(),
            thread_id = t.ManagedThreadId,
            thread_name = t.Name,
            on_iea_worker = t == _workerThread,
            iea_instance_id = InstanceId,
            execution_instrument_key = ExecutionInstrumentKey
        });
    }

    private void LogAdoptionScanRequestAccepted(DateTimeOffset utcNow, AdoptionScanRequestSource source, string disposition, int queueDepth, bool onWorker)
    {
        if (Log == null) return;
        var t = Thread.CurrentThread;
        LogIeaEngine(utcNow, "IEA_ADOPTION_SCAN_REQUEST_ACCEPTED", new
        {
            scan_request_source = source.ToString(),
            disposition,
            queue_depth_at_accept = queueDepth,
            adoption_scan_gate = _adoptionSingleFlightGate.State.ToString(),
            thread_id = t.ManagedThreadId,
            thread_name = t.Name,
            on_iea_worker = onWorker,
            iea_instance_id = InstanceId,
            execution_instrument_key = ExecutionInstrumentKey
        });
    }

    /// <param name="deferWorkerMutationUtcToAlignUtc">
    /// When true, the IEA worker loop uses the recovery scan completion time for LastMutationUtc instead of UtcNow (queued adoption only).
    /// Inline adoption must pass false so a pending align is not consumed by the next queue item.
    /// </param>
    private void RunGatedAdoptionScanBody(AdoptionScanRequestSource source, Action? postScanOnWorker, string adoptionScanEpisodeId, bool deferWorkerMutationUtcToAlignUtc)
    {
        var utcStart = NowEvent();
        var sw = Stopwatch.StartNew();
        var before = GetOwnedPlusAdoptedWorkingCount();
        var heavyScanExecuted = false;
        var recoveryFpOk = false;
        AdoptionScanRecoveryFingerprint recoveryFpAtStart = default;
        var phaseTelemetry = new AdoptionScanPhaseTelemetry();
        try
        {
            _preResolvedAdoptionCandidatesForScan = null;
            _adoptionScanProofCandidateCountOverride = null;

            if (source == AdoptionScanRequestSource.RecoveryAdoption && Executor != null)
            {
                var swCand = Stopwatch.StartNew();
                _preResolvedAdoptionCandidatesForScan = Executor.GetAdoptionCandidateIntentIds(ExecutionInstrumentKey);
                swCand.Stop();
                phaseTelemetry.CandidatesMs = swCand.ElapsedMilliseconds;
                _adoptionScanProofCandidateCountOverride = _preResolvedAdoptionCandidatesForScan.Count;
            }

            if (source == AdoptionScanRequestSource.RecoveryAdoption)
            {
                var swFp = Stopwatch.StartNew();
                try
                {
                    if (TryBuildRecoveryScanFingerprint(out recoveryFpAtStart))
                    {
                        recoveryFpOk = true;
                        if (RecoveryNoProgressSkipEvaluator.ShouldSkipRecoveryNoProgress(
                                _hasLastRecoveryScanSnapshot,
                                in recoveryFpAtStart,
                                in _lastRecoveryScanFingerprint,
                                _lastRecoveryScanAdoptedDelta,
                                _lastRecoveryScanCompletedUtc,
                                utcStart,
                                RecoveryAdoptionNoProgressCooldownSeconds,
                                LastMutationUtc))
                        {
                            MaybeLogAdoptionScanSkippedNoProgress(utcNow: utcStart, adoptionScanEpisodeId, in recoveryFpAtStart);
                            return;
                        }

                        if (_hasLastRecoveryScanSnapshot &&
                            _lastRecoveryScanAdoptedDelta == 0 &&
                            AdoptionScanRecoveryFingerprint.CountFieldMismatches(in recoveryFpAtStart, in _lastRecoveryScanFingerprint) <= 1)
                        {
                            var noProgDiag = RecoveryNoProgressSkipEvaluator.BuildDiagnosticSnapshot(
                                scanIsRecovery: true,
                                _hasLastRecoveryScanSnapshot,
                                in recoveryFpAtStart,
                                in _lastRecoveryScanFingerprint,
                                _lastRecoveryScanAdoptedDelta,
                                _lastRecoveryScanCompletedUtc,
                                utcStart,
                                RecoveryAdoptionNoProgressCooldownSeconds,
                                LastMutationUtc);
                            MaybeLogAdoptionScanNoProgressNotSkipped(utcStart, adoptionScanEpisodeId, noProgDiag, in recoveryFpAtStart, in _lastRecoveryScanFingerprint);
                        }
                    }
                }
                finally
                {
                    swFp.Stop();
                    phaseTelemetry.FingerprintBuildMs = swFp.ElapsedMilliseconds;
                }
            }

            LogIeaEngine(utcStart, "IEA_ADOPTION_SCAN_EXECUTION_STARTED", new
            {
                adoption_scan_episode_id = adoptionScanEpisodeId,
                scan_request_source = source.ToString(),
                iea_instance_id = InstanceId,
                execution_instrument_key = ExecutionInstrumentKey
            });
            ScanAndAdoptExistingOrders(source, ref phaseTelemetry);
            postScanOnWorker?.Invoke();
            heavyScanExecuted = true;
        }
        finally
        {
            _preResolvedAdoptionCandidatesForScan = null;
            _adoptionScanProofCandidateCountOverride = null;

            sw.Stop();
            var after = GetOwnedPlusAdoptedWorkingCount();
            var utcDone = NowEvent();
            if (heavyScanExecuted)
            {
                var adoptedDelta = Math.Max(0, after - before);
                LogIeaEngine(utcDone, "IEA_ADOPTION_SCAN_EXECUTION_COMPLETED", new
                {
                    adoption_scan_episode_id = adoptionScanEpisodeId,
                    scan_request_source = source.ToString(),
                    scan_wall_ms = sw.ElapsedMilliseconds,
                    adopted_delta = adoptedDelta,
                    account_orders_total = TryGetAccountOrdersTotalForAdoptionProof(),
                    same_instrument_qtsw2_working_count = TryGetSameInstrumentQtsw2WorkingForAdoptionProof(),
                    candidate_intent_count = TryGetAdoptionCandidateCountForProof(),
                    deferred_state = _adoptionDeferred,
                    iea_instance_id = InstanceId,
                    execution_instrument_key = ExecutionInstrumentKey,
                    recovery_fingerprint_at_start_account_orders = recoveryFpOk ? recoveryFpAtStart.AccountOrdersTotal : (int?)null,
                    recovery_fingerprint_at_start_candidate_intent_count = recoveryFpOk ? recoveryFpAtStart.CandidateIntentCount : (int?)null,
                    recovery_fingerprint_at_start_qtsw2_working = recoveryFpOk ? recoveryFpAtStart.SameInstrumentQtsw2WorkingCount : (int?)null,
                    recovery_fingerprint_at_start_deferred = recoveryFpOk ? recoveryFpAtStart.DeferredState : (bool?)null,
                    recovery_fingerprint_at_start_broker_working_exec = recoveryFpOk ? recoveryFpAtStart.BrokerWorkingExecutionInstrumentCount : (int?)null,
                    recovery_fingerprint_at_start_iea_registry_working = recoveryFpOk ? recoveryFpAtStart.IeaRegistryWorkingCount : (int?)null,
                    last_scan_completed_utc_before_this_run = _lastRecoveryScanCompletedUtc == DateTimeOffset.MinValue ? null : _lastRecoveryScanCompletedUtc.ToString("o")
                });
                MaybeLogAdoptionScanPhaseTiming(utcDone, source, adoptionScanEpisodeId, sw.ElapsedMilliseconds, ref phaseTelemetry, adoptedDelta);
                if (source == AdoptionScanRequestSource.RecoveryAdoption && recoveryFpOk)
                {
                    _hasLastRecoveryScanSnapshot = true;
                    _lastRecoveryScanFingerprint = recoveryFpAtStart;
                    _lastRecoveryScanAdoptedDelta = adoptedDelta;
                    _lastRecoveryScanCompletedUtc = utcDone;
                    // Prevent recovery scan from self-invalidating no-progress guard (WorkerLoop would otherwise set LastMutationUtc ~1–3s later).
                    var previousLastMutationUtc = _lastMutationUtc;
                    _lastMutationUtc = _lastRecoveryScanCompletedUtc;
                    if (deferWorkerMutationUtcToAlignUtc)
                        _pendingRecoveryAdoptionMutationAlignUtc = _lastRecoveryScanCompletedUtc;
                    LogIeaEngine(utcDone, "IEA_ADOPTION_SCAN_MUTATION_TIME_ALIGNED", new
                    {
                        iea_instance_id = InstanceId,
                        execution_instrument_key = ExecutionInstrumentKey,
                        previous_last_mutation_utc = previousLastMutationUtc == DateTimeOffset.MinValue ? null : previousLastMutationUtc.ToString("o"),
                        new_last_mutation_utc = _lastMutationUtc.ToString("o"),
                        scan_completed_utc = utcDone.ToString("o")
                    });
                }
            }
            _adoptionSingleFlightGate.EndRun();
        }
    }

    /// <summary>Single pass over account orders for counts used in recovery no-progress fingerprint (no adoption mutations).</summary>
    private bool TryBuildRecoveryScanFingerprint(out AdoptionScanRecoveryFingerprint fp)
    {
        fp = new AdoptionScanRecoveryFingerprint("", -1, -1, -1, false, -1, -1);
        if (Executor == null) return false;
        try
        {
            if (Executor.GetAccount() is not Account account || account.Orders == null)
                return false;
            var accountTotal = account.Orders.Count;
            var brokerWorkingExec = 0;
            var qtsw2Same = 0;
            foreach (Order o in account.Orders)
            {
                if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted)
                    continue;
                var broName = o.Instrument?.MasterInstrument?.Name ?? "";
                if (!AdoptionScanInstrumentGate.BrokerOrderMatchesExecutionInstrument(ExecutionInstrumentKey, broName))
                    continue;
                brokerWorkingExec++;
                var tag = Executor.GetOrderTag(o);
                if (!string.IsNullOrEmpty(tag) && tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase))
                    qtsw2Same++;
            }
            var cand = _preResolvedAdoptionCandidatesForScan != null
                ? _preResolvedAdoptionCandidatesForScan.Count
                : Executor.GetAdoptionCandidateIntentIds(ExecutionInstrumentKey).Count;
            var ieaW = GetOwnedPlusAdoptedWorkingCount();
            fp = new AdoptionScanRecoveryFingerprint(
                ExecutionInstrumentKey,
                accountTotal,
                cand,
                qtsw2Same,
                _adoptionDeferred,
                brokerWorkingExec,
                ieaW);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Rate-limited proof of why recovery no-progress skip was not taken when eligibility was borderline (does not change skip logic).</summary>
    private void MaybeLogAdoptionScanNoProgressNotSkipped(
        DateTimeOffset utcNow,
        string adoptionScanEpisodeId,
        RecoveryNoProgressSkipDiagnosticSnapshot d,
        in AdoptionScanRecoveryFingerprint current,
        in AdoptionScanRecoveryFingerprint previous)
    {
        if (Log == null) return;
        var rateKey = $"no_prog_not_skip|{InstanceId}|{d.SkipBlockedReason}|IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED";
        lock (_adoptionScanSkipLogLock)
        {
            if (_adoptionScanSkipLogLastUtc.TryGetValue(rateKey, out var last) && (utcNow - last).TotalSeconds < 50)
                return;
            _adoptionScanSkipLogLastUtc[rateKey] = utcNow;
        }

        LogIeaEngine(utcNow, "IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED", new
        {
            adoption_scan_episode_id = adoptionScanEpisodeId,
            scan_request_source = AdoptionScanRequestSource.RecoveryAdoption.ToString(),
            iea_instance_id = InstanceId,
            execution_instrument_key = ExecutionInstrumentKey,
            has_last_completed_recovery_scan = d.HasLastCompletedRecoveryScan,
            last_completed_adopted_delta_zero = d.LastCompletedAdoptedDeltaZero,
            fingerprint_equal = d.FingerprintEqual,
            fingerprint_field_mismatch_count = d.FingerprintFieldMismatchCount,
            cooldown_positive = d.CooldownPositive,
            last_completed_utc_valid = d.LastCompletedUtcValid,
            within_cooldown = d.WithinCooldown,
            last_iea_mutation_lte_last_completed = d.LastIeaMutationLteLastCompleted,
            skip_blocked_reason = d.SkipBlockedReason,
            seconds_since_last_completed = d.SecondsSinceLastCompleted,
            cooldown_sec = RecoveryAdoptionNoProgressCooldownSeconds,
            last_scan_completed_utc = _lastRecoveryScanCompletedUtc == DateTimeOffset.MinValue ? null : _lastRecoveryScanCompletedUtc.ToString("o"),
            last_iea_mutation_utc = LastMutationUtc == DateTimeOffset.MinValue ? null : LastMutationUtc.ToString("o"),
            current_account_orders_total = current.AccountOrdersTotal,
            current_candidate_intent_count = current.CandidateIntentCount,
            current_same_instrument_qtsw2_working_count = current.SameInstrumentQtsw2WorkingCount,
            current_deferred_state = current.DeferredState,
            current_broker_working_execution_instrument = current.BrokerWorkingExecutionInstrumentCount,
            current_iea_registry_working = current.IeaRegistryWorkingCount,
            prev_account_orders_total = previous.AccountOrdersTotal,
            prev_candidate_intent_count = previous.CandidateIntentCount,
            prev_same_instrument_qtsw2_working_count = previous.SameInstrumentQtsw2WorkingCount,
            prev_deferred_state = previous.DeferredState,
            prev_broker_working_execution_instrument = previous.BrokerWorkingExecutionInstrumentCount,
            prev_iea_registry_working = previous.IeaRegistryWorkingCount
        });
    }

    private void MaybeLogAdoptionScanSkippedNoProgress(DateTimeOffset utcNow, string adoptionScanEpisodeId, in AdoptionScanRecoveryFingerprint fp)
    {
        if (Log == null) return;
        const string rateKey = "no_progress_skip|IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS";
        lock (_adoptionScanSkipLogLock)
        {
            if (_adoptionScanSkipLogLastUtc.TryGetValue(rateKey, out var last) && (utcNow - last).TotalSeconds < 45)
                return;
            _adoptionScanSkipLogLastUtc[rateKey] = utcNow;
        }
        double? secSince = _lastRecoveryScanCompletedUtc == DateTimeOffset.MinValue
            ? null
            : (utcNow - _lastRecoveryScanCompletedUtc).TotalSeconds;
        LogIeaEngine(utcNow, "IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS", new
        {
            adoption_scan_episode_id = adoptionScanEpisodeId,
            execution_instrument_key = fp.ExecutionInstrumentKey,
            account_orders_total = fp.AccountOrdersTotal,
            candidate_intent_count = fp.CandidateIntentCount,
            same_instrument_qtsw2_working_count = fp.SameInstrumentQtsw2WorkingCount,
            deferred_state = fp.DeferredState,
            broker_working_execution_instrument = fp.BrokerWorkingExecutionInstrumentCount,
            iea_registry_working = fp.IeaRegistryWorkingCount,
            last_scan_completed_utc = _lastRecoveryScanCompletedUtc == DateTimeOffset.MinValue ? null : _lastRecoveryScanCompletedUtc.ToString("o"),
            seconds_since_last_scan = secSince,
            scan_request_source = AdoptionScanRequestSource.RecoveryAdoption.ToString(),
            cooldown_sec = RecoveryAdoptionNoProgressCooldownSeconds,
            iea_instance_id = InstanceId
        });
    }

    /// <summary>Rate-limited unless <paramref name="totalWallMs"/> &gt; 2000 (always log slow scans).</summary>
    private void MaybeLogAdoptionScanPhaseTiming(
        DateTimeOffset utcNow,
        AdoptionScanRequestSource source,
        string adoptionScanEpisodeId,
        long totalWallMs,
        ref AdoptionScanPhaseTelemetry t,
        int adoptedDelta)
    {
        if (Log == null) return;
        const int slowThresholdMs = 2000;
        var rateKey = $"phase_timing|{InstanceId}|IEA_ADOPTION_SCAN_PHASE_TIMING";
        lock (_adoptionScanSkipLogLock)
        {
            if (totalWallMs <= slowThresholdMs &&
                _adoptionScanSkipLogLastUtc.TryGetValue(rateKey, out var last) &&
                (utcNow - last).TotalSeconds < 60)
                return;
            _adoptionScanSkipLogLastUtc[rateKey] = utcNow;
        }

        var scanBodySum = t.CandidatesMs + t.PrecountMs + t.JournalDiagMs + t.PreLoopLogMs + t.MainLoopMs + t.SummaryMs;
        LogIeaEngine(utcNow, "IEA_ADOPTION_SCAN_PHASE_TIMING", new
        {
            adoption_scan_episode_id = adoptionScanEpisodeId,
            scan_request_source = source.ToString(),
            iea_instance_id = InstanceId,
            execution_instrument_key = ExecutionInstrumentKey,
            total_wall_ms = totalWallMs,
            fingerprint_build_ms = t.FingerprintBuildMs,
            phase_candidates_ms = t.CandidatesMs,
            phase_precount_ms = t.PrecountMs,
            phase_journal_diag_ms = t.JournalDiagMs,
            phase_pre_loop_log_ms = t.PreLoopLogMs,
            phase_main_loop_ms = t.MainLoopMs,
            phase_summary_ms = t.SummaryMs,
            scan_body_phases_sum_ms = scanBodySum,
            adopted_delta = adoptedDelta,
            adopted_in_loop = t.ScanAdoptedInLoop,
            account_orders_total = t.AccountOrdersTotal,
            candidate_intent_count = t.CandidateIntentCount,
            qtsw2_same_instrument_working = t.Qtsw2SameInstrumentWorking,
            main_loop_orders_seen = t.MainLoopOrdersSeen
        });
    }

    private int TryGetAccountOrdersTotalForAdoptionProof()
    {
        try
        {
            if (Executor?.GetAccount() is Account a && a.Orders != null)
                return a.Orders.Count;
        }
        catch { }
        return -1;
    }

    private int TryGetSameInstrumentQtsw2WorkingForAdoptionProof()
    {
        try
        {
            if (Executor?.GetAccount() is not Account account || account.Orders == null) return -1;
            var n = 0;
            foreach (Order o in account.Orders)
            {
                if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue;
                var tagProbe = Executor.GetOrderTag(o);
                if (string.IsNullOrEmpty(tagProbe) || !tagProbe.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) continue;
                var broName = o.Instrument?.MasterInstrument?.Name ?? "";
                if (AdoptionScanInstrumentGate.BrokerOrderMatchesExecutionInstrument(ExecutionInstrumentKey, broName))
                    n++;
            }
            return n;
        }
        catch { }
        return -1;
    }

    private int TryGetAdoptionCandidateCountForProof()
    {
        try
        {
            if (_adoptionScanProofCandidateCountOverride.HasValue)
                return _adoptionScanProofCandidateCountOverride.Value;
            return Executor?.GetAdoptionCandidateIntentIds(ExecutionInstrumentKey).Count ?? -1;
        }
        catch { }
        return -1;
    }

    private void ExecuteAdoptionScanFromReservedQueueItem(AdoptionScanRequestSource source, Action? postScanOnWorker)
    {
        var utc0 = NowEvent();
        if (!_adoptionSingleFlightGate.TryBeginQueuedRun())
        {
            LogIeaEngine(utc0, "IEA_ADOPTION_SCAN_GATE_ANOMALY", new
            {
                note = "Worker expected Queued gate state for adoption scan",
                adoption_scan_gate = _adoptionSingleFlightGate.State.ToString(),
                scan_request_source = source.ToString(),
                iea_instance_id = InstanceId,
                execution_instrument_key = ExecutionInstrumentKey
            });
            return;
        }
        var ep = Guid.NewGuid().ToString("N");
        _adoptionScanExecutionEpisodeId = ep;
        try
        {
            RunGatedAdoptionScanBody(source, postScanOnWorker, ep, deferWorkerMutationUtcToAlignUtc: true);
        }
        finally
        {
            _adoptionScanExecutionEpisodeId = null;
        }
    }

    /// <summary>
    /// Single entry for expensive adoption scans. Off-worker callers enqueue; on-worker may run inline when gate is idle.
    /// Preserves recovery throttle (10s) for new <see cref="AdoptionScanRequestSource.RecoveryAdoption"/> requests only (not for duplicate gate hits).
    /// </summary>
    private AdoptionScanRequestDispatchResult RequestAdoptionScan(AdoptionScanRequestSource source, bool applyRecoveryThrottle, Action? postScanOnWorker)
    {
        var utcNow = NowEvent();
        var onWorker = Thread.CurrentThread == _workerThread;
        var depth = QueueDepth;

        if (onWorker)
        {
            if (applyRecoveryThrottle && source == AdoptionScanRequestSource.RecoveryAdoption)
            {
                if (_lastTryRecoveryAdoptionUtc != DateTimeOffset.MinValue &&
                    (utcNow - _lastTryRecoveryAdoptionUtc).TotalSeconds < TryRecoveryAdoptionMinIntervalSeconds)
                {
                    MaybeLogAdoptionScanRequestSkipped(utcNow, source, AdoptionScanRequestLogOutcome.SkippedThrottled);
                    return new AdoptionScanRequestDispatchResult(AdoptionScanRequestLogOutcome.SkippedThrottled, queueDepthAtDecision: depth);
                }
            }

            if (!_adoptionSingleFlightGate.TryBeginInlineRun())
            {
                var st = _adoptionSingleFlightGate.State;
                if (st == AdoptionScanGateState.Running)
                    MaybeLogAdoptionScanRequestSkipped(utcNow, source, AdoptionScanRequestLogOutcome.SkippedAlreadyRunning);
                else if (st == AdoptionScanGateState.Queued)
                    MaybeLogAdoptionScanRequestSkipped(utcNow, source, AdoptionScanRequestLogOutcome.SkippedAlreadyQueued);
                var o = st == AdoptionScanGateState.Running
                    ? AdoptionScanRequestLogOutcome.SkippedAlreadyRunning
                    : AdoptionScanRequestLogOutcome.SkippedAlreadyQueued;
                return new AdoptionScanRequestDispatchResult(o, queueDepthAtDecision: depth);
            }

            if (applyRecoveryThrottle && source == AdoptionScanRequestSource.RecoveryAdoption)
                _lastTryRecoveryAdoptionUtc = utcNow;

            MaybeLogExpensivePathThread(utcNow, "RequestAdoptionScan_inline");
            if (source == AdoptionScanRequestSource.RecoveryAdoption)
                EnsureAdoptionEpisode("try_recovery_adoption");

            var ep = Guid.NewGuid().ToString("N");
            _adoptionScanExecutionEpisodeId = ep;
            LogAdoptionScanRequestAccepted(utcNow, source, "accepted_inline", depth, true);
            var beforeInline = GetOwnedPlusAdoptedWorkingCount();
            try
            {
                RunGatedAdoptionScanBody(source, postScanOnWorker, ep, deferWorkerMutationUtcToAlignUtc: false);
            }
            finally
            {
                _adoptionScanExecutionEpisodeId = null;
            }
            var afterInline = GetOwnedPlusAdoptedWorkingCount();
            return new AdoptionScanRequestDispatchResult(AdoptionScanRequestLogOutcome.AcceptedInline, Math.Max(0, afterInline - beforeInline), depth);
        }

        if (applyRecoveryThrottle && source == AdoptionScanRequestSource.RecoveryAdoption)
        {
            if (_lastTryRecoveryAdoptionUtc != DateTimeOffset.MinValue &&
                (utcNow - _lastTryRecoveryAdoptionUtc).TotalSeconds < TryRecoveryAdoptionMinIntervalSeconds)
            {
                MaybeLogAdoptionScanRequestSkipped(utcNow, source, AdoptionScanRequestLogOutcome.SkippedThrottled);
                return new AdoptionScanRequestDispatchResult(AdoptionScanRequestLogOutcome.SkippedThrottled, queueDepthAtDecision: depth);
            }
        }

        var res = _adoptionSingleFlightGate.TryReserveQueuedSlot();
        if (res == AdoptionScanEnqueueAttemptOutcome.AlreadyRunning)
        {
            MaybeLogAdoptionScanRequestSkipped(utcNow, source, AdoptionScanRequestLogOutcome.SkippedAlreadyRunning);
            return new AdoptionScanRequestDispatchResult(AdoptionScanRequestLogOutcome.SkippedAlreadyRunning, queueDepthAtDecision: depth);
        }
        if (res == AdoptionScanEnqueueAttemptOutcome.AlreadyQueued)
        {
            MaybeLogAdoptionScanRequestSkipped(utcNow, source, AdoptionScanRequestLogOutcome.SkippedAlreadyQueued);
            return new AdoptionScanRequestDispatchResult(AdoptionScanRequestLogOutcome.SkippedAlreadyQueued, queueDepthAtDecision: depth);
        }

        if (applyRecoveryThrottle && source == AdoptionScanRequestSource.RecoveryAdoption)
            _lastTryRecoveryAdoptionUtc = utcNow;

        if (source == AdoptionScanRequestSource.RecoveryAdoption)
        {
            MaybeLogExpensivePathThread(utcNow, "TryRecoveryAdoption_enqueue");
            EnsureAdoptionEpisode("try_recovery_adoption");
        }

        depth = QueueDepth;
        var workKind = source switch
        {
            AdoptionScanRequestSource.Bootstrap => "BootstrapAdoptionScan",
            AdoptionScanRequestSource.DeferredRetry => "AdoptionDeferredRetry",
            AdoptionScanRequestSource.RecoveryAdoption => "RecoveryAdoptionScan",
            AdoptionScanRequestSource.FirstExecutionUpdate => "FirstExecutionAdoptionScan",
            _ => "AdoptionScan"
        };
        try
        {
            EnqueueRecoveryEssential(() => ExecuteAdoptionScanFromReservedQueueItem(source, postScanOnWorker), workKind);
            LogAdoptionScanRequestAccepted(utcNow, source, "accepted_and_queued", depth, false);
            return new AdoptionScanRequestDispatchResult(AdoptionScanRequestLogOutcome.AcceptedAndQueued, queueDepthAtDecision: depth);
        }
        catch
        {
            _adoptionSingleFlightGate.AbortQueuedReservation();
            throw;
        }
    }

    /// <summary>
    /// Reconciliation / gate recovery: schedules full adoption scan on IEA worker (was synchronous off-worker — CPU amplification).
    /// Throttle: at most one new recovery scheduling attempt per 10s per IEA (unchanged semantics for accepted requests).
    /// </summary>
    private DateTimeOffset _lastTryRecoveryAdoptionUtc = DateTimeOffset.MinValue;
    private const double TryRecoveryAdoptionMinIntervalSeconds = 10.0;

    /// <summary>True when adoption retry is pending (heartbeat / execution paths should consider enqueueing a scan).</summary>
    internal bool HasDeferredAdoptionScanPending => _adoptionDeferred;

    /// <summary>Wall-clock seconds to defer UNOWNED when adoption candidates empty but broker has orders (journal load race / path resolution).</summary>
    private const int RestartAdoptionGraceSeconds = 60;

    /// <summary>
    /// Retry adoption scan when deferred (candidates empty, broker has orders).
    /// Enqueues to IEA worker; single-flight gate collapses duplicates with recovery/bootstrap/first scan.
    /// </summary>
    /// <returns>True if a scan was newly enqueued; false if not deferred, throttled by gate duplicate, or enqueue failed.</returns>
    internal bool TryRetryDeferredAdoptionScanIfDeferred()
    {
        if (!_adoptionDeferred) return false;
        try
        {
            var r = RequestAdoptionScan(AdoptionScanRequestSource.DeferredRetry, applyRecoveryThrottle: false, postScanOnWorker: null);
            return r.Outcome == AdoptionScanRequestLogOutcome.AcceptedAndQueued;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Schedules recovery adoption on the IEA worker. Returns true when a scan was queued, ran inline, or is already queued/running (caller may skip immediate mismatch classification).
    /// Returns false when recovery throttle rejects a new request. <paramref name="adoptedCountIfRanSynchronously"/> is non-zero only if the scan ran inline on the IEA worker (unusual for engine callers).
    /// </summary>
    public bool TryScheduleRecoveryAdoptionScan(out int adoptedCountIfRanSynchronously)
    {
        adoptedCountIfRanSynchronously = 0;
        var r = RequestAdoptionScan(AdoptionScanRequestSource.RecoveryAdoption, applyRecoveryThrottle: true, postScanOnWorker: null);
        adoptedCountIfRanSynchronously = r.AdoptedDeltaIfInline;
        var ok = r.Outcome == AdoptionScanRequestLogOutcome.AcceptedAndQueued
                 || r.Outcome == AdoptionScanRequestLogOutcome.AcceptedInline
                 || r.Outcome == AdoptionScanRequestLogOutcome.SkippedAlreadyRunning
                 || r.Outcome == AdoptionScanRequestLogOutcome.SkippedAlreadyQueued;
        Log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "ADOPTION_RECOVERY_SCHEDULE_PROBE", state: "ENGINE",
            new
            {
                execution_instrument_key = ExecutionInstrumentKey,
                iea_instance_id = InstanceId,
                outcome = r.Outcome.ToString(),
                adopted_delta_if_inline = r.AdoptedDeltaIfInline,
                queue_depth_at_decision = r.QueueDepthAtDecision,
                schedule_accepted_or_in_flight = ok,
                note = "Instrumentation only — recovery adoption request decision after gate reconciliation trigger"
            }));
        return ok;
    }

    /// <summary>Run adoption for late recovery (reconciliation). Prefer <see cref="TryScheduleRecoveryAdoptionScan"/> for engine paths; this returns adopted count only when the scan ran synchronously on the worker.</summary>
    public int TryRecoveryAdoption()
    {
        TryScheduleRecoveryAdoptionScan(out var adopted);
        return adopted;
    }

    /// <summary>Phase 4: Run adoption for bootstrap ADOPT path on IEA worker; then <see cref="OnBootstrapAdoptionCompleted"/>.</summary>
    internal void RunBootstrapAdoption(string instrument, DateTimeOffset utcNow)
    {
        LogIeaEngine(utcNow, "BOOTSTRAP_ADOPTION_ATTEMPT", new
        {
            execution_instrument_key = ExecutionInstrumentKey,
            instrument,
            iea_instance_id = InstanceId,
            note = "Running adoption for bootstrap ADOPT path (queued to IEA worker when not on worker)"
        });
        var inst = instrument;
        void Post() => OnBootstrapAdoptionCompleted(inst, NowEvent());
        _ = RequestAdoptionScan(AdoptionScanRequestSource.Bootstrap, applyRecoveryThrottle: false, Post);
    }

    /// <summary>
    /// Gap 4: On first execution update, sync OrderMap with broker reality. Hydration does not mutate broker state.
    /// For each QTSW2 protective (STOP/TARGET) with entry filled per journal, populate OrderMap.
    /// Same-instrument QTSW2 stop/target with no journal adoption candidate → STALE_QTSW2_ORDER_DETECTED (UNOWNED registry), recovery/supervisory once per fingerprint; convergence quarantine prevents hot loops.
    /// Foreign-instrument QTSW2 orders → skipped before adoption/tag work (FOREIGN_INSTRUMENT_QTSW2_ORDER_SKIPPED sample).
    /// IEA robustness: Adopt entry orders (QTSW2:{intentId} without :STOP/:TARGET) when candidate exists.
    /// Deferral: when candidates empty but this instrument still has QTSW2 working orders, grace window before stale classification (journal load race).
    /// </summary>
    private void ScanAndAdoptExistingOrders(AdoptionScanRequestSource source, ref AdoptionScanPhaseTelemetry tel)
    {
        if (Thread.CurrentThread != _workerThread)
        {
            LogIeaEngine(NowEvent(), "IEA_ADOPTION_SCAN_OFF_WORKER_VIOLATION", new
            {
                note = "Heavy adoption scan must run on IEA worker only — use RequestAdoptionScan",
                iea_instance_id = InstanceId,
                execution_instrument_key = ExecutionInstrumentKey
            });
            return;
        }
        var savedWorkType = _currentWorkType;
        _currentWorkType = "AdoptionScan";
        try
        {
            var scanCpu = RuntimeAuditHubRef.Active != null ? RuntimeAuditHub.CpuStart() : 0L;
            try
            {
                ScanAndAdoptExistingOrdersCore(source, ref tel);
            }
            finally
            {
                if (scanCpu != 0)
                    RuntimeAuditHubRef.Active?.CpuEnd(scanCpu, RuntimeAuditSubsystem.IeaScan, ExecutionInstrumentKey, stream: "", onIeaWorker: true);
            }
        }
        finally
        {
            _currentWorkType = savedWorkType;
        }
    }

    private void ScanAndAdoptExistingOrdersCore(AdoptionScanRequestSource source, ref AdoptionScanPhaseTelemetry tel)
    {
        if (Executor == null || Log == null) return;
        var account = Executor.GetAccount() as Account;
        if (account?.Orders == null) return;
        var accountOrdersTotal = account.Orders.Count;
        tel.AccountOrdersTotal = accountOrdersTotal;
        MaybeLogExpensivePathThread(NowEvent(), "ScanAndAdoptExistingOrders");

        var swPh = Stopwatch.StartNew();
        IReadOnlyCollection<string> activeIntentIds;
        if (source == AdoptionScanRequestSource.RecoveryAdoption && _preResolvedAdoptionCandidatesForScan != null)
        {
            activeIntentIds = _preResolvedAdoptionCandidatesForScan;
        }
        else
        {
            activeIntentIds = Executor.GetAdoptionCandidateIntentIds(ExecutionInstrumentKey);
            swPh.Stop();
            tel.CandidatesMs = swPh.ElapsedMilliseconds;
        }

        swPh.Restart();
        var workingCount = account.Orders.Count(o => o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted);
        var qtsw2SameInstrumentWorking = 0;
        foreach (Order o in account.Orders)
        {
            if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue;
            var tagProbe = Executor.GetOrderTag(o);
            if (string.IsNullOrEmpty(tagProbe) || !tagProbe.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) continue;
            var broName = o.Instrument?.MasterInstrument?.Name ?? "";
            if (AdoptionScanInstrumentGate.BrokerOrderMatchesExecutionInstrument(ExecutionInstrumentKey, broName))
                qtsw2SameInstrumentWorking++;
        }
        swPh.Stop();
        tel.PrecountMs = swPh.ElapsedMilliseconds;

        swPh.Restart();
        var utcNow = NowEvent();
        var (journalDir, journalFileCount, journalDirExists) = Executor.GetJournalDiagnostics(ExecutionInstrumentKey);
        swPh.Stop();
        tel.JournalDiagMs = swPh.ElapsedMilliseconds;

        var scanStartFingerprint = $"scan_start|{accountOrdersTotal}|{qtsw2SameInstrumentWorking}|{activeIntentIds.Count}|{_adoptionDeferred}";

        swPh.Restart();
        TrackAdoptionSameStateRetry(utcNow, scanStartFingerprint);
        LogIeaEngine(utcNow, "ADOPTION_SCAN_START", new
        {
            adoption_episode_id = _adoptionEpisodeId,
            account_orders_total = accountOrdersTotal,
            same_instrument_qtsw2_working_count = qtsw2SameInstrumentWorking,
            candidate_intent_count = activeIntentIds.Count,
            deferred = _adoptionDeferred,
            adoption_scan_gate = _adoptionSingleFlightGate.State.ToString(),
            execution_instrument_key = ExecutionInstrumentKey,
            adoption_candidate_count = activeIntentIds.Count,
            broker_working_count = workingCount,
            qtsw2_working_same_instrument_count = qtsw2SameInstrumentWorking,
            journal_dir = journalDir,
            journal_file_count = journalFileCount,
            journal_dir_exists = journalDirExists,
            iea_instance_id = InstanceId,
            scanned_orders_total = Interlocked.Read(ref _metricAdoptionScannedOrdersTotal),
            skipped_foreign_instrument_orders_total = Interlocked.Read(ref _metricAdoptionSkippedForeignInstrumentOrdersTotal),
            stale_qtsw2_orders_total = Interlocked.Read(ref _metricAdoptionStaleQtsw2OrdersTotal),
            successful_adoptions_total = Interlocked.Read(ref _metricAdoptionSuccessfulAdoptionsTotal),
            non_convergent_orders_total = Interlocked.Read(ref _metricAdoptionNonConvergentEscalationsTotal),
            suppressed_rechecks_total = Interlocked.Read(ref _metricAdoptionSuppressedRechecksTotal)
        });
        swPh.Stop();
        tel.PreLoopLogMs = swPh.ElapsedMilliseconds;
        tel.CandidateIntentCount = activeIntentIds.Count;
        tel.Qtsw2SameInstrumentWorking = qtsw2SameInstrumentWorking;

        if (activeIntentIds.Count == 0 && qtsw2SameInstrumentWorking > 0)
        {
            _adoptionDeferredCount++;
            _firstAdoptionScanUtc ??= utcNow;
            var elapsedMs = (long)(utcNow - _firstAdoptionScanUtc.Value).TotalMilliseconds;
            var action = AdoptionDeferralDecision.Evaluate(activeIntentIds.Count, qtsw2SameInstrumentWorking, elapsedMs, RestartAdoptionGraceSeconds);
            if (action == AdoptionDeferralAction.Defer)
            {
                _adoptionDeferred = true;
                _hasScannedForAdoption = false;
                EnsureAdoptionEpisode("deferred_candidates_empty");
                LogIeaEngine(utcNow, "ADOPTION_DEFERRED_CANDIDATES_EMPTY", new
                {
                    adoption_episode_id = _adoptionEpisodeId,
                    account_orders_total = accountOrdersTotal,
                    same_instrument_qtsw2_working_count = qtsw2SameInstrumentWorking,
                    candidate_intent_count = activeIntentIds.Count,
                    deferred = _adoptionDeferred,
                    adoption_scan_gate = _adoptionSingleFlightGate.State.ToString(),
                    execution_instrument_key = ExecutionInstrumentKey,
                    broker_working_same_instrument_qtsw2 = qtsw2SameInstrumentWorking,
                    journal_dir = journalDir,
                    retry_count = _adoptionDeferredCount,
                    elapsed_since_first_scan_ms = elapsedMs,
                    journal_file_count = journalFileCount,
                    journal_dir_exists = journalDirExists,
                    iea_instance_id = InstanceId,
                    note = "Candidates empty but this instrument has QTSW2 working orders; defer UNOWNED to allow journal load"
                });
                return;
            }
            _adoptionDeferred = false;
            EndAdoptionEpisode("grace_expired");
            EnsureAdoptionEpisode("post_grace_scan");
            LogIeaEngine(utcNow, "ADOPTION_GRACE_EXPIRED_UNOWNED", new
            {
                adoption_episode_id = _adoptionEpisodeId,
                account_orders_total = accountOrdersTotal,
                same_instrument_qtsw2_working_count = qtsw2SameInstrumentWorking,
                candidate_intent_count = activeIntentIds.Count,
                deferred = _adoptionDeferred,
                adoption_scan_gate = _adoptionSingleFlightGate.State.ToString(),
                execution_instrument_key = ExecutionInstrumentKey,
                broker_working_same_instrument_qtsw2 = qtsw2SameInstrumentWorking,
                journal_dir = journalDir,
                retry_count = _adoptionDeferredCount,
                elapsed_since_first_scan_ms = elapsedMs,
                journal_file_count = journalFileCount,
                journal_dir_exists = journalDirExists,
                iea_instance_id = InstanceId,
                note = "Grace window expired; classifying same-instrument QTSW2 orders (stale vs adoptable)"
            });
        }
        else if (activeIntentIds.Count == 0 && qtsw2SameInstrumentWorking == 0)
        {
            _hasScannedForAdoption = true;
            _adoptionDeferred = false;
            var episodeNoBroker = _adoptionEpisodeId;
            EndAdoptionEpisode("no_broker_orders");
            LogIeaEngine(utcNow, "ADOPTION_CANDIDATES_EMPTY_NO_BROKER_ORDERS", new
            {
                adoption_episode_id = episodeNoBroker,
                account_orders_total = accountOrdersTotal,
                same_instrument_qtsw2_working_count = qtsw2SameInstrumentWorking,
                candidate_intent_count = activeIntentIds.Count,
                deferred = _adoptionDeferred,
                adoption_scan_gate = _adoptionSingleFlightGate.State.ToString(),
                execution_instrument_key = ExecutionInstrumentKey,
                journal_dir = journalDir,
                journal_file_count = journalFileCount,
                iea_instance_id = InstanceId,
                note = "Nothing to adopt on this instrument"
            });
            return;
        }

        var swScan = Stopwatch.StartNew();
        var scanSeen = 0L;
        var scanForeign = 0L;
        var scanStale = 0L;
        var scanAdopted = 0L;
        var scanEscalations = 0L;
        var scanSuppressed = 0L;
        var recoveryRequestsDuringScan = 0;
        var supervisoryRequestsDuringScan = 0;

        foreach (Order o in account.Orders)
        {
            if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue;
            var tag = Executor.GetOrderTag(o);
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) continue;

            var brokerInstrument = o.Instrument?.MasterInstrument?.Name ?? "";
            if (!AdoptionScanInstrumentGate.BrokerOrderMatchesExecutionInstrument(ExecutionInstrumentKey, brokerInstrument))
            {
                Interlocked.Increment(ref _metricAdoptionSkippedForeignInstrumentOrdersTotal);
                scanForeign++;
                var n = Interlocked.Increment(ref _foreignInstrumentSkipLogCounter);
                if (n % 50 == 1)
                {
                    LogIeaEngine(utcNow, "FOREIGN_INSTRUMENT_QTSW2_ORDER_SKIPPED", new
                    {
                        execution_instrument_key = ExecutionInstrumentKey,
                        broker_order_instrument = brokerInstrument,
                        iea_instance_id = InstanceId,
                        note = "Low-rate sample: QTSW2-tagged order on another instrument — ignored by this IEA before adoption/tag decode cost",
                        sample_sequence = n
                    });
                }
                continue;
            }

            scanSeen++;
            Interlocked.Increment(ref _metricAdoptionScannedOrdersTotal);
            var orderId = o.OrderId?.ToString() ?? "";
            if (_adoptionConvergence.IsQuarantined(orderId, utcNow))
            {
                Interlocked.Increment(ref _metricAdoptionSuppressedRechecksTotal);
                scanSuppressed++;
                continue;
            }

            var intentId = RobotOrderIds.DecodeIntentId(tag);
            var isStop = tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase);
            var isTarget = tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase);
            var hasCandidate = !string.IsNullOrEmpty(intentId) && activeIntentIds.Contains(intentId);
            var inRegistry = TryResolveByBrokerOrderId(orderId, out _);
            var orderStateStr = o.OrderState.ToString();
            var roleChar = isStop ? "S" : isTarget ? "T" : "E";
            var fingerprint = $"{orderStateStr}|{inRegistry}|{hasCandidate}|{roleChar}|{intentId ?? ""}";

            _adoptionConvergence.RegisterEvaluation(orderId, utcNow, fingerprint, AdoptionConvergenceUnchangedThreshold, AdoptionConvergenceCooldownSeconds,
                out var escalateNonConvergence, out var suppressHeavy, out var unchangedStreak);
            if (escalateNonConvergence)
            {
                Interlocked.Increment(ref _metricAdoptionNonConvergentEscalationsTotal);
                scanEscalations++;
                _adoptionConvergence.TryGetTimestamps(orderId, out var fs, out var ls);
                var episodeNonConv = _adoptionEpisodeId;
                EndAdoptionEpisode("non_convergence_escalated");
                LogIeaEngine(utcNow, "ADOPTION_NON_CONVERGENCE_ESCALATED", new
                {
                    adoption_episode_id = episodeNonConv,
                    account_orders_total = accountOrdersTotal,
                    same_instrument_qtsw2_working_count = qtsw2SameInstrumentWorking,
                    candidate_intent_count = activeIntentIds.Count,
                    deferred = _adoptionDeferred,
                    adoption_scan_gate = _adoptionSingleFlightGate.State.ToString(),
                    broker_order_id = orderId,
                    instrument = brokerInstrument,
                    intent_id = intentId,
                    attempt_count = unchangedStreak,
                    attempt_threshold = AdoptionConvergenceUnchangedThreshold,
                    cooldown_seconds = AdoptionConvergenceCooldownSeconds,
                    first_seen_utc = fs.ToString("o"),
                    last_seen_utc = ls.ToString("o"),
                    iea_instance_id = InstanceId,
                    classification = fingerprint,
                    execution_instrument_key = ExecutionInstrumentKey
                });
            }
            if (suppressHeavy)
                continue;

            if (isStop || isTarget)
            {
                if (!string.IsNullOrEmpty(intentId) && hasCandidate)
                {
                    var mapKey = $"{intentId}:{(isStop ? "STOP" : "TARGET")}";
                    if (!OrderMap.TryGetValue(mapKey, out _))
                    {
                        LogIeaEngine(utcNow, "ADOPTION_CANDIDATE_FOUND", new
                        {
                            broker_order_id = orderId,
                            intent_id = intentId,
                            execution_instrument_key = ExecutionInstrumentKey,
                            iea_instance_id = InstanceId,
                            order_role = isStop ? "STOP" : "TARGET"
                        });
                        var oi = new OrderInfo
                        {
                            IntentId = intentId,
                            Instrument = brokerInstrument,
                            OrderId = o.OrderId,
                            OrderType = isStop ? "STOP" : "TARGET",
                            Price = isStop ? (decimal?)o.StopPrice : (decimal?)o.LimitPrice,
                            Quantity = o.Quantity,
                            FilledQuantity = o.Filled,
                            State = "WORKING",
                            IsEntryOrder = false,
                            BrokerLastEventUtc = TryGetBrokerOrderTimeUtc(o)
                        };
                        OrderMap[mapKey] = oi;
                        RegisterAdoptedOrder(o.OrderId, intentId, oi.Instrument, isStop ? OrderRole.STOP : OrderRole.TARGET, "RESTART_ADOPTION", oi, NowEvent());
                        _adoptionConvergence.ClearOrder(orderId);
                        if (isStop)
                            Executor?.SetProtectionStateWorkingForAdoptedStop(intentId);
                        Interlocked.Increment(ref _metricAdoptionSuccessfulAdoptionsTotal);
                        scanAdopted++;
                        Log?.Write(RobotEvents.ExecutionBase(NowEvent(), intentId, oi.Instrument, "ADOPTION_SUCCESS", new
                        {
                            broker_order_id = o.OrderId,
                            intent_id = intentId,
                            order_class = isStop ? "STOP" : "TARGET",
                            source = "RESTART_ADOPTION",
                            iea_instance_id = InstanceId
                        }));
                    }
                }
                else
                {
                    if (TryResolveByBrokerOrderId(orderId, out _))
                        continue;

                    var corrupt = string.IsNullOrWhiteSpace(intentId);
                    Interlocked.Increment(ref _metricAdoptionStaleQtsw2OrdersTotal);
                    scanStale++;
                    if (unchangedStreak == 1)
                    {
                        LogIeaEngine(utcNow, "ADOPTION_CANDIDATE_NOT_FOUND", new
                        {
                            broker_order_id = orderId,
                            intent_id = intentId,
                            execution_instrument_key = ExecutionInstrumentKey,
                            iea_instance_id = InstanceId,
                            order_role = isStop ? "STOP" : "TARGET"
                        });
                        LogIeaEngine(utcNow, "STALE_QTSW2_ORDER_DETECTED", new
                        {
                            broker_order_id = orderId,
                            intent_id = intentId,
                            instrument = brokerInstrument,
                            execution_instrument_key = ExecutionInstrumentKey,
                            order_type = isStop ? "STOP" : "TARGET",
                            reason = corrupt ? "MALFORMED_OR_EMPTY_INTENT_TAG" : "NO_JOURNAL_ADOPTION_CANDIDATE_FOR_THIS_INSTRUMENT",
                            iea_instance_id = InstanceId,
                            policy = "NO_AUTO_ADOPT_WITHOUT_CANDIDATE"
                        });
                    }

                    if (!corrupt)
                    {
                        RegisterUnownedOrder(orderId, intentId, brokerInstrument, "STALE_QTSW2_ORDER_DETECTED", NowEvent(),
                            classAsRecoverableRobotOwned: true);
                        if (unchangedStreak == 1)
                        {
                            supervisoryRequestsDuringScan++;
                            RequestSupervisoryAction(brokerInstrument, SupervisoryTriggerReason.REPEATED_UNOWNED_EXECUTIONS, SupervisorySeverity.HIGH,
                                new { broker_order_id = orderId, intent_id = intentId, classification = "STALE_QTSW2_ORDER_DETECTED" }, utcNow);
                            recoveryRequestsDuringScan++;
                            RequestRecovery(brokerInstrument, "STALE_QTSW2_ORDER_DETECTED",
                                new { broker_order_id = orderId, intent_id = intentId, note = "Same-instrument QTSW2 protective without adoption candidate — fail-closed recovery (convergence-guarded)" }, utcNow);
                        }
                    }
                    else
                    {
                        RegisterUnownedOrder(orderId, intentId, brokerInstrument, "STALE_QTSW2_ORDER_DETECTED", NowEvent(),
                            classAsRecoverableRobotOwned: true);
                        if (unchangedStreak == 1)
                        {
                            supervisoryRequestsDuringScan++;
                            RequestSupervisoryAction(brokerInstrument, SupervisoryTriggerReason.REPEATED_UNOWNED_EXECUTIONS, SupervisorySeverity.CRITICAL,
                                new { broker_order_id = orderId, reason = "CORRUPT_QTSW2_TAG" }, utcNow);
                            recoveryRequestsDuringScan++;
                            RequestRecovery(brokerInstrument, "STALE_QTSW2_ORDER_DETECTED",
                                new { broker_order_id = orderId, intent_id = intentId, note = "Malformed QTSW2 tag — fail-closed recovery" }, utcNow);
                        }
                    }
                }
            }
            else
            {
                if (TryResolveByBrokerOrderId(o.OrderId, out _)) continue;
                if (!string.IsNullOrEmpty(intentId) && hasCandidate)
                {
                    LogIeaEngine(utcNow, "ADOPTION_CANDIDATE_FOUND", new
                    {
                        broker_order_id = orderId,
                        intent_id = intentId,
                        execution_instrument_key = ExecutionInstrumentKey,
                        iea_instance_id = InstanceId,
                        order_role = "ENTRY"
                    });
                    var oi = new OrderInfo
                    {
                        IntentId = intentId,
                        Instrument = brokerInstrument,
                        OrderId = o.OrderId,
                        OrderType = "ENTRY",
                        Price = (decimal?)o.StopPrice ?? (decimal?)o.LimitPrice,
                        Quantity = o.Quantity,
                        State = "WORKING",
                        IsEntryOrder = true,
                        FilledQuantity = o.Filled,
                        BrokerLastEventUtc = TryGetBrokerOrderTimeUtc(o)
                    };
                    OrderMap[intentId] = oi;
                    RegisterAdoptedOrder(o.OrderId, intentId, brokerInstrument, OrderRole.ENTRY, "RESTART_ADOPTION_ENTRY", oi, NowEvent());
                    _adoptionConvergence.ClearOrder(orderId);
                    Interlocked.Increment(ref _metricAdoptionSuccessfulAdoptionsTotal);
                    scanAdopted++;
                    Log?.Write(RobotEvents.ExecutionBase(NowEvent(), intentId, brokerInstrument, "ADOPTION_SUCCESS", new
                    {
                        broker_order_id = o.OrderId,
                        intent_id = intentId,
                        order_class = "ENTRY_STOP",
                        source = "RESTART_ADOPTION_ENTRY",
                        iea_instance_id = InstanceId
                    }));
                }
                else
                {
                    if (TryResolveByBrokerOrderId(orderId, out _))
                        continue;
                    var corrupt = string.IsNullOrWhiteSpace(intentId);
                    Interlocked.Increment(ref _metricAdoptionStaleQtsw2OrdersTotal);
                    scanStale++;
                    if (unchangedStreak == 1)
                    {
                        LogIeaEngine(utcNow, "ADOPTION_CANDIDATE_NOT_FOUND", new
                        {
                            broker_order_id = orderId,
                            intent_id = intentId,
                            execution_instrument_key = ExecutionInstrumentKey,
                            iea_instance_id = InstanceId,
                            order_role = "ENTRY"
                        });
                        LogIeaEngine(utcNow, "STALE_QTSW2_ORDER_DETECTED", new
                        {
                            broker_order_id = orderId,
                            intent_id = intentId,
                            instrument = brokerInstrument,
                            execution_instrument_key = ExecutionInstrumentKey,
                            order_type = "ENTRY",
                            reason = corrupt ? "MALFORMED_OR_EMPTY_INTENT_TAG" : "NO_JOURNAL_ADOPTION_CANDIDATE_FOR_THIS_INSTRUMENT",
                            iea_instance_id = InstanceId
                        });
                    }
                    RegisterUnownedOrder(orderId, intentId, brokerInstrument, "STALE_QTSW2_ORDER_DETECTED", NowEvent(),
                        classAsRecoverableRobotOwned: true);
                    if (unchangedStreak == 1)
                    {
                        supervisoryRequestsDuringScan++;
                        RequestSupervisoryAction(brokerInstrument, SupervisoryTriggerReason.REPEATED_UNOWNED_EXECUTIONS,
                            corrupt ? SupervisorySeverity.CRITICAL : SupervisorySeverity.HIGH,
                            new { broker_order_id = orderId, intent_id = intentId }, utcNow);
                        recoveryRequestsDuringScan++;
                        RequestRecovery(brokerInstrument, "STALE_QTSW2_ORDER_DETECTED",
                            new { broker_order_id = orderId, intent_id = intentId, note = "QTSW2 entry without adoption candidate" }, utcNow);
                    }
                }
            }
        }

        swScan.Stop();
        tel.MainLoopMs = swScan.ElapsedMilliseconds;
        tel.MainLoopOrdersSeen = scanSeen > int.MaxValue ? int.MaxValue : (int)scanSeen;
        tel.ScanAdoptedInLoop = scanAdopted > int.MaxValue ? int.MaxValue : (int)scanAdopted;

        var episodeSummary = _adoptionEpisodeId;
        var swSummary = Stopwatch.StartNew();
        if (scanAdopted > 0)
            EndAdoptionEpisode("adoption_success");
        else
            EndAdoptionEpisode("scan_summary_complete");

        LogIeaEngine(utcNow, "ADOPTION_SCAN_SUMMARY", new
        {
            adoption_episode_id = episodeSummary,
            account_orders_total = accountOrdersTotal,
            same_instrument_qtsw2_working_count = qtsw2SameInstrumentWorking,
            candidate_intent_count = activeIntentIds.Count,
            deferred = _adoptionDeferred,
            adoption_scan_gate = _adoptionSingleFlightGate.State.ToString(),
            wall_ms = swScan.ElapsedMilliseconds,
            orders_scanned_loop = scanSeen,
            recovery_requests_emitted = recoveryRequestsDuringScan,
            supervisory_requests_emitted = supervisoryRequestsDuringScan,
            execution_instrument_key = ExecutionInstrumentKey,
            iea_instance_id = InstanceId,
            scan_qtsw2_same_instrument_seen = scanSeen,
            scan_skipped_foreign = scanForeign,
            scan_stale_classified = scanStale,
            scan_adopted = scanAdopted,
            scan_non_convergence_escalations = scanEscalations,
            scan_suppressed_rechecks = scanSuppressed,
            scanned_orders_total = Interlocked.Read(ref _metricAdoptionScannedOrdersTotal),
            skipped_foreign_instrument_orders_total = Interlocked.Read(ref _metricAdoptionSkippedForeignInstrumentOrdersTotal),
            stale_qtsw2_orders_total = Interlocked.Read(ref _metricAdoptionStaleQtsw2OrdersTotal),
            successful_adoptions_total = Interlocked.Read(ref _metricAdoptionSuccessfulAdoptionsTotal),
            non_convergent_orders_total = Interlocked.Read(ref _metricAdoptionNonConvergentEscalationsTotal),
            suppressed_rechecks_total = Interlocked.Read(ref _metricAdoptionSuppressedRechecksTotal),
            scan_wall_ms = EngineCpuProfile.IsEnabled() ? swScan.ElapsedMilliseconds : -1L
        });
        swSummary.Stop();
        tel.SummaryMs = swSummary.ElapsedMilliseconds;

        _hasScannedForAdoption = true;
        _adoptionDeferred = false;
    }

    /// <summary>
    /// Adopt a broker order into registry when registry is missing it (broker_has_registry_missing).
    /// Restores IEA consistency so we never lose orders. Returns true if adopted.
    /// </summary>
    internal bool TryAdoptBrokerOrderIfNotInRegistry(Order o)
    {
        if (Executor == null || o == null) return false;
        var id = o.OrderId?.ToString();
        if (string.IsNullOrEmpty(id) || TryResolveByBrokerOrderId(id, out _)) return false;

        var brokerInstrumentGate = o.Instrument?.MasterInstrument?.Name ?? "";
        if (!AdoptionScanInstrumentGate.BrokerOrderMatchesExecutionInstrument(ExecutionInstrumentKey, brokerInstrumentGate))
            return false;

        var tag = Executor.GetOrderTag(o);
        if (string.IsNullOrEmpty(tag) || !tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase))
        {
            RegisterUnownedOrder(id, null, o.Instrument?.MasterInstrument?.Name ?? ExecutionInstrumentKey, "BROKER_REGISTRY_MISSING_NON_QTSW2", NowEvent());
            return false; // Not adopted (unowned)
        }

        var intentId = RobotOrderIds.DecodeIntentId(tag);
        var isStop = tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase);
        var isTarget = tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase);
        var instrument = o.Instrument?.MasterInstrument?.Name ?? ExecutionInstrumentKey;
        var activeIntentIds = Executor.GetAdoptionCandidateIntentIds(ExecutionInstrumentKey);

        if (isStop || isTarget)
        {
            if (!string.IsNullOrEmpty(intentId) && activeIntentIds.Contains(intentId))
            {
                var mapKey = $"{intentId}:{(isStop ? "STOP" : "TARGET")}";
                if (!OrderMap.TryGetValue(mapKey, out _))
                {
                    var oi = new OrderInfo
                    {
                        IntentId = intentId,
                        Instrument = instrument,
                        OrderId = o.OrderId,
                        OrderType = isStop ? "STOP" : "TARGET",
                        Price = isStop ? (decimal?)o.StopPrice : (decimal?)o.LimitPrice,
                        Quantity = o.Quantity,
                        FilledQuantity = o.Filled,
                        State = "WORKING",
                        IsEntryOrder = false,
                        BrokerLastEventUtc = TryGetBrokerOrderTimeUtc(o)
                    };
                    OrderMap[mapKey] = oi;
                    RegisterAdoptedOrder(o.OrderId, intentId, instrument, isStop ? OrderRole.STOP : OrderRole.TARGET, "BROKER_REGISTRY_MISSING_ADOPT", oi, NowEvent());
                    _adoptionConvergence.ClearOrder(id);
                    if (isStop)
                        Executor?.SetProtectionStateWorkingForAdoptedStop(intentId);
                    Log?.Write(RobotEvents.ExecutionBase(NowEvent(), intentId, instrument, "ADOPTION_SUCCESS", new
                    {
                        broker_order_id = o.OrderId,
                        intent_id = intentId,
                        order_class = isStop ? "STOP" : "TARGET",
                        source = "REGISTRY_BROKER_DIVERGENCE",
                        iea_instance_id = InstanceId
                    }));
                    return true;
                }
            }
            else
            {
                RegisterUnownedOrder(o.OrderId, intentId, instrument, "BROKER_REGISTRY_MISSING_UNOWNED", NowEvent(), isEntryOrder: false,
                    classAsRecoverableRobotOwned: true);
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(intentId) && activeIntentIds.Contains(intentId))
            {
                var oi = new OrderInfo
                {
                    IntentId = intentId,
                    Instrument = instrument,
                    OrderId = o.OrderId,
                    OrderType = "ENTRY",
                    Price = (decimal?)o.StopPrice ?? (decimal?)o.LimitPrice,
                    Quantity = o.Quantity,
                    State = "WORKING",
                    IsEntryOrder = true,
                    FilledQuantity = o.Filled,
                    BrokerLastEventUtc = TryGetBrokerOrderTimeUtc(o)
                };
                OrderMap[intentId] = oi;
                RegisterAdoptedOrder(o.OrderId, intentId, instrument, OrderRole.ENTRY, "BROKER_REGISTRY_MISSING_ADOPT", oi, NowEvent());
                _adoptionConvergence.ClearOrder(id);
                Log?.Write(RobotEvents.ExecutionBase(NowEvent(), intentId, instrument, "ADOPTION_SUCCESS", new
                {
                    broker_order_id = o.OrderId,
                    intent_id = intentId,
                    order_class = "ENTRY_STOP",
                    source = "REGISTRY_BROKER_DIVERGENCE",
                    iea_instance_id = InstanceId
                }));
                return true;
            }
            else
            {
                RegisterUnownedOrder(o.OrderId, intentId, instrument, "BROKER_REGISTRY_MISSING_UNOWNED", NowEvent(), isEntryOrder: true,
                    classAsRecoverableRobotOwned: true);
            }
        }
        return false;
    }

    /// <summary>
    /// Gap 3: Deduplicate execution callbacks. Returns true if duplicate (caller should skip), false if new (marks as processed).
    /// Parses NT types and delegates to TryMarkAndCheckDuplicateCore.
    /// </summary>
    internal bool TryMarkAndCheckDuplicate(object execution, object order)
    {
        if (execution == null || order == null) return false;
        try
        {
            dynamic dynExec = execution;
            dynamic dynOrder = order;
            var execId = dynExec.ExecutionId as string;
            var orderId = (dynOrder.OrderId ?? "").ToString();
            var time = dynExec.Time;
            long ticks = 0;
            if (time != null)
            {
                if (time is DateTime dt) ticks = dt.Ticks;
                else if (time is DateTimeOffset dto) ticks = dto.UtcTicks;
                else try { ticks = ((dynamic)time).Ticks; } catch { }
            }
            var qty = (int)(dynExec.Quantity ?? 0);
            var mpos = (dynExec.MarketPosition?.ToString() ?? "");
            return TryMarkAndCheckDuplicateCore(execId, orderId, ticks, qty, mpos);
        }
        catch { return false; }
    }

    private void EvictDedupEntries()
    {
        var cutoff = NowEvent().AddMinutes(-DEDUP_MAX_AGE_MINUTES);
        var toRemove = new List<string>();
        foreach (var kvp in _processedExecutionIds.OrderBy(k => k.Key))
        {
            if (kvp.Value < cutoff)
                toRemove.Add(kvp.Key);
        }
        foreach (var k in toRemove)
            _processedExecutionIds.TryRemove(k, out _);
    }

    /// <summary>
    /// Deterministic snapshot for replay hash. Keys sorted for canonical serialization.
    /// </summary>
    internal IEASnapshot GetSnapshot()
    {
        var snap = new IEASnapshot
        {
            InstrumentBlocked = _instrumentBlocked
        };
        foreach (var kvp in OrderMap.OrderBy(k => k.Key))
        {
            var oi = kvp.Value;
            snap.OrderMap[kvp.Key] = new OrderInfoSnapshot
            {
                IntentId = oi.IntentId,
                OrderId = oi.OrderId,
                OrderType = oi.OrderType ?? "",
                State = oi.State,
                FilledQuantity = oi.FilledQuantity,
                EntryFillTime = oi.EntryFillTime,
                IsEntryOrder = oi.IsEntryOrder,
                ProtectiveStopAcknowledged = oi.ProtectiveStopAcknowledged,
                ProtectiveTargetAcknowledged = oi.ProtectiveTargetAcknowledged
            };
        }
        foreach (var kvp in IntentPolicy.OrderBy(k => k.Key))
        {
            var p = kvp.Value;
            snap.IntentPolicy[kvp.Key] = new IntentPolicySnapshot
            {
                IntentId = kvp.Key,
                ExpectedQuantity = p.ExpectedQuantity,
                MaxQuantity = p.MaxQuantity
            };
        }
        foreach (var kvp in IntentMap.OrderBy(k => k.Key))
        {
            var i = kvp.Value;
            snap.IntentMap[kvp.Key] = new IntentSnapshot
            {
                IntentId = kvp.Key,
                Instrument = i.Instrument ?? "",
                Direction = i.Direction,
                StopPrice = i.StopPrice,
                TargetPrice = i.TargetPrice,
                BeTrigger = i.BeTrigger,
                EntryPrice = i.EntryPrice
            };
        }
        foreach (var kvp in _processedExecutionIds.OrderBy(k => k.Key))
            snap.DedupState[kvp.Key] = kvp.Value.ToString("o");
        // BE state now in adapter (EvaluateBreakEvenCore); IEA no longer tracks it

        // BE diagnostics: per-intent BE state for invariant checks (keys sorted for determinism)
        if (Executor != null)
        {
            var diagByIntent = new Dictionary<string, BEDiagnosticSnapshot>(StringComparer.OrdinalIgnoreCase);
            var activeIntents = Executor.GetActiveIntentsForBEMonitoring(null);
            foreach (var (intentId, intent, beTriggerPrice, entryPrice, _, direction) in activeIntents)
            {
                diagByIntent[intentId] = new BEDiagnosticSnapshot
                {
                    IntentId = intentId,
                    BeTriggerPrice = beTriggerPrice,
                    EntryPrice = entryPrice,
                    Direction = direction ?? "",
                    PriceCrossed = false,
                    BeTriggered = false
                };
            }
            foreach (var kvp in diagByIntent.OrderBy(k => k.Key))
                snap.BeDiagnostics[kvp.Key] = kvp.Value;
        }

        return snap;
    }

    /// <summary>
    /// Phase 3: Evaluate break-even triggers directly (replay, NT strategy thread). Runs synchronously; no queue.
    /// Delegates to Executor.EvaluateBreakEvenCore (single evaluation function; branch only at mutation).
    /// </summary>
    internal void EvaluateBreakEvenDirect(decimal tickPrice, DateTimeOffset eventTime, bool hasEventTime, string executionInstrument)
    {
        if (Executor == null) return;
        Executor.EvaluateBreakEvenCore(tickPrice, eventTime, executionInstrument);
    }

    /// <summary>
    /// Phase 3: Evaluate break-even triggers. Enqueues via execution queue for serialization with fills.
    /// </summary>
    public void EvaluateBreakEven(decimal tickPrice, DateTimeOffset? tickTimeFromEvent, string executionInstrument)
    {
        if (Executor == null || Log == null) return;
        var eventTime = tickTimeFromEvent ?? NowEvent();
        Enqueue(() => Executor.EvaluateBreakEvenCore(tickPrice, eventTime, executionInstrument), "BreakEvenEvaluate");
    }
}
#endif
