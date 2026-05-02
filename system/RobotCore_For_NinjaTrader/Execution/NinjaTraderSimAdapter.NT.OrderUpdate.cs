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
    /// Non-IEA ingress: trace + dedup + enqueue only (NinjaTrader order-update fan-out). Drained on strategy thread.
    /// </summary>
    private void HandleOrderIngressFromNt(object orderObj, object orderUpdateObj)
    {
        var order = orderObj as Order;
        dynamic orderUpdate = orderUpdateObj;
        if (order == null) return;

        var (encodedTag, _) = GetOrderTagWithSource(order);
        LogAggregatedTagAttributionIfNeeded(encodedTag, "HandleOrderIngressFromNt", DateTimeOffset.UtcNow);
        var parsed = RobotOrderIds.ParseTag(encodedTag);
        var intentId = parsed.IntentId ?? RobotOrderIds.DecodeIntentId(encodedTag);
        var utcNow = DateTimeOffset.UtcNow;
        var orderState = order.OrderState;
        var instrumentTrace = order.Instrument?.MasterInstrument?.Name ?? "";
        var orderIdTrace = order.OrderId ?? "";
        _executionTrace?.WriteExecutionTrace(utcNow, "OnOrderUpdate", "raw_callback", instrumentTrace, intentId ?? "",
            orderIdTrace, "", 0, orderState.ToString());

        if (TrySkipDuplicateOrderUpdate50ms(instrumentTrace, orderIdTrace, orderState.ToString(), utcNow, out var orderDedupSkips))
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instrumentTrace, "ORDER_UPDATE_DEDUP_SKIPPED", new
            {
                order_id = orderIdTrace,
                order_state = orderState.ToString(),
                skipped_count = orderDedupSkips,
                window_ms = 50
            }));
            return;
        }

        if (string.IsNullOrEmpty(intentId)) return;

        var eff = BuildOrderIngressEffectiveStateKey(order);
        EnqueueOrCoalesceOrderIngress(orderObj, orderUpdateObj, order, orderIdTrace, eff);
    }

    /// <summary>
    /// Non-IEA ingress: trace + permanent / non-IEA dedup + enqueue only. Drained on strategy thread.
    /// </summary>
    private void HandleExecutionIngressFromNt(object executionObj, object orderObj)
    {
        dynamic execution = executionObj;
        var order = orderObj as Order;
        if (execution == null || order == null) return;

        string? execIdTrace = null;
        try { dynamic dex = execution; execIdTrace = dex.ExecutionId as string; } catch { }
        var fillQtyTrace = 0;
        try { fillQtyTrace = (int)execution.Quantity; } catch { }
        var tagTrace = GetOrderTag(order);
        var utcTrace = DateTimeOffset.UtcNow;
        LogAggregatedTagAttributionIfNeeded(tagTrace, "HandleExecutionIngressFromNt", utcTrace);
        var intentTrace = RobotOrderIds.DecodeIntentId(tagTrace) ?? "";
        var instTrace = order.Instrument?.MasterInstrument?.Name ?? "";
        _executionTrace?.WriteExecutionTrace(utcTrace, "OnExecutionUpdate", "raw_callback", instTrace, intentTrace,
            order.OrderId ?? "", execIdTrace ?? "", fillQtyTrace, order.OrderState.ToString());

        var permKey = BuildPermanentExecutionDedupKey(instTrace, execIdTrace, order.OrderId, fillQtyTrace);
        if (!TryMarkFirstPermanentExecutionProcessing(permKey, out var permSkips))
        {
            _log.Write(RobotEvents.ExecutionBase(utcTrace, intentTrace, instTrace, "EXECUTION_DEDUP_SKIPPED_PERMANENT", new
            {
                execution_id = execIdTrace ?? "",
                broker_order_id = order.OrderId,
                fill_qty = fillQtyTrace,
                dedup_key = permKey,
                skipped_count = permSkips
            }));
            return;
        }

        var dedupKey = BuildNonIeaDedupKey(execution, order);
        if (TryMarkAndCheckDuplicateNonIea(dedupKey))
        {
            string? execId = null;
            try { dynamic d = execution; execId = d.ExecutionId as string; } catch { }
            _log.Write(RobotEvents.ExecutionBase(DateTimeOffset.UtcNow, "", order.Instrument?.MasterInstrument?.Name ?? "", "EXECUTION_DUPLICATE_DETECTED",
                new { broker_order_id = order.OrderId, execution_id = execId, note = "Duplicate execution callback skipped (dedup)" }));
            return;
        }

        EnqueueExecutionIngressNormal(executionObj, orderObj, instTrace);
    }

    /// <summary>Resolve OrderInfo for entry or protective leg (OrderMap keys differ for STOP/TARGET).</summary>
    private bool TryGetOrderInfoForIntentAndLeg(string intentId, string? tagLeg, out OrderInfo? orderInfo)
    {
        orderInfo = null;
        if (OrderMap.TryGetValue(intentId, out var oi) && oi != null)
        {
            orderInfo = oi;
            return true;
        }
        if (string.Equals(tagLeg, "STOP", StringComparison.OrdinalIgnoreCase) &&
            OrderMap.TryGetValue($"{intentId}:STOP", out oi) && oi != null)
        {
            orderInfo = oi;
            return true;
        }
        if (string.Equals(tagLeg, "TARGET", StringComparison.OrdinalIgnoreCase) &&
            OrderMap.TryGetValue($"{intentId}:TARGET", out oi) && oi != null)
        {
            orderInfo = oi;
            return true;
        }
        return false;
    }

    private static string? LegTagFromOrderTypeForIeResolution(string? orderTypeFromTag)
    {
        if (string.Equals(orderTypeFromTag, "STOP", StringComparison.OrdinalIgnoreCase)) return "STOP";
        if (string.Equals(orderTypeFromTag, "TARGET", StringComparison.OrdinalIgnoreCase)) return "TARGET";
        return null;
    }

    private string ResolveCanonicalBrokerOrderIdForIeLifecycle(string brokerIdFromEvent, string intentId, string? legTag)
    {
        if (!_useInstrumentExecutionAuthority || _iea == null) return brokerIdFromEvent;
        if (string.IsNullOrEmpty(brokerIdFromEvent)) return brokerIdFromEvent;
        if (_iea.TryResolveForExecutionUpdate(brokerIdFromEvent, intentId, legTag, out var re, out _) && re != null)
            return re.BrokerOrderId ?? brokerIdFromEvent;
        return brokerIdFromEvent;
    }

    /// <summary>
    /// When NT reports a post-ack broker/native id on <see cref="Order.OrderId"/> while IEA registered the pre-submit id,
    /// bind the new id to the existing registry row before lifecycle/execution paths resolve by broker id.
    /// </summary>
    private void TryLinkBrokerOrderIdFromOrderUpdate(Order order, string intentId, string? encodedTag, string? tagLeg, DateTimeOffset utcNow)
    {
        if (!_useInstrumentExecutionAuthority || _iea == null) return;
        if (string.IsNullOrEmpty(encodedTag) || !encodedTag.StartsWith(RobotOrderIds.Prefix, StringComparison.OrdinalIgnoreCase)) return;

        var incomingId = order.OrderId ?? "";
        if (string.IsNullOrEmpty(incomingId)) return;
        if (_iea.TryResolveByBrokerOrderId(incomingId, out _)) return;

        var legForResolve = tagLeg == "STOP" || tagLeg == "TARGET" ? tagLeg : null;
        OrderRegistryEntry? regEntry = null;
        if (!_iea.TryResolveForExecutionUpdate(incomingId, intentId, legForResolve, out regEntry, out _) || regEntry == null)
        {
            if (!TryGetOrderInfoForIntentAndLeg(intentId, tagLeg, out var oiLookup) || oiLookup == null) return;
            if (!_iea.TryResolveForExecutionUpdate(oiLookup.OrderId ?? "", intentId, legForResolve, out regEntry, out _) ||
                regEntry == null)
                return;
        }

        var canonical = regEntry.BrokerOrderId ?? "";
        if (string.IsNullOrEmpty(canonical) || string.Equals(incomingId, canonical, StringComparison.OrdinalIgnoreCase)) return;

        var instrument = order.Instrument?.MasterInstrument?.Name ?? "";
        if (TryGetOrderInfoForIntentAndLeg(intentId, tagLeg, out var oiInst) && oiInst != null && !string.IsNullOrEmpty(oiInst.Instrument))
            instrument = oiInst.Instrument ?? instrument;

        if (!_iea.LinkBrokerOrderIdAlias(incomingId, canonical, utcNow, intentId, instrument)) return;

        if (TryGetOrderInfoForIntentAndLeg(intentId, tagLeg, out var oiBump) && oiBump != null)
            oiBump.OrderId = incomingId;
    }

    /// <summary>
    /// QTSW2-only: hydrate <see cref="OrderMap"/> from IEA registry when Sim reports an update before local tracking caught up
    /// (registry row exists for the same intent+leg and is still live). Preserves manual/external behavior for untagged or unknown rows.
    /// </summary>
    private bool TryHydrateOrderMapFromIeRegistryBeforeOrderMapMiss(Order order, string intentId, string? encodedTag, ParsedTagResult parsed,
        DateTimeOffset utcNow)
    {
        if (!_useInstrumentExecutionAuthority || _iea == null) return false;
        if (string.IsNullOrEmpty(encodedTag) || !encodedTag.StartsWith(RobotOrderIds.Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(intentId)) return false;

        var legR = parsed.Leg == "STOP" || parsed.Leg == "TARGET" ? parsed.Leg : null;
        if (!_iea.TryResolveForExecutionUpdate(order.OrderId ?? "", intentId, legR, out var regEntry, out _) || regEntry == null)
            return false;

        if (!Qtsw2OrderUpdateHydrationPolicy.RegistryRowMatchesTaggedIntentAndLeg(regEntry, intentId, parsed.Leg)) return false;
        if (Qtsw2OrderUpdateHydrationPolicy.IsTerminalRegistryRow(regEntry)) return false;

        var incomingId = order.OrderId ?? "";
        var canonical = regEntry.BrokerOrderId ?? "";
        var instrument = order.Instrument?.MasterInstrument?.Name ?? regEntry.Instrument ?? "";
        if (!string.IsNullOrEmpty(canonical) && !string.Equals(incomingId, canonical, StringComparison.OrdinalIgnoreCase))
            _iea.LinkBrokerOrderIdAlias(incomingId, canonical, utcNow, intentId, instrument);

        var oi = regEntry.OrderInfo;
        if (oi == null) return false;

        oi.OrderId = incomingId;
        oi.NTOrder = order;

        OrderMap[intentId] = oi;
        if (regEntry.OrderRole == OrderRole.STOP)
            OrderMap[$"{intentId}:STOP"] = oi;
        else if (regEntry.OrderRole == OrderRole.TARGET)
            OrderMap[$"{intentId}:TARGET"] = oi;

        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_MAP_HYDRATED_FROM_IEA", new
        {
            broker_order_id = incomingId,
            canonical_broker_order_id = canonical,
            intent_id = intentId,
            order_role = regEntry.OrderRole.ToString(),
            parsed_leg = parsed.Leg,
            note = "OrderMap populated from IEA registry before manual/external miss path"
        }));
        return true;
    }

    /// <summary>
    /// STEP 3: Handle real NT OrderUpdate event.
    /// Called from public HandleOrderUpdate() method in main adapter.
    /// </summary>
    private void HandleOrderUpdateReal(object orderObj, object orderUpdateObj, bool beginAfterIngress = false)
    {
        var order = orderObj as Order;
        // OrderUpdate is the event args type, use dynamic to access it
        dynamic orderUpdate = orderUpdateObj;
        if (order == null) return;

        // Get tag/name with source (Tag, Name, FromEntrySignal, SignalName)
        var (encodedTag, tagSource) = GetOrderTagWithSource(order);
        var parsed = RobotOrderIds.ParseTag(encodedTag);
        var intentId = parsed.IntentId ?? RobotOrderIds.DecodeIntentId(encodedTag);
        var utcNow = DateTimeOffset.UtcNow;
        var orderState = order.OrderState;
        var instrumentTrace = order.Instrument?.MasterInstrument?.Name ?? "";
        var orderIdTrace = order.OrderId ?? "";
        if (!beginAfterIngress)
        {
            _executionTrace?.WriteExecutionTrace(utcNow, "OnOrderUpdate", "raw_callback", instrumentTrace, intentId ?? "",
                orderIdTrace, "", 0, orderState.ToString());

            if (TrySkipDuplicateOrderUpdate50ms(instrumentTrace, orderIdTrace, orderState.ToString(), utcNow, out var orderDedupSkips))
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instrumentTrace, "ORDER_UPDATE_DEDUP_SKIPPED", new
                {
                    order_id = orderIdTrace,
                    order_state = orderState.ToString(),
                    skipped_count = orderDedupSkips,
                    window_ms = 50
                }));
                return;
            }
        }

        if (string.IsNullOrEmpty(intentId)) return; // strict: non-robot orders ignored

        using var _orderUpdateForensic = OrderUpdateIntegrityForensicTrace.BeginHandleOrderUpdateScope(orderIdTrace, intentId,
            instrumentTrace, orderState.ToString());

        // Pre-ack / post-ack: Sim may report a new OrderId on the Order while registry + OrderMap still key the id from submit.
        TryLinkBrokerOrderIdFromOrderUpdate(order, intentId, encodedTag, parsed.Leg, utcNow);

        // Phase 1 Execution Ownership: resolve row by incoming id OR intent/leg alias, then lifecycle on canonical id.
        string? legR = parsed.Leg == "STOP" || parsed.Leg == "TARGET" ? parsed.Leg : null;
        if (_useInstrumentExecutionAuthority && _iea != null &&
            _iea.TryResolveForExecutionUpdate(order.OrderId ?? "", intentId, legR, out var regEntryLifecycle, out _) &&
            regEntryLifecycle != null)
        {
            var lifecycleState = orderState == OrderState.Filled ? OrderLifecycleState.FILLED
                : orderState == OrderState.Cancelled || orderState == OrderState.CancelPending ? OrderLifecycleState.CANCELED
                : orderState == OrderState.Rejected ? OrderLifecycleState.REJECTED
                : orderState == OrderState.Working || orderState == OrderState.Accepted ? OrderLifecycleState.WORKING
                : orderState == OrderState.PartFilled ? OrderLifecycleState.PART_FILLED
                : (OrderLifecycleState?)null;
            if (lifecycleState.HasValue && lifecycleState.Value != regEntryLifecycle.LifecycleState)
            {
                var canonicalLc = regEntryLifecycle.BrokerOrderId ?? order.OrderId ?? "";
                var incomingLc = order.OrderId ?? "";
                var terminal = lifecycleState.Value is OrderLifecycleState.FILLED or OrderLifecycleState.CANCELED
                    or OrderLifecycleState.REJECTED;
                var usedAlias = terminal && !string.Equals(incomingLc, canonicalLc, StringComparison.OrdinalIgnoreCase);
                _iea.UpdateOrderLifecycle(canonicalLc, lifecycleState.Value, utcNow);
                if (usedAlias && _log != null)
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instrumentTrace,
                        "ORDER_REGISTRY_TERMINAL_LIFECYCLE_RESOLVED_BY_ALIAS", new
                        {
                            incoming_broker_order_id = incomingLc,
                            canonical_broker_order_id = canonicalLc,
                            lifecycle_target = lifecycleState.Value.ToString(),
                            intent_id = intentId,
                            instrument = instrumentTrace,
                            iea_instance_id = _iea.InstanceId
                        }));
                }
            }
        }

        // ORPHAN DETECTION: Order update for protective order whose intent is already completed
        if ((parsed.Leg == "STOP" || parsed.Leg == "TARGET") &&
            IntentMap.TryGetValue(intentId, out var orderUpdateIntent) &&
            _executionJournal.IsIntentCompleted(intentId, orderUpdateIntent.TradingDate ?? "", orderUpdateIntent.Stream ?? ""))
        {
            _log.Write(RobotEvents.EngineBase(utcNow, orderUpdateIntent.TradingDate ?? "", "COMPLETED_INTENT_ORDER_UPDATE", "ENGINE", new
            {
                error = "Order update received for protective order whose intent is already TradeCompleted",
                intent_id = intentId,
                instrument = instrumentTrace,
                broker_order_id = order.OrderId,
                order_leg = parsed.Leg,
                order_state = orderState.ToString(),
                stream = orderUpdateIntent.Stream,
                action = "ORPHAN_DETECTED",
                note = "Protective order for completed intent - route to reconciliation"
            }));
        }

        // Pre-flight diagnostic: when tag indicates STOP, log for BE confirmation verification
        if (parsed.Leg == "STOP")
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, order.Instrument?.MasterInstrument?.Name ?? "", "BE_PREFLIGHT_STOP_ORDER_UPDATE",
                new
                {
                    stop_order_id = order.OrderId,
                    broker_order_id = order.OrderId,
                    raw_tag = encodedTag ?? "",
                    tag_source = tagSource,
                    parsed_intent_id = parsed.IntentId,
                    parsed_leg = parsed.Leg,
                    order_type_from_tag = "STOP",
                    order_state = orderState.ToString(),
                    stop_price = order.StopPrice,
                    note = "Pre-flight: STOP OrderUpdate received; verify BE confirmation pipeline"
                }));
        }

        // BE confirmation: when STOP order update arrives, check pending BE requests
        if (parsed.Leg == "STOP")
        {
            if (orderState == OrderState.Working || orderState == OrderState.Accepted)
                SetProtectionState(intentId, ProtectionState.Working);
            // BE cancel+replace: log when new stop becomes Working (quantify overlap/gap distribution)
            if (orderState == OrderState.Working && _pendingBECancelReplaceByIntent.TryGetValue(intentId, out var beReplace) && string.Equals(beReplace.NewStopOrderId, order.OrderId, StringComparison.OrdinalIgnoreCase))
            {
                _pendingBECancelReplaceByIntent.TryRemove(intentId, out _);
                var elapsedMs = (utcNow - beReplace.StartUtc).TotalMilliseconds;
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, order.Instrument?.MasterInstrument?.Name ?? "", "BE_CANCEL_REPLACE_STOP_WORKING", new
                {
                    stop_order_id = order.OrderId,
                    start_utc = beReplace.StartUtc,
                    elapsed_ms = Math.Round(elapsedMs, 1),
                    note = "New BE stop Working; overlap/gap distribution metric"
                }));
            }
            TryConfirmPendingBE(order, intentId, parsed, orderState, utcNow);
            // ORDER_UPDATED for STOP orders
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, order.Instrument?.MasterInstrument?.Name ?? "", "ORDER_UPDATED", new
            {
                order_type = "STOP",
                stop_order_id = order.OrderId,
                stop_price = order.StopPrice,
                order_state = orderState.ToString(),
                intent_id = intentId
            }));
            // Stop order Rejected: emit STOP_MODIFY_REJECTED if pending
            if (orderState == OrderState.Rejected)
            {
                var stopOrderId = order.OrderId ?? "";
                if (_pendingBERequests.TryRemove(stopOrderId, out var rejectedPending))
                {
                    string errorMsg = "Order rejected";
                    try { dynamic du = orderUpdate; errorMsg = (string?)du.Comment ?? errorMsg; } catch { }
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, order.Instrument?.MasterInstrument?.Name ?? "", "STOP_MODIFY_REJECTED", new
                    {
                        stop_order_id = stopOrderId,
                        intent_id = intentId,
                        oco_id = rejectedPending.OcoId,
                        error = errorMsg,
                        order_state = "Rejected",
                        note = "OrderUpdate Rejected for pending BE"
                    }));
                }
                // Also record CancelPending for replace-semantics (when old stop goes CancelPending)
            }
            else if (orderState == OrderState.CancelPending || orderState == OrderState.Cancelled)
            {
                _pendingBECancelUtcByIntent[intentId] = utcNow;
            }
        }

        if (!OrderMap.TryGetValue(intentId, out var orderInfo))
        {
            TryHydrateOrderMapFromIeRegistryBeforeOrderMapMiss(order, intentId, encodedTag, parsed, utcNow);
            OrderMap.TryGetValue(intentId, out orderInfo);
        }

        if (orderInfo == null)
        {
            if (IsRobotOwnedFlattenByTag(encodedTag))
                return;

            var instrument = order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
            if (IsRobotOwnedOrderUpdateRace(encodedTag, intentId))
            {
                var orderStateText = orderState.ToString();
                var earlyInitializedUpdate = string.Equals(orderStateText, "Initialized", StringComparison.OrdinalIgnoreCase);
                var eventType = earlyInitializedUpdate
                    ? "ROBOT_ORDER_UPDATE_REGISTRY_RACE_INITIALIZED"
                    : "ROBOT_ORDER_UPDATE_REGISTRY_RACE";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, eventType,
                    new
                    {
                        broker_order_id = order.OrderId,
                        intent_id = intentId,
                        tag = encodedTag,
                        tag_source = tagSource,
                        parsed_leg = parsed.Leg,
                        order_state = orderStateText,
                        early_initialized_update = earlyInitializedUpdate,
                        classification = earlyInitializedUpdate ? "AUDIT_EXPECTED_EARLY_ORDER_UPDATE" : "ORDER_UPDATE_REGISTRY_RACE",
                        note = "Robot-tagged order update arrived before OrderMap/IEA registry hydration completed; suppressing manual/external recovery."
                    }));
                return;
            }

            // Phase 2: Order update for order not in registry or OrderMap - manual/external order policy
            if (_useInstrumentExecutionAuthority && _iea != null && !_iea.TryResolveByBrokerOrderId(order.OrderId, out _))
            {
                _iea.RegisterUnownedOrder(order.OrderId, intentId, instrument, "MANUAL_OR_EXTERNAL_ORDER_DETECTED", utcNow,
                    classAsRecoverableRobotOwned: true);
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "MANUAL_OR_EXTERNAL_ORDER_DETECTED", new
                {
                    broker_order_id = order.OrderId,
                    intent_id = intentId,
                    instrument,
                    order_state = orderState.ToString(),
                    note = "Order update for order not in registry - fail-closed flatten",
                    policy = "FAIL_CLOSED_FLATTEN"
                }));
                if (orderState == OrderState.Working || orderState == OrderState.Accepted)
                {
                    _iea.RequestRecovery(instrument, "MANUAL_OR_EXTERNAL_ORDER_DETECTED", new { broker_order_id = order.OrderId, intent_id = intentId }, utcNow);
                    _iea.RequestSupervisoryAction(instrument, SupervisoryTriggerReason.REPEATED_UNOWNED_EXECUTIONS, SupervisorySeverity.HIGH, new { broker_order_id = order.OrderId, intent_id = intentId }, utcNow);
                }
            }
            else
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_UPDATE_UNKNOWN_ORDER",
                    new
                    {
                        error = "Order not found in tracking map",
                        broker_order_id = order.OrderId,
                        tag = encodedTag,
                        order_state = orderState.ToString(),
                        note = "Order update for untracked order - may indicate rejected before tracking or race condition",
                        severity = "INFO"
                    }));
            }
            return;
        }

        var instKey = orderInfo.Instrument.Trim();
        _executionTrace?.WriteExecutionTrace(utcNow, "NotifyExecutionTrigger", "before_notify", instKey, intentId,
            order.OrderId ?? "", "", 0, orderState.ToString());
        OrderUpdateIntegrityForensicTrace.Step("MISMATCH_TRIGGER_BEFORE");
        _onMismatchExecutionTrigger?.Invoke(instKey, utcNow, new MismatchExecutionTriggerDetails
        {
            IntentId = intentId,
            FillDelta = 0,
            SuppressHardJournalIntegrityActions = orderState == OrderState.Filled
        });
        OrderUpdateIntegrityForensicTrace.Step("MISMATCH_TRIGGER_AFTER");
        _executionTrace?.WriteExecutionTrace(utcNow, "NotifyExecutionTrigger", "after_notify", instKey, intentId,
            order.OrderId ?? "", "", 0, orderState.ToString());

        // Reentry protective acceptance: when BOTH stop and target for a reentry intent are Working/Accepted,
        // invoke HandleReentryProtectionAccepted (once per intent). Only for reentry protective set, not original entry.
        if ((parsed.Leg == "STOP" || parsed.Leg == "TARGET") &&
            IntentMap.TryGetValue(intentId, out var reentryProtIntent) &&
            string.Equals(reentryProtIntent.TriggerReason, "SUBMIT_MARKET_REENTRY", StringComparison.OrdinalIgnoreCase) &&
            (orderState == OrderState.Working || orderState == OrderState.Accepted) &&
            !_reentryProtectionAcceptedNotified.Contains(intentId))
        {
            if (((IIEAOrderExecutor)this).HasWorkingProtectivesForIntent(intentId))
            {
                _reentryProtectionAcceptedNotified.Add(intentId);
                ReleaseMarketReentryExecutionLatch(intentId, orderInfo.Instrument, utcNow, "REENTRY_PROTECTION_ACCEPTED");
                _onReentryProtectionAcceptedCallback?.Invoke(intentId, utcNow);
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "REENTRY_PROTECTION_ACCEPTED_CALLBACK",
                    new { reentry_intent_id = intentId, note = "Both reentry protective orders Working - HandleReentryProtectionAccepted invoked" }));
            }
        }

        // Update journal based on order state. Truth source: use parsed.Leg from tag, NOT orderInfo.OrderType.
        if (orderState == OrderState.Accepted)
        {
            var orderTypeFromTag = parsed.Leg ?? orderInfo.OrderType;
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_ACKNOWLEDGED",
                new { broker_order_id = order.OrderId, order_type = orderTypeFromTag }));
            
            // Intent lifecycle: entry order accepted -> ENTRY_WORKING
            if (parsed.Leg != "STOP" && parsed.Leg != "TARGET")
                _iea?.TryTransitionIntentLifecycle(intentId, IntentLifecycleTransition.ENTRY_ACCEPTED, null, utcNow);
            
            // Mark protective orders as acknowledged for watchdog tracking
            if (parsed.Leg == "STOP")
            {
                orderInfo.ProtectiveStopAcknowledged = true;
            }
            else if (parsed.Leg == "TARGET")
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
            
            // OCO sibling cancellation: NinjaTrader reports cancelled OCO sibling as Rejected with "CancelPending"
            // Narrow predicate: require OCO present + CancelPending (entry bracket or protective bracket)
            var ocoGroupId = order.Oco as string;
            var hasCancelPending = (fullErrorMsg?.IndexOf("CancelPending", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
            var isOcoSiblingCancel = hasCancelPending && !string.IsNullOrEmpty(ocoGroupId);
            if (isOcoSiblingCancel)
            {
                orderInfo.State = "CANCELLED";
                // Forensic traceability: log all fields for audit depth
                var siblingOrderId = order.OrderId; // This order (cancelled sibling)
                string? filledOrderId = null;
                string? executionInstrumentKey = null;
                try
                {
                    var account = _ntAccount as Account;
                    if (account != null)
                    {
                        var accountName = account.Name ?? "";
                        executionInstrumentKey = ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(accountName, order.Instrument, _ieaEngineExecutionInstrument);
                        if (!string.IsNullOrEmpty(ocoGroupId))
                        {
                            foreach (Order o in SnapshotAccountOrders(account))
                            {
                                if (o.OrderId != siblingOrderId && string.Equals(o.Oco as string, ocoGroupId, StringComparison.OrdinalIgnoreCase) && o.OrderState == OrderState.Filled)
                                {
                                    filledOrderId = o.OrderId;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { /* best-effort forensic fields */ }
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "OCO_SIBLING_CANCELLED",
                    new
                    {
                        oco_group_id = ocoGroupId,
                        sibling_order_id = siblingOrderId,
                        filled_order_id = filledOrderId,
                        execution_instrument_key = executionInstrumentKey,
                        intent_id = intentId,
                        order_type = orderInfo.OrderType,
                        note = "OCO sibling cancelled (other leg filled) - expected behavior, not a rejection"
                    }));
                if (orderInfo.IsEntryOrder && orderInfo.FilledQuantity == 0)
                {
                    var (tdOco, streamOco, _, _, _, _, _) = GetIntentInfo(intentId);
                    TryAppendKeyEventEntryTerminated(utcNow, orderInfo.Instrument ?? "", string.IsNullOrEmpty(streamOco) ? null : streamOco,
                        intentId, tdOco ?? "", "cancelled");
                }
                return; // Skip rejection handling
            }
            
            var (tradingDate11, stream11, _, _, _, _, _) = GetIntentInfo(intentId);
            _executionJournal.RecordRejection(intentId, tradingDate11, stream11, $"ORDER_REJECTED: {fullErrorMsg}", utcNow, 
                orderType: orderInfo.OrderType, rejectedPrice: orderInfo.Price, rejectedQuantity: orderInfo.Quantity);
            orderInfo.State = "REJECTED";
            var isProtectiveOrderReject = orderInfo.OrderType == "STOP" || orderInfo.OrderType == "TARGET";
            if (isProtectiveOrderReject)
                _keyEventWriter?.AppendKeyEvent(utcNow, "PROTECTIVE_FAILED", orderInfo.Instrument?.Trim(),
                    string.IsNullOrEmpty(stream11) ? null : stream11, fullErrorMsg,
                    new Dictionary<string, object?> { ["order_type"] = orderInfo.OrderType });
            else
                _keyEventWriter?.AppendKeyEvent(utcNow, "ENTRY_REJECTED", orderInfo.Instrument?.Trim(),
                    string.IsNullOrEmpty(stream11) ? null : stream11, fullErrorMsg,
                    new Dictionary<string, object?> { ["order_type"] = orderInfo.OrderType });
            
            // CRITICAL FIX: If protective order is rejected, trigger fail-closed pathway
            // Order submission success != order acceptance - broker can reject orders after submission
            bool isProtectiveOrder = isProtectiveOrderReject;
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
            if (orderInfo.IsEntryOrder && orderInfo.FilledQuantity == 0)
            {
                var (tdCx, streamCx, _, _, _, _, _) = GetIntentInfo(intentId);
                if (!string.IsNullOrWhiteSpace(tdCx) && !string.IsNullOrWhiteSpace(streamCx))
                    _executionJournal.RecordCancelledUnfilledEntry(intentId, tdCx, streamCx, utcNow);
                TryAppendKeyEventEntryTerminated(utcNow, orderInfo.Instrument ?? "", string.IsNullOrEmpty(streamCx) ? null : streamCx,
                    intentId, tdCx ?? "", "cancelled");
            }
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
    /// When order is provided and resolution fails, emits EXECUTION_FILLED(mapped=false) before fail-closed.
    /// </summary>
    private bool ResolveIntentContextOrFailClosed(
        string intentId, 
        string encodedTag, 
        string orderType, 
        string instrument, 
        decimal fillPrice, 
        int fillQuantity, 
        DateTimeOffset utcNow, 
        out IntentContext context,
        Order? order = null)
    {
        context = default;
        
        if (!IntentMap.TryGetValue(intentId, out var intent))
        {
            EmitUnmappedFill(instrument, "INTENT_NOT_FOUND", fillPrice, fillQuantity, utcNow, order?.OrderId, order, tag: encodedTag);
            LogOrphanFill(intentId, encodedTag, orderType, instrument, fillPrice, fillQuantity, 
                utcNow, reason: "INTENT_NOT_FOUND");
            var orphanSlotId = RecordOrphanFillIfEnabled(
                instrument,
                order?.OrderId?.ToString() ?? "",
                intentId,
                fillPrice,
                fillQuantity,
                utcNow,
                OrphanReason.UnknownOrder,
                SlotDirection.Long);
            
            // Stand down stream if known, otherwise block instrument
            // Try to get stream from orderInfo if available, but likely unknown
            _standDownStreamCallback?.Invoke("", utcNow, $"ORPHAN_FILL:INTENT_NOT_FOUND:{intentId}");
            
            // CRITICAL FIX: Flatten position immediately (fail-closed) - Phase 3: route through RequestRecovery
            if (_useInstrumentExecutionAuthority)
            {
                RequestRecoveryForInstrument(instrument, "ORPHAN_FILL_INTENT_NOT_FOUND", new { intent_id = intentId, tag = encodedTag, order_type = orderType, fill_price = fillPrice, fill_quantity = fillQuantity }, utcNow);
                LogCriticalEngineWithIeaContext(utcNow, "", "ORPHAN_FILL_RECOVERY_REQUESTED", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        tag = encodedTag,
                        order_type = orderType,
                        instrument = instrument,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        reason = "INTENT_NOT_FOUND",
                        note = "Phase 3: RequestRecovery for orphan fill"
                    });
            }
            else
            {
            bool flattenOk = false;
            string? flattenErr = null;
            try
            {
                var flattenResult = Flatten(intentId, instrument, utcNow);
                flattenOk = flattenResult.Success;
                flattenErr = flattenResult.ErrorMessage;
                
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
                flattenErr = ex.Message;
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
            NotifyOrphanFlattenResult(instrument, orphanSlotId, flattenOk, flattenErr, utcNow);
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
            RecordOrphanFillIfEnabled(instrument, order?.OrderId?.ToString() ?? "", intentId,
                fillPrice, fillQuantity, utcNow, OrphanReason.IntentLostAfterContext,
                ParseSlotDirection(direction), OrphanActionTaken.NoAction);
            _standDownStreamCallback?.Invoke(stream, utcNow, $"ORPHAN_FILL:MISSING_TRADING_DATE:{intentId}");
            return false;
        }
        
        if (string.IsNullOrWhiteSpace(stream))
        {
            LogOrphanFill(intentId, encodedTag, orderType, instrument, fillPrice, fillQuantity, 
                utcNow, reason: "MISSING_STREAM");
            RecordOrphanFillIfEnabled(instrument, order?.OrderId?.ToString() ?? "", intentId,
                fillPrice, fillQuantity, utcNow, OrphanReason.IntentLostAfterContext,
                ParseSlotDirection(direction), OrphanActionTaken.NoAction);
            _standDownStreamCallback?.Invoke("", utcNow, $"ORPHAN_FILL:MISSING_STREAM:{intentId}");
            return false;
        }
        
        if (string.IsNullOrWhiteSpace(direction))
        {
            LogOrphanFill(intentId, encodedTag, orderType, instrument, fillPrice, fillQuantity, 
                utcNow, stream, "MISSING_DIRECTION");
            RecordOrphanFillIfEnabled(instrument, order?.OrderId?.ToString() ?? "", intentId,
                fillPrice, fillQuantity, utcNow, OrphanReason.IntentLostAfterContext,
                SlotDirection.Long, OrphanActionTaken.NoAction);
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
            RecordOrphanFillIfEnabled(instrument, order?.OrderId?.ToString() ?? "", intentId,
                fillPrice, fillQuantity, utcNow, OrphanReason.ContextResolutionFailed,
                ParseSlotDirection(direction), OrphanActionTaken.NoAction);
            _standDownStreamCallback?.Invoke(stream, utcNow, $"ORPHAN_FILL:MISSING_MULTIPLIER:{intentId}");
            return false;
        }
        
        context = new IntentContext
        {
            TradingDate = tradingDate,
            Stream = stream,
            Direction = direction,
            ContractMultiplier = contractMultiplier.Value,
            ExecutionInstrument = intent.ExecutionInstrument ?? intent.Instrument ?? "",
            CanonicalInstrument = intent.Instrument ?? "",
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
            var orphanDir = Path.Combine(_stateRoot, "data", "execution_incidents", "orphan_fills");
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

    /// <summary>Build dedup key for non-IEA path. Same format as IEA: executionId or orderId|ticks|qty|mpos.</summary>
    private static string BuildNonIeaDedupKey(dynamic execution, Order order)
    {
        try
        {
            var execId = execution.ExecutionId as string;
            var orderId = (order.OrderId ?? "").ToString();
            var time = execution.Time;
            long ticks = 0;
            if (time != null)
            {
                if (time is DateTime dt) ticks = dt.Ticks;
                else if (time is DateTimeOffset dto) ticks = dto.UtcTicks;
                else try { ticks = ((dynamic)time).Ticks; } catch { }
            }
            var qty = (int)(execution.Quantity ?? 0);
            var mpos = (execution.MarketPosition?.ToString() ?? "");
            return !string.IsNullOrEmpty(execId) ? execId : $"{orderId}|{ticks}|{qty}|{mpos}|{orderId}";
        }
        catch { return ""; }
    }

    /// <summary>Defer unresolved execution for non-blocking retry. IEA: enqueue to worker (recovery-essential). Non-IEA: add to pending list.</summary>
    private void DeferUnresolvedExecution(UnresolvedExecutionRecord record)
    {
        if (_useInstrumentExecutionAuthority && _iea != null)
            _iea.EnqueueRecoveryEssential(() => ProcessUnresolvedRetry(record), "ExecutionUnresolvedRetry");
        else
        {
            lock (_pendingUnresolvedLock)
                _pendingUnresolvedExecutions.Add(record);
        }
    }

    /// <summary>Process unresolved retry: try OrderMap and registry; if found continue; if retries left re-enqueue; else flatten.</summary>
    private void ProcessUnresolvedRetry(UnresolvedExecutionRecord record)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var elapsed = record.ElapsedMs;
        var snapshot = record.Snapshot;
        var accountName = !string.IsNullOrWhiteSpace(snapshot?.AccountName)
            ? snapshot!.AccountName
            : (_iea?.AccountName ?? ExecutionUpdateRouter.GetAccountNameFromOrder(record.Order));
        var execInstKey = !string.IsNullOrWhiteSpace(snapshot?.ExecutionInstrumentKey)
            ? snapshot!.ExecutionInstrumentKey
            : (_iea?.ExecutionInstrumentKey ?? ExecutionUpdateRouter.GetExecutionInstrumentKeyFromOrder((record.Order as Order)?.Instrument));
        OrderInfo? orderInfo = null;

        if (OrderMap.TryGetValue(record.IntentId, out orderInfo))
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, record.IntentId, record.Instrument, "EXECUTION_DEFERRED_RESOLVED",
                new { account_name = accountName, execution_instrument_key = execInstKey, intent_id = record.IntentId, execution_id = record.ExecutionId, broker_order_id = record.BrokerOrderId, elapsed_ms = elapsed, retry_count = record.RetryCount }));
            if (_useInstrumentExecutionAuthority && _iea != null)
                ProcessExecutionUpdateContinuation(record, orderInfo);
            else
                EnqueueExecutionIngressRetry(record, orderInfo);
            return;
        }
        if (_useInstrumentExecutionAuthority && _iea != null && !string.IsNullOrEmpty(record.BrokerOrderId))
        {
            var parsedTag = RobotOrderIds.ParseTag(record.EncodedTag);
            if (_iea.TryResolveForExecutionUpdate(record.BrokerOrderId, record.IntentId, parsedTag.Leg, out var regEntry, out var resolutionPath))
            {
                orderInfo = regEntry!.OrderInfo;
                _log.Write(RobotEvents.ExecutionBase(utcNow, record.IntentId, record.Instrument, "EXECUTION_DEFERRED_RESOLVED",
                    new { account_name = accountName, execution_instrument_key = execInstKey, intent_id = record.IntentId, execution_id = record.ExecutionId, broker_order_id = record.BrokerOrderId, elapsed_ms = elapsed, retry_count = record.RetryCount, resolution_path = resolutionPath }));
                if (_useInstrumentExecutionAuthority && _iea != null)
                    ProcessExecutionUpdateContinuation(record, orderInfo);
                else
                    EnqueueExecutionIngressRetry(record, orderInfo);
                return;
            }
        }
        if (record.RetryCount < UNRESOLVED_MAX_RETRIES && elapsed < UNRESOLVED_GRACE_MS)
        {
            record.RetryCount++;
            _iea?.IncrementExecutionResolutionRetries();
            _log.Write(RobotEvents.ExecutionBase(utcNow, record.IntentId, record.Instrument, "EXECUTION_DEFERRED_RETRY",
                new { account_name = accountName, execution_instrument_key = execInstKey, intent_id = record.IntentId, execution_id = record.ExecutionId, broker_order_id = record.BrokerOrderId, retry_count = record.RetryCount, elapsed_ms = elapsed }));
            DeferUnresolvedExecution(record);
            return;
        }
        _log.Write(RobotEvents.ExecutionBase(utcNow, record.IntentId, record.Instrument, "EXECUTION_RESOLUTION_TIMEOUT",
            new { account_name = accountName, execution_instrument_key = execInstKey, intent_id = record.IntentId, execution_id = record.ExecutionId, broker_order_id = record.BrokerOrderId, elapsed_ms = elapsed, retry_count = record.RetryCount }));
        TriggerUnknownOrderFlatten(record);
    }

    private void TriggerUnknownOrderFlatten(UnresolvedExecutionRecord record)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var order = record.Order as Order;
        var snapshot = record.Snapshot;
        var intentId = record.IntentId;
        var instrument = record.Instrument;
        if (snapshot != null && snapshot.IsRobotFlattenOrder && record.FillQuantity != 0)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, "FLATTEN", instrument, "FLATTEN_EXECUTION_RECOVERED", new
            {
                broker_order_id = snapshot.BrokerOrderId,
                execution_id = snapshot.ExecutionId,
                reason = "snapshot_registry_miss_but_tag_matched"
            }));
            EnqueueBrokerFlattenFillPostFill(snapshot, instrument, record.FillPrice, record.FillQuantity, utcNow, snapshot.BrokerOrderId,
                TryResolveFlattenRegistryEntry(snapshot.BrokerOrderId), runFlatCheck: true);
            return;
        }
        if (IsRobotOwnedFlattenByTag(record.EncodedTag) && order != null && record.FillQuantity != 0)
        {
            string? execIdRec = null;
            try { dynamic d = record.Execution; execIdRec = d.ExecutionId as string; } catch { }
            _log.Write(RobotEvents.ExecutionBase(utcNow, "FLATTEN", instrument, "FLATTEN_EXECUTION_RECOVERED", new
            {
                broker_order_id = order.OrderId,
                execution_id = execIdRec,
                reason = "registry_miss_but_tag_matched"
            }));
            EnqueueBrokerFlattenFillPostFill(record.Execution, instrument, record.FillPrice, record.FillQuantity, utcNow, order.OrderId, order,
                TryResolveFlattenRegistryEntry(order.OrderId), runFlatCheck: true);
            return;
        }

        var brokerOrderId = snapshot?.BrokerOrderId ?? order?.OrderId;
        EmitUnmappedFill(instrument, "UNKNOWN_ORDER_AFTER_GRACE", record.FillPrice, record.FillQuantity, utcNow, brokerOrderId, order, tag: record.EncodedTag);
        LogCriticalWithIeaContext(utcNow, intentId, instrument, "EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL",
            new { error = "Order not found in map after 500ms grace", broker_order_id = brokerOrderId, intent_id = intentId, fill_price = record.FillPrice, fill_quantity = record.FillQuantity, account_name = snapshot?.AccountName ?? _iea?.AccountName });
        if (_useInstrumentExecutionAuthority && _ntActionQueue != null)
        {
            var cid = $"UNKNOWN_ORDER:{intentId}:{utcNow:yyyyMMddHHmmssfff}";
            EnqueueNtActionInternal(new NtFlattenInstrumentCommand(cid, intentId, instrument, "UNKNOWN_ORDER_FILL", utcNow,
                DestructiveActionSource.FAIL_CLOSED, DestructiveTriggerReason.FAIL_CLOSED));
            var ns = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
            ns?.EnqueueNotification($"UNKNOWN_ORDER_FILL_ENQUEUED:{intentId}", $"Unknown Order Fill - Flatten Enqueued - {instrument}", $"Fill for order not in map. Intent: {intentId}", priority: 1);
        }
        else
        {
            try
            {
                var r = Flatten(intentId, instrument, utcNow);
                var ns = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                ns?.EnqueueNotification($"UNKNOWN_ORDER_FILL:{intentId}", r.Success ? "Position Flattened" : "Flatten FAILED", r.Success ? "Flattened" : r.ErrorMessage ?? "", priority: r.Success ? 1 : 3);
            }
            catch (Exception ex)
            {
                var ns = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                ns?.EnqueueNotification($"UNKNOWN_ORDER_FILL_FAILED:{intentId}", $"CRITICAL: Flatten FAILED - {instrument}", ex.Message, priority: 3);
            }
        }
    }

}

#endif
