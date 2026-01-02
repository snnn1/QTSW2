// NinjaTrader-specific implementation using real NT APIs
// This file is compiled only when NINJATRADER is defined (inside NT Strategy context)

#if NINJATRADER
using System;
using System.Collections.Concurrent;
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
            var error = $"Account '{account.Name}' is not Sim account - aborting execution";
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
            var orderType = entryPrice.HasValue ? OrderType.Limit : OrderType.Market;
            
            // Real NT API: CreateOrder
            var order = account.CreateOrder(ntInstrument, orderAction, orderType, quantity, entryPrice ?? 0);
            order.Tag = intentId; // Namespace by intent_id
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
                NTOrder = order // Store NT order object
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

            // Journal: ENTRY_SUBMITTED
            _executionJournal.RecordSubmission(intentId, "", "", instrument, "ENTRY", order.OrderId, acknowledgedAt);

            _log.Write(RobotEvents.ExecutionBase(acknowledgedAt, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = order.OrderId,
                order_type = "ENTRY",
                direction,
                entry_price = entryPrice,
                quantity,
                account = "SIM",
                order_action = orderAction.ToString(),
                order_type_nt = orderType.ToString(),
                order_state = submitResult.OrderState.ToString()
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

        var intentId = order.Tag as string;
        if (string.IsNullOrEmpty(intentId)) return;

        var utcNow = DateTimeOffset.UtcNow;
        var orderState = order.OrderState;

        if (!_orderMap.TryGetValue(intentId, out var orderInfo))
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo?.Instrument ?? "", "EXECUTION_UPDATE_UNKNOWN_ORDER",
                new { error = "Order not found in tracking map", broker_order_id = order.OrderId }));
            return;
        }

        // Update journal based on order state
        if (orderState == OrderState.Accepted)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_ACKNOWLEDGED",
                new { broker_order_id = order.OrderId, order_type = orderInfo.OrderType }));
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

        var intentId = order.Tag as string;
        if (string.IsNullOrEmpty(intentId)) return;

        var utcNow = DateTimeOffset.UtcNow;
        var fillPrice = (decimal)execution.Price;
        var fillQuantity = execution.Quantity;

        if (!_orderMap.TryGetValue(intentId, out var orderInfo))
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", "EXECUTION_UPDATE_UNKNOWN_ORDER",
                new { error = "Order not found in tracking map", broker_order_id = order.OrderId }));
            return;
        }

        // Update ExecutionJournal: PARTIAL_FILL or FILLED
        if (fillQuantity < orderInfo.Quantity)
        {
            // Partial fill
            _executionJournal.RecordFill(intentId, "", "", fillPrice, fillQuantity, utcNow);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_PARTIAL_FILL",
                new
                {
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    order_quantity = orderInfo.Quantity,
                    broker_order_id = order.OrderId
                }));
        }
        else
        {
            // Full fill
            _executionJournal.RecordFill(intentId, "", "", fillPrice, fillQuantity, utcNow);
            orderInfo.State = "FILLED";

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_FILLED",
                new
                {
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    broker_order_id = order.OrderId,
                    order_type = orderInfo.OrderType
                }));

            // STEP 4: On full entry fill, trigger protective order submission
            if (orderInfo.OrderType == "ENTRY" && _intentMap.TryGetValue(intentId, out var intent))
            {
                HandleEntryFill(intentId, intent, fillPrice, fillQuantity, utcNow);
            }
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
            // Real NT API: Create stop order
            var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
            var order = account.CreateOrder(ntInstrument, orderAction, OrderType.StopMarket, quantity, stopPrice);
            order.Tag = $"{intentId}_STOP";
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
            // Real NT API: Create target order
            var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
            var order = account.CreateOrder(ntInstrument, orderAction, OrderType.Limit, quantity, targetPrice);
            order.Tag = $"{intentId}_TARGET";
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
            // Find existing stop order
            var stopOrder = account.Orders.FirstOrDefault(o => 
                o.Tag == $"{intentId}_STOP" && 
                (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

            if (stopOrder == null)
            {
                var error = "Stop order not found for BE modification";
                return OrderModificationResult.FailureResult(error, utcNow);
            }

            // Real NT API: Modify stop price
            stopOrder.StopPrice = beStopPrice;
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
}

// Extend OrderInfo to store NT order object
partial class OrderInfo
{
    public object? NTOrder { get; set; } // NinjaTrader.Cbi.Order
}

#endif
