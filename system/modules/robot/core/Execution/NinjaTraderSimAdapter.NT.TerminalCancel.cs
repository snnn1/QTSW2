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
    /// Terminalize an intent: cancel all protective orders, verify invariant, remove from management.
    /// Single canonical path for target fill, stop fill, flatten, and reconciliation completion.
    /// Completed intent = terminal object - no further order management allowed.
    /// </summary>
    private void TerminalizeIntent(string intentId, string tradingDate, string stream, string completionReason, DateTimeOffset utcNow)
    {
        if (!IsStrategyThreadContext() && _ntActionQueue != null)
        {
            EnqueueTerminalProtectiveCleanup(intentId, tradingDate, stream, completionReason, utcNow);
            return;
        }

        CancelProtectiveOrdersForIntent(intentId, utcNow);
        VerifyTerminalProtectiveCleanup(intentId, tradingDate, stream, completionReason, utcNow);
    }

    private void EnqueueTerminalProtectiveCleanup(string intentId, string tradingDate, string stream, string completionReason, DateTimeOffset utcNow)
    {
        var cmd = new NtCancelOrdersCommand(
            $"TERMINAL_CANCEL_PROTECTIVE:{intentId}",
            intentId,
            null,
            protectiveOrdersOnly: true,
            reason: "TERMINAL_INTENT_PROTECTIVE_CLEANUP",
            utcNow,
            preferUrgentDrain: true,
            verifyWorkingProtectivesClearedAfter: true,
            postCancelTradingDate: tradingDate,
            postCancelStream: stream,
            postCancelCompletionReason: completionReason);

        EnqueueNtActionInternal(cmd);
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "TERMINAL_INTENT_PROTECTIVE_CLEANUP_ENQUEUED", "ENGINE",
            new
            {
                intent_id = intentId,
                stream = stream,
                completion_reason = completionReason,
                correlation_id = cmd.CorrelationId,
                note = "Terminal protective cleanup routed onto strategy thread before touching NT account/orders."
            }));
    }

    private void VerifyTerminalProtectiveCleanup(string intentId, string tradingDate, string stream, string completionReason, DateTimeOffset utcNow)
    {
        var workingOrders = GetWorkingProtectiveOrdersForIntent(intentId);
        if (workingOrders.Count > 0)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "TERMINAL_INTENT_PROTECTIVE_CLEANUP_PENDING", "ENGINE",
                new
                {
                    intent_id = intentId,
                    stream = stream,
                    completion_reason = completionReason,
                    working_order_ids = workingOrders.Select(o => o.OrderId).ToList(),
                    working_order_types = workingOrders.Select(o => GetOrderTag(o)).ToList(),
                    count = workingOrders.Count,
                    action = "CANCEL_PENDING",
                    note = "Terminal intent cancel submitted; protective order state can remain accepted/working until NT emits cancel updates."
                }));
        }
        else
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "TERMINAL_INTENT_VERIFIED", "ENGINE",
                new { intent_id = intentId, stream = stream, completion_reason = completionReason }));
        }
    }

    /// <summary>
    /// Get working (Accepted/Working) protective orders for an intent.
    /// </summary>
    private List<Order> GetWorkingProtectiveOrdersForIntent(string intentId)
    {
        var result = new List<Order>();
        if (_ntAccount == null) return result;
        var account = _ntAccount as Account;
        if (account == null) return result;

        var stopTag = RobotOrderIds.EncodeStopTag(intentId);
        var targetTag = RobotOrderIds.EncodeTargetTag(intentId);

        foreach (var order in SnapshotAccountOrders(account))
        {
            if (order.OrderState != OrderState.Working && order.OrderState != OrderState.Accepted)
                continue;
            var tag = GetOrderTag(order) ?? "";
            if (tag == stopTag || tag == targetTag)
                result.Add(order);
        }
        return result;
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

            foreach (var order in SnapshotAccountOrders(account))
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
                var ordersArr = ordersToCancel.ToArray();
                if (!EnsureStrategyThreadOrEnqueue("CancelProtectiveOrdersForIntent", intentId, null, $"CANCEL_PROTECTIVE:{intentId}", () => account.Cancel(ordersArr)))
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", "PROTECTIVE_CANCEL_ENQUEUED", new
                    {
                        intent_id = intentId,
                        cancel_count = ordersArr.Length,
                        order_ids = ordersArr.Select(o => o.OrderId).ToList(),
                        note = "Protective cancel queued onto strategy thread."
                    }));
                    return;
                }

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
            foreach (var order in SnapshotAccountOrders(account))
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
                var ordersArr = ordersToCancel.ToArray();
                if (!EnsureStrategyThreadOrEnqueue("CancelIntentOrdersReal", intentId, null, $"CANCEL_INTENT:{intentId}", () => account.Cancel(ordersArr)))
                    return false;
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

        var flattenIntentIdForGate = string.Equals(intentId, "NT_FLATTEN", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(intentId, "EMERGENCY_BLOCK", StringComparison.OrdinalIgnoreCase)
            ? null
            : intentId;
        if (!TryExecutionSafetyFlattenGuard(instrument, flattenIntentIdForGate, utcNow, "FLATTEN_INTENT_REAL", null, out _))
            return FlattenResult.FailureResult("EXECUTION_BLOCKED_UNSAFE_STATE", utcNow);

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
            if (_intentMap.TryGetValue(intentId, out var flattenedIntent))
            {
                // Find the stream and trading date from the flattened intent
                var stream = flattenedIntent.Stream ?? "";
                var tradingDate = flattenedIntent.TradingDate ?? "";

                if (!string.IsNullOrEmpty(stream) && !string.IsNullOrEmpty(tradingDate))
                {
                    // Find both entry intents (long and short) for this stream
                    var entryIntentIds = new List<string>();
                    foreach (var kvp in _intentMap)
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
    /// Check ALL instruments for flat positions and cancel entry stop orders.
    /// Called periodically to detect manual position closures that bypass robot code.
    /// </summary>
    partial void OnCheckAllInstrumentsForFlatPositions(DateTimeOffset utcNow)
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

        try
        {
            // Get unique set of instruments from _intentMap
            var instrumentsToCheck = new HashSet<string>();
            foreach (var kvp in _intentMap)
            {
                var intent = kvp.Value;
                if (!string.IsNullOrEmpty(intent.Instrument))
                {
                    instrumentsToCheck.Add(intent.Instrument);
                }
            }

            // Check each instrument
            foreach (var instrument in instrumentsToCheck)
            {
                CheckAndCancelEntryStopsOnPositionFlat(instrument, utcNow);
            }
        }
        catch (Exception ex)
        {
            // Log error but don't throw - this is a safety check, not critical path
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CHECK_ALL_INSTRUMENTS_FLAT_ERROR", state: "ENGINE",
                new
                {
                    error = ex.Message,
                    exception_type = ex.GetType().Name,
                    note = "Error checking all instruments for flat positions - entry stop cancellation skipped"
                }));
        }
    }

    /// <summary>
    /// Record an orphan fill to both the ownership ledger and the orphan fill journal.
    /// Returns the orphan slot ID (or null if feature disabled) so the caller can
    /// wire <see cref="InstrumentOwnershipLedger.UpdateOrphanFlattenResult"/> after the flatten attempt.
    /// </summary>
    private string? RecordOrphanFillIfEnabled(string instrument, string brokerOrderId, string intentId,
        decimal fillPrice, int fillQuantity, DateTimeOffset utcNow, OrphanReason reason,
        SlotDirection direction, OrphanActionTaken actionTaken = OrphanActionTaken.FlattenAttempted)
    {
        if (!FeatureFlags.CanonicalOwnershipLedgerEnabled) return null;

        var result = _ownershipLedger?.RecordOrphanFill(
            GetLedgerAccountName(), instrument.Trim(), brokerOrderId, direction, fillQuantity, utcNow, reason);
        var orphanSlotId = result?.OrphanSlotId ?? $"ORPHAN_{brokerOrderId}_{utcNow.Ticks}";

        var tradingDate = GetAuditTradingDate(utcNow);
        _orphanFillJournal?.RecordOrphanFill(new OrphanFillRecord
        {
            BrokerOrderId = brokerOrderId,
            IntentIdIfKnown = intentId,
            Instrument = instrument.Trim(),
            FillPrice = fillPrice,
            FillQty = fillQuantity,
            FillUtc = utcNow.ToString("o"),
            TradingDate = tradingDate,
            OrphanReason = reason,
            ActionTaken = actionTaken,
            OwnershipLedgerSlotId = orphanSlotId,
            RecordedUtc = utcNow.ToString("o"),
            Direction = direction
        });

        return orphanSlotId;
    }

    private void NotifyOrphanFlattenResult(string instrument, string? orphanSlotId,
        bool flattenSucceeded, string? flattenError, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.CanonicalOwnershipLedgerEnabled || string.IsNullOrEmpty(orphanSlotId)) return;

        _ownershipLedger?.UpdateOrphanFlattenResult(
            GetLedgerAccountName(), instrument.Trim(), orphanSlotId!, flattenSucceeded, utcNow);

        var td = GetAuditTradingDate(utcNow);
        var action = flattenSucceeded ? OrphanActionTaken.FlattenSucceeded : OrphanActionTaken.FlattenFailed;
        _orphanFillJournal?.RecordOrphanFlattenResult(td, orphanSlotId!, action, flattenError, utcNow);
    }

    private static SlotDirection ParseSlotDirection(string? direction)
    {
        return string.Equals(direction, "Short", StringComparison.OrdinalIgnoreCase)
            ? SlotDirection.Short
            : SlotDirection.Long;
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
                // GUARD: Only cancel when at least one entry has filled for this instrument (post-entry cleanup).
                // Pre-entry RANGE_LOCKED streams have valid entry orders waiting for breakout - do NOT cancel.
                bool anyEntryFilledForInstrument = false;
                foreach (var kvp in _intentMap)
                {
                    var intent = kvp.Value;
                    if (intent.Instrument != instrument)
                        continue;
                    if (intent.TriggerReason == null ||
                        (!intent.TriggerReason.Contains("ENTRY_STOP_BRACKET_LONG") &&
                         !intent.TriggerReason.Contains("ENTRY_STOP_BRACKET_SHORT")))
                        continue;
                    if (_executionJournal != null && !string.IsNullOrEmpty(intent.TradingDate) && !string.IsNullOrEmpty(intent.Stream))
                    {
                        var entry = _executionJournal.GetEntry(kvp.Key, intent.TradingDate, intent.Stream);
                        if (entry != null && (entry.EntryFilled || entry.EntryFilledQuantityTotal > 0))
                        {
                            anyEntryFilledForInstrument = true;
                            break;
                        }
                    }
                }
                if (!anyEntryFilledForInstrument)
                    return; // Pre-entry state (e.g. RANGE_LOCKED waiting) - do not cancel valid entry orders

                // Find all entry intents for this instrument that haven't filled yet
                var entryIntentIdsToCancel = new List<string>();

                foreach (var kvp in _intentMap)
                {
                    var intent = kvp.Value;

                    // Match instrument
                    if (intent.Instrument != instrument)
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
            foreach (var order in SnapshotAccountOrders(account))
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
    /// Cancel specific orders by order ID (stream-scoped recovery).
    /// </summary>
    private void CancelOrdersReal(List<string> orderIds, DateTimeOffset utcNow)
    {
        if (_ntAccount == null || orderIds == null || orderIds.Count == 0)
            return;

        var account = _ntAccount as Account;
        if (account == null)
            return;

        var orderIdSet = new HashSet<string>(orderIds, StringComparer.OrdinalIgnoreCase);
        var ordersToCancel = new List<Order>();

        foreach (var order in SnapshotAccountOrders(account))
        {
            if (order.OrderState != OrderState.Working && order.OrderState != OrderState.Accepted)
                continue;
            if (orderIdSet.Contains(order.OrderId))
                ordersToCancel.Add(order);
        }

        if (ordersToCancel.Count > 0)
        {
            try
            {
                account.Cancel(ordersToCancel.ToArray());
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENTRY_ORDER_SET_CANCEL_CONFIRMED", state: "ENGINE",
                    new { cancelled_order_ids = ordersToCancel.Select(o => o.OrderId).ToList(), note = "Stream-scoped entry orders cancelled" }));
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_ROBOT_ORDERS_ERROR", state: "ENGINE",
                    new { error = ex.Message, order_ids = orderIds, note = "Failed to cancel specific orders" }));
            }
        }
    }

}

#endif
