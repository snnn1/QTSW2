// NinjaTrader-specific implementation using real NT APIs
// This file is compiled only when NINJATRADER is defined (inside NT Strategy context)

// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

#if NINJATRADER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            
            // CRITICAL: Verify tag was set correctly
            var verifyTag = GetOrderTag(order);
            if (verifyTag != encodedTag)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_TAG_SET_FAILED",
                    new
                    {
                        intent_id = intentId,
                        expected_tag = encodedTag,
                        actual_tag = verifyTag ?? "NULL",
                        broker_order_id = order.OrderId,
                        error = "Order tag verification failed - tag may not be set correctly",
                        note = "This may cause fills to be ignored if tag cannot be decoded"
                    }));
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
        // OrderUpdate is the event args type, use dynamic to access it
        dynamic orderUpdate = orderUpdateObj;
        if (order == null) return;

        // Get tag/name (try Tag first, fallback to Name)
        var encodedTag = GetOrderTag(order);
        var intentId = RobotOrderIds.DecodeIntentId(encodedTag);
        if (string.IsNullOrEmpty(intentId)) return; // strict: non-robot orders ignored

        var utcNow = DateTimeOffset.UtcNow;
        var orderState = order.OrderState;

        if (!_orderMap.TryGetValue(intentId, out var orderInfo))
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
            
            _executionJournal.RecordRejection(intentId, "", "", $"ORDER_REJECTED: {fullErrorMsg}", utcNow);
            orderInfo.State = "REJECTED";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_REJECTED",
                new 
                { 
                    broker_order_id = order.OrderId, 
                    error = fullErrorMsg,
                    error_code = errorCode,
                    comment = comment,
                    error_details = errorDetails
                }));
        }
        else if (orderState == OrderState.Cancelled)
        {
            orderInfo.State = "CANCELLED";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_CANCELLED",
                new { broker_order_id = order.OrderId }));
        }
    }

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
        
        if (!_intentMap.TryGetValue(intentId, out var intent))
        {
            LogOrphanFill(intentId, encodedTag, orderType, instrument, fillPrice, fillQuantity, 
                utcNow, reason: "INTENT_NOT_FOUND");
            
            // Stand down stream if known, otherwise block instrument
            // Try to get stream from orderInfo if available, but likely unknown
            _standDownStreamCallback?.Invoke("", utcNow, $"ORPHAN_FILL:INTENT_NOT_FOUND:{intentId}");
            
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
                    action_taken = "EXECUTION_BLOCKED",
                    note = "Intent not found in _intentMap - orphan fill logged, execution blocked"
                }));
            
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

        var encodedTag = GetOrderTag(order);
        var intentId = RobotOrderIds.DecodeIntentId(encodedTag);
        var utcNow = DateTimeOffset.UtcNow;
        
        // CRITICAL: Log when orders are ignored due to missing/invalid tags
        if (string.IsNullOrEmpty(intentId))
        {
            var ignoredFillPrice = (decimal)execution.Price;
            var ignoredFillQuantity = execution.Quantity;
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", "", "EXECUTION_UPDATE_IGNORED_NO_TAG",
                new 
                { 
                    error = "Execution update ignored - order tag missing or invalid",
                    broker_order_id = order.OrderId,
                    order_tag = encodedTag ?? "NULL",
                    fill_price = ignoredFillPrice,
                    fill_quantity = ignoredFillQuantity,
                    instrument = order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN",
                    note = "Order may not be robot-managed or tag was not set correctly"
                }));
            return; // strict: non-robot orders ignored
        }

        var fillPrice = (decimal)execution.Price;
        var fillQuantity = execution.Quantity;

        if (!_orderMap.TryGetValue(intentId, out var orderInfo))
        {
            // CRITICAL FIX: Handle execution updates (fills) for untracked orders gracefully
            // This can happen if:
            // 1. Order was rejected before being tracked but still got filled
            // 2. Order tracking failed but execution update arrived
            // 3. Multiple execution updates for same order (race condition)
            // Log as INFO (not WARN) since this is often expected for rejected orders that still fill
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, order.Instrument?.MasterInstrument?.Name ?? "", "EXECUTION_UPDATE_UNKNOWN_ORDER",
                new 
                { 
                    error = "Order not found in tracking map", 
                    broker_order_id = order.OrderId, 
                    tag = encodedTag,
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    instrument = order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN",
                    note = "Execution update (fill) received for untracked order - may indicate order was rejected before tracking or tracking race condition",
                    severity = "INFO" // Not an error - often expected for rejected orders that still fill
                }));
            return;
        }

        // Track cumulative fills for partial-fill safety
        orderInfo.FilledQuantity += fillQuantity;
        var filledTotal = orderInfo.FilledQuantity;
        
        // Get expectation for fill accounting
        var expectedQty = orderInfo.ExpectedQuantity > 0 ? orderInfo.ExpectedQuantity : 
            (_intentPolicy.TryGetValue(intentId, out var exp) ? exp.ExpectedQuantity : 0);
        var maxQty = orderInfo.MaxQuantity > 0 ? orderInfo.MaxQuantity :
            (_intentPolicy.TryGetValue(intentId, out var exp2) ? exp2.MaxQuantity : 0);
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
        if (!ResolveIntentContextOrFailClosed(intentId, encodedTag, orderInfo.OrderType, orderInfo.Instrument, 
            fillPrice, fillQuantity, utcNow, out var context))
        {
            // Context resolution failed - orphan fill logged and execution blocked
            // Do NOT call journal with empty strings
            return; // Fail-closed
        }
        
        // Explicit Entry vs Exit Classification
        if (orderInfo.IsEntryOrder == true)
        {
            // Entry fill
            _executionJournal.RecordEntryFill(
                context.IntentId, 
                context.TradingDate, 
                context.Stream,
                fillPrice, 
                fillQuantity,  // DELTA ONLY - not filledTotal
                utcNow, 
                context.ContractMultiplier, 
                context.Direction,
                context.ExecutionInstrument, 
                context.CanonicalInstrument);
            
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
            
            // Register entry fill with coordinator
            if (_intentMap.TryGetValue(intentId, out var entryIntent))
            {
                // Register exposure with coordinator
                _coordinator?.OnEntryFill(intentId, filledTotal, entryIntent.Stream, entryIntent.Instrument, entryIntent.Direction ?? "", utcNow);
                
                // Ensure we protect the currently filled quantity (no market-close gating)
                HandleEntryFill(intentId, entryIntent, fillPrice, filledTotal, utcNow);
            }
            else
            {
                // CRITICAL: Intent not found - protective orders cannot be placed
                // EMERGENCY FIX: Flatten position immediately to prevent unprotected exposure
                // Note: Cannot get stream from OrderInfo (it doesn't have Stream property) - use empty string
                    _log.Write(RobotEvents.EngineBase(utcNow, context.TradingDate, "EXECUTION_ERROR", "ENGINE",
                    new 
                    { 
                        error = "Intent not found in _intentMap after context resolution - defensive check",
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
                    
                        _log.Write(RobotEvents.EngineBase(utcNow, context.TradingDate, "INTENT_NOT_FOUND_FLATTENED", "ENGINE",
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
                        }));
                    
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
                    _log.Write(RobotEvents.EngineBase(utcNow, context.TradingDate, "INTENT_NOT_FOUND_FLATTEN_FAILED", "ENGINE",
                        new
                        {
                            intent_id = intentId,
                            instrument = orderInfo.Instrument,
                            stream = context.Stream,
                            error = ex.Message,
                            exception_type = ex.GetType().Name,
                            note = "Failed to flatten position after intent not found - manual intervention may be required"
                        }));
                }
            }
        }
        else if (orderInfo.OrderType == "STOP" || orderInfo.OrderType == "TARGET")
        {
            // Exit fill
            _executionJournal.RecordExitFill(
                context.IntentId, 
                context.TradingDate, 
                context.Stream,
                fillPrice, 
                fillQuantity,  // DELTA ONLY - not filledTotal
                orderInfo.OrderType, 
                utcNow);
            
            // Log exit fill event
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_EXIT_FILL",
                new
                {
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    filled_total = filledTotal,
                    broker_order_id = order.OrderId,
                    exit_order_type = orderInfo.OrderType,
                    stream = context.Stream
                }));
            
            // Register exit fill with coordinator
            _coordinator?.OnExitFill(intentId, filledTotal, utcNow);
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
                    note = "Unknown exit order type - orphan fill logged"
                }));
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
                var changed = false;
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
                            // Both attempts failed - reject change
                            var errorMsg = $"Stop order change failed: {ex.Message} (fallback also failed: {fallbackEx.Message})";
                            _executionJournal.RecordRejection(intentId, "", "", $"STOP_CHANGE_FAILED: {errorMsg}", utcNow);
                            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CHANGE_FAIL", new
                            {
                                error = errorMsg,
                                first_error = ex.Message,
                                fallback_error = fallbackEx.Message,
                                order_type = "PROTECTIVE_STOP",
                                broker_order_id = existingStop.OrderId,
                                account = "SIM",
                                exception_type = ex.GetType().Name,
                                fallback_exception_type = fallbackEx.GetType().Name
                            }));
                            return OrderSubmissionResult.FailureResult(errorMsg, utcNow);
                        }
                    }
                    if (changeRes == null || changeRes.Length == 0 || changeRes[0].OrderState == OrderState.Rejected)
                    {
                        dynamic dynChangeRes = changeRes?[0];
                        var err = (string?)dynChangeRes?.ErrorMessage ?? (string?)dynChangeRes?.Error ?? "Stop order change rejected";
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

            // Real NT API: Create stop order using official NT8 CreateOrder factory method
            var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
            // stopPriceD is already computed above before idempotency check
            
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
                    ocoGroup,                               // Oco (OCO group for pairing with target order)
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
                _executionJournal.RecordRejection(intentId, "", "", $"STOP_SUBMIT_FAILED: {ex.Message}", utcNow);
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
                var changed = false;
                var existingTargetPriceD = (double)targetPrice;
                if (existingTarget.Quantity != quantity)
                {
                    existingTarget.Quantity = quantity;
                    changed = true;
                }
                if (Math.Abs(existingTarget.LimitPrice - existingTargetPriceD) > 1e-10)
                {
                    existingTarget.LimitPrice = existingTargetPriceD;
                    changed = true;
                }

                if (changed)
                {
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
                            // Both attempts failed - reject change
                            var errorMsg = $"Target order change failed: {ex.Message} (fallback also failed: {fallbackEx.Message})";
                            _executionJournal.RecordRejection(intentId, "", "", $"TARGET_CHANGE_FAILED: {errorMsg}", utcNow);
                            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CHANGE_FAIL", new
                            {
                                error = errorMsg,
                                first_error = ex.Message,
                                fallback_error = fallbackEx.Message,
                                order_type = "PROTECTIVE_TARGET",
                                broker_order_id = existingTarget.OrderId,
                                account = "SIM",
                                exception_type = ex.GetType().Name,
                                fallback_exception_type = fallbackEx.GetType().Name
                            }));
                            return OrderSubmissionResult.FailureResult(errorMsg, utcNow);
                        }
                    }
                    if (changeRes == null || changeRes.Length == 0 || changeRes[0].OrderState == OrderState.Rejected)
                    {
                        dynamic dynChangeRes = changeRes?[0];
                        var err = (string?)dynChangeRes?.ErrorMessage ?? (string?)dynChangeRes?.Error ?? "Target order change rejected";
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
            dynamic dynAccountTarget = account;
            Order[] result;
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
                dynamic dynResult = result?[0];
                var error = (string?)dynResult?.ErrorMessage ?? (string?)dynResult?.Error ?? "Target order rejected";
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
                GetOrderTag(o) == stopTag &&
                (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

            if (stopOrder == null)
            {
                var error = "Stop order not found for BE modification";
                return OrderModificationResult.FailureResult(error, utcNow);
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
                
                var tag = GetOrderTag(order) ?? "";
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
                    var tag = GetOrderTag(order) ?? "";
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
            // Get position for this instrument - use dynamic to handle different API signatures
            dynamic dynAccountFlatten = account;
            Position? position = null;
            try
            {
                position = dynAccountFlatten.GetPosition(ntInstrument);
            }
            catch
            {
                // Try alternative signature - GetPosition might take instrument name string
                try
                {
                    position = dynAccountFlatten.GetPosition(ntInstrument.MasterInstrument.Name);
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
            
            // Flatten - use dynamic to handle different API signatures
            try
            {
                dynAccountFlatten.Flatten(ntInstrument);
            }
            catch
            {
                // Try alternative signature - Flatten might take ICollection<Instrument>
                try
                {
                    dynAccountFlatten.Flatten(new[] { ntInstrument });
                }
                catch
                {
                    // Try with instrument name string
                    dynAccountFlatten.Flatten(ntInstrument.MasterInstrument.Name);
                }
            }
            
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
                _executionJournal.RecordRejection(intentId, "", "", $"STOP_PRICE_VALIDATION_FAILED: {priceValidationReason}", utcNow);
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
            // Note: LastPrice is not directly available on MasterInstrument in NinjaTrader API
            // This validation is optional - NinjaTrader will reject invalid orders anyway
            try
            {
                // Try to get current price through dynamic access (may not be available)
                double currentPrice = 0.0;
                try
                {
                    dynamic dynInstrument = ntInstrument.MasterInstrument;
                    currentPrice = (double)(dynInstrument?.LastPrice ?? 0.0);
                }
                catch
                {
                    // LastPrice not available - skip validation, let NinjaTrader handle it
                    currentPrice = 0.0;
                }
                
                if (currentPrice > 0)
                {
                    bool isValidStop = false;
                    string validationError = "";
                    
                    if (orderAction == OrderAction.SellShort || orderAction == OrderAction.Sell)
                    {
                        // Short entry: stop must be BELOW current price
                        isValidStop = stopPriceD < currentPrice;
                        if (!isValidStop)
                        {
                            validationError = $"Sell Short Stop Market order rejected: stop price {stopPriceD} must be BELOW current price {currentPrice}. " +
                                            $"Current price has already fallen below breakout level.";
                        }
                    }
                    else if (orderAction == OrderAction.Buy)
                    {
                        // Long entry: stop must be ABOVE current price
                        isValidStop = stopPriceD > currentPrice;
                        if (!isValidStop)
                        {
                            validationError = $"Buy Stop Market order rejected: stop price {stopPriceD} must be ABOVE current price {currentPrice}. " +
                                            $"Current price has already risen above breakout level.";
                        }
                    }
                    
                    if (!isValidStop)
                    {
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_STOP_PRICE_INVALID", new
                        {
                            error = validationError,
                            order_type = "StopMarket",
                            order_action = orderAction.ToString(),
                            quantity = quantity,
                            stop_price = stopPriceD,
                            current_price = currentPrice,
                            instrument = instrument,
                            intent_id = intentId,
                            account = "SIM",
                            note = "Stop price validation failed - order would be rejected by NinjaTrader"
                        }));
                        return OrderSubmissionResult.FailureResult(validationError, utcNow);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log warning but don't block order submission if we can't get current price
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_STOP_PRICE_VALIDATION_WARNING", new
                {
                    warning = $"Could not validate stop price against current market price: {ex.Message}",
                    order_type = "StopMarket",
                    order_action = orderAction.ToString(),
                    stop_price = stopPriceD,
                    instrument = instrument,
                    intent_id = intentId,
                    note = "Proceeding with order submission - NinjaTrader will validate"
                }));
            }
            
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
                    _executionJournal.RecordRejection(intentId, "", "", $"ENTRY_STOP_SUBMIT_FAILED: {errorMsg}", utcNow);
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
