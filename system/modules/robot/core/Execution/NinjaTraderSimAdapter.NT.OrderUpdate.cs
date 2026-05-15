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
    /// STEP 3: Handle real NT OrderUpdate event.
    /// Called from public HandleOrderUpdate() method in main adapter.
    /// </summary>
    private void HandleOrderUpdateReal(object orderObj, object orderUpdateObj)
    {
        var order = orderObj as Order;
        // OrderUpdate is the event args type, use dynamic to access it
        dynamic orderUpdate = orderUpdateObj;
        if (order == null) return;

        // Get tag/name (try Tag first, fallback to Name)
        var encodedTag = GetOrderTag(order);
        var parsedTag = RobotOrderIds.ParseTag(encodedTag);
        var intentId = parsedTag.IntentId ?? RobotOrderIds.DecodeIntentId(encodedTag);
        var utcNow = DateTimeOffset.UtcNow;
        var orderState = order.OrderState;
        var instrumentTrace = order.Instrument?.MasterInstrument?.Name ?? "";
        var orderIdTrace = order.OrderId ?? "";
        _executionTrace?.WriteExecutionTrace(utcNow, "OnOrderUpdate", "raw_callback", instrumentTrace, intentId ?? "",
            orderIdTrace, "", 0, orderState.ToString());

        if (TrySkipDuplicateOrderUpdate50ms(instrumentTrace, orderIdTrace, orderState.ToString(), utcNow, out var orderDedupSkips))
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instrumentTrace, "ORDER_UPDATE_DEDUP_SKIPPED", new
            {
                order_id = orderIdTrace,
                order_state = orderState.ToString(),
                skipped_count = orderDedupSkips,
                window_ms = 50
            }));
            return;
        }

        if (string.IsNullOrEmpty(intentId)) return; // strict: non-robot orders ignored

        // ORPHAN DETECTION: Order update for protective order whose intent is already completed
        var isProtective = !string.IsNullOrEmpty(encodedTag) &&
            (encodedTag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase) || encodedTag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase));
        if (isProtective && _intentMap.TryGetValue(intentId, out var orderUpdateIntent) &&
            _executionJournal.IsIntentCompleted(intentId, orderUpdateIntent.TradingDate ?? "", orderUpdateIntent.Stream ?? ""))
        {
            _log.Write(RobotEvents.EngineBase(utcNow, orderUpdateIntent.TradingDate ?? "", "COMPLETED_INTENT_ORDER_UPDATE", "ENGINE", new
            {
                error = "Order update received for protective order whose intent is already TradeCompleted",
                intent_id = intentId,
                instrument = instrumentTrace,
                broker_order_id = order.OrderId,
                order_state = orderState.ToString(),
                stream = orderUpdateIntent.Stream,
                action = "ORPHAN_DETECTED",
                note = "Protective order for completed intent - route to reconciliation"
            }));
        }

        var legKey = parsedTag.Leg == "STOP" || parsedTag.Leg == "TARGET" ? parsedTag.Leg : null;
        OrderInfo? orderInfo;
        var foundOrderInfo = legKey != null
            ? _orderMap.TryGetValue($"{intentId}:{legKey}", out orderInfo)
            : _orderMap.TryGetValue(intentId, out orderInfo);
        if (!foundOrderInfo && legKey == null)
            foundOrderInfo = _orderMap.TryGetValue(intentId, out orderInfo);
        if (!foundOrderInfo || orderInfo == null)
        {
            // CRITICAL FIX: Handle execution updates for untracked orders gracefully
            // This can happen if:
            // 1. Order was rejected before being tracked
            // 2. Order tracking failed but execution update arrived
            // 3. Multiple execution updates for same order (race condition)
            // Log as INFO (not WARN) since this is often expected for rejected orders
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, order.Instrument?.MasterInstrument?.Name ?? "", "EXECUTION_UPDATE_UNKNOWN_ORDER",
                new 
                { 
                    error = "Order not found in tracking map", 
                    broker_order_id = order.OrderId, 
                    tag = encodedTag,
                    order_state = orderState.ToString(),
                    note = "Execution update received for untracked order - may indicate order was rejected before tracking or tracking race condition",
                    severity = "INFO" // Not an error - often expected for rejected orders
                }));
            return;
        }

        _onMismatchExecutionTrigger?.Invoke(orderInfo.Instrument.Trim(), utcNow, new MismatchExecutionTriggerDetails
        {
            IntentId = intentId,
            FillDelta = 0,
            SuppressHardJournalIntegrityActions = orderState == OrderState.Filled
        });

        // Update journal based on order state
        if (orderState == OrderState.Accepted)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_ACKNOWLEDGED",
                new { broker_order_id = order.OrderId, order_type = orderInfo.OrderType }));
            
            // Mark protective orders as acknowledged for watchdog tracking
            if (orderInfo.OrderType == "STOP")
            {
                orderInfo.ProtectiveStopAcknowledged = true;
            }
            else if (orderInfo.OrderType == "TARGET")
            {
                orderInfo.ProtectiveTargetAcknowledged = true;
            }
        }
        else if (orderState == OrderState.Rejected)
        {
            // CRITICAL FIX: NinjaTrader OrderEventArgs doesn't have ErrorMessage property
            // Error information comes from Order object properties and OrderEventArgs.ErrorCode
            string errorMsg = "Order rejected";
            string? errorCode = null;
            string? comment = null;
            Dictionary<string, object> errorDetails = new();
            
            try
            {
                // CRITICAL FIX: NinjaTrader OrderEventArgs doesn't have ErrorMessage property
                // Error information comes from Order object properties and OrderEventArgs.ErrorCode
                dynamic dynOrderUpdate = orderUpdate;
                
                // Try to get ErrorCode from OrderEventArgs (this property exists)
                try 
                { 
                    var errCode = dynOrderUpdate.ErrorCode;
                    if (errCode != null) 
                    {
                        errorCode = errCode.ToString();
                        // ErrorCode enum often contains descriptive error names
                        errorMsg = $"Order rejected (ErrorCode: {errorCode})";
                    }
                } 
                catch { }
                
                // Try to get Comment from OrderEventArgs
                try 
                { 
                    comment = (string?)dynOrderUpdate.Comment;
                    if (!string.IsNullOrEmpty(comment))
                    {
                        errorMsg = $"Order rejected: {comment}";
                    }
                } 
                catch { }
                
                // Get error message from Order object properties using dynamic typing
                // NinjaTrader Order may have error info in Name or other properties
                try
                {
                    dynamic dynOrder = order;
                    
                    // Try Order.Name (sometimes contains error info)
                    try
                    {
                        var orderName = dynOrder.Name as string;
                        if (!string.IsNullOrEmpty(orderName) && orderName.IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            errorMsg = $"Order rejected: {orderName}";
                        }
                    }
                    catch { }
                    
                    // Try to get Comment from Order using dynamic (may not exist)
                    try
                    {
                        var orderComment = dynOrder.Comment as string;
                        if (!string.IsNullOrEmpty(orderComment))
                        {
                            errorMsg = $"Order rejected: {orderComment}";
                        }
                    }
                    catch { }
                    
                    // If we have ErrorCode but no message, use ErrorCode
                    if (!string.IsNullOrEmpty(errorCode) && errorMsg == "Order rejected")
                    {
                        errorMsg = $"Order rejected (ErrorCode: {errorCode})";
                    }
                }
                catch { }
                
                // Log all available properties from OrderEventArgs for debugging
                try
                {
                    var props = new List<string>();
                    foreach (var prop in dynOrderUpdate.GetType().GetProperties())
                    {
                        try
                        {
                            var val = prop.GetValue(dynOrderUpdate);
                            if (val != null)
                            {
                                props.Add($"{prop.Name}={val}");
                                errorDetails[$"orderUpdate_{prop.Name}"] = val?.ToString() ?? "null";
                            }
                        }
                        catch { }
                    }
                    errorDetails["available_properties"] = string.Join(", ", props);
                }
                catch { }
            }
            catch (Exception ex)
            {
                errorDetails["extraction_exception"] = ex.Message;
                errorDetails["extraction_stack"] = ex.StackTrace ?? "";
                
                // Final fallback: try to get error info from Order using dynamic typing
                try
                {
                    dynamic dynOrder = order;
                    
                    // Try Order.Comment (may not exist, so use dynamic)
                    try
                    {
                        var orderComment = dynOrder.Comment as string;
                        if (!string.IsNullOrEmpty(orderComment))
                        {
                            errorMsg = $"Order rejected: {orderComment}";
                        }
                        else
                        {
                            errorMsg = $"Order rejected (error extraction failed: {ex.Message})";
                        }
                    }
                    catch
                    {
                        errorMsg = $"Order rejected (error extraction failed: {ex.Message})";
                    }
                }
                catch (Exception ex2)
                {
                    errorMsg = $"Order rejected (unable to extract error details: {ex.Message})";
                    errorDetails["fallback_exception"] = ex2.Message;
                }
            }
            
            // Add order details for debugging
            errorDetails["order_id"] = order.OrderId ?? "null";
            errorDetails["order_instrument"] = order.Instrument?.MasterInstrument?.Name ?? "null";
            errorDetails["order_state"] = orderState.ToString();
            try { errorDetails["order_action"] = order.OrderAction.ToString(); } catch { errorDetails["order_action"] = "null"; }
            try { errorDetails["order_type"] = order.OrderType.ToString(); } catch { errorDetails["order_type"] = "null"; }
            try { errorDetails["order_quantity"] = order.Quantity.ToString(); } catch { errorDetails["order_quantity"] = "null"; }
            try { errorDetails["order_limit_price"] = order.LimitPrice.ToString(); } catch { errorDetails["order_limit_price"] = "null"; }
            try { errorDetails["order_stop_price"] = order.StopPrice.ToString(); } catch { errorDetails["order_stop_price"] = "null"; }
            
            var fullErrorMsg = errorMsg;
            if (!string.IsNullOrEmpty(errorCode)) fullErrorMsg += $" (ErrorCode: {errorCode})";
            if (!string.IsNullOrEmpty(comment)) fullErrorMsg += $" (Comment: {comment})";
            
            var (tradingDate11, stream11, _, _, _, _, _) = GetIntentInfo(intentId);
            _executionJournal.RecordRejection(intentId, tradingDate11, stream11, $"ORDER_REJECTED: {fullErrorMsg}", utcNow, 
                orderType: orderInfo.OrderType, rejectedPrice: orderInfo.Price, rejectedQuantity: orderInfo.Quantity);
            orderInfo.State = "REJECTED";
            
            // CRITICAL FIX: If protective order is rejected, trigger fail-closed pathway
            // Order submission success != order acceptance - broker can reject orders after submission
            bool isProtectiveOrder = orderInfo.OrderType == "STOP" || orderInfo.OrderType == "TARGET";
            if (isProtectiveOrder)
            {
                // Protective order rejected - position is unprotected
                // Trigger same fail-closed pathway as submission failure
                var failureReason = $"Protective {orderInfo.OrderType} order rejected by broker: {fullErrorMsg}";
                
                // Get intent to find stream
                string? stream = null;
                if (_intentMap.TryGetValue(intentId, out var intent))
                {
                    stream = intent.Stream;
                    
                    // Notify coordinator of protective failure
                    _coordinator?.OnProtectiveFailure(intentId, stream, utcNow);
                    
                    // Flatten position immediately with retry logic
                    var flattenResult = FlattenWithRetry(intentId, orderInfo.Instrument, utcNow);
                    
                    // Stand down stream
                    _standDownStreamCallback?.Invoke(stream, utcNow, $"PROTECTIVE_ORDER_REJECTED: {failureReason}");
                    
                    // Persist incident record
                    PersistProtectiveFailureIncident(intentId, intent,
                        OrderSubmissionResult.FailureResult(fullErrorMsg, utcNow), // Stop/Target result
                        null, // Other leg (only one leg rejected)
                        flattenResult, utcNow);
                    
                    // Raise high-priority alert
                    var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                    if (notificationService != null)
                    {
                        var title = $"CRITICAL: Protective Order Rejected - {orderInfo.Instrument}";
                        var message = $"Protective {orderInfo.OrderType} order rejected by broker. Position flattened. Stream: {stream}, Intent: {intentId}. Error: {fullErrorMsg}";
                        notificationService.EnqueueNotification($"PROTECTIVE_REJECTED:{intentId}", title, message, priority: 2); // Emergency priority
                    }
                    
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "PROTECTIVE_ORDER_REJECTED_FLATTENED",
                        new
                        {
                            intent_id = intentId,
                            stream = stream,
                            instrument = orderInfo.Instrument,
                            order_type = orderInfo.OrderType,
                            broker_order_id = order.OrderId,
                            error = fullErrorMsg,
                            error_code = errorCode,
                            comment = comment,
                            flatten_success = flattenResult.Success,
                            flatten_error = flattenResult.ErrorMessage,
                            failure_reason = failureReason,
                            note = "Position flattened due to protective order rejection by broker (fail-closed behavior)"
                        }));
                }
                else
                {
                    // Intent not found - log error but still log rejection
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "PROTECTIVE_ORDER_REJECTED_INTENT_NOT_FOUND",
                        new
                        {
                            intent_id = intentId,
                            instrument = orderInfo.Instrument,
                            order_type = orderInfo.OrderType,
                            broker_order_id = order.OrderId,
                            error = fullErrorMsg,
                            note = "Protective order rejected but intent not found - cannot flatten (orphan rejection)"
                        }));
                    
                    // Still log the rejection
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_REJECTED",
                        new 
                        { 
                            broker_order_id = order.OrderId, 
                            error = fullErrorMsg,
                            error_code = errorCode,
                            comment = comment,
                            error_details = errorDetails,
                            order_type = orderInfo.OrderType,
                            note = "Protective order rejected but intent not found - manual intervention may be required"
                        }));
                }
            }
            else
            {
                // Non-protective order rejected - log only
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_REJECTED",
                    new 
                    { 
                        broker_order_id = order.OrderId, 
                        error = fullErrorMsg,
                        error_code = errorCode,
                        comment = comment,
                        error_details = errorDetails,
                        order_type = orderInfo.OrderType,
                        note = "Non-protective order rejected - no flatten required"
                    }));
            }
        }
        else if (orderState == OrderState.Cancelled)
        {
            orderInfo.State = "CANCELLED";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_CANCELLED",
                new { broker_order_id = order.OrderId }));
            if (orderInfo.IsEntryOrder && orderInfo.FilledQuantity == 0)
            {
                var (tdCx, streamCx, _, _, _, _, _) = GetIntentInfo(intentId);
                if (!string.IsNullOrWhiteSpace(tdCx) && !string.IsNullOrWhiteSpace(streamCx))
                    _executionJournal.RecordCancelledUnfilledEntry(intentId, tdCx, streamCx, utcNow);
            }
        }
    }

    /// <summary>
    /// Intent context structure for resolved intent information.
    /// </summary>
    private struct IntentContext
    {
        public string TradingDate;
        public string Stream;
        public string Direction;
        public decimal ContractMultiplier;
        public string ExecutionInstrument;
        public string CanonicalInstrument;
        public string Tag;
        public string IntentId;
    }
    
    /// <summary>
    /// Resolve intent context or fail-closed.
    /// Returns true if context resolved successfully, false if orphan fill detected.
    /// </summary>
    private bool ResolveIntentContextOrFailClosed(
        string intentId, 
        string encodedTag, 
        string orderType, 
        string instrument, 
        decimal fillPrice, 
        int fillQuantity, 
        DateTimeOffset utcNow, 
        out IntentContext context,
        out bool flattenAttempted,
        out bool flattenSucceeded,
        out string? flattenError)
    {
        context = default;
        flattenAttempted = false;
        flattenSucceeded = false;
        flattenError = null;
        
        if (!_intentMap.TryGetValue(intentId, out var intent))
        {
            LogOrphanFill(intentId, encodedTag, orderType, instrument, fillPrice, fillQuantity, 
                utcNow, reason: "INTENT_NOT_FOUND");
            RecordOrphanFillIfEnabled(instrument, "", intentId, fillPrice, fillQuantity,
                utcNow, OrphanReason.UnknownOrder, SlotDirection.Long);
            
            // Stand down stream if known, otherwise block instrument
            // Try to get stream from orderInfo if available, but likely unknown
            _standDownStreamCallback?.Invoke("", utcNow, $"ORPHAN_FILL:INTENT_NOT_FOUND:{intentId}");
            
            // CRITICAL FIX: Flatten position immediately (fail-closed)
            // Entry fill happened but intent not found - position exists but is unprotected
            // Must flatten to prevent unprotected position accumulation
            flattenAttempted = true;
            try
            {
                var flattenResult = Flatten(intentId, instrument, utcNow);
                flattenSucceeded = flattenResult.Success;
                flattenError = flattenResult.ErrorMessage;
                
                _log.Write(RobotEvents.EngineBase(utcNow, "", "ORPHAN_FILL_CRITICAL", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        tag = encodedTag,
                        order_type = orderType,
                        instrument = instrument,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        reason = "INTENT_NOT_FOUND",
                        action_taken = "EXECUTION_BLOCKED_AND_FLATTENED",
                        flatten_success = flattenResult.Success,
                        flatten_error = flattenResult.ErrorMessage,
                        note = "Intent not found in _intentMap - orphan fill logged, position flattened (fail-closed)"
                    }));
                
                // Raise high-priority alert if flatten succeeded
                if (flattenResult.Success)
                {
                    var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                    if (notificationService != null)
                    {
                        var title = $"CRITICAL: Intent Not Found - Position Flattened - {instrument}";
                        var message = $"Entry fill occurred but intent not found in map. Position flattened automatically. Intent: {intentId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}";
                        notificationService.EnqueueNotification($"INTENT_NOT_FOUND:{intentId}", title, message, priority: 2); // Emergency priority
                    }
                }
                else
                {
                    // Flatten failed - critical alert
                    var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                    if (notificationService != null)
                    {
                        var title = $"CRITICAL: Intent Not Found - Flatten FAILED - {instrument}";
                        var message = $"Entry fill occurred but intent not found. Flatten operation FAILED - MANUAL INTERVENTION REQUIRED. Intent: {intentId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}, Error: {flattenResult.ErrorMessage}";
                        notificationService.EnqueueNotification($"INTENT_NOT_FOUND_FLATTEN_FAILED:{intentId}", title, message, priority: 3); // Highest priority
                    }
                }
            }
            catch (Exception ex)
            {
                flattenSucceeded = false;
                flattenError = ex.Message;
                _log.Write(RobotEvents.EngineBase(utcNow, "", "ORPHAN_FILL_FLATTEN_EXCEPTION", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        tag = encodedTag,
                        order_type = orderType,
                        instrument = instrument,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        reason = "INTENT_NOT_FOUND",
                        error = ex.Message,
                        exception_type = ex.GetType().Name,
                        action_taken = "EXECUTION_BLOCKED_FLATTEN_EXCEPTION",
                        note = "CRITICAL: Intent not found and flatten operation threw exception - MANUAL INTERVENTION REQUIRED"
                    }));
                
                // Critical alert for flatten exception
                var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                if (notificationService != null)
                {
                    var title = $"CRITICAL: Intent Not Found - Flatten EXCEPTION - {instrument}";
                    var message = $"Entry fill occurred but intent not found. Flatten operation threw exception - MANUAL INTERVENTION REQUIRED. Intent: {intentId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}, Exception: {ex.Message}";
                    notificationService.EnqueueNotification($"INTENT_NOT_FOUND_FLATTEN_EXCEPTION:{intentId}", title, message, priority: 3); // Highest priority
                }
            }
            
            return false;
        }
        
        var tradingDate = intent.TradingDate ?? "";
        var stream = intent.Stream ?? "";
        var direction = intent.Direction ?? "";
        
        // Validate required fields
        if (string.IsNullOrWhiteSpace(tradingDate))
        {
            LogOrphanFill(intentId, encodedTag, orderType, instrument, fillPrice, fillQuantity, 
                utcNow, stream, "MISSING_TRADING_DATE");
            RecordOrphanFillIfEnabled(instrument, order?.OrderId?.ToString() ?? "", intentId,
                fillPrice, fillQuantity, utcNow, OrphanReason.IntentLostAfterContext,
                ParseSlotDirection(direction), OrphanActionTaken.NoAction);
            _standDownStreamCallback?.Invoke(stream, utcNow, $"ORPHAN_FILL:MISSING_TRADING_DATE:{intentId}");
            return false;
        }
        
        if (string.IsNullOrWhiteSpace(stream))
        {
            LogOrphanFill(intentId, encodedTag, orderType, instrument, fillPrice, fillQuantity, 
                utcNow, reason: "MISSING_STREAM");
            RecordOrphanFillIfEnabled(instrument, order?.OrderId?.ToString() ?? "", intentId,
                fillPrice, fillQuantity, utcNow, OrphanReason.IntentLostAfterContext,
                ParseSlotDirection(direction), OrphanActionTaken.NoAction);
            _standDownStreamCallback?.Invoke("", utcNow, $"ORPHAN_FILL:MISSING_STREAM:{intentId}");
            return false;
        }
        
        if (string.IsNullOrWhiteSpace(direction))
        {
            LogOrphanFill(intentId, encodedTag, orderType, instrument, fillPrice, fillQuantity, 
                utcNow, stream, "MISSING_DIRECTION");
            RecordOrphanFillIfEnabled(instrument, order?.OrderId?.ToString() ?? "", intentId,
                fillPrice, fillQuantity, utcNow, OrphanReason.IntentLostAfterContext,
                SlotDirection.Long, OrphanActionTaken.NoAction);
            _standDownStreamCallback?.Invoke(stream, utcNow, $"ORPHAN_FILL:MISSING_DIRECTION:{intentId}");
            return false;
        }
        
        // Derive contract multiplier
        decimal? contractMultiplier = null;
        if (_ntInstrument is Instrument ntInst)
        {
            contractMultiplier = (decimal)ntInst.MasterInstrument.PointValue;
        }
        
        if (!contractMultiplier.HasValue)
        {
            LogOrphanFill(intentId, encodedTag, orderType, instrument, fillPrice, fillQuantity, 
                utcNow, stream, "MISSING_MULTIPLIER");
            RecordOrphanFillIfEnabled(instrument, order?.OrderId?.ToString() ?? "", intentId,
                fillPrice, fillQuantity, utcNow, OrphanReason.ContextResolutionFailed,
                ParseSlotDirection(direction), OrphanActionTaken.NoAction);
            _standDownStreamCallback?.Invoke(stream, utcNow, $"ORPHAN_FILL:MISSING_MULTIPLIER:{intentId}");
            return false;
        }
        
        context = new IntentContext
        {
            TradingDate = tradingDate,
            Stream = stream,
            Direction = direction,
            ContractMultiplier = contractMultiplier.Value,
            ExecutionInstrument = intent.Instrument ?? "",
            CanonicalInstrument = intent.Instrument ?? "", // TODO: derive canonical if different from execution
            Tag = encodedTag,
            IntentId = intentId
        };
        
        return true;
    }
    
    /// <summary>
    /// Log orphan fill to JSONL file.
    /// </summary>
    private void LogOrphanFill(
        string intentId, 
        string tag, 
        string orderType, 
        string instrument,
        decimal fillPrice, 
        int fillQuantity, 
        DateTimeOffset utcNow, 
        string? stream = null,
        string reason = "UNKNOWN")
    {
        try
        {
            var orphanDir = Path.Combine(_stateRoot, "data", "execution_incidents", "orphan_fills");
            Directory.CreateDirectory(orphanDir);
            var orphanFile = Path.Combine(orphanDir, $"orphan_fills_{utcNow:yyyy-MM-dd}.jsonl");
            
            var orphanRecord = new
            {
                event_type = "ORPHAN_FILL",
                timestamp_utc = utcNow.ToString("o"),
                intent_id = intentId,
                tag = tag,
                order_type = orderType,
                instrument = instrument,
                fill_price = fillPrice,
                fill_quantity = fillQuantity,
                stream = stream ?? "UNKNOWN",
                reason = reason,
                action_taken = "EXECUTION_BLOCKED"
            };
            
            var json = QTSW2.Robot.Core.JsonUtil.Serialize(orphanRecord);
            File.AppendAllText(orphanFile, json + "\n");
            
            // Also log as CRITICAL event
            _log.Write(RobotEvents.EngineBase(utcNow, stream ?? "", "ORPHAN_FILL_CRITICAL", "ENGINE",
                new
                {
                    intent_id = intentId,
                    tag = tag,
                    order_type = orderType,
                    instrument = instrument,
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    stream = stream ?? "UNKNOWN",
                    reason = reason,
                    orphan_file = orphanFile,
                    action_taken = "EXECUTION_BLOCKED"
                }));
        }
        catch (Exception ex)
        {
            // Log error but don't fail execution (orphan logging failure shouldn't block)
            _log.Write(RobotEvents.EngineBase(utcNow, stream ?? "", "ORPHAN_FILL_LOG_ERROR", "ENGINE",
                new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    intent_id = intentId,
                    note = "Failed to write orphan fill log - continuing"
                }));
        }
    }

}

#endif
