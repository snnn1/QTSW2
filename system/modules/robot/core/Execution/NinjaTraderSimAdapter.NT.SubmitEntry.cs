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
    /// STEP 1: Verify SIM account using real NT API.
    /// </summary>
    private void VerifySimAccountReal()
    {
        if (_ntAccount == null)
        {
            var error = "NT account is null - cannot verify non-live account";
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "NOT_SIM_ACCOUNT", error }));
            throw new InvalidOperationException(error);
        }

        var account = _ntAccount as Account;
        if (account == null)
        {
            var error = "NT account type mismatch";
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "ACCOUNT_TYPE_MISMATCH", error }));
            throw new InvalidOperationException(error);
        }

        if (!NtNonLiveAccountValidation.IsAllowedAlgorithmicPaperAccount(account))
        {
            var error = $"Account '{account.Name}' is not a simulation/playback (non-live) account - aborting execution";
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "NOT_SIM_ACCOUNT", account_name = account.Name, error }));
            throw new InvalidOperationException(error);
        }

        _simAccountVerified = true;
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "SIM_ACCOUNT_VERIFIED", state: "ENGINE",
            new { account_name = account.Name, note = "Non-live account verification passed (Simulation/Playback or Sim*/Playback* name)" }));
    }

    /// <summary>
    /// STEP 2: Submit entry order using real NT API.
    /// </summary>
    private OrderSubmissionResult SubmitEntryOrderReal(
        string intentId,
        string instrument,
        string direction,
        decimal? entryPrice,
        int quantity,
        string? entryOrderType,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        if (_ntAccount == null)
        {
            var error = "NT context not set - cannot submit orders";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        var account = _ntAccount as Account;
        if (account == null)
        {
            var error = "NT account type mismatch";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        // EXECUTION AUDIT FIX 1: Prevent multiple entry orders for same intent
        // CRITICAL: Check if entry order already exists for this intent
        if (_orderMap.TryGetValue(intentId, out var existingOrder))
        {
            // Check if existing order is an entry order and still active
            if (existingOrder.IsEntryOrder && 
                (existingOrder.State == "SUBMITTED" || 
                 existingOrder.State == "ACCEPTED" || 
                 existingOrder.State == "WORKING"))
            {
                var error = $"Entry order already exists for intent {intentId}. " +
                           $"Existing order state: {existingOrder.State}, " +
                           $"Broker Order ID: {existingOrder.OrderId}";
                
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_ORDER_DUPLICATE_BLOCKED", new
                {
                    intent_id = intentId,
                    existing_order_id = existingOrder.OrderId,
                    existing_order_state = existingOrder.State,
                    error = error,
                    note = "Multiple entry orders prevented - only one entry order allowed per intent"
                }));
                
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }
            
            // If existing order is filled, allow new entry (shouldn't happen but handle gracefully)
            if (existingOrder.State == "FILLED")
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_ORDER_ALREADY_FILLED", new
                {
                    intent_id = intentId,
                    existing_order_id = existingOrder.OrderId,
                    warning = "Entry order already filled - new entry order submission blocked",
                    note = "This should not happen - entry orders should only be submitted once per intent"
                }));
                
                return OrderSubmissionResult.FailureResult("Entry order already filled for this intent", utcNow);
            }
        }

        // Fix 1: Hard guard - check executionInstrument matches strategy's Instrument exactly
        if (!IsStrategyExecutionInstrument(instrument))
        {
            var error = $"Execution instrument '{instrument}' does not match strategy's Instrument. " +
                       $"Strategy Instrument: {(_ntInstrument as Instrument)?.FullName ?? "NULL"}. " +
                       $"Orders can only be placed on the strategy's enabled Instrument.";
            
            // OPERATIONAL HYGIENE: Rate-limit INSTRUMENT_MISMATCH logging to prevent log flooding
            // Log once per hour per instrument to avoid masking other signals
            var shouldLog = !_lastInstrumentMismatchLogUtc.TryGetValue(instrument, out var lastLogUtc) ||
                           (utcNow - lastLogUtc).TotalMinutes >= INSTRUMENT_MISMATCH_RATE_LIMIT_MINUTES;
            
            if (shouldLog)
            {
                _lastInstrumentMismatchLogUtc[instrument] = utcNow;
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_BLOCKED", new
                {
                    error,
                    requested_instrument = instrument,
                    strategy_instrument = (_ntInstrument as Instrument)?.FullName ?? "NULL",
                    reason = "INSTRUMENT_MISMATCH",
                    rate_limited = true,
                    note = $"INSTRUMENT_MISMATCH logging rate-limited to once per {INSTRUMENT_MISMATCH_RATE_LIMIT_MINUTES} minute(s) per instrument to prevent log flooding"
                }));
            }
            
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        // Fix 3: Anchor on Instrument instance - use strategy's Instrument directly
        var ntInstrument = _ntInstrument as Instrument;
        if (ntInstrument == null)
        {
            var error = "Strategy Instrument instance not available - cannot submit orders";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_BLOCKED", new
            {
                error,
                reason = "INSTRUMENT_INSTANCE_NULL"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            // STEP 2: Create NT Order using real API
            var orderAction = direction == "Long" ? OrderAction.Buy : OrderAction.SellShort;
            
            // Determine order type: use entryOrderType if provided, otherwise infer from entryPrice
            OrderType orderType;
            double ntEntryPrice;
            
            if (entryOrderType == "STOP_MARKET")
            {
                // Breakout entry: use StopMarket order
                orderType = OrderType.StopMarket;
                if (!entryPrice.HasValue)
                {
                    var error = "StopMarket order requires entryPrice (stop trigger price)";
                    return OrderSubmissionResult.FailureResult(error, utcNow);
                }
                ntEntryPrice = (double)entryPrice.Value;
            }
            else if (entryOrderType == "MARKET" || !entryPrice.HasValue)
            {
                // Market order
                orderType = OrderType.Market;
                ntEntryPrice = 0.0;
            }
            else
            {
                // Default: Limit order (for immediate entries at lock)
                orderType = OrderType.Limit;
                ntEntryPrice = (double)entryPrice.Value;
            }
            
            // Pre-submission invariant check
            if (!_intentPolicy.TryGetValue(intentId, out var expectation))
            {
                // HARD BLOCK: expectation missing (fail-closed by default)
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, 
                    "ENTRY_SUBMIT_PRECHECK", new
                {
                    intent_id = intentId,
                    requested_quantity = quantity,
                    expected_quantity = (int?)null,
                    max_quantity = (int?)null,
                    cumulative_filled_qty = 0,
                    remaining_allowed_qty = (int?)null,
                    chart_trader_quantity = (int?)null,
                    allowed = false,
                    reason = "Intent policy expectation missing"
                }));
                return OrderSubmissionResult.FailureResult(
                    "Pre-submission check failed: intent policy expectation missing", utcNow);
            }

            var expectedQty = expectation.ExpectedQuantity;
            var maxQty = expectation.MaxQuantity;
            var filledQty = _orderMap.TryGetValue(intentId, out var existingOrderInfo) ? existingOrderInfo.FilledQuantity : 0;
            var remainingAllowed = expectedQty - filledQty;

            // Get Chart Trader quantity if accessible
            int? chartTraderQty = null;
            // Note: Strategy reference not available in adapter - would need to be passed if needed

            // HARD BLOCK rules
            bool hardBlock = false;
            string? blockReason = null;

            if (quantity <= 0)
            {
                hardBlock = true;
                blockReason = $"Invalid quantity: {quantity}";
            }
            else if (filledQty > expectedQty)
            {
                hardBlock = true;
                blockReason = $"Already overfilled: filled={filledQty}, expected={expectedQty}";
            }
            else if (quantity > remainingAllowed)
            {
                hardBlock = true;
                blockReason = $"Quantity exceeds remaining allowed: {quantity} > {remainingAllowed}";
            }
            else if (quantity > maxQty)
            {
                hardBlock = true;
                blockReason = $"Quantity exceeds max: {quantity} > {maxQty}";
            }

            if (hardBlock)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, 
                    "ENTRY_SUBMIT_PRECHECK", new
                {
                    intent_id = intentId,
                    requested_quantity = quantity,
                    expected_quantity = expectedQty,
                    max_quantity = maxQty,
                    cumulative_filled_qty = filledQty,
                    remaining_allowed_qty = remainingAllowed,
                    chart_trader_quantity = chartTraderQty,
                    allowed = false,
                    reason = blockReason
                }));
                return OrderSubmissionResult.FailureResult($"Pre-submission check failed: {blockReason}", utcNow);
            }

            // WARN but allow if non-ideal (shouldn't happen)
            bool warn = false;
            string? warnReason = null;

            if (quantity != expectedQty && filledQty == 0 && quantity <= expectedQty)
            {
                warn = true;
                warnReason = $"Quantity mismatch: requested={quantity}, expected={expectedQty}";
            }

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, 
                "ENTRY_SUBMIT_PRECHECK", new
            {
                intent_id = intentId,
                requested_quantity = quantity,
                expected_quantity = expectedQty,
                max_quantity = maxQty,
                cumulative_filled_qty = filledQty,
                remaining_allowed_qty = remainingAllowed,
                chart_trader_quantity = chartTraderQty,
                allowed = true,
                warning = warn ? warnReason : null
            }));
            
            // Real NT API: CreateOrder
            // Use dynamic to handle different CreateOrder signatures
            // CRITICAL: Catch RuntimeBinderException specifically for dynamic binding failures
            dynamic dynAccount = account;
            Order order = null!; // Initialize to satisfy compiler, will be assigned before use
            
            // StopMarket orders: Use official NT8 CreateOrder factory method with full parameter list
            if (orderType == OrderType.StopMarket)
            {
                // Runtime safety checks BEFORE CreateOrder
                if (!_ntContextSet)
                {
                    var error = "NT context not set - cannot create StopMarket order";
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                    {
                        error,
                        order_type = "StopMarket",
                        order_action = orderAction.ToString(),
                        quantity = quantity,
                        stop_price = ntEntryPrice,
                        instrument = instrument,
                        intent_id = intentId,
                        account = "SIM"
                    }));
                    return OrderSubmissionResult.FailureResult(error, utcNow);
                }
                
                if (account == null)
                {
                    var error = "Account is null - cannot create StopMarket order";
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                    {
                        error,
                        order_type = "StopMarket",
                        order_action = orderAction.ToString(),
                        quantity = quantity,
                        stop_price = ntEntryPrice,
                        instrument = instrument,
                        intent_id = intentId,
                        account = "SIM"
                    }));
                    return OrderSubmissionResult.FailureResult(error, utcNow);
                }
                
                if (ntInstrument == null)
                {
                    var error = "Instrument is null - cannot create StopMarket order";
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                    {
                        error,
                        order_type = "StopMarket",
                        order_action = orderAction.ToString(),
                        quantity = quantity,
                        stop_price = ntEntryPrice,
                        instrument = instrument,
                        intent_id = intentId,
                        account = "SIM"
                    }));
                    return OrderSubmissionResult.FailureResult(error, utcNow);
                }
                
                // Fix 2: Quantity assertion (fail-fast) - throw immediately for invalid quantity
                if (quantity <= 0)
                {
                    var error = $"Order quantity unresolved: {quantity}";
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                    {
                        error,
                        order_type = "StopMarket",
                        order_action = orderAction.ToString(),
                        quantity = quantity,
                        stop_price = ntEntryPrice,
                        instrument = instrument,
                        intent_id = intentId,
                        account = "SIM"
                    }));
                    throw new InvalidOperationException(error);
                }
                
                if (ntEntryPrice <= 0)
                {
                    var error = $"Invalid stop price: {ntEntryPrice} (must be > 0)";
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                    {
                        error,
                        order_type = "StopMarket",
                        order_action = orderAction.ToString(),
                        quantity = quantity,
                        stop_price = ntEntryPrice,
                        instrument = instrument,
                        intent_id = intentId,
                        account = "SIM"
                    }));
                    return OrderSubmissionResult.FailureResult(error, utcNow);
                }

                var relationshipFailure = TryBlockInvalidStopMarketRelationship(
                    intentId, instrument, direction, (decimal)ntEntryPrice, quantity, "ENTRY_STOP", utcNow,
                    out var convertStopEntryToMarket);
                if (relationshipFailure != null)
                    return relationshipFailure;
                if (convertStopEntryToMarket)
                    return SubmitEntryOrderCore(intentId, instrument, direction, null, quantity, "MARKET", ocoGroup, utcNow,
                        "SUBMIT_ENTRY_STOP");
                
                // Create order using official NT8 CreateOrder factory method
                try
                {
                    order = account.CreateOrder(
                        ntInstrument,                           // Instrument
                        orderAction,                            // OrderAction
                        OrderType.StopMarket,                   // OrderType
                        OrderEntry.Manual,                      // OrderEntry
                        TimeInForce.Day,                        // TimeInForce
                        quantity,                               // Quantity
                        0.0,                                    // LimitPrice (0 for StopMarket)
                        ntEntryPrice,                           // StopPrice
                        ocoGroup,                               // Oco (for MARKET+STOP bracket when breakout crossed)
                        RobotOrderIds.EncodeTag(intentId),      // OrderName
                        DateTime.MinValue,                      // Gtd
                        null                                    // CustomOrder
                    );
                    
                    // Log success before Submit
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATED_STOPMARKET", new
                    {
                        order_name = RobotOrderIds.EncodeTag(intentId),
                        stop_price = ntEntryPrice,
                        quantity = quantity,
                        order_action = orderAction.ToString(),
                        instrument = instrument
                    }));
                    
                    // Set order tag
                    SetOrderTag(order, RobotOrderIds.EncodeTag(intentId));
                }
                catch (Exception ex)
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                    {
                        error = $"Failed to create StopMarket order: {ex.Message}",
                        order_type = "StopMarket",
                        order_action = orderAction.ToString(),
                        quantity = quantity,
                        stop_price = ntEntryPrice,
                        instrument = instrument,
                        intent_id = intentId,
                        account = "SIM"
                    }));
                    return OrderSubmissionResult.FailureResult($"Failed to create StopMarket order: {ex.Message}", utcNow);
                }
            }
            else
            {
                // Market/Limit orders: Try 5-argument version first
                // Attempt 1: 5-argument version (instrument, action, type, quantity, price)
                // For Market orders, pass 0 as price (NT will ignore it)
                bool orderCreated = false;
                string? lastError = null;
                
                if (!orderCreated)
                {
                    try
                    {
                        double priceForOrder = orderType == OrderType.Market ? 0 : ntEntryPrice;
                        order = dynAccount.CreateOrder(ntInstrument, orderAction, orderType, quantity, priceForOrder);
                        orderCreated = true;
                    }
                    catch (RuntimeBinderException ex)
                    {
                        lastError = ex.Message;
                        // 5-argument version doesn't exist, try next
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        // Other exception, try next signature
                    }
                }
                
                // Attempt 2: 3-argument version (instrument, action, quantity) - then set properties
                if (!orderCreated)
                {
                    try
                    {
                        order = dynAccount.CreateOrder(ntInstrument, orderAction, quantity);
                        dynamic dynOrder = order;
                        // Set order type and price via properties
                        dynOrder.OrderType = orderType;
                        if (orderType == OrderType.Limit)
                        {
                            dynOrder.LimitPrice = ntEntryPrice;
                        }
                        orderCreated = true;
                    }
                    catch (RuntimeBinderException ex)
                    {
                        lastError = ex.Message;
                        // 3-argument version doesn't exist, try next
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        // Other exception, try next signature
                    }
                }
                
                // Attempt 3: Try with different parameter order
                if (!orderCreated)
                {
                    try
                    {
                        // Some NT versions might use: CreateOrder(instrument, quantity, action, type, price)
                        order = dynAccount.CreateOrder(ntInstrument, quantity, orderAction, orderType, ntEntryPrice);
                        orderCreated = true;
                    }
                    catch (RuntimeBinderException ex)
                    {
                        lastError = ex.Message;
                        // This signature doesn't exist either
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                    }
                }
                
                // If all attempts failed, throw descriptive error
                if (!orderCreated)
                {
                    string errorMsg = $"Failed to create order: No compatible CreateOrder overload found. " +
                                  $"Tried signatures: (instrument, action, type, qty, price), (instrument, action, qty), " +
                                  $"(instrument, qty, action, type, price). OrderType={orderType}, Action={orderAction}, " +
                                  $"Quantity={quantity}, Price={ntEntryPrice}. Last error: {lastError}";
                    string[] attemptedSignatures = new[] { "(instrument, action, type, qty, price)", "(instrument, action, qty)", "(instrument, qty, action, type, price)" };
                    
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                    {
                        error = errorMsg,
                        order_type = orderType.ToString(),
                        order_action = orderAction.ToString(),
                        quantity = quantity,
                        entry_price = ntEntryPrice,
                        account = "SIM",
                        last_error = lastError,
                        attempted_signatures = attemptedSignatures
                    }));
                    throw new InvalidOperationException(errorMsg);
                }
            }
            
            // Order creation verification
            var verified = order.Quantity == quantity;
            if (!verified)
            {
                // EMERGENCY: Quantity mismatch
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, 
                    "ORDER_CREATED_VERIFICATION", new
                {
                    intent_id = intentId,
                    requested_quantity = quantity,
                    order_quantity = order.Quantity,
                    order_id = order.OrderId,
                    instrument = instrument,
                    verified = false
                }));
                
                // Trigger emergency handler (quantity mismatch, not overfill)
                TriggerQuantityEmergency(intentId, "QUANTITY_MISMATCH_EMERGENCY", utcNow, new Dictionary<string, object>
                {
                    { "requested_quantity", quantity },
                    { "order_quantity", order.Quantity },
                    { "reason", "Order creation quantity mismatch" }
                });
                
                return OrderSubmissionResult.FailureResult(
                    $"Order quantity mismatch: requested {quantity}, order has {order.Quantity}", utcNow);
            }
            else
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, 
                    "ORDER_CREATED_VERIFICATION", new
                {
                    intent_id = intentId,
                    requested_quantity = quantity,
                    order_quantity = order.Quantity,
                    order_id = order.OrderId,
                    instrument = instrument,
                    verified = true
                }));
            }
            
            // Set order tag/name (try Tag first, fallback to Name)
            var encodedTag = RobotOrderIds.EncodeTag(intentId);
            SetOrderTag(order, encodedTag); // Robot-owned envelope
            order.TimeInForce = TimeInForce.Day;
            
            // EXECUTION AUDIT FIX 2: Make tag verification failure fatal with retry logic
            // CRITICAL: Verify tag was set correctly - fail-closed if verification fails
            var verifyTag = GetOrderTag(order);
            if (verifyTag != encodedTag)
            {
                var error = $"Order tag verification failed - tag may not be set correctly. " +
                           $"Expected: {encodedTag}, Actual: {verifyTag ?? "NULL"}";
                
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_TAG_SET_FAILED_CRITICAL",
                    new
                    {
                        intent_id = intentId,
                        expected_tag = encodedTag,
                        actual_tag = verifyTag ?? "NULL",
                        broker_order_id = order.OrderId,
                        error = error,
                        action = "RETRYING_ORDER_CREATION",
                        note = "CRITICAL: Tag verification failed - retrying order creation with explicit tag setting"
                    }));
                
                // CRITICAL FIX: Retry order creation with explicit tag setting
                // Try setting tag again before giving up
                try
                {
                    SetOrderTag(order, encodedTag);
                    verifyTag = GetOrderTag(order);
                    
                    if (verifyTag != encodedTag)
                    {
                        // Still failed after retry - fail-closed
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_TAG_SET_FAILED_FATAL",
                            new
                            {
                                intent_id = intentId,
                                expected_tag = encodedTag,
                                actual_tag = verifyTag ?? "NULL",
                                broker_order_id = order.OrderId,
                                error = "Tag verification failed after retry - order creation aborted",
                                action = "ORDER_CREATION_ABORTED",
                                note = "CRITICAL: Cannot guarantee fill tracking - aborting order creation (fail-closed)"
                            }));
                        
                        // Remove from order map if already added
                        _orderMap.TryRemove(intentId, out _);
                        
                        return OrderSubmissionResult.FailureResult(
                            $"Order tag verification failed after retry: {error}. Order creation aborted (fail-closed).", 
                            utcNow);
                    }
                    else
                    {
                        // Retry succeeded - log success
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_TAG_SET_RETRY_SUCCEEDED",
                            new
                            {
                                intent_id = intentId,
                                tag = encodedTag,
                                broker_order_id = order.OrderId,
                                note = "Tag verification retry succeeded - order creation continuing"
                            }));
                    }
                }
                catch (Exception ex)
                {
                    // Retry failed with exception - fail-closed
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_TAG_SET_RETRY_EXCEPTION",
                        new
                        {
                            intent_id = intentId,
                            expected_tag = encodedTag,
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            action = "ORDER_CREATION_ABORTED",
                            note = "CRITICAL: Tag retry threw exception - aborting order creation (fail-closed)"
                        }));
                    
                    // Remove from order map if already added
                    _orderMap.TryRemove(intentId, out _);
                    
                    return OrderSubmissionResult.FailureResult(
                        $"Order tag verification retry failed: {ex.Message}. Order creation aborted (fail-closed).", 
                        utcNow);
                }
            }

            // Store order info for callback correlation
            var orderInfo = new OrderInfo
            {
                IntentId = intentId,
                Instrument = instrument,
                OrderId = order.OrderId, // Real NT order ID
                OrderType = "ENTRY",
                Direction = direction,
                Quantity = quantity,
                Price = entryPrice,
                State = "SUBMITTED",
                NTOrder = order, // Store NT order object
                IsEntryOrder = true,
                FilledQuantity = 0
            };
            
            // Copy policy expectation from _intentPolicy if available
            if (_intentPolicy.TryGetValue(intentId, out var expectationForOrder))
            {
                orderInfo.ExpectedQuantity = expectationForOrder.ExpectedQuantity;
                orderInfo.MaxQuantity = expectationForOrder.MaxQuantity;
                orderInfo.PolicySource = expectationForOrder.PolicySource;
                orderInfo.CanonicalInstrument = expectationForOrder.CanonicalInstrument;
                orderInfo.ExecutionInstrument = expectationForOrder.ExecutionInstrument;
            }
            else
            {
                // Log warning if expectation missing
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, 
                    "INTENT_POLICY_MISSING_AT_ORDER_CREATE", new
                {
                    intent_id = intentId,
                    instrument = instrument,
                    warning = "Order created but policy expectation not registered"
                }));
            }
            
            _orderMap[intentId] = orderInfo;
            // Registry/adoption scans run on wall clock; arm this bounded convergence window on the same clock.
            QuantExecutionControlStore.NotifyWorkingOrderSubmitTransition(instrument, 1, DateTimeOffset.UtcNow);

            // Real NT API: Submit order
            // Submit may return Order[] or void - use dynamic to handle both
            dynamic dynAccountSubmit = account;
            Order submitResult;
            try
            {
                object? result = dynAccountSubmit.Submit(new[] { order });
                if (result != null && result is Order[] resultArray && resultArray.Length > 0)
                {
                    submitResult = resultArray[0];
                }
                else
                {
                    submitResult = order;
                }
            }
            catch
            {
                // Submit returns void - use the order we created
                dynAccountSubmit.Submit(new[] { order });
                submitResult = order;
            }
            var acknowledgedAt = DateTimeOffset.UtcNow;

            if (submitResult.OrderState == OrderState.Rejected)
            {
                // Get error message using dynamic typing
                dynamic dynOrder = submitResult;
                string error = "Order rejected";
                try
                {
                    error = (string?)dynOrder.ErrorMessage ?? (string?)dynOrder.Error ?? "Order rejected";
                }
                catch
                {
                    try
                    {
                        error = (string?)dynOrder.Error ?? "Order rejected";
                    }
                    catch
                    {
                        error = "Order rejected";
                    }
                }
                var (tradingDate9, stream9, _, _, _, _, _) = GetIntentInfo(intentId);
                _executionJournal.RecordRejection(intentId, tradingDate9, stream9, $"ENTRY_SUBMIT_FAILED: {error}", utcNow, 
                    orderType: "ENTRY", rejectedPrice: entryPrice, rejectedQuantity: quantity);
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
                {
                    error,
                    broker_order_id = order.OrderId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }

            // Journal: ENTRY_SUBMITTED (store expected entry price for slippage calculation)
            var (tradingDate, stream, intentEntryPrice, intentStopPrice, intentTargetPrice, intentDirection, _) = GetIntentInfo(intentId);
            var intentBeTrigger = _intentMap.TryGetValue(intentId, out var submissionIntent)
                ? submissionIntent.BeTrigger
                : null;
            var finalEntryPrice = intentEntryPrice ?? entryPrice;
            var finalDirection = intentDirection ?? direction;
            
            _executionJournal.RecordSubmission(intentId, tradingDate, stream, instrument, "ENTRY", order.OrderId, acknowledgedAt, 
                expectedEntryPrice: entryPrice, entryPrice: finalEntryPrice, stopPrice: intentStopPrice, 
                targetPrice: intentTargetPrice, beTriggerPrice: intentBeTrigger, direction: finalDirection, ocoGroup: ocoGroup);

            _log.Write(RobotEvents.ExecutionBase(acknowledgedAt, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = order.OrderId,
                order_type = "ENTRY",
                direction,
                entry_price = entryPrice,
                entry_order_type = entryOrderType,
                quantity,
                account = "SIM",
                order_action = orderAction.ToString(),
                order_type_nt = orderType.ToString(),
                order_state = submitResult.OrderState.ToString()
            }));

            // Alias event for easier grepping (user-facing)
            _log.Write(RobotEvents.ExecutionBase(acknowledgedAt, intentId, instrument, "ORDER_SUBMITTED", new
            {
                broker_order_id = order.OrderId,
                order_type = "ENTRY",
                direction,
                entry_price = entryPrice,
                quantity,
                account = "SIM"
            }));

            _onMismatchExecutionTrigger?.Invoke(instrument.Trim(), acknowledgedAt, new MismatchExecutionTriggerDetails
            {
                IntentId = intentId,
                WorkingOrderSubmitTransition = true
            });

            return OrderSubmissionResult.SuccessResult(order.OrderId, utcNow, acknowledgedAt);
        }
        catch (Exception ex)
        {
            var (tradingDate10, stream10, _, _, _, _, _) = GetIntentInfo(intentId);
            _executionJournal.RecordRejection(intentId, tradingDate10, stream10, $"ENTRY_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "ENTRY", rejectedPrice: entryPrice, rejectedQuantity: quantity);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                account = "SIM",
                exception_type = ex.GetType().Name
            }));
            return OrderSubmissionResult.FailureResult($"Entry order submission failed: {ex.Message}", utcNow);
        }
    }

}

#endif
