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
using QTSW2.Robot.Core.Diagnostics;

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
        var (tag, _) = GetOrderTagWithSource(order);
        return tag;
    }

    /// <summary>
    /// Get best available tag string and which NT field it came from.
    /// NT may use Tag, Name, FromEntrySignal, or SignalName. Log tag_source for diagnostics.
    /// </summary>
    private static (string? tag, string tagSource) GetOrderTagWithSource(Order order)
    {
        dynamic dynOrder = order;
        try
        {
            var tag = dynOrder.Tag as string;
            if (!string.IsNullOrEmpty(tag)) return (tag, "Tag");
        }
        catch { }
        try
        {
            var name = dynOrder.Name as string;
            if (!string.IsNullOrEmpty(name)) return (name, "Name");
        }
        catch { }
        try
        {
            var fromEntry = dynOrder.FromEntrySignal as string;
            if (!string.IsNullOrEmpty(fromEntry)) return (fromEntry, "FromEntrySignal");
        }
        catch { }
        try
        {
            var signalName = dynOrder.SignalName as string;
            if (!string.IsNullOrEmpty(signalName)) return (signalName, "SignalName");
        }
        catch { }
        return (null, "None");
    }

    private static bool IsRobotOwnedFlattenByTag(string? encodedTag) =>
        !string.IsNullOrEmpty(encodedTag) &&
        encodedTag.StartsWith($"{RobotOrderIds.Prefix}FLATTEN:", StringComparison.OrdinalIgnoreCase);

    private static bool IsRobotOwnedFlattenOrder(string? encodedTag, OrderInfo? orderFromMap) =>
        IsRobotOwnedFlattenByTag(encodedTag) ||
        (orderFromMap != null && string.Equals(orderFromMap.OrderType, "FLATTEN", StringComparison.OrdinalIgnoreCase));

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
        if (ntOrders.Length == 0) return;
        var orderIds = string.Join(",", ntOrders.Select(o => o.OrderId));
        if (!EnsureStrategyThreadOrEnqueue("CancelOrders", null, null, $"CANCEL:{orderIds}", () => account!.Cancel(ntOrders)))
            return;
    }

    void IIEAOrderExecutor.SubmitOrders(IReadOnlyList<object> orders)
    {
        if (System.Threading.Volatile.Read(ref _sessionMismatchBlocked) != 0)
        {
            RecordSessionIdentityBlockAttempt();
            return;
        }
        var account = _ntAccount as Account;
        if (account == null || orders == null) return;
        var ntOrders = orders.OfType<Order>().ToArray();
        if (ntOrders.Length == 0) return;
        if (!EnsureStrategyThreadOrEnqueue("SubmitOrders", null, null, null, () => account!.Submit(ntOrders)))
            return;
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

    IReadOnlyCollection<string> IIEAOrderExecutor.GetAdoptionCandidateIntentIds(string? executionInstrument) =>
        GetAdoptionCandidateIntentIds(executionInstrument);

    (string JournalDir, int FileCount, bool DirectoryExists) IIEAOrderExecutor.GetJournalDiagnostics(string? executionInstrument) =>
        GetJournalDiagnostics(executionInstrument);

    OrderModificationResult IIEAOrderExecutor.ModifyStopToBreakEven(string intentId, string instrument, decimal beStopPrice, DateTimeOffset utcNow) =>
        ModifyStopToBreakEven(intentId, instrument, beStopPrice, utcNow);

    decimal IIEAOrderExecutor.GetTickSize()
    {
        var inst = _ntInstrument as Instrument;
        return inst?.MasterInstrument != null ? (decimal)inst.MasterInstrument.TickSize : 0.25m;
    }

    FlattenResult IIEAOrderExecutor.Flatten(string intentId, string instrument, DateTimeOffset utcNow) =>
        Flatten(intentId, instrument, utcNow);

    /// <inheritdoc />
    BrokerCanonicalExposure IIEAOrderExecutor.GetBrokerCanonicalExposure(string instrument) =>
        GetBrokerCanonicalExposureInternal(instrument);

    /// <summary>
    /// Live account enumeration: all position rows whose master instrument matches the canonical key.
    /// Same bucketing as <see cref="BrokerPositionResolver.BuildReconciliationAbsTotalsByCanonicalKey"/>.
    /// </summary>
    private BrokerCanonicalExposure GetBrokerCanonicalExposureInternal(string? instrument)
    {
        var canonical = BrokerPositionResolver.NormalizeCanonicalKey(instrument);
        var account = _ntAccount as Account;
        if (account == null || string.IsNullOrEmpty(canonical))
            return BrokerCanonicalExposure.Empty(canonical);

        var legs = new List<BrokerPositionLeg>();
        try
        {
            foreach (var position in account.Positions)
            {
                if (position.Quantity == 0) continue;
                var master = position.Instrument?.MasterInstrument?.Name?.Trim() ?? "";
                if (string.IsNullOrEmpty(master) ||
                    !string.Equals(master, canonical, StringComparison.OrdinalIgnoreCase))
                    continue;
                var label = position.Instrument?.FullName ?? master;
                legs.Add(new BrokerPositionLeg
                {
                    SignedQuantity = position.Quantity,
                    ContractLabel = label,
                    NativeInstrument = position.Instrument
                });
            }
        }
        catch
        {
            return BrokerCanonicalExposure.Empty(canonical);
        }

        var totalAbs = legs.Sum(l => Math.Abs(l.SignedQuantity));
        return new BrokerCanonicalExposure
        {
            CanonicalKey = canonical,
            ReconciliationAbsQuantityTotal = totalAbs,
            Legs = legs
        };
    }

    /// <summary>
    /// IEA Flatten Authority: Get account position. THREAD: Must be called on strategy thread.
    /// Legacy single (qty,direction): derived from <see cref="GetBrokerCanonicalExposureInternal"/> — net signed when multiple legs share sign;
    /// offsetting legs same bucket return flat here; use <see cref="IIEAOrderExecutor.GetBrokerCanonicalExposure"/> for full rows.
    /// </summary>
    (int quantity, string direction) IIEAOrderExecutor.GetAccountPositionForInstrument(string instrument)
    {
        var e = GetBrokerCanonicalExposureInternal(instrument);
        if (e.ReconciliationAbsQuantityTotal == 0 || e.Legs.Count == 0)
            return (0, "");
        if (e.Legs.Count == 1)
        {
            var q = e.Legs[0].SignedQuantity;
            if (q == 0) return (0, "");
            return (q, q > 0 ? "Long" : "Short");
        }

        var net = e.Legs.Sum(l => l.SignedQuantity);
        if (net != 0)
            return (net, net > 0 ? "Long" : "Short");

        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "BROKER_EXPOSURE_NET_ZERO_MULTI_LEG", state: "ENGINE",
            new
            {
                instrument,
                canonical_key = e.CanonicalKey,
                reconciliation_abs_total = e.ReconciliationAbsQuantityTotal,
                leg_count = e.Legs.Count,
                note = "Offsetting signed legs in same master bucket — use GetBrokerCanonicalExposure / per-leg flatten"
            }));
        return (0, "");
    }

    /// <summary>
    /// IEA Flatten Authority: Submit position-derived market close order.
    /// Side is "SELL" (close long) or "BUY" (close short). THREAD: Must run on strategy thread.
    /// </summary>
    OrderSubmissionResult IIEAOrderExecutor.SubmitFlattenOrder(string instrument, string side, int quantity, FlattenDecisionSnapshot snapshot, DateTimeOffset utcNow, object? nativeInstrumentForBrokerOrder = null)
    {
        if (!TrySessionIdentityGateDestructiveFlatten(instrument, utcNow, out var sessionFailFlatten))
            return sessionFailFlatten!;
        var account = _ntAccount as Account;
        var ntInstrument = (nativeInstrumentForBrokerOrder ?? (object?)_ntInstrument) as Instrument;
        if (account == null || ntInstrument == null)
            return OrderSubmissionResult.FailureResult("NT context not set", utcNow);

        var orderAction = side.Equals("SELL", StringComparison.OrdinalIgnoreCase) ? OrderAction.Sell : OrderAction.BuyToCover;
        try
        {
            var order = account.CreateOrder(
                ntInstrument,
                orderAction,
                OrderType.Market,
                OrderEntry.Manual,
                TimeInForce.Day,
                quantity,
                0.0,
                0.0,
                null,
                $"QTSW2:FLATTEN:{snapshot.LatchRequestId}",
                DateTime.MinValue,
                null);
            SetOrderTag(order, $"QTSW2:FLATTEN:{snapshot.LatchRequestId}");
            account.Submit(new[] { order });

            _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "FLATTEN_SENT", snapshot.ToLogPayload()));

            lock (_flattenRecognitionLock)
            {
                _lastFlattenInstrument = ntInstrument?.MasterInstrument?.Name ?? instrument;
                _lastFlattenUtc = utcNow;
            }

            return OrderSubmissionResult.SuccessResult(order.OrderId, utcNow);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "FLATTEN_ORDER_REJECTED", new
            {
                instrument,
                error = ex.Message,
                snapshot = snapshot.ToLogPayload()
            }));
            return OrderSubmissionResult.FailureResult($"Flatten order failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// P2.6.7: Emergency flatten — still bypasses IEA queue but is constrained: strategy thread only + explicit policy classification before submit.
    /// </summary>
    FlattenResult IIEAOrderExecutor.EmergencyFlatten(string instrument, DateTimeOffset utcNow)
    {
        var currentThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        var inStrategyContext = _strategyThreadContextCount > 0 && _strategyThreadId == currentThreadId;
        if (!inStrategyContext)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EMERGENCY_FLATTEN_CONSTRAINT_VIOLATION", state: "CRITICAL",
                new
                {
                    instrument,
                    note = "P2.6.7: EmergencyFlatten must run on strategy thread — use NtFlattenInstrumentCommand / FlattenEmergency enqueue path"
                }));
            return FlattenResult.FailureResult("EmergencyFlatten requires strategy thread (enqueue FlattenEmergency or drain NT actions)", utcNow);
        }

        if (System.Threading.Volatile.Read(ref _sessionMismatchBlocked) != 0)
        {
            RecordSessionIdentityBlockAttempt();
            return FlattenResult.FailureResult("SESSION_IDENTITY_LATCHED", utcNow);
        }

        var exposure = ((IIEAOrderExecutor)this).GetBrokerCanonicalExposure(instrument);
        if (exposure.ReconciliationAbsQuantityTotal == 0)
            return FlattenResult.SuccessResult(utcNow);
        var execKey = _iea?.ExecutionInstrumentKey ?? instrument;
        var journalQty = 0;
        try
        {
            journalQty = SumOpenJournalForInstrument(instrument, execKey);
        }
        catch { /* observability */ }

        var emInput = new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.EMERGENCY,
            ExplicitTrigger = DestructiveTriggerReason.DIRECT_EMERGENCY_FLATTEN,
            RecoveryReasonString = "EMERGENCY_FLATTEN_BYPASS",
            ExecutionInstrumentKey = execKey,
            BrokerPositionQty = exposure.ReconciliationAbsQuantityTotal,
            JournalOpenQtySum = journalQty,
            ReconstructionActionKind = RecoveryActionKind.Flatten
        };
        var emDecision = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(emInput);
        _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "DESTRUCTIVE_ACTION_REQUESTED", new
        {
            source = DestructiveActionSource.EMERGENCY.ToString(),
            trigger = DestructiveTriggerReason.DIRECT_EMERGENCY_FLATTEN.ToString(),
            execution_instrument_key = execKey,
            phase = "emergency_flatten_direct"
        }));
        _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "DESTRUCTIVE_ACTION_DECISION", new
        {
            allowed = emDecision.AllowInstrumentScope,
            reason = emDecision.ReasonCode,
            scope = emDecision.CancelScopeMode,
            policy_path = emDecision.PolicyPath,
            phase = "emergency_flatten_direct"
        }));
        if (!emDecision.AllowInstrumentScope)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "DESTRUCTIVE_ACTION_BLOCKED", new
            {
                execution_instrument_key = execKey,
                reason_code = emDecision.ReasonCode,
                note = "P2.6.7: EmergencyFlatten denied by policy (unexpected)"
            }));
            return FlattenResult.FailureResult($"Emergency flatten policy denied ({emDecision.ReasonCode})", utcNow);
        }

        _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "EMERGENCY_FLATTEN_EXECUTING", new
        {
            instrument,
            canonical_broker_key = exposure.CanonicalKey,
            reconciliation_abs_total = exposure.ReconciliationAbsQuantityTotal,
            leg_count = exposure.Legs.Count,
            execution_instrument_key = execKey,
            policy_path = emDecision.PolicyPath,
            note = "P2.6.7: per-leg submit on canonical exposure (same as reconciliation)"
        }));
        OrderSubmissionResult? lastSubmit = null;
        for (var li = 0; li < exposure.Legs.Count; li++)
        {
            var leg = exposure.Legs[li];
            if (leg.SignedQuantity == 0) continue;
            var absQty = Math.Abs(leg.SignedQuantity);
            var chosenSide = leg.SignedQuantity > 0 ? "SELL" : "BUY";
            var requestId = $"EMERGENCY:{instrument}:{utcNow:yyyyMMddHHmmssfff}:L{li}";
            var snapshot = new FlattenDecisionSnapshot
            {
                Instrument = instrument,
                AccountQuantityAtDecision = leg.SignedQuantity,
                AccountDirectionAtDecision = leg.SignedQuantity > 0 ? "Long" : "Short",
                RequestedReason = "EMERGENCY_FLATTEN_BYPASS",
                CallerContext = "IIEAOrderExecutor.EmergencyFlatten",
                ChosenSide = chosenSide,
                ChosenQuantity = absQty,
                LatchRequestId = requestId,
                LatchState = "EmergencyBypass",
                DecisionUtc = utcNow,
                FlattenLegIndex = li,
                CanonicalExposureAbsTotalAtDecision = exposure.ReconciliationAbsQuantityTotal,
                LegContractLabel = leg.ContractLabel
            };
            lastSubmit = ((IIEAOrderExecutor)this).SubmitFlattenOrder(instrument, chosenSide, absQty, snapshot, utcNow, leg.NativeInstrument);
            if (!lastSubmit.Success)
                return FlattenResult.FailureResult(lastSubmit.ErrorMessage ?? "Emergency flatten failed", utcNow);
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_ORDER_SUBMITTED", state: "ENGINE",
                new
                {
                    instrument,
                    phase = "emergency_flatten_leg",
                    leg_index = li,
                    broker_order_id = lastSubmit.BrokerOrderId,
                    canonical_broker_key = exposure.CanonicalKey
                }));
            _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "DESTRUCTIVE_ACTION_EXECUTED", new
            {
                execution_instrument_key = execKey,
                source = DestructiveActionSource.EMERGENCY.ToString(),
                trigger = DestructiveTriggerReason.DIRECT_EMERGENCY_FLATTEN.ToString(),
                broker_order_id = lastSubmit.BrokerOrderId,
                phase = "emergency_flatten_direct_submit",
                leg_index = li
            }));
        }
        return FlattenResult.SuccessResult(utcNow);
    }

    void IIEAOrderExecutor.StandDownStream(string streamId, DateTimeOffset utcNow, string reason) =>
        _standDownStreamCallback?.Invoke(streamId, utcNow, reason);

    void IIEAOrderExecutor.ProcessExecutionUpdate(object execution, object order) =>
        HandleExecutionUpdateReal(execution, order);

    void IIEAOrderExecutor.ProcessOrderUpdate(object order, object orderUpdate) =>
        HandleOrderUpdateReal(order, orderUpdate);

    void IIEAOrderExecutor.FailClosed(string intentId, Intent intent, string failureReason, string eventType, string notificationKey, string notificationTitle, string notificationMessage, OrderSubmissionResult? stopResult, OrderSubmissionResult? targetResult, object? additionalData, DateTimeOffset utcNow) =>
        FailClosed(intentId, intent, failureReason, eventType, notificationKey, notificationTitle, notificationMessage, stopResult, targetResult, additionalData, utcNow);

    void INtActionExecutor.ExecuteSubmitProtectives(NtSubmitProtectivesCommand cmd)
    {
        const int MAX_RETRIES = 3;
        const int RETRY_DELAY_MS = 100;
        OrderSubmissionResult? stopResult = null;
        OrderSubmissionResult? targetResult = null;
        var intentId = cmd.IntentId ?? "";
        if (!string.IsNullOrEmpty(intentId))
            SetProtectionState(intentId, ProtectionState.Executing);
        if (!IntentMap.TryGetValue(intentId, out var intent))
            throw new InvalidOperationException($"Intent not found: {cmd.IntentId}");
        if (!TrySessionIdentityGate(intentId, cmd.Instrument ?? "", "recovery", cmd.UtcNow, null, out _))
            return;
        for (int attempt = 0; attempt < MAX_RETRIES; attempt++)
        {
            if (attempt > 0) System.Threading.Thread.Sleep(RETRY_DELAY_MS);
            if (_coordinator != null && !_coordinator.CanSubmitExit(cmd.IntentId!, cmd.Quantity))
            {
                if (!string.IsNullOrEmpty(intentId))
                    PruneIntentState(intentId, "exit_validation_failed");
                var err = "Exit validation failed during retry";
                FailClosed(cmd.IntentId!, intent, err, "PROTECTIVE_ORDERS_FAILED_FLATTENED", $"PROTECTIVE_FAILURE:{cmd.IntentId}",
                    "CRITICAL: Protective Order Failure", $"Exit validation failed. Stream: {intent.Stream}, Intent: {cmd.IntentId}.",
                    null, null, new { retry_count = MAX_RETRIES }, cmd.UtcNow);
                return;
            }
            var protectiveOcoGroup = GenerateProtectiveOcoGroup(cmd.IntentId!, attempt, cmd.UtcNow);
            _log.Write(RobotEvents.ExecutionBase(cmd.UtcNow, cmd.IntentId ?? "", cmd.Instrument, "STOP_SUBMIT_REQUESTED", new { correlation_id = cmd.CorrelationId, attempt = attempt + 1 }));
            stopResult = SubmitProtectiveStopReal(cmd.IntentId!, cmd.Instrument, cmd.Direction, cmd.StopPrice, cmd.Quantity, protectiveOcoGroup, cmd.UtcNow);
            _log.Write(RobotEvents.ExecutionBase(cmd.UtcNow, cmd.IntentId ?? "", cmd.Instrument, "STOP_SUBMIT_CONFIRMED", new { correlation_id = cmd.CorrelationId, success = stopResult.Success }));
            if (!stopResult.Success) { targetResult = OrderSubmissionResult.FailureResult("Stop failed", cmd.UtcNow); continue; }
            _log.Write(RobotEvents.ExecutionBase(cmd.UtcNow, cmd.IntentId ?? "", cmd.Instrument, "TARGET_SUBMIT_REQUESTED", new { correlation_id = cmd.CorrelationId, attempt = attempt + 1 }));
            targetResult = SubmitTargetOrderReal(cmd.IntentId!, cmd.Instrument, cmd.Direction, cmd.TargetPrice, cmd.Quantity, protectiveOcoGroup, cmd.UtcNow);
            _log.Write(RobotEvents.ExecutionBase(cmd.UtcNow, cmd.IntentId ?? "", cmd.Instrument, "TARGET_SUBMIT_CONFIRMED", new { correlation_id = cmd.CorrelationId, success = targetResult.Success }));
            if (targetResult.Success)
            {
                if (!string.IsNullOrEmpty(intentId))
                {
                    SetProtectionState(intentId, ProtectionState.Submitted);
                    _iea?.TryTransitionIntentLifecycle(intentId, IntentLifecycleTransition.PROTECTIVES_PLACED, null, cmd.UtcNow);
                }
                return;
            }
        }
        if (!string.IsNullOrEmpty(intentId))
            PruneIntentState(intentId, "protective_orders_failed");
        var failureReason = $"Protective orders failed after {MAX_RETRIES} retries: STOP: {stopResult?.ErrorMessage}, TARGET: {targetResult?.ErrorMessage}";
        FailClosed(cmd.IntentId!, intent, failureReason, "PROTECTIVE_ORDERS_FAILED_FLATTENED", $"PROTECTIVE_FAILURE:{cmd.IntentId}",
            "CRITICAL: Protective Order Failure", $"Entry filled but protective orders failed. Stream: {intent.Stream}, Intent: {cmd.IntentId}. {failureReason}",
            stopResult, targetResult, new { retry_count = MAX_RETRIES }, cmd.UtcNow);
    }

    void INtActionExecutor.ExecuteCancelOrders(NtCancelOrdersCommand cmd)
    {
        var utcDispatch = DateTimeOffset.UtcNow;
        if (cmd.ProtectiveOrdersOnly &&
            string.Equals(cmd.Reason, NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill, StringComparison.Ordinal))
        {
            _log.Write(RobotEvents.EngineBase(utcDispatch, cmd.PostCancelTradingDate ?? "", "SIBLING_PROTECTIVE_CANCEL_STRATEGY_DISPATCH", "ENGINE",
                new
                {
                    intent_id = cmd.IntentId,
                    correlation_id = cmd.CorrelationId,
                    stream = cmd.PostCancelStream,
                    completion_reason = cmd.PostCancelCompletionReason,
                    note = "Strategy thread executing URGENT NtCancelOrdersCommand (sibling protective cleanup)"
                }));
        }

        if (!string.IsNullOrEmpty(cmd.IntentId))
        {
            var siblingInline = cmd.ProtectiveOrdersOnly &&
                string.Equals(cmd.Reason, NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill, StringComparison.Ordinal);
            if (siblingInline)
                CancelProtectiveOrdersForIntent(cmd.IntentId, cmd.UtcNow, strategyThreadInlineCancel: true, siblingCancelCorrelationId: cmd.CorrelationId);
            else if (cmd.ProtectiveOrdersOnly)
                CancelProtectiveOrdersForIntent(cmd.IntentId, cmd.UtcNow);
            else
                CancelIntentOrdersReal(cmd.IntentId, cmd.UtcNow);
        }

        if (cmd.VerifyWorkingProtectivesClearedAfter && !string.IsNullOrEmpty(cmd.IntentId))
        {
            RunTerminalProtectiveInvariantCheck(cmd.IntentId,
                cmd.PostCancelTradingDate ?? "",
                cmd.PostCancelStream ?? "",
                cmd.PostCancelCompletionReason ?? cmd.Reason,
                cmd.UtcNow);
        }
    }

    /// <summary>Compute protective status from broker orders. Broker-truth based.</summary>
    private ProtectiveStatus ComputeProtectiveStatusFromBroker(Account? account, string instrument, int brokerPositionQty)
    {
        if (account?.Orders == null || brokerPositionQty == 0) return ProtectiveStatus.NONE;
        var instRoot = (instrument ?? "").Split(' ').FirstOrDefault() ?? instrument;
        int stopCount = 0, targetCount = 0, totalProtectiveQty = 0;
        foreach (var o in account.Orders)
        {
            if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue;
            if (!ExecutionInstrumentResolver.IsSameInstrument(o.Instrument?.MasterInstrument?.Name ?? o.Instrument?.FullName ?? "", instRoot)) continue;
            var tag = GetOrderTag(o);
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) continue;
            if (tag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase)) { stopCount++; totalProtectiveQty += o.Quantity; }
            else if (tag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase)) { targetCount++; totalProtectiveQty += o.Quantity; }
        }
        if (stopCount == 0 && targetCount == 0) return ProtectiveStatus.NONE;
        if (stopCount > 0 && targetCount == 0) return ProtectiveStatus.STOP_ONLY;
        if (stopCount == 0 && targetCount > 0) return ProtectiveStatus.TARGET_ONLY;
        if (totalProtectiveQty < brokerPositionQty) return ProtectiveStatus.QTY_MISMATCH;
        return ProtectiveStatus.BOTH_PRESENT;
    }

    /// <summary>Phase 4: Bootstrap snapshot callback. Gathers four views, builds BootstrapSnapshot, classifies, processes result.</summary>
    private void OnBootstrapSnapshotRequested(string instrument, BootstrapReason reason, DateTimeOffset utcNow)
    {
        if (_iea == null) return;
        var execInst = _iea.ExecutionInstrumentKey;
        int brokerQty = 0, brokerWorkingCount = 0;
        Account? account = null;
        try
        {
            if (!_iea.TryTransitionToSnapshooting(utcNow)) return;
            var exposure = ((IIEAOrderExecutor)this).GetBrokerCanonicalExposure(instrument);
            brokerQty = exposure.ReconciliationAbsQuantityTotal;
            account = _ntAccount as Account;
            if (account?.Orders != null)
            {
                var instRoot = (instrument ?? "").Split(' ').FirstOrDefault() ?? instrument;
                brokerWorkingCount = account.Orders.Count(o =>
                    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted) &&
                    ExecutionInstrumentResolver.IsSameInstrument(o.Instrument?.MasterInstrument?.Name ?? o.Instrument?.FullName ?? "", instRoot));
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_RECONSTRUCTION_ERROR", new { error = ex.Message, iea_instance_id = _iea.InstanceId }));
            _iea.ProcessBootstrapResult(new BootstrapSnapshot { Instrument = instrument, UnownedLiveOrderCount = 999 }, utcNow);
            return;
        }
        var journalQty = SumOpenJournalForInstrument(instrument, execInst);
        var registry = _iea.GetRegistrySnapshotForRecovery();
        var runtime = _iea.GetRuntimeIntentSnapshotForRecovery();
        var protectiveStatus = ComputeProtectiveStatusFromBroker(account, instrument, brokerQty);
        var slotJournalShowsEntryStops = _hasSlotJournalWithEntryStopsForInstrumentCallback?.Invoke(instrument) ?? false;
        var snapshot = new BootstrapSnapshot
        {
            Instrument = instrument,
            BrokerPositionQty = brokerQty,
            BrokerWorkingOrderCount = brokerWorkingCount,
            JournalQty = journalQty,
            UnownedLiveOrderCount = registry.UnownedLiveCount,
            RegistrySnapshot = registry,
            JournalSnapshot = null,
            RuntimeIntentSnapshot = runtime,
            CaptureUtc = utcNow,
            SnapshotEpoch = 0,
            ProtectiveStatus = protectiveStatus,
            SlotJournalShowsEntryStopsExpected = slotJournalShowsEntryStops
        };
        _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "BOOTSTRAP_SNAPSHOT_CAPTURED", new
        {
            instrument,
            broker_position_qty = brokerQty,
            broker_working_count = brokerWorkingCount,
            journal_qty = journalQty,
            unowned_live_count = registry.UnownedLiveCount,
            slot_journal_shows_entry_stops_expected = slotJournalShowsEntryStops,
            iea_instance_id = _iea.InstanceId
        }));
        var decision = _iea.ProcessBootstrapResult(snapshot, utcNow);
        if (decision == BootstrapDecision.ADOPT)
            _iea.RunBootstrapAdoption(instrument, utcNow);
    }

    /// <summary>P2 Phase 1: populate broker working rows for ownership attribution.</summary>
    private void EnrichRecoveryAttributionBrokerOrders(RecoveryAttributionSnapshot snap, string instrument)
    {
        if (_iea == null) return;
        var account = _ntAccount as Account;
        if (account?.Orders == null) return;
        var instRoot = (instrument ?? "").Split(' ').FirstOrDefault() ?? instrument ?? "";
        foreach (var o in account.Orders)
        {
            if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue;
            if (!ExecutionInstrumentResolver.IsSameInstrument(o.Instrument?.MasterInstrument?.Name ?? o.Instrument?.FullName ?? "", instRoot))
                continue;
            var id = o.OrderId.ToString() ?? "";
            var tag = GetOrderTag(o) ?? "";
            var row = new RecoveryAttributionBrokerOrderRow { BrokerOrderId = id, IsAggregatedTag = RobotOrderIds.IsAggregatedTag(tag) };
            if (row.IsAggregatedTag)
            {
                var agg = RobotOrderIds.DecodeAggregatedIntentIds(tag);
                if (agg != null) row.AggregatedIntentIds.AddRange(agg);
            }
            else if (!string.IsNullOrEmpty(tag) && tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase))
                row.TagIntentId = RobotOrderIds.DecodeIntentId(tag);
            if (_iea.TryResolveByBrokerOrderId(id, out var re) && re != null)
                row.RegistryIntentId = re.IntentId;
            snap.BrokerWorking.Add(row);
        }
    }

    /// <summary>P2.6: Rebuild recovery policy inputs when a flatten command lacks enqueue seal (legacy / defensive).</summary>
    private bool TryBuildRecoveryDestructivePolicyInputFromLiveState(
        string instrument,
        string reason,
        object? context,
        DateTimeOffset utcNow,
        out DestructiveActionPolicyInput? policyInput,
        out StateOwnershipAttributionResult? attributionOut,
        out RecoveryAttributionSnapshot? snapOut)
    {
        policyInput = null;
        attributionOut = null;
        snapOut = null;
        if (_iea == null) return false;
        var execInst = _iea.ExecutionInstrumentKey;
        int brokerQty = 0, brokerWorkingCount = 0;
        try
        {
            var exposure = ((IIEAOrderExecutor)this).GetBrokerCanonicalExposure(instrument);
            brokerQty = exposure.ReconciliationAbsQuantityTotal;
            var account = _ntAccount as Account;
            if (account?.Orders != null)
            {
                var instRoot = (instrument ?? "").Split(' ').FirstOrDefault() ?? instrument;
                brokerWorkingCount = account.Orders.Count(o =>
                    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted) &&
                    ExecutionInstrumentResolver.IsSameInstrument(o.Instrument?.MasterInstrument?.Name ?? o.Instrument?.FullName ?? "", instRoot));
            }
        }
        catch
        {
            return false;
        }
        var journalQty = SumOpenJournalForInstrument(instrument, execInst);
        var registry = _iea.GetRegistrySnapshotForRecovery();
        var runtime = _iea.GetRuntimeIntentSnapshotForRecovery();
        var result = RecoveryReconstructor.Reconstruct(instrument, brokerQty, brokerWorkingCount, journalQty, registry, runtime, reason);
        var actionKind = RecoveryPhase3DecisionRules.GetActionKind(result.Classification, result);
        if (actionKind != RecoveryActionKind.Flatten)
        {
            policyInput = new DestructiveActionPolicyInput
            {
                Source = DestructiveActionSource.RECOVERY,
                RecoveryReasonString = reason,
                ReconstructionActionKind = actionKind,
                ExecutionInstrumentKey = execInst,
                BrokerPositionQty = brokerQty,
                JournalOpenQtySum = journalQty
            };
            return true;
        }
        if (RecoveryOwnershipAttributionEvaluator.IsEmergencyInstrumentRecoveryTrigger(reason))
        {
            policyInput = new DestructiveActionPolicyInput
            {
                Source = DestructiveActionSource.RECOVERY,
                RecoveryReasonString = reason,
                ExplicitTrigger = DestructiveTriggerParser.TryParseStrictPrefix(reason),
                ReconstructionActionKind = actionKind,
                ExecutionInstrumentKey = execInst,
                BrokerPositionQty = brokerQty,
                JournalOpenQtySum = journalQty
            };
            return true;
        }
        string? triggerIntent = null;
        string? triggerBroker = null;
        try
        {
            if (context != null)
            {
                dynamic d = context;
                triggerIntent = d.intent_id?.ToString();
                triggerBroker = d.broker_order_id?.ToString();
            }
        }
        catch { /* best-effort */ }
        var gateEngaged = _instrumentMismatchGateEngaged?.Invoke(instrument) == true
                          || _instrumentMismatchGateEngaged?.Invoke(execInst) == true;
        var snap = _iea.BuildRecoveryAttributionSnapshot(brokerQty, brokerWorkingCount, journalQty, reason, triggerIntent, triggerBroker, gateEngaged);
        EnrichRecoveryAttributionBrokerOrders(snap, instrument);
        var attribution = RecoveryOwnershipAttributionEvaluator.EvaluateOwnershipAttributionForRecovery(snap);
        attributionOut = attribution;
        snapOut = snap;
        policyInput = new DestructiveActionPolicyInput
        {
            Source = DestructiveActionSource.RECOVERY,
            RecoveryReasonString = reason,
            ReconstructionActionKind = actionKind,
            GateEngagedForSymbol = gateEngaged,
            ExecutionInstrumentKey = execInst,
            BrokerPositionQty = brokerQty,
            JournalOpenQtySum = journalQty,
            Attribution = attribution,
            AttributionSnapshot = snap
        };
        return true;
    }

    /// <summary>P2.6.6: Shared template for pre-cancel policy and RequestFlatten final gate (broker qty refreshed in RequestFlatten).</summary>
    private bool TryBuildDestructivePolicyInputForFlattenCommand(NtFlattenInstrumentCommand cmd, DateTimeOffset utcNow, out DestructiveActionPolicyInput? input)
    {
        input = null;
        var execKey = _iea?.ExecutionInstrumentKey ?? "";
        if (cmd.HasRecoveryPolicySeal)
        {
            input = new DestructiveActionPolicyInput
            {
                Source = cmd.DestructiveSource,
                RecoveryReasonString = cmd.Reason,
                ExplicitTrigger = cmd.ExplicitPolicyTrigger,
                ExecutionInstrumentKey = execKey,
                RecoveryEnqueuePolicySealValid = true,
                RecoveryEnqueueAllowInstrument = cmd.RecoveryPolicySealAllowInstrument,
                RecoveryEnqueueReasonCode = cmd.RecoveryPolicySealCode,
                ReconstructionActionKind = RecoveryActionKind.Flatten
            };
            return true;
        }

        if (cmd.DestructiveSource != DestructiveActionSource.RECOVERY)
        {
            var built = new DestructiveActionPolicyInput
            {
                Source = cmd.DestructiveSource,
                RecoveryReasonString = cmd.Reason,
                ExplicitTrigger = cmd.ExplicitPolicyTrigger,
                ExecutionInstrumentKey = execKey,
                ReconstructionActionKind = RecoveryActionKind.Flatten
            };
            if (cmd.DestructiveSource == DestructiveActionSource.BOOTSTRAP)
                built.BootstrapAdministrativeFlatten = true;
            else if (cmd.DestructiveSource is DestructiveActionSource.MANUAL or DestructiveActionSource.COMMAND)
                built.ManualInstrumentFlatten = true;
            else if (cmd.DestructiveSource == DestructiveActionSource.FAIL_CLOSED)
                built.ExplicitTrigger = DestructiveTriggerReason.FAIL_CLOSED;
            else if (cmd.DestructiveSource == DestructiveActionSource.EMERGENCY)
                built.ExplicitTrigger = DestructiveTriggerReason.IEA_ENQUEUE_FAILURE;
            input = built;
            return true;
        }

        if (!TryBuildRecoveryDestructivePolicyInputFromLiveState(cmd.Instrument, cmd.Reason, null, utcNow, out var rebuilt, out _, out _))
        {
            input = new DestructiveActionPolicyInput
            {
                Source = DestructiveActionSource.RECOVERY,
                RecoveryReasonString = cmd.Reason,
                ExecutionInstrumentKey = execKey,
                ReconstructionActionKind = RecoveryActionKind.Halt
            };
            return true;
        }

        input = rebuilt;
        return true;
    }

    /// <summary>Phase 3: Recovery callback. Gathers broker/journal/registry/runtime data, runs reconstruction, processes result.</summary>
    private void OnRecoveryRequested(string instrument, string reason, object context, DateTimeOffset utcNow)
    {
        if (_iea == null) return;
        var execInst = _iea.ExecutionInstrumentKey;
        int brokerQty = 0, brokerWorkingCount = 0;
        try
        {
            var exposure = ((IIEAOrderExecutor)this).GetBrokerCanonicalExposure(instrument);
            brokerQty = exposure.ReconciliationAbsQuantityTotal;
            var account = _ntAccount as Account;
            if (account?.Orders != null)
            {
                var instRoot = (instrument ?? "").Split(' ').FirstOrDefault() ?? instrument;
                brokerWorkingCount = account.Orders.Count(o =>
                    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted) &&
                    ExecutionInstrumentResolver.IsSameInstrument(o.Instrument?.MasterInstrument?.Name ?? o.Instrument?.FullName ?? "", instRoot));
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "RECOVERY_RECONSTRUCTION_ERROR", new { error = ex.Message, iea_instance_id = _iea.InstanceId }));
            _iea.ProcessReconstructionResult(new ReconstructionResult { Instrument = instrument, Classification = ReconstructionClassification.UNSAFE_AMBIGUOUS_STATE }, utcNow);
            return;
        }
        var journalQty = SumOpenJournalForInstrument(instrument, execInst);
        var registry = _iea.GetRegistrySnapshotForRecovery();
        var runtime = _iea.GetRuntimeIntentSnapshotForRecovery();
        var result = RecoveryReconstructor.Reconstruct(instrument, brokerQty, brokerWorkingCount, journalQty, registry, runtime, reason);
        var actionKind = RecoveryPhase3DecisionRules.GetActionKind(result.Classification, result);

        string? triggerIntent = null;
        string? triggerBroker = null;
        try
        {
            if (context != null)
            {
                dynamic d = context;
                triggerIntent = d.intent_id?.ToString();
                triggerBroker = d.broker_order_id?.ToString();
            }
        }
        catch { /* best-effort context parse */ }

        var gateEngaged = _instrumentMismatchGateEngaged?.Invoke(instrument) == true
                          || _instrumentMismatchGateEngaged?.Invoke(execInst) == true;

        StateOwnershipAttributionResult? p2Attribution = null;
        if (actionKind == RecoveryActionKind.Flatten && !RecoveryOwnershipAttributionEvaluator.IsEmergencyInstrumentRecoveryTrigger(reason))
        {
            var snap = _iea.BuildRecoveryAttributionSnapshot(brokerQty, brokerWorkingCount, journalQty, reason, triggerIntent, triggerBroker, gateEngaged);
            EnrichRecoveryAttributionBrokerOrders(snap, instrument);
            var attribution = RecoveryOwnershipAttributionEvaluator.EvaluateOwnershipAttributionForRecovery(snap);
            p2Attribution = attribution;
            _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "STREAM_OWNERSHIP_AMBIGUITY_DETECTED", new
            {
                execution_instrument_key = execInst,
                implicated_streams = attribution.ImplicatedStreams,
                implicated_intent_ids = attribution.ImplicatedIntentIds,
                unattributed_order_ids = attribution.UnattributedBrokerOrderIds,
                attributable_scope = attribution.AttributableScope.ToString(),
                gate_state = gateEngaged ? "engaged" : "not_engaged",
                broker_position_qty = brokerQty,
                broker_working_count = brokerWorkingCount,
                iea_instance_id = _iea.InstanceId
            }));
            var recoveryPolicyInput = new DestructiveActionPolicyInput
            {
                Source = DestructiveActionSource.RECOVERY,
                RecoveryReasonString = reason,
                ReconstructionActionKind = RecoveryActionKind.Flatten,
                GateEngagedForSymbol = gateEngaged,
                ExecutionInstrumentKey = execInst,
                BrokerPositionQty = brokerQty,
                JournalOpenQtySum = journalQty,
                Attribution = attribution,
                AttributionSnapshot = snap
            };
            var recoveryPolicyDecision = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(recoveryPolicyInput);
            _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "DESTRUCTIVE_ACTION_DECISION", new
            {
                allowed = recoveryPolicyDecision.AllowInstrumentScope,
                reason = recoveryPolicyDecision.ReasonCode,
                scope = recoveryPolicyDecision.CancelScopeMode,
                policy_path = recoveryPolicyDecision.PolicyPath,
                phase = "on_recovery_requested_instrument_escalation",
                iea_instance_id = _iea.InstanceId
            }));
            if (!recoveryPolicyDecision.AllowInstrumentScope)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "INSTRUMENT_SCOPED_RECOVERY_BLOCKED_BY_ATTRIBUTION", new
                {
                    execution_instrument_key = execInst,
                    summary = attribution.Summary,
                    recommended_scope = attribution.RecommendedRecoveryScope.ToString(),
                    sibling_streams_preserved = true,
                    destructive_action_blocked = true,
                    policy_reason_code = recoveryPolicyDecision.ReasonCode,
                    iea_instance_id = _iea.InstanceId
                }));
                _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "DESTRUCTIVE_ACTION_BLOCKED", new
                {
                    execution_instrument_key = execInst,
                    reason_code = recoveryPolicyDecision.ReasonCode,
                    phase = "on_recovery_requested_stream_containment",
                    iea_instance_id = _iea.InstanceId
                }));
                _iea.ProcessReconstructionResult(result, utcNow, new P2PostReconstructionDirective
                {
                    UseStreamContainmentInsteadOfInstrumentFlatten = true,
                    Attribution = attribution
                });
                return;
            }
            if (attribution.IsContradictory)
                _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "CROSS_STREAM_CONTRADICTION_DETECTED", new
                {
                    execution_instrument_key = execInst,
                    contradictions = attribution.Contradictions,
                    iea_instance_id = _iea.InstanceId
                }));
            _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "INSTRUMENT_ESCALATION_APPROVED", new
            {
                attribution_scope = attribution.AttributableScope.ToString(),
                reason,
                gate_engaged = gateEngaged,
                broker_position_qty = brokerQty,
                journal_open_qty = journalQty,
                execution_instrument_key = execInst,
                iea_instance_id = _iea.InstanceId
            }));
            _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "INSTRUMENT_SCOPED_RECOVERY_ALLOWED", new
            {
                execution_instrument_key = execInst,
                summary = attribution.Summary,
                attributable_scope = attribution.AttributableScope.ToString(),
                iea_instance_id = _iea.InstanceId
            }));
        }

        var decision = _iea.ProcessReconstructionResult(result, utcNow, null);
        if (decision == InstrumentExecutionAuthority.RecoveryDecision.FLATTEN)
        {
            var emergRec = RecoveryOwnershipAttributionEvaluator.IsEmergencyInstrumentRecoveryTrigger(reason);
            var meta = new RecoveryFlattenNtMetadata
            {
                HasSeal = true,
                SealAllowInstrument = true,
                SealReasonCode = emergRec ? "EMERGENCY_RECOVERY" : "recovery_attribution_allows_instrument",
                AttributionScope = emergRec ? "" : (p2Attribution?.AttributableScope.ToString() ?? ""),
                DestructiveSource = DestructiveActionSource.RECOVERY
            };
            EnqueueRecoveryFlattenNtCommand(instrument, reason, "RECOVERY_PHASE3", utcNow, meta);
        }
    }

    /// <summary>IEA recovery flatten callback (fixed arity). Consumes staged P2.6 metadata from IEA.</summary>
    private void OnRecoveryFlattenRequestedFromIeCallback(string instrument, string reason, string callerContext, DateTimeOffset utcNow)
    {
        RecoveryFlattenNtMetadata? meta = null;
        _iea?.TryConsumeRecoveryFlattenNtMetadata(out meta);
        EnqueueRecoveryFlattenNtCommand(instrument, reason, callerContext, utcNow, meta);
    }

    /// <summary>Phase 3: Enqueue NtFlattenInstrumentCommand with P2.6 policy seal / source.</summary>
    private void EnqueueRecoveryFlattenNtCommand(string instrument, string reason, string callerContext, DateTimeOffset utcNow, RecoveryFlattenNtMetadata? meta)
    {
        var src = meta?.DestructiveSource ?? DestructiveActionSource.RECOVERY;
        var prefix = src == DestructiveActionSource.BOOTSTRAP ? "BOOTSTRAP" : "RECOVERY";
        var cid = $"{prefix}:{instrument}:{utcNow:yyyyMMddHHmmssfff}";
        var hasSeal = meta?.HasSeal ?? false;
        var sealAllow = meta?.SealAllowInstrument ?? false;
        var cmd = new NtFlattenInstrumentCommand(
            cid,
            null,
            instrument,
            reason,
            utcNow,
            destructiveSource: src,
            explicitPolicyTrigger: null,
            allowAccountWideCancelFallback: false,
            hasRecoveryPolicySeal: hasSeal,
            recoveryPolicySealAllowInstrument: sealAllow,
            recoveryPolicySealCode: meta?.SealReasonCode,
            recoveryPolicySealAttributionScope: meta?.AttributionScope);
        EnqueueNtActionInternal(cmd);
    }

    void INtActionExecutor.ExecuteFlattenInstrument(NtFlattenInstrumentCommand cmd)
    {
        var utcNow = cmd.UtcNow;
        var execKey = _iea?.ExecutionInstrumentKey ?? "";
        var coordInst = GetFlattenCoordinationInstanceId();
        var coordAcct = GetCoordinationAccountName();
        var canonicalKey = BrokerPositionResolver.NormalizeCanonicalKey(cmd.Instrument);
        if (!string.IsNullOrEmpty(canonicalKey) &&
            !FlattenCoordinationTracker.Shared.IsActiveFlattenOwner(coordAcct, cmd.Instrument, coordInst))
        {
            FlattenCoordinationTracker.Shared.TryPeekKey(coordAcct, cmd.Instrument, out var ow, out var epi, out _);
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_SECONDARY_INSTANCE_SKIPPED", state: "ENGINE",
                new
                {
                    account = coordAcct,
                    canonical_broker_key = canonicalKey,
                    owner_instance_id = ow,
                    current_instance_id = coordInst,
                    episode_id = epi,
                    host_chart_instrument = GetHostChartInstrumentName(),
                    correlation_id = cmd.CorrelationId,
                    reason = "execute_phase_not_coordination_owner",
                    critical_flatten = IsCriticalFlattenCommand(cmd)
                }));
            if (IsCriticalFlattenCommand(cmd))
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_COORDINATION_SKIP_ESCALATION", state: "CRITICAL",
                    new
                    {
                        account = coordAcct,
                        canonical_broker_key = canonicalKey,
                        correlation_id = cmd.CorrelationId,
                        instrument = cmd.Instrument,
                        phase = "execute_flatten_instrument",
                        note = "Non-owner skipped critical flatten execute — escalating block"
                    }));
                _blockInstrumentCallback?.Invoke(cmd.Instrument ?? "", utcNow, "FLATTEN_EXECUTE_NON_OWNER_CRITICAL");
                ScheduleCriticalFlattenCoordinationRetryIfNeeded(cmd, "execute_flatten_instrument");
            }
            return;
        }

        var preTrigger = DestructiveTriggerParser.Resolve(cmd.ExplicitPolicyTrigger, cmd.Reason);
        _log.Write(RobotEvents.EngineBase(utcNow, "", cmd.Instrument, "DESTRUCTIVE_ACTION_REQUESTED", new
        {
            execution_instrument_key = execKey,
            correlation_id = cmd.CorrelationId,
            source = cmd.DestructiveSource.ToString(),
            trigger = preTrigger.ToString(),
            instrument = cmd.Instrument,
            phase = "execute_flatten_instrument_entry"
        }));

        TryBuildDestructivePolicyInputForFlattenCommand(cmd, utcNow, out var policyTemplate);
        var decision = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(policyTemplate!);
        _log.Write(RobotEvents.EngineBase(utcNow, "", cmd.Instrument, "DESTRUCTIVE_ACTION_DECISION", new
        {
            allowed = decision.AllowInstrumentScope,
            reason = decision.ReasonCode,
            scope = decision.CancelScopeMode,
            policy_path = decision.PolicyPath,
            phase = "pre_cancel_execute_flatten_instrument",
            correlation_id = cmd.CorrelationId
        }));

        int brokerQtySigned = 0, brokerQty = 0, journalQty = 0;
        try
        {
            var exposurePrecheck = ((IIEAOrderExecutor)this).GetBrokerCanonicalExposure(cmd.Instrument);
            brokerQty = exposurePrecheck.ReconciliationAbsQuantityTotal;
            brokerQtySigned = exposurePrecheck.Legs.Sum(l => l.SignedQuantity);
            journalQty = SumOpenJournalForInstrument(cmd.Instrument, execKey);
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_BROKER_EXPOSURE_PRECHECK", state: "ENGINE",
                new
                {
                    correlation_id = cmd.CorrelationId,
                    execution_instrument_key = execKey,
                    logical_instrument = cmd.Instrument,
                    canonical_broker_key = exposurePrecheck.CanonicalKey,
                    reconciliation_abs_total = exposurePrecheck.ReconciliationAbsQuantityTotal,
                    broker_exposure_aggregated = exposurePrecheck.IsAggregatedMultipleRows,
                    broker_position_rows = BrokerPositionResolver.ToDiagnosticRows(exposurePrecheck),
                    host_chart_instrument = (_ntInstrument as Instrument)?.FullName,
                    note = "Canonical exposure (same model as reconciliation) before cancel+flatten"
                }));
        }
        catch { /* observability only */ }

        if (!decision.AllowInstrumentScope)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", cmd.Instrument, "DESTRUCTIVE_ACTION_BLOCKED", new
            {
                execution_instrument_key = execKey,
                correlation_id = cmd.CorrelationId,
                trigger_source = cmd.DestructiveSource.ToString(),
                reason_code = decision.ReasonCode,
                policy_path = decision.PolicyPath,
                is_emergency = decision.IsEmergency,
                note = "P2.6: instrument flatten denied by centralized policy"
            }));
            FlattenCoordinationTracker.Shared.NotifyFlattenAborted(coordAcct, cmd.Instrument, coordInst, DateTimeOffset.UtcNow);
            throw new InvalidOperationException($"P2.6: destructive policy denied instrument flatten ({decision.ReasonCode})");
        }

        try
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, cmd.IntentId ?? "", cmd.Instrument, "FLATTEN_REQUESTED", new { correlation_id = cmd.CorrelationId, reason = cmd.Reason }));
            // Cancel-then-flatten: cancel robot-owned working orders first (P2.6: scoped to instrument unless explicit set / fallback)
            CancelRobotOwnedWorkingOrdersReal(default, utcNow, cmd.Instrument, cmd.ExplicitCancelBrokerOrderIds, cmd.AllowAccountWideCancelFallback, cmd.CorrelationId);
            try
            {
                var journalAfterCancel = SumOpenJournalForInstrument(cmd.Instrument, execKey);
                if (journalAfterCancel != journalQty)
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, "", cmd.Instrument, "DESTRUCTIVE_POLICY_PREPOST_DRIFT", new
                    {
                        drift_kind = "journal_open_qty_after_cancel_phase",
                        precheck_journal = journalQty,
                        post_cancel_journal = journalAfterCancel,
                        correlation_id = cmd.CorrelationId,
                        note = "P2.6.7: journal sum changed between pre-check and post-cancel (RequestFlatten still uses final broker read)"
                    }));
                }
            }
            catch { /* observability */ }

            FlattenResult result;
            if (_useInstrumentExecutionAuthority && _iea != null)
            {
                var flattenCtx = FlattenPolicyExecutionContext.FromDestructivePolicyInput(policyTemplate!);
                flattenCtx.PrecheckBrokerPositionQtyAbs = brokerQty;
                flattenCtx.PrecheckBrokerQtySigned = brokerQtySigned;
                flattenCtx.PrecheckJournalOpenQtySum = journalQty;
                flattenCtx.PrecheckCorrelationId = cmd.CorrelationId;
                flattenCtx.PrecheckPolicyReasonCode = decision.ReasonCode;
                flattenCtx.PrecheckAllowInstrument = decision.AllowInstrumentScope;
                result = _iea.RequestFlatten(cmd.Instrument, cmd.Reason, cmd.IntentId ?? "NT_FLATTEN", cmd.UtcNow, flattenCtx);
            }
            else
            {
                var token = NtDestructivePolicyAlreadyAppliedToken.ForNtFlattenCommand(cmd, decision.ReasonCode);
                result = FlattenIntentReal(cmd.IntentId ?? "NT_FLATTEN", cmd.Instrument, cmd.UtcNow, policyPrechecked: token, cmd.DestructiveSource, cmd.ExplicitPolicyTrigger);
            }
            _log.Write(RobotEvents.ExecutionBase(cmd.UtcNow, cmd.IntentId ?? "", cmd.Instrument, "FLATTEN_SUBMITTED", new { correlation_id = cmd.CorrelationId, success = result.Success }));
            if (!result.Success) throw new InvalidOperationException($"Flatten failed: {result.ErrorMessage}");
            _log.Write(RobotEvents.EngineBase(cmd.UtcNow, tradingDate: "", eventType: "FLATTEN_ORDER_SUBMITTED", state: "ENGINE",
                new
                {
                    correlation_id = cmd.CorrelationId,
                    instrument = cmd.Instrument,
                    note = "Flatten path completed submit phase (per-leg submits may be logged in IEA); broker flat not confirmed until FLATTEN_BROKER_FLAT_CONFIRMED"
                }));
            FlattenCoordinationTracker.Shared.NotifyFlattenSubmitted(coordAcct, cmd.Instrument, coordInst, cmd.UtcNow);
            RegisterPendingFlattenVerification(cmd.Instrument, cmd.CorrelationId, cmd.UtcNow);
        }
        catch
        {
            FlattenCoordinationTracker.Shared.NotifyFlattenAborted(coordAcct, cmd.Instrument, coordInst, DateTimeOffset.UtcNow);
            throw;
        }
    }

    void INtActionExecutor.ExecuteSubmitEntryIntent(NtSubmitEntryIntentCommand ntCmd)
    {
        var cmd = ntCmd.Command;
        var utcNow = cmd.TimestampUtc;
        var instrument = cmd.Instrument ?? cmd.ExecutionInstrument ?? "";
        var execInst = cmd.ExecutionInstrument ?? instrument;
        var longIntentId = cmd.LongIntentId;
        var shortIntentId = cmd.ShortIntentId;
        var qty = cmd.Quantity ?? 1;
        var maxQty = cmd.MaxQuantity ?? qty;
        var canonical = cmd.CanonicalInstrument ?? instrument;
        var ocoGroup = cmd.OcoGroup;

        if (string.IsNullOrEmpty(longIntentId) || string.IsNullOrEmpty(shortIntentId) ||
            !cmd.BreakLong.HasValue || !cmd.BreakShort.HasValue ||
            !cmd.LongStopPrice.HasValue || !cmd.ShortStopPrice.HasValue)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "SUBMIT_ENTRY_INTENT_SKIPPED",
                new { reason = "Missing required fields", longIntentId, shortIntentId }));
            return;
        }

        var longIntent = new Intent(
            cmd.TradingDate ?? "",
            cmd.Stream ?? "",
            instrument,
            execInst,
            cmd.Session ?? "",
            cmd.SlotTimeChicago ?? "",
            "Long",
            cmd.BreakLong.Value,
            cmd.LongStopPrice.Value,
            cmd.LongTargetPrice ?? cmd.LongStopPrice.Value,
            cmd.LongBeTrigger ?? cmd.LongStopPrice.Value,
            utcNow,
            "SUBMIT_ENTRY_INTENT_LONG");

        var shortIntent = new Intent(
            cmd.TradingDate ?? "",
            cmd.Stream ?? "",
            instrument,
            execInst,
            cmd.Session ?? "",
            cmd.SlotTimeChicago ?? "",
            "Short",
            cmd.BreakShort.Value,
            cmd.ShortStopPrice.Value,
            cmd.ShortTargetPrice ?? cmd.ShortStopPrice.Value,
            cmd.ShortBeTrigger ?? cmd.ShortStopPrice.Value,
            utcNow,
            "SUBMIT_ENTRY_INTENT_SHORT");

        RegisterIntent(longIntent);
        RegisterIntent(shortIntent);
        RegisterIntentPolicy(longIntentId, qty, maxQty, canonical, execInst, "EXECUTION_COMMAND");
        RegisterIntentPolicy(shortIntentId, qty, maxQty, canonical, execInst, "EXECUTION_COMMAND");

        var longRes = SubmitStopEntryOrder(longIntentId, execInst, "Long", cmd.BreakLong.Value, qty, ocoGroup, utcNow);
        var shortRes = SubmitStopEntryOrder(shortIntentId, execInst, "Short", cmd.BreakShort.Value, qty, ocoGroup, utcNow);

        _log.Write(RobotEvents.ExecutionBase(utcNow, longIntentId, execInst, "SUBMIT_ENTRY_INTENT_COMPLETED",
            new { long_success = longRes.Success, short_success = shortRes.Success }));
    }

    void INtActionExecutor.ExecuteSubmitMarketReentry(NtSubmitMarketReentryCommand ntCmd)
    {
        var cmd = ntCmd.Command;
        var utcNow = cmd.TimestampUtc;
        var instrument = cmd.ExecutionInstrument ?? cmd.Instrument ?? "";
        var execInst = string.IsNullOrEmpty(instrument) ? "" : instrument.Trim().ToUpperInvariant();
        var reentryIntentId = cmd.ReentryIntentId ?? "";
        var direction = cmd.Direction ?? "";
        var quantity = cmd.Quantity;

        if (string.IsNullOrEmpty(reentryIntentId) || string.IsNullOrEmpty(execInst) || string.IsNullOrEmpty(direction) || quantity <= 0)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, reentryIntentId, execInst, "SUBMIT_MARKET_REENTRY_SKIPPED",
                new { reason = "Missing required fields", reentryIntentId, execInst, direction, quantity }));
            return;
        }

        var reentryIntent = new Intent(
            cmd.TradingDate ?? "",
            cmd.Stream ?? "",
            instrument,
            execInst,
            cmd.Session ?? "",
            cmd.SlotTimeChicago ?? "",
            direction,
            null,
            null,
            null,
            null,
            utcNow,
            "SUBMIT_MARKET_REENTRY");
        RegisterIntent(reentryIntent);
        RegisterIntentPolicy(reentryIntentId, quantity, quantity, instrument, execInst, "EXECUTION_COMMAND");

        var result = SubmitEntryOrder(reentryIntentId, execInst, direction, null, quantity, "MARKET", null, utcNow);
        _log.Write(RobotEvents.ExecutionBase(utcNow, reentryIntentId, execInst, "SUBMIT_MARKET_REENTRY_COMPLETED",
            new { success = result.Success, error = result.ErrorMessage }));
    }

    private void RegisterPendingFlattenVerification(string instrumentKey, string correlationId, DateTimeOffset utcNow)
    {
        var deadline = utcNow.AddSeconds(FLATTEN_VERIFY_WINDOW_SEC);
        var coordAcct = GetCoordinationAccountName();
        var coordInst = GetFlattenCoordinationInstanceId();
        FlattenCoordinationTracker.Shared.TryPeekEpisodeForOwner(coordAcct, instrumentKey, coordInst, out var eid, out _, out _);
        _pendingFlattenVerifications[instrumentKey] = (utcNow, deadline, correlationId, eid);
    }

    partial void OnVerifyPendingFlattens()
    {
        var utcNow = DateTimeOffset.UtcNow;
        if (_ntAccount as Account == null) return;

        var coordAcct = GetCoordinationAccountName();
        var coordInst = GetFlattenCoordinationInstanceId();
        var hostChart = (_ntInstrument as Instrument)?.FullName;

        var toRemove = new List<string>();
        foreach (var kvp in _pendingFlattenVerifications.ToArray())
        {
            var instrumentKey = kvp.Key;
            var (requestedUtc, deadline, correlationId, pendingEpisodeId) = kvp.Value;
            if (utcNow < deadline) continue;

            var canonicalForKey = BrokerPositionResolver.NormalizeCanonicalKey(instrumentKey);
            if (!string.IsNullOrEmpty(canonicalForKey) &&
                !FlattenCoordinationTracker.Shared.IsActiveFlattenOwner(coordAcct, instrumentKey, coordInst))
            {
                FlattenCoordinationTracker.Shared.TryPeekKey(coordAcct, instrumentKey, out var ow, out var epi, out _);
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_SECONDARY_INSTANCE_SKIPPED", state: "ENGINE",
                    new
                    {
                        account = coordAcct,
                        canonical_broker_key = canonicalForKey,
                        owner_instance_id = ow,
                        current_instance_id = coordInst,
                        episode_id = epi,
                        host_chart_instrument = hostChart,
                        correlation_id = correlationId,
                        reason = "verify_window_non_owner_pending_dropped"
                    }));
                toRemove.Add(instrumentKey);
                continue;
            }

            var exposure = ((IIEAOrderExecutor)this).GetBrokerCanonicalExposure(instrumentKey);
            var remainingAbs = exposure.ReconciliationAbsQuantityTotal;

            if (remainingAbs == 0)
            {
                FlattenCoordinationTracker.Shared.TryPeekEpisodeForOwner(coordAcct, instrumentKey, coordInst, out _, out var vac0, out _);
                var pv0 = FlattenCoordinationTracker.Shared.ProcessVerifyWindow(coordAcct, instrumentKey, coordInst, 0, utcNow, out _, out _, out _);
                if (pv0 != FlattenVerifyProcessOutcome.ResolvedFlat)
                    continue;

                _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrumentKey, "FLATTEN_VERIFY_PASS",
                    new
                    {
                        correlation_id = correlationId,
                        instrument = instrumentKey,
                        episode_id = pendingEpisodeId,
                        owner_instance_id = coordInst,
                        verify_attempt_count = vac0
                    }));
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_BROKER_FLAT_CONFIRMED", state: "ENGINE",
                    new
                    {
                        account = coordAcct,
                        correlation_id = correlationId,
                        logical_instrument = instrumentKey,
                        canonical_broker_key = exposure.CanonicalKey,
                        reconciliation_abs_remaining = remainingAbs,
                        broker_position_rows = BrokerPositionResolver.ToDiagnosticRows(exposure),
                        host_chart_instrument = hostChart,
                        owner_instance_id = coordInst,
                        current_instance_id = coordInst,
                        episode_id = pendingEpisodeId
                    }));
                try
                {
                    var uDone = _executionJournal.CompleteOpenUntrackedFillRecoveryForInstrument(
                        instrumentKey,
                        string.IsNullOrEmpty(canonicalForKey) ? null : canonicalForKey,
                        utcNow,
                        CompletionReasons.UNTRACKED_FILL_RECOVERY_FLAT);
                    if (uDone > 0)
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, "", "UNTRACKED_FILL_RECOVERY_JOURNAL_COMPLETED_VERIFY", "ENGINE",
                            new { instrument = instrumentKey, count = uDone, correlation_id = correlationId }));
                    }
                }
                catch (Exception ex)
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, "", "UNTRACKED_FILL_RECOVERY_JOURNAL_COMPLETE_ERROR", "ENGINE",
                        new { instrument = instrumentKey, error = ex.Message, exception_type = ex.GetType().Name }));
                }
                toRemove.Add(instrumentKey);
                continue;
            }

            var pv = FlattenCoordinationTracker.Shared.ProcessVerifyWindow(coordAcct, instrumentKey, coordInst, remainingAbs, utcNow, out _, out var epAfter, out var worsened);
            FlattenCoordinationTracker.Shared.TryPeekEpisodeForOwner(coordAcct, instrumentKey, coordInst, out _, out var verifyAttemptCount, out var consecNonzero);

            _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrumentKey, "FLATTEN_VERIFY_FAIL",
                new
                {
                    correlation_id = correlationId,
                    instrument = instrumentKey,
                    position_qty = remainingAbs,
                    verify_attempt_count = verifyAttemptCount,
                    consecutive_nonzero_verifies = consecNonzero
                }));

            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_BROKER_POSITION_REMAINS", state: "ENGINE",
                new
                {
                    account = coordAcct,
                    correlation_id = correlationId,
                    logical_instrument = instrumentKey,
                    canonical_broker_key = exposure.CanonicalKey,
                    reconciliation_abs_remaining = remainingAbs,
                    broker_exposure_aggregated = exposure.IsAggregatedMultipleRows,
                    broker_position_rows = BrokerPositionResolver.ToDiagnosticRows(exposure),
                    host_chart_instrument = hostChart,
                    owner_instance_id = coordInst,
                    current_instance_id = coordInst,
                    episode_id = string.IsNullOrEmpty(epAfter) ? pendingEpisodeId : epAfter,
                    verify_attempt_count = verifyAttemptCount,
                    consecutive_nonzero_verifies = consecNonzero,
                    note = "Post-flatten verification: canonical exposure still non-zero; recovery/mismatch not cleared by submit alone"
                }));

            switch (pv)
            {
                case FlattenVerifyProcessOutcome.FailedPersistent:
                    FlattenCoordinationTracker.Shared.MarkFailedPersistent(coordAcct, instrumentKey, coordInst, utcNow);
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_FAILED_PERSISTENT", state: "CRITICAL",
                        new
                        {
                            account = coordAcct,
                            correlation_id = correlationId,
                            instrument = instrumentKey,
                            canonical_broker_key = exposure.CanonicalKey,
                            reconciliation_abs_remaining = remainingAbs,
                            verify_attempt_count = verifyAttemptCount,
                            episode_id = pendingEpisodeId,
                            owner_instance_id = coordInst,
                            metrics_flatten_failed_persistent_total = FlattenCoordinationTracker.Shared.Metrics.FlattenFailedPersistentTotal,
                            note = "Flatten verify failed after max coordinator retries - fail-closed"
                        }));
                    _standDownStreamCallback?.Invoke("", utcNow, $"FLATTEN_FAILED_PERSISTENT:{instrumentKey}");
                    _blockInstrumentCallback?.Invoke(instrumentKey, utcNow, $"FLATTEN_FAILED_PERSISTENT:{instrumentKey}");
                    toRemove.Add(instrumentKey);
                    break;

                case FlattenVerifyProcessOutcome.DebounceExtendWindow:
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_VERIFY_STILL_OPEN_DEBOUNCED", state: "ENGINE",
                        new
                        {
                            account = coordAcct,
                            canonical_broker_key = exposure.CanonicalKey,
                            reconciliation_abs_remaining = remainingAbs,
                            consecutive_nonzero_verifies = consecNonzero,
                            verify_attempt_count = verifyAttemptCount,
                            owner_instance_id = coordInst,
                            episode_id = pendingEpisodeId,
                            metrics_flatten_verify_debounced_total = FlattenCoordinationTracker.Shared.Metrics.FlattenVerifyDebouncedTotal
                        }));
                    _pendingFlattenVerifications[instrumentKey] = (requestedUtc, utcNow.AddSeconds(FLATTEN_VERIFY_WINDOW_SEC), correlationId, pendingEpisodeId);
                    break;

                case FlattenVerifyProcessOutcome.EnqueueRetryFlatten:
                    if (worsened)
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_VERIFY_EXPOSURE_WORSENED", state: "ENGINE",
                            new
                            {
                                account = coordAcct,
                                canonical_broker_key = exposure.CanonicalKey,
                                reconciliation_abs_remaining = remainingAbs,
                                owner_instance_id = coordInst,
                                current_instance_id = coordInst,
                                episode_id = pendingEpisodeId,
                                metrics_flatten_verify_worsened_total = FlattenCoordinationTracker.Shared.Metrics.FlattenVerifyWorsenedTotal
                            }));
                    }

                    var newCid = $"{correlationId}:V{verifyAttemptCount}";
                    var retryCmd = new NtFlattenInstrumentCommand(
                        newCid,
                        null,
                        instrumentKey,
                        $"VERIFY_FAIL_RETRY_{verifyAttemptCount}",
                        utcNow,
                        DestructiveActionSource.MANUAL,
                        DestructiveTriggerReason.MANUAL,
                        allowAccountWideCancelFallback: false,
                        hasRecoveryPolicySeal: false,
                        recoveryPolicySealAllowInstrument: false,
                        recoveryPolicySealCode: null,
                        recoveryPolicySealAttributionScope: null,
                        explicitCancelBrokerOrderIds: null,
                        isVerifyRetryFlatten: true);
                    EnqueueNtActionInternal(retryCmd);
                    FlattenCoordinationTracker.Shared.TryPeekEpisodeForOwner(coordAcct, instrumentKey, coordInst, out var ep2, out _, out _);
                    _pendingFlattenVerifications[instrumentKey] = (requestedUtc, utcNow.AddSeconds(FLATTEN_VERIFY_WINDOW_SEC), newCid, string.IsNullOrEmpty(ep2) ? pendingEpisodeId : ep2);
                    break;
            }
        }

        foreach (var k in toRemove)
            _pendingFlattenVerifications.TryRemove(k, out _);
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
        string? ocoGroup,
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
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "DUPLICATE_ORDER_SUBMISSION_DETECTED", new
                {
                    intent_id = intentId,
                    instrument = instrument,
                    role = "entry",
                    side = existingOrder.Direction ?? "unknown",
                    qty = existingOrder.Quantity,
                    price = existingOrder.Price,
                    first_order_id = existingOrder.OrderId,
                    second_order_id = (string?)null,
                    window_ms = 5000,
                    reason = "Entry order already exists for intent"
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
                        ocoGroup,                               // Oco (for MARKET+STOP bracket when breakout crossed)
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
                // Market/Limit orders: Use same 12-parameter CreateOrder overload as StopMarket
                // NT8 CreateOrder(instrument, action, orderType, orderEntry, timeInForce, quantity, limitPrice, stopPrice, oco, orderName, gtd, customOrder)
                double limitPrice = orderType == OrderType.Market ? 0.0 : ntEntryPrice;
                double stopPrice = 0.0;
                try
                {
                    order = account.CreateOrder(
                        ntInstrument,                           // Instrument
                        orderAction,                            // OrderAction
                        orderType,                              // OrderType (Market or Limit)
                        OrderEntry.Manual,                      // OrderEntry
                        TimeInForce.Day,                        // TimeInForce
                        quantity,                               // Quantity
                        limitPrice,                             // LimitPrice (0 for Market)
                        stopPrice,                              // StopPrice (0 for Market/Limit)
                        ocoGroup,                               // Oco (for MARKET+STOP bracket when breakout crossed)
                        RobotOrderIds.EncodeTag(intentId),       // OrderName
                        DateTime.MinValue,                      // Gtd
                        null                                    // CustomOrder
                    );
                    SetOrderTag(order, RobotOrderIds.EncodeTag(intentId));
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATED_MARKET_OR_LIMIT", new
                    {
                        order_name = RobotOrderIds.EncodeTag(intentId),
                        order_type = orderType.ToString(),
                        quantity = quantity,
                        limit_price = limitPrice,
                        order_action = orderAction.ToString(),
                        instrument = instrument
                    }));
                }
                catch (Exception ex)
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_CREATE_FAIL", new
                    {
                        error = $"Failed to create {orderType} order: {ex.Message}",
                        order_type = orderType.ToString(),
                        order_action = orderAction.ToString(),
                        quantity = quantity,
                        entry_price = ntEntryPrice,
                        instrument = instrument,
                        intent_id = intentId,
                        account = "SIM"
                    }));
                    return OrderSubmissionResult.FailureResult($"Failed to create {orderType} order: {ex.Message}", utcNow);
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
                targetPrice: intentTargetPrice, direction: finalDirection, ocoGroup: ocoGroup);

            orderInfo.OrderId = order.OrderId ?? orderInfo.OrderId;
            _iea?.RegisterOrder(order.OrderId, intentId, instrument, stream, OrderRole.ENTRY, OrderOwnershipStatus.OWNED, "SubmitEntryOrder", orderInfo, utcNow);
            // Execution boundary: SUBMIT_ENTRY must precede ORDER_ACKNOWLEDGED → ENTRY_ACCEPTED (validator: ENTRY_SUBMITTED only).
            _iea?.TryTransitionIntentLifecycle(intentId, IntentLifecycleTransition.SUBMIT_ENTRY, null, acknowledgedAt);

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
    /// Non-IEA ingress: trace + dedup + enqueue only (NinjaTrader order-update fan-out). Drained on strategy thread.
    /// </summary>
    private void HandleOrderIngressFromNt(object orderObj, object orderUpdateObj)
    {
        var order = orderObj as Order;
        dynamic orderUpdate = orderUpdateObj;
        if (order == null) return;

        var (encodedTag, _) = GetOrderTagWithSource(order);
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
        var intentTrace = RobotOrderIds.DecodeIntentId(tagTrace) ?? "";
        var utcTrace = DateTimeOffset.UtcNow;
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
            _log.Write(RobotEvents.EngineBase(utcNow, intentId, order.Instrument?.MasterInstrument?.Name ?? "", "COMPLETED_INTENT_ORDER_UPDATE", new
            {
                error = "Order update received for protective order whose intent is already TradeCompleted",
                intent_id = intentId,
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
        _onMismatchExecutionTrigger?.Invoke(instKey, utcNow, new MismatchExecutionTriggerDetails
        {
            IntentId = intentId,
            FillDelta = 0
        });
        _executionTrace?.WriteExecutionTrace(utcNow, "NotifyExecutionTrigger", "after_notify", instKey, intentId,
            order.OrderId ?? "", "", 0, orderState.ToString());

        // Reentry protective acceptance: when BOTH stop and target for a reentry intent are Working/Accepted,
        // invoke HandleReentryProtectionAccepted (once per intent). Only for reentry protective set, not original entry.
        if ((parsed.Leg == "STOP" || parsed.Leg == "TARGET") &&
            intentId.EndsWith("_REENTRY", StringComparison.OrdinalIgnoreCase) &&
            (orderState == OrderState.Working || orderState == OrderState.Accepted) &&
            !_reentryProtectionAcceptedNotified.Contains(intentId))
        {
            if (((IIEAOrderExecutor)this).HasWorkingProtectivesForIntent(intentId))
            {
                _reentryProtectionAcceptedNotified.Add(intentId);
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
                            foreach (Order o in account.Orders)
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
                return; // Skip rejection handling
            }
            
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
        var accountName = _iea?.AccountName ?? ExecutionUpdateRouter.GetAccountNameFromOrder(record.Order);
        var execInstKey = _iea?.ExecutionInstrumentKey ?? ExecutionUpdateRouter.GetExecutionInstrumentKeyFromOrder((record.Order as Order)?.Instrument);
        OrderInfo? orderInfo = null;

        if (OrderMap.TryGetValue(record.IntentId, out orderInfo))
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, record.IntentId, record.Instrument, "EXECUTION_DEFERRED_RESOLVED",
                new { account_name = accountName, execution_instrument_key = execInstKey, intent_id = record.IntentId, execution_id = record.ExecutionId, broker_order_id = record.BrokerOrderId, elapsed_ms = elapsed, retry_count = record.RetryCount }));
            if (_useInstrumentExecutionAuthority && _iea != null)
                HandleExecutionUpdateReal(record.Execution, record.Order, record, orderInfo);
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
                    HandleExecutionUpdateReal(record.Execution, record.Order, record, orderInfo);
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
        var intentId = record.IntentId;
        var instrument = record.Instrument;
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
            ProcessBrokerFlattenFill(record.Execution, instrument, record.FillPrice, record.FillQuantity, utcNow, order.OrderId, order);
            CheckAllInstrumentsForFlatPositions(utcNow);
            return;
        }

        EmitUnmappedFill(instrument, "UNKNOWN_ORDER_AFTER_GRACE", record.FillPrice, record.FillQuantity, utcNow, order?.OrderId, order, tag: record.EncodedTag);
        LogCriticalWithIeaContext(utcNow, intentId, instrument, "EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL",
            new { error = "Order not found in map after 500ms grace", broker_order_id = order?.OrderId, intent_id = intentId, fill_price = record.FillPrice, fill_quantity = record.FillQuantity, account_name = _iea?.AccountName });
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

    /// <summary>
    /// STEP 3: Handle real NT ExecutionUpdate event.
    /// Called from public HandleExecutionUpdate() method.
    /// </summary>
    private void HandleExecutionUpdateReal(object executionObj, object orderObj, UnresolvedExecutionRecord? retryRecord = null, OrderInfo? retryOrderInfo = null, bool beginAtFillPath = false)
    {
        // Use dynamic for Execution type to avoid namespace conflicts
        dynamic execution = retryRecord != null ? retryRecord.Execution : executionObj;
        var order = (retryRecord != null ? retryRecord.Order : orderObj) as Order;
        if (execution == null || order == null) return;

        if (!beginAtFillPath)
        {
            string? execIdTrace = null;
            try { dynamic dex = execution; execIdTrace = dex.ExecutionId as string; } catch { }
            var fillQtyTrace = 0;
            try { fillQtyTrace = (int)execution.Quantity; } catch { }
            var tagTrace = GetOrderTag(order);
            var intentTrace = RobotOrderIds.DecodeIntentId(tagTrace) ?? "";
            var utcTrace = DateTimeOffset.UtcNow;
            var instTrace = order.Instrument?.MasterInstrument?.Name ?? "";
            _executionTrace?.WriteExecutionTrace(utcTrace, "OnExecutionUpdate", "raw_callback", instTrace, intentTrace,
                order.OrderId ?? "", execIdTrace ?? "", fillQtyTrace, order.OrderState.ToString());

            if (retryRecord == null && retryOrderInfo == null)
            {
                // Non-IEA only: permanent dedup keys can collide when ExecutionId is empty; IEA dedup runs below.
                if (_iea == null)
                {
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
                }
            }

            if (retryRecord != null && retryOrderInfo != null)
            {
                ProcessExecutionUpdateContinuation(retryRecord, retryOrderInfo);
                return;
            }

            // Gap 3 / Step 5: Deduplicate execution callbacks before any state mutation (multi-forwarding from router)
            bool isDuplicate = false;
            if (_useInstrumentExecutionAuthority && _iea != null)
                isDuplicate = _iea.TryMarkAndCheckDuplicate(executionObj, orderObj);
            else
            {
                var dedupKey = BuildNonIeaDedupKey(execution, order);
                isDuplicate = TryMarkAndCheckDuplicateNonIea(dedupKey);
            }
            if (isDuplicate)
            {
                string? execId = null;
                try { dynamic d = execution; execId = d.ExecutionId as string; } catch { }
                _log.Write(RobotEvents.ExecutionBase(DateTimeOffset.UtcNow, "", order.Instrument?.MasterInstrument?.Name ?? "", "EXECUTION_DUPLICATE_DETECTED",
                    new { broker_order_id = order.OrderId, execution_id = execId, note = "Duplicate execution callback skipped (dedup)" }));
                return;
            }
        }

        var fillPathSw = Stopwatch.StartNew();
        string? fillPathIntentId = null;

        try
        {
        var encodedTag = GetOrderTag(order);
        var intentId = RobotOrderIds.DecodeIntentId(encodedTag);
        fillPathIntentId = intentId;
        var utcNow = DateTimeOffset.UtcNow;

        if (_useInstrumentExecutionAuthority && _iea != null &&
            !string.IsNullOrEmpty(encodedTag) && encodedTag.StartsWith("QTSW2:FLATTEN:", StringComparison.OrdinalIgnoreCase))
        {
            var instrumentKey = order.Instrument?.MasterInstrument?.Name ?? _iea.ExecutionInstrumentKey;
            _iea.OnFlattenFillReceived(instrumentKey, order.OrderId, utcNow);
        }
        
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

        // Robot-owned flatten: broker OrderId may not match IEA registry yet; route by tag before OrderMap/registry resolution.
        if (IsRobotOwnedFlattenByTag(encodedTag) && fillQuantity != 0)
        {
            var instrumentFlatten = order.Instrument?.MasterInstrument?.Name ?? _iea?.ExecutionInstrumentKey ?? "UNKNOWN";
            string? execIdRec = null;
            try { dynamic d = execution; execIdRec = d.ExecutionId as string; } catch { }
            _log.Write(RobotEvents.ExecutionBase(utcNow, "FLATTEN", instrumentFlatten, "FLATTEN_EXECUTION_RECOVERED", new
            {
                broker_order_id = order.OrderId,
                execution_id = execIdRec,
                reason = "registry_miss_but_tag_matched"
            }));
            ProcessBrokerFlattenFill(execution, instrumentFlatten, fillPrice, fillQuantity, utcNow, order.OrderId, order);
            CheckAllInstrumentsForFlatPositions(utcNow);
            return;
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
            // this fill is our own flatten order - route through canonical exit path (P0)
            lock (_flattenRecognitionLock)
            {
                if (!string.IsNullOrEmpty(_lastFlattenInstrument) &&
                    ExecutionInstrumentResolver.IsSameInstrument(_lastFlattenInstrument, instrument) &&
                    (utcNow - _lastFlattenUtc).TotalSeconds < FLATTEN_RECOGNITION_WINDOW_SECONDS)
                {
                    ProcessBrokerFlattenFill(execution, instrument, fillPrice, fillQuantity, utcNow, order.OrderId, order);
                    CheckAllInstrumentsForFlatPositions(utcNow);
                    return;
                }
            }
            
            EmitUnmappedFill(instrument, "UNTrackED_TAG", fillPrice, fillQuantity, utcNow, order.OrderId, order, tag: encodedTag);
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

            var brokerOrderIdUntracked = order.OrderId?.ToString() ?? "";
            var untrackedFlattenCorrelationId = $"UNTrackED_FILL:{instrument}:{utcNow:yyyyMMddHHmmssfff}";
            try
            {
                _executionJournal.UpsertUntrackedFillRecoveryJournal(
                    instrument,
                    brokerOrderIdUntracked,
                    fillQuantity,
                    fillPrice,
                    utcNow,
                    _useInstrumentExecutionAuthority && _ntActionQueue != null ? untrackedFlattenCorrelationId : null);
            }
            catch (Exception ex)
            {
                LogCriticalWithIeaContext(utcNow, "", instrument, "UNTRACKED_FILL_RECOVERY_JOURNAL_UPSERT_FAILED",
                    new
                    {
                        error = ex.Message,
                        exception_type = ex.GetType().Name,
                        broker_order_id = brokerOrderIdUntracked,
                        note = "Flatten still proceeds (fail-closed); reconciliation may be impaired until journal writable"
                    });
            }
            
            // CRITICAL: Flatten position immediately (fail-closed)
            // NT THREADING FIX: Worker MUST NOT call account.Flatten. Enqueue for strategy thread.
            if (_useInstrumentExecutionAuthority && _ntActionQueue != null)
            {
                var cid = untrackedFlattenCorrelationId;
                EnqueueNtActionInternal(new NtFlattenInstrumentCommand(cid, null, instrument, "UNTrackED_FILL", utcNow,
                    DestructiveActionSource.FAIL_CLOSED, DestructiveTriggerReason.FAIL_CLOSED));
                LogCriticalWithIeaContext(utcNow, "", instrument, "UNTrackED_FILL_FLATTEN_ENQUEUED",
                    new
                    {
                        broker_order_id = order.OrderId,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        correlation_id = cid,
                        note = "Flatten enqueued for strategy thread (fail-closed)"
                    });
                var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                if (notificationService != null)
                {
                    var title = $"Untracked Fill - Flatten Enqueued - {instrument}";
                    var message = $"Untracked fill occurred (missing/invalid tag). Flatten enqueued for strategy thread. Broker Order ID: {order.OrderId}, Fill Price: {fillPrice}, Quantity: {fillQuantity}";
                    notificationService.EnqueueNotification($"UNTrackED_FILL_ENQUEUED:{order.OrderId}", title, message, priority: 1);
                }
            }
            else
            {
                try
                {
                    var flattenResult = Flatten("UNKNOWN_UNTrackED_FILL", instrument, utcNow);
                    LogCriticalWithIeaContext(utcNow, "", instrument, "UNTrackED_FILL_FLATTENED",
                        new { broker_order_id = order.OrderId, fill_price = fillPrice, fill_quantity = fillQuantity, flatten_success = flattenResult.Success, flatten_error = flattenResult.ErrorMessage });
                    var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                    if (notificationService != null)
                    {
                        var title = flattenResult.Success ? $"Untracked Fill - Position Flattened - {instrument}" : $"CRITICAL: Untracked Fill - Flatten FAILED - {instrument}";
                        var message = flattenResult.Success
                            ? $"Untracked fill occurred and position was flattened. Broker Order ID: {order.OrderId}"
                            : $"Untracked fill occurred but flatten FAILED - MANUAL INTERVENTION REQUIRED. Error: {flattenResult.ErrorMessage}";
                        notificationService.EnqueueNotification($"UNTrackED_FILL:{order.OrderId}", title, message, priority: flattenResult.Success ? 1 : 3);
                    }
                }
                catch (Exception ex)
                {
                    LogCriticalWithIeaContext(utcNow, "", instrument, "UNTrackED_FILL_FLATTEN_FAILED",
                        new { error = ex.Message, broker_order_id = order.OrderId, fill_price = fillPrice, fill_quantity = fillQuantity });
                    var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                    if (notificationService != null)
                        notificationService.EnqueueNotification($"UNTrackED_FILL_FAILED:{order.OrderId}", $"CRITICAL: Untracked Fill - Flatten FAILED - {instrument}", $"Error: {ex.Message}", priority: 3);
                }
            }
            
            // CRITICAL FIX: Check all instruments for flat positions even for untracked fills
            // Manual flatten may have occurred, and we need to cancel entry stops
            CheckAllInstrumentsForFlatPositions(utcNow);
            
            return; // Fail-closed: don't process untracked fill
        }

        // Phase 1 Execution Ownership: Try registry first (broker order id, then alias)
        var parsedTag = RobotOrderIds.ParseTag(encodedTag);
        OrderInfo? orderInfo = null;
        bool orderInfoCreatedFromTag = false;

        if (_useInstrumentExecutionAuthority && _iea != null &&
            _iea.TryResolveForExecutionUpdate(order.OrderId, intentId, parsedTag.Leg, out var regEntry, out var resolutionPath))
        {
            orderInfo = regEntry!.OrderInfo;
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_REGISTRY_EXEC_RESOLVED", new
            {
                broker_order_id = order.OrderId,
                intent_id = intentId,
                resolution_path = resolutionPath,
                order_role = regEntry.OrderRole.ToString(),
                ownership_status = regEntry.OwnershipStatus.ToString()
            }));
        }

        if (orderInfo == null && !OrderMap.TryGetValue(intentId, out orderInfo))
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
        
        // If still no orderInfo, try shared adopted-order registry (cross-instance fill resolution)
        if (orderInfo == null && !string.IsNullOrEmpty(intentId) && !isProtectiveOrder)
        {
            var brokerOrderIdStr = order.OrderId?.ToString();
            if (!string.IsNullOrEmpty(brokerOrderIdStr) && SharedAdoptedOrderRegistry.TryResolve(brokerOrderIdStr, out var adoptedRecord) && adoptedRecord != null)
            {
                var instrumentForResolve = order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
                var execVariant = instrumentForResolve.StartsWith("M", StringComparison.OrdinalIgnoreCase) && instrumentForResolve.Length > 1 ? instrumentForResolve : "M" + instrumentForResolve;
                var canonical = DeriveCanonicalFromExecutionInstrument(execVariant);
                var journalEntry = _executionJournal.TryGetAdoptionCandidateEntry(adoptedRecord.IntentId, instrumentForResolve, canonical);
                if (journalEntry.HasValue && adoptedRecord.IsEntryOrder)
                {
                    var (tradingDate, stream, jEntry) = journalEntry.Value;
                    if (!string.IsNullOrWhiteSpace(tradingDate) && !string.IsNullOrWhiteSpace(stream))
                    {
                        if (!IntentMap.TryGetValue(adoptedRecord.IntentId, out var intent) || intent == null)
                        {
                            intent = CreateIntentFromJournalEntry(tradingDate, stream, adoptedRecord.IntentId, jEntry);
                            if (intent != null)
                                RegisterIntent(intent);
                        }
                        if (intent != null)
                        {
                        orderInfo = new OrderInfo
                        {
                            IntentId = adoptedRecord.IntentId,
                            Instrument = adoptedRecord.Instrument,
                            OrderId = brokerOrderIdStr,
                            OrderType = "ENTRY",
                            State = "FILLED",
                            IsEntryOrder = true,
                            Quantity = order.Quantity,
                            FilledQuantity = fillQuantity,
                            Price = (decimal?)order.StopPrice ?? (decimal?)order.LimitPrice
                        };
                        _log.Write(RobotEvents.ExecutionBase(utcNow, adoptedRecord.IntentId, instrumentForResolve, "ADOPTED_ORDER_FILL_RESOLVED_CROSS_INSTANCE", new
                        {
                            broker_order_id = brokerOrderIdStr,
                            intent_id = adoptedRecord.IntentId,
                            receiving_instance_id = _iea?.InstanceId,
                            note = "Resolved via SharedAdoptedOrderRegistry for journaling"
                        }));
                        decimal contractMultiplier = 100;
                        try
                        {
                            if (order?.Instrument?.MasterInstrument != null)
                                contractMultiplier = (decimal)order.Instrument.MasterInstrument.PointValue;
                        }
                        catch { /* fallback 100 */ }
                        var context = new IntentContext
                        {
                            IntentId = adoptedRecord.IntentId,
                            TradingDate = tradingDate,
                            Stream = stream,
                            Direction = intent.Direction ?? "Long",
                            ExecutionInstrument = intent.Instrument ?? instrumentForResolve,
                            CanonicalInstrument = canonical,
                            ContractMultiplier = contractMultiplier,
                            Tag = encodedTag
                        };
                        _executionJournal.RecordEntryFill(
                            context.IntentId,
                            context.TradingDate,
                            context.Stream,
                            fillPrice,
                            fillQuantity,
                            utcNow,
                            context.ContractMultiplier,
                            context.Direction,
                            context.ExecutionInstrument,
                            context.CanonicalInstrument,
                            brokerOrderInstrumentKey: instrumentForResolve);
                        _log.Write(RobotEvents.ExecutionBase(utcNow, adoptedRecord.IntentId, instrumentForResolve, "ADOPTED_ORDER_FILL_JOURNALED", new
                        {
                            broker_order_id = brokerOrderIdStr,
                            intent_id = adoptedRecord.IntentId,
                            fill_price = fillPrice,
                            fill_quantity = fillQuantity,
                            trading_date = tradingDate,
                            stream = stream
                        }));
                        _coordinator?.OnEntryFill(adoptedRecord.IntentId, fillQuantity, stream, context.CanonicalInstrument ?? context.ExecutionInstrument, context.Direction ?? "", utcNow);
                        _iea?.HandleEntryFill(adoptedRecord.IntentId, intent, fillPrice, fillQuantity, fillQuantity, utcNow);
                        return;
                        }
                    }
                }
                _log.Write(RobotEvents.ExecutionBase(utcNow, adoptedRecord.IntentId ?? "", instrumentForResolve, "ADOPTED_ORDER_FILL_UNRESOLVED", new
                {
                    broker_order_id = brokerOrderIdStr,
                    intent_id = adoptedRecord.IntentId,
                    reason = journalEntry.HasValue ? "context_incomplete" : "no_journal_entry",
                    note = "SharedAdoptedOrderRegistry had record but could not journal"
                }));
            }
        }

        // If still no orderInfo, handle as untracked
        if (orderInfo == null)
        {
            if (IsRobotOwnedFlattenByTag(encodedTag))
                return;

            var instrument = order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
            var orderState = order.OrderState;

            // Phase 2: Emit EXECUTION_UNOWNED when registry resolve failed (ownership-aware path)
            if (_useInstrumentExecutionAuthority && _iea != null)
            {
                _iea.IncrementUnownedDetected();
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instrument, "EXECUTION_UNOWNED", new
                {
                    broker_order_id = order.OrderId,
                    intent_id = intentId,
                    instrument,
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    note = "Execution update for order not in registry - fail-closed recovery",
                    iea_instance_id = _iea.InstanceId
                }));
            }

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
                {
                    return;
                }
            }

            // Phase 1: Non-blocking deferred retry (no Thread.Sleep). Enqueue for retry; max retries with 50-100ms interval.
            string? execId = null;
            try { dynamic d = execution; execId = d.ExecutionId as string; } catch { }
            var brokerOrderId = order.OrderId?.ToString();
            var record = new UnresolvedExecutionRecord(execution, order, intentId, instrument, encodedTag,
                fillPrice, fillQuantity, orderState.ToString(), isProtectiveOrder, orderTypeFromTag, utcNow, execId, brokerOrderId);
            var accountName = _iea?.AccountName ?? ExecutionUpdateRouter.GetAccountNameFromOrder(order);
            var execInstKey = _iea?.ExecutionInstrumentKey ?? ExecutionUpdateRouter.GetExecutionInstrumentKeyFromOrder(order.Instrument);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_DEFERRED_FOR_REGISTRY_RESOLUTION",
                new { account_name = accountName, execution_instrument_key = execInstKey, intent_id = intentId, execution_id = execId, broker_order_id = order.OrderId, elapsed_ms = 0, retry_count = 0 }));
            _iea?.IncrementDeferredExecutionCount();
            DeferUnresolvedExecution(record);
            return;
        }

        // Build record for continuation (normal path)
        string? execIdNorm = null;
        try { dynamic d = execution; execIdNorm = d.ExecutionId as string; } catch { }
        var instrumentNorm = order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
        var recordNorm = new UnresolvedExecutionRecord(execution, order, intentId, instrumentNorm, encodedTag,
            fillPrice, fillQuantity, order.OrderState.ToString(), isProtectiveOrder, orderTypeFromTag, utcNow, execIdNorm);
        ProcessExecutionUpdateContinuation(recordNorm, orderInfo);
        }
        finally
        {
            var elapsed = fillPathSw.ElapsedMilliseconds;
            if (elapsed >= 100)
            {
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "FILL_PATH_SLOW", "ENGINE",
                    new { total_ms = elapsed, intent_id = fillPathIntentId, note = "Correlate with disconnects; disconnects preceded by heavy fill-path bursts?" }));
            }
        }
    }

    private void ProcessExecutionUpdateContinuation(UnresolvedExecutionRecord record, OrderInfo orderInfo)
    {
        var intentId = record.IntentId;
        var encodedTag = record.EncodedTag;
        var fillPrice = record.FillPrice;
        var fillQuantity = record.FillQuantity;
        var isProtectiveOrder = record.IsProtectiveOrder;
        var orderTypeFromTag = record.OrderTypeFromTag;
        var order = record.Order as Order;
        var execution = record.Execution;
        var utcNow = DateTimeOffset.UtcNow;
        var execInstKey = _iea?.ExecutionInstrumentKey ?? (order != null ? ExecutionUpdateRouter.GetExecutionInstrumentKeyFromOrder(order.Instrument) : "");
        var accountName = _iea?.AccountName ?? (order != null ? ExecutionUpdateRouter.GetAccountNameFromOrder(order) : "");
        var brokerOrderId = order?.OrderId?.ToString() ?? "";
        var orderIdInternal = brokerOrderId; // NT: use OrderId as internal correlation key
        var executionSeq = GetNextExecutionSequence(execInstKey, accountName);
        string? brokerExecId = null;
        try { dynamic d = execution; brokerExecId = d.ExecutionId as string; } catch { }
        var fillGroupId = ComputeFillGroupId(brokerExecId, orderIdInternal, brokerOrderId, utcNow.ToString("o"), fillPrice, fillQuantity);

        var instFill = orderInfo.Instrument.Trim();
        var ordStateFill = order?.OrderState.ToString() ?? "";
        _executionTrace?.WriteExecutionTrace(utcNow, "Fill", "raw_callback", instFill, intentId, brokerOrderId,
            brokerExecId ?? "", fillQuantity, ordStateFill);
        _executionTrace?.WriteExecutionTrace(utcNow, "NotifyExecutionTrigger", "before_notify", instFill, intentId,
            brokerOrderId, brokerExecId ?? "", fillQuantity, ordStateFill);
        _onMismatchExecutionTrigger?.Invoke(instFill, utcNow, new MismatchExecutionTriggerDetails
        {
            IntentId = intentId,
            FillDelta = fillQuantity
        });
        _executionTrace?.WriteExecutionTrace(utcNow, "NotifyExecutionTrigger", "after_notify", instFill, intentId,
            brokerOrderId, brokerExecId ?? "", fillQuantity, ordStateFill);

        var orderTypeForContext = orderTypeFromTag ?? orderInfo.OrderType;

        // Robot flatten orders: tag decodes to pseudo intentId "FLATTEN" — skip IntentMap and journal via coordinator path.
        if (IsRobotOwnedFlattenOrder(encodedTag, orderInfo) && order != null)
        {
            var flattenInst = order.Instrument?.MasterInstrument?.Name ?? orderInfo.Instrument?.Trim() ?? "UNKNOWN";
            ProcessBrokerFlattenFill(execution, flattenInst, fillPrice, fillQuantity, utcNow, order.OrderId, order);
            return;
        }

        // CRITICAL: Resolve intent context before any journal call
        IntentContext context;
        if (!ResolveIntentContextOrFailClosed(intentId, encodedTag, orderTypeForContext, orderInfo.Instrument,
            fillPrice, fillQuantity, utcNow, out context, order))
        {
            // Context resolution failed - orphan fill logged and execution blocked
            // Do NOT call journal with empty strings
            return; // Fail-closed
        }

        // UNIFY FILL EVENTS: trading_date must be non-null for PnL/accounting integrity
        if (string.IsNullOrWhiteSpace(context.TradingDate))
        {
            EmitUnmappedFill(orderInfo.Instrument, "TRADING_DATE_NULL", fillPrice, fillQuantity, utcNow, order?.OrderId, order, tag: encodedTag);
            LogCriticalWithIeaContext(utcNow, intentId, orderInfo.Instrument, "EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL",
                new
                {
                    error = "trading_date cannot be null on fill events - PnL integrity broken",
                    intent_id = intentId,
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    order_type = orderTypeForContext,
                    action = "BLOCKED",
                    note = "Fail-closed: emit ERROR and block trading until trading_date is set"
                });
            return; // Fail-closed
        }

        // Explicit Entry vs Exit Classification before entry cumulative accounting.
        // Protective orders (STOP/TARGET) are exit orders even if orderInfo.IsEntryOrder is true (from entry order in OrderMap)
        bool isEntryFill = !isProtectiveOrder && orderInfo.IsEntryOrder == true;
        if (isEntryFill)
        {
            // ENTRY only: track cumulative fills on the entry order row; INTENT_FILL_UPDATE and overfill guard are entry-only.
            orderInfo.FilledQuantity += fillQuantity;
            var filledTotal = orderInfo.FilledQuantity;

            var expectedQty = orderInfo.ExpectedQuantity > 0 ? orderInfo.ExpectedQuantity :
                (IntentPolicy.TryGetValue(intentId, out var exp) ? exp.ExpectedQuantity : 0);
            var maxQty = orderInfo.MaxQuantity > 0 ? orderInfo.MaxQuantity :
                (IntentPolicy.TryGetValue(intentId, out var exp2) ? exp2.MaxQuantity : 0);
            var remainingQty = expectedQty - filledTotal;
            var overfill = filledTotal > expectedQty;

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
                TriggerQuantityEmergency(intentId, "INTENT_OVERFILL_EMERGENCY", utcNow, new Dictionary<string, object>
                {
                    { "expected_qty", expectedQty },
                    { "actual_filled_qty", filledTotal },
                    { "last_fill_qty", fillQuantity },
                    { "reason", "Fill exceeded expected quantity" }
                });
            }

            // Entry fill - handle aggregated orders (multiple streams, one broker order)
            var intentIdsToUpdate = orderInfo.AggregatedIntentIds ?? new List<string> { context.IntentId };
            var isAggregated = intentIdsToUpdate.Count > 1;

            // Deterministic partial-fill allocation: allocate in lexicographic order, first fill goes to intentIds[0]
            // until its policy qty satisfied, then next. Track cumulative per intent.
            // Phase 2: When IEA enabled, IEA owns allocation; otherwise adapter does it.
            var allocations = (_useInstrumentExecutionAuthority && _iea != null)
                ? _iea.AllocateFillToIntents(intentIdsToUpdate, fillQuantity, orderInfo)
                : AllocateFillToIntents(intentIdsToUpdate, fillQuantity, orderInfo);

            var entryFillJournalRecordedQty = 0;
            foreach (var alloc in allocations)
            {
                var allocIntentId = alloc.Item1;
                var allocQty = alloc.Item2;
                if (allocQty <= 0) continue;
                if (!IntentMap.TryGetValue(allocIntentId, out Intent? allocIntent) || allocIntent == null)
                {
                    var posHint = 0;
                    try { posHint = Math.Abs(GetCurrentPositionReal(orderInfo.Instrument.Trim())); } catch { /* best-effort */ }
                    _log.Write(RobotEvents.ExecutionBase(utcNow, allocIntentId, orderInfo.Instrument,
                        "TAGGED_ENTRY_FILL_JOURNAL_SKIPPED", new
                        {
                            reason = "INTENTMAP_MISS",
                            intent_id = allocIntentId,
                            fill_qty = allocQty,
                            trading_date = (string?)null,
                            broker_position_abs_hint = posHint,
                            iea_active = _iea != null,
                            order_broker_id = brokerOrderId
                        }));
                    continue;
                }
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
                    allocContext.CanonicalInstrument,
                    brokerOrderInstrumentKey: orderInfo.Instrument);
                entryFillJournalRecordedQty += allocQty;
                var exposureInstrument = allocContext.CanonicalInstrument ?? allocIntent.Instrument ?? allocContext.ExecutionInstrument;
                _coordinator?.OnEntryFill(allocContext.IntentId, allocQty, allocContext.Stream, exposureInstrument, allocContext.Direction ?? "", utcNow);
            }

            if (isEntryFill && fillQuantity > 0 && entryFillJournalRecordedQty == 0 && allocations.Count > 0)
            {
                var posAbs = 0;
                try { posAbs = Math.Abs(GetCurrentPositionReal(orderInfo.Instrument.Trim())); } catch { }
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument,
                    "TAGGED_ENTRY_FILL_JOURNAL_RECORDED_ZERO", new
                    {
                        reason = "ALL_ALLOCATION_PATHS_SKIPPED_OR_EMPTY",
                        intent_id = intentId,
                        fill_qty = fillQuantity,
                        allocation_count = allocations.Count,
                        broker_position_abs_hint = posAbs,
                        iea_active = _iea != null,
                        note = "Downstream TAGGED_BROKER_WITHOUT_JOURNAL repair may run from reconciliation / protective"
                    }));
                if (posAbs > 0 &&
                    SumOpenJournalForInstrument(orderInfo.Instrument.Trim(), _iea?.ExecutionInstrumentKey ?? orderInfo.Instrument.Trim()) == 0)
                {
                    _ = TryRepairTaggedBrokerWithoutJournalCore(orderInfo.Instrument.Trim(), posAbs, 0, utcNow, out _, out _);
                }
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
                        broker_order_id = order?.OrderId,
                        note = "Deterministic allocation in lexicographic order for partial fills"
                    }));
            }
            
            // P1: Emit one EXECUTION_FILLED/PARTIAL per intent when aggregated; single when not
            var isPartial = filledTotal < orderInfo.Quantity;
            var eventType = isPartial ? "EXECUTION_PARTIAL_FILL" : "EXECUTION_FILLED";
            if (!isPartial)
            {
                orderInfo.State = "FILLED";
                var legLc = LegTagFromOrderTypeForIeResolution(orderTypeFromTag);
                var incomingOid = order?.OrderId?.ToString() ?? "";
                var oidLc = ResolveCanonicalBrokerOrderIdForIeLifecycle(
                    string.IsNullOrEmpty(incomingOid) ? (orderInfo.OrderId ?? "") : incomingOid, intentId, legLc);
                _iea?.UpdateOrderLifecycle(oidLc, OrderLifecycleState.FILLED, utcNow);
                if (_iea != null && _log != null && !string.IsNullOrEmpty(incomingOid) &&
                    !string.Equals(incomingOid, oidLc, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument,
                        "ORDER_REGISTRY_TERMINAL_LIFECYCLE_RESOLVED_BY_ALIAS", new
                        {
                            incoming_broker_order_id = incomingOid,
                            canonical_broker_order_id = oidLc,
                            lifecycle_target = OrderLifecycleState.FILLED.ToString(),
                            intent_id = intentId,
                            instrument = orderInfo.Instrument,
                            iea_instance_id = _iea.InstanceId
                        }));
                }
            }

            // Intent lifecycle: entry fill transitions
            var entryTransition = isPartial ? IntentLifecycleTransition.ENTRY_PARTIALLY_FILLED : IntentLifecycleTransition.ENTRY_FILLED;

            void EmitEntryFill(string allocIntentId, int allocFillQty, int allocFilledTotal, string allocStream, string allocTradingDate, string allocDirection)
            {
                _iea?.TryTransitionIntentLifecycle(allocIntentId, entryTransition, null, utcNow);
                var side = allocDirection.Equals("Long", StringComparison.OrdinalIgnoreCase) ? "BUY" : "SELL";
                var sessionClass = DeriveSessionClass(allocStream);
                var allocRemaining = isPartial ? orderInfo.Quantity - filledTotal : 0;
                _log.Write(RobotEvents.ExecutionBase(utcNow, allocIntentId, orderInfo.Instrument, eventType,
                    new
                    {
                        execution_sequence = executionSeq,
                        fill_group_id = fillGroupId,
                        order_id = orderIdInternal,
                        broker_order_id = brokerOrderId,
                        intent_id = allocIntentId,
                        instrument = orderInfo.Instrument,
                        execution_instrument_key = execInstKey,
                        side = side,
                        order_type = orderInfo.OrderType,
                        position_effect = "OPEN",
                        fill_price = fillPrice,
                        fill_quantity = allocFillQty,
                        filled_total = allocFilledTotal,
                        remaining_qty = allocRemaining,
                        order_quantity = orderInfo.Quantity,
                        stream = allocStream,
                        stream_key = allocStream,
                        trading_date = allocTradingDate,
                        account = accountName,
                        session_class = sessionClass,
                        source = "robot",
                        mapped = true
                    }));
            }

            if (isAggregated && allocations.Count > 0)
            {
                foreach (var alloc in allocations)
                {
                    var allocIntentId = alloc.Item1;
                    var allocQty = alloc.Item2;
                    if (allocQty <= 0) continue;
                    if (!IntentMap.TryGetValue(allocIntentId, out var allocIntent) || allocIntent == null) continue;
                    var cumulativeForIntent = (orderInfo.AggregatedFilledByIntent != null && orderInfo.AggregatedFilledByIntent.TryGetValue(allocIntentId, out var cum))
                        ? cum : allocQty;
                    EmitEntryFill(allocIntentId, allocQty, cumulativeForIntent, allocIntent.Stream ?? context.Stream, allocIntent.TradingDate ?? context.TradingDate ?? "", allocIntent.Direction ?? context.Direction ?? "");
                }
            }
            else
            {
                EmitEntryFill(intentId, fillQuantity, filledTotal, context.Stream ?? "", context.TradingDate ?? "", context.Direction ?? "");
            }
            
            // Register entry fill with coordinator and HandleEntryFill for protective orders
            // CRITICAL FIX: For aggregated orders (CL1+CL2 same price), call HandleEntryFill for EACH intent
            // with that intent's allocated cumulative. Passing filledTotal (order total) to primary only caused
            // CanSubmitExit(primaryIntentId, 4) to fail WOULD_OVER_CLOSE when exposure had only 2 - protectives
            // never submitted, BE had no stop to modify, BE_STOP_VISIBILITY_TIMEOUT → flatten.
            if (isAggregated && allocations.Count > 0)
            {
                CheckAndCancelEntryStopsOnPositionFlat(orderInfo.Instrument, utcNow);
                foreach (var alloc in allocations)
                {
                    var allocIntentId = alloc.Item1;
                    var allocQty = alloc.Item2;
                    if (allocQty <= 0) continue;
                    if (!IntentMap.TryGetValue(allocIntentId, out var allocIntent) || allocIntent == null) continue;
                    var cumulativeForIntent = (orderInfo.AggregatedFilledByIntent != null && orderInfo.AggregatedFilledByIntent.TryGetValue(allocIntentId, out var cum))
                        ? cum
                        : allocQty;
                    if (allocIntentId.EndsWith("_REENTRY", StringComparison.OrdinalIgnoreCase))
                    {
                        _onReentryFillCallback?.Invoke(allocIntentId, utcNow);
                    }
                    else if (_useInstrumentExecutionAuthority && _iea != null)
                        _iea.HandleEntryFill(allocIntentId, allocIntent, fillPrice, allocQty, cumulativeForIntent, utcNow);
                    else
                        HandleEntryFill(allocIntentId, allocIntent, fillPrice, allocQty, cumulativeForIntent, utcNow);
                }
            }
            else if (IntentMap.TryGetValue(intentId, out var entryIntent))
            {
                // Non-aggregated: coordinator OnEntryFill is already invoked once per allocation in the loop above (canonical instrument).

                CheckAndCancelEntryStopsOnPositionFlat(orderInfo.Instrument, utcNow);

                if (intentId.EndsWith("_REENTRY", StringComparison.OrdinalIgnoreCase))
                {
                    _onReentryFillCallback?.Invoke(intentId, utcNow);
                }
                else if (_useInstrumentExecutionAuthority && _iea != null)
                    _iea.HandleEntryFill(intentId, entryIntent, fillPrice, fillQuantity, filledTotal, utcNow);
                else
                    HandleEntryFill(intentId, entryIntent, fillPrice, fillQuantity, filledTotal, utcNow);
            }
            else
            {
                // This should not happen - ResolveIntentContextOrFailClosed already checked
                // But handle defensively - emit unmapped fill before flatten
                EmitUnmappedFill(orderInfo.Instrument, "INTENT_NOT_FOUND", fillPrice, fillQuantity, utcNow, order?.OrderId, order, tag: encodedTag);
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
                // NT THREADING FIX: Worker MUST NOT call account.Flatten. Enqueue for strategy thread.
                if (_useInstrumentExecutionAuthority && _ntActionQueue != null)
                {
                    var cid = $"INTENT_LOST:{intentId}:{utcNow:yyyyMMddHHmmssfff}";
                    EnqueueNtActionInternal(new NtFlattenInstrumentCommand(cid, intentId, orderInfo.Instrument, "INTENT_NOT_FOUND_AFTER_CONTEXT", utcNow,
                        DestructiveActionSource.FAIL_CLOSED, DestructiveTriggerReason.FAIL_CLOSED));
                    LogCriticalEngineWithIeaContext(utcNow, context.TradingDate, "INTENT_NOT_FOUND_FLATTEN_ENQUEUED", "ENGINE",
                        new { intent_id = intentId, instrument = orderInfo.Instrument, stream = context.Stream, correlation_id = cid });
                    _standDownStreamCallback?.Invoke(context.Stream, utcNow, $"INTENT_NOT_FOUND:{intentId}");
                    var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                    if (notificationService != null)
                        notificationService.EnqueueNotification($"INTENT_NOT_FOUND:{intentId}", $"CRITICAL: Intent Not Found - {orderInfo.Instrument}", $"Intent lost after context resolution. Flatten enqueued. Stream: {context.Stream}", priority: 2);
                }
                else
                {
                try
                {
                    var flattenResult = Flatten(intentId, orderInfo.Instrument, utcNow);
                    LogCriticalEngineWithIeaContext(utcNow, context.TradingDate, "INTENT_NOT_FOUND_FLATTENED", "ENGINE",
                        new { intent_id = intentId, instrument = orderInfo.Instrument, stream = context.Stream, flatten_success = flattenResult.Success, flatten_error = flattenResult.ErrorMessage });
                    _standDownStreamCallback?.Invoke(context.Stream, utcNow, $"INTENT_NOT_FOUND:{intentId}");
                    var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                    if (notificationService != null)
                        notificationService.EnqueueNotification($"INTENT_NOT_FOUND:{intentId}", $"CRITICAL: Intent Not Found - {orderInfo.Instrument}", $"Position flattened. Stream: {context.Stream}, Intent: {intentId}", priority: 2);
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
        }
        else if (orderTypeForContext == "STOP" || orderTypeForContext == "TARGET")
        {
            // STOP/TARGET: do not mutate orderInfo.FilledQuantity (entry cumulative is only updated on the entry path).
            var exitFilledTotalForLog = orderInfo.FilledQuantity + fillQuantity;

            // LATE-FILL PROTECTION: If intent already completed, this is a stale/late fill (race with sibling cancel).
            var tradingDate = context.TradingDate ?? "";
            var stream = context.Stream ?? "";
            if (_executionJournal.IsIntentCompleted(intentId, tradingDate, stream))
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "COMPLETED_INTENT_RECEIVED_FILL", "ENGINE",
                    new
                    {
                        error = "Fill received for intent already TradeCompleted - stale/late fill on terminal intent",
                        intent_id = intentId,
                        broker_order_id = order.OrderId,
                        instrument = orderInfo.Instrument,
                        stream = stream,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        side = orderInfo.Direction,
                        exit_order_type = orderTypeForContext,
                        execution_timestamp_utc = utcNow.ToString("o"),
                        mapped = true,
                        action = "ANOMALY_LOGGED",
                        note = "CRITICAL: Late fill on completed intent - route to reconciliation. Do not process as normal exit."
                    }));
                var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
                if (notificationService != null)
                {
                    notificationService.EnqueueNotification($"COMPLETED_INTENT_RECEIVED_FILL:{intentId}",
                        "CRITICAL: Late Fill on Completed Intent",
                        $"Fill received for intent {intentId} already TradeCompleted. Broker Order: {order.OrderId}, Qty: {fillQuantity}, Price: {fillPrice}. Route to reconciliation.",
                        priority: 2);
                }
                return; // Fail-closed: do not process as normal fill
            }

            PruneIntentState(intentId, "exit_fill");
            // Intent exit: purge pending BE (target/stop fill)
            PurgePendingBEForIntent(intentId, utcNow, orderInfo.Instrument, "exit_fill");
            var exitLeg = orderTypeForContext == "STOP" ? "STOP" : orderTypeForContext == "TARGET" ? "TARGET" : null;
            var exitIncoming = order?.OrderId?.ToString() ?? "";
            var exitOid = ResolveCanonicalBrokerOrderIdForIeLifecycle(
                string.IsNullOrEmpty(exitIncoming) ? (orderInfo.OrderId ?? "") : exitIncoming, intentId, exitLeg);
            _iea?.UpdateOrderLifecycle(exitOid, OrderLifecycleState.FILLED, utcNow);
            if (_iea != null && _log != null && !string.IsNullOrEmpty(exitIncoming) &&
                !string.Equals(exitIncoming, exitOid, StringComparison.OrdinalIgnoreCase))
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument,
                    "ORDER_REGISTRY_TERMINAL_LIFECYCLE_RESOLVED_BY_ALIAS", new
                    {
                        incoming_broker_order_id = exitIncoming,
                        canonical_broker_order_id = exitOid,
                        lifecycle_target = OrderLifecycleState.FILLED.ToString(),
                        intent_id = intentId,
                        instrument = orderInfo.Instrument,
                        iea_instance_id = _iea.InstanceId
                    }));
            }
            _iea?.TryTransitionIntentLifecycle(intentId, IntentLifecycleTransition.INTENT_COMPLETED, null, utcNow);
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
            
            // Log exit fill event - UNIFY FILL EVENTS: Use EXECUTION_FILLED (canonical) with order_type for ledger/PnL
            // P1: Enriched payload with execution_instrument_key, side, account, session_class
            // Canonical: execution_sequence, fill_group_id, order_id, broker_order_id, position_effect, mapped
            var exitSide = (context.Direction ?? "").Equals("Long", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY";
            var exitSessionClass = DeriveSessionClass(context.Stream ?? "");
            var exitBrokerOrderId = order?.OrderId?.ToString() ?? "";
            var exitOrderIdInternal = exitBrokerOrderId;
            var exitExecutionSeq = GetNextExecutionSequence(execInstKey, accountName);
            string? exitBrokerExecId = null;
            try { dynamic d = execution; exitBrokerExecId = d.ExecutionId as string; } catch { }
            var exitFillGroupId = ComputeFillGroupId(exitBrokerExecId, exitOrderIdInternal, exitBrokerOrderId, utcNow.ToString("o"), fillPrice, fillQuantity);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_FILLED",
                new
                {
                    execution_sequence = exitExecutionSeq,
                    fill_group_id = exitFillGroupId,
                    order_id = exitOrderIdInternal,
                    broker_order_id = exitBrokerOrderId,
                    intent_id = intentId,
                    instrument = orderInfo.Instrument,
                    execution_instrument_key = execInstKey,
                    side = exitSide,
                    order_type = orderTypeForContext, // STOP or TARGET (tag-based ground truth)
                    position_effect = "CLOSE",
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    filled_total = exitFilledTotalForLog,
                    remaining_qty = 0,
                    stream = context.Stream,
                    stream_key = context.Stream,
                    trading_date = context.TradingDate ?? "",
                    account = accountName,
                    session_class = exitSessionClass,
                    source = "robot",
                    mapped = true
                }));
            
            // CRITICAL FIX: Coordinator accumulates internally, so pass fillQuantity (delta) not exit cumulative
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
                
                // TERMINALIZATION: Cancel remaining protective orders and verify invariant.
                TerminalizeIntent(intentId, filledIntent.TradingDate ?? "", filledIntent.Stream ?? "", orderTypeForContext, utcNow);
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
    /// P0: Process broker flatten fill through canonical exit path.
    /// Emits EXECUTION_FILLED per intent, RecordExitFill, OnExitFill.
    /// If no intents can be mapped, emits EXECUTION_FILL_UNMAPPED (CRITICAL).
    /// </summary>
    private void ProcessBrokerFlattenFill(object execution, string instrument, decimal fillPrice, int fillQuantity, DateTimeOffset utcNow, object orderId, Order order)
    {
        var accountName = _iea?.AccountName ?? ExecutionUpdateRouter.GetAccountNameFromOrder(order);
        var execInstKey = _iea?.ExecutionInstrumentKey ?? ExecutionUpdateRouter.GetExecutionInstrumentKeyFromOrder(order.Instrument);
        var brokerOrderId = orderId?.ToString() ?? "";
        var orderIdInternal = brokerOrderId;
        var executionSeq = GetNextExecutionSequence(execInstKey, accountName);
        string? brokerExecId = null;
        try { dynamic d = execution; brokerExecId = d.ExecutionId as string; } catch { }
        var fillGroupId = ComputeFillGroupId(brokerExecId, orderIdInternal, brokerOrderId, utcNow.ToString("o"), fillPrice, fillQuantity);

        // Try instrument and execInstKey - exposure.Instrument may be MES while order reports ES
        var exposures = _coordinator?.GetActiveExposuresForInstrument(instrument) ?? new List<IntentExposure>();
        if (exposures.Count == 0 && !string.IsNullOrEmpty(execInstKey))
            exposures = _coordinator?.GetActiveExposuresForInstrument(execInstKey) ?? new List<IntentExposure>();
        var matchingExposures = exposures.Where(e => ExecutionInstrumentResolver.IsSameInstrument(e.Instrument, instrument)).ToList();

        if (matchingExposures.Count == 0)
        {
            EmitUnmappedFill(instrument, "NO_ACTIVE_EXPOSURES", fillPrice, fillQuantity, utcNow, orderId, order);
            LogCriticalWithIeaContext(utcNow, "", instrument, "EXECUTION_FILL_UNMAPPED",
                new
                {
                    error = "Broker flatten fill cannot be mapped to any intent",
                    broker_order_id = brokerOrderId,
                    instrument = instrument,
                    account = accountName,
                    execution_instrument_key = execInstKey,
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    timestamp_utc = utcNow.ToString("o"),
                    note = "No active exposures for instrument - PnL gap"
                });
            return;
        }

        var totalRemaining = matchingExposures.Sum(e => e.RemainingExposure);
        if (totalRemaining <= 0)
        {
            EmitUnmappedFill(instrument, "ZERO_REMAINING_EXPOSURE", fillPrice, fillQuantity, utcNow, orderId, order);
            LogCriticalWithIeaContext(utcNow, "", instrument, "EXECUTION_FILL_UNMAPPED",
                new
                {
                    error = "Broker flatten fill but exposures have zero remaining",
                    broker_order_id = brokerOrderId,
                    instrument = instrument,
                    account = accountName,
                    execution_instrument_key = execInstKey,
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    note = "Coordinator state inconsistent"
                });
            return;
        }

        var allocated = fillQuantity >= totalRemaining
            ? matchingExposures.Select(e => (e.IntentId, e.RemainingExposure)).ToList()
            : AllocateFlattenFillToExposures(matchingExposures, fillQuantity);

        foreach (var (intentId, allocQty) in allocated)
        {
            if (allocQty <= 0) continue;
            if (!IntentMap.TryGetValue(intentId, out var intent))
            {
                EmitUnmappedFill(instrument, "INTENT_NOT_FOUND", fillPrice, allocQty, utcNow, orderId, order);
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_FILL_UNMAPPED",
                    new { broker_order_id = brokerOrderId, intent_id = intentId, alloc_qty = allocQty, error = "Intent not in IntentMap" }));
                continue;
            }

            var tradingDate = intent.TradingDate ?? "";
            if (string.IsNullOrWhiteSpace(tradingDate))
            {
                EmitUnmappedFill(instrument, "TRADING_DATE_NULL", fillPrice, allocQty, utcNow, orderId, order);
                LogCriticalWithIeaContext(utcNow, intentId, instrument, "EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL",
                    new { intent_id = intentId, fill_price = fillPrice, fill_quantity = allocQty, order_type = "FLATTEN", note = "Broker flatten: trading_date null" });
                continue;
            }

            var stream = intent.Stream ?? "";
            var direction = intent.Direction ?? "";
            var side = direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY";
            var sessionClass = DeriveSessionClass(stream);

            _executionJournal.RecordExitFill(intentId, tradingDate, stream, fillPrice, allocQty, "FLATTEN", utcNow);
            _iea?.TryTransitionIntentLifecycle(intentId, IntentLifecycleTransition.INTENT_COMPLETED, null, utcNow);
            _coordinator?.OnExitFill(intentId, allocQty, utcNow);

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_FILLED",
                new
                {
                    execution_sequence = executionSeq,
                    fill_group_id = fillGroupId,
                    order_id = orderIdInternal,
                    broker_order_id = brokerOrderId,
                    intent_id = intentId,
                    instrument = instrument,
                    execution_instrument_key = execInstKey,
                    side = side,
                    order_type = "FLATTEN",
                    position_effect = "CLOSE",
                    fill_price = fillPrice,
                    fill_quantity = allocQty,
                    filled_total = allocQty,
                    remaining_qty = 0,
                    timestamp_utc = utcNow.ToString("o"),
                    trading_date = tradingDate,
                    account = accountName,
                    stream_key = stream,
                    session_class = sessionClass,
                    source = "robot",
                    mapped = true
                }));
        }
    }

    private static List<(string IntentId, int Qty)> AllocateFlattenFillToExposures(List<IntentExposure> exposures, int fillQuantity)
    {
        var total = exposures.Sum(e => e.RemainingExposure);
        if (total <= 0) return new List<(string, int)>();
        var result = new List<(string, int)>();
        var remaining = fillQuantity;
        foreach (var e in exposures.OrderBy(x => x.IntentId))
        {
            if (remaining <= 0) break;
            var pct = (double)e.RemainingExposure / total;
            var alloc = (int)Math.Round(fillQuantity * pct);
            if (alloc > remaining) alloc = remaining;
            if (alloc > e.RemainingExposure) alloc = e.RemainingExposure;
            if (alloc > 0)
            {
                result.Add((e.IntentId, alloc));
                remaining -= alloc;
            }
        }
        if (remaining > 0 && result.Count > 0)
        {
            var last = result[result.Count - 1];
            result[result.Count - 1] = (last.Item1, last.Item2 + remaining);
        }
        return result;
    }

    private static string DeriveSessionClass(string stream)
    {
        if (string.IsNullOrEmpty(stream)) return "";
        var last = stream[stream.Length - 1];
        return char.IsDigit(last) ? "S" + last : "";
    }

    /// <summary>
    /// Canonical unmapped fill: emit EXECUTION_FILLED(mapped=false) before fail-closed.
    /// Removes accounting hole; unmapped fills become actionable incidents.
    /// unmappedReason: NO_ACTIVE_EXPOSURES, ZERO_REMAINING_EXPOSURE, UNTrackED_TAG, UNKNOWN_ORDER_AFTER_GRACE, INTENT_NOT_FOUND, TRADING_DATE_NULL, OTHER
    /// </summary>
    private void EmitUnmappedFill(
        string instrument,
        string unmappedReason,
        decimal fillPrice,
        int fillQuantity,
        DateTimeOffset utcNow,
        object? orderId,
        Order? order,
        string? ntOrderName = null,
        string? tag = null,
        string? ocoId = null)
    {
        var execInstKey = _iea?.ExecutionInstrumentKey ?? (order != null ? ExecutionUpdateRouter.GetExecutionInstrumentKeyFromOrder(order.Instrument) : "");
        var accountName = _iea?.AccountName ?? (order != null ? ExecutionUpdateRouter.GetAccountNameFromOrder(order) : "");
        var brokerOrderId = orderId?.ToString() ?? "";
        var orderIdInternal = brokerOrderId;
        var seq = GetNextExecutionSequence(execInstKey, accountName);
        var fillGroupId = ComputeFillGroupId(null, orderIdInternal, brokerOrderId, utcNow.ToString("o"), fillPrice, fillQuantity);
        var side = "UNKNOWN";
        if (order != null)
        {
            try
            {
                dynamic d = order;
                var action = d.OrderAction;
                if (action != null && action.ToString()?.Contains("Buy") == true) side = "BUY";
                else if (action != null && action.ToString()?.Contains("Sell") == true) side = "SELL";
            }
            catch { }
        }

        _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "EXECUTION_FILLED",
            new
            {
                execution_sequence = seq,
                fill_group_id = fillGroupId,
                order_id = orderIdInternal,
                broker_order_id = brokerOrderId,
                execution_instrument_key = execInstKey,
                side = side,
                order_type = "UNMAPPED",
                fill_price = fillPrice,
                fill_quantity = fillQuantity,
                trading_date = "",
                account = accountName,
                source = "robot",
                mapped = false,
                unmapped_reason = unmappedReason,
                nt_order_name = ntOrderName,
                tag = tag,
                oco_id = ocoId
            }));

        // Phase 2: Emit EXECUTION_GHOST_FILL_DETECTED for institutional anomaly monitoring
        _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "EXECUTION_GHOST_FILL_DETECTED",
            new
            {
                account = accountName,
                instrument = instrument,
                broker_order_id = brokerOrderId,
                order_id = orderIdInternal,
                quantity = fillQuantity,
                price = fillPrice,
                side = side,
                mapped = false,
                reason = unmappedReason,
                stream_key = (string?)null,
                intent_id = (string?)null
            }));
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
                // OCO sibling suppression: if error is CancelPending and we have OCO, emit OCO_SIBLING_CANCELLED instead of ORDER_SUBMIT_FAIL
                var isOcoCancelPending = (error?.IndexOf("CancelPending", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 && !string.IsNullOrEmpty(ocoGroupToUse);
                if (isOcoCancelPending)
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "OCO_SIBLING_CANCELLED", new
                    {
                        oco_group_id = ocoGroupToUse,
                        order_type = "STOP",
                        intent_id = intentId,
                        note = "OCO sibling cancelled at submit (other leg filled) - expected, not ORDER_SUBMIT_FAIL"
                    }));
                    return OrderSubmissionResult.FailureResult(error, utcNow); // Still return failure, but no ORDER_SUBMIT_FAIL log
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
            _iea?.RegisterOrder(order.OrderId, intentId, instrument, IntentMap.TryGetValue(intentId, out var si) ? si.Stream : null, OrderRole.STOP, OrderOwnershipStatus.OWNED, "SubmitProtectiveStop", stopOrderInfo, utcNow);
            
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
                // OCO sibling suppression: if error is CancelPending and we have OCO, emit OCO_SIBLING_CANCELLED instead of ORDER_SUBMIT_FAIL
                var ocoForTarget = ocoGroup ?? GetIntentInfo(intentId).Item7;
                var isOcoCancelPendingTarget = (error?.IndexOf("CancelPending", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 && !string.IsNullOrEmpty(ocoForTarget);
                if (isOcoCancelPendingTarget)
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "OCO_SIBLING_CANCELLED", new
                    {
                        oco_group_id = ocoForTarget,
                        order_type = "TARGET",
                        intent_id = intentId,
                        note = "OCO sibling cancelled at submit (other leg filled) - expected, not ORDER_SUBMIT_FAIL"
                    }));
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
            _iea?.RegisterOrder(order.OrderId, intentId, instrument, IntentMap.TryGetValue(intentId, out var ti) ? ti.Stream : null, OrderRole.TARGET, OrderOwnershipStatus.OWNED, "SubmitTargetOrder", targetOrderInfo, utcNow);
            
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
    private readonly Dictionary<string, DateTimeOffset> _lastBePreChangeSnapshotUtcByIntent = new(StringComparer.OrdinalIgnoreCase);
    private const double BE_MODIFY_ATTEMPT_INTERVAL_MS = 200;
    private const double BE_PRE_CHANGE_SNAPSHOT_RATE_LIMIT_SEC = 5;

    /// <summary>Part 3 (optional): Check Enqueued backlog. Only in tick/bar path. No timers.</summary>
    private void CheckEnqueuedBacklog(DateTimeOffset utcNow)
    {
        foreach (var kvp in _firstEnqueuedUtcByIntent.ToArray())
        {
            var intentId = kvp.Key;
            var firstUtc = kvp.Value;
            if (GetProtectionState(intentId) != ProtectionState.Enqueued) continue;
            var elapsedSec = (utcNow - firstUtc).TotalSeconds;
            if (elapsedSec >= PROTECTION_ENQUEUED_WARN_SEC && elapsedSec < PROTECTION_ENQUEUED_TIMEOUT_SEC)
            {
                if (!_lastEnqueuedWarnUtcByIntent.TryGetValue(intentId, out var lastWarn) || (utcNow - lastWarn).TotalSeconds >= 30)
                {
                    _lastEnqueuedWarnUtcByIntent[intentId] = utcNow;
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", "PROTECTION_ENQUEUED_BACKLOG_WARN", new
                    {
                        intent_id = intentId,
                        elapsed_sec = Math.Round(elapsedSec, 1),
                        warn_sec = PROTECTION_ENQUEUED_WARN_SEC,
                        note = "Protectives enqueued for extended time - queue may be backed up"
                    }));
                }
            }
            if (ENABLE_PROTECTION_ENQUEUED_TIMEOUT && elapsedSec >= PROTECTION_ENQUEUED_TIMEOUT_SEC)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", "PROTECTION_ENQUEUED_BACKLOG_TIMEOUT", new
                {
                    intent_id = intentId,
                    elapsed_sec = Math.Round(elapsedSec, 1),
                    timeout_sec = PROTECTION_ENQUEUED_TIMEOUT_SEC,
                    note = "CRITICAL: Enqueued timeout - fail-closed flatten"
                }));
                _firstEnqueuedUtcByIntent.TryRemove(intentId, out _);
                if (IntentMap.TryGetValue(intentId, out var intent))
                    FailClosed(intentId, intent, $"Protection enqueued timeout ({elapsedSec:F0}s)", "PROTECTION_ENQUEUED_TIMEOUT", $"PROTECTION_TIMEOUT:{intentId}", "CRITICAL: Protection Enqueued Timeout", $"Protectives stuck in queue {elapsedSec:F0}s. Stream: {intent.Stream}.", null, null, new { elapsed_sec = elapsedSec }, utcNow);
            }
        }
    }

    /// <summary>Single BE evaluation function. Gating: IsBEModified and pending modify in GetActiveIntentsForBEMonitoring. Branch only at ModifyStopToBreakEven.</summary>
    void IIEAOrderExecutor.EvaluateBreakEvenCore(decimal tickPrice, DateTimeOffset eventTime, string executionInstrument)
    {
        EvaluateBreakEvenCoreImpl(tickPrice, eventTime, executionInstrument);
    }

    private void EvaluateBreakEvenCoreImpl(decimal tickPrice, DateTimeOffset eventTime, string executionInstrument)
    {
        var utcNow = DateTimeOffset.UtcNow;
        CheckEnqueuedBacklog(utcNow);
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

            // Use entry price only (the price it was supposed to be), not actual fill price
            var breakoutLevel = entryPrice;
            var beStopPrice = direction == "Long" ? breakoutLevel - tickSize : breakoutLevel + tickSize;

            if ((utcNow - (_lastBeModifyAttemptUtcByIntent.TryGetValue(intentId, out var lastAttempt) ? lastAttempt : DateTimeOffset.MinValue)).TotalMilliseconds < BE_MODIFY_ATTEMPT_INTERVAL_MS)
                continue;
            _lastBeModifyAttemptUtcByIntent[intentId] = utcNow;

            var modifyResult = ModifyStopToBreakEven(intentId, intent.Instrument ?? "", beStopPrice, utcNow);

            if (modifyResult.Success)
            {
                _lastBeModifyAttemptUtcByIntent.Remove(intentId);
                _firstMissingStopUtcByIntent.TryRemove(intentId, out _);
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument ?? "", "BE_TRIGGERED", new
                {
                    direction,
                    breakout_level = breakoutLevel,
                    actual_fill_price = actualFillPrice,
                    be_trigger_price = beTriggerPrice,
                    be_stop_price = beStopPrice,
                    tick_size = tickSize,
                    tick_price = tickPrice
                }));
            }
            else
            {
                var errorMsg = modifyResult.ErrorMessage ?? "";
                var isRetryableError = errorMsg.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      errorMsg.IndexOf("Stop order", StringComparison.OrdinalIgnoreCase) >= 0;
                var stopMissing = isRetryableError;

                if (stopMissing)
                {
                    var protectionState = GetProtectionState(intentId);
                    // Only skip flatten when protectives are known to be in-flight (Enqueued, Executing, Submitted)
                    if (protectionState == ProtectionState.Enqueued || protectionState == ProtectionState.Executing || protectionState == ProtectionState.Submitted)
                    {
                        var accountName = _iea?.AccountName ?? "";
                        var execInstKey = _iea?.ExecutionInstrumentKey ?? intent.ExecutionInstrument ?? "";
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument ?? "", "BE_STOP_NOT_VISIBLE_PROTECTION_PENDING", new
                        {
                            intent_id = intentId,
                            execution_instrument_key = execInstKey,
                            account_name = accountName,
                            protection_state = protectionState.ToString(),
                            error = errorMsg,
                            note = "Stop not visible; protection pending - skip flatten, wait for next tick"
                        }));
                        continue;
                    }
                    // None or Working: use 5s wall-clock. None = non-IEA path (no ProtectionState) or unknown.
                    if (!_firstMissingStopUtcByIntent.TryGetValue(intentId, out var firstMissing))
                    {
                        _firstMissingStopUtcByIntent.AddOrUpdate(intentId, utcNow, (_, _) => utcNow);
                        firstMissing = utcNow;
                    }
                    var elapsedMs = (utcNow - firstMissing).TotalMilliseconds;
                    if (elapsedMs >= BE_STOP_VISIBILITY_TIMEOUT_SEC * 1000)
                    {
                        var accountName = _iea?.AccountName ?? "";
                        var execInstKey = _iea?.ExecutionInstrumentKey ?? intent.ExecutionInstrument ?? "";
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument ?? "", "BE_STOP_VISIBILITY_TIMEOUT", new
                        {
                            intent_id = intentId,
                            execution_instrument_key = execInstKey,
                            account_name = accountName,
                            elapsed_ms = (long)elapsedMs,
                            timeout_sec = BE_STOP_VISIBILITY_TIMEOUT_SEC,
                            note = "Stop still not visible after 5s wall-clock - flatten (real inconsistency)"
                        }));
                        _firstMissingStopUtcByIntent.TryRemove(intentId, out _);
                        _lastBeModifyAttemptUtcByIntent.Remove(intentId);

                        // Guard: If account is flat for this instrument, skip flatten to prevent opening a wrong
                        // position (e.g. SELL when flat would open short). See 2026-03-13_MES_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md
                        var account = _ntAccount as Account;
                        var hasPosition = false;
                        if (account != null && !string.IsNullOrWhiteSpace(intent.Instrument))
                        {
                            try
                            {
                                hasPosition = account.Positions.Any(p => p.Quantity != 0 &&
                                    ExecutionInstrumentResolver.IsSameInstrument(p.Instrument?.MasterInstrument?.Name, intent.Instrument));
                            }
                            catch { /* fall through to flatten if check fails */ }
                        }
                        if (!hasPosition)
                        {
                            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument ?? "", "BE_STOP_VISIBILITY_TIMEOUT_SKIP_ACCOUNT_FLAT", new
                            {
                                intent_id = intentId,
                                execution_instrument_key = execInstKey,
                                note = "Account flat for instrument - skip flatten; stop likely cancelled from prior flatten"
                            }));
                            continue;
                        }
                        Flatten(intentId, intent.Instrument ?? "", utcNow);
                    }
                }
                else
                {
                    _standDownStreamCallback?.Invoke(intent.Stream ?? "", utcNow, $"BE_MODIFY_MAX_RETRIES:{intentId}");
                }
            }
        }
    }

    /// <summary>
    /// Timeout check: runs in EvaluateBreakEven tick path. Post-read, retry, or STOP_MODIFY_FAILED.
    /// Confirmation always wins over timeout — if CONFIRMED arrives after TIMEOUT but before FAILED, we cancel retry.
    /// </summary>
    private void CheckPendingBETimeouts(DateTimeOffset utcNow)
    {
        var account = _ntAccount as Account;
        if (account == null) return;

        var toProcess = _pendingBERequests.ToArray();

        foreach (var kvp in toProcess)
        {
            var stopOrderId = kvp.Key;
            var pending = kvp.Value;

            // Phase 1: Retry scheduled — time to retry
            if (pending.RetryUtc != null && pending.RetryUtc.Value <= utcNow && pending.RetryCount < BE_MAX_RETRY_ATTEMPTS)
            {
                _pendingBERequests.TryRemove(stopOrderId, out _);
                ModifyStopToBreakEven(pending.IntentId, pending.Instrument, pending.RequestedStopPriceQuantized, utcNow, pending.RetryCount + 1);
                continue;
            }

            // Phase 2: Initial wait timed out (no retry scheduled yet)
            if (pending.RetryUtc == null && (utcNow - pending.RequestedUtc).TotalSeconds <= BE_CONFIRM_TIMEOUT_SEC)
                continue;

            if (pending.RetryUtc != null)
                continue; // Already in retry wait, not yet time

            // Timeout: try post-read first
            var tickSize = GetTickSizeForInstrument(pending.Instrument);
            Order? stopOrder = null;
            try
            {
                stopOrder = account.Orders.FirstOrDefault(o =>
                    (o.OrderId == stopOrderId || GetOrderTag(o) == pending.RawTag) &&
                    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));
            }
            catch { }

            if (stopOrder != null)
            {
                var orderStopQuantized = QuantizeToTick((decimal)stopOrder.StopPrice, tickSize);
                if (orderStopQuantized == pending.RequestedStopPriceQuantized)
                {
                    _pendingBERequests.TryRemove(stopOrderId, out _);
                    ConfirmAndRecordBE(pending, orderStopQuantized, utcNow);
                    continue;
                }
            }

            // Post-read did not confirm: emit TIMEOUT, schedule retry or fail
            _log.Write(RobotEvents.ExecutionBase(utcNow, pending.IntentId, pending.Instrument, "STOP_MODIFY_TIMEOUT", new
            {
                stop_order_id = stopOrderId,
                intent_id = pending.IntentId,
                retry_count = pending.RetryCount,
                requested_stop_price_quantized = pending.RequestedStopPriceQuantized
            }));

            var nextRetryCount = pending.RetryCount + 1;
            if (nextRetryCount >= BE_MAX_RETRY_ATTEMPTS)
            {
                _pendingBERequests.TryRemove(stopOrderId, out _);
                _log.Write(RobotEvents.ExecutionBase(utcNow, pending.IntentId, pending.Instrument, "STOP_MODIFY_FAILED", new
                {
                    stop_order_id = stopOrderId,
                    intent_id = pending.IntentId,
                    reason = "MAX_RETRIES_EXCEEDED",
                    retry_count = nextRetryCount
                }));
                _log.Write(RobotEvents.ExecutionBase(utcNow, pending.IntentId, pending.Instrument, "STOP_MODIFY_FAILED_SNAPSHOT", new
                {
                    stop_order_id = stopOrderId,
                    last_seen_stop_price = stopOrder != null ? (decimal?)(decimal)stopOrder.StopPrice : null,
                    order_state = stopOrder != null ? stopOrder.OrderState.ToString() : "NotFound",
                    retry_count = nextRetryCount,
                    instrument = pending.Instrument,
                    intent_id = pending.IntentId
                }));
            }
            else
            {
                pending.RetryCount = nextRetryCount;
                pending.RetryUtc = utcNow.AddSeconds(BE_RETRY_INTERVAL_SEC);
                _pendingBERequests[stopOrderId] = pending;
            }
        }
    }

    /// <summary>
    /// Check if STOP order update confirms a pending BE request. Direct match: stop_order_id. Replace: intent_id + same OCO or recent CancelPending.
    /// </summary>
    private void TryConfirmPendingBE(Order order, string intentId, ParsedTagResult parsed, OrderState orderState, DateTimeOffset utcNow)
    {
        if (parsed.Leg != "STOP") return;
        if (orderState != OrderState.Working && orderState != OrderState.Accepted) return;

        var stopOrderId = order.OrderId ?? "";
        var orderStopPrice = (decimal)order.StopPrice;
        var instrument = order.Instrument?.MasterInstrument?.Name ?? "";
        var accountName = (_ntAccount as Account)?.Name ?? "";
        var orderExecutionKey = ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(accountName, order.Instrument, _ieaEngineExecutionInstrument);
        var tickSize = GetTickSizeForInstrument(instrument);
        var orderStopQuantized = QuantizeToTick(orderStopPrice, tickSize);

        // Direct match: stop_order_id in pending
        if (_pendingBERequests.TryGetValue(stopOrderId, out var pending))
        {
            if (orderExecutionKey != pending.ExecutionInstrumentKey) return;
            if (orderStopQuantized == pending.RequestedStopPriceQuantized)
            {
                _pendingBERequests.TryRemove(stopOrderId, out _);
                ConfirmAndRecordBE(pending, orderStopQuantized, utcNow);
                return;
            }
            // Broker reverted our BE change - order came back with different (old) price
            _pendingBERequests.TryRemove(stopOrderId, out _);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_REVERTED", new
            {
                stop_order_id = stopOrderId,
                intent_id = intentId,
                requested_stop_price = pending.RequestedStopPriceQuantized,
                actual_stop_price = orderStopQuantized,
                note = "Broker reverted BE change - order returned with different price. May retry on next tick."
            }));
            return;
        }

        // Replace-semantics: new STOP with different id, same intent_id, stop price matches, (same OCO or recent CancelPending)
        foreach (var kvp in _pendingBERequests.ToArray())
        {
            var p = kvp.Value;
            if (p.IntentId != intentId) continue;
            if (orderExecutionKey != p.ExecutionInstrumentKey) continue;
            if (orderStopQuantized != p.RequestedStopPriceQuantized) continue;

            var sameOco = !string.IsNullOrEmpty(p.OcoId) && string.Equals(order.Oco as string, p.OcoId, StringComparison.OrdinalIgnoreCase);
            var recentCancel = _pendingBECancelUtcByIntent.TryGetValue(intentId, out var cancelUtc) &&
                (utcNow - cancelUtc).TotalSeconds <= BE_REPLACE_CANCEL_WINDOW_SEC;
            if (!sameOco && !recentCancel) continue;

            _pendingBERequests.TryRemove(kvp.Key, out _);
            ConfirmAndRecordBE(p, orderStopQuantized, utcNow);
            return;
        }
    }

    private void ConfirmAndRecordBE(PendingBERequest pending, decimal confirmedStopPrice, DateTimeOffset utcNow)
    {
        decimal? previousStopPrice = null;
        decimal? beTriggerPrice = null;
        if (IntentMap.TryGetValue(pending.IntentId, out var intent))
            beTriggerPrice = intent.BeTrigger;
        var journalPath = System.IO.Path.Combine(_projectRoot, "data", "execution_journals", $"{pending.TradingDate}_{pending.Stream}_{pending.IntentId}.json");
        if (System.IO.File.Exists(journalPath))
        {
            try
            {
                var journalJson = System.IO.File.ReadAllText(journalPath);
                var journalEntry = QTSW2.Robot.Core.JsonUtil.Deserialize<ExecutionJournalEntry>(journalJson);
                previousStopPrice = journalEntry?.StopPrice ?? journalEntry?.BEStopPrice;
            }
            catch { }
        }
        _executionJournal.RecordBEModification(pending.IntentId, pending.TradingDate, pending.Stream, confirmedStopPrice, utcNow,
            previousStopPrice: previousStopPrice, beTriggerPrice: beTriggerPrice, entryPrice: null);
        _log.Write(RobotEvents.ExecutionBase(utcNow, pending.IntentId, pending.Instrument, "STOP_MODIFY_CONFIRMED", new
        {
            intent_id = pending.IntentId,
            confirmed_stop_price = confirmedStopPrice,
            requested_stop_price_quantized = pending.RequestedStopPriceQuantized
        }));
    }

    /// <summary>
    /// STEP 5: Modify stop to break-even using real NT API.
    /// </summary>
    private OrderModificationResult ModifyStopToBreakEvenReal(
        string intentId,
        string instrument,
        decimal beStopPrice,
        DateTimeOffset utcNow,
        int retryCount = 0)
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

            // Quantize requested stop (deterministic rounding)
            var tickSize = GetTickSizeForInstrument(instrument);
            var beStopQuantized = QuantizeToTick(beStopPrice, tickSize);
            var (tradingDate8, stream8, intentEntryPrice2, intentStopPrice3, intentTargetPrice3, _, _) = GetIntentInfo(intentId);
            var accountName = (account as Account)?.Name ?? "";
            var executionInstrumentKey = ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(accountName, stopOrder.Instrument, _ieaEngineExecutionInstrument);
            var rawTag = GetOrderTag(stopOrder) ?? RobotOrderIds.EncodeStopTag(intentId);
            var ocoId = stopOrder.Oco as string;

            // NT OCO FIX: account.Change() on OCO stop orders is rejected/reverted by broker.
            // Use cancel+replace: cancel both OCO legs, submit new stop at BE + new target.
            var quantity = stopOrder.Quantity;
            var targetPrice = intentTargetPrice3 ?? 0m;
            var targetTag = RobotOrderIds.EncodeTargetTag(intentId);
            var targetOrder = account.Orders.FirstOrDefault(o =>
                GetOrderTag(o) == targetTag &&
                (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));
            if (targetOrder != null && intentTargetPrice3 == null)
                targetPrice = (decimal)targetOrder.LimitPrice;

            if (targetPrice <= 0)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "BE_REPLACE_SKIP", new
                {
                    error = "Target price not found for BE replace",
                    intent_id = intentId,
                    note = "Cannot cancel+replace without target price"
                }));
                return OrderModificationResult.FailureResult("Target price not found for BE replace", utcNow);
            }

            var direction = intentDirection ?? "Long";
            var newOcoGroup = $"QTSW2:OCO_BE:{intentId}:{Guid.NewGuid():N}"; // Always unique, never reused

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "BE_CANCEL_REPLACE_START", new
            {
                stop_order_id = stopOrder.OrderId,
                current_stop_price = (decimal)stopOrder.StopPrice,
                requested_be_stop = beStopQuantized,
                target_price = targetPrice,
                quantity,
                old_oco_id = ocoId,
                new_oco_id = newOcoGroup,
                note = "OCO Change() reverts - using cancel+replace for BE"
            }));

            // 1. Cancel both OCO orders
            CancelProtectiveOrdersForIntent(intentId, utcNow);

            // 2. Submit new stop at BE price
            var stopResult = SubmitProtectiveStop(intentId, instrument, direction, beStopQuantized, quantity, newOcoGroup, utcNow);
            if (!stopResult.Success)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "BE_REPLACE_STOP_FAIL", new
                {
                    error = stopResult.ErrorMessage,
                    note = "BE replace: stop submit failed after cancel"
                }));
                return OrderModificationResult.FailureResult(stopResult.ErrorMessage ?? "BE stop submit failed", utcNow);
            }

            // Overlap adoption: both old and new stop use same intent tag (QTSW2:{intentId}:STOP).
            // OrderMap[intentId] is overwritten by SubmitProtectiveStop with newest; we adopt newest deterministically.
            var newStopOrderId = stopResult.BrokerOrderId ?? "";
            if (!string.IsNullOrEmpty(newStopOrderId))
                _pendingBECancelReplaceByIntent[intentId] = (newStopOrderId, utcNow);

            // 3. Submit new target (same price as before)
            var targetResult = SubmitTargetOrder(intentId, instrument, direction, targetPrice, quantity, newOcoGroup, utcNow);
            if (!targetResult.Success)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "BE_REPLACE_TARGET_FAIL", new
                {
                    error = targetResult.ErrorMessage,
                    note = "BE replace: target submit failed - position has BE stop but no target"
                }));
                // Stop is working; target failed. Log but consider BE done (stop is at BE).
                return OrderModificationResult.SuccessResult(utcNow);
            }

            // 4. Record BE modification (we replaced, so new stop is at BE)
            _executionJournal.RecordBEModification(intentId, tradingDate8 ?? "", stream8 ?? "", beStopQuantized, utcNow,
                previousStopPrice: currentStop, beTriggerPrice: IntentMap.TryGetValue(intentId, out var beIntent) ? beIntent.BeTrigger : null, entryPrice: intentEntryPrice2);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "BE_CANCEL_REPLACE_DONE", new
            {
                be_stop_price = beStopQuantized,
                target_price = targetPrice,
                new_stop_order_id = newStopOrderId,
                note = "BE cancel+replace complete; STOP_WORKING will log when broker confirms"
            }));

            return OrderModificationResult.SuccessResult(utcNow);
        }
        catch (Exception ex)
        {
            return OrderModificationResult.FailureResult($"BE modification failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// Get current market bid/ask for breakout validity gate.
    /// </summary>
    private (decimal? Bid, decimal? Ask) GetCurrentMarketPriceReal(string instrument, DateTimeOffset utcNow)
    {
        if (_ntInstrument == null)
            return (null, null);
        try
        {
            dynamic dynInstrument = _ntInstrument;
            var marketData = dynInstrument.MarketData;
            if (marketData == null)
                return (null, null);
            double? bid = null;
            double? ask = null;
            try
            {
                bid = (double?)marketData.GetBid(0);
                ask = (double?)marketData.GetAsk(0);
            }
            catch
            {
                try
                {
                    bid = (double?)marketData.Bid;
                    ask = (double?)marketData.Ask;
                }
                catch { return (null, null); }
            }
            if (bid.HasValue && ask.HasValue && !double.IsNaN(bid.Value) && !double.IsNaN(ask.Value))
                return ((decimal)bid.Value, (decimal)ask.Value);
        }
        catch { }
        return (null, null);
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
                        AveragePrice = (decimal)position.AveragePrice,
                        ContractLabel = position.Instrument.FullName
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
    /// Terminalize an intent after STOP/TARGET execution fill: cancel sibling protectives via strategy-thread NT command,
    /// then verify no Working/Accepted protectives remain (verification after cancel completes, not before enqueue).
    /// </summary>
    private void TerminalizeIntent(string intentId, string tradingDate, string stream, string completionReason, DateTimeOffset utcNow)
    {
        var auditRows = ScanProtectiveOrdersForSiblingCancelAudit(intentId, out var eligibleCount);

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "SIBLING_PROTECTIVE_CANCEL_REQUESTED", "ENGINE",
            new
            {
                intent_id = intentId,
                stream,
                completion_reason = completionReason,
                eligible_working_or_accepted_count_matched = eligibleCount,
                protective_leg_rows = auditRows,
                nt_action_queue_configured = _ntActionQueue != null,
                note = "Exit fill: audit before routing sibling protective cancel"
            }));

        if (eligibleCount == 0)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "SIBLING_PROTECTIVE_CANCEL_SKIPPED_NO_ELIGIBLE", "ENGINE",
                new
                {
                    intent_id = intentId,
                    stream,
                    completion_reason = completionReason,
                    protective_leg_rows = auditRows,
                    note = "No Working/Accepted protectives matched robot tags — broker may have moved sibling (e.g. OCO); invariant check runs immediately"
                }));
            RunTerminalProtectiveInvariantCheck(intentId, tradingDate, stream, completionReason, utcNow);
            return;
        }

        if (_ntActionQueue != null)
        {
            var cid = $"SIBLING_PROTECTIVE:{intentId}:{utcNow:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}";
            var cmd = new NtCancelOrdersCommand(
                cid,
                intentId,
                null,
                true,
                NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill,
                utcNow,
                preferUrgentDrain: true,
                verifyWorkingProtectivesClearedAfter: true,
                postCancelTradingDate: tradingDate,
                postCancelStream: stream,
                postCancelCompletionReason: completionReason);

            var enqueued = _ntActionQueue.EnqueueNtAction(cmd, out var dup);
            if (!enqueued)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "SIBLING_PROTECTIVE_CANCEL_ENQUEUE_FAILED", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        correlation_id = cid,
                        dropped_as_duplicate = dup,
                        urgent_pending = _ntActionQueue.UrgentPendingCount,
                        normal_pending = _ntActionQueue.NormalPendingCount,
                        note = dup
                            ? "Duplicate correlation id — another sibling cancel may already be pending"
                            : "EnqueueNtAction returned false — fall back to thread-guard cancel path"
                    }));
                CancelProtectiveOrdersForIntent(intentId, utcNow);
                RunTerminalProtectiveInvariantCheck(intentId, tradingDate, stream, completionReason, utcNow);
                return;
            }

            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "SIBLING_PROTECTIVE_CANCEL_ENQUEUED", "ENGINE",
                new
                {
                    intent_id = intentId,
                    stream,
                    correlation_id = cid,
                    reason = NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill,
                    urgent_lane = true,
                    urgent_pending_after = _ntActionQueue.UrgentPendingCount,
                    normal_pending_after = _ntActionQueue.NormalPendingCount,
                    total_pending_after = _ntActionQueue.PendingCount,
                    eligible_count_at_request = eligibleCount,
                    note = "Sibling cancel on URGENT lane; invariant check deferred until strategy-thread dispatch completes"
                }));
            return;
        }

        CancelProtectiveOrdersForIntent(intentId, utcNow);
        RunTerminalProtectiveInvariantCheck(intentId, tradingDate, stream, completionReason, utcNow);
    }

    /// <summary>Build JSON-serializable rows for all broker orders matching this intent's STOP/TARGET tags (any state).</summary>
    private List<Dictionary<string, object?>> ScanProtectiveOrdersForSiblingCancelAudit(string intentId, out int eligibleWorkingOrAcceptedCount)
    {
        var rows = new List<Dictionary<string, object?>>();
        eligibleWorkingOrAcceptedCount = 0;
        if (_ntAccount is not Account account)
            return rows;

        var stopTag = RobotOrderIds.EncodeStopTag(intentId);
        var targetTag = RobotOrderIds.EncodeTargetTag(intentId);

        foreach (var order in account.Orders)
        {
            var tag = GetOrderTag(order) ?? "";
            if (!string.Equals(tag, stopTag, StringComparison.Ordinal) &&
                !string.Equals(tag, targetTag, StringComparison.Ordinal))
                continue;

            var stateStr = order.OrderState.ToString();
            var eligible = order.OrderState == OrderState.Working || order.OrderState == OrderState.Accepted;
            if (eligible)
                eligibleWorkingOrAcceptedCount++;

            rows.Add(new Dictionary<string, object?>
            {
                ["broker_order_id"] = order.OrderId,
                ["order_state"] = stateStr,
                ["tag"] = tag,
                ["eligible_for_immediate_cancel"] = eligible
            });
        }

        return rows;
    }

    private void RunTerminalProtectiveInvariantCheck(string intentId, string tradingDate, string stream, string completionReason, DateTimeOffset utcNow)
    {
        var workingOrders = GetWorkingProtectiveOrdersForIntent(intentId);
        if (workingOrders.Count > 0)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "TERMINAL_INTENT_HAS_WORKING_ORDERS", "ENGINE",
                new
                {
                    error = "Intent exit handling still has working protective orders - invariant violated",
                    intent_id = intentId,
                    stream = stream,
                    completion_reason = completionReason,
                    working_order_ids = workingOrders.Select(o => o.OrderId).ToList(),
                    working_order_types = workingOrders.Select(o => GetOrderTag(o)).ToList(),
                    count = workingOrders.Count,
                    action = "CRITICAL_ANOMALY",
                    note = "Expected no Working/Accepted protectives after sibling cleanup. Route to reconciliation."
                }));
            var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
            if (notificationService != null)
            {
                notificationService.EnqueueNotification($"TERMINAL_INTENT_HAS_WORKING_ORDERS:{intentId}",
                    "CRITICAL: Terminal Intent Has Working Orders",
                    $"Intent {intentId} completed ({completionReason}) but {workingOrders.Count} protective order(s) still Working. Order IDs: {string.Join(", ", workingOrders.Select(o => o.OrderId))}.",
                    priority: 2);
            }
            CancelProtectiveOrdersForIntent(intentId, utcNow);
        }
        else
        {
            _log.Write(RobotEvents.EngineBase(utcNow, intentId, "", "TERMINAL_INTENT_VERIFIED",
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
        foreach (var order in account.Orders)
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
    /// <param name="strategyThreadInlineCancel">When true, caller is strategy-thread NT command — skip EnsureStrategyThreadOrEnqueue.</param>
    /// <param name="siblingCancelCorrelationId">When set, success log uses sibling-cancel lifecycle event name.</param>
    private void CancelProtectiveOrdersForIntent(string intentId, DateTimeOffset utcNow, bool strategyThreadInlineCancel = false, string? siblingCancelCorrelationId = null)
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
        var consideredRows = new List<Dictionary<string, object?>>();
        
        try
        {
            // Find protective orders (stop and target) for this intent
            var stopTag = RobotOrderIds.EncodeStopTag(intentId);
            var targetTag = RobotOrderIds.EncodeTargetTag(intentId);
            
            foreach (var order in account.Orders)
            {
                var tag = GetOrderTag(order) ?? "";
                if (!string.Equals(tag, stopTag, StringComparison.Ordinal) &&
                    !string.Equals(tag, targetTag, StringComparison.Ordinal))
                    continue;

                var stateStr = order.OrderState.ToString();
                var eligible = order.OrderState == OrderState.Working || order.OrderState == OrderState.Accepted;
                consideredRows.Add(new Dictionary<string, object?>
                {
                    ["broker_order_id"] = order.OrderId,
                    ["order_state"] = stateStr,
                    ["tag"] = tag,
                    ["eligible_for_immediate_cancel"] = eligible
                });

                if (!eligible)
                    continue;
                
                ordersToCancel.Add(order);
            }
            
            if (ordersToCancel.Count > 0)
            {
                var ordersArr = ordersToCancel.ToArray();
                if (strategyThreadInlineCancel)
                {
                    account.Cancel(ordersArr);
                    if (!string.IsNullOrEmpty(siblingCancelCorrelationId))
                    {
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", "SIBLING_PROTECTIVE_CANCEL_COMPLETED", new
                        {
                            intent_id = intentId,
                            correlation_id = siblingCancelCorrelationId,
                            cancelled_count = ordersToCancel.Count,
                            cancelled_order_ids = ordersToCancel.Select(o => o.OrderId).ToList(),
                            cancelled_tags = ordersToCancel.Select(o => GetOrderTag(o)).ToList(),
                            orders_considered = consideredRows,
                            note = "account.Cancel invoked on strategy thread (sibling protective cleanup path)"
                        }));
                    }
                    else
                    {
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", "PROTECTIVE_ORDERS_CANCELLED_FOR_RECREATE", new
                        {
                            intent_id = intentId,
                            cancelled_count = ordersToCancel.Count,
                            cancelled_order_ids = ordersToCancel.Select(o => o.OrderId).ToList(),
                            cancelled_tags = ordersToCancel.Select(o => GetOrderTag(o)).ToList(),
                            note = "Cancelled protective orders (inline strategy-thread path)"
                        }));
                    }
                }
                else if (!EnsureStrategyThreadOrEnqueue("CancelProtectiveOrdersForRecreate", intentId, null, $"CANCEL_PROTECTIVE:{intentId}", () => account.Cancel(ordersArr)))
                {
                    return;
                }
                else
                {
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
            else if (strategyThreadInlineCancel && !string.IsNullOrEmpty(siblingCancelCorrelationId))
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", "SIBLING_PROTECTIVE_CANCEL_SKIPPED_NO_ELIGIBLE", "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        correlation_id = siblingCancelCorrelationId,
                        orders_considered = consideredRows,
                        note = "At strategy dispatch: no Working/Accepted protectives matched tags (race with OCO or prior cancel)"
                    }));
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", "PROTECTIVE_ORDERS_CANCEL_ERROR", new
            {
                intent_id = intentId,
                correlation_id = siblingCancelCorrelationId,
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
            RecordSessionIdentityBlockAttempt();
            return FlattenResult.FailureResult("SESSION_IDENTITY_LATCHED", utcNow);
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
                    _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "DESTRUCTIVE_ACTION_REQUESTED", new
                    {
                        source = src.ToString(),
                        trigger = tr.ToString(),
                        execution_instrument_key = execKey,
                        phase = "flatten_intent_real_pre_submit"
                    }));
                    var polDec = DestructiveActionPolicy.EvaluateDestructiveActionPolicy(polIn);
                    _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "DESTRUCTIVE_ACTION_DECISION", new
                    {
                        allowed = polDec.AllowInstrumentScope,
                        reason = polDec.ReasonCode,
                        scope = polDec.CancelScopeMode,
                        policy_path = polDec.PolicyPath,
                        phase = "flatten_intent_real_pre_submit"
                    }));
                    if (!polDec.AllowInstrumentScope)
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "DESTRUCTIVE_ACTION_BLOCKED", new
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
                        LegContractLabel = leg.ContractLabel
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
                    _log.Write(RobotEvents.EngineBase(utcNow, "", instrument, "DESTRUCTIVE_ACTION_EXECUTED", new
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

        foreach (var order in account.Orders)
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

            foreach (var order in account.Orders)
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
                    
                    // QUICK BREAKOUT: If price has already passed through the stop, allow the order.
                    // Long stop: triggers when price >= stopPrice. If current ask >= stopPrice, we're in the money.
                    // Short stop: triggers when price <= stopPrice. If current bid <= stopPrice, we're in the money.
                    // In these cases, the stop will fill immediately — exactly what we want for a fast breakout.
                    bool stopInTheMoney = (direction == "Long" && currentMarketPrice.Value >= stopPrice) ||
                                         (direction == "Short" && currentMarketPrice.Value <= stopPrice);
                    
                    if (stopInTheMoney)
                    {
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_PRICE_VALIDATION_SKIPPED_ITM", new
                        {
                            stop_price = stopPrice,
                            market_price = currentMarketPrice.Value,
                            market_price_source = direction == "Long" ? "Ask" : "Bid",
                            direction,
                            stop_distance_points = stopDistance,
                            note = "Stop in the money (quick breakout) — skipping distance validation, allowing order"
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

        return OrderSubmissionResult.SuccessResult(order.OrderId, utcNow, acknowledgedAt);
    }

    /// <summary>
    /// Tagged broker exposure without open journal row: upsert recovery journal when ownership evidence is strong.
    /// </summary>
    private bool TryRepairTaggedBrokerWithoutJournalCore(
        string instrument,
        int accountQtyAbs,
        int journalOpenQtySum,
        DateTimeOffset utcNow,
        out string resultCode,
        out string? detail)
    {
        resultCode = "";
        detail = null;
        if (journalOpenQtySum > 0)
        {
            resultCode = "SKIP_JOURNAL_ALREADY_OPEN";
            return false;
        }
        if (accountQtyAbs <= 0)
        {
            resultCode = "SKIP_NO_ACCOUNT_EXPOSURE";
            return false;
        }
        if (!_ntContextSet)
        {
            resultCode = "NT_CONTEXT";
            return false;
        }

        var instTrim = (instrument ?? "").Trim();
        if (string.IsNullOrEmpty(instTrim))
        {
            resultCode = "INSTRUMENT_EMPTY";
            return false;
        }

        if (TrySuppressTaggedRepairRepeatedFailure(instTrim, accountQtyAbs, journalOpenQtySum, utcNow, out resultCode))
        {
            detail = null;
            return false;
        }

        AccountSnapshot snap;
        try
        {
            snap = GetAccountSnapshotReal(utcNow);
        }
        catch (Exception ex)
        {
            resultCode = "SNAPSHOT_ERROR";
            detail = ex.Message;
            return false;
        }

        PrepareOrderRegistryForMismatchAssembly(instTrim, snap, utcNow);

        var positions = snap.Positions ?? new List<PositionSnapshot>();
        var exposure = BrokerPositionResolver.ResolveFromSnapshots(positions, instTrim);
        if (exposure.ReconciliationAbsQuantityTotal <= 0)
        {
            resultCode = "SNAPSHOT_ZERO_POSITION";
            RecordTaggedRepairFailureForCooldown(instTrim, accountQtyAbs, journalOpenQtySum, utcNow, resultCode);
            return false;
        }

        var repairQty = Math.Min(accountQtyAbs, exposure.ReconciliationAbsQuantityTotal);
        if (repairQty <= 0)
            repairQty = accountQtyAbs;

        var signedSum = 0;
        foreach (var leg in exposure.Legs)
            signedSum += leg.SignedQuantity;
        var direction = signedSum > 0 ? "Long" : "Short";

        decimal? avgFill = null;
        foreach (var p in positions)
        {
            if (p.Quantity == 0 || string.IsNullOrWhiteSpace(p.Instrument)) continue;
            if (!string.Equals(p.Instrument.Trim(), instTrim, StringComparison.OrdinalIgnoreCase)) continue;
            avgFill = p.AveragePrice;
            break;
        }

        var robotWorkingOnInst = 0;
        var brokerOrderIds = new List<string>();
        foreach (var w in snap.WorkingOrders ?? new List<WorkingOrderSnapshot>())
        {
            if (string.IsNullOrEmpty(w.Tag) || !w.Tag.StartsWith(RobotOrderIds.Prefix, StringComparison.Ordinal)) continue;
            var wInst = (w.Instrument ?? "").Trim();
            if (!string.Equals(wInst, instTrim, StringComparison.OrdinalIgnoreCase) &&
                !ExecutionInstrumentResolver.IsSameInstrument(wInst, instTrim))
                continue;
            robotWorkingOnInst++;
            if (!string.IsNullOrEmpty(w.OrderId))
                brokerOrderIds.Add(w.OrderId);
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in GetActiveIntentIdsForProtectiveAudit(instTrim))
            candidates.Add(id);
        foreach (var w in snap.WorkingOrders ?? new List<WorkingOrderSnapshot>())
        {
            if (string.IsNullOrEmpty(w.Tag) || !w.Tag.StartsWith(RobotOrderIds.Prefix, StringComparison.Ordinal)) continue;
            var wInst = (w.Instrument ?? "").Trim();
            if (!string.Equals(wInst, instTrim, StringComparison.OrdinalIgnoreCase) &&
                !ExecutionInstrumentResolver.IsSameInstrument(wInst, instTrim))
                continue;
            var dec = RobotOrderIds.DecodeIntentId(w.Tag);
            if (!string.IsNullOrEmpty(dec))
                candidates.Add(dec);
        }
        foreach (var kv in OrderMap)
        {
            var oi = kv.Value;
            if (!oi.IsEntryOrder) continue;
            var oInst = (oi.Instrument ?? "").Trim();
            if (!string.Equals(oInst, instTrim, StringComparison.OrdinalIgnoreCase) &&
                !ExecutionInstrumentResolver.IsSameInstrument(oInst, instTrim))
                continue;
            candidates.Add(kv.Key);
        }

        var candidateList = candidates.Count > 0 ? candidates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
        if (candidateList.Count == 0)
        {
            resultCode = "NO_OWNERSHIP_EVIDENCE";
            _log.Write(RobotEvents.EngineBase(utcNow, "", "TAGGED_BROKER_WITHOUT_JOURNAL_DETECTED", "ENGINE",
                new
                {
                    instrument = instTrim,
                    account_qty = accountQtyAbs,
                    journal_qty = journalOpenQtySum,
                    intent_ids = Array.Empty<string>(),
                    registry_owned_working_count = robotWorkingOnInst,
                    broker_order_ids_sample = brokerOrderIds.Take(8).ToList(),
                    repair_action = "NONE",
                    repair_result = resultCode,
                    reason = "TAGGED_ORPHAN_POSITION_RECOVERY"
                }));
            RecordTaggedRepairFailureForCooldown(instTrim, accountQtyAbs, journalOpenQtySum, utcNow, resultCode);
            return false;
        }

        string? bestId = null;
        Intent? bestIntent = null;
        foreach (var cid in candidateList)
        {
            if (!IntentMap.TryGetValue(cid, out var intn) || intn == null) continue;
            if (string.IsNullOrWhiteSpace(intn.TradingDate) || string.IsNullOrWhiteSpace(intn.Stream)) continue;
            var idir = intn.Direction ?? "";
            if (!string.IsNullOrEmpty(idir) && !idir.Equals(direction, StringComparison.OrdinalIgnoreCase))
                continue;
            bestId = cid;
            bestIntent = intn;
            break;
        }
        if (bestId == null)
        {
            foreach (var cid in candidateList)
            {
                if (!IntentMap.TryGetValue(cid, out var intn) || intn == null) continue;
                if (string.IsNullOrWhiteSpace(intn.TradingDate) || string.IsNullOrWhiteSpace(intn.Stream)) continue;
                bestId = cid;
                bestIntent = intn;
                break;
            }
        }

        if (bestId == null || bestIntent == null)
        {
            resultCode = "NO_QUALIFYING_INTENT_CONTEXT";
            _log.Write(RobotEvents.EngineBase(utcNow, "", "TAGGED_BROKER_WITHOUT_JOURNAL_DETECTED", "ENGINE",
                new
                {
                    instrument = instTrim,
                    account_qty = accountQtyAbs,
                    journal_qty = journalOpenQtySum,
                    intent_ids = candidateList,
                    registry_owned_working_count = robotWorkingOnInst,
                    broker_order_ids_sample = brokerOrderIds.Take(8).ToList(),
                    repair_action = "NONE",
                    repair_result = resultCode,
                    reason = "TAGGED_ORPHAN_POSITION_RECOVERY"
                }));
            RecordTaggedRepairFailureForCooldown(instTrim, accountQtyAbs, journalOpenQtySum, utcNow, resultCode);
            return false;
        }

        var (_, _, _, intentStop, intentTarget, intentDir, _) = GetIntentInfo(bestId);
        var useDir = string.IsNullOrEmpty(intentDir) ? direction : intentDir!;
        var brokerOid = OrderMap.TryGetValue(bestId, out var oMap) ? oMap.OrderId : null;

        _log.Write(RobotEvents.EngineBase(utcNow, bestIntent.TradingDate ?? "", "TAGGED_BROKER_WITHOUT_JOURNAL_DETECTED", "ENGINE",
            new
            {
                instrument = instTrim,
                account_qty = accountQtyAbs,
                journal_qty = journalOpenQtySum,
                intent_ids = candidateList,
                chosen_intent_id = bestId,
                registry_owned_working_count = robotWorkingOnInst,
                broker_order_ids_sample = brokerOrderIds.Take(8).ToList(),
                repair_action = "TAGGED_BROKER_EXPOSURE_RECOVERY_JOURNAL_UPSERT",
                repair_result = "ATTEMPTING",
                reason = "TAGGED_ORPHAN_POSITION_RECOVERY"
            }));

        var correl = $"TAGGED_RECOVERY:{instTrim}:{utcNow:yyyyMMddHHmmssfff}";
        _executionJournal.UpsertTaggedBrokerExposureRecoveryJournal(
            bestId,
            bestIntent.TradingDate!,
            bestIntent.Stream!,
            bestIntent.ExecutionInstrument ?? bestIntent.Instrument ?? instTrim,
            brokerOid,
            repairQty,
            useDir,
            avgFill,
            intentStop,
            intentTarget,
            utcNow,
            correl);

        ClearTaggedRepairFailureCooldown(instTrim);
        resultCode = "REPAIR_UPSERT_OK";
        detail = bestId;
        _log.Write(RobotEvents.EngineBase(utcNow, bestIntent.TradingDate ?? "", "TAGGED_BROKER_WITHOUT_JOURNAL_REPAIR_COMPLETE", "ENGINE",
            new
            {
                instrument = instTrim,
                intent_id = bestId,
                open_journal_qty = repairQty,
                correlation_id = correl,
                result = resultCode
            }));
        return true;
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

    /// <summary>Suppress duplicate NT order callbacks: same instrument + order_id + order_state within 50ms.</summary>
    private bool TrySkipDuplicateOrderUpdate50ms(string instrument, string orderId, string orderState, DateTimeOffset utcNow, out long skippedCount)
    {
        skippedCount = 0;
        var inst = string.IsNullOrEmpty(instrument) ? "_" : instrument.Trim();
        var oid = orderId ?? "";
        var key = inst + "|" + oid + "|" + orderState;
        lock (_callbackDedupLock)
        {
            if (_orderCallbackDedup50ms.TryGetValue(key, out var ent))
            {
                if ((utcNow - ent.LastUtc).TotalMilliseconds < 50.0)
                {
                    ent.TotalSkips++;
                    skippedCount = ent.TotalSkips;
                    return true;
                }
                ent.LastUtc = utcNow;
                ent.TotalSkips = 0;
                return false;
            }
            _orderCallbackDedup50ms[key] = new OrderCallbackDedupEntry { LastUtc = utcNow, TotalSkips = 0 };
            return false;
        }
    }

    private static string BuildPermanentExecutionDedupKey(string instrument, string? executionId, string? brokerOrderId, int fillQty)
    {
        var inst = string.IsNullOrEmpty(instrument) ? "_" : instrument.Trim();
        var q = fillQty.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(executionId))
            return inst + "|" + executionId.Trim() + "|q:" + q;
        var oid = brokerOrderId ?? "";
        return inst + "|noid:" + oid + "|q:" + q;
    }

    /// <summary>First occurrence of <paramref name="dedupKey"/> returns true (process). Repeats return false (skip) and bump skip counter.</summary>
    private bool TryMarkFirstPermanentExecutionProcessing(string dedupKey, out long skippedCount)
    {
        skippedCount = 0;
        if (_permanentExecutionProcessed.TryAdd(dedupKey, 0))
            return true;
        skippedCount = _permanentExecutionDedupSkipCount.AddOrUpdate(dedupKey, 1L, static (_, v) => v + 1);
        return false;
    }
}

// Emergency handler accessor (delegates to base class method)
partial class NinjaTraderSimAdapter
{
    // TriggerQuantityEmergency is defined in NinjaTraderSimAdapter.cs
    // This partial class declaration allows NT-specific code to call it
}

#endif
