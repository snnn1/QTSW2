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
    /// GC FIX: Cancel protective orders (stop and target) for an intent when quantity changes are needed.
    /// <summary>
    /// Terminalize an intent: cancel all protective orders, verify invariant, remove from management.
    /// Single canonical path for target fill, stop fill, flatten, and reconciliation completion.
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
                var ordersArr = ordersToCancel.ToArray();
                if (!EnsureStrategyThreadOrEnqueue("CancelProtectiveOrdersForRecreate", intentId, null, $"CANCEL_PROTECTIVE:{intentId}", () => account.Cancel(ordersArr)))
                    return;
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
    /// Emergency flatten when IEA is blocked. P2.6.6: always NtFlattenInstrumentCommand → ExecuteFlattenInstrument (policy funnel).
    /// </summary>
    public FlattenResult FlattenEmergency(string instrument, DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_EMERGENCY_ON_BLOCK", state: "ENGINE",
            new { instrument, note = "Flatten on block — enqueued via NtFlattenInstrumentCommand (policy funnel)" }));
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            EnqueueEmergencyFlattenProtective(instrument, utcNow);
            return FlattenResult.FailureResult("Enqueued for strategy thread", utcNow);
        }
        return FlattenIntentReal("EMERGENCY_BLOCK", instrument, utcNow);
    }

    /// <summary>
    /// Flatten exposure for a specific intent only using real NT API.
    /// P2.6.7: pre-submit policy is mandatory unless <paramref name="policyPrechecked"/> is minted by ExecuteFlattenInstrument (token + instrument invariants).
    /// </summary>
    private FlattenResult FlattenIntentReal(string intentId, string instrument, DateTimeOffset utcNow, NtDestructivePolicyAlreadyAppliedToken? policyPrechecked = null, DestructiveActionSource? destructiveSourceOverride = null, DestructiveTriggerReason? explicitTriggerOverride = null)
    {
        if (System.Threading.Volatile.Read(ref _sessionMismatchBlocked) != 0)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SESSION_IDENTITY_FLATTEN_ALLOWED_WHILE_LATCHED", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    instrument,
                    submit_path = "flatten_intent_real",
                    note = "Session identity latch remains armed for opening submits, but flatten is exposure-reducing."
                }));
        }
        PurgePendingBEForIntent(intentId, utcNow, instrument, "flatten");
        if (!string.IsNullOrEmpty(intentId) && intentId != "NT_FLATTEN" && intentId != "EMERGENCY_BLOCK")
            PruneIntentState(intentId, "flatten");
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
            // Get position for this instrument - use dynamic to handle different API signatures
            dynamic dynAccountPos = account;
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
                    position = dynAccountPos.GetPosition(ntInstrument);
                }
            }
            catch
            {
                // Try alternative signature - GetPosition might take instrument name string
                try
                {
                    if (!string.IsNullOrEmpty(instrumentName))
                    {
                        position = dynAccountPos.GetPosition(instrumentName);
                    }
                }
                catch
                {
                    // If GetPosition fails, try to flatten anyway
                }
            }

            // Do not short-circuit on chart GetPosition alone — canonical bucket may still have exposure (multi-contract / identity fix).

            // IEA Flatten Authority: Use position-derived market order instead of Account.Flatten.
            // Position query + decision + submission as one critical section on strategy thread.
            var resultHolder = new[] { FlattenResult.FailureResult("Not executed", utcNow) };
            if (!EnsureStrategyThreadOrEnqueue("FlattenIntentReal", intentId, instrument, $"FLATTEN:{intentId}:{utcNow:yyyyMMddHHmmssfff}", () =>
            {
                var exposure = (this as IIEAOrderExecutor).GetBrokerCanonicalExposure(instrument);
                if (exposure.ReconciliationAbsQuantityTotal == 0)
                {
                    resultHolder[0] = FlattenResult.SuccessResult(utcNow);
                    return;
                }
                var absForPolicy = exposure.ReconciliationAbsQuantityTotal;

                if (policyPrechecked.HasValue)
                {
                    if (!string.Equals(instrument, policyPrechecked.Value.ExpectedInstrumentKey, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "DESTRUCTIVE_POLICY_INVARIANT_VIOLATION", new
                        {
                            drift_kind = "policy_token_instrument_mismatch",
                            token_expected = policyPrechecked.Value.ExpectedInstrumentKey,
                            actual_instrument = instrument,
                            correlation_id = policyPrechecked.Value.CorrelationId,
                            note = "P2.6.7: NtDestructivePolicyAlreadyAppliedToken does not match instrument — refusing skip"
                        }));
                        resultHolder[0] = FlattenResult.FailureResult("P2.6.7: destructive policy token instrument mismatch", utcNow);
                        return;
                    }
                    var ct = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    if (_strategyThreadContextCount <= 0 || _strategyThreadId != ct)
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "DESTRUCTIVE_POLICY_INVARIANT_VIOLATION", new
                        {
                            drift_kind = "policy_token_wrong_thread",
                            correlation_id = policyPrechecked.Value.CorrelationId,
                            note = "P2.6.7: policy preclear token requires strategy thread context"
                        }));
                        resultHolder[0] = FlattenResult.FailureResult("P2.6.7: policy token requires strategy thread", utcNow);
                        return;
                    }
                }

                if (!policyPrechecked.HasValue)
                {
                    var execKey = _iea?.ExecutionInstrumentKey ?? instrument;
                    var journalQty = _executionJournal != null
                        ? SumOpenJournalForInstrument(instrument, execKey)
                        : 0;
                    var src = destructiveSourceOverride
                              ?? (intentId == "EMERGENCY_BLOCK" ? DestructiveActionSource.EMERGENCY : DestructiveActionSource.MANUAL);
                    DestructiveTriggerReason? expl = explicitTriggerOverride;
                    if (src == DestructiveActionSource.EMERGENCY)
                        expl ??= DestructiveTriggerReason.IEA_ENQUEUE_FAILURE;
                    else if (src == DestructiveActionSource.FAIL_CLOSED)
                        expl ??= DestructiveTriggerReason.FAIL_CLOSED;
                    else
                        expl ??= DestructiveTriggerReason.MANUAL;
                    var polIn = new DestructiveActionPolicyInput
                    {
                        Source = src,
                        RecoveryReasonString = intentId,
                        ExplicitTrigger = expl,
                        ExecutionInstrumentKey = execKey,
                        BrokerPositionQty = absForPolicy,
                        JournalOpenQtySum = journalQty,
                        ReconstructionActionKind = RecoveryActionKind.Flatten,
                        ManualInstrumentFlatten = src is DestructiveActionSource.MANUAL or DestructiveActionSource.COMMAND,
                        BootstrapAdministrativeFlatten = src == DestructiveActionSource.BOOTSTRAP
                    };
                    var tr = DestructiveTriggerParser.Resolve(polIn.ExplicitTrigger, polIn.RecoveryReasonString);
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_REQUESTED", state: "ENGINE", new
                    {
                        source = src.ToString(),
                        trigger = tr.ToString(),
                        execution_instrument_key = execKey,
                        phase = "flatten_intent_real_pre_submit"
                    }));
                    var polDec = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(polIn);
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_DECISION", state: "ENGINE", new
                    {
                        allowed = polDec.AllowInstrumentScope,
                        reason = polDec.ReasonCode,
                        scope = polDec.CancelScopeMode,
                        policy_path = polDec.PolicyPath,
                        phase = "flatten_intent_real_pre_submit"
                    }));
                    if (!polDec.AllowInstrumentScope)
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_BLOCKED", state: "ENGINE", new
                        {
                            execution_instrument_key = execKey,
                            reason_code = polDec.ReasonCode,
                            note = "P2.6.6: FlattenIntentReal blocked by policy (no order submitted)"
                        }));
                        resultHolder[0] = FlattenResult.FailureResult($"Destructive policy denied flatten ({polDec.ReasonCode})", utcNow);
                        return;
                    }
                }

                OrderSubmissionResult? submitResult = null;
                for (var li = 0; li < exposure.Legs.Count; li++)
                {
                    var leg = exposure.Legs[li];
                    if (leg.SignedQuantity == 0) continue;
                    var qty = leg.SignedQuantity;
                    var absQty = Math.Abs(qty);
                    var dir = qty > 0 ? "Long" : "Short";
                    var chosenSide = dir.Equals("Long", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY";
                    var snapshot = new FlattenDecisionSnapshot
                    {
                        Instrument = instrument,
                        AccountQuantityAtDecision = qty,
                        AccountDirectionAtDecision = dir,
                        RequestedReason = intentId,
                        CallerContext = "FlattenIntentReal",
                        ChosenSide = chosenSide,
                        ChosenQuantity = absQty,
                        LatchRequestId = $"FLATTEN:{intentId}:{utcNow:yyyyMMddHHmmssfff}:L{li}",
                        DecisionUtc = utcNow,
                        FlattenLegIndex = li,
                        CanonicalExposureAbsTotalAtDecision = absForPolicy,
                        LegContractLabel = leg.ContractLabel,
                        BrokerMarketPositionAtDecision = leg.BrokerMarketPosition
                    };
                    submitResult = (this as IIEAOrderExecutor).SubmitFlattenOrder(instrument, chosenSide, absQty, snapshot, utcNow, leg.NativeInstrument);
                    if (!submitResult.Success)
                    {
                        resultHolder[0] = FlattenResult.FailureResult(submitResult.ErrorMessage ?? "Flatten order failed", utcNow);
                        return;
                    }
                    var execKey2 = _iea?.ExecutionInstrumentKey ?? instrument;
                    var src2 = destructiveSourceOverride
                               ?? (intentId == "EMERGENCY_BLOCK" ? DestructiveActionSource.EMERGENCY : DestructiveActionSource.MANUAL);
                    var tr2 = DestructiveTriggerParser.Resolve(explicitTriggerOverride ?? (src2 == DestructiveActionSource.EMERGENCY ? DestructiveTriggerReason.IEA_ENQUEUE_FAILURE : DestructiveTriggerReason.MANUAL), intentId);
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_EXECUTED", state: "ENGINE", new
                    {
                        execution_instrument_key = execKey2,
                        source = src2.ToString(),
                        trigger = tr2.ToString(),
                        phase = "flatten_intent_real_order_submitted",
                        leg_index = li
                    }));
                }
                resultHolder[0] = submitResult != null && submitResult.Success
                    ? FlattenResult.SuccessResult(utcNow)
                    : FlattenResult.FailureResult(submitResult?.ErrorMessage ?? "Flatten order failed", utcNow);
            }))
            {
                return FlattenResult.FailureResult("Enqueued for strategy thread", utcNow);
            }
            var flattenSucceeded = resultHolder[0].Success;
            if (!flattenSucceeded)
            {
                return resultHolder[0];
            }

            var postExposure = (this as IIEAOrderExecutor).GetBrokerCanonicalExposure(instrument);

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
                            SetPendingEntryTerminationReason(entryIntentId, "flattened");
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
                    canonical_broker_key = postExposure.CanonicalKey,
                    reconciliation_abs_remaining = postExposure.ReconciliationAbsQuantityTotal,
                    chart_position_qty = position?.Quantity,
                    note = "After flatten submit: canonical abs remaining (may be non-zero until fills); chart qty for reference only"
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

    partial void OnCheckAllInstrumentsForFlatPositions(DateTimeOffset utcNow)
    {
        var inj = _ntInstrument as Instrument;
        var instrument = inj?.MasterInstrument?.Name ?? inj?.FullName ?? "";
        if (!string.IsNullOrEmpty(instrument))
            CheckAndCancelEntryStopsOnPositionFlat(instrument, utcNow);
    }

    /// <summary>
    /// Check if position is flat and cancel all entry stop orders for the instrument.
    /// Called after execution updates to detect manual position closures.
    /// When IEA enabled: enqueues NtCancelOrdersCommand instead of calling account.Cancel on worker.
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

            // If position is flat, cancel entry stop orders when:
            // 1. At least one entry filled (post-entry cleanup) - existing
            // 2. Stream no longer eligible (slot_expired, committed, forced_flatten) - lifecycle cleanup
            if (position != null && position.MarketPosition == MarketPosition.Flat)
            {
                bool anyEntryFilledForInstrument = false;
                var invalidStateIntentsToCancel = new List<(string IntentId, string Stream, string Reason)>();
                foreach (var kvp in IntentMap)
                {
                    var intent = kvp.Value;
                    var intentInstrument = intent.Instrument ?? "";
                    var intentExecutionInstrument = intent.ExecutionInstrument ?? intentInstrument;
                    if (string.IsNullOrEmpty(instrument) ||
                        (string.Compare(intentInstrument, instrument, StringComparison.OrdinalIgnoreCase) != 0 &&
                         string.Compare(intentExecutionInstrument, instrument, StringComparison.OrdinalIgnoreCase) != 0))
                        continue;
                    if (intent.TriggerReason == null ||
                        (!intent.TriggerReason.Contains("ENTRY_STOP_BRACKET_LONG") &&
                         !intent.TriggerReason.Contains("ENTRY_STOP_BRACKET_SHORT")))
                        continue;
                    var entryFilled = false;
                    if (_executionJournal != null && !string.IsNullOrEmpty(intent.TradingDate) && !string.IsNullOrEmpty(intent.Stream))
                    {
                        var entry = _executionJournal.GetEntry(kvp.Key, intent.TradingDate, intent.Stream);
                        entryFilled = entry != null && (entry.EntryFilled || entry.EntryFilledQuantityTotal > 0);
                    }
                    if (entryFilled)
                        anyEntryFilledForInstrument = true;
                    else
                    {
                        // Unfilled - check if stream is no longer eligible (lifecycle cleanup)
                        if (_shouldCancelEntryOrdersForStreamCallback != null && !string.IsNullOrEmpty(intent.Stream) && !string.IsNullOrEmpty(intent.TradingDate))
                        {
                            var (shouldCancel, reason) = _shouldCancelEntryOrdersForStreamCallback(intent.Stream, intent.TradingDate);
                            if (shouldCancel && !string.IsNullOrEmpty(reason))
                                invalidStateIntentsToCancel.Add((kvp.Key, intent.Stream, reason));
                        }
                    }
                }
                if (!anyEntryFilledForInstrument && invalidStateIntentsToCancel.Count == 0)
                    return; // Pre-entry RANGE_LOCKED valid - do not cancel

                // Reset ProtectionState for intents that had position (entry filled) - position now flat
                foreach (var kvp in IntentMap)
                {
                    var i = kvp.Value;
                    var intentInstrument = i.Instrument ?? "";
                    var intentExecInst = i.ExecutionInstrument ?? intentInstrument;
                    if (string.IsNullOrEmpty(instrument) ||
                        (string.Compare(intentInstrument, instrument, StringComparison.OrdinalIgnoreCase) != 0 &&
                         string.Compare(intentExecInst, instrument, StringComparison.OrdinalIgnoreCase) != 0))
                        continue;
                    if (i.TriggerReason == null ||
                        (!i.TriggerReason.Contains("ENTRY_STOP_BRACKET_LONG") && !i.TriggerReason.Contains("ENTRY_STOP_BRACKET_SHORT")))
                        continue;
                    if (_executionJournal != null && !string.IsNullOrEmpty(i.TradingDate) && !string.IsNullOrEmpty(i.Stream))
                    {
                        var je = _executionJournal.GetEntry(kvp.Key, i.TradingDate, i.Stream);
                        if (je != null && (je.EntryFilled || je.EntryFilledQuantityTotal > 0))
                            PruneIntentState(kvp.Key, "position_flat");
                    }
                }
                // Build cancellation list: post-entry cleanup (all unfilled) OR invalid-state lifecycle (stream no longer eligible)
                var entryIntentIdsToCancel = new List<string>();
                var invalidStateReasons = new Dictionary<string, (string Stream, string Reason)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (intentId, stream, reason) in invalidStateIntentsToCancel)
                {
                    entryIntentIdsToCancel.Add(intentId);
                    invalidStateReasons[intentId] = (stream, reason);
                }
                if (anyEntryFilledForInstrument)
                {
                    foreach (var kvp in IntentMap)
                    {
                        var intent = kvp.Value;
                        var intentInstrument = intent.Instrument ?? "";
                        var intentExecutionInstrument = intent.ExecutionInstrument ?? intentInstrument;
                        if (string.IsNullOrEmpty(instrument) ||
                            (string.Compare(intentInstrument, instrument, StringComparison.OrdinalIgnoreCase) != 0 &&
                             string.Compare(intentExecutionInstrument, instrument, StringComparison.OrdinalIgnoreCase) != 0))
                            continue;
                        if (intent.TriggerReason == null ||
                            (!intent.TriggerReason.Contains("ENTRY_STOP_BRACKET_LONG") &&
                             !intent.TriggerReason.Contains("ENTRY_STOP_BRACKET_SHORT")))
                            continue;
                        var entryFilled = false;
                        if (_executionJournal != null && !string.IsNullOrEmpty(intent.TradingDate) && !string.IsNullOrEmpty(intent.Stream))
                        {
                            var journalEntry = _executionJournal.GetEntry(kvp.Key, intent.TradingDate, intent.Stream);
                            entryFilled = journalEntry != null && (journalEntry.EntryFilled || journalEntry.EntryFilledQuantityTotal > 0);
                        }
                        if (!entryFilled && !entryIntentIdsToCancel.Contains(kvp.Key))
                            entryIntentIdsToCancel.Add(kvp.Key);
                    }
                }

                // Cancel all unfilled entry stop orders.
                // NT THREADING FIX: When IEA enabled, worker must not call account.Cancel. Enqueue for strategy thread.
                if (_useInstrumentExecutionAuthority && _ntActionQueue != null)
                {
                    foreach (var entryIntentId in entryIntentIdsToCancel)
                    {
                        var isInvalidState = invalidStateReasons.TryGetValue(entryIntentId, out var inv);
                        if (isInvalidState)
                            SetPendingEntryTerminationReason(entryIntentId, "no_fill");
                        var cid = isInvalidState ? $"CANCEL:{entryIntentId}:INVALID_STATE" : $"CANCEL:{entryIntentId}:POSITION_FLAT_CLEANUP";
                        _ntActionQueue.EnqueueNtAction(new NtCancelOrdersCommand(cid, entryIntentId, instrument, false, isInvalidState ? "INVALID_STATE" : "POSITION_FLAT_CLEANUP", utcNow), out _);
                        if (isInvalidState)
                            _log.Write(RobotEvents.ExecutionBase(utcNow, entryIntentId, instrument, "ENTRY_ORDERS_CANCELLED_POSITION_FLAT_INVALID_STATE",
                                new { stream = inv.Stream, instrument = instrument, state = "flat", reason = inv.Reason, correlation_id = cid }));
                        else
                            _log.Write(RobotEvents.ExecutionBase(utcNow, entryIntentId, instrument, "ENTRY_STOP_CANCEL_ENQUEUED_ON_POSITION_FLAT",
                                new { cancelled_entry_intent_id = entryIntentId, instrument = instrument, position_market_position = "Flat", correlation_id = cid, note = "Cancel enqueued for strategy thread (position flat cleanup)" }));
                    }
                }
                else
                {
                    foreach (var entryIntentId in entryIntentIdsToCancel)
                    {
                        try
                        {
                            if (invalidStateReasons.TryGetValue(entryIntentId, out _))
                                SetPendingEntryTerminationReason(entryIntentId, "no_fill");
                            var cancelled = CancelIntentOrders(entryIntentId, utcNow);
                            if (cancelled)
                            {
                                if (invalidStateReasons.TryGetValue(entryIntentId, out var inv))
                                    _log.Write(RobotEvents.ExecutionBase(utcNow, entryIntentId, instrument, "ENTRY_ORDERS_CANCELLED_POSITION_FLAT_INVALID_STATE",
                                        new { stream = inv.Stream, instrument = instrument, state = "flat", reason = inv.Reason }));
                                else
                                    _log.Write(RobotEvents.ExecutionBase(utcNow, entryIntentId, instrument, "ENTRY_STOP_CANCELLED_ON_POSITION_FLAT",
                                        new { cancelled_entry_intent_id = entryIntentId, instrument = instrument, position_market_position = "Flat", note = "Cancelled entry stop order because position is flat (manual closure detected)" }));
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Write(RobotEvents.ExecutionBase(utcNow, entryIntentId, instrument, "ENTRY_STOP_CANCEL_FAILED_ON_POSITION_FLAT",
                                new { error = ex.Message, entry_intent_id = entryIntentId, instrument = instrument, note = "Failed to cancel entry stop order when position went flat - re-entry may occur" }));
                        }
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
    /// <summary>
    /// Cancel specific orders by broker order ID (stream-scoped recovery, e.g. CancelAndRebuild).
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
                if (!EnsureStrategyThreadOrEnqueue("CancelOrdersReal", null, null, $"CANCEL_ORDERS:{string.Join(",", orderIds)}",
                    () => account.Cancel(ordersToCancel.ToArray())))
                    return;
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

    private void CancelRobotOwnedWorkingOrdersReal(
        AccountSnapshot snap,
        DateTimeOffset utcNow,
        string? instrumentRootForScope,
        IReadOnlyList<string>? explicitBrokerOrderIds,
        bool allowAccountWideCancelFallback,
        string? correlationId)
    {
        if (_ntAccount == null)
            return;

        var account = _ntAccount as Account;
        if (account == null)
            return;

        var executionInstrumentKey = _iea?.ExecutionInstrumentKey ?? "";
        var instRoot = (instrumentRootForScope ?? "").Split(' ').FirstOrDefault() ?? instrumentRootForScope ?? "";
        var totalSeen = 0;
        var ordersToCancel = new List<Order>();
        string cancelScopeApplied;

        try
        {
            var idSet = explicitBrokerOrderIds != null && explicitBrokerOrderIds.Count > 0
                ? new HashSet<string>(explicitBrokerOrderIds, StringComparer.OrdinalIgnoreCase)
                : null;

            if (idSet == null && string.IsNullOrEmpty(instRoot) && !allowAccountWideCancelFallback)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_SCOPE_SKIPPED_NO_INSTRUMENT", state: "ENGINE",
                    new
                    {
                        execution_instrument_key = executionInstrumentKey,
                        correlation_id = correlationId,
                        note = "P2.6: no instrument scope, no explicit order set, fallback disabled — robot cancel skipped"
                    }));
                return;
            }

            if (idSet == null && string.IsNullOrEmpty(instRoot) && allowAccountWideCancelFallback)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_SCOPE_FALLBACK_ACCOUNT_WIDE", state: "ENGINE",
                    new
                    {
                        execution_instrument_key = executionInstrumentKey,
                        correlation_id = correlationId,
                        note = "P2.6: instrument scope unavailable — account-wide QTSW2 cancel fallback"
                    }));
            }

            foreach (var order in SnapshotAccountOrders(account))
            {
                if (order.OrderState != OrderState.Working && order.OrderState != OrderState.Accepted)
                    continue;
                totalSeen++;

                var tag = GetOrderTag(order) ?? "";
                var oco = order.Oco ?? "";
                var isRobot = (!string.IsNullOrEmpty(tag) && tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) ||
                              (!string.IsNullOrEmpty(oco) && oco.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase));
                if (!isRobot)
                    continue;

                if (idSet != null)
                {
                    var oid = order.OrderId.ToString() ?? "";
                    if (idSet.Contains(oid))
                        ordersToCancel.Add(order);
                    continue;
                }

                if (!string.IsNullOrEmpty(instRoot))
                {
                    var oInst = order.Instrument?.MasterInstrument?.Name ?? order.Instrument?.FullName ?? "";
                    if (ExecutionInstrumentResolver.IsSameInstrument(oInst, instRoot))
                        ordersToCancel.Add(order);
                    continue;
                }

                if (allowAccountWideCancelFallback)
                    ordersToCancel.Add(order);
            }

            cancelScopeApplied = idSet != null ? "explicit_set" : (!string.IsNullOrEmpty(instRoot) ? "instrument" : "fallback_account");

            if (ordersToCancel.Count > 0)
            {
                var ordersArr = ordersToCancel.ToArray();
                if (!EnsureStrategyThreadOrEnqueue("CancelRobotOwnedWorkingOrdersReal", null, null, "CANCEL_ROBOT_ORDERS", () => account.Cancel(ordersArr)))
                    return;
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_SCOPE_APPLIED", state: "ENGINE",
                    new
                    {
                        execution_instrument_key = executionInstrumentKey,
                        correlation_id = correlationId,
                        total_orders_seen = totalSeen,
                        total_orders_cancelled = ordersToCancel.Count,
                        cancel_scope = cancelScopeApplied
                    }));
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ROBOT_ORDERS_CANCELLED", state: "ENGINE",
                    new
                    {
                        cancelled_count = ordersToCancel.Count,
                        cancelled_order_ids = ordersToCancel.Select(o => o.OrderId).ToList(),
                        cancel_scope = cancelScopeApplied,
                        note = "Robot-owned working orders cancelled (P2.6 scoped)"
                    }));
            }
            else if (totalSeen > 0)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_SCOPE_APPLIED", state: "ENGINE",
                    new
                    {
                        execution_instrument_key = executionInstrumentKey,
                        correlation_id = correlationId,
                        total_orders_seen = totalSeen,
                        total_orders_cancelled = 0,
                        cancel_scope = cancelScopeApplied
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

}

#endif
