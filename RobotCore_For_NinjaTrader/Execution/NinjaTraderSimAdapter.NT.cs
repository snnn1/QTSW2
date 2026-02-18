// NinjaTrader-specific implementation using real NT APIs
// This file is compiled only when NINJATRADER is defined (inside NT Strategy context)

// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

#if NINJATRADER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CSharp.RuntimeBinder;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Real NinjaTrader API implementation for SIM adapter.
/// This partial class provides real NT API calls when running inside NinjaTrader.
/// </summary>
public sealed partial class NinjaTraderSimAdapter
{
    // Note: Rate-limiting fields (_lastInstrumentMismatchLogUtc, INSTRUMENT_MISMATCH_RATE_LIMIT_MINUTES, 
    // _lastInstrumentMismatchDiagLogUtc) are defined in the base class file (NinjaTraderSimAdapter.cs)

    /// <summary>
    /// Helper method to safely get order tag/name using dynamic typing.
    /// </summary>
    private static string? GetOrderTag(Order order)
    {
        dynamic dynOrder = order;
        try
        {
            return dynOrder.Tag as string ?? dynOrder.Name as string;
        }
        catch
        {
            return dynOrder.Name as string;
        }
    }

    /// <summary>
    /// Helper method to safely set order tag/name using dynamic typing.
    /// </summary>
    private static void SetOrderTag(Order order, string tag)
    {
        dynamic dynOrder = order;
        try
        {
            dynOrder.Tag = tag;
        }
        catch
        {
            dynOrder.Name = tag;
        }
    }

    // IIEAOrderExecutor implementation (Phase 2: IEA delegates order ops to adapter)
    object IIEAOrderExecutor.CreateStopMarketOrder(string instrument, string direction, int quantity, decimal stopPrice, string tag, string? ocoGroup)
    {
        var account = _ntAccount as Account;
        var ntInstrument = _ntInstrument as Instrument;
        if (account == null || ntInstrument == null) throw new InvalidOperationException("NT context not set");
        var orderAction = direction == "Long" ? OrderAction.Buy : OrderAction.SellShort;
        var order = account.CreateOrder(ntInstrument, orderAction, OrderType.StopMarket, OrderEntry.Manual, TimeInForce.Day,
            quantity, 0.0, (double)stopPrice, ocoGroup ?? "", tag, DateTime.MinValue, null);
        SetOrderTag(order, tag);
        if (!string.IsNullOrEmpty(ocoGroup)) order.Oco = ocoGroup;
        return order;
    }

    void IIEAOrderExecutor.CancelOrders(IReadOnlyList<object> orders)
    {
        var account = _ntAccount as Account;
        if (account == null || orders == null) return;
        var ntOrders = orders.OfType<Order>().ToArray();
        if (ntOrders.Length > 0) account.Cancel(ntOrders);
    }

    void IIEAOrderExecutor.SubmitOrders(IReadOnlyList<object> orders)
    {
        var account = _ntAccount as Account;
        if (account == null || orders == null) return;
        var ntOrders = orders.OfType<Order>().ToArray();
        if (ntOrders.Length > 0) account.Submit(ntOrders);
    }

    void IIEAOrderExecutor.SetOrderTag(object order, string tag) => SetOrderTag(order as Order, tag);

    string IIEAOrderExecutor.GetOrderTag(object order) => GetOrderTag(order as Order) ?? "";

    string IIEAOrderExecutor.GetOrderId(object order) => (order as Order)?.OrderId ?? "";

    void IIEAOrderExecutor.RecordSubmission(string intentId, string tradingDate, string stream, string instrument, string orderType, string brokerOrderId, DateTimeOffset utcNow) =>
        _executionJournal.RecordSubmission(intentId, tradingDate, stream, instrument, orderType, brokerOrderId, utcNow);

    (string, string, decimal?, decimal?, decimal?, string?, string?) IIEAOrderExecutor.GetIntentInfo(string intentId)
    {
        var (td, stream, ep, sp, tp, dir, oco) = GetIntentInfo(intentId);
        return (td, stream, ep, sp, tp, dir, oco);
    }

    object IIEAOrderExecutor.GetInstrument() => _ntInstrument!;

    object IIEAOrderExecutor.GetAccount() => _ntAccount!;

    OrderSubmissionResult IIEAOrderExecutor.SubmitProtectiveStop(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow) =>
        SubmitProtectiveStop(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);

    OrderSubmissionResult IIEAOrderExecutor.SubmitTargetOrder(string intentId, string instrument, string direction, decimal targetPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow) =>
        SubmitTargetOrder(intentId, instrument, direction, targetPrice, quantity, ocoGroup, utcNow);

    bool IIEAOrderExecutor.CanSubmitExit(string intentId, int quantity) =>
        _coordinator == null || _coordinator.CanSubmitExit(intentId, quantity);

    bool IIEAOrderExecutor.HasWorkingProtectivesForIntent(string intentId)
    {
        var account = _ntAccount as Account;
        if (account?.Orders == null) return false;
        var stopTag = RobotOrderIds.EncodeStopTag(intentId);
        var targetTag = RobotOrderIds.EncodeTargetTag(intentId);
        bool hasStop = false, hasTarget = false;
        foreach (Order o in account.Orders)
        {
            var tag = GetOrderTag(o);
            if (string.Equals(tag, stopTag, StringComparison.OrdinalIgnoreCase) &&
                (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted))
                hasStop = true;
            if (string.Equals(tag, targetTag, StringComparison.OrdinalIgnoreCase) &&
                (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted))
                hasTarget = true;
            if (hasStop && hasTarget) return true;
        }
        return hasStop && hasTarget;
    }

    (decimal? stopPrice, decimal? targetPrice) IIEAOrderExecutor.GetWorkingProtectivePrices(string intentId)
    {
        var (sp, tp, _, _) = ((IIEAOrderExecutor)this).GetWorkingProtectiveState(intentId);
        return (sp, tp);
    }

    (decimal? stopPrice, decimal? targetPrice, int? stopQty, int? targetQty) IIEAOrderExecutor.GetWorkingProtectiveState(string intentId)
    {
        var account = _ntAccount as Account;
        if (account?.Orders == null) return (null, null, null, null);
        var stopTag = RobotOrderIds.EncodeStopTag(intentId);
        var targetTag = RobotOrderIds.EncodeTargetTag(intentId);
        decimal? stopPrice = null, targetPrice = null;
        int? stopQty = null, targetQty = null;
        foreach (Order o in account.Orders)
        {
            var tag = GetOrderTag(o);
            if ((o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted)) continue;
            if (string.Equals(tag, stopTag, StringComparison.OrdinalIgnoreCase))
            {
                stopPrice = (decimal)o.StopPrice;
                stopQty = o.Quantity;
            }
            if (string.Equals(tag, targetTag, StringComparison.OrdinalIgnoreCase))
            {
                targetPrice = (decimal)o.LimitPrice;
                targetQty = o.Quantity;
            }
        }
        return (stopPrice, targetPrice, stopQty, targetQty);
    }

    bool IIEAOrderExecutor.IsExecutionAllowed() =>
        _isExecutionAllowedCallback == null || _isExecutionAllowedCallback();

    IReadOnlyList<(string intentId, Intent intent, decimal beTriggerPrice, decimal entryPrice, decimal? actualFillPrice, string direction)> IIEAOrderExecutor.GetActiveIntentsForBEMonitoring(string? executionInstrument) =>
        GetActiveIntentsForBEMonitoring(executionInstrument);

    OrderModificationResult IIEAOrderExecutor.ModifyStopToBreakEven(string intentId, string instrument, decimal beStopPrice, DateTimeOffset utcNow) =>
        ModifyStopToBreakEven(intentId, instrument, beStopPrice, utcNow);

    decimal IIEAOrderExecutor.GetTickSize()
    {
        var inst = _ntInstrument as Instrument;
        return inst?.MasterInstrument != null ? (decimal)inst.MasterInstrument.TickSize : 0.25m;
    }

    FlattenResult IIEAOrderExecutor.Flatten(string intentId, string instrument, DateTimeOffset utcNow) =>
        Flatten(intentId, instrument, utcNow);

    void IIEAOrderExecutor.StandDownStream(string streamId, DateTimeOffset utcNow, string reason) =>
        _standDownStreamCallback?.Invoke(streamId, utcNow, reason);

    void IIEAOrderExecutor.ProcessExecutionUpdate(object execution, object order) =>
        HandleExecutionUpdateReal(execution, order);

    void IIEAOrderExecutor.ProcessOrderUpdate(object order, object orderUpdate) =>
        HandleOrderUpdateReal(order, orderUpdate);

    void IIEAOrderExecutor.FailClosed(string intentId, Intent intent, string failureReason, string eventType, string notificationKey, string notificationTitle, string notificationMessage, OrderSubmissionResult? stopResult, OrderSubmissionResult? targetResult, object? additionalData, DateTimeOffset utcNow) =>
        FailClosed(intentId, intent, failureReason, eventType, notificationKey, notificationTitle, notificationMessage, stopResult, targetResult, additionalData, utcNow);

    /// <summary>
    /// Check if executionInstrument matches the strategy's NinjaTrader Instrument.
    /// Handles both root-only names (e.g., "MGC") and full contract names (e.g., "MGC 04-26").
    /// If executionInstrument is root-only, compares to strategy instrument root.
    /// If executionInstrument includes contract month, requires exact match.
    /// </summary>
    private bool IsStrategyExecutionInstrument(string executionInstrument)
    {
        if (_ntInstrument == null)
            return false;
        
        var strategyInstrument = _ntInstrument as Instrument;
        if (strategyInstrument == null)
            return false;
        
        var trimmedInstrument = executionInstrument?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedInstrument))
            return false;
        
        var strategyFullName = strategyInstrument.FullName ?? "";
        var strategyRoot = strategyFullName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        
        // CRITICAL FIX: Check if executionInstrument is root-only (no space = no contract month)
        // If it's root-only, compare to strategy instrument root
        var executionParts = trimmedInstrument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var isRootOnly = executionParts.Length == 1;
        
        if (isRootOnly)
        {
            // Root-only comparison: "MGC" matches "MGC 04-26"
            return string.Equals(strategyRoot, trimmedInstrument, StringComparison.OrdinalIgnoreCase);
        }
        
        // Full contract name provided: require exact match
        try
        {
            var resolvedInstrument = Instrument.GetInstrument(trimmedInstrument);
            if (resolvedInstrument == null)
            {
                // Resolution failed - compare strings directly
                return string.Equals(strategyFullName, trimmedInstrument, StringComparison.OrdinalIgnoreCase);
            }
            
            // Compare Instrument instances (reference equality or FullName match)
            return ReferenceEquals(resolvedInstrument, strategyInstrument) || 
                   string.Equals(resolvedInstrument.FullName, strategyFullName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // If resolution throws, fall back to string comparison
            return string.Equals(strategyFullName, trimmedInstrument, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Helper method to get Intent info for journal logging.
    /// Returns tradingDate, stream, and intent prices from IntentMap if available.
    /// </summary>
    private (string tradingDate, string stream, decimal? entryPrice, decimal? stopPrice, decimal? targetPrice, string? direction, string? ocoGroup) GetIntentInfo(string intentId)
    {
        if (IntentMap.TryGetValue(intentId, out var intent))
        {
            return (intent.TradingDate, intent.Stream, intent.EntryPrice, intent.StopPrice, intent.TargetPrice, intent.Direction, null);
        }
        return ("", "", null, null, null, null, null);
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

        // Assert: account is SIM account (check by name pattern since IsSimAccount may not exist in all NT versions)
        var accountNameUpper = account.Name.ToUpperInvariant();
        var isSimAccount = accountNameUpper.Contains("SIM") || 
                          accountNameUpper.Contains("SIMULATION") ||
                          accountNameUpper.Contains("DEMO");
        if (!isSimAccount)
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
        if (OrderMap.TryGetValue(intentId, out var existingOrder))
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
            if (!IntentPolicy.TryGetValue(intentId, out var expectation))
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
            var filledQty = OrderMap.TryGetValue(intentId, out var existingOrderInfo) ? existingOrderInfo.FilledQuantity : 0;
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
                        null,                                   // Oco (entry orders from SubmitEntryOrderReal don't have ocoGroup)
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
                        OrderMap.TryRemove(intentId, out _);
                        
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
                    OrderMap.TryRemove(intentId, out _);
                    
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
            
            // Copy policy expectation from IntentPolicy if available
            if (IntentPolicy.TryGetValue(intentId, out var expectationForOrder))
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
            
            OrderMap[intentId] = orderInfo;

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
            var finalEntryPrice = intentEntryPrice ?? entryPrice;
            var finalDirection = intentDirection ?? direction;
            
            _executionJournal.RecordSubmission(intentId, tradingDate, stream, instrument, "ENTRY", order.OrderId, acknowledgedAt, 
                expectedEntryPrice: entryPrice, entryPrice: finalEntryPrice, stopPrice: intentStopPrice, 
                targetPrice: intentTargetPrice, direction: finalDirection, ocoGroup: null);

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
        var intentId = RobotOrderIds.DecodeIntentId(encodedTag);
        if (string.IsNullOrEmpty(intentId)) return; // strict: non-robot orders ignored

        var utcNow = DateTimeOffset.UtcNow;
        var orderState = order.OrderState;

        if (!OrderMap.TryGetValue(intentId, out var orderInfo))
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
                if (IntentMap.TryGetValue(intentId, out var intent))
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
                    
                    LogCriticalWithIeaContext(utcNow, intentId, orderInfo.Instrument, "PROTECTIVE_ORDER_REJECTED_FLATTENED",
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
                        });
                }
                else
                {
                    // Intent not found - log error but still log rejection
                    LogCriticalWithIeaContext(utcNow, intentId, orderInfo.Instrument, "PROTECTIVE_ORDER_REJECTED_INTENT_NOT_FOUND",
                        new
                        {
                            intent_id = intentId,
                            instrument = orderInfo.Instrument,
                            order_type = orderInfo.OrderType,
                            broker_order_id = order.OrderId,
                            error = fullErrorMsg,
                            note = "Protective order rejected but intent not found - cannot flatten (orphan rejection)"
                        });
                    
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
        out IntentContext context)
    {
        context = default;
        
        if (!IntentMap.TryGetValue(intentId, out var intent))
        {
            LogOrphanFill(intentId, encodedTag, orderType, instrument, fillPrice, fillQuantity, 
                utcNow, reason: "INTENT_NOT_FOUND");
            
            // Stand down stream if known, otherwise block instrument
            // Try to get stream from orderInfo if available, but likely unknown
            _standDownStreamCallback?.Invoke("", utcNow, $"ORPHAN_FILL:INTENT_NOT_FOUND:{intentId}");
            
            // CRITICAL FIX: Flatten position immediately (fail-closed)
            // Entry fill happened but intent not found - position exists but is unprotected
            // Must flatten to prevent unprotected position accumulation
            try
            {
                var flattenResult = Flatten(intentId, instrument, utcNow);
                
                LogCriticalEngineWithIeaContext(utcNow, "", "ORPHAN_FILL_CRITICAL", "ENGINE",
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
                        note = "Intent not found in IntentMap - orphan fill logged, position flattened (fail-closed)"
                    });
                
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
                LogCriticalEngineWithIeaContext(utcNow, "", "ORPHAN_FILL_FLATTEN_EXCEPTION", "ENGINE",
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
                    });
                
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
            _standDownStreamCallback?.Invoke(stream, utcNow, $"ORPHAN_FILL:MISSING_TRADING_DATE:{intentId}");
            return false;
        }
        
        if (string.IsNullOrWhiteSpace(stream))
        {
            LogOrphanFill(intentId, encodedTag, orderType, instrument, fillPrice, fillQuantity, 
                utcNow, reason: "MISSING_STREAM");
            _standDownStreamCallback?.Invoke("", utcNow, $"ORPHAN_FILL:MISSING_STREAM:{intentId}");
            return false;
        }
        
        if (string.IsNullOrWhiteSpace(direction))
        {
            LogOrphanFill(intentId, encodedTag, orderType, instrument, fillPrice, fillQuantity, 
                utcNow, stream, "MISSING_DIRECTION");
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
            var orphanDir = Path.Combine(_projectRoot, "data", "execution_incidents", "orphan_fills");
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
            LogCriticalEngineWithIeaContext(utcNow, stream ?? "", "ORPHAN_FILL_CRITICAL", "ENGINE",
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
                    action_taken = "EXECUTION_BLOCKED",
                    account_name = _iea?.AccountName
                });
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

        // Gap 3: Deduplicate execution callbacks before any state mutation
        if (_useInstrumentExecutionAuthority && _iea != null && _iea.TryMarkAndCheckDuplicate(executionObj, orderObj))
        {
            _log.Write(RobotEvents.ExecutionBase(DateTimeOffset.UtcNow, "", order.Instrument?.MasterInstrument?.Name ?? "", "EXECUTION_DUPLICATE_SKIPPED",
                new { broker_order_id = order.OrderId, note = "Duplicate execution callback skipped (Gap 3 dedup)" }));
            return;
        }

        var encodedTag = GetOrderTag(order);
        var intentId = RobotOrderIds.DecodeIntentId(encodedTag);
        var utcNow = DateTimeOffset.UtcNow;
        
        var fillPrice = (decimal)execution.Price;
        var fillQuantity = execution.Quantity;
        
        // CRITICAL FIX: Determine order type from tag (STOP/TARGET suffix) before looking up in OrderMap
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
        // EXCEPTION: When we call Flatten(), the broker creates a close order with no QTSW2 tag.
        // That fill would be untracked - don't flatten again (would cause redundant flatten cascade).
        if (string.IsNullOrEmpty(intentId))
        {
            var instrument = order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
            
            // Broker flatten recognition: if we recently called Flatten for this instrument,
            // this fill is likely our own flatten order - don't flatten again
            lock (_flattenRecognitionLock)
            {
                if (!string.IsNullOrEmpty(_lastFlattenInstrument) &&
                    string.Equals(_lastFlattenInstrument, instrument, StringComparison.OrdinalIgnoreCase) &&
                    (utcNow - _lastFlattenUtc).TotalSeconds < FLATTEN_RECOGNITION_WINDOW_SECONDS)
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "BROKER_FLATTEN_FILL_RECOGNIZED",
                        new
                        {
                            broker_order_id = order.OrderId,
                            fill_price = fillPrice,
                            fill_quantity = fillQuantity,
                            seconds_since_flatten = (utcNow - _lastFlattenUtc).TotalSeconds,
                            note = "Fill has no QTSW2 tag but we recently called Flatten for this instrument - treating as broker flatten order fill, skipping redundant flatten"
                        }));
                    CheckAllInstrumentsForFlatPositions(utcNow);
                    return;
                }
            }
            
            LogCriticalWithIeaContext(utcNow, "", instrument, "EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL",
                new 
                { 
                    error = "Execution update (fill) received for order with missing/invalid tag - position may exist but is untracked",
                    broker_order_id = order.OrderId,
                    order_tag = encodedTag ?? "NULL",
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    instrument = instrument,
                    action = "FLATTEN_IMMEDIATELY",
                    note = "CRITICAL: Fill happened but can't be tracked. Flattening position immediately (fail-closed) to prevent unprotected accumulation.",
                    account_name = _iea?.AccountName
                });
            
            // CRITICAL: Flatten position immediately (fail-closed)
            // The fill happened in NinjaTrader, so we must flatten to prevent unprotected position
            // Since we don't have intent_id, we'll flatten the entire instrument position
            try
            {
                // Use a dummy intent_id to flatten instrument position
                // FlattenIntentReal flattens the entire instrument anyway, so this works
                var flattenResult = Flatten("UNKNOWN_UNTrackED_FILL", instrument, utcNow);
                LogCriticalWithIeaContext(utcNow, "", instrument, "UNTrackED_FILL_FLATTENED",
                    new
                    {
                        broker_order_id = order.OrderId,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        flatten_success = flattenResult.Success,
                        flatten_error = flattenResult.ErrorMessage,
                        note = "Position flattened due to untracked fill (fail-closed behavior)"
                    });
                
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
                LogCriticalWithIeaContext(utcNow, "", instrument, "UNTrackED_FILL_FLATTEN_FAILED",
                    new
                    {
                        error = ex.Message,
                        exception_type = ex.GetType().Name,
                        broker_order_id = order.OrderId,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        note = "CRITICAL: Failed to flatten untracked fill position - manual intervention required"
                    });
                
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

        // CRITICAL FIX: Handle race condition where fill arrives before order is fully added to OrderMap
        // This can happen when order state is "Initialized" - order is being submitted but fill arrives immediately
        // CRITICAL FIX: Handle protective orders that aren't in OrderMap
        // Protective orders (STOP/TARGET) are not always added to OrderMap, but we can create OrderInfo from tag
        OrderInfo? orderInfo = null;
        bool orderInfoCreatedFromTag = false;
        
        if (!OrderMap.TryGetValue(intentId, out orderInfo))
        {
            // If this is a protective order (detected from tag), create OrderInfo on the fly
            if (isProtectiveOrder && !string.IsNullOrEmpty(orderTypeFromTag) && !string.IsNullOrEmpty(intentId))
            {
                // Get intent to get direction and instrument
                if (IntentMap.TryGetValue(intentId, out var intent))
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
                    orderInfoCreatedFromTag = true;
                    
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "PROTECTIVE_ORDER_FILL_TRACKED_FROM_TAG",
                        new
                        {
                            broker_order_id = order.OrderId,
                            tag = encodedTag,
                            order_type = orderTypeFromTag,
                            intent_id = intentId,
                            fill_price = fillPrice,
                            fill_quantity = fillQuantity,
                            note = "Protective order fill tracked from tag (order not in OrderMap - created OrderInfo on the fly)"
                        }));
                }
            }
        }
        
        // If still no orderInfo, handle as untracked
        if (orderInfo == null)
        {
            var instrument = order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
            var orderState = order.OrderState;

            // MULTI-INSTANCE FIX: When multiple strategy instances run for same instrument (e.g. two MNQ charts),
            // all receive execution callbacks. Only the instance that submitted has the order in OrderMap.
            // If we have NO orders for this instrument, we're the wrong instance - skip (don't flatten).
            // The instance that submitted will process the fill and place protective orders.
            // IEA: When use_instrument_execution_authority is enabled, we use shared maps - wrong-instance skip
            // is demoted to diagnostic only (don't skip) so real unknown fills are not suppressed.
            // CRITICAL: Use IsSameInstrument so NQ (order.MasterInstrument.Name) matches MNQ (orderInfo.Instrument).
            // Matching logic only — does NOT affect IEA registry keying; (account, MNQ) and (account, NQ) remain distinct.
            var hasAnyOrderForInstrument = OrderMap.Values.Any(oi =>
                ExecutionInstrumentResolver.IsSameInstrument(oi.Instrument, instrument));
            if (!hasAnyOrderForInstrument)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument,
                    _useInstrumentExecutionAuthority ? "EXECUTION_UPDATE_WRONG_INSTANCE_DIAGNOSTIC" : "EXECUTION_UPDATE_SKIPPED_WRONG_INSTANCE",
                    new
                    {
                        broker_order_id = order.OrderId,
                        tag = encodedTag,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        iea_enabled = _useInstrumentExecutionAuthority,
                        note = _useInstrumentExecutionAuthority
                            ? "Order not in map and no orders for instrument - IEA enabled so continuing (diagnostic only, not skipping)"
                            : "Order not in map and we have no orders for this instrument - likely wrong instance (multiple charts), skipping to let submitting instance handle"
                    }));
                if (!_useInstrumentExecutionAuthority)
                    return;
            }

            // RACE CONDITION FIX: Retry lookup when order not in map (single-instance MNQ incident).
            // Fill can arrive before order is visible in OrderMap (same-tick sim fill, threading).
            // Previously only retried when orderState==Initialized; now retry for ALL states.
            const int MAX_RETRIES = 5; // Increased from 3 for same-tick fill race
            const int RETRY_DELAY_MS = 50; // Shorter delay, more retries
            bool found = false;
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_UPDATE_ORDER_MAP_RETRY",
                new
                {
                    broker_order_id = order.OrderId,
                    tag = encodedTag,
                    intent_id = intentId,
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    order_state = orderState.ToString(),
                    order_map_count = OrderMap.Count,
                    order_map_intent_ids = OrderMap.Keys.Take(10).ToList(),
                    max_retries = MAX_RETRIES,
                    note = "Order not in map - retrying lookup (race condition fix for single-instance fill)"
                }));
            for (int retry = 0; retry < MAX_RETRIES; retry++)
            {
                if (retry > 0)
                    System.Threading.Thread.Sleep(RETRY_DELAY_MS);

                if (OrderMap.TryGetValue(intentId, out orderInfo))
                {
                    found = true;
                    if (retry > 0)
                    {
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
                    }
                    break;
                }
            }

            if (!found)
                {
                    // Still not found after retries - flatten (fail-closed)
                    LogCriticalWithIeaContext(utcNow, intentId, instrument, "EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL",
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
                            note = "CRITICAL: Fill happened but order not in tracking map after retries. Flattening position immediately (fail-closed) to prevent unprotected accumulation.",
                            account_name = _iea?.AccountName
                        });
                    
                    try
                    {
                        var flattenResult = Flatten(intentId, instrument, utcNow);
                        
                        LogCriticalWithIeaContext(utcNow, intentId, instrument, "UNKNOWN_ORDER_FILL_FLATTENED",
                            new
                            {
                                broker_order_id = order.OrderId,
                                intent_id = intentId,
                                fill_price = fillPrice,
                                fill_quantity = fillQuantity,
                                flatten_success = flattenResult.Success,
                                flatten_error = flattenResult.ErrorMessage,
                                note = "Position flattened due to untracked order fill (fail-closed behavior)"
                            });
                        
                        // Alert if flatten succeeded (info) or failed (critical)
                        var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                        if (notificationService != null)
                        {
                            if (flattenResult.Success)
                            {
                                var title = $"Unknown Order Fill - Position Flattened - {instrument}";
                                var message = $"Fill occurred for order not found in tracking map and position was flattened automatically. Intent: {intentId}, Broker Order ID: {order.OrderId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}";
                                notificationService.EnqueueNotification($"UNKNOWN_ORDER_FILL_FLATTENED:{intentId}", title, message, priority: 1); // Info priority
                            }
                            else
                            {
                                var title = $"CRITICAL: Unknown Order Fill - Flatten FAILED - {instrument}";
                                var message = $"Fill occurred for order not found in tracking map but flatten operation FAILED - MANUAL INTERVENTION REQUIRED. Intent: {intentId}, Broker Order ID: {order.OrderId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}, Error: {flattenResult.ErrorMessage}";
                                notificationService.EnqueueNotification($"UNKNOWN_ORDER_FILL_FLATTEN_FAILED:{intentId}", title, message, priority: 3); // Highest priority
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogCriticalWithIeaContext(utcNow, intentId, instrument, "UNKNOWN_ORDER_FILL_FLATTEN_FAILED",
                            new
                            {
                                error = ex.Message,
                                exception_type = ex.GetType().Name,
                                broker_order_id = order.OrderId,
                                intent_id = intentId,
                                note = "CRITICAL: Failed to flatten untracked order fill position - manual intervention required"
                            });
                        var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                        if (notificationService != null)
                        {
                            var title = $"CRITICAL: Unknown Order Fill - Flatten FAILED - {instrument}";
                            var message = $"Fill occurred for order not found in tracking map but flatten operation FAILED - MANUAL INTERVENTION REQUIRED. Intent: {intentId}, Broker Order ID: {order.OrderId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}, Error: {ex.Message}";
                            notificationService.EnqueueNotification($"UNKNOWN_ORDER_FILL_FLATTEN_FAILED:{intentId}", title, message, priority: 3);
                        }
                    }
                    return; // Fail-closed: don't process untracked fill
                }
        }

        // Track cumulative fills for partial-fill safety
        orderInfo.FilledQuantity += fillQuantity;
        var filledTotal = orderInfo.FilledQuantity;
        
        // Get expectation for fill accounting
        var expectedQty = orderInfo.ExpectedQuantity > 0 ? orderInfo.ExpectedQuantity : 
            (IntentPolicy.TryGetValue(intentId, out var exp) ? exp.ExpectedQuantity : 0);
        var maxQty = orderInfo.MaxQuantity > 0 ? orderInfo.MaxQuantity :
            (IntentPolicy.TryGetValue(intentId, out var exp2) ? exp2.MaxQuantity : 0);
        var remainingQty = expectedQty - filledTotal;
        var overfill = filledTotal > expectedQty;
        
        // Per-fill accounting log
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, 
            "INTENT_FILL_UPDATE", new
        {
            intent_id = intentId,
            fill_qty = fillQuantity,
            cumulative_filled_qty = filledTotal,
            expected_qty = expectedQty,
            max_qty = maxQty,
            remaining_qty = remainingQty,
            overfill = overfill
        }));
        
        if (overfill)
        {
            // Trigger emergency handler
            TriggerQuantityEmergency(intentId, "INTENT_OVERFILL_EMERGENCY", utcNow, new Dictionary<string, object>
            {
                { "expected_qty", expectedQty },
                { "actual_filled_qty", filledTotal },
                { "last_fill_qty", fillQuantity },
                { "reason", "Fill exceeded expected quantity" }
            });
        }
        
        // CRITICAL: Resolve intent context before any journal call
        // Use orderTypeFromTag if available (from tag), otherwise use orderInfo.OrderType
        var orderTypeForContext = orderTypeFromTag ?? orderInfo.OrderType;
        IntentContext context;
        if (!ResolveIntentContextOrFailClosed(intentId, encodedTag, orderTypeForContext, orderInfo.Instrument, 
            fillPrice, fillQuantity, utcNow, out context))
        {
            // Context resolution failed - orphan fill logged and execution blocked
            // Do NOT call journal with empty strings
            return; // Fail-closed
        }
        
        // Explicit Entry vs Exit Classification
        // CRITICAL FIX: Use orderTypeFromTag to determine if it's an entry or exit order
        // Protective orders (STOP/TARGET) are exit orders even if orderInfo.IsEntryOrder is true (from entry order in OrderMap)
        bool isEntryFill = !isProtectiveOrder && orderInfo.IsEntryOrder == true;
        if (isEntryFill)
        {
            // Entry fill - handle aggregated orders (multiple streams, one broker order)
            var intentIdsToUpdate = orderInfo.AggregatedIntentIds ?? new List<string> { context.IntentId };
            var isAggregated = intentIdsToUpdate.Count > 1;

            // Deterministic partial-fill allocation: allocate in lexicographic order, first fill goes to intentIds[0]
            // until its policy qty satisfied, then next. Track cumulative per intent.
            // Phase 2: When IEA enabled, IEA owns allocation; otherwise adapter does it.
            var allocations = (_useInstrumentExecutionAuthority && _iea != null)
                ? _iea.AllocateFillToIntents(intentIdsToUpdate, fillQuantity, orderInfo)
                : AllocateFillToIntents(intentIdsToUpdate, fillQuantity, orderInfo);

            foreach (var alloc in allocations)
            {
                var allocIntentId = alloc.Item1;
                var allocQty = alloc.Item2;
                if (allocQty <= 0) continue;
                if (!IntentMap.TryGetValue(allocIntentId, out Intent? allocIntent) || allocIntent == null) continue;
                var allocContext = new IntentContext
                {
                    IntentId = allocIntentId,
                    TradingDate = allocIntent.TradingDate ?? context.TradingDate,
                    Stream = allocIntent.Stream ?? context.Stream,
                    Direction = allocIntent.Direction ?? context.Direction,
                    ExecutionInstrument = context.ExecutionInstrument,
                    CanonicalInstrument = context.CanonicalInstrument,
                    ContractMultiplier = context.ContractMultiplier,
                    Tag = context.Tag
                };
                _executionJournal.RecordEntryFill(
                    allocContext.IntentId,
                    allocContext.TradingDate,
                    allocContext.Stream,
                    fillPrice,
                    allocQty,
                    utcNow,
                    allocContext.ContractMultiplier,
                    allocContext.Direction,
                    allocContext.ExecutionInstrument,
                    allocContext.CanonicalInstrument);
                _coordinator?.OnEntryFill(allocContext.IntentId, allocQty, allocContext.Stream, allocContext.ExecutionInstrument, allocContext.Direction ?? "", utcNow);
            }

            if (isAggregated && allocations.Count > 0)
            {
                var allocDict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in allocations)
                    allocDict[a.Item1] = a.Item2;
                var cumulative = orderInfo.AggregatedFilledByIntent != null
                    ? new Dictionary<string, int>(orderInfo.AggregatedFilledByIntent, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "AGG_ENTRY_FILL_ALLOCATED",
                    new
                    {
                        fill_qty = fillQuantity,
                        allocations = allocDict,
                        cumulative_qty_per_intent = cumulative,
                        broker_order_id = order.OrderId,
                        note = "Deterministic allocation in lexicographic order for partial fills"
                    }));
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
            
            // Register entry fill with coordinator and HandleEntryFill for protective orders
            if (IntentMap.TryGetValue(intentId, out var entryIntent))
            {
                // CRITICAL FIX: For aggregated orders, we already called OnEntryFill for each intent in the loop above.
                // Do NOT call again for primary - that would double-count.
                if (!isAggregated)
                {
                    _coordinator?.OnEntryFill(intentId, fillQuantity, entryIntent.Stream, entryIntent.Instrument, entryIntent.Direction ?? "", utcNow);
                }
                
                // CRITICAL FIX: Check if position is now flat after entry fill - if so, cancel entry stop orders
                // This handles manual position closures (user clicks "Flatten" in NinjaTrader UI) that happen
                // immediately after entry fills (race condition where user flattens before protective orders submit)
                CheckAndCancelEntryStopsOnPositionFlat(orderInfo.Instrument, utcNow);
                
                // CRITICAL FIX: Pass filledTotal (cumulative) to HandleEntryFill for protective orders
                // HandleEntryFill needs TOTAL filled quantity to submit protective orders that cover the ENTIRE position
                // For incremental fills, protective orders must be updated to cover cumulative position, not just delta
                // filledTotal is already updated: orderInfo.FilledQuantity += fillQuantity (line 1372)
                // Phase 2: When IEA is enabled, IEA owns HandleEntryFill; otherwise adapter handles it
                if (_useInstrumentExecutionAuthority && _iea != null)
                    _iea.HandleEntryFill(intentId, entryIntent, fillPrice, fillQuantity, filledTotal, utcNow);
                else
                    HandleEntryFill(intentId, entryIntent, fillPrice, fillQuantity, filledTotal, utcNow);
            }
            else
            {
                // This should not happen - ResolveIntentContextOrFailClosed already checked
                // But handle defensively
                _log.Write(RobotEvents.EngineBase(utcNow, context.TradingDate, "EXECUTION_ERROR", "ENGINE",
                    new 
                    { 
                        error = "Intent not found in IntentMap after context resolution - defensive check",
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
                
                // Emergency flatten to prevent unprotected position
                try
                {
                    var flattenResult = Flatten(intentId, orderInfo.Instrument, utcNow);
                    
                    LogCriticalEngineWithIeaContext(utcNow, context.TradingDate, "INTENT_NOT_FOUND_FLATTENED", "ENGINE",
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
                        });
                    
                    // Stand down stream
                    _standDownStreamCallback?.Invoke(context.Stream, utcNow, $"INTENT_NOT_FOUND:{intentId}");
                    
                    // Raise high-priority alert
                    var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                    if (notificationService != null)
                    {
                        var title = $"CRITICAL: Intent Not Found - {orderInfo.Instrument}";
                        var message = $"Entry order filled but intent was not registered. Position flattened. Stream: {context.Stream}, Intent: {intentId}";
                        notificationService.EnqueueNotification($"INTENT_NOT_FOUND:{intentId}", title, message, priority: 2); // Emergency priority
                    }
                }
                catch (Exception ex)
                {
                    // Log flatten failure but don't throw - we've already logged the error
                    LogCriticalEngineWithIeaContext(utcNow, context.TradingDate, "INTENT_NOT_FOUND_FLATTEN_FAILED", "ENGINE",
                        new
                        {
                            intent_id = intentId,
                            instrument = orderInfo.Instrument,
                            stream = context.Stream,
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            note = "Failed to flatten position after intent not found - manual intervention may be required"
                        });
                }
            }
        }
        else if (orderTypeForContext == "STOP" || orderTypeForContext == "TARGET")
        {
            // Exit fill
            // CRITICAL FIX: Use orderTypeForContext (from tag) instead of orderInfo.OrderType
            // orderInfo might be from entry order if protective order wasn't added to OrderMap yet
            _executionJournal.RecordExitFill(
                context.IntentId, 
                context.TradingDate, 
                context.Stream,
                fillPrice, 
                fillQuantity,  // DELTA ONLY - not filledTotal
                orderTypeForContext, // Use tag-based order type (ground truth)
                utcNow);
            
            // Log exit fill event
            // CRITICAL FIX: Use orderTypeForContext (from tag) for logging - it's the ground truth
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_EXIT_FILL",
                new
                {
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    filled_total = filledTotal,
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
            // orderInfo might be from entry order if protective order wasn't added to OrderMap yet
            if ((orderTypeForContext == "STOP" || orderTypeForContext == "TARGET") && IntentMap.TryGetValue(intentId, out var filledIntent))
            {
                // Find the opposite entry intent for this stream
                // Entry intents are created in pairs: one Long, one Short with same TradingDate/Stream/SlotTime
                var oppositeDirection = filledIntent.Direction == "Long" ? "Short" : "Long";
                
                // Find opposite intent by searching IntentMap for same stream with opposite direction
                string? oppositeIntentId = null;
                foreach (var kvp in IntentMap)
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
            }
        }
        else
        {
            // Unknown exit type - orphan it
            LogOrphanFill(intentId, encodedTag, orderInfo.OrderType, orderInfo.Instrument, fillPrice, fillQuantity,
                utcNow, context.Stream, "UNKNOWN_EXIT_TYPE");
            
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
            
            // Idempotent: if stop already exists, ensure it matches desired stop/qty
            var stopTag = RobotOrderIds.EncodeStopTag(intentId);
            var existingStop = account.Orders.FirstOrDefault(o =>
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
                    if (changeRes == null || changeRes.Length == 0 || changeRes[0].OrderState == OrderState.Rejected)
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

            // CRITICAL FIX: Add protective stop order to OrderMap so it can be tracked when it fills
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
            OrderMap[intentId] = stopOrderInfo; // Use same intentId - OnExecutionUpdate will find it by tag decode
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = order.OrderId,
                order_type = "PROTECTIVE_STOP",
                direction,
                stop_price = stopPrice,
                quantity,
                account = "SIM",
                note = "Protective stop order added to OrderMap for tracking"
            }));

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
            var existingTarget = account.Orders.FirstOrDefault(o =>
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
                    if (changeRes == null || changeRes.Length == 0 || changeRes[0].OrderState == OrderState.Rejected)
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

            // CRITICAL FIX: Add protective target order to OrderMap so it can be tracked when it fills
            // Without this, protective target fills are treated as untracked and trigger flatten operations
            // Note: This overwrites entry order in OrderMap, but that's OK because:
            // - Entry order is already filled by the time protective orders are submitted
            // - Entry fills are tracked in execution journal (ground truth)
            // - Tag-based detection provides fallback if OrderMap lookup fails
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
            OrderMap[intentId] = targetOrderInfo; // Overwrites entry order (already filled) or stop order (if stop was added first)
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = order.OrderId,
                order_type = "TARGET",
                direction,
                target_price = targetPrice,
                quantity,
                account = "SIM",
                note = "Protective target order added to OrderMap for tracking"
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

    // Phase 3: BE evaluation state (non-IEA path)
    private readonly Dictionary<string, DateTimeOffset> _lastBeModifyAttemptUtcByIntent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _beModifyFailureCountByIntent = new(StringComparer.OrdinalIgnoreCase);
    private const double BE_MODIFY_ATTEMPT_INTERVAL_MS = 200;
    private const int BE_MODIFY_MAX_RETRIES = 25;
    private const double BE_SCAN_THROTTLE_MS = 200;
    private DateTimeOffset _lastBeCheckUtcNonIEA = DateTimeOffset.MinValue;

    private void EvaluateBreakEvenNonIEA(decimal tickPrice, DateTimeOffset utcNow, string executionInstrument)
    {
        if ((utcNow - _lastBeCheckUtcNonIEA).TotalMilliseconds < BE_SCAN_THROTTLE_MS)
            return;
        _lastBeCheckUtcNonIEA = utcNow;

        var activeIntents = GetActiveIntentsForBEMonitoring(executionInstrument);
        if (activeIntents.Count == 0) return;

        decimal tickSize = 0.25m;
        try
        {
            var inst = _ntInstrument as Instrument;
            if (inst?.MasterInstrument != null)
                tickSize = (decimal)inst.MasterInstrument.TickSize;
        }
        catch { }

        foreach (var (intentId, intent, beTriggerPrice, entryPrice, actualFillPrice, direction) in activeIntents)
        {
            bool beTriggerReached = direction == "Long" ? tickPrice >= beTriggerPrice : direction == "Short" ? tickPrice <= beTriggerPrice : false;
            if (!beTriggerReached) continue;

            var breakoutLevel = entryPrice;
            var beStopPrice = direction == "Long" ? breakoutLevel - tickSize : breakoutLevel + tickSize;

            if ((utcNow - (_lastBeModifyAttemptUtcByIntent.TryGetValue(intentId, out var lastAttempt) ? lastAttempt : DateTimeOffset.MinValue)).TotalMilliseconds < BE_MODIFY_ATTEMPT_INTERVAL_MS)
                continue;
            _lastBeModifyAttemptUtcByIntent[intentId] = utcNow;

            var modifyResult = ModifyStopToBreakEven(intentId, intent.Instrument ?? "", beStopPrice, utcNow);

            if (modifyResult.Success)
            {
                _lastBeModifyAttemptUtcByIntent.Remove(intentId);
                _beModifyFailureCountByIntent.Remove(intentId);
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
                    _beModifyFailureCountByIntent.Remove(intentId);
                    _lastBeModifyAttemptUtcByIntent.Remove(intentId);

                    if (stopMissing)
                        Flatten(intentId, intent.Instrument ?? "", utcNow);
                    else
                        _standDownStreamCallback?.Invoke(intent.Stream ?? "", utcNow, $"BE_MODIFY_MAX_RETRIES:{intentId}");
                }
            }
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
                GetOrderTag(o) == stopTag &&
                (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

            if (stopOrder == null)
            {
                var error = "Stop order not found for BE modification";
                return OrderModificationResult.FailureResult(error, utcNow);
            }
            // CRITICAL: Do NOT record BE when stop not found — retry later. RecordBEModification only below.

            // Only tighten: never move stop backward (e.g. if trailing logic already moved it tighter)
            var currentStop = (decimal)stopOrder.StopPrice;
            var (_, _, _, _, _, intentDirection, _) = GetIntentInfo(intentId);
            var stopAlreadyTighter = intentDirection == "Long"
                ? currentStop >= beStopPrice   // Long: current stop at or above BE = already tighter
                : currentStop <= beStopPrice;  // Short: current stop at or below BE = already tighter
            if (stopAlreadyTighter)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "BE_SKIP_STOP_ALREADY_TIGHTER", new
                {
                    current_stop = currentStop,
                    be_stop_price = beStopPrice,
                    direction = intentDirection,
                    note = "Stop already at or tighter than BE - skip modification (idempotent)"
                }));
                // Record as modified so we don't retry. Safe: we only reach here after confirming valid stop
                // order exists (found by intent tag QTSW2:{intentId}:STOP) and comparison is meaningful.
                var (tradingDateSkip, streamSkip, intentEntrySkip, _, _, _, _) = GetIntentInfo(intentId);
                decimal? beTriggerSkip = null;
                if (IntentMap.TryGetValue(intentId, out var skipIntent))
                    beTriggerSkip = skipIntent.BeTrigger;
                _executionJournal.RecordBEModification(intentId, tradingDateSkip ?? "", streamSkip ?? "", currentStop, utcNow,
                    previousStopPrice: currentStop, beTriggerPrice: beTriggerSkip, entryPrice: intentEntrySkip);
                return OrderModificationResult.SuccessResult(utcNow);
            }

            // Real NT API: Modify stop price
            stopOrder.StopPrice = (double)beStopPrice;
            dynamic dynAccountModify = account;
            Order[]? result = null;
            try
            {
                object? changeResult = dynAccountModify.Change(new[] { stopOrder });
                if (changeResult != null && changeResult is Order[] changeArray)
                {
                    result = changeArray;
                }
            }
            catch (Exception ex)
            {
                // Change() call failed - log and attempt fallback
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CHANGE_FALLBACK", new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    order_type = "STOP_BREAK_EVEN",
                    broker_order_id = stopOrder.OrderId,
                    note = "Change() call failed for BE modification, attempting fallback (Change returns void)"
                }));
                
                // Fallback: Change returns void - check order state directly
                try
                {
                    // Try calling Change() again (void return)
                    dynAccountModify.Change(new[] { stopOrder });
                    result = new[] { stopOrder };
                }
                catch (Exception fallbackEx)
                {
                    // Both attempts failed - reject modification
                    var errorMsg = $"BE modification failed: {ex.Message} (fallback also failed: {fallbackEx.Message})";
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CHANGE_FAIL", new
                    {
                        error = errorMsg,
                        first_error = ex.Message,
                        fallback_error = fallbackEx.Message,
                        order_type = "STOP_BREAK_EVEN",
                        broker_order_id = stopOrder.OrderId,
                        account = "SIM",
                        exception_type = ex.GetType().Name,
                        fallback_exception_type = fallbackEx.GetType().Name
                    }));
                    return OrderModificationResult.FailureResult(errorMsg, utcNow);
                }
            }

            if (result == null || result.Length == 0 || result[0].OrderState == OrderState.Rejected)
            {
                dynamic dynResult = result?[0];
                var error = (string?)dynResult?.ErrorMessage ?? (string?)dynResult?.Error ?? "BE modification rejected";
                return OrderModificationResult.FailureResult(error, utcNow);
            }

            // Get Intent and previous stop price for BE modification context
            var (tradingDate8, stream8, intentEntryPrice2, intentStopPrice3, intentTargetPrice3, _, _) = GetIntentInfo(intentId);
            decimal? previousStopPrice = null;
            decimal? beTriggerPrice = null;
            
            // Get previous stop price from journal entry
            var journalPath = System.IO.Path.Combine(_projectRoot, "data", "execution_journals", $"{tradingDate8}_{stream8}_{intentId}.json");
            if (System.IO.File.Exists(journalPath))
            {
                try
                {
                    var journalJson = System.IO.File.ReadAllText(journalPath);
                    var journalEntry = QTSW2.Robot.Core.JsonUtil.Deserialize<ExecutionJournalEntry>(journalJson);
                    if (journalEntry != null && journalEntry.StopPrice.HasValue)
                    {
                        previousStopPrice = journalEntry.StopPrice.Value;
                    }
                    if (journalEntry != null && journalEntry.BEStopPrice.HasValue)
                    {
                        // If BE was already modified, use the previous BE stop price
                        previousStopPrice = journalEntry.BEStopPrice.Value;
                    }
                }
                catch
                {
                    // Ignore errors reading journal
                }
            }
            
            // Get BE trigger price from Intent
            if (IntentMap.TryGetValue(intentId, out var beIntent))
            {
                beTriggerPrice = beIntent.BeTrigger;
            }
            
            _executionJournal.RecordBEModification(intentId, tradingDate8, stream8, beStopPrice, utcNow, 
                previousStopPrice: previousStopPrice, beTriggerPrice: beTriggerPrice, entryPrice: intentEntryPrice2);

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
                        Tag = GetOrderTag(order),
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
    /// GC FIX: Cancel protective orders (stop and target) for an intent when quantity changes are needed.
    /// This is used when updating existing protective orders fails - we cancel and recreate them.
    /// </summary>
    private void CancelProtectiveOrdersForIntent(string intentId, DateTimeOffset utcNow)
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
            // Find protective orders (stop and target) for this intent
            var stopTag = RobotOrderIds.EncodeStopTag(intentId);
            var targetTag = RobotOrderIds.EncodeTargetTag(intentId);
            
            foreach (var order in account.Orders)
            {
                if (order.OrderState != OrderState.Working && order.OrderState != OrderState.Accepted)
                {
                    continue;
                }
                
                var tag = GetOrderTag(order) ?? "";
                
                // Match stop or target tag
                if (tag == stopTag || tag == targetTag)
                {
                    ordersToCancel.Add(order);
                }
            }
            
            if (ordersToCancel.Count > 0)
            {
                // Real NT API: Cancel orders
                account.Cancel(ordersToCancel.ToArray());
                
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", "PROTECTIVE_ORDERS_CANCELLED_FOR_RECREATE", new
                {
                    intent_id = intentId,
                    cancelled_count = ordersToCancel.Count,
                    cancelled_order_ids = ordersToCancel.Select(o => o.OrderId).ToList(),
                    cancelled_tags = ordersToCancel.Select(o => GetOrderTag(o)).ToList(),
                    note = "Cancelled protective orders to recreate with correct quantity (quantity change requires cancel/recreate)"
                }));
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", "PROTECTIVE_ORDERS_CANCEL_ERROR", new
            {
                intent_id = intentId,
                error = ex.Message,
                exception_type = ex.GetType().Name,
                note = "Failed to cancel protective orders - may cause duplicate order issues"
            }));
        }
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
            // CRITICAL FIX: Only cancel entry orders, not protective orders
            // Protective orders should be managed via OCO groups (when one fills, OCO cancels the other)
            // If we cancel protective orders here, we could leave positions unprotected
            foreach (var order in account.Orders)
            {
                if (order.OrderState != OrderState.Working && order.OrderState != OrderState.Accepted)
                {
                    continue;
                }
                
                var tag = GetOrderTag(order) ?? "";
                var decodedIntentId = RobotOrderIds.DecodeIntentId(tag);
                
                // Match intent ID AND ensure it's an entry order (not STOP/TARGET)
                // Entry orders: QTSW2:{intentId}
                // Protective orders: QTSW2:{intentId}:STOP or QTSW2:{intentId}:TARGET
                if (decodedIntentId == intentId && 
                    !tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase) && 
                    !tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase))
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
                    var tag = GetOrderTag(order) ?? "";
                    var decodedIntentId = RobotOrderIds.DecodeIntentId(tag);
                    if (decodedIntentId == intentId && OrderMap.TryGetValue(intentId, out var orderInfo))
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
            }
            
            // CRITICAL FIX: Defensively cancel opposite entry stop order to prevent re-entry
            // This handles cases where cancellation is called but opposite entry wasn't explicitly cancelled
            if (IntentMap.TryGetValue(intentId, out var cancelledIntent))
            {
                // Find the opposite entry intent for this stream
                var oppositeDirection = cancelledIntent.Direction == "Long" ? "Short" : "Long";
                string? oppositeIntentId = null;
                
                foreach (var kvp in IntentMap)
                {
                    var otherIntent = kvp.Value;
                    if (otherIntent.Stream == cancelledIntent.Stream &&
                        otherIntent.TradingDate == cancelledIntent.TradingDate &&
                        otherIntent.Direction == oppositeDirection &&
                        otherIntent.TriggerReason != null &&
                        (otherIntent.TriggerReason.Contains("ENTRY_STOP_BRACKET_LONG") ||
                         otherIntent.TriggerReason.Contains("ENTRY_STOP_BRACKET_SHORT")))
                    {
                        // Check if opposite entry hasn't filled yet
                        var oppositeEntryFilled = false;
                        if (_executionJournal != null && !string.IsNullOrEmpty(otherIntent.TradingDate) && !string.IsNullOrEmpty(otherIntent.Stream))
                        {
                            var oppositeEntry = _executionJournal.GetEntry(kvp.Key, otherIntent.TradingDate, otherIntent.Stream);
                            oppositeEntryFilled = oppositeEntry != null && (oppositeEntry.EntryFilled || oppositeEntry.EntryFilledQuantityTotal > 0);
                        }
                        
                        if (!oppositeEntryFilled)
                        {
                            oppositeIntentId = kvp.Key;
                            break;
                        }
                    }
                }
                
                // Cancel opposite entry if found and not filled
                if (oppositeIntentId != null && oppositeIntentId != intentId)
                {
                    try
                    {
                        var oppositeCancelled = CancelIntentOrdersReal(oppositeIntentId, utcNow);
                        if (oppositeCancelled)
                        {
                            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "OPPOSITE_ENTRY_CANCELLED_DEFENSIVELY", state: "ENGINE",
                                new
                                {
                                    cancelled_intent_id = intentId,
                                    opposite_intent_id = oppositeIntentId,
                                    stream = cancelledIntent.Stream,
                                    note = "Defensively cancelled opposite entry stop order when cancelling intent to prevent re-entry"
                                }));
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail - defensive cancellation failure is not critical
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "OPPOSITE_ENTRY_CANCEL_DEFENSIVE_FAILED", state: "ENGINE",
                            new
                            {
                                cancelled_intent_id = intentId,
                                opposite_intent_id = oppositeIntentId,
                                error = ex.Message,
                                note = "Failed to defensively cancel opposite entry - may cause re-entry"
                            }));
                    }
                }
            }
            
            return true; // Success (even if no orders to cancel)
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
            // CRITICAL FIX: Add null checks to prevent NullReferenceException
            // Get position for this instrument - use dynamic to handle different API signatures
            dynamic dynAccountFlatten = account;
            Position? position = null;
            string? instrumentName = null;
            
            // Safely get instrument name
            try
            {
                if (ntInstrument?.MasterInstrument != null)
                {
                    instrumentName = ntInstrument.MasterInstrument.Name;
                }
                else if (ntInstrument != null)
                {
                    // Fallback if MasterInstrument is null
                    instrumentName = ntInstrument.ToString();
                }
            }
            catch
            {
                // If we can't get instrument name, use instrument symbol from parameter
                instrumentName = instrument;
            }
            
            // Try to get position
            try
            {
                if (ntInstrument != null)
                {
                    position = dynAccountFlatten.GetPosition(ntInstrument);
                }
            }
            catch
            {
                // Try alternative signature - GetPosition might take instrument name string
                try
                {
                    if (!string.IsNullOrEmpty(instrumentName))
                    {
                        position = dynAccountFlatten.GetPosition(instrumentName);
                    }
                }
                catch
                {
                    // If GetPosition fails, try to flatten anyway
                }
            }
            
            if (position != null && position.MarketPosition == MarketPosition.Flat)
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
            
            // CRITICAL FIX: Add null checks before flattening
            bool flattenSucceeded = false;
            
            // Record flatten call for broker flatten recognition (avoids UNTrackED_FILL cascade)
            lock (_flattenRecognitionLock)
            {
                _lastFlattenInstrument = instrumentName ?? instrument;
                _lastFlattenUtc = utcNow;
            }
            
            // Flatten - use dynamic to handle different API signatures
            if (ntInstrument != null)
            {
                try
                {
                    dynAccountFlatten.Flatten(ntInstrument);
                    flattenSucceeded = true;
                }
                catch
                {
                    // Try alternative signature - Flatten might take ICollection<Instrument>
                    try
                    {
                        dynAccountFlatten.Flatten(new[] { ntInstrument });
                        flattenSucceeded = true;
                    }
                    catch
                    {
                        // Try with instrument name string
                        if (!string.IsNullOrEmpty(instrumentName))
                        {
                            try
                            {
                                dynAccountFlatten.Flatten(instrumentName);
                                flattenSucceeded = true;
                            }
                            catch
                            {
                                // All flatten attempts failed
                            }
                        }
                    }
                }
            }
            
            if (!flattenSucceeded)
            {
                var error = $"Flatten failed: ntInstrument is null or all flatten attempts failed. Instrument: {instrument}, InstrumentName: {instrumentName ?? "N/A"}";
                return FlattenResult.FailureResult(error, utcNow);
            }
            
            // GC FIX: Check if position is null before accessing Quantity
            var positionQty = position?.Quantity ?? 0;
            
            // CRITICAL FIX: Cancel entry stop orders when position is manually flattened
            // When user manually cancels a position, entry stop orders remain active
            // If price is at/through opposite breakout level, opposite entry stop fills immediately → re-entry
            // Solution: Cancel BOTH entry stop orders (long and short) for this stream when flattening
            if (IntentMap.TryGetValue(intentId, out var flattenedIntent))
            {
                // Find the stream and trading date from the flattened intent
                var stream = flattenedIntent.Stream ?? "";
                var tradingDate = flattenedIntent.TradingDate ?? "";
                
                if (!string.IsNullOrEmpty(stream) && !string.IsNullOrEmpty(tradingDate))
                {
                    // Find both entry intents (long and short) for this stream
                    var entryIntentIds = new List<string>();
                    foreach (var kvp in IntentMap)
                    {
                        var otherIntent = kvp.Value;
                        if (otherIntent.Stream == stream &&
                            otherIntent.TradingDate == tradingDate &&
                            otherIntent.TriggerReason != null &&
                            (otherIntent.TriggerReason.Contains("ENTRY_STOP_BRACKET_LONG") ||
                             otherIntent.TriggerReason.Contains("ENTRY_STOP_BRACKET_SHORT")))
                        {
                            // Check if this entry hasn't filled yet (only cancel unfilled entries)
                            var entryFilled = false;
                            if (_executionJournal != null)
                            {
                                var journalEntry = _executionJournal.GetEntry(kvp.Key, tradingDate, stream);
                                entryFilled = journalEntry != null && (journalEntry.EntryFilled || journalEntry.EntryFilledQuantityTotal > 0);
                            }
                            
                            if (!entryFilled)
                            {
                                entryIntentIds.Add(kvp.Key);
                            }
                        }
                    }
                    
                    // Cancel all unfilled entry stop orders for this stream
                    foreach (var entryIntentId in entryIntentIds)
                    {
                        try
                        {
                            var cancelled = CancelIntentOrders(entryIntentId, utcNow);
                            if (cancelled)
                            {
                                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_STOP_CANCELLED_ON_MANUAL_FLATTEN",
                                    new
                                    {
                                        flattened_intent_id = intentId,
                                        cancelled_entry_intent_id = entryIntentId,
                                        stream = stream,
                                        trading_date = tradingDate,
                                        note = "Cancelled entry stop order when position was manually flattened to prevent re-entry"
                                    }));
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_STOP_CANCEL_FAILED_ON_FLATTEN",
                                new
                                {
                                    error = ex.Message,
                                    entry_intent_id = entryIntentId,
                                    stream = stream,
                                    note = "Failed to cancel entry stop order on manual flatten - re-entry may occur"
                                }));
                        }
                    }
                }
            }
            
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_INTENT_SUCCESS", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    instrument = instrument,
                    position_qty = positionQty,
                    position_available = position != null,
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
    /// Check if position is flat and cancel all entry stop orders for the instrument.
    /// Called after execution updates to detect manual position closures.
    /// </summary>
    private void CheckAndCancelEntryStopsOnPositionFlat(string instrument, DateTimeOffset utcNow)
    {
        if (_ntAccount == null || _ntInstrument == null)
        {
            return;
        }
        
        var account = _ntAccount as Account;
        var ntInstrument = _ntInstrument as Instrument;
        
        if (account == null || ntInstrument == null)
        {
            return;
        }
        
        try
        {
            // Get current position for this instrument
            Position? position = null;
            try
            {
                if (ntInstrument != null)
                {
                    dynamic dynAccount = account;
                    position = dynAccount.GetPosition(ntInstrument);
                }
            }
            catch
            {
                // Try alternative signature
                try
                {
                    if (ntInstrument?.MasterInstrument != null)
                    {
                        dynamic dynAccount = account;
                        position = dynAccount.GetPosition(ntInstrument.MasterInstrument.Name);
                    }
                }
                catch
                {
                    // Position check failed - skip cancellation
                    return;
                }
            }
            
            // If position is flat, cancel all entry stop orders for this instrument
            if (position != null && position.MarketPosition == MarketPosition.Flat)
            {
                // Find all entry intents for this instrument that haven't filled yet
                var entryIntentIdsToCancel = new List<string>();
                
                foreach (var kvp in IntentMap)
                {
                    var intent = kvp.Value;
                    
                    // Match instrument (intent may store canonical NG, orderInfo may have execution MNG)
                    var intentInstrument = intent.Instrument ?? "";
                    var intentExecutionInstrument = intent.ExecutionInstrument ?? intentInstrument;
                    if (string.IsNullOrEmpty(instrument) ||
                        (string.Compare(intentInstrument, instrument, StringComparison.OrdinalIgnoreCase) != 0 &&
                         string.Compare(intentExecutionInstrument, instrument, StringComparison.OrdinalIgnoreCase) != 0))
                        continue;
                    
                    // Only check entry stop bracket intents
                    if (intent.TriggerReason == null ||
                        (!intent.TriggerReason.Contains("ENTRY_STOP_BRACKET_LONG") &&
                         !intent.TriggerReason.Contains("ENTRY_STOP_BRACKET_SHORT")))
                        continue;
                    
                    // Check if entry hasn't filled yet
                    var entryFilled = false;
                    if (_executionJournal != null && !string.IsNullOrEmpty(intent.TradingDate) && !string.IsNullOrEmpty(intent.Stream))
                    {
                        var journalEntry = _executionJournal.GetEntry(kvp.Key, intent.TradingDate, intent.Stream);
                        entryFilled = journalEntry != null && (journalEntry.EntryFilled || journalEntry.EntryFilledQuantityTotal > 0);
                    }
                    
                    if (!entryFilled)
                    {
                        entryIntentIdsToCancel.Add(kvp.Key);
                    }
                }
                
                // Cancel all unfilled entry stop orders
                foreach (var entryIntentId in entryIntentIdsToCancel)
                {
                    try
                    {
                        var cancelled = CancelIntentOrders(entryIntentId, utcNow);
                        if (cancelled)
                        {
                            _log.Write(RobotEvents.ExecutionBase(utcNow, entryIntentId, instrument, "ENTRY_STOP_CANCELLED_ON_POSITION_FLAT",
                                new
                                {
                                    cancelled_entry_intent_id = entryIntentId,
                                    instrument = instrument,
                                    position_market_position = "Flat",
                                    note = "Cancelled entry stop order because position is flat (manual closure detected)"
                                }));
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Write(RobotEvents.ExecutionBase(utcNow, entryIntentId, instrument, "ENTRY_STOP_CANCEL_FAILED_ON_POSITION_FLAT",
                            new
                            {
                                error = ex.Message,
                                entry_intent_id = entryIntentId,
                                instrument = instrument,
                                note = "Failed to cancel entry stop order when position went flat - re-entry may occur"
                            }));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log error but don't throw - this is a safety check, not critical path
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "CHECK_POSITION_FLAT_ERROR",
                new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    instrument = instrument,
                    note = "Error checking if position is flat - entry stop cancellation skipped"
                }));
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
                
                var tag = GetOrderTag(order) ?? "";
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
    /// Find opposite entry intent (same stream, opposite direction) for OCO pairing.
    /// </summary>
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
    /// Allocate fill quantity to intents deterministically: lexicographic order, first fill goes to intentIds[0]
    /// until its policy qty satisfied, then next. Updates orderInfo.AggregatedFilledByIntent.
    /// </summary>
    private List<(string allocIntentId, int allocQty)> AllocateFillToIntents(
        IReadOnlyList<string> intentIds,
        int fillQuantity,
        OrderInfo orderInfo)
    {
        var result = new List<(string, int)>();
        if (fillQuantity <= 0 || intentIds == null || intentIds.Count == 0)
            return result;

        // Single intent: allocate all to it (no cumulative tracking needed)
        if (intentIds.Count == 1)
        {
            result.Add((intentIds[0], fillQuantity));
            return result;
        }

        // Aggregated: sort lexicographically, allocate in order
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
    /// When multiple streams have entry stops at same price, aggregate into one broker order.
    /// Returns non-null if aggregation was attempted (success or failure); null to continue with normal flow.
    /// </summary>
    private OrderSubmissionResult? TryAggregateWithExistingOrders(
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
        if (!IntentMap.TryGetValue(intentId, out var currentIntent))
            return null;

        // Find existing working entry orders at same (instrument, price, direction) from other streams
        // Eligibility guard: only aggregate if stop/target/trading_date also match (one bracket can't satisfy different intents)
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
            // Eligibility: same protective parameters - one bracket must satisfy both streams
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

        // We have existing orders at same price - aggregate
        var totalQty = quantity;
        foreach (var (_, _, q, _) in toAggregate)
            totalQty += q;

        var allIntentIds = new List<string> { intentId };
        foreach (var (id, _, _, _) in toAggregate)
            allIntentIds.Add(id);

        var qtyPerIntent = new List<object> { new { id = intentId, qty = quantity } };
        foreach (var (id, _, q, _) in toAggregate)
            qtyPerIntent.Add(new { id, qty = q });
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_AGGREGATION_ATTEMPT",
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
                eligibility_passed = true,
                note = "Multiple streams at same price - aggregating into one broker order"
            }));

        string? failedStep = null;
        var replacedOrderIds = new List<string>();
        var resubmittedOrderIds = new List<string>();
        try
        {
            // 1. Cancel existing orders (OCO will auto-cancel their opposite-side pairs)
            failedStep = "CANCEL_EXISTING";
            var ordersToCancel = new List<Order>();
            foreach (var (existingIntentId, _, _, _) in toAggregate)
            {
                if (OrderMap.TryGetValue(existingIntentId, out var oi) && oi.NTOrder is Order ord)
                {
                    ordersToCancel.Add(ord);
                    replacedOrderIds.Add(ord.OrderId);
                }
            }
            if (ordersToCancel.Count > 0)
            {
                account.Cancel(ordersToCancel.ToArray());
                foreach (var (existingIntentId, _, _, _) in toAggregate)
                {
                    if (OrderMap.TryGetValue(existingIntentId, out var oi))
                        oi.State = "CANCELLED";
                }
            }

            // 2. Cancel current stream's opposite order if already submitted (e.g. NG2 long was submitted before NG2 short)
            failedStep = "CANCEL_CURRENT_OPPOSITE";
            var oppositeIntentId = FindOppositeEntryIntentId(intentId);
            if (oppositeIntentId != null && OrderMap.TryGetValue(oppositeIntentId, out var oppOrderInfo) && oppOrderInfo.NTOrder is Order oppOrd)
            {
                if (oppOrderInfo.State == "SUBMITTED" || oppOrderInfo.State == "ACCEPTED" || oppOrderInfo.State == "WORKING")
                {
                    account.Cancel(new[] { oppOrd });
                    oppOrderInfo.State = "CANCELLED";
                    replacedOrderIds.Add(oppOrd.OrderId);
                }
            }

            // 3. Create new OCO group for aggregated set
            failedStep = "SUBMIT_AGGREGATED";
            var newOcoGroup = RobotOrderIds.EncodeEntryOco(currentIntent.TradingDate ?? "", $"AGG_{string.Join("_", allIntentIds.Take(2))}", currentIntent.SlotTimeChicago ?? "");

            // 4. Submit aggregated entry order with composite tag
            var aggregatedTag = RobotOrderIds.EncodeAggregatedTag(allIntentIds);
            var orderAction = direction == "Long" ? OrderAction.Buy : OrderAction.SellShort;
            var stopPriceD = (double)stopPrice;

            var order = account.CreateOrder(
                ntInstrument,
                orderAction,
                OrderType.StopMarket,
                OrderEntry.Manual,
                TimeInForce.Day,
                totalQty,
                0.0,
                stopPriceD,
                newOcoGroup,
                aggregatedTag,
                DateTime.MinValue,
                null);

            SetOrderTag(order, aggregatedTag);
            order.Oco = newOcoGroup;

            var primaryIntentId = allIntentIds[0];
            var orderInfo = new OrderInfo
            {
                IntentId = primaryIntentId,
                Instrument = instrument,
                OrderId = order.OrderId,
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

            var ordersToSubmit = new List<Order> { order };

            // 5. Submit opposite-side orders (longs) so they're in same OCO - when short fills, longs cancel
            var oppositeDirection = direction == "Long" ? "Short" : "Long";
            foreach (var id in allIntentIds)
            {
                var oppId = FindOppositeEntryIntentId(id);
                if (oppId == null || !IntentMap.TryGetValue(oppId, out var oppIntent)) continue;
                var oppPrice = oppIntent.EntryPrice ?? 0;
                var oppQty = IntentPolicy.TryGetValue(oppId, out var oppPol) ? oppPol.ExpectedQuantity : 1;
                var oppAction = oppositeDirection == "Long" ? OrderAction.Buy : OrderAction.SellShort;
                var oppOrder = account.CreateOrder(ntInstrument, oppAction, OrderType.StopMarket, OrderEntry.Manual, TimeInForce.Day,
                    oppQty, 0.0, (double)oppPrice, newOcoGroup, RobotOrderIds.EncodeTag(oppId), DateTime.MinValue, null);
                SetOrderTag(oppOrder, RobotOrderIds.EncodeTag(oppId));
                oppOrder.Oco = newOcoGroup;
                ordersToSubmit.Add(oppOrder);
                resubmittedOrderIds.Add(oppOrder.OrderId);
                var oppOi = new OrderInfo
                {
                    IntentId = oppId,
                    Instrument = instrument,
                    OrderId = oppOrder.OrderId,
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
                if (_executionJournal != null)
                    _executionJournal.RecordSubmission(oppId, oppIntent.TradingDate ?? "", oppIntent.Stream ?? "", instrument, $"ENTRY_STOP_{oppositeDirection}", oppOrder.OrderId, utcNow);
            }

            account.Submit(ordersToSubmit.ToArray());

            foreach (var id in allIntentIds)
            {
                if (IntentMap.TryGetValue(id, out var intent) && _executionJournal != null)
                    _executionJournal.RecordSubmission(id, intent.TradingDate ?? "", intent.Stream ?? "", instrument, $"ENTRY_STOP_{direction}", order.OrderId, utcNow);
            }

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_AGGREGATION_SUCCESS",
                new
                {
                    agg_tag = aggregatedTag,
                    broker_order_id = order.OrderId,
                    aggregated_intents = allIntentIds,
                    total_quantity = totalQty,
                    oco_group = newOcoGroup,
                    replaced_order_ids = replacedOrderIds,
                    resubmitted_order_ids = resubmittedOrderIds,
                    note = "One broker order for multiple streams - eliminates same-tick dual-fill race"
                }));

            return OrderSubmissionResult.SuccessResult(order.OrderId, utcNow);
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

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_AGGREGATION_FAILED",
                new
                {
                    failed_step = failedStep ?? "UNKNOWN",
                    nt_error = ex.Message,
                    action_taken = "STAND_DOWN",
                    exposure_at_failure = exposureAtFailure,
                    existing_intents = toAggregate.Select(x => x.intentId).ToList(),
                    note = "Aggregation failed - stream blocked; flatten if exposure exists"
                }));
            return OrderSubmissionResult.FailureResult($"Entry aggregation failed at {failedStep}: {ex.Message}", utcNow);
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
        if (_ntAccount == null)
        {
            var error = "NT context not set - cannot submit stop entry orders";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        var account = _ntAccount as Account;
        if (account == null)
        {
            var error = "NT account type mismatch";
            return OrderSubmissionResult.FailureResult(error, utcNow);
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
            else
            {
                // DIAGNOSTIC: Log when rate-limiting is active (verifies rate-limiting fix is working)
                // Log this diagnostic once per 10 minutes to confirm rate-limiting without flooding
                var shouldLogDiag = !_lastInstrumentMismatchDiagLogUtc.TryGetValue(instrument, out var lastDiagLogUtc) ||
                                   (utcNow - lastDiagLogUtc).TotalMinutes >= 10.0;
                
                if (shouldLogDiag)
                {
                    _lastInstrumentMismatchDiagLogUtc[instrument] = utcNow;
                    var minutesSinceLastLog = lastLogUtc != DateTimeOffset.MinValue ? (utcNow - lastLogUtc).TotalMinutes : 0;
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "INSTRUMENT_MISMATCH_RATE_LIMITED", new
                    {
                        requested_instrument = instrument,
                        strategy_instrument = (_ntInstrument as Instrument)?.FullName ?? "NULL",
                        reason = "INSTRUMENT_MISMATCH",
                        rate_limiting_active = true,
                        minutes_since_last_log = Math.Round(minutesSinceLastLog, 1),
                        rate_limit_minutes = INSTRUMENT_MISMATCH_RATE_LIMIT_MINUTES,
                        note = $"DIAGNOSTIC: INSTRUMENT_MISMATCH rate-limiting is working. Last log was {Math.Round(minutesSinceLastLog, 1)} minutes ago. This block is suppressed."
                    }));
                }
            }
            
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        // Fix 3: Anchor on Instrument instance - use strategy's Instrument directly
        var ntInstrument = _ntInstrument as Instrument;
        if (ntInstrument == null)
        {
            var error = "Strategy Instrument instance not available - cannot submit stop entry order";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_BLOCKED", new
            {
                error,
                reason = "INSTRUMENT_INSTANCE_NULL"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            var orderAction = direction == "Long" ? OrderAction.Buy : OrderAction.SellShort;

            // Pre-submission invariant check
            if (!IntentPolicy.TryGetValue(intentId, out var expectation))
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
            var filledQty = OrderMap.TryGetValue(intentId, out var existingOrderInfo) ? existingOrderInfo.FilledQuantity : 0;
            var remainingAllowed = expectedQty - filledQty;

            // Get Chart Trader quantity if accessible
            int? chartTraderQty = null;

            // Fix 2: Quantity assertion (fail-fast) - throw immediately for invalid quantity
            if (quantity <= 0)
            {
                var error = $"Order quantity unresolved: {quantity}";
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
                    reason = error
                }));
                throw new InvalidOperationException(error);
            }

            // HARD BLOCK rules for other validation failures
            bool hardBlock = false;
            string? blockReason = null;

            if (filledQty > expectedQty)
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

            // PRICE LIMIT VALIDATION: Check stop price distance from current market price
            // Prevents NinjaTrader rejections due to stale breakout levels or market gaps
            decimal? currentMarketPrice = null;
            decimal stopDistance = 0;
            bool priceValidationFailed = false;
            string? priceValidationReason = null;
            
            try
            {
                // Get current market price using dynamic typing (API varies by NT version)
                dynamic dynInstrument = ntInstrument;
                var marketData = dynInstrument.MarketData;
                if (marketData != null)
                {
                    try
                    {
                        // Try GetBid()/GetAsk() methods first
                        double? bid = (double?)marketData.GetBid(0);
                        double? ask = (double?)marketData.GetAsk(0);
                        
                        if (bid.HasValue && ask.HasValue && !double.IsNaN(bid.Value) && !double.IsNaN(ask.Value))
                        {
                            // For long stops: use ask (buy stop triggers above ask)
                            // For short stops: use bid (sell stop triggers below bid)
                            currentMarketPrice = direction == "Long" ? (decimal)ask.Value : (decimal)bid.Value;
                        }
                    }
                    catch
                    {
                        // Fallback to Bid/Ask properties
                        try
                        {
                            double? bid = (double?)marketData.Bid;
                            double? ask = (double?)marketData.Ask;
                            
                            if (bid.HasValue && ask.HasValue && !double.IsNaN(bid.Value) && !double.IsNaN(ask.Value))
                            {
                                currentMarketPrice = direction == "Long" ? (decimal)ask.Value : (decimal)bid.Value;
                            }
                        }
                        catch
                        {
                            // Market data unavailable - skip validation (fail open)
                            // This is acceptable as NT will reject invalid prices anyway
                        }
                    }
                }
                
                if (currentMarketPrice.HasValue)
                {
                    // Calculate distance from stop price to current market price
                    stopDistance = Math.Abs(stopPrice - currentMarketPrice.Value);
                    
                    // Configurable threshold: Maximum allowed stop distance in points
                    // M2K: ~100 points (10.0) is reasonable limit
                    // ES: ~200 points (50.0) is reasonable limit
                    // Use instrument-specific thresholds based on typical range sizes
                    decimal maxStopDistancePoints = 100.0m; // Default: 100 points
                    
                    // Adjust threshold based on instrument (micro futures have smaller ranges)
                    var instrumentName = ntInstrument.MasterInstrument?.Name ?? "";
                    if (instrumentName.StartsWith("M", StringComparison.OrdinalIgnoreCase))
                    {
                        // Micro futures: tighter limit (e.g., M2K, MGC, MES)
                        maxStopDistancePoints = 50.0m; // 50 points for micros
                    }
                    else if (instrumentName == "ES" || instrumentName == "NQ")
                    {
                        // Mini futures: larger limit
                        maxStopDistancePoints = 200.0m; // 200 points for minis
                    }
                    
                    if (stopDistance > maxStopDistancePoints)
                    {
                        priceValidationFailed = true;
                        priceValidationReason = $"Stop price {stopPrice} is {stopDistance:F2} points from current market {currentMarketPrice.Value:F2}, exceeding limit of {maxStopDistancePoints} points. This indicates a stale breakout level or market gap.";
                    }
                }
            }
            catch (Exception priceEx)
            {
                // Market data access failed - log but don't block (fail open)
                // NT will reject invalid prices anyway
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, 
                    "PRICE_VALIDATION_WARNING", new
                {
                    warning = "Could not access market data for price validation",
                    error = priceEx.Message,
                    stop_price = stopPrice,
                    direction = direction,
                    note = "Proceeding with order submission - NinjaTrader will validate price"
                }));
            }
            
            if (priceValidationFailed)
            {
                // HARD BLOCK: Stop price too far from market (fail-closed)
                var (tradingDate20, stream20, _, _, _, _, _) = GetIntentInfo(intentId);
                _executionJournal.RecordRejection(intentId, tradingDate20, stream20, $"STOP_PRICE_VALIDATION_FAILED: {priceValidationReason}", utcNow, 
                    orderType: "STOP", rejectedPrice: stopPrice, rejectedQuantity: null);
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, 
                    "STOP_PRICE_VALIDATION_FAILED", new
                {
                    intent_id = intentId,
                    stop_price = stopPrice,
                    current_market_price = currentMarketPrice,
                    stop_distance_points = stopDistance,
                    direction = direction,
                    reason = priceValidationReason,
                    note = "Order rejected before submission to prevent NinjaTrader price limit error. This indicates a stale breakout level or significant market gap."
                }));
                return OrderSubmissionResult.FailureResult($"Stop price validation failed: {priceValidationReason}", utcNow);
            }

            // ENTRY AGGREGATION: When IEA enabled, route through queue (Gap 1). IEA owns aggregation + single-order fallback.
            // Both paths run on worker to preserve single mutation lane invariant.
            if (_useInstrumentExecutionAuthority && _iea != null)
            {
                var (success, ieaResult) = _iea.EnqueueAndWait(() =>
                {
                    var aggResult = _iea.SubmitStopEntryOrder(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
                    if (aggResult != null)
                        return aggResult;
                    return SubmitSingleEntryOrderCore(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
                });
                if (!success)
                    return OrderSubmissionResult.FailureResult("Entry submission failed (IEA queue overflow or timeout)", utcNow);
                return ieaResult ?? OrderSubmissionResult.FailureResult("Entry submission returned null", utcNow);
            }

            if (!_useInstrumentExecutionAuthority)
            {
                var aggregateResult = TryAggregateWithExistingOrders(intentId, instrument, direction, stopPrice, quantity, ocoGroup, account, ntInstrument, utcNow);
                if (aggregateResult != null)
                    return aggregateResult;
            }

            // Real NT API: Create stop-market entry using official NT8 CreateOrder factory method
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
                    stop_price = stopPrice,
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
                    stop_price = stopPrice,
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
                    stop_price = stopPrice,
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
                    stop_price = stopPrice,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                throw new InvalidOperationException(error);
            }
            
            var stopPriceD = (double)stopPrice;
            if (stopPriceD <= 0)
            {
                var error = $"Invalid stop price: {stopPriceD} (must be > 0)";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                {
                    error,
                    order_type = "StopMarket",
                    order_action = orderAction.ToString(),
                    quantity = quantity,
                    stop_price = stopPrice,
                    instrument = instrument,
                    intent_id = intentId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }

            // CRITICAL: Validate stop price relative to current market price
            // For Sell Short Stop Market: stop must be BELOW current price
            // For Buy Stop Market: stop must be ABOVE current price
            // Note: Instrument.LastPrice is not available in NT API, skipping price validation
            // Stop price validation is handled by NinjaTrader broker
            // Price validation removed - NinjaTrader broker will validate stop prices and reject invalid orders
            
            // Create order using official NT8 CreateOrder factory method
            Order order = null!;
            try
            {
                // If ocoGroup is empty/whitespace, pass null
                string? ocoForOrder = string.IsNullOrWhiteSpace(ocoGroup) ? null : ocoGroup;
                
                order = account.CreateOrder(
                    ntInstrument,                           // Instrument
                    orderAction,                            // OrderAction
                    OrderType.StopMarket,                   // OrderType
                    OrderEntry.Manual,                      // OrderEntry
                    TimeInForce.Day,                        // TimeInForce
                    quantity,                               // Quantity
                    0.0,                                    // LimitPrice (0 for StopMarket)
                    stopPriceD,                             // StopPrice
                    ocoForOrder,                            // Oco (null if empty/whitespace)
                    RobotOrderIds.EncodeTag(intentId),      // OrderName
                    DateTime.MinValue,                      // Gtd
                    null                                    // CustomOrder
                );
                
                // Log success before Submit
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATED_STOPMARKET", new
                {
                    order_name = RobotOrderIds.EncodeTag(intentId),
                    stop_price = stopPriceD,
                    quantity = quantity,
                    order_action = orderAction.ToString(),
                    instrument = instrument
                }));
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
                    account = "SIM",
                    exception_type = ex.GetType().Name
                }));
                return OrderSubmissionResult.FailureResult($"Failed to create StopMarket order: {ex.Message}", utcNow);
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
            
            // Set order tag (already set via OrderName in CreateOrder, but ensure consistency)
            SetOrderTag(order, RobotOrderIds.EncodeTag(intentId));
            order.TimeInForce = TimeInForce.Day;
            // Oco is already set via CreateOrder parameter, but ensure it's set correctly
            if (!string.IsNullOrWhiteSpace(ocoGroup))
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
            
            // Copy policy expectation from IntentPolicy if available
            if (IntentPolicy.TryGetValue(intentId, out var expectationForOrder))
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
            
            OrderMap[intentId] = orderInfo;

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
            catch (Exception ex)
            {
                // First Submit() call failed - log and attempt fallback
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FALLBACK", new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    order_type = "ENTRY_STOP",
                    note = "First Submit() call failed, attempting fallback (Submit returns void)"
                }));
                
                // Fallback: Submit returns void - try again
                try
                {
                    dynAccountSubmit.Submit(new[] { order });
                    submitResult = order;
                }
                catch (Exception fallbackEx)
                {
                    // Both attempts failed - reject order
                    var errorMsg = $"Entry stop order submission failed: {ex.Message} (fallback also failed: {fallbackEx.Message})";
                    var (tradingDate17, stream17, _, _, _, _, _) = GetIntentInfo(intentId);
                    _executionJournal.RecordRejection(intentId, tradingDate17, stream17, $"ENTRY_STOP_SUBMIT_FAILED: {errorMsg}", utcNow, 
                        orderType: "ENTRY_STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
                    {
                        error = errorMsg,
                        first_error = ex.Message,
                        fallback_error = fallbackEx.Message,
                        broker_order_id = order.OrderId,
                        account = "SIM",
                        exception_type = ex.GetType().Name,
                        fallback_exception_type = fallbackEx.GetType().Name
                    }));
                    return OrderSubmissionResult.FailureResult(errorMsg, utcNow);
                }
            }
            var acknowledgedAt = DateTimeOffset.UtcNow;

            if (submitResult.OrderState == OrderState.Rejected)
            {
                // Get error message using dynamic typing with nested try-catch for graceful fallback
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
                var (tradingDate18, stream18, _, _, _, _, _) = GetIntentInfo(intentId);
                _executionJournal.RecordRejection(intentId, tradingDate18, stream18, $"ENTRY_STOP_SUBMIT_FAILED: {error}", utcNow, 
                    orderType: "ENTRY_STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
                {
                    error,
                    order_type = "ENTRY_STOP",
                    broker_order_id = order.OrderId,
                    account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult(error, utcNow);
            }

            var (tradingDate19, stream19, intentEntryPrice3, intentStopPrice4, intentTargetPrice4, intentDirection2, ocoGroup3) = GetIntentInfo(intentId);
            _executionJournal.RecordSubmission(intentId, tradingDate19, stream19, instrument, "ENTRY_STOP", order.OrderId, acknowledgedAt, 
                expectedEntryPrice: null, entryPrice: intentEntryPrice3, stopPrice: intentStopPrice4 ?? stopPrice, 
                targetPrice: intentTargetPrice4, direction: intentDirection2 ?? direction, ocoGroup: ocoGroup3 ?? ocoGroup);

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

    /// <summary>
    /// Gap 1: Single-order entry submission (runs on IEA worker when IEA enabled).
    /// Called when IEA.SubmitStopEntryOrder returns null (no aggregation). Preserves single mutation lane.
    /// </summary>
    private OrderSubmissionResult SubmitSingleEntryOrderCore(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        var account = _ntAccount as Account;
        var ntInstrument = _ntInstrument as Instrument;
        if (account == null || ntInstrument == null)
            return OrderSubmissionResult.FailureResult("NT context not set", utcNow);

        var orderAction = direction == "Long" ? OrderAction.Buy : OrderAction.SellShort;

        // Runtime safety checks
        if (!_ntContextSet)
            return OrderSubmissionResult.FailureResult("NT context not set - cannot create StopMarket order", utcNow);
        if (quantity <= 0)
            throw new InvalidOperationException($"Order quantity unresolved: {quantity}");

        var stopPriceD = (double)stopPrice;
        if (stopPriceD <= 0)
            return OrderSubmissionResult.FailureResult($"Invalid stop price: {stopPriceD} (must be > 0)", utcNow);

        Order order;
        try
        {
            string? ocoForOrder = string.IsNullOrWhiteSpace(ocoGroup) ? null : ocoGroup;
            order = account.CreateOrder(
                ntInstrument, orderAction, OrderType.StopMarket, OrderEntry.Manual, TimeInForce.Day,
                quantity, 0.0, stopPriceD, ocoForOrder, RobotOrderIds.EncodeTag(intentId), DateTime.MinValue, null);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
            {
                error = $"Failed to create StopMarket order: {ex.Message}",
                order_type = "StopMarket",
                quantity, stop_price = stopPriceD, instrument, intent_id = intentId, account = "SIM",
                exception_type = ex.GetType().Name
            }));
            return OrderSubmissionResult.FailureResult($"Failed to create StopMarket order: {ex.Message}", utcNow);
        }

        if (order.Quantity != quantity)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATED_VERIFICATION", new
            {
                intent_id = intentId, requested_quantity = quantity, order_quantity = order.Quantity,
                order_id = order.OrderId, instrument, verified = false
            }));
            TriggerQuantityEmergency(intentId, "QUANTITY_MISMATCH_EMERGENCY", utcNow, new Dictionary<string, object>
            {
                { "requested_quantity", quantity }, { "order_quantity", order.Quantity },
                { "reason", "Order creation quantity mismatch" }
            });
            return OrderSubmissionResult.FailureResult($"Order quantity mismatch: requested {quantity}, order has {order.Quantity}", utcNow);
        }

        SetOrderTag(order, RobotOrderIds.EncodeTag(intentId));
        order.TimeInForce = TimeInForce.Day;
        if (!string.IsNullOrWhiteSpace(ocoGroup))
            order.Oco = ocoGroup;

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
        if (IntentPolicy.TryGetValue(intentId, out var expectationForOrder))
        {
            orderInfo.ExpectedQuantity = expectationForOrder.ExpectedQuantity;
            orderInfo.MaxQuantity = expectationForOrder.MaxQuantity;
            orderInfo.PolicySource = expectationForOrder.PolicySource;
            orderInfo.CanonicalInstrument = expectationForOrder.CanonicalInstrument;
            orderInfo.ExecutionInstrument = expectationForOrder.ExecutionInstrument;
        }

        OrderMap[intentId] = orderInfo;

        dynamic dynAccountSubmit = account;
        Order submitResult;
        try
        {
            object? result = dynAccountSubmit.Submit(new[] { order });
            submitResult = (result != null && result is Order[] arr && arr.Length > 0) ? arr[0] : order;
        }
        catch (Exception ex)
        {
            try
            {
                dynAccountSubmit.Submit(new[] { order });
                submitResult = order;
            }
            catch (Exception fallbackEx)
            {
                var errorMsg = $"Entry stop order submission failed: {ex.Message} (fallback: {fallbackEx.Message})";
                var (td, streamReject, _, _, _, _, _) = GetIntentInfo(intentId);
                _executionJournal.RecordRejection(intentId, td, streamReject, $"ENTRY_STOP_SUBMIT_FAILED: {errorMsg}", utcNow,
                    orderType: "ENTRY_STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
                {
                    error = errorMsg, first_error = ex.Message, fallback_error = fallbackEx.Message,
                    broker_order_id = order.OrderId, account = "SIM"
                }));
                return OrderSubmissionResult.FailureResult(errorMsg, utcNow);
            }
        }

        if (submitResult.OrderState == OrderState.Rejected)
        {
            dynamic dynOrder = submitResult;
            string error = "Order rejected";
            try { error = (string?)dynOrder.ErrorMessage ?? (string?)dynOrder.Error ?? "Order rejected"; }
            catch { try { error = (string?)dynOrder.Error ?? "Order rejected"; } catch { } }
            var (td, streamReject, _, _, _, _, _) = GetIntentInfo(intentId);
            _executionJournal.RecordRejection(intentId, td, streamReject, $"ENTRY_STOP_SUBMIT_FAILED: {error}", utcNow,
                orderType: "ENTRY_STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error, order_type = "ENTRY_STOP", broker_order_id = order.OrderId, account = "SIM"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        var acknowledgedAt = DateTimeOffset.UtcNow;
        var (tradingDate, stream, intentEntryPrice, intentStopPrice, intentTargetPrice, intentDirection, ocoGroupVal) = GetIntentInfo(intentId);
        _executionJournal.RecordSubmission(intentId, tradingDate, stream, instrument, "ENTRY_STOP", order.OrderId, acknowledgedAt,
            expectedEntryPrice: null, entryPrice: intentEntryPrice, stopPrice: intentStopPrice ?? stopPrice,
            targetPrice: intentTargetPrice, direction: intentDirection ?? direction, ocoGroup: ocoGroupVal ?? ocoGroup);

        _log.Write(RobotEvents.ExecutionBase(acknowledgedAt, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
        {
            broker_order_id = order.OrderId, order_type = "ENTRY_STOP", direction, stop_price = stopPrice,
            quantity, oco_group = ocoGroup, account = "SIM", order_action = orderAction.ToString()
        }));

        return OrderSubmissionResult.SuccessResult(order.OrderId, utcNow, acknowledgedAt);
    }

    /// <summary>
    /// Get current position quantity for instrument using real NT API.
    /// </summary>
    private int GetCurrentPositionReal(string instrument)
    {
        if (_ntAccount == null || _ntInstrument == null)
        {
            return 0;
        }
        
        var account = _ntAccount as Account;
        var ntInstrument = _ntInstrument as Instrument;
        
        if (account == null || ntInstrument == null)
        {
            return 0;
        }
        
        try
        {
            // Get position - use dynamic to handle different API signatures
            dynamic dynAccountPos = account;
            Position? position = null;
            try
            {
                position = dynAccountPos.GetPosition(ntInstrument);
            }
            catch
            {
                // Try alternative signature - GetPosition might take instrument name string
                try
                {
                    position = dynAccountPos.GetPosition(ntInstrument.MasterInstrument.Name);
                }
                catch
                {
                    return 0;
                }
            }
            return position?.Quantity ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}

// Emergency handler accessor (delegates to base class method)
partial class NinjaTraderSimAdapter
{
    // TriggerQuantityEmergency is defined in NinjaTraderSimAdapter.cs
    // This partial class declaration allows NT-specific code to call it
}

#endif
