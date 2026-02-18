#if NINJATRADER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NinjaTrader.Cbi;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Phase 2: IEA owns aggregation and order submission. Phase 3: BE evaluation.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    // Phase 3: Tick de-dupe (Trap B - event timestamp first)
    private decimal _lastTickPriceForBE;
    private DateTimeOffset _lastTickTimeFromEvent = DateTimeOffset.MinValue;
    private const double BE_DEDUPE_FALLBACK_MS = 50; // When event timestamp unavailable

    // Phase 3: BE evaluation state
    private readonly Dictionary<string, DateTimeOffset> _lastBeModifyAttemptUtcByIntent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _beModifyFailureCountByIntent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _beTriggerReachedPendingModification = new(StringComparer.OrdinalIgnoreCase);
    private const double BE_MODIFY_ATTEMPT_INTERVAL_MS = 200;
    private const int BE_MODIFY_MAX_RETRIES = 25;
    private const double BE_SCAN_THROTTLE_MS = 200;
    private DateTimeOffset _lastBeCheckUtc = DateTimeOffset.MinValue;

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
        if (!IntentMap.TryGetValue(intentId, out var currentIntent))
            return null;

        var toAggregate = new List<(string intentId, string stream, int qty, string? oco)>();
        foreach (var kvp in IntentMap)
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
        Log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENTRY_AGGREGATION_ATTEMPT", state: "ENGINE",
            new
            {
                execution_instrument = instrument,
                current_intent = intentId,
                existing_intents = toAggregate.Select(x => x.intentId).ToList(),
                intent_ids = allIntentIds,
                qty_per_intent = qtyPerIntent,
                total_quantity = totalQty,
                stop_price = stopPrice,
                direction,
                iea_instance_id = InstanceId,
                note = "IEA: Multiple streams at same price - aggregating into one broker order"
            }));

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
                Executor.RecordSubmission(oppId, oppIntent.TradingDate ?? "", oppIntent.Stream ?? "", instrument, $"ENTRY_STOP_{oppositeDirection}", Executor.GetOrderId(oppOrder), utcNow);
            }

            Executor.SubmitOrders(ordersToSubmit);

            foreach (var id in allIntentIds)
            {
                if (IntentMap.TryGetValue(id, out var intent))
                    Executor.RecordSubmission(id, intent.TradingDate ?? "", intent.Stream ?? "", instrument, $"ENTRY_STOP_{direction}", Executor.GetOrderId(order), utcNow);
            }

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
        foreach (var kvp in IntentMap)
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

        if (!Executor.IsExecutionAllowed())
        {
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

        const int MAX_RETRIES = 3;
        const int RETRY_DELAY_MS = 100;
        OrderSubmissionResult? stopResult = null;
        OrderSubmissionResult? targetResult = null;

        for (int attempt = 0; attempt < MAX_RETRIES; attempt++)
        {
            if (attempt > 0) System.Threading.Thread.Sleep(RETRY_DELAY_MS);
            if (!Executor.CanSubmitExit(intentId, totalFilledQuantity))
            {
                var err = "Exit validation failed during retry";
                stopResult = OrderSubmissionResult.FailureResult(err, utcNow);
                targetResult = OrderSubmissionResult.FailureResult(err, utcNow);
                break;
            }

            var protectiveOcoGroup = GenerateProtectiveOcoGroup(intentId, attempt, intent);
            stopResult = Executor.SubmitProtectiveStop(intentId, intent.Instrument, intent.Direction!, intent.StopPrice!.Value, totalFilledQuantity, protectiveOcoGroup, utcNow);

            if (stopResult.Success)
            {
                targetResult = Executor.SubmitTargetOrder(intentId, intent.Instrument, intent.Direction!, intent.TargetPrice!.Value, totalFilledQuantity, protectiveOcoGroup, utcNow);
                if (targetResult.Success) break;
            }
            else
            {
                targetResult = OrderSubmissionResult.FailureResult("Target not attempted - stop failed first", utcNow);
            }
        }

        if (!stopResult!.Success || targetResult == null || !targetResult.Success)
        {
            var failedLegs = new List<string>();
            if (!stopResult.Success) failedLegs.Add($"STOP: {stopResult.ErrorMessage}");
            if (targetResult != null && !targetResult.Success) failedLegs.Add($"TARGET: {targetResult.ErrorMessage}");
            var failureReason = $"Protective orders failed after {MAX_RETRIES} retries: {string.Join(", ", failedLegs)}";
            Executor.FailClosed(intentId, intent, failureReason, "PROTECTIVE_ORDERS_FAILED_FLATTENED", $"PROTECTIVE_FAILURE:{intentId}",
                $"CRITICAL: Protective Order Failure - {intent.Instrument}",
                $"Entry filled but protective orders failed. Position flattened. Stream: {intent.Stream}, Intent: {intentId}. Failures: {failureReason}",
                stopResult, targetResult, new { retry_count = MAX_RETRIES }, utcNow);
        }
    }

    /// <summary>Enqueue execution update for serialized processing.</summary>
    public void EnqueueExecutionUpdate(object execution, object order)
    {
        if (Executor == null) return;
        Enqueue(() =>
        {
            if (!_hasScannedForAdoption)
            {
                _hasScannedForAdoption = true;
                ScanAndAdoptExistingProtectives();
            }
            Executor.ProcessExecutionUpdate(execution, order);
        });
    }

    private bool _hasScannedForAdoption;

    /// <summary>
    /// Gap 4: On first execution update, sync OrderMap with broker reality. Hydration does not mutate broker state.
    /// For each QTSW2 protective (STOP/TARGET) with entry filled per journal, populate OrderMap.
    /// </summary>
    private void ScanAndAdoptExistingProtectives()
    {
        if (Executor == null || Log == null) return;
        var account = Executor.GetAccount() as Account;
        if (account?.Orders == null) return;
        var activeIntents = Executor.GetActiveIntentsForBEMonitoring(ExecutionInstrumentKey);
        var activeIntentIds = new HashSet<string>(activeIntents.Select(x => x.intentId), StringComparer.OrdinalIgnoreCase);
        foreach (Order o in account.Orders)
        {
            if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue;
            var tag = Executor.GetOrderTag(o);
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) continue;
            var intentId = RobotOrderIds.DecodeIntentId(tag);
            if (string.IsNullOrEmpty(intentId) || !activeIntentIds.Contains(intentId)) continue;
            var isStop = tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase);
            var isTarget = tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase);
            if (!isStop && !isTarget) continue;
            var mapKey = $"{intentId}:{(isStop ? "STOP" : "TARGET")}";
            if (!OrderMap.TryGetValue(mapKey, out _))
            {
                var oi = new OrderInfo
                {
                    IntentId = intentId,
                    Instrument = o.Instrument?.MasterInstrument?.Name ?? ExecutionInstrumentKey,
                    OrderId = o.OrderId,
                    OrderType = isStop ? "STOP" : "TARGET",
                    Price = isStop ? (decimal?)o.StopPrice : (decimal?)o.LimitPrice,
                    Quantity = o.Quantity,
                    State = "WORKING",
                    IsEntryOrder = false
                };
                OrderMap[mapKey] = oi;
            }
        }
    }

    /// <summary>
    /// Gap 3: Deduplicate execution callbacks. Returns true if duplicate (caller should skip), false if new (marks as processed).
    /// Key: ExecutionId if available; else orderId|execution.Time.Ticks|execution.Quantity|execution.MarketPosition|execution.OrderId.
    /// Eviction: periodic sweep every 100 inserts, remove entries older than 5 minutes.
    /// </summary>
    internal bool TryMarkAndCheckDuplicate(object execution, object order)
    {
        if (execution == null || order == null) return false;
        string key;
        try
        {
            dynamic dynExec = execution;
            dynamic dynOrder = order;
            var execId = dynExec.ExecutionId as string;
            if (!string.IsNullOrEmpty(execId))
                key = execId;
            else
            {
                var orderId = (dynOrder.OrderId ?? "").ToString();
                var time = dynExec.Time;
                long ticks = 0;
                if (time != null)
                {
                    if (time is DateTime dt) ticks = dt.Ticks;
                    else if (time is DateTimeOffset dto) ticks = dto.UtcTicks;
                    else try { ticks = ((dynamic)time).Ticks; } catch { }
                }
                var qty = dynExec.Quantity?.ToString() ?? "0";
                var mpos = (dynExec.MarketPosition?.ToString() ?? "");
                var execOrderId = (dynExec.OrderId ?? "").ToString();
                key = $"{orderId}|{ticks}|{qty}|{mpos}|{execOrderId}";
            }
        }
        catch { return false; }
        if (string.IsNullOrEmpty(key)) return false;

        if (_processedExecutionIds.TryAdd(key, DateTimeOffset.UtcNow))
        {
            var count = Interlocked.Increment(ref _dedupInsertCount);
            if (count % DEDUP_EVICTION_INTERVAL == 0)
                EvictDedupEntries();
            return false;
        }
        return true;
    }

    private void EvictDedupEntries()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-DEDUP_MAX_AGE_MINUTES);
        var toRemove = new List<string>();
        foreach (var kvp in _processedExecutionIds)
        {
            if (kvp.Value < cutoff)
                toRemove.Add(kvp.Key);
        }
        foreach (var k in toRemove)
            _processedExecutionIds.TryRemove(k, out _);
    }

    /// <summary>
    /// Phase 3: Evaluate break-even triggers. One BE per position. Tick de-dupe (event timestamp first).
    /// Enqueues via execution queue for serialization with fills.
    /// </summary>
    public void EvaluateBreakEven(decimal tickPrice, DateTimeOffset? tickTimeFromEvent, string executionInstrument)
    {
        if (Executor == null || Log == null) return;
        var hasEventTime = tickTimeFromEvent.HasValue;
        var eventTime = tickTimeFromEvent ?? DateTimeOffset.UtcNow;
        Enqueue(() => EvaluateBreakEvenCore(tickPrice, eventTime, hasEventTime, executionInstrument));
    }

    /// <summary>BE evaluation core (runs on worker thread).</summary>
    private void EvaluateBreakEvenCore(decimal tickPrice, DateTimeOffset eventTime, bool hasEventTime, string executionInstrument)
    {
        if (Executor == null || Log == null) return;

        var utcNow = DateTimeOffset.UtcNow;

        // 3.1 Tick de-dupe: if (price == lastPrice && eventTimestamp == lastTimestamp) return
            if (hasEventTime)
            {
                if (tickPrice == _lastTickPriceForBE && eventTime == _lastTickTimeFromEvent)
                    return;
            }
            else
            {
                // Fallback when event timestamp unavailable: 50ms window
                if (tickPrice == _lastTickPriceForBE && (utcNow - _lastBeCheckUtc).TotalMilliseconds < BE_DEDUPE_FALLBACK_MS)
                    return;
            }
            _lastTickPriceForBE = tickPrice;
            _lastTickTimeFromEvent = eventTime;

            // Scan throttle
            if ((utcNow - _lastBeCheckUtc).TotalMilliseconds < BE_SCAN_THROTTLE_MS)
                return;
            _lastBeCheckUtc = utcNow;

            var activeIntents = Executor.GetActiveIntentsForBEMonitoring(executionInstrument);
            if (activeIntents.Count == 0) return;

            var tickSize = Executor.GetTickSize();

            foreach (var (intentId, intent, beTriggerPrice, entryPrice, actualFillPrice, direction) in activeIntents)
            {
                bool beTriggerReached = direction == "Long"
                    ? tickPrice >= beTriggerPrice
                    : direction == "Short"
                        ? tickPrice <= beTriggerPrice
                        : false;

                if (!beTriggerReached)
                {
                    _beTriggerReachedPendingModification.Remove(intentId);
                    continue;
                }

                _beTriggerReachedPendingModification.TryGetValue(intentId, out var triggerReachedAt);
                if (triggerReachedAt == default)
                {
                    _beTriggerReachedPendingModification[intentId] = utcNow;
                    triggerReachedAt = utcNow;
                }

                var breakoutLevel = entryPrice;
                var beStopPrice = direction == "Long"
                    ? breakoutLevel - tickSize
                    : breakoutLevel + tickSize;

                if ((utcNow - (_lastBeModifyAttemptUtcByIntent.TryGetValue(intentId, out var lastAttempt) ? lastAttempt : DateTimeOffset.MinValue)).TotalMilliseconds < BE_MODIFY_ATTEMPT_INTERVAL_MS)
                    continue;
                _lastBeModifyAttemptUtcByIntent[intentId] = utcNow;

                var modifyResult = Executor.ModifyStopToBreakEven(intentId, intent.Instrument ?? "", beStopPrice, utcNow);

                if (modifyResult.Success)
                {
                    _beTriggerReachedPendingModification.Remove(intentId);
                    _lastBeModifyAttemptUtcByIntent.Remove(intentId);
                    _beModifyFailureCountByIntent.Remove(intentId);
                    Log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "BE_TRIGGER_REACHED",
                        new
                        {
                            direction,
                            breakout_level = breakoutLevel,
                            actual_fill_price = actualFillPrice,
                            be_trigger_price = beTriggerPrice,
                            be_stop_price = beStopPrice,
                            tick_size = tickSize,
                            tick_price = tickPrice,
                            iea_instance_id = InstanceId,
                            execution_instrument_key = ExecutionInstrumentKey,
                            account_name = AccountName
                        }));
                }
                else
                {
                    var errorMsg = modifyResult.ErrorMessage ?? "";
                    var isRetryableError = errorMsg.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          errorMsg.IndexOf("Stop order", StringComparison.OrdinalIgnoreCase) >= 0;
                    var stopMissing = isRetryableError;

                    var failCount = _beModifyFailureCountByIntent.TryGetValue(intentId, out var c) ? c + 1 : 1;
                    _beModifyFailureCountByIntent[intentId] = failCount;

                    if (failCount >= BE_MODIFY_MAX_RETRIES)
                    {
                        var actionTaken = stopMissing ? "FLATTEN" : "STAND_DOWN";
                        Log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "BE_MODIFY_MAX_RETRIES_EXCEEDED",
                            new
                            {
                                action_taken = actionTaken,
                                failure_count = failCount,
                                error = errorMsg,
                                iea_instance_id = InstanceId,
                                execution_instrument_key = ExecutionInstrumentKey,
                                account_name = AccountName
                            }));
                        _beModifyFailureCountByIntent.Remove(intentId);
                        _beTriggerReachedPendingModification.Remove(intentId);
                        _lastBeModifyAttemptUtcByIntent.Remove(intentId);

                        if (stopMissing)
                            Executor.Flatten(intentId, intent.Instrument ?? "", utcNow);
                        else
                            Executor.StandDownStream(intent.Stream ?? "", utcNow, $"BE_MODIFY_MAX_RETRIES:{intentId}");
                    }
                }
            }
    }
}
#endif
