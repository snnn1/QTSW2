// NinjaTrader-specific implementation using real NT APIs
// This file is compiled only when NINJATRADER is defined (inside NT Strategy context)

// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

#if NINJATRADER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.CSharp.RuntimeBinder;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Real NinjaTrader API implementation for SIM adapter.
/// This partial class provides real NT API calls when running inside NinjaTrader.
/// </summary>
public sealed partial class NinjaTraderSimAdapter
{
    /// <summary>
    /// STEP 3: Handle real NT ExecutionUpdate event.
    /// Called from public HandleExecutionUpdate() method.
    /// </summary>
    private void HandleExecutionUpdateReal(object executionObj, object orderObj)
    {
        // Use dynamic for Execution type to avoid namespace conflicts
        dynamic execution = executionObj;
        var order = orderObj as Order;
        if (execution == null || order == null) return;

        var encodedTag = GetOrderTag(order);
        var intentId = RobotOrderIds.DecodeIntentId(encodedTag);
        string? execIdTrace = null;
        try { dynamic dex = execution; execIdTrace = dex.ExecutionId as string; } catch { }
        int fillQtyTrace = 0;
        try { fillQtyTrace = (int)execution.Quantity; } catch { }
        var utcNow = DateTimeOffset.UtcNow;
        var instTrace = order.Instrument?.MasterInstrument?.Name ?? "";
        _executionTrace?.WriteExecutionTrace(utcNow, "OnExecutionUpdate", "raw_callback", instTrace, intentId ?? "",
            order.OrderId ?? "", execIdTrace ?? "", fillQtyTrace, order.OrderState.ToString());

        var permKey = BuildPermanentExecutionDedupKey(instTrace, execIdTrace, order.OrderId, fillQtyTrace);
        if (!TryMarkFirstPermanentExecutionProcessing(permKey, out var permSkips))
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instTrace, "EXECUTION_DEDUP_SKIPPED_PERMANENT", new
            {
                execution_id = execIdTrace ?? "",
                broker_order_id = order.OrderId,
                fill_qty = fillQtyTrace,
                dedup_key = permKey,
                skipped_count = permSkips
            }));
            return;
        }

        var fillPrice = (decimal)execution.Price;
        var fillQuantity = execution.Quantity;
        
        // CRITICAL FIX: Determine order type from tag (STOP/TARGET suffix) before looking up in _orderMap
        // Protective orders have tags like QTSW2:{intentId}:STOP or QTSW2:{intentId}:TARGET
        // Entry orders have tags like QTSW2:{intentId}
        string? orderTypeFromTag = null;
        bool isProtectiveOrder = false;
        if (!string.IsNullOrEmpty(encodedTag))
        {
            if (encodedTag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase))
            {
                orderTypeFromTag = "STOP";
                isProtectiveOrder = true;
            }
            else if (encodedTag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase))
            {
                orderTypeFromTag = "TARGET";
                isProtectiveOrder = true;
            }
        }
        
        // CRITICAL FIX: Fail-closed behavior for untracked fills
        // If a fill can't be tracked, the position still exists in NinjaTrader but is unprotected
        // We MUST flatten immediately to prevent unprotected position accumulation
        if (string.IsNullOrEmpty(intentId))
        {
            var instrument = order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL",
                new 
                { 
                    error = "Execution update (fill) received for order with missing/invalid tag - position may exist but is untracked",
                    broker_order_id = order.OrderId,
                    order_tag = encodedTag ?? "NULL",
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    instrument = instrument,
                    action = "FLATTEN_IMMEDIATELY",
                    note = "CRITICAL: Fill happened but can't be tracked. Flattening position immediately (fail-closed) to prevent unprotected accumulation."
                }));

            var brokerOrderIdUntracked = order.OrderId?.ToString() ?? "";
            try
            {
                _executionJournal?.UpsertUntrackedFillRecoveryJournal(
                    instrument,
                    brokerOrderIdUntracked,
                    fillQuantity,
                    fillPrice,
                    utcNow,
                    correlationId: null);
            }
            catch (Exception jEx)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "UNTRACKED_FILL_RECOVERY_JOURNAL_UPSERT_FAILED",
                    new
                    {
                        error = jEx.Message,
                        exception_type = jEx.GetType().Name,
                        broker_order_id = brokerOrderIdUntracked,
                        note = "Flatten still proceeds (fail-closed)"
                    }));
            }
            
            // CRITICAL: Flatten position immediately (fail-closed)
            // The fill happened in NinjaTrader, so we must flatten to prevent unprotected position
            // Since we don't have intent_id, we'll flatten the entire instrument position
            try
            {
                // Use a dummy intent_id to flatten instrument position
                // FlattenIntentReal flattens the entire instrument anyway, so this works
                var flattenResult = Flatten("UNKNOWN_UNTrackED_FILL", instrument, utcNow);
                _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "UNTrackED_FILL_FLATTENED",
                    new
                    {
                        broker_order_id = order.OrderId,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        flatten_success = flattenResult.Success,
                        flatten_error = flattenResult.ErrorMessage,
                        note = "Position flattened due to untracked fill (fail-closed behavior)"
                    }));
                
                // Alert if flatten succeeded (info) or failed (critical)
                var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                if (notificationService != null)
                {
                    if (flattenResult.Success)
                    {
                        var title = $"Untracked Fill - Position Flattened - {instrument}";
                        var message = $"Untracked fill occurred (missing/invalid tag) and position was flattened automatically. Broker Order ID: {order.OrderId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}";
                        notificationService.EnqueueNotification($"UNTrackED_FILL_FLATTENED:{order.OrderId}", title, message, priority: 1); // Info priority
                    }
                    else
                    {
                        var title = $"CRITICAL: Untracked Fill - Flatten FAILED - {instrument}";
                        var message = $"Untracked fill occurred but flatten operation FAILED - MANUAL INTERVENTION REQUIRED. Broker Order ID: {order.OrderId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}, Error: {flattenResult.ErrorMessage}";
                        notificationService.EnqueueNotification($"UNTrackED_FILL_FLATTEN_FAILED:{order.OrderId}", title, message, priority: 3); // Highest priority
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "UNTrackED_FILL_FLATTEN_FAILED",
                    new
                    {
                        error = ex.Message,
                        exception_type = ex.GetType().Name,
                        broker_order_id = order.OrderId,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        note = "CRITICAL: Failed to flatten untracked fill position - manual intervention required"
                    }));
                
                // Critical alert for flatten failure
                var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                if (notificationService != null)
                {
                    var title = $"CRITICAL: Untracked Fill - Flatten FAILED - {instrument}";
                    var message = $"Untracked fill occurred (missing/invalid tag) but flatten operation FAILED - MANUAL INTERVENTION REQUIRED. Broker Order ID: {order.OrderId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}, Error: {ex.Message}";
                    notificationService.EnqueueNotification($"UNTrackED_FILL_FLATTEN_FAILED:{order.OrderId}", title, message, priority: 3); // Highest priority
                }
            }
            
            // CRITICAL FIX: Check all instruments for flat positions even for untracked fills
            // Manual flatten may have occurred, and we need to cancel entry stops
            CheckAllInstrumentsForFlatPositions(utcNow);
            
            return; // Fail-closed: don't process untracked fill
        }

        // CRITICAL FIX: Handle race condition where fill arrives before order is fully added to _orderMap
        // This can happen when order state is "Initialized" - order is being submitted but fill arrives immediately
        // CRITICAL FIX: Handle protective orders that aren't in _orderMap
        // Protective orders (STOP/TARGET) are not always added to _orderMap, but we can create OrderInfo from tag
        OrderInfo? orderInfo = null;
        
        if (!_orderMap.TryGetValue(intentId, out orderInfo))
        {
            // If this is a protective order (detected from tag), create OrderInfo on the fly
            if (isProtectiveOrder && !string.IsNullOrEmpty(orderTypeFromTag) && !string.IsNullOrEmpty(intentId))
            {
                // Get intent to get direction and instrument
                if (_intentMap.TryGetValue(intentId, out var intent))
                {
                    orderInfo = new OrderInfo
                    {
                        IntentId = intentId,
                        Instrument = intent.Instrument ?? order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN",
                        OrderId = order.OrderId,
                        OrderType = orderTypeFromTag,
                        Direction = intent.Direction ?? "",
                        Quantity = order.Quantity,
                        Price = orderTypeFromTag == "STOP" ? (decimal?)order.StopPrice : (decimal?)order.LimitPrice,
                        State = "SUBMITTED",
                        NTOrder = order,
                        IsEntryOrder = false, // Protective order, not entry
                        FilledQuantity = 0
                    };
                    
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "PROTECTIVE_ORDER_FILL_TRACKED_FROM_TAG",
                        new
                        {
                            broker_order_id = order.OrderId,
                            tag = encodedTag,
                            order_type = orderTypeFromTag,
                            intent_id = intentId,
                            fill_price = fillPrice,
                            fill_quantity = fillQuantity,
                            note = "Protective order fill tracked from tag (order not in _orderMap - created OrderInfo on the fly)"
                        }));
                }
            }
        }
        
        // If still no orderInfo, handle as untracked
        if (orderInfo == null)
        {
            var instrument = order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
            var orderState = order.OrderState;
            
            // RACE CONDITION FIX: If order is in Initialized state, wait briefly and retry
            // Order is added to _orderMap BEFORE submission (line 706), but there might be a threading visibility issue
            // or the fill is arriving from a different order instance
            if (orderState == OrderState.Initialized)
            {
                // EXECUTION AUDIT FIX 3: Increased retry delay from 50ms to 100ms for better race condition resolution
                // Brief wait for order to be added to map (max 3 retries, 100ms each)
                const int MAX_RETRIES = 3;
                const int RETRY_DELAY_MS = 100; // Increased from 50ms to improve race condition resolution
                
                bool found = false;
                for (int retry = 0; retry < MAX_RETRIES; retry++)
                {
                    if (retry > 0)
                    {
                        System.Threading.Thread.Sleep(RETRY_DELAY_MS);
                    }
                    
                    if (_orderMap.TryGetValue(intentId, out orderInfo))
                    {
                        found = true;
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_UPDATE_RACE_CONDITION_RESOLVED",
                            new 
                            { 
                                broker_order_id = order.OrderId, 
                                tag = encodedTag,
                                intent_id = intentId,
                                fill_price = fillPrice,
                                fill_quantity = fillQuantity,
                                retry_count = retry + 1,
                                order_state = orderState.ToString(),
                                note = "Fill arrived before order was visible in map - retry succeeded (race condition resolved)"
                            }));
                        break; // Found it - continue processing below
                    }
                }
                
                if (!found)
                {
                    // Still not found after retries - flatten (fail-closed)
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL",
                        new 
                        { 
                            error = "Execution update (fill) received for untracked order - order not found in map after retries",
                            broker_order_id = order.OrderId, 
                            tag = encodedTag,
                            intent_id = intentId,
                            fill_price = fillPrice,
                            fill_quantity = fillQuantity,
                            instrument = instrument,
                            order_state = orderState.ToString(),
                            retry_count = MAX_RETRIES,
                            action = "FLATTEN_IMMEDIATELY",
                            note = "CRITICAL: Fill happened but order not in tracking map after retries. Flattening position immediately (fail-closed) to prevent unprotected accumulation."
                        }));
                    
                    bool flattenOk1 = false; string? flattenErr1 = null;
                    try
                    {
                        var flattenResult = Flatten(intentId, instrument, utcNow);
                        flattenOk1 = flattenResult.Success;
                        flattenErr1 = flattenResult.ErrorMessage;
                        
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "UNKNOWN_ORDER_FILL_FLATTENED",
                            new
                            {
                                broker_order_id = order.OrderId,
                                intent_id = intentId,
                                fill_price = fillPrice,
                                fill_quantity = fillQuantity,
                                flatten_success = flattenResult.Success,
                                flatten_error = flattenResult.ErrorMessage,
                                note = "Position flattened due to untracked order fill (fail-closed behavior)"
                            }));
                        
                        var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                        if (notificationService != null)
                        {
                            if (flattenResult.Success)
                            {
                                var title = $"Unknown Order Fill - Position Flattened - {instrument}";
                                var message = $"Fill occurred for order not found in tracking map and position was flattened automatically. Intent: {intentId}, Broker Order ID: {order.OrderId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}";
                                notificationService.EnqueueNotification($"UNKNOWN_ORDER_FILL_FLATTENED:{intentId}", title, message, priority: 1);
                            }
                            else
                            {
                                var title = $"CRITICAL: Unknown Order Fill - Flatten FAILED - {instrument}";
                                var message = $"Fill occurred for order not found in tracking map but flatten operation FAILED - MANUAL INTERVENTION REQUIRED. Intent: {intentId}, Broker Order ID: {order.OrderId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}, Error: {flattenResult.ErrorMessage}";
                                notificationService.EnqueueNotification($"UNKNOWN_ORDER_FILL_FLATTEN_FAILED:{intentId}", title, message, priority: 3);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        flattenErr1 = ex.Message;
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "UNKNOWN_ORDER_FILL_FLATTEN_FAILED",
                            new
                            {
                                error = ex.Message,
                                exception_type = ex.GetType().Name,
                                broker_order_id = order.OrderId,
                                intent_id = intentId,
                                note = "CRITICAL: Failed to flatten untracked order fill position - manual intervention required"
                            }));
                    }

                    var orphanDir = (order.OrderAction == OrderAction.Buy || order.OrderAction == OrderAction.BuyToCover)
                        ? SlotDirection.Long : SlotDirection.Short;
                    var orphanSlotId1 = RecordOrphanFillIfEnabled(instrument, order.OrderId ?? "", intentId, fillPrice, fillQuantity, utcNow, OrphanReason.UnknownOrder, orphanDir);
                    NotifyOrphanFlattenResult(instrument, orphanSlotId1, flattenOk1, flattenErr1, utcNow);
                    return; // Fail-closed: don't process untracked fill
                }
            }
            else
            {
                // Order not in Initialized state - immediate flatten (fail-closed)
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL",
                    new 
                    { 
                        error = "Execution update (fill) received for untracked order - position may exist but is untracked",
                        broker_order_id = order.OrderId, 
                        tag = encodedTag,
                        intent_id = intentId,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        instrument = instrument,
                        order_state = orderState.ToString(),
                        action = "FLATTEN_IMMEDIATELY",
                        note = "CRITICAL: Fill happened but order not in tracking map. Flattening position immediately (fail-closed) to prevent unprotected accumulation."
                    }));
                
                bool flattenOk2 = false; string? flattenErr2 = null;
                try
                {
                    var flattenResult = Flatten(intentId, instrument, utcNow);
                    flattenOk2 = flattenResult.Success;
                    flattenErr2 = flattenResult.ErrorMessage;
                    
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "UNKNOWN_ORDER_FILL_FLATTENED",
                        new
                        {
                            broker_order_id = order.OrderId,
                            intent_id = intentId,
                            fill_price = fillPrice,
                            fill_quantity = fillQuantity,
                            flatten_success = flattenResult.Success,
                            flatten_error = flattenResult.ErrorMessage,
                            note = "Position flattened due to untracked order fill (fail-closed behavior)"
                        }));
                }
                catch (Exception ex)
                {
                    flattenErr2 = ex.Message;
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "UNKNOWN_ORDER_FILL_FLATTEN_FAILED",
                        new
                        {
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            broker_order_id = order.OrderId,
                            intent_id = intentId,
                            fill_price = fillPrice,
                            fill_quantity = fillQuantity,
                            note = "CRITICAL: Failed to flatten untracked order fill position - manual intervention required"
                        }));
                    
                    var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                    if (notificationService != null)
                    {
                        var title = $"CRITICAL: Unknown Order Fill - Flatten FAILED - {instrument}";
                        var message = $"Fill occurred for order not found in tracking map but flatten operation FAILED - MANUAL INTERVENTION REQUIRED. Intent: {intentId}, Broker Order ID: {order.OrderId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}, Error: {ex.Message}";
                        notificationService.EnqueueNotification($"UNKNOWN_ORDER_FILL_FLATTEN_FAILED:{intentId}", title, message, priority: 3);
                    }
                }

                var orphanDir2 = (order.OrderAction == OrderAction.Buy || order.OrderAction == OrderAction.BuyToCover)
                    ? SlotDirection.Long : SlotDirection.Short;
                var orphanSlotId2 = RecordOrphanFillIfEnabled(instrument, order.OrderId ?? "", intentId, fillPrice, fillQuantity, utcNow, OrphanReason.UnknownOrder, orphanDir2);
                NotifyOrphanFlattenResult(instrument, orphanSlotId2, flattenOk2, flattenErr2, utcNow);
                return; // Fail-closed: don't process untracked fill
            }
        }

        var instFill = orderInfo.Instrument.Trim();
        var bid = order.OrderId ?? "";
        string? brokerExecIdFill = null;
        try { dynamic d = execution; brokerExecIdFill = d.ExecutionId as string; } catch { }
        _executionTrace?.WriteExecutionTrace(utcNow, "Fill", "raw_callback", instFill, intentId, bid,
            brokerExecIdFill ?? "", fillQuantity, order.OrderState.ToString());
        _executionTrace?.WriteExecutionTrace(utcNow, "NotifyExecutionTrigger", "before_notify", instFill, intentId, bid,
            brokerExecIdFill ?? "", fillQuantity, order.OrderState.ToString());
        _onMismatchExecutionTrigger?.Invoke(instFill, utcNow, new MismatchExecutionTriggerDetails
        {
            IntentId = intentId,
            FillDelta = fillQuantity,
            SuppressHardJournalIntegrityActions = true
        });
        _executionTrace?.WriteExecutionTrace(utcNow, "NotifyExecutionTrigger", "after_notify", instFill, intentId, bid,
            brokerExecIdFill ?? "", fillQuantity, order.OrderState.ToString());

        // Use orderTypeFromTag when present (ground truth for STOP/TARGET); else fall back to tracked OrderInfo.
        var orderTypeForContext = orderTypeFromTag ?? orderInfo.OrderType;

        // Robot flatten orders: tag decodes to pseudo intentId "FLATTEN" — skip IntentMap and journal via coordinator path.
        var isRobotFlattenOrder = string.Equals(orderInfo.OrderType, "FLATTEN", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(encodedTag) &&
                encodedTag.StartsWith($"{RobotOrderIds.Prefix}FLATTEN:", StringComparison.OrdinalIgnoreCase));
        if (isRobotFlattenOrder && order != null)
        {
            var flattenInst = order.Instrument?.MasterInstrument?.Name ?? orderInfo.Instrument?.Trim() ?? "UNKNOWN";
            ProcessBrokerFlattenFill(execution, flattenInst, fillPrice, fillQuantity, utcNow, order.OrderId, order);
            return;
        }

        // CRITICAL: Resolve intent context before any journal call
        IntentContext context;
        if (!ResolveIntentContextOrFailClosed(intentId, encodedTag, orderTypeForContext, orderInfo.Instrument,
            fillPrice, fillQuantity, utcNow, out context,
            out var ctxFlattenAttempted, out var ctxFlattenOk, out var ctxFlattenErr))
        {
            // Context resolution failed -- flatten attempted inside ResolveIntentContextOrFailClosed.
            // Record orphan to ledger so the exposure is tracked even if flatten fails.
            var ctxFailDir = (order.OrderAction == OrderAction.Buy || order.OrderAction == OrderAction.BuyToCover)
                ? SlotDirection.Long : SlotDirection.Short;
            var ctxOrphanSlotId = RecordOrphanFillIfEnabled(orderInfo.Instrument, order.OrderId ?? "", intentId,
                fillPrice, fillQuantity, utcNow, OrphanReason.ContextResolutionFailed, ctxFailDir);
            if (ctxFlattenAttempted)
                NotifyOrphanFlattenResult(orderInfo.Instrument, ctxOrphanSlotId, ctxFlattenOk, ctxFlattenErr, utcNow);
            return; // Fail-closed
        }

        // Explicit Entry vs Exit Classification BEFORE cumulative accounting.
        // Protective orders (STOP/TARGET) are exit orders even if orderInfo.IsEntryOrder is true (from entry row in _orderMap).
        bool isEntryFill = !isProtectiveOrder && orderInfo.IsEntryOrder == true;
        if (isEntryFill)
        {
            // ENTRY only: track cumulative fills on the entry order row, emit INTENT_FILL_UPDATE, entry overfill guard.
            orderInfo.FilledQuantity += fillQuantity;
            var filledTotal = orderInfo.FilledQuantity;

            var expectedQty = orderInfo.ExpectedQuantity > 0 ? orderInfo.ExpectedQuantity :
                (IntentPolicy.TryGetValue(intentId, out var exp) ? exp.ExpectedQuantity : 0);
            var maxQty = orderInfo.MaxQuantity > 0 ? orderInfo.MaxQuantity :
                (IntentPolicy.TryGetValue(intentId, out var exp2) ? exp2.MaxQuantity : 0);
            var policySource = string.IsNullOrWhiteSpace(orderInfo.PolicySource) ? "UNKNOWN" : orderInfo.PolicySource;
            if (expectedQty <= 0 && orderInfo.Quantity > 0)
            {
                expectedQty = orderInfo.Quantity;
                maxQty = maxQty > 0 ? maxQty : orderInfo.Quantity;
                policySource = "ORDER_INFO_FILL_FALLBACK";
                orderInfo.ExpectedQuantity = expectedQty;
                orderInfo.MaxQuantity = maxQty;
                orderInfo.PolicySource = policySource;
                orderInfo.CanonicalInstrument = string.IsNullOrWhiteSpace(orderInfo.CanonicalInstrument)
                    ? context.CanonicalInstrument
                    : orderInfo.CanonicalInstrument;
                orderInfo.ExecutionInstrument = string.IsNullOrWhiteSpace(orderInfo.ExecutionInstrument)
                    ? context.ExecutionInstrument
                    : orderInfo.ExecutionInstrument;

                if (!string.IsNullOrWhiteSpace(intentId))
                {
                    IntentPolicy[intentId] = new IntentPolicyExpectation
                    {
                        ExpectedQuantity = expectedQty,
                        MaxQuantity = maxQty,
                        PolicySource = policySource,
                        CanonicalInstrument = orderInfo.CanonicalInstrument,
                        ExecutionInstrument = orderInfo.ExecutionInstrument
                    };
                }

                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument,
                    "INTENT_POLICY_RESTORED_FROM_ORDER_INFO", new
                    {
                        intent_id = intentId,
                        broker_order_id = order.OrderId ?? "",
                        order_qty = orderInfo.Quantity,
                        expected_qty = expectedQty,
                        max_qty = maxQty,
                        policy_source = policySource,
                        reason = "Entry fill order row had broker quantity but no expected quantity policy."
                    }));
            }
            else if (expectedQty > 0 && maxQty <= 0)
            {
                maxQty = expectedQty;
                orderInfo.MaxQuantity = maxQty;
            }

            var quantityPolicyMissing = expectedQty <= 0 || maxQty <= 0;
            var remainingQty = expectedQty > 0 ? expectedQty - filledTotal : -filledTotal;
            var overfill = expectedQty > 0 && filledTotal > expectedQty;

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument,
                "INTENT_FILL_UPDATE", new
                {
                    intent_id = intentId,
                    fill_qty = fillQuantity,
                    cumulative_filled_qty = filledTotal,
                    expected_qty = expectedQty,
                    max_qty = maxQty,
                    remaining_qty = remainingQty,
                    quantity_policy_missing = quantityPolicyMissing,
                    policy_source = policySource,
                    overfill = overfill
                }));

            if (quantityPolicyMissing)
            {
                TriggerQuantityEmergency(intentId, "INTENT_QUANTITY_POLICY_MISSING", utcNow, new Dictionary<string, object>
                {
                    { "expected_qty", expectedQty },
                    { "actual_filled_qty", filledTotal },
                    { "last_fill_qty", fillQuantity },
                    { "order_qty", orderInfo.Quantity },
                    { "reason", "Entry fill has no expected quantity policy; journal the fill but block new risk." }
                });
            }
            else if (overfill)
            {
                TriggerQuantityEmergency(intentId, "INTENT_OVERFILL_EMERGENCY", utcNow, new Dictionary<string, object>
                {
                    { "expected_qty", expectedQty },
                    { "actual_filled_qty", filledTotal },
                    { "last_fill_qty", fillQuantity },
                    { "reason", "Fill exceeded expected quantity" }
                });
            }

            // Entry fill
            var fillGroupId = NinjaTraderSimAdapter.ComputeFillGroupId(brokerExecIdFill, bid,
                order.OrderId ?? "", utcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), fillPrice,
                fillQuantity);
            var parityKey = fillGroupId + "|" + intentId.Trim();
            var entrySign = string.Equals(context.Direction, "Short", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
            JournalParityPendingLedger.TryRecordTrustedFill(context.ExecutionInstrument.Trim(), parityKey,
                entrySign * fillQuantity, intentId, utcNow);

            var isLongEntry = !string.Equals(context.Direction, "Short", StringComparison.OrdinalIgnoreCase);
            _pendingFillBridge?.RecordEntryFillObserved(
                orderInfo.Instrument.Trim(),
                fillQuantity,
                isLongEntry,
                utcNow,
                context.IntentId,
                order.OrderId?.ToString() ?? "");

            _executionJournal.RecordEntryFill(
                context.IntentId, 
                context.TradingDate, 
                context.Stream,
                fillPrice, 
                fillQuantity,  // DELTA ONLY - not filledTotal
                utcNow, 
                context.ContractMultiplier, 
                context.Direction,
                context.ExecutionInstrument, 
                context.CanonicalInstrument,
                brokerOrderInstrumentKey: null,
                parityPendingDedupeKey: parityKey);

            if (FeatureFlags.CanonicalOwnershipLedgerEnabled && _ownershipLedger != null)
            {
                var dir = string.Equals(context.Direction, "Short", StringComparison.OrdinalIgnoreCase)
                    ? SlotDirection.Short : SlotDirection.Long;
                _ownershipLedger.RecordMappedEntryFill(
                    GetLedgerAccountName(), context.ExecutionInstrument.Trim(),
                    context.IntentId, context.Stream, dir, fillQuantity, utcNow, 0);
            }

            // Log fill event
            if (filledTotal < orderInfo.Quantity)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_PARTIAL_FILL",
                    new
                    {
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        filled_total = filledTotal,
                        order_quantity = orderInfo.Quantity,
                        broker_order_id = order.OrderId,
                        order_type = orderInfo.OrderType,
                        stream = context.Stream
                    }));
            }
            else
            {
                orderInfo.State = "FILLED";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_FILLED",
                    new
                    {
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        filled_total = filledTotal,
                        broker_order_id = order.OrderId,
                        order_type = orderInfo.OrderType,
                        stream = context.Stream
                    }));
            }
            
            // Register entry fill with coordinator
            if (_intentMap.TryGetValue(intentId, out var entryIntent))
            {
                // CRITICAL FIX: Coordinator accumulates internally, so pass fillQuantity (delta) not filledTotal (cumulative)
                // OnEntryFill does: exposure.EntryFilledQty += qty, so passing cumulative totals causes double-counting
                var exposureInstrument = !string.IsNullOrWhiteSpace(entryIntent.ExecutionInstrument)
                    ? entryIntent.ExecutionInstrument
                    : entryIntent.Instrument;
                _coordinator?.OnEntryFill(intentId, fillQuantity, entryIntent.Stream, exposureInstrument, entryIntent.Direction ?? "", utcNow);
                
                // CRITICAL FIX: Check if position is now flat after entry fill - if so, cancel entry stop orders
                // This handles manual position closures (user clicks "Flatten" in NinjaTrader UI) that happen
                // immediately after entry fills (race condition where user flattens before protective orders submit)
                CheckAndCancelEntryStopsOnPositionFlat(orderInfo.Instrument, utcNow);
                
                // CRITICAL FIX: Pass filledTotal (cumulative) to HandleEntryFill for protective orders
                // HandleEntryFill needs TOTAL filled quantity to submit protective orders that cover the ENTIRE position
                // For incremental fills, protective orders must be updated to cover cumulative position, not just delta
                // filledTotal is already updated on the entry-only path above.
                HandleEntryFill(intentId, entryIntent, fillPrice, fillQuantity, filledTotal, utcNow);
            }
            else
            {
                // This should not happen - ResolveIntentContextOrFailClosed already checked
                // But handle defensively
                _log.Write(RobotEvents.EngineBase(utcNow, context.TradingDate, "EXECUTION_ERROR", "ENGINE",
                    new 
                    { 
                        error = "Intent not found in _intentMap after context resolution - defensive check",
                        intent_id = intentId,
                        fill_price = fillPrice,
                        fill_quantity = filledTotal,
                        order_type = orderInfo.OrderType,
                        broker_order_id = order.OrderId,
                        instrument = orderInfo.Instrument,
                        stream = context.Stream,
                        action_taken = "FLATTENING_POSITION",
                        note = "Entry order filled but intent lost after context resolution - flattening position"
                    }));

                var orphanDirCtx = string.Equals(context.Direction, "Short", StringComparison.OrdinalIgnoreCase)
                    ? SlotDirection.Short : SlotDirection.Long;
                var orphanSlotIdCtx = RecordOrphanFillIfEnabled(orderInfo.Instrument, order.OrderId ?? "", intentId, fillPrice, fillQuantity, utcNow, OrphanReason.IntentLostAfterContext, orphanDirCtx);

                bool flattenOkCtx = false; string? flattenErrCtx = null;
                try
                {
                    var flattenResult = Flatten(intentId, orderInfo.Instrument, utcNow);
                    flattenOkCtx = flattenResult.Success;
                    flattenErrCtx = flattenResult.ErrorMessage;
                    
                    _log.Write(RobotEvents.EngineBase(utcNow, context.TradingDate, "INTENT_NOT_FOUND_FLATTENED", "ENGINE",
                        new
                        {
                            intent_id = intentId,
                            instrument = orderInfo.Instrument,
                            stream = context.Stream,
                            fill_price = fillPrice,
                            fill_quantity = filledTotal,
                            flatten_success = flattenResult.Success,
                            flatten_error = flattenResult.ErrorMessage,
                            note = "Position flattened due to missing intent - protective orders could not be placed"
                        }));
                    
                    _standDownStreamCallback?.Invoke(context.Stream, utcNow, $"INTENT_NOT_FOUND:{intentId}");
                    
                    var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                    if (notificationService != null)
                    {
                        var title = $"CRITICAL: Intent Not Found - {orderInfo.Instrument}";
                        var message = $"Entry order filled but intent was not registered. Position flattened. Stream: {context.Stream}, Intent: {intentId}";
                        notificationService.EnqueueNotification($"INTENT_NOT_FOUND:{intentId}", title, message, priority: 2);
                    }
                }
                catch (Exception ex)
                {
                    flattenErrCtx = ex.Message;
                    _log.Write(RobotEvents.EngineBase(utcNow, context.TradingDate, "INTENT_NOT_FOUND_FLATTEN_FAILED", "ENGINE",
                        new
                        {
                            intent_id = intentId,
                            instrument = orderInfo.Instrument,
                            stream = context.Stream,
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            note = "Failed to flatten position after intent not found - manual intervention may be required"
                        }));
                }
                NotifyOrphanFlattenResult(orderInfo.Instrument, orphanSlotIdCtx, flattenOkCtx, flattenErrCtx, utcNow);
            }
        }
        else if (orderTypeForContext == "STOP" || orderTypeForContext == "TARGET")
        {
            // STOP/TARGET: do not mutate orderInfo.FilledQuantity (entry cumulative lives only on the entry order row).
            // filled_total here is diagnostic for this exit leg (prior exit fills on this row were not accumulated per strict separation).
            var exitFilledTotalForLog = orderInfo.FilledQuantity + fillQuantity;

            // LATE-FILL PROTECTION: If intent already completed, this is a stale/late fill (race with sibling cancel).
            // Do NOT process as normal - emit critical event and route to anomaly path.
            var tradingDate = context.TradingDate ?? "";
            var stream = context.Stream ?? "";
            var completedEntry = _executionJournal.GetEntry(intentId, tradingDate, stream);
            if (completedEntry != null && completedEntry.TradeCompleted)
            {
                var boundedLateTerminalFill = false;
                var completionAgeMs = -1L;
                if (string.Equals(completedEntry.CompletionReason, CompletionReasons.RECONCILIATION_BROKER_FLAT, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(completedEntry.CompletedAtUtc) &&
                    DateTimeOffset.TryParse(completedEntry.CompletedAtUtc, out var completedAtUtc))
                {
                    completionAgeMs = Math.Max(0L, (long)(utcNow - completedAtUtc).TotalMilliseconds);
                    boundedLateTerminalFill = completionAgeMs <= 5000;
                }

                if (boundedLateTerminalFill)
                {
                    _executionJournal.RecordExitFill(
                        context.IntentId,
                        context.TradingDate,
                        context.Stream,
                        fillPrice,
                        fillQuantity,
                        orderTypeForContext,
                        utcNow);
                    var reconciledEntry = _executionJournal.GetEntry(intentId, tradingDate, stream);

                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "COMPLETED_INTENT_LATE_TERMINAL_FILL_RECONCILED", "ENGINE",
                        new
                        {
                            intent_id = intentId,
                            broker_order_id = order.OrderId,
                            instrument = orderInfo.Instrument,
                            stream = stream,
                            fill_price = fillPrice,
                            fill_quantity = fillQuantity,
                            side = orderInfo.Direction,
                            exit_order_type = orderTypeForContext,
                            execution_timestamp_utc = utcNow.ToString("o"),
                            prior_completion_reason = completedEntry.CompletionReason,
                            completed_at_utc = completedEntry.CompletedAtUtc,
                            completion_age_ms = completionAgeMs,
                            mapped = true,
                            journal_completion_reason = reconciledEntry?.CompletionReason,
                            journal_exit_avg_fill_price = reconciledEntry?.ExitAvgFillPrice,
                            journal_realized_pnl_gross = reconciledEntry?.RealizedPnLGross,
                            action = reconciledEntry?.RealizedPnLGross.HasValue == true
                                ? "BOUNDED_LATE_TERMINAL_FILL_JOURNAL_UPGRADED"
                                : "BOUNDED_LATE_TERMINAL_FILL_TYPE_RECONCILED",
                            note = "Late terminal fill arrived shortly after reconciliation broker-flat completion; journal upgrade path was invoked without replaying ownership/coordinator side effects."
                        }));
                    return;
                }

                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "COMPLETED_INTENT_RECEIVED_FILL", "ENGINE",
                    new
                    {
                        error = "Fill received for intent already TradeCompleted - stale/late fill on terminal intent",
                        intent_id = intentId,
                        broker_order_id = order.OrderId,
                        instrument = orderInfo.Instrument,
                        stream = stream,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        side = orderInfo.Direction,
                        exit_order_type = orderTypeForContext,
                        execution_timestamp_utc = utcNow.ToString("o"),
                        mapped = true,
                        action = "ANOMALY_LOGGED",
                        note = "CRITICAL: Late fill on completed intent - route to reconciliation. Do not process as normal exit."
                    }));
                var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                if (notificationService != null)
                {
                    notificationService.EnqueueNotification($"COMPLETED_INTENT_RECEIVED_FILL:{intentId}",
                        "CRITICAL: Late Fill on Completed Intent",
                        $"Fill received for intent {intentId} already TradeCompleted. Broker Order: {order.OrderId}, Qty: {fillQuantity}, Price: {fillPrice}. Route to reconciliation.",
                        priority: 2);
                }
                return; // Fail-closed: do not process as normal fill
            }

            // Exit fill - intent is not yet completed
            // CRITICAL FIX: Use orderTypeForContext (from tag) instead of orderInfo.OrderType
            // orderInfo might be from entry order if protective order wasn't added to _orderMap yet
            _executionJournal.RecordExitFill(
                context.IntentId, 
                context.TradingDate, 
                context.Stream,
                fillPrice, 
                fillQuantity,  // DELTA ONLY - not filledTotal
                orderTypeForContext, // Use tag-based order type (ground truth)
                utcNow);

            if (FeatureFlags.CanonicalOwnershipLedgerEnabled && _ownershipLedger != null)
            {
                _ownershipLedger.RecordMappedExitFill(
                    GetLedgerAccountName(), context.ExecutionInstrument.Trim(),
                    context.IntentId, fillQuantity, utcNow, 0);
            }

            // Log exit fill event
            // CRITICAL FIX: Use orderTypeForContext (from tag) for logging - it's the ground truth
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_EXIT_FILL",
                new
                {
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    filled_total = exitFilledTotalForLog,
                    broker_order_id = order.OrderId,
                    exit_order_type = orderTypeForContext, // Use tag-based order type (ground truth)
                    stream = context.Stream
                }));
            
            // CRITICAL FIX: Coordinator accumulates internally, so pass fillQuantity (delta) not filledTotal (cumulative)
            // OnExitFill does: exposure.ExitFilledQty += qty, so passing cumulative totals causes double-counting
            _coordinator?.OnExitFill(intentId, fillQuantity, utcNow);
            
            // CRITICAL FIX: Check if position is now flat after exit fill - if so, cancel entry stop orders
            // This handles manual position closures (user clicks "Flatten" in NinjaTrader UI)
            // When position goes flat, entry stop orders should be cancelled to prevent re-entry
            CheckAndCancelEntryStopsOnPositionFlat(orderInfo.Instrument, utcNow);
            
            // CRITICAL FIX: When protective stop OR target fills, cancel opposite entry stop order to prevent re-entry
            // OCO should handle this, but if OCO fails or there's a race condition, the opposite entry stop
            // can still be active and fill later, creating an unwanted new position
            // This applies to BOTH stop loss and target (limit) fills - when either fills, position is closed
            // CRITICAL FIX: Use orderTypeForContext (from tag) instead of orderInfo.OrderType
            // orderInfo might be from entry order if protective order wasn't added to _orderMap yet
            if ((orderTypeForContext == "STOP" || orderTypeForContext == "TARGET") && _intentMap.TryGetValue(intentId, out var filledIntent))
            {
                // Find the opposite entry intent for this stream
                // Entry intents are created in pairs: one Long, one Short with same TradingDate/Stream/SlotTime
                var oppositeDirection = filledIntent.Direction == "Long" ? "Short" : "Long";
                
                // Find opposite intent by searching _intentMap for same stream with opposite direction
                string? oppositeIntentId = null;
                foreach (var kvp in _intentMap)
                {
                    var otherIntent = kvp.Value;
                    if (otherIntent.Stream == filledIntent.Stream &&
                        otherIntent.TradingDate == filledIntent.TradingDate &&
                        otherIntent.Direction == oppositeDirection &&
                        otherIntent.TriggerReason != null &&
                        (otherIntent.TriggerReason.Contains("ENTRY_STOP_BRACKET_LONG") || 
                         otherIntent.TriggerReason.Contains("ENTRY_STOP_BRACKET_SHORT")))
                    {
                        oppositeIntentId = kvp.Key;
                        break;
                    }
                }
                
                // Cancel opposite entry order if found
                // CRITICAL FIX: Only cancel if opposite entry hasn't filled yet
                // If opposite entry already filled, it has protective orders that should NOT be cancelled
                // (They protect the opposite position and should be managed via OCO groups)
                if (oppositeIntentId != null)
                {
                    // Check if opposite entry already filled (defense-in-depth)
                    // Get journal entry for opposite intent and check if it has an entry fill
                    var oppositeEntryFilled = false;
                    if (_executionJournal != null && !string.IsNullOrEmpty(filledIntent.TradingDate) && !string.IsNullOrEmpty(filledIntent.Stream))
                    {
                        var oppositeEntry = _executionJournal.GetEntry(oppositeIntentId, filledIntent.TradingDate, filledIntent.Stream);
                        oppositeEntryFilled = oppositeEntry != null && (oppositeEntry.EntryFilled || oppositeEntry.EntryFilledQuantityTotal > 0);
                    }
                    
                    if (!oppositeEntryFilled)
                    {
                        // Opposite entry hasn't filled - safe to cancel (only cancels entry order, not protective orders)
                        var cancelled = CancelIntentOrders(oppositeIntentId, utcNow);
                        if (cancelled)
                        {
                            var exitType = orderTypeForContext == "STOP" ? "stop" : "target";
                            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "OPPOSITE_ENTRY_CANCELLED_ON_EXIT_FILL",
                                new
                                {
                                    filled_intent_id = intentId,
                                    opposite_intent_id = oppositeIntentId,
                                    filled_direction = filledIntent.Direction,
                                    opposite_direction = oppositeDirection,
                                    exit_order_type = orderTypeForContext,
                                    stream = context.Stream,
                                    note = $"Cancelled opposite entry stop order when protective {exitType} filled to prevent re-entry"
                                }));
                        }
                    }
                    else
                    {
                        // Opposite entry already filled - don't cancel (would cancel its protective orders)
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "OPPOSITE_ENTRY_ALREADY_FILLED_SKIP_CANCEL",
                            new
                            {
                                filled_intent_id = intentId,
                                opposite_intent_id = oppositeIntentId,
                                filled_direction = filledIntent.Direction,
                                opposite_direction = oppositeDirection,
                                stream = context.Stream,
                                note = "Opposite entry already filled - skipping cancel to avoid cancelling protective orders (OCO should handle entry cancellation)"
                            }));
                    }
                }
                
                var postExitEntry = _executionJournal.GetEntry(intentId, filledIntent.TradingDate ?? "", filledIntent.Stream ?? "");
                if (ShouldTerminalizeAfterExitFill(postExitEntry))
                {
                    // TERMINALIZATION: Cancel remaining protective orders and verify invariant.
                    // Single canonical path for making intent terminal - prevents zombie stops.
                    _iea?.TryTransitionIntentLifecycle(intentId, IntentLifecycleTransition.INTENT_COMPLETED, null, utcNow);
                    TerminalizeIntent(intentId, filledIntent.TradingDate ?? "", filledIntent.Stream ?? "", orderTypeForContext, utcNow);
                }
                else
                {
                    var remainingQty = postExitEntry == null
                        ? (int?)null
                        : Math.Max(0, postExitEntry.EntryFilledQuantityTotal - postExitEntry.ExitFilledQuantityTotal);
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "PROTECTIVE_PARTIAL_EXIT_RETAINED",
                        new
                        {
                            intent_id = intentId,
                            stream = context.Stream,
                            exit_order_type = orderTypeForContext,
                            exit_filled_qty = postExitEntry?.ExitFilledQuantityTotal,
                            entry_filled_qty = postExitEntry?.EntryFilledQuantityTotal,
                            remaining_qty = remainingQty,
                            note = "Partial protective exit observed; remaining protection stays managed and intent is not terminalized."
                        }));
                }
            }
        }
        else
        {
            // Unknown exit type - orphan it
            LogOrphanFill(intentId, encodedTag, orderInfo.OrderType, orderInfo.Instrument, fillPrice, fillQuantity,
                utcNow, context.Stream, "UNKNOWN_EXIT_TYPE");
            RecordOrphanFillIfEnabled(orderInfo.Instrument, order?.OrderId?.ToString() ?? "", intentId,
                fillPrice, fillQuantity, utcNow, OrphanReason.IntentLostAfterContext,
                ParseSlotDirection(context.Direction), OrphanActionTaken.NoAction);
            
            _log.Write(RobotEvents.EngineBase(utcNow, context.TradingDate, "EXECUTION_UNKNOWN_EXIT_TYPE", "ENGINE",
                new
                {
                    intent_id = intentId,
                    order_type = orderInfo.OrderType,
                    instrument = orderInfo.Instrument,
                    stream = context.Stream,
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    action_taken = "EXECUTION_BLOCKED",
                    note = "Unknown exit order type - orphan fill logged, execution blocked"
                }));
            
            return; // Fail-closed
        }
        
        // CRITICAL FIX: Check all instruments for flat positions after EVERY execution update
        // This detects manual position closures that bypass robot code
        // Called after both entry and exit fills to catch manual flattens quickly
        CheckAllInstrumentsForFlatPositions(utcNow);
    }

}

#endif
