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

        if (TryBlockDuplicateEntrySubmit(intentId, instrument, "ENTRY_STOP", quantity, utcNow, out var duplicateEntryFailure))
            return duplicateEntryFailure!;

        try
        {
            var orderAction = direction == "Long" ? OrderAction.Buy : OrderAction.SellShort;

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

            var relationshipFailure = TryBlockInvalidStopMarketRelationship(
                intentId, instrument, direction, stopPrice, quantity, "ENTRY_STOP", utcNow,
                out var convertStopEntryToMarket);
            if (relationshipFailure != null)
                return relationshipFailure;
            if (convertStopEntryToMarket)
                return SubmitEntryOrderCore(intentId, instrument, direction, null, quantity, "MARKET", ocoGroup, utcNow,
                    "SUBMIT_ENTRY_STOP");

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

                    // Stop-market entries must not be submitted after price has already crossed the stop.
                    // NinjaTrader rejects marketable stop entries synchronously, and in playback that rejection
                    // can re-enter OCO cancellation deeply enough to crash the process.
                    bool stopAlreadyCrossed = (direction == "Long" && currentMarketPrice.Value >= stopPrice) ||
                                              (direction == "Short" && currentMarketPrice.Value <= stopPrice);

                    if (stopAlreadyCrossed)
                    {
                        var tickSize = GetTickSizeForInstrument(instrument);
                        var crossedTicks = tickSize > 0 ? stopDistance / tickSize : (decimal?)null;
                        if (crossedTicks.HasValue && crossedTicks.Value <= EntryStopMarketConversionToleranceTicks)
                        {
                            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_STOP_CROSSED_CONVERTED_TO_MARKET", new
                            {
                                intent_id = intentId,
                                stop_price = stopPrice,
                                market_price = currentMarketPrice.Value,
                                market_price_source = direction == "Long" ? "Ask" : "Bid",
                                direction,
                                crossed_distance_points = stopDistance,
                                crossed_distance_ticks = crossedTicks,
                                tolerance_ticks = EntryStopMarketConversionToleranceTicks,
                                note = "Entry stop is already marketable but still within tolerance; converting to MARKET before NT CreateOrder/Submit."
                            }));
                            return SubmitEntryOrderCore(intentId, instrument, direction, null, quantity, "MARKET", ocoGroup, utcNow,
                                "SUBMIT_ENTRY_STOP");
                        }

                        priceValidationFailed = true;
                        var side = direction == "Long" ? "Buy stop" : "Sell stop";
                        var relation = direction == "Long" ? "at/below current ask" : "at/above current bid";
                        priceValidationReason = $"{side} can't be placed: stop price {stopPrice} is {relation} {currentMarketPrice.Value}. Market moved through the breakout beyond market-conversion tolerance before stop-entry submission.";
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_STOP_ALREADY_CROSSED_BLOCKED", new
                        {
                            intent_id = intentId,
                            stop_price = stopPrice,
                            market_price = currentMarketPrice.Value,
                            market_price_source = direction == "Long" ? "Ask" : "Bid",
                            direction,
                            stop_distance_points = stopDistance,
                            stop_distance_ticks = crossedTicks,
                            tolerance_ticks = EntryStopMarketConversionToleranceTicks,
                            reason = priceValidationReason,
                            note = "Blocked before NT CreateOrder/Submit to avoid rejected marketable stop entry."
                        }));
                    }
                    
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
                    
                    if (!priceValidationFailed && stopDistance > maxStopDistancePoints)
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
            var intentBeTrigger3 = _intentMap.TryGetValue(intentId, out var stopIntent)
                ? stopIntent.BeTrigger
                : null;
            _executionJournal.RecordSubmission(intentId, tradingDate19, stream19, instrument, "ENTRY_STOP", order.OrderId, acknowledgedAt, 
                expectedEntryPrice: null, entryPrice: intentEntryPrice3, stopPrice: intentStopPrice4 ?? stopPrice, 
                targetPrice: intentTargetPrice4, beTriggerPrice: intentBeTrigger3, direction: intentDirection2 ?? direction, ocoGroup: ocoGroup3 ?? ocoGroup);

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

            _onMismatchExecutionTrigger?.Invoke(instrument.Trim(), acknowledgedAt, new MismatchExecutionTriggerDetails
            {
                IntentId = intentId,
                WorkingOrderSubmitTransition = true
            });

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
