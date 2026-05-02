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
    // _lastInstrumentMismatchDiagLogUtc) are defined in the base class file (NinjaTraderSimAdapter.cs)

    /// <summary>
    /// Helper method to safely get order tag/name using dynamic typing.
    /// </summary>
    private static string? GetOrderTag(Order? order)
    {
        if (order == null)
            return null;

        dynamic dynOrder = order;
        try
        {
            return dynOrder.Tag as string ?? dynOrder.Name as string;
        }
        catch
        {
            try
            {
                return dynOrder.Name as string;
            }
            catch
            {
                return null;
            }
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

    private static List<Order> SnapshotAccountOrders(Account? account)
    {
        var snapshot = new List<Order>();
        if (account?.Orders == null) return snapshot;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            snapshot.Clear();
            try
            {
                foreach (Order order in account.Orders)
                {
                    if (order != null) snapshot.Add(order);
                }
                return snapshot;
            }
            catch (InvalidOperationException) when (attempt == 0)
            {
                // NinjaTrader mutates Account.Orders while order/execution callbacks are active.
            }
            catch (InvalidOperationException)
            {
                snapshot.Clear();
                return snapshot;
            }
        }

        return snapshot;
    }

    private static List<Position> SnapshotAccountPositions(Account? account)
    {
        var snapshot = new List<Position>();
        if (account?.Positions == null) return snapshot;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            snapshot.Clear();
            try
            {
                foreach (Position position in account.Positions)
                {
                    if (position != null) snapshot.Add(position);
                }
                return snapshot;
            }
            catch (InvalidOperationException) when (attempt == 0)
            {
                // NinjaTrader mutates Account.Positions while order/execution callbacks are active.
            }
            catch (InvalidOperationException)
            {
                snapshot.Clear();
                return snapshot;
            }
        }

        return snapshot;
    }

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
    /// Get current market bid/ask for breakout validity gate.
    /// </summary>
    private (decimal? Bid, decimal? Ask) GetCurrentMarketPriceReal(string instrument, DateTimeOffset utcNow)
    {
        decimal? bid = null;
        decimal? ask = null;

        if (_ntInstrument == null)
            return ApplyLastPriceEnvelope(instrument, utcNow, bid, ask);

        try
        {
            dynamic dynInstrument = _ntInstrument;
            var marketData = dynInstrument.MarketData;
            if (marketData == null)
                return ApplyLastPriceEnvelope(instrument, utcNow, bid, ask);
            double? ntBid = null;
            double? ntAsk = null;
            try
            {
                ntBid = (double?)marketData.GetBid(0);
                ntAsk = (double?)marketData.GetAsk(0);
            }
            catch
            {
                try
                {
                    ntBid = (double?)marketData.Bid;
                    ntAsk = (double?)marketData.Ask;
                }
                catch { return ApplyLastPriceEnvelope(instrument, utcNow, bid, ask); }
            }
            if (ntBid.HasValue && !double.IsNaN(ntBid.Value) && ntBid.Value > 0)
                bid = (decimal)ntBid.Value;
            if (ntAsk.HasValue && !double.IsNaN(ntAsk.Value) && ntAsk.Value > 0)
                ask = (decimal)ntAsk.Value;
        }
        catch { }

        return ApplyLastPriceEnvelope(instrument, utcNow, bid, ask);
    }

    private (decimal? Bid, decimal? Ask) ApplyLastPriceEnvelope(string instrument, DateTimeOffset utcNow, decimal? bid, decimal? ask)
    {
        if (TryGetLatestMarketDataLast(instrument, utcNow, out var lastPrice, out _))
        {
            bid = bid.HasValue ? Math.Min(bid.Value, lastPrice) : lastPrice;
            ask = ask.HasValue ? Math.Max(ask.Value, lastPrice) : lastPrice;
        }
        return (bid, ask);
    }

    private const int EntryStopMarketConversionToleranceTicks = 2;

    private OrderSubmissionResult? TryBlockInvalidStopMarketRelationship(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int? quantity,
        string orderRole,
        DateTimeOffset utcNow,
        out bool convertEntryToMarket,
        bool allowEntryMarketConversion = true)
    {
        convertEntryToMarket = false;
        var isEntryStop = orderRole.IndexOf("ENTRY", StringComparison.OrdinalIgnoreCase) >= 0;
        var isBuyStop = isEntryStop
            ? string.Equals(direction, "Long", StringComparison.OrdinalIgnoreCase)
            : string.Equals(direction, "Short", StringComparison.OrdinalIgnoreCase);
        var (bid, ask) = GetCurrentMarketPriceReal(instrument, utcNow);
        var marketPrice = isBuyStop ? ask : bid;
        var marketPriceSource = isBuyStop ? "Ask" : "Bid";

        if (!marketPrice.HasValue)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument,
                "STOP_MARKET_RELATIONSHIP_VALIDATION_UNAVAILABLE", new
                {
                    intent_id = intentId,
                    order_role = orderRole,
                    stop_price = stopPrice,
                    direction,
                    note = "Market bid/ask unavailable; proceeding with existing submit path."
                }));
            return null;
        }

        var invalidRelationship = isBuyStop
            ? stopPrice <= marketPrice.Value
            : stopPrice >= marketPrice.Value;
        if (!invalidRelationship)
            return null;

        var crossedDistance = isBuyStop
            ? marketPrice.Value - stopPrice
            : stopPrice - marketPrice.Value;
        var tickSize = GetTickSizeForInstrument(instrument);
        var crossedTicks = tickSize > 0 ? crossedDistance / tickSize : (decimal?)null;
        if (isEntryStop && allowEntryMarketConversion && crossedTicks.HasValue &&
            crossedTicks.Value <= EntryStopMarketConversionToleranceTicks)
        {
            convertEntryToMarket = true;
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument,
                "ENTRY_STOP_CROSSED_CONVERTED_TO_MARKET", new
                {
                    intent_id = intentId,
                    order_role = orderRole,
                    stop_price = stopPrice,
                    market_price = marketPrice.Value,
                    market_price_source = marketPriceSource,
                    direction,
                    quantity,
                    crossed_distance_points = crossedDistance,
                    crossed_distance_ticks = crossedTicks,
                    tolerance_ticks = EntryStopMarketConversionToleranceTicks,
                    note = "Entry stop is already marketable but still within tolerance; converting to MARKET before NT CreateOrder/Submit."
                }));
            return null;
        }

        var side = isBuyStop ? "Buy stop" : "Sell stop";
        var relation = isBuyStop ? "at/below current ask" : "at/above current bid";
        var reason = $"{side} can't be placed: stop price {stopPrice} is {relation} {marketPrice.Value}. Stop-market order is already crossed beyond market-conversion tolerance before submission.";
        var eventType = isEntryStop ? "ENTRY_STOP_ALREADY_CROSSED_BLOCKED" : "STOP_MARKET_RELATIONSHIP_BLOCKED";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, eventType, new
        {
            intent_id = intentId,
            order_role = orderRole,
            stop_price = stopPrice,
            market_price = marketPrice.Value,
            market_price_source = marketPriceSource,
            direction,
            quantity,
            crossed_distance_points = crossedDistance,
            crossed_distance_ticks = crossedTicks,
            tolerance_ticks = isEntryStop ? EntryStopMarketConversionToleranceTicks : (int?)null,
            reason,
            note = "Blocked before NT CreateOrder/Submit to avoid synchronous NinjaTrader stop rejection."
        }));

        var (tradingDate, stream, _, _, _, _, _) = GetIntentInfo(intentId);
        _executionJournal.RecordRejection(intentId, tradingDate, stream, $"STOP_PRICE_VALIDATION_FAILED: {reason}", utcNow,
            orderType: isEntryStop ? "ENTRY_STOP" : "STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument,
            "STOP_PRICE_VALIDATION_FAILED", new
            {
                intent_id = intentId,
                order_role = orderRole,
                stop_price = stopPrice,
                current_market_price = marketPrice.Value,
                market_price_source = marketPriceSource,
                direction,
                quantity,
                reason
            }));
        return OrderSubmissionResult.FailureResult($"Stop price validation failed: {reason}", utcNow);
    }

    /// <summary>
    /// Helper method to get Intent info for journal logging.
    /// Returns tradingDate, stream, and intent prices from _intentMap if available.
    /// </summary>
    private (string tradingDate, string stream, decimal? entryPrice, decimal? stopPrice, decimal? targetPrice, string? direction, string? ocoGroup) GetIntentInfo(string intentId)
    {
        if (_intentMap.TryGetValue(intentId, out var intent))
        {
            return (intent.TradingDate, intent.Stream, intent.EntryPrice, intent.StopPrice, intent.TargetPrice, intent.Direction, null);
        }
        return ("", "", null, null, null, null, null);
    }

}

#endif
