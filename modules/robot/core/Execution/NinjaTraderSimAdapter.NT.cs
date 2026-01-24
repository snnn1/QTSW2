// NinjaTrader-specific implementation using real NT APIs
// This file is compiled only when NINJATRADER is defined (inside NT Strategy context)

#if NINJATRADER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Real NinjaTrader API implementation for SIM adapter.
/// This partial class provides real NT API calls when running inside NinjaTrader.
/// </summary>
public partial class NinjaTraderSimAdapter
{
    /// <summary>
    /// STEP 1: Verify SIM account using real NT API.
    /// </summary>
    private void VerifySimAccountReal()
    {
        if (_ntAccount == null)
        {
            var error = "NT account is null - cannot verify Sim account";
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

        // Assert: account.IsSimAccount == true
        if (!account.IsSimAccount)
        {
            var error = $"Account '{account.Name}' is not a Sim account - aborting execution";
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "NOT_SIM_ACCOUNT", account_name = account.Name, error }));
            throw new InvalidOperationException(error);
        }

        _simAccountVerified = true;
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "SIM_ACCOUNT_VERIFIED", state: "ENGINE",
            new { account_name = account.Name, note = "SIM account verification passed" }));
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
        DateTimeOffset utcNow)
    {
        if (_ntAccount == null || _ntInstrument == null)
        {
            var error = "NT context not set - cannot submit orders";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        var account = _ntAccount as Account;
        var ntInstrument = _ntInstrument as Instrument;
        
        if (account == null || ntInstrument == null)
        {
            var error = "NT context type mismatch";
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
            
            // Real NT API: CreateOrder
            var order = account.CreateOrder(ntInstrument, orderAction, orderType, quantity, ntEntryPrice);
            order.Tag = RobotOrderIds.EncodeTag(intentId); // Robot-owned envelope
            order.TimeInForce = TimeInForce.Day;

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
            _orderMap[intentId] = orderInfo;

            // Real NT API: Submit order
            var result = account.Submit(new[] { order });
            
            if (result == null || result.Length == 0)
            {
                var error = "Order submission returned null/empty result";
                _executionJournal.RecordRejection(intentId, "", "", $"ENTRY_SUBMIT_FAILED: {error}", utcNow);
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }

            var submitResult = result[0];
            var acknowledgedAt = DateTimeOffset.UtcNow;

            if (submitResult.OrderState == OrderState.Rejected)
            {
                var error = submitResult.ErrorMessage ?? "Order rejected";
                _executionJournal.RecordRejection(intentId, "", "", $"ENTRY_SUBMIT_FAILED: {error}", utcNow);
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
                {
                    error,
                    broker_order_id = order.OrderId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }

            // Journal: ENTRY_SUBMITTED (store expected entry price for slippage calculation)
            _executionJournal.RecordSubmission(intentId, "", "", instrument, "ENTRY", order.OrderId, acknowledgedAt, entryPrice);

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

            return OrderSubmissionResult.SuccessResult(order.OrderId, utcNow, acknowledgedAt);
        }
        catch (Exception ex)
        {
            _executionJournal.RecordRejection(intentId, "", "", $"ENTRY_SUBMIT_FAILED: {ex.Message}", utcNow);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                account = "SIM",
                exception_type = ex.GetType().Name
            }));
            return OrderSubmissionResult.FailureResult($"Entry order submission failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// STEP 3: Handle real NT OrderUpdate event.
    /// Called from public HandleOrderUpdate() method in main adapter.
    /// </summary>
    private void HandleOrderUpdateReal(object orderObj, object orderUpdateObj)
    {
        var order = orderObj as Order;
        var orderUpdate = orderUpdateObj as OrderUpdate;
        if (order == null) return;

        var encodedTag = order.Tag as string;
        var intentId = RobotOrderIds.DecodeIntentId(encodedTag);
        if (string.IsNullOrEmpty(intentId)) return; // strict: non-robot orders ignored

        var utcNow = DateTimeOffset.UtcNow;
        var orderState = order.OrderState;

        if (!_orderMap.TryGetValue(intentId, out var orderInfo))
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo?.Instrument ?? "", "EXECUTION_UPDATE_UNKNOWN_ORDER",
                new { error = "Order not found in tracking map", broker_order_id = order.OrderId, tag = encodedTag }));
            return;
        }

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
            _executionJournal.RecordRejection(intentId, "", "", $"ORDER_REJECTED: {order.ErrorMessage}", utcNow);
            orderInfo.State = "REJECTED";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_REJECTED",
                new { broker_order_id = order.OrderId, error = order.ErrorMessage }));
        }
        else if (orderState == OrderState.Cancelled)
        {
            orderInfo.State = "CANCELLED";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_CANCELLED",
                new { broker_order_id = order.OrderId }));
        }
    }

    /// <summary>
    /// STEP 3: Handle real NT ExecutionUpdate event.
    /// Called from public HandleExecutionUpdate() method.
    /// </summary>
    private void HandleExecutionUpdateReal(object executionObj, object orderObj)
    {
        var execution = executionObj as Execution;
        var order = orderObj as Order;
        if (execution == null || order == null) return;

        var encodedTag = order.Tag as string;
        var intentId = RobotOrderIds.DecodeIntentId(encodedTag);
        if (string.IsNullOrEmpty(intentId)) return; // strict: non-robot orders ignored

        var utcNow = DateTimeOffset.UtcNow;
        var fillPrice = (decimal)execution.Price;
        var fillQuantity = execution.Quantity;

        if (!_orderMap.TryGetValue(intentId, out var orderInfo))
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", "EXECUTION_UPDATE_UNKNOWN_ORDER",
                new { error = "Order not found in tracking map", broker_order_id = order.OrderId, tag = encodedTag }));
            return;
        }

        // Track cumulative fills for partial-fill safety
        orderInfo.FilledQuantity += fillQuantity;
        var filledTotal = orderInfo.FilledQuantity;
        
        // Get contract multiplier for slippage calculation
        decimal? contractMultiplier = null;
        if (_ntInstrument is Instrument ntInst)
        {
            contractMultiplier = (decimal)ntInst.MasterInstrument.PointValue;
        }

        // Update ExecutionJournal: PARTIAL_FILL or FILLED (use cumulative quantity for safety)
        if (filledTotal < orderInfo.Quantity)
        {
            // Partial fill
            _executionJournal.RecordFill(intentId, "", "", fillPrice, filledTotal, utcNow, contractMultiplier);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_PARTIAL_FILL",
                new
                {
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    filled_total = filledTotal,
                    order_quantity = orderInfo.Quantity,
                    broker_order_id = order.OrderId,
                    order_type = orderInfo.OrderType
                }));
        }
        else
        {
            // Full fill
            _executionJournal.RecordFill(intentId, "", "", fillPrice, filledTotal, utcNow, contractMultiplier);
            orderInfo.State = "FILLED";

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_FILLED",
                new
                {
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    filled_total = filledTotal,
                    broker_order_id = order.OrderId,
                    order_type = orderInfo.OrderType
                }));
        }

        // STEP 4: Register entry fill with coordinator
        if (orderInfo.IsEntryOrder && _intentMap.TryGetValue(intentId, out var entryIntent))
        {
            // Register exposure with coordinator
            _coordinator?.OnEntryFill(intentId, filledTotal, entryIntent.Stream, entryIntent.Instrument, entryIntent.Direction ?? "", utcNow);
            
            // Ensure we protect the currently filled quantity (no market-close gating)
            HandleEntryFill(intentId, entryIntent, fillPrice, filledTotal, utcNow);
        }
        else if (orderInfo.OrderType == "STOP" || orderInfo.OrderType == "TARGET")
        {
            // Exit fill: register with coordinator
            _coordinator?.OnExitFill(intentId, filledTotal, utcNow);
        }
    }

    /// <summary>
    /// STEP 4: Submit protective stop using real NT API.
    /// </summary>
    private OrderSubmissionResult SubmitProtectiveStopReal(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        DateTimeOffset utcNow)
    {
        if (_ntAccount == null || _ntInstrument == null)
        {
            var error = "NT context not set";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        var account = _ntAccount as Account;
        var ntInstrument = _ntInstrument as Instrument;
        
        if (account == null || ntInstrument == null)
        {
            var error = "NT context type mismatch";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            // Idempotent: if stop already exists, ensure it matches desired stop/qty
            var stopTag = RobotOrderIds.EncodeStopTag(intentId);
            var existingStop = account.Orders.FirstOrDefault(o =>
                (o.Tag as string) == stopTag &&
                (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

            if (existingStop != null)
            {
                var changed = false;
                var stopPriceD = (double)stopPrice;
                if (existingStop.Quantity != quantity)
                {
                    existingStop.Quantity = quantity;
                    changed = true;
                }
                if (Math.Abs(existingStop.StopPrice - stopPriceD) > 1e-10)
                {
                    existingStop.StopPrice = stopPriceD;
                    changed = true;
                }

                if (changed)
                {
                    var changeRes = account.Change(new[] { existingStop });
                    if (changeRes == null || changeRes.Length == 0 || changeRes[0].OrderState == OrderState.Rejected)
                    {
                        var err = changeRes?[0]?.ErrorMessage ?? "Stop order change rejected";
                        _executionJournal.RecordRejection(intentId, "", "", $"STOP_CHANGE_FAILED: {err}", utcNow);
                        return OrderSubmissionResult.FailureResult(err, utcNow);
                    }
                }

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

                return OrderSubmissionResult.SuccessResult(existingStop.OrderId, utcNow, utcNow);
            }

            // Real NT API: Create stop order
            var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
            var order = account.CreateOrder(ntInstrument, orderAction, OrderType.StopMarket, quantity, (double)stopPrice);
            order.Tag = stopTag;
            order.TimeInForce = TimeInForce.Day;

            // Real NT API: Submit order
            var result = account.Submit(new[] { order });
            
            if (result == null || result.Length == 0 || result[0].OrderState == OrderState.Rejected)
            {
                var error = result?[0]?.ErrorMessage ?? "Stop order rejected";
                _executionJournal.RecordRejection(intentId, "", "", $"STOP_SUBMIT_FAILED: {error}", utcNow);
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }

            // Journal: STOP_SUBMITTED
            _executionJournal.RecordSubmission(intentId, "", "", instrument, "STOP", order.OrderId, utcNow);

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = order.OrderId,
                order_type = "PROTECTIVE_STOP",
                direction,
                stop_price = stopPrice,
                quantity,
                account = "SIM"
            }));

            return OrderSubmissionResult.SuccessResult(order.OrderId, utcNow, utcNow);
        }
        catch (Exception ex)
        {
            _executionJournal.RecordRejection(intentId, "", "", $"STOP_SUBMIT_FAILED: {ex.Message}", utcNow);
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
        DateTimeOffset utcNow)
    {
        if (_ntAccount == null || _ntInstrument == null)
        {
            var error = "NT context not set";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        var account = _ntAccount as Account;
        var ntInstrument = _ntInstrument as Instrument;
        
        if (account == null || ntInstrument == null)
        {
            var error = "NT context type mismatch";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            // Idempotent: if target already exists, ensure it matches desired target/qty
            var targetTag = RobotOrderIds.EncodeTargetTag(intentId);
            var existingTarget = account.Orders.FirstOrDefault(o =>
                (o.Tag as string) == targetTag &&
                (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

            if (existingTarget != null)
            {
                var changed = false;
                var targetPriceD = (double)targetPrice;
                if (existingTarget.Quantity != quantity)
                {
                    existingTarget.Quantity = quantity;
                    changed = true;
                }
                if (Math.Abs(existingTarget.LimitPrice - targetPriceD) > 1e-10)
                {
                    existingTarget.LimitPrice = targetPriceD;
                    changed = true;
                }

                if (changed)
                {
                    var changeRes = account.Change(new[] { existingTarget });
                    if (changeRes == null || changeRes.Length == 0 || changeRes[0].OrderState == OrderState.Rejected)
                    {
                        var err = changeRes?[0]?.ErrorMessage ?? "Target order change rejected";
                        _executionJournal.RecordRejection(intentId, "", "", $"TARGET_CHANGE_FAILED: {err}", utcNow);
                        return OrderSubmissionResult.FailureResult(err, utcNow);
                    }
                }

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

            // Real NT API: Create target order
            var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
            var order = account.CreateOrder(ntInstrument, orderAction, OrderType.Limit, quantity, (double)targetPrice);
            order.Tag = targetTag;
            order.TimeInForce = TimeInForce.Day;

            // Real NT API: Submit order
            var result = account.Submit(new[] { order });
            
            if (result == null || result.Length == 0 || result[0].OrderState == OrderState.Rejected)
            {
                var error = result?[0]?.ErrorMessage ?? "Target order rejected";
                _executionJournal.RecordRejection(intentId, "", "", $"TARGET_SUBMIT_FAILED: {error}", utcNow);
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }

            // Journal: TARGET_SUBMITTED
            _executionJournal.RecordSubmission(intentId, "", "", instrument, "TARGET", order.OrderId, utcNow);

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = order.OrderId,
                order_type = "TARGET",
                direction,
                target_price = targetPrice,
                quantity,
                account = "SIM"
            }));

            return OrderSubmissionResult.SuccessResult(order.OrderId, utcNow, utcNow);
        }
        catch (Exception ex)
        {
            _executionJournal.RecordRejection(intentId, "", "", $"TARGET_SUBMIT_FAILED: {ex.Message}", utcNow);
            return OrderSubmissionResult.FailureResult($"Target order submission failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// STEP 5: Modify stop to break-even using real NT API.
    /// </summary>
    private OrderModificationResult ModifyStopToBreakEvenReal(
        string intentId,
        string instrument,
        decimal beStopPrice,
        DateTimeOffset utcNow)
    {
        if (_ntAccount == null)
        {
            var error = "NT context not set";
            return OrderModificationResult.FailureResult(error, utcNow);
        }

        var account = _ntAccount as Account;
        if (account == null)
        {
            var error = "NT account type mismatch";
            return OrderModificationResult.FailureResult(error, utcNow);
        }

        try
        {
            // Find existing stop order (robot-owned tag envelope)
            var stopTag = RobotOrderIds.EncodeStopTag(intentId);
            var stopOrder = account.Orders.FirstOrDefault(o =>
                (o.Tag as string) == stopTag &&
                (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

            if (stopOrder == null)
            {
                var error = "Stop order not found for BE modification";
                return OrderModificationResult.FailureResult(error, utcNow);
            }

            // Real NT API: Modify stop price
            stopOrder.StopPrice = (double)beStopPrice;
            var result = account.Change(new[] { stopOrder });

            if (result == null || result.Length == 0 || result[0].OrderState == OrderState.Rejected)
            {
                var error = result?[0]?.ErrorMessage ?? "BE modification rejected";
                return OrderModificationResult.FailureResult(error, utcNow);
            }

            _executionJournal.RecordBEModification(intentId, "", "", beStopPrice, utcNow);

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_SUCCESS", new
            {
                be_stop_price = beStopPrice,
                broker_order_id = stopOrder.OrderId,
                account = "SIM"
            }));

            return OrderModificationResult.SuccessResult(utcNow);
        }
        catch (Exception ex)
        {
            return OrderModificationResult.FailureResult($"BE modification failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// Get account snapshot using real NT API.
    /// </summary>
    private AccountSnapshot GetAccountSnapshotReal(DateTimeOffset utcNow)
    {
        if (_ntAccount == null)
        {
            return new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };
        }
        
        var account = _ntAccount as Account;
        if (account == null)
        {
            return new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };
        }
        
        var positions = new List<PositionSnapshot>();
        var workingOrders = new List<WorkingOrderSnapshot>();
        
        try
        {
            // Get positions
            foreach (var position in account.Positions)
            {
                if (position.Quantity != 0)
                {
                    positions.Add(new PositionSnapshot
                    {
                        Instrument = position.Instrument.MasterInstrument.Name,
                        Quantity = position.Quantity,
                        AveragePrice = (decimal)position.AveragePrice
                    });
                }
            }
            
            // Get working orders
            foreach (var order in account.Orders)
            {
                if (order.OrderState == OrderState.Working || order.OrderState == OrderState.Accepted)
                {
                    workingOrders.Add(new WorkingOrderSnapshot
                    {
                        OrderId = order.OrderId,
                        Instrument = order.Instrument.MasterInstrument.Name,
                        Tag = order.Tag as string,
                        OcoGroup = order.Oco,
                        OrderType = order.OrderType.ToString(),
                        Price = order.OrderType == OrderType.Limit ? (decimal?)order.LimitPrice : null,
                        StopPrice = order.OrderType == OrderType.StopMarket || order.OrderType == OrderType.StopLimit ? (decimal?)order.StopPrice : null,
                        Quantity = order.Quantity
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ACCOUNT_SNAPSHOT_ERROR", state: "ENGINE",
                new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    note = "Failed to snapshot account - returning partial snapshot"
                }));
        }
        
        return new AccountSnapshot
        {
            Positions = positions,
            WorkingOrders = workingOrders
        };
    }
    
    /// <summary>
    /// Cancel orders for a specific intent only using real NT API.
    /// </summary>
    private bool CancelIntentOrdersReal(string intentId, DateTimeOffset utcNow)
    {
        if (_ntAccount == null)
        {
            return false;
        }
        
        var account = _ntAccount as Account;
        if (account == null)
        {
            return false;
        }
        
        var ordersToCancel = new List<Order>();
        
        try
        {
            // Find orders matching this intent ID
            foreach (var order in account.Orders)
            {
                if (order.OrderState != OrderState.Working && order.OrderState != OrderState.Accepted)
                {
                    continue;
                }
                
                var tag = order.Tag as string ?? "";
                var decodedIntentId = RobotOrderIds.DecodeIntentId(tag);
                
                // Match intent ID (handles STOP, TARGET suffixes)
                if (decodedIntentId == intentId)
                {
                    ordersToCancel.Add(order);
                }
            }
            
            if (ordersToCancel.Count > 0)
            {
                // Real NT API: Cancel orders
                account.Cancel(ordersToCancel.ToArray());
                
                // Update order map
                foreach (var order in ordersToCancel)
                {
                    var tag = order.Tag as string ?? "";
                    var decodedIntentId = RobotOrderIds.DecodeIntentId(tag);
                    if (decodedIntentId == intentId && _orderMap.TryGetValue(intentId, out var orderInfo))
                    {
                        orderInfo.State = "CANCELLED";
                    }
                }
                
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_SUCCESS", state: "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        cancelled_count = ordersToCancel.Count,
                        cancelled_order_ids = ordersToCancel.Select(o => o.OrderId).ToList()
                    }));
                
                return true;
            }
            
            return true; // No orders to cancel is success
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_ERROR", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    error = ex.Message,
                    exception_type = ex.GetType().Name
                }));
            return false;
        }
    }
    
    /// <summary>
    /// Flatten exposure for a specific intent only using real NT API.
    /// </summary>
    private FlattenResult FlattenIntentReal(string intentId, string instrument, DateTimeOffset utcNow)
    {
        if (_ntAccount == null || _ntInstrument == null)
        {
            var error = "NT context not set";
            return FlattenResult.FailureResult(error, utcNow);
        }
        
        var account = _ntAccount as Account;
        var ntInstrument = _ntInstrument as Instrument;
        
        if (account == null || ntInstrument == null)
        {
            var error = "NT context type mismatch";
            return FlattenResult.FailureResult(error, utcNow);
        }
        
        try
        {
            // Get position for this instrument
            var position = account.GetPosition(ntInstrument);
            
            if (position.MarketPosition == MarketPosition.Flat)
            {
                // Already flat
                return FlattenResult.SuccessResult(utcNow);
            }
            
            // Note: NinjaTrader API doesn't support per-intent flattening
            // We flatten the entire instrument position
            // This is acceptable because:
            // 1. The coordinator tracks remaining intents
            // 2. If other intents exist, they would need to be re-entered (rare path)
            // 3. This is an emergency fallback scenario
            
            account.Flatten(ntInstrument);
            
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_INTENT_SUCCESS", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    instrument = instrument,
                    position_qty = position.Quantity,
                    note = "Flattened instrument position (broker API limitation - per-intent not supported)"
                }));
            
            return FlattenResult.SuccessResult(utcNow);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_INTENT_ERROR", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    instrument = instrument,
                    error = ex.Message,
                    exception_type = ex.GetType().Name
                }));
            
            return FlattenResult.FailureResult($"Flatten intent failed: {ex.Message}", utcNow);
        }
    }
    
    /// <summary>
    /// Cancel robot-owned working orders using real NT API (strict prefix matching).
    /// </summary>
    private void CancelRobotOwnedWorkingOrdersReal(AccountSnapshot snap, DateTimeOffset utcNow)
    {
        if (_ntAccount == null)
        {
            return;
        }
        
        var account = _ntAccount as Account;
        if (account == null)
        {
            return;
        }
        
        var ordersToCancel = new List<Order>();
        
        try
        {
            // Find robot-owned orders in account
            foreach (var order in account.Orders)
            {
                if (order.OrderState != OrderState.Working && order.OrderState != OrderState.Accepted)
                {
                    continue;
                }
                
                var tag = order.Tag as string ?? "";
                var oco = order.Oco ?? "";
                
                // Strict robot-owned detection: Tag or OCO starts with "QTSW2:"
                if ((!string.IsNullOrEmpty(tag) && tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(oco) && oco.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)))
                {
                    ordersToCancel.Add(order);
                }
            }
            
            if (ordersToCancel.Count > 0)
            {
                // Real NT API: Cancel orders
                account.Cancel(ordersToCancel.ToArray());
                
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ROBOT_ORDERS_CANCELLED", state: "ENGINE",
                    new
                    {
                        cancelled_count = ordersToCancel.Count,
                        cancelled_order_ids = ordersToCancel.Select(o => o.OrderId).ToList(),
                        note = "Robot-owned working orders cancelled"
                    }));
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_ROBOT_ORDERS_ERROR", state: "ENGINE",
                new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    note = "Failed to cancel robot-owned orders"
                }));
        }
    }
    
    /// <summary>
    /// STEP 2b: Submit stop-market entry order using real NT API (breakout stop).
    /// </summary>
    private OrderSubmissionResult SubmitStopEntryOrderReal(
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
            var error = "NT context not set - cannot submit stop entry orders";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        var account = _ntAccount as Account;
        var ntInstrument = _ntInstrument as Instrument;

        if (account == null || ntInstrument == null)
        {
            var error = "NT context type mismatch";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            var orderAction = direction == "Long" ? OrderAction.Buy : OrderAction.SellShort;

            // Real NT API: Create stop-market entry
            var order = account.CreateOrder(ntInstrument, orderAction, OrderType.StopMarket, quantity, (double)stopPrice);
            order.Tag = RobotOrderIds.EncodeTag(intentId);
            order.TimeInForce = TimeInForce.Day;
            if (!string.IsNullOrEmpty(ocoGroup))
                order.Oco = ocoGroup;

            // Store order info for callback correlation
            var orderInfo = new OrderInfo
            {
                IntentId = intentId,
                Instrument = instrument,
                OrderId = order.OrderId,
                OrderType = "ENTRY_STOP",
                Direction = direction,
                Quantity = quantity,
                Price = stopPrice,
                State = "SUBMITTED",
                NTOrder = order,
                IsEntryOrder = true,
                FilledQuantity = 0
            };
            _orderMap[intentId] = orderInfo;

            var result = account.Submit(new[] { order });
            if (result == null || result.Length == 0)
            {
                var error = "Stop entry submission returned null/empty result";
                _executionJournal.RecordRejection(intentId, "", "", $"ENTRY_STOP_SUBMIT_FAILED: {error}", utcNow);
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }

            var submitResult = result[0];
            var acknowledgedAt = DateTimeOffset.UtcNow;

            if (submitResult.OrderState == OrderState.Rejected)
            {
                var error = submitResult.ErrorMessage ?? "Stop entry order rejected";
                _executionJournal.RecordRejection(intentId, "", "", $"ENTRY_STOP_SUBMIT_FAILED: {error}", utcNow);
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
                {
                    error,
                    order_type = "ENTRY_STOP",
                    broker_order_id = order.OrderId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }

            _executionJournal.RecordSubmission(intentId, "", "", instrument, "ENTRY_STOP", order.OrderId, acknowledgedAt);

            _log.Write(RobotEvents.ExecutionBase(acknowledgedAt, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = order.OrderId,
                order_type = "ENTRY_STOP",
                direction,
                stop_price = stopPrice,
                quantity,
                oco_group = ocoGroup,
                account = "SIM",
                order_action = orderAction.ToString(),
                order_type_nt = OrderType.StopMarket.ToString(),
                order_state = submitResult.OrderState.ToString()
            }));

            // Alias event for easier grepping (user-facing)
            _log.Write(RobotEvents.ExecutionBase(acknowledgedAt, intentId, instrument, "ORDER_SUBMITTED", new
            {
                broker_order_id = order.OrderId,
                order_type = "ENTRY_STOP",
                direction,
                stop_price = stopPrice,
                quantity,
                oco_group = ocoGroup,
                account = "SIM"
            }));

            return OrderSubmissionResult.SuccessResult(order.OrderId, utcNow, acknowledgedAt);
        }
        catch (Exception ex)
        {
            _executionJournal.RecordRejection(intentId, "", "", $"ENTRY_STOP_SUBMIT_FAILED: {ex.Message}", utcNow);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                order_type = "ENTRY_STOP",
                account = "SIM",
                exception_type = ex.GetType().Name
            }));
            return OrderSubmissionResult.FailureResult($"Stop entry order submission failed: {ex.Message}", utcNow);
        }
    }
}

// Extend OrderInfo to store NT order object
partial class OrderInfo
{
    public object? NTOrder { get; set; } // NinjaTrader.Cbi.Order
}

#endif
