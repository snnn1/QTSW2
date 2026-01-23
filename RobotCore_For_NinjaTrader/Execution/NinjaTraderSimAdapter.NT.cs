// NOTE: NinjaTrader's in-app C# compiler does not automatically define custom
// conditional compilation symbols. This file is the NT8 API implementation and
// must be compiled inside NinjaTrader.
#define NINJATRADER

// NinjaTrader-specific implementation using real NT APIs
// This file is compiled only when NINJATRADER is defined (inside NT Strategy context)

#if NINJATRADER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Real NinjaTrader API implementation for SIM adapter.
/// This partial class provides real NT API calls when running inside NinjaTrader.
/// </summary>
public sealed partial class NinjaTraderSimAdapter
{
    /// <summary>
    /// NT8 API compatibility shim for Account.CreateOrder().
    /// Different NT8 builds/broker adapters expose different CreateOrder overload sets.
    /// We use reflection to select an available overload at runtime and supply best-effort defaults.
    /// </summary>
    private static Order CreateOrderCompat(
        Account account,
        Instrument instrument,
        OrderAction orderAction,
        OrderType orderType,
        TimeInForce timeInForce,
        int quantity,
        double limitPrice,
        double stopPrice,
        string oco,
        string name)
    {
        var methods = account.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
        MethodInfo? best = null;

        // Prefer the simplest overloads first (fewest parameters)
        foreach (var m in methods)
        {
            if (!string.Equals(m.Name, "CreateOrder", StringComparison.Ordinal))
                continue;

            if (m.ReturnType != typeof(Order))
                continue;

            var ps = m.GetParameters();
            if (ps.Length < 4) // must have at least instrument/action/type/qty-ish
                continue;

            // Keep the smallest param-count as "best" to minimize mismatch risk.
            if (best == null || ps.Length < best.GetParameters().Length)
                best = m;
        }

        if (best == null)
            throw new InvalidOperationException("No compatible Account.CreateOrder() overload found (return type Order).");

        var p = best.GetParameters();
        var args = new object?[p.Length];

        var intCount = 0;
        var doubleCount = 0;
        var stringCount = 0;

        for (var i = 0; i < p.Length; i++)
        {
            var t = p[i].ParameterType;

            if (t == typeof(Instrument))
            {
                args[i] = instrument;
            }
            else if (t == typeof(OrderAction))
            {
                args[i] = orderAction;
            }
            else if (t == typeof(OrderType))
            {
                args[i] = orderType;
            }
            else if (t == typeof(TimeInForce))
            {
                args[i] = timeInForce;
            }
            else if (t == typeof(int))
            {
                // First int is usually quantity; subsequent ints use a safe default (0).
                args[i] = intCount == 0 ? quantity : 0;
                intCount++;
            }
            else if (t == typeof(double))
            {
                // First double typically limitPrice, second stopPrice; remaining doubles default 0.
                args[i] = doubleCount == 0 ? limitPrice : (doubleCount == 1 ? stopPrice : 0.0);
                doubleCount++;
            }
            else if (t == typeof(string))
            {
                // First string typically OCO id, second string name/signal; remaining strings empty.
                args[i] = stringCount == 0 ? oco : (stringCount == 1 ? name : "");
                stringCount++;
            }
            else if (t == typeof(OrderState))
            {
                // Best-effort default: Initialized.
                args[i] = OrderState.Initialized;
            }
            else
            {
                // Unknown/optional parameters: pass null for ref types, default(T) for value types.
                args[i] = t.IsValueType ? Activator.CreateInstance(t) : null;
            }
        }

        var orderObj = best.Invoke(account, args);
        if (orderObj is not Order order)
            throw new InvalidOperationException("Account.CreateOrder() returned null or non-Order.");

        return order;
    }

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

        // NT8: Account types vary by connection/provider. We still fail-closed, but allow:
        // - NT internal sim accounts (e.g., "Sim101")
        // - Provider demo accounts (often prefixed with "DEMO")
        // This prevents the strategy from disabling itself when running on a demo feed/account.
        var accountName = account.Name ?? "";
        var isAllowedSimulationAccount =
            accountName.StartsWith("Sim", StringComparison.OrdinalIgnoreCase) ||
            accountName.StartsWith("DEMO", StringComparison.OrdinalIgnoreCase);

        if (!isAllowedSimulationAccount)
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
            var limitPrice = entryPrice.HasValue ? (double)entryPrice.Value : 0.0;
            var stopPrice = 0.0;
            var oco = ""; // entry is not OCO-linked
            var name = RobotOrderIds.EncodeTag(intentId); // robot-owned envelope (NT8 uses Order.Name, not Tag)

            var order = CreateOrderCompat(
                account,
                ntInstrument,
                orderAction,
                orderType,
                TimeInForce.Day,
                quantity,
                limitPrice,
                stopPrice,
                oco,
                name);
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
                IsEntryOrder = true,
                FilledQuantity = 0
            };
            _orderMap[intentId] = orderInfo;

            // NT8: Submit() often returns void in NinjaScript; rely on OrderUpdate callbacks for final state.
            account.Submit(new[] { order });
            var acknowledgedAt = DateTimeOffset.UtcNow;

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
                note = "Submitted via Account.Submit(); awaiting OrderUpdate for final state"
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
        if (order == null) return;

        // NT8: use Order.Name as the robot-owned "tag" field
        var encodedTag = order.Name ?? "";
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
        }
        else if (orderState == OrderState.Rejected)
        {
            // NT8 Order does not always expose ErrorMessage; keep it generic.
            _executionJournal.RecordRejection(intentId, "", "", "ORDER_REJECTED", utcNow);
            orderInfo.State = "REJECTED";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_REJECTED",
                new { broker_order_id = order.OrderId }));
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
        // Disambiguate: NinjaTrader also has an Execution namespace; we want the Cbi.Execution type.
        var execution = executionObj as NinjaTrader.Cbi.Execution;
        var order = orderObj as Order;
        if (execution == null || order == null) return;

        var encodedTag = order.Name ?? "";
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

        // Update ExecutionJournal: PARTIAL_FILL or FILLED (use cumulative quantity for safety)
        if (filledTotal < orderInfo.Quantity)
        {
            // Partial fill
            _executionJournal.RecordFill(intentId, "", "", fillPrice, filledTotal, utcNow);
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
            _executionJournal.RecordFill(intentId, "", "", fillPrice, filledTotal, utcNow);
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

        // STEP 4: Protective submission must fire for entry intents (ENTRY and ENTRY_STOP)
        // Partial-fill rule: never allow filled position without a stop; protect filled qty immediately.
        if (orderInfo.IsEntryOrder && _intentMap.TryGetValue(intentId, out var entryIntent))
        {
            // Ensure we protect the currently filled quantity (no market-close gating)
            HandleEntryFill(intentId, entryIntent, fillPrice, filledTotal, utcNow);
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
                (o.Name ?? "") == stopTag &&
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
                    // NT8: Change() often returns void in NinjaScript; rely on OrderUpdate for final state.
                    account.Change(new[] { existingStop });
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
            var order = CreateOrderCompat(
                account,
                ntInstrument,
                orderAction,
                OrderType.StopMarket,
                TimeInForce.Day,
                quantity,
                0.0, // limitPrice (unused for StopMarket)
                (double)stopPrice,
                "",  // OCO (set later when paired)
                stopTag);
            order.TimeInForce = TimeInForce.Day;

            // Submit (event-driven confirmation via OrderUpdate)
            account.Submit(new[] { order });

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
                (o.Name ?? "") == targetTag &&
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
                    // NT8: Change() often returns void in NinjaScript; rely on OrderUpdate for final state.
                    account.Change(new[] { existingTarget });
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
            var order = CreateOrderCompat(
                account,
                ntInstrument,
                orderAction,
                OrderType.Limit,
                TimeInForce.Day,
                quantity,
                (double)targetPrice,
                0.0, // stopPrice
                "",  // OCO (set later when paired)
                targetTag);
            order.TimeInForce = TimeInForce.Day;

            // Submit (event-driven confirmation via OrderUpdate)
            account.Submit(new[] { order });

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
                (o.Name ?? "") == stopTag &&
                (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

            if (stopOrder == null)
            {
                var error = "Stop order not found for BE modification";
                return OrderModificationResult.FailureResult(error, utcNow);
            }

            // Real NT API: Modify stop price
            stopOrder.StopPrice = (double)beStopPrice;
            // NT8: Change() often returns void in NinjaScript; rely on OrderUpdate for final state.
            account.Change(new[] { stopOrder });

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
                        Tag = order.Name ?? "",
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
                
                var tag = order.Name ?? "";
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

            // NT8 API: Create stop-market entry (breakout stop entry)
            var name = RobotOrderIds.EncodeTag(intentId);
            var oco = ocoGroup ?? "";
            var order = CreateOrderCompat(
                account,
                ntInstrument,
                orderAction,
                OrderType.StopMarket,
                TimeInForce.Day,
                quantity,
                0.0, // limitPrice (unused for StopMarket)
                (double)stopPrice,
                oco,
                name);
            order.TimeInForce = TimeInForce.Day;

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
                IsEntryOrder = true,
                FilledQuantity = 0
            };
            _orderMap[intentId] = orderInfo;

            // Submit (event-driven confirmation via OrderUpdate)
            account.Submit(new[] { order });
            var acknowledgedAt = DateTimeOffset.UtcNow;

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
                note = "Submitted via Account.Submit(); awaiting OrderUpdate for final state"
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

#endif
