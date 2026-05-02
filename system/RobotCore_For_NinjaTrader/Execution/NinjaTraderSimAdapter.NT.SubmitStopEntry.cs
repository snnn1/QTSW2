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
using QTSW2.Robot.Contracts;
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

        var aggregateRelationshipFailure = TryBlockInvalidStopMarketRelationship(
            intentId, instrument, direction, stopPrice, totalQty, "ENTRY_STOP_AGGREGATE", utcNow,
            out var aggregateConvertToMarket, allowEntryMarketConversion: false);
        if (aggregateRelationshipFailure != null)
            return aggregateRelationshipFailure;

        var aggregateOppositeDirection = direction == "Long" ? "Short" : "Long";
        foreach (var id in allIntentIds)
        {
            var oppId = FindOppositeEntryIntentId(id);
            if (oppId == null || !IntentMap.TryGetValue(oppId, out var oppIntent))
                continue;

            var oppPrice = oppIntent.EntryPrice ?? 0;
            var oppQty = IntentPolicy.TryGetValue(oppId, out var oppPol) ? oppPol.ExpectedQuantity : 1;
            var oppositeRelationshipFailure = TryBlockInvalidStopMarketRelationship(
                oppId, instrument, aggregateOppositeDirection, oppPrice, oppQty, "ENTRY_STOP_AGGREGATE_OPPOSITE", utcNow,
                out var oppositeConvertToMarket, allowEntryMarketConversion: false);
            if (oppositeRelationshipFailure != null)
                return oppositeRelationshipFailure;
        }

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
                var ordersArr = ordersToCancel.ToArray();
                if (!EnsureStrategyThreadOrEnqueue("CancelReplaceWithAggregated", intentId, null, $"CANCEL_AGG:{intentId}", () => account.Cancel(ordersArr)))
                    throw new InvalidOperationException("CancelReplaceWithAggregated enqueued for strategy thread");
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
                    if (!EnsureStrategyThreadOrEnqueue("CancelReplaceWithAggregated_Opposite", oppositeIntentId, null, $"CANCEL_OPP:{oppOrd.OrderId}", () => account.Cancel(new[] { oppOrd })))
                        throw new InvalidOperationException("Cancel opposite enqueued for strategy thread");
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

            var bundleGateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in allIntentIds)
            {
                bundleGateIds.Add(id);
                var oppG = FindOppositeEntryIntentId(id);
                if (oppG != null) bundleGateIds.Add(oppG);
            }
            if (!TrySessionIdentityGateForIntentBundle(bundleGateIds.ToList(), instrument, "entry_stop_aggregate", utcNow, out var bundleGateFailure))
                return bundleGateFailure;

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
                    bool stopInTheMoney = (direction == "Long" && currentMarketPrice.Value >= stopPrice) ||
                                          (direction == "Short" && currentMarketPrice.Value <= stopPrice);
                    
                    if (stopInTheMoney)
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
                    else
                    {
                        // Configurable threshold: Maximum allowed stop distance in points
                        // M2K: ~100 points (10.0) is reasonable limit
                        // ES: ~200 points (50.0) is reasonable limit
                        // Use instrument-specific thresholds based on typical range sizes
                        decimal maxStopDistancePoints = 100.0m; // Default: 100 points
                        
                        // Adjust threshold based on instrument (micro futures have smaller ranges)
                        var instrumentName = ntInstrument.MasterInstrument?.Name ?? "";
                        if (instrumentName.StartsWith("M", StringComparison.OrdinalIgnoreCase))
                        {
                            // Micro futures: tighter limit (e.g., M2K, MGC, MES, MYM)
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

            // NT THREADING FIX: NT CreateOrder/Submit must run on strategy thread (OnBarUpdate context).
            // IEA worker caused IEA_ENQUEUE_AND_WAIT_TIMEOUT when NT APIs blocked the worker.
            // Run entry submission on caller thread with lock for serialization across instances sharing this IEA.
            if (_useInstrumentExecutionAuthority && _iea != null)
            {
                lock (_iea.EntrySubmissionLock)
                {
                    var aggResult = _iea.SubmitStopEntryOrder(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
                    if (aggResult != null)
                        return aggResult;
                    return SubmitSingleEntryOrderCore(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
                }
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
                var (tradingDate18, stream18, _, _, _, _, ocoGroup18) = GetIntentInfo(intentId);
                var ocoForEntryStop = ocoGroup ?? ocoGroup18;
                var isOcoCancelPendingEntry = (error?.IndexOf("CancelPending", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 && !string.IsNullOrEmpty(ocoForEntryStop);
                if (isOcoCancelPendingEntry)
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "OCO_SIBLING_CANCELLED", new
                    {
                        oco_group_id = ocoForEntryStop,
                        order_type = "ENTRY_STOP",
                        intent_id = intentId,
                        note = "OCO sibling cancelled at submit (other leg filled) - expected, not ORDER_SUBMIT_FAIL"
                    }));
                }
                _executionJournal.RecordRejection(intentId, tradingDate18, stream18, $"ENTRY_STOP_SUBMIT_FAILED: {error}", utcNow, 
                    orderType: "ENTRY_STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);
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

        var relationshipFailure = TryBlockInvalidStopMarketRelationship(
            intentId, instrument, direction, stopPrice, quantity, "ENTRY_STOP", utcNow,
            out var convertStopEntryToMarket);
        if (relationshipFailure != null)
            return relationshipFailure;
        if (convertStopEntryToMarket)
            return SubmitEntryOrderCore(intentId, instrument, direction, null, quantity, "MARKET", ocoGroup, utcNow,
                "SUBMIT_ENTRY_STOP");

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
        // Registry/adoption scans run on wall clock; arm this bounded convergence window on the same clock.
        QuantExecutionControlStore.NotifyWorkingOrderSubmitTransition(instrument, 1, DateTimeOffset.UtcNow);

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
            var (td, streamReject, _, _, _, _, ocoGroupReject) = GetIntentInfo(intentId);
            var ocoForEntryStop2 = ocoGroup ?? ocoGroupReject;
            var isOcoCancelPendingEntry2 = (error?.IndexOf("CancelPending", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 && !string.IsNullOrEmpty(ocoForEntryStop2);
            if (isOcoCancelPendingEntry2)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "OCO_SIBLING_CANCELLED", new
                {
                    oco_group_id = ocoForEntryStop2,
                    order_type = "ENTRY_STOP",
                    intent_id = intentId,
                    note = "OCO sibling cancelled at submit (other leg filled) - expected, not ORDER_SUBMIT_FAIL"
                }));
            }
            _executionJournal.RecordRejection(intentId, td, streamReject, $"ENTRY_STOP_SUBMIT_FAILED: {error}", utcNow,
                orderType: "ENTRY_STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        var acknowledgedAt = DateTimeOffset.UtcNow;
        var (tradingDate, stream, intentEntryPrice, intentStopPrice, intentTargetPrice, intentDirection, ocoGroupVal) = GetIntentInfo(intentId);
        _executionJournal.RecordSubmission(intentId, tradingDate, stream, instrument, "ENTRY_STOP", order.OrderId, acknowledgedAt,
            expectedEntryPrice: null, entryPrice: intentEntryPrice, stopPrice: intentStopPrice ?? stopPrice,
            targetPrice: intentTargetPrice, direction: intentDirection ?? direction, ocoGroup: ocoGroupVal ?? ocoGroup);

        orderInfo.OrderId = order.OrderId ?? orderInfo.OrderId;
        _iea?.RegisterOrder(order.OrderId, intentId, instrument, stream, OrderRole.ENTRY, OrderOwnershipStatus.OWNED, "SubmitStopEntryOrder", orderInfo, acknowledgedAt);
        _iea?.TryTransitionIntentLifecycle(intentId, IntentLifecycleTransition.SUBMIT_ENTRY, null, acknowledgedAt);

        _log.Write(RobotEvents.ExecutionBase(acknowledgedAt, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
        {
            broker_order_id = order.OrderId, order_type = "ENTRY_STOP", direction, stop_price = stopPrice,
            quantity, oco_group = ocoGroup, account = "SIM", order_action = orderAction.ToString()
        }));

        _onMismatchExecutionTrigger?.Invoke(instrument.Trim(), acknowledgedAt, new MismatchExecutionTriggerDetails
        {
            IntentId = intentId,
            WorkingOrderSubmitTransition = true
        });

        return OrderSubmissionResult.SuccessResult(order.OrderId, utcNow, acknowledgedAt);
    }

}

#endif
