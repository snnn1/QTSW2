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
            var stopOrder = SnapshotAccountOrders(account).FirstOrDefault(o =>
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

            // Get Intent and previous stop price for BE modification context
            var (tradingDate8, stream8, intentEntryPrice2, intentStopPrice3, intentTargetPrice3, _, _) = GetIntentInfo(intentId);
            decimal? previousStopPrice = null;
            decimal? beTriggerPrice = null;
            
            // Get previous stop price from journal entry
            var journalPath = System.IO.Path.Combine(RobotRunArtifactPaths.StateExecutionJournals(_stateRoot), $"{tradingDate8}_{stream8}_{intentId}.json");
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
            if (_intentMap.TryGetValue(intentId, out var beIntent))
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

}

#endif
