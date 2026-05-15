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
    /// STEP 4: Submit protective stop using real NT API.
    /// </summary>
    private OrderSubmissionResult SubmitProtectiveStopReal(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        if (_ntAccount == null || _ntInstrument == null)
        {
            var error = "NT context not set";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        var account = _ntAccount as Account;
        if (account == null)
        {
            var error = "NT account type mismatch";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        // Fix 3: Anchor on Instrument instance - use strategy's Instrument directly
        var ntInstrument = _ntInstrument as Instrument;
        if (ntInstrument == null)
        {
            var error = "Strategy Instrument instance not available - cannot submit protective stop order";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_BLOCKED", new
            {
                error,
                reason = "INSTRUMENT_INSTANCE_NULL"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            // Compute stop price once for use throughout method
            var stopPriceD = (double)stopPrice;

            var relationshipFailure = TryBlockInvalidStopMarketRelationship(
                intentId, instrument, direction, stopPrice, quantity, "PROTECTIVE_STOP", utcNow,
                out var protectiveConvertToMarket);
            if (relationshipFailure != null)
                return relationshipFailure;
            
            // Idempotent: if stop already exists, ensure it matches desired stop/qty
            var stopTag = RobotOrderIds.EncodeStopTag(intentId);
            var existingStop = SnapshotAccountOrders(account).FirstOrDefault(o =>
                GetOrderTag(o) == stopTag &&
                (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

            if (existingStop != null)
            {
                var quantityChanged = existingStop.Quantity != quantity;
                var priceChanged = Math.Abs(existingStop.StopPrice - stopPriceD) > 1e-10;
                var changed = quantityChanged || priceChanged;

                // GC FIX: If quantity changed, cancel and recreate (NinjaTrader may not allow quantity changes)
                if (quantityChanged)
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "PROTECTIVE_ORDER_QUANTITY_CHANGE", new
                    {
                        old_quantity = existingStop.Quantity,
                        new_quantity = quantity,
                        order_type = "PROTECTIVE_STOP",
                        broker_order_id = existingStop.OrderId,
                        note = "Quantity changed - canceling and recreating protective orders (NinjaTrader may not allow quantity changes on working orders)"
                    }));
                    
                    // Cancel existing protective orders (both stop and target since they're OCO paired)
                    CancelProtectiveOrdersForIntent(intentId, utcNow);
                    
                    // Fall through to create new order below
                    existingStop = null;
                }
                else if (priceChanged)
                {
                    // Only price changed - try to update (quantity unchanged)
                    dynamic dynAccountChange = account;
                    Order[]? changeRes = null;
                    try
                    {
                        object? changeResult = dynAccountChange.Change(new[] { existingStop });
                        if (changeResult != null && changeResult is Order[] changeArray)
                        {
                            changeRes = changeArray;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Change() call failed - log and attempt fallback
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CHANGE_FALLBACK", new
                        {
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            order_type = "PROTECTIVE_STOP",
                            broker_order_id = existingStop.OrderId,
                            note = "Change() call failed, attempting fallback (Change returns void)"
                        }));
                        
                        // Fallback: Change returns void - check order state directly
                        try
                        {
                            // Try calling Change() again (void return)
                            dynAccountChange.Change(new[] { existingStop });
                            changeRes = new[] { existingStop };
                        }
                        catch (Exception fallbackEx)
                        {
                            // GC FIX: When order change fails (especially quantity changes), cancel and recreate
                            // NinjaTrader may not allow quantity changes on working orders, so we need to cancel and recreate
                            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CHANGE_FAILED_CANCEL_RECREATE", new
                            {
                                error = ex.Message,
                                fallback_error = fallbackEx.Message,
                                order_type = "PROTECTIVE_STOP",
                                broker_order_id = existingStop.OrderId,
                                old_quantity = existingStop.Quantity,
                                new_quantity = quantity,
                                note = "Order change failed - will cancel and recreate with correct quantity"
                            }));
                            
                            // Cancel existing stop order (and its OCO pair target if exists)
                            CancelProtectiveOrdersForIntent(intentId, utcNow);
                            
                            // Fall through to create new order below
                            existingStop = null; // Clear so we create new order
                        }
                    }
                    if (existingStop != null &&
                        (changeRes == null || changeRes.Length == 0 || changeRes[0].OrderState == OrderState.Rejected))
                    {
                        // GC FIX: When order change is rejected, cancel and recreate
                        dynamic dynChangeRes = changeRes?[0];
                        var err = (string?)dynChangeRes?.Error ?? "Stop order change rejected";
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CHANGE_REJECTED_CANCEL_RECREATE", new
                        {
                            error = err,
                            order_type = "PROTECTIVE_STOP",
                            broker_order_id = existingStop.OrderId,
                            old_quantity = existingStop.Quantity,
                            new_quantity = quantity,
                            note = "Order change rejected - will cancel and recreate with correct quantity"
                        }));
                        
                        // Cancel existing stop order (and its OCO pair target if exists)
                        CancelProtectiveOrdersForIntent(intentId, utcNow);
                        
                        // Fall through to create new order below
                        existingStop = null; // Clear so we create new order
                    }
                }

                if (existingStop != null)
                {
                    EnsureExistingProtectiveOrderTracked(
                        existingStop,
                        intentId,
                        instrument,
                        direction,
                        quantity,
                        stopPrice,
                        OrderRole.STOP,
                        utcNow,
                        "SubmitProtectiveStop_IdempotentExisting");

                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
                    {
                        broker_order_id = existingStop.OrderId,
                        order_type = "PROTECTIVE_STOP",
                        direction,
                        stop_price = stopPrice,
                        quantity,
                        account = "SIM",
                        note = "Idempotent: stop already existed; ensured correct qty/price"
                    }));

                    QuantExecutionControlStore.NotifyProtectiveStopSubmitted(instrument, utcNow);
                    return OrderSubmissionResult.SuccessResult(existingStop.OrderId, utcNow, utcNow);
                }
            }

            // Real NT API: Create stop order using official NT8 CreateOrder factory method
            var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
            // stopPriceD is already computed above before idempotency check
            
            // Get ocoGroup from intent if not provided (use separate variable to avoid parameter shadowing)
            string? ocoGroupToUse = ocoGroup;
            if (ocoGroupToUse == null)
            {
                var (_, _, _, _, _, _, intentOcoGroup) = GetIntentInfo(intentId);
                ocoGroupToUse = intentOcoGroup;
            }
            
            // Runtime safety checks BEFORE CreateOrder
            if (!_ntContextSet)
            {
                var error = "NT context not set - cannot create protective StopMarket order";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                {
                    error,
                    order_type = "StopMarket",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    stop_price = stopPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }
            
            if (account == null)
            {
                var error = "Account is null - cannot create protective StopMarket order";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                {
                    error,
                    order_type = "StopMarket",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    stop_price = stopPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }
            
            if (ntInstrument == null)
            {
                var error = "Instrument is null - cannot create protective StopMarket order";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                {
                    error,
                    order_type = "StopMarket",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    stop_price = stopPriceD,
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
                    stop_price = stopPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                throw new InvalidOperationException(error);
            }
            
            if (stopPriceD <= 0)
            {
                var error = $"Invalid stop price: {stopPriceD} (must be > 0)";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                {
                    error,
                    order_type = "StopMarket",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    stop_price = stopPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }
            
            Order order;
            try
            {
                // Create order using official NT8 CreateOrder factory method
                order = account.CreateOrder(
                    ntInstrument,                           // Instrument
                    orderAction,                            // OrderAction
                    OrderType.StopMarket,                   // OrderType
                    OrderEntry.Manual,                      // OrderEntry
                    TimeInForce.Day,                        // TimeInForce
                    quantity,                               // Quantity
                    0.0,                                    // LimitPrice (0 for StopMarket)
                    stopPriceD,                             // StopPrice (use already computed variable)
                    ocoGroupToUse,                          // Oco (OCO group for pairing with target order)
                    $"{intentId}_STOP",                     // OrderName
                    DateTime.MinValue,                      // Gtd
                    null                                    // CustomOrder
                );
                
                // Log success before Submit
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATED_STOPMARKET", new
                {
                    order_name = $"{intentId}_STOP",
                    stop_price = stopPriceD,
                    quantity = quantity,
                    order_action = orderAction.ToString(),
                    instrument = instrument
                }));
                
                // CRITICAL FIX: Use EncodeStopTag() so BE modification can find the order
                // BE modification code (ModifyStopToBreakEven) looks for tag QTSW2:{intentId}:STOP
                SetOrderTag(order, RobotOrderIds.EncodeStopTag(intentId));
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                {
                    error = $"Failed to create StopMarket order: {ex.Message}",
                    order_type = "StopMarket",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    stop_price = stopPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult($"Failed to create StopMarket order: {ex.Message}", utcNow);
            }
            
            order.TimeInForce = TimeInForce.Day;

            // Real NT API: Submit order
            Order[] result;
            try
            {
                account.Submit(new[] { order });
                // Submit returns void - use the order we created
                result = new[] { order };
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
                {
                    error = $"Failed to submit StopMarket order: {ex.Message}",
                    order_type = "StopMarket",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    stop_price = stopPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                var (tradingDate1, stream1, _, _, _, _, _) = GetIntentInfo(intentId);
                _executionJournal.RecordRejection(intentId, tradingDate1, stream1, $"STOP_SUBMIT_FAILED: {ex.Message}", utcNow, 
                    orderType: "STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);
                return OrderSubmissionResult.FailureResult($"Failed to submit StopMarket order: {ex.Message}", utcNow);
            }
            
            if (result == null || result.Length == 0 || result[0].OrderState == OrderState.Rejected)
            {
                dynamic dynResult = result?[0];
                string error = "Stop order rejected";
                try
                {
                    error = (string?)dynResult?.ErrorMessage ?? (string?)dynResult?.Error ?? "Stop order rejected";
                }
                catch
                {
                    try
                    {
                        error = (string?)dynResult?.Error ?? "Stop order rejected";
                    }
                    catch
                    {
                        error = "Stop order rejected";
                    }
                }
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
                {
                    error,
                    order_type = "StopMarket",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    stop_price = stopPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                var (tradingDate2, stream2, _, _, _, _, _) = GetIntentInfo(intentId);
                _executionJournal.RecordRejection(intentId, tradingDate2, stream2, $"STOP_SUBMIT_FAILED: {error}", utcNow, 
                    orderType: "STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }

            // Journal: STOP_SUBMITTED
            var (tradingDate3, stream3, _, intentStopPrice, intentTargetPrice, _, intentOcoGroupFromJournal) = GetIntentInfo(intentId);
            // Use intentOcoGroup if ocoGroupToUse is still null
            if (ocoGroupToUse == null)
            {
                ocoGroupToUse = intentOcoGroupFromJournal;
            }
            _executionJournal.RecordSubmission(intentId, tradingDate3, stream3, instrument, "STOP", order.OrderId, utcNow, 
                expectedEntryPrice: null, entryPrice: null, stopPrice: intentStopPrice ?? stopPrice, 
                targetPrice: intentTargetPrice, direction: direction, ocoGroup: ocoGroupToUse);

            // CRITICAL FIX: Add protective stop order to _orderMap so it can be tracked when it fills
            // Without this, protective stop fills are treated as untracked and trigger flatten operations
            var stopOrderInfo = new OrderInfo
            {
                IntentId = intentId,
                Instrument = instrument,
                OrderId = order.OrderId,
                OrderType = "STOP",
                Direction = direction,
                Quantity = quantity,
                Price = stopPrice,
                State = "SUBMITTED",
                NTOrder = order,
                IsEntryOrder = false, // Protective order, not entry
                FilledQuantity = 0
            };
            _orderMap[intentId] = stopOrderInfo; // Use same intentId - OnExecutionUpdate will find it by tag decode
            _orderMap[$"{intentId}:STOP"] = stopOrderInfo;
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = order.OrderId,
                order_type = "PROTECTIVE_STOP",
                direction,
                stop_price = stopPrice,
                quantity,
                account = "SIM",
                note = "Protective stop order added to _orderMap for tracking"
            }));

            QuantExecutionControlStore.NotifyProtectiveStopSubmitted(instrument, utcNow);
            return OrderSubmissionResult.SuccessResult(order.OrderId, utcNow, utcNow);
        }
        catch (Exception ex)
        {
            var (tradingDate14, stream14, _, _, _, _, _) = GetIntentInfo(intentId);
            _executionJournal.RecordRejection(intentId, tradingDate14, stream14, $"STOP_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);
            return OrderSubmissionResult.FailureResult($"Stop order submission failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// STEP 4: Submit target order using real NT API.
    /// </summary>
    private OrderSubmissionResult SubmitTargetOrderReal(
        string intentId,
        string instrument,
        string direction,
        decimal targetPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        if (_ntAccount == null || _ntInstrument == null)
        {
            var error = "NT context not set";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        var account = _ntAccount as Account;
        if (account == null)
        {
            var error = "NT account type mismatch";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        // Fix 3: Anchor on Instrument instance - use strategy's Instrument directly
        var ntInstrument = _ntInstrument as Instrument;
        if (ntInstrument == null)
        {
            var error = "Strategy Instrument instance not available - cannot submit target order";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_BLOCKED", new
            {
                error,
                reason = "INSTRUMENT_INSTANCE_NULL"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            // Idempotent: if target already exists, ensure it matches desired target/qty
            var targetTag = RobotOrderIds.EncodeTargetTag(intentId);
            var existingTarget = SnapshotAccountOrders(account).FirstOrDefault(o =>
                GetOrderTag(o) == targetTag &&
                (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

            if (existingTarget != null)
            {
                var existingTargetPriceD = (double)targetPrice;
                var quantityChanged = existingTarget.Quantity != quantity;
                var priceChanged = Math.Abs(existingTarget.LimitPrice - existingTargetPriceD) > 1e-10;
                var changed = quantityChanged || priceChanged;

                // GC FIX: If quantity changed, cancel and recreate (NinjaTrader may not allow quantity changes)
                if (quantityChanged)
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "PROTECTIVE_ORDER_QUANTITY_CHANGE", new
                    {
                        old_quantity = existingTarget.Quantity,
                        new_quantity = quantity,
                        order_type = "PROTECTIVE_TARGET",
                        broker_order_id = existingTarget.OrderId,
                        note = "Quantity changed - canceling and recreating protective orders (NinjaTrader may not allow quantity changes on working orders)"
                    }));
                    
                    // Cancel existing protective orders (both stop and target since they're OCO paired)
                    CancelProtectiveOrdersForIntent(intentId, utcNow);
                    
                    // Fall through to create new order below
                    existingTarget = null;
                }
                else if (priceChanged)
                {
                    // Only price changed - try to update (quantity unchanged)
                    dynamic dynAccountChangeTarget = account;
                    Order[]? changeRes = null;
                    try
                    {
                        object? changeResult = dynAccountChangeTarget.Change(new[] { existingTarget });
                        if (changeResult != null && changeResult is Order[] changeArray)
                        {
                            changeRes = changeArray;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Change() call failed - log and attempt fallback
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CHANGE_FALLBACK", new
                        {
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            order_type = "PROTECTIVE_TARGET",
                            broker_order_id = existingTarget.OrderId,
                            note = "Change() call failed, attempting fallback (Change returns void)"
                        }));
                        
                        // Fallback: Change returns void - check order state directly
                        try
                        {
                            // Try calling Change() again (void return)
                            dynAccountChangeTarget.Change(new[] { existingTarget });
                            changeRes = new[] { existingTarget };
                        }
                        catch (Exception fallbackEx)
                        {
                            // GC FIX: When order change fails, cancel and recreate
                            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CHANGE_FAILED_CANCEL_RECREATE", new
                            {
                                error = ex.Message,
                                fallback_error = fallbackEx.Message,
                                order_type = "PROTECTIVE_TARGET",
                                broker_order_id = existingTarget.OrderId,
                                old_quantity = existingTarget.Quantity,
                                new_quantity = quantity,
                                note = "Order change failed - will cancel and recreate with correct quantity"
                            }));
                            
                            // Cancel existing target order (and its OCO pair stop if exists)
                            CancelProtectiveOrdersForIntent(intentId, utcNow);
                            
                            // Fall through to create new order below
                            existingTarget = null; // Clear so we create new order
                        }
                    }
                    if (existingTarget != null &&
                        (changeRes == null || changeRes.Length == 0 || changeRes[0].OrderState == OrderState.Rejected))
                    {
                        // GC FIX: When order change is rejected, cancel and recreate
                        dynamic dynChangeRes = changeRes?[0];
                        var err = (string?)dynChangeRes?.Error ?? "Target order change rejected";
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CHANGE_REJECTED_CANCEL_RECREATE", new
                        {
                            error = err,
                            order_type = "PROTECTIVE_TARGET",
                            broker_order_id = existingTarget.OrderId,
                            old_quantity = existingTarget.Quantity,
                            new_quantity = quantity,
                            note = "Order change rejected - will cancel and recreate with correct quantity"
                        }));
                        
                        // Cancel existing target order (and its OCO pair stop if exists)
                        CancelProtectiveOrdersForIntent(intentId, utcNow);
                        
                        // Fall through to create new order below
                        existingTarget = null; // Clear so we create new order
                    }
                }

                if (existingTarget != null)
                {
                    EnsureExistingProtectiveOrderTracked(
                        existingTarget,
                        intentId,
                        instrument,
                        direction,
                        quantity,
                        targetPrice,
                        OrderRole.TARGET,
                        utcNow,
                        "SubmitTargetOrder_IdempotentExisting");

                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
                    {
                        broker_order_id = existingTarget.OrderId,
                        order_type = "TARGET",
                        direction,
                        target_price = targetPrice,
                        quantity,
                        account = "SIM",
                        note = "Idempotent: target already existed; ensured correct qty/price"
                    }));

                    return OrderSubmissionResult.SuccessResult(existingTarget.OrderId, utcNow, utcNow);
                }
            }

            // Real NT API: Create target order using official NT8 CreateOrder factory method
            var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
            var targetPriceD = (double)targetPrice;
            
            // Runtime safety checks BEFORE CreateOrder
            if (!_ntContextSet)
            {
                var error = "NT context not set - cannot create protective Limit order";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                {
                    error,
                    order_type = "Limit",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    target_price = targetPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }
            
            if (account == null)
            {
                var error = "Account is null - cannot create protective Limit order";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                {
                    error,
                    order_type = "Limit",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    target_price = targetPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }
            
            if (ntInstrument == null)
            {
                var error = "Instrument is null - cannot create protective Limit order";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                {
                    error,
                    order_type = "Limit",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    target_price = targetPriceD,
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
                    order_type = "Limit",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    target_price = targetPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                throw new InvalidOperationException(error);
            }
            
            if (targetPriceD <= 0)
            {
                var error = $"Invalid target price: {targetPriceD} (must be > 0)";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                {
                    error,
                    order_type = "Limit",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    target_price = targetPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }
            
            Order order;
            try
            {
                // Create order using official NT8 CreateOrder factory method (same signature as StopMarket)
                order = account.CreateOrder(
                    ntInstrument,                           // Instrument
                    orderAction,                            // OrderAction
                    OrderType.Limit,                        // OrderType
                    OrderEntry.Manual,                      // OrderEntry
                    TimeInForce.Day,                        // TimeInForce
                    quantity,                               // Quantity
                    targetPriceD,                           // LimitPrice
                    0.0,                                    // StopPrice (0 for Limit orders)
                    ocoGroup,                               // Oco (OCO group for pairing with stop order)
                    $"{intentId}_TARGET",                   // OrderName
                    DateTime.MinValue,                      // Gtd
                    null                                    // CustomOrder
                );
                
                // Log success before Submit
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATED_LIMIT", new
                {
                    order_name = $"{intentId}_TARGET",
                    target_price = targetPriceD,
                    quantity = quantity,
                    order_action = orderAction.ToString(),
                    instrument = instrument
                }));
                
                // Set order tag
                SetOrderTag(order, targetTag);
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    order_type = "Limit",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    target_price = targetPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult($"Target order creation failed: {ex.Message}", utcNow);
            }

            // Real NT API: Submit order
            Order[] result;
            dynamic dynAccountTarget = account;
            try
            {
                object? submitResult = dynAccountTarget.Submit(new[] { order });
                if (submitResult != null && submitResult is Order[] resultArray)
                {
                    result = resultArray;
                }
                else
                {
                    result = new[] { order };
                }
            }
            catch
            {
                dynAccountTarget.Submit(new[] { order });
                result = new[] { order };
            }
            
            if (result == null || result.Length == 0 || result[0].OrderState == OrderState.Rejected)
            {
                // GC FIX: Safely extract error message - Order object doesn't have ErrorMessage property
                string error = "Target order rejected";
                if (result != null && result.Length > 0)
                {
                    try
                    {
                        dynamic dynResult = result[0];
                        error = (string?)dynResult?.Error ?? "Target order rejected";
                    }
                    catch
                    {
                        // Fallback if dynamic access fails
                        error = "Target order rejected";
                    }
                }
                
                var (tradingDate4, stream4, _, _, _, _, _) = GetIntentInfo(intentId);
                _executionJournal.RecordRejection(intentId, tradingDate4, stream4, $"TARGET_SUBMIT_FAILED: {error}", utcNow, 
                    orderType: "TARGET", rejectedPrice: targetPrice, rejectedQuantity: quantity);
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }

            // Journal: TARGET_SUBMITTED
            var (tradingDate5, stream5, _, intentStopPrice2, intentTargetPrice2, _, ocoGroup2) = GetIntentInfo(intentId);
            _executionJournal.RecordSubmission(intentId, tradingDate5, stream5, instrument, "TARGET", order.OrderId, utcNow, 
                expectedEntryPrice: null, entryPrice: null, stopPrice: intentStopPrice2, 
                targetPrice: intentTargetPrice2 ?? targetPrice, direction: direction, ocoGroup: ocoGroup2);

            // CRITICAL FIX: Add protective target order to _orderMap so it can be tracked when it fills
            // Without this, protective target fills are treated as untracked and trigger flatten operations
            // Note: This overwrites entry order in _orderMap, but that's OK because:
            // - Entry order is already filled by the time protective orders are submitted
            // - Entry fills are tracked in execution journal (ground truth)
            // - Tag-based detection provides fallback if _orderMap lookup fails
            var targetOrderInfo = new OrderInfo
            {
                IntentId = intentId,
                Instrument = instrument,
                OrderId = order.OrderId,
                OrderType = "TARGET",
                Direction = direction,
                Quantity = quantity,
                Price = targetPrice,
                State = "SUBMITTED",
                NTOrder = order,
                IsEntryOrder = false, // Protective order, not entry
                FilledQuantity = 0
            };
            _orderMap[intentId] = targetOrderInfo; // Overwrites entry order (already filled) or stop order (if stop was added first)
            _orderMap[$"{intentId}:TARGET"] = targetOrderInfo;
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = order.OrderId,
                order_type = "TARGET",
                direction,
                target_price = targetPrice,
                quantity,
                account = "SIM",
                note = "Protective target order added to _orderMap for tracking"
            }));

            return OrderSubmissionResult.SuccessResult(order.OrderId, utcNow, utcNow);
        }
        catch (Exception ex)
        {
            var (tradingDate6, stream6, _, _, _, _, _) = GetIntentInfo(intentId);
            _executionJournal.RecordRejection(intentId, tradingDate6, stream6, $"TARGET_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "TARGET", rejectedPrice: targetPrice, rejectedQuantity: quantity);
            return OrderSubmissionResult.FailureResult($"Target order submission failed: {ex.Message}", utcNow);
        }
    }

    private void EnsureExistingProtectiveOrderTracked(
        Order order,
        string intentId,
        string instrument,
        string direction,
        int quantity,
        decimal price,
        OrderRole orderRole,
        DateTimeOffset utcNow,
        string sourceContext)
    {
        var orderType = orderRole == OrderRole.STOP ? "STOP" : "TARGET";
        var orderInfo = new OrderInfo
        {
            IntentId = intentId,
            Instrument = instrument,
            OrderId = order.OrderId,
            OrderType = orderType,
            Direction = direction,
            Quantity = quantity,
            Price = price,
            State = order.OrderState.ToString().ToUpperInvariant(),
            NTOrder = order,
            IsEntryOrder = false,
            FilledQuantity = 0
        };

        OrderMap[intentId] = orderInfo;
        OrderMap[$"{intentId}:{orderType}"] = orderInfo;

        if (_iea == null || string.IsNullOrWhiteSpace(order.OrderId))
            return;

        if (!_iea.TryResolveForExecutionUpdate(order.OrderId, intentId, orderType, out _, out _))
        {
            _iea.RegisterOrder(
                order.OrderId,
                intentId,
                instrument,
                IntentMap.TryGetValue(intentId, out var intent) ? intent.Stream : null,
                orderRole,
                OrderOwnershipStatus.OWNED,
                sourceContext,
                orderInfo,
                utcNow);
        }

        if (order.OrderState == OrderState.Working)
            _iea.UpdateOrderLifecycle(order.OrderId, OrderLifecycleState.WORKING, utcNow);
    }

}

#endif
