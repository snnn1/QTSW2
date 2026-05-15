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
    /// STEP 3: Handle real NT ExecutionUpdate event.
    /// Called from public HandleExecutionUpdate() method.
    /// </summary>
    private void HandleExecutionUpdateSnapshot(ExecutionUpdateSnapshot snapshot)
    {
        if (snapshot == null) return;

        var utcNow = DateTimeOffset.UtcNow;
        var encodedTag = snapshot.EncodedTag ?? "";
        var parsedTag = RobotOrderIds.ParseTag(encodedTag);
        var intentId = snapshot.IntentId;
        if (string.IsNullOrWhiteSpace(intentId))
            intentId = RobotOrderIds.DecodeIntentId(encodedTag) ?? parsedTag.IntentId ?? "";

        var instrument = string.IsNullOrWhiteSpace(snapshot.Instrument)
            ? (_iea?.ExecutionInstrumentKey ?? "UNKNOWN")
            : snapshot.Instrument;
        var fillPrice = snapshot.FillPrice;
        var fillQuantity = snapshot.FillQuantity;
        var orderTypeFromTag = snapshot.OrderTypeFromTag ?? parsedTag.Leg;
        var isProtectiveOrder = snapshot.IsProtectiveOrder ||
            string.Equals(orderTypeFromTag, "STOP", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(orderTypeFromTag, "TARGET", StringComparison.OrdinalIgnoreCase);

        if (snapshot.IsRobotFlattenOrder && fillQuantity != 0)
        {
            _iea?.OnFlattenFillReceived(
                instrument,
                snapshot.BrokerOrderId,
                utcNow,
                RobotOrderIds.DecodeFlattenRequestId(encodedTag));
            _log.Write(RobotEvents.ExecutionBase(utcNow, "FLATTEN", instrument, "FLATTEN_EXECUTION_RECOVERED", new
            {
                broker_order_id = snapshot.BrokerOrderId,
                execution_id = snapshot.ExecutionId,
                reason = "snapshot_tag_matched"
            }));
            EnqueueBrokerFlattenFillPostFill(snapshot, instrument, fillPrice, fillQuantity, utcNow, snapshot.BrokerOrderId,
                TryResolveFlattenRegistryEntry(snapshot.BrokerOrderId), runFlatCheck: true);
            return;
        }

        if (string.IsNullOrEmpty(intentId))
        {
            if (TryRecognizeSelfInitiatedFlattenCloseFill(instrument, utcNow))
            {
                EnqueueBrokerFlattenFillPostFill(snapshot, instrument, fillPrice, fillQuantity, utcNow, snapshot.BrokerOrderId,
                    TryResolveFlattenRegistryEntry(snapshot.BrokerOrderId), runFlatCheck: true);
                return;
            }

            EmitUnmappedFill(instrument, "UNTrackED_TAG", fillPrice, fillQuantity, utcNow, snapshot.BrokerOrderId, null, tag: encodedTag);
            LogCriticalWithIeaContext(utcNow, "", instrument, "EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL",
                new
                {
                    error = "Execution update snapshot received for order with missing/invalid tag - position may exist but is untracked",
                    broker_order_id = snapshot.BrokerOrderId,
                    execution_id = snapshot.ExecutionId,
                    order_tag = encodedTag,
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    instrument,
                    action = "FLATTEN_ENQUEUED",
                    account_name = snapshot.AccountName
                });
            var cid = $"UNTrackED_FILL:{instrument}:{utcNow:yyyyMMddHHmmssfff}";
            EnqueueNtActionInternal(new NtFlattenInstrumentCommand(cid, null, instrument, "UNTrackED_FILL", utcNow,
                DestructiveActionSource.FAIL_CLOSED, DestructiveTriggerReason.FAIL_CLOSED));
            return;
        }

        OrderInfo? orderInfo = null;
        if (_useInstrumentExecutionAuthority && _iea != null &&
            _iea.TryResolveForExecutionUpdate(snapshot.BrokerOrderId, intentId, parsedTag.Leg, out var regEntry, out var resolutionPath))
        {
            orderInfo = regEntry!.OrderInfo;
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "ORDER_REGISTRY_EXEC_RESOLVED", new
            {
                broker_order_id = snapshot.BrokerOrderId,
                intent_id = intentId,
                resolution_path = resolutionPath,
                order_role = regEntry.OrderRole.ToString(),
                ownership_status = regEntry.OwnershipStatus.ToString(),
                flatten_original_intent_id = regEntry.FlattenOriginalIntentId,
                flatten_request_id = regEntry.FlattenRequestId,
                flatten_reason = regEntry.FlattenReason,
                snapshot_path = true
            }));

            IeExecutionLatencyTrace.WriteResolved("ORDER_REGISTRY_EXEC_RESOLVED",
                snapshot.BrokerOrderId, intentId, orderInfo.Instrument, _iea?.InstanceId.ToString(), fillQuantity);
        }

        if (orderInfo == null && !OrderMap.TryGetValue(intentId, out orderInfo))
        {
            if (isProtectiveOrder && !string.IsNullOrEmpty(orderTypeFromTag) && IntentMap.TryGetValue(intentId, out var intent))
            {
                orderInfo = new OrderInfo
                {
                    IntentId = intentId,
                    Instrument = intent.Instrument ?? instrument,
                    OrderId = snapshot.BrokerOrderId,
                    OrderType = orderTypeFromTag,
                    Direction = intent.Direction ?? "",
                    Quantity = snapshot.OrderQuantity,
                    Price = string.Equals(orderTypeFromTag, "STOP", StringComparison.OrdinalIgnoreCase)
                        ? snapshot.StopPrice
                        : snapshot.LimitPrice,
                    State = string.IsNullOrWhiteSpace(snapshot.OrderState) ? "SUBMITTED" : snapshot.OrderState,
                    IsEntryOrder = false,
                    FilledQuantity = 0
                };

                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "PROTECTIVE_ORDER_FILL_TRACKED_FROM_TAG",
                    new
                    {
                        broker_order_id = snapshot.BrokerOrderId,
                        execution_id = snapshot.ExecutionId,
                        tag = encodedTag,
                        order_type = orderTypeFromTag,
                        intent_id = intentId,
                        fill_price = fillPrice,
                        fill_quantity = fillQuantity,
                        snapshot_path = true,
                        note = "Protective order fill tracked from immutable ExecutionUpdate snapshot."
                    }));
            }
        }

        var record = new UnresolvedExecutionRecord(snapshot, intentId, instrument, encodedTag,
            fillPrice, fillQuantity, snapshot.OrderState, isProtectiveOrder, orderTypeFromTag, utcNow,
            snapshot.ExecutionId, snapshot.BrokerOrderId);

        if (orderInfo == null)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_DEFERRED_FOR_REGISTRY_RESOLUTION",
                new
                {
                    account_name = snapshot.AccountName,
                    execution_instrument_key = snapshot.ExecutionInstrumentKey,
                    intent_id = intentId,
                    execution_id = snapshot.ExecutionId,
                    broker_order_id = snapshot.BrokerOrderId,
                    elapsed_ms = 0,
                    retry_count = 0,
                    snapshot_path = true
                }));
            _iea?.IncrementDeferredExecutionCount();
            DeferUnresolvedExecution(record);
            return;
        }

        ProcessExecutionUpdateContinuation(record, orderInfo);
    }

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

            // Gap 3 / Step 5: Non-IEA dedupe. IEA dedupe runs before worker enqueue.
            bool isDuplicate = false;
            if (!(_useInstrumentExecutionAuthority && _iea != null))
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
            _iea.OnFlattenFillReceived(
                instrumentKey,
                order.OrderId,
                utcNow,
                RobotOrderIds.DecodeFlattenRequestId(encodedTag));
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
            EnqueueBrokerFlattenFillPostFill(execution, instrumentFlatten, fillPrice, fillQuantity, utcNow, order.OrderId, order,
                TryResolveFlattenRegistryEntry(order.OrderId), runFlatCheck: true);
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

            if (TryRecognizeSelfInitiatedFlattenCloseFill(instrument, utcNow))
            {
                EnqueueBrokerFlattenFillPostFill(execution, instrument, fillPrice, fillQuantity, utcNow, order.OrderId, order,
                    TryResolveFlattenRegistryEntry(order.OrderId), runFlatCheck: true);
                return;
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
                ownership_status = regEntry.OwnershipStatus.ToString(),
                flatten_original_intent_id = regEntry.FlattenOriginalIntentId,
                flatten_request_id = regEntry.FlattenRequestId,
                flatten_reason = regEntry.FlattenReason
            }));
            var fillQtyResolved = 0;
            try
            {
                fillQtyResolved = (int)fillQuantity;
            }
            catch
            {
                /* ignore */
            }

            IeExecutionLatencyTrace.WriteResolved("ORDER_REGISTRY_EXEC_RESOLVED",
                order.OrderId?.ToString() ?? "", intentId ?? "", orderInfo.Instrument, _iea?.InstanceId.ToString(), fillQtyResolved);
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
                        string? brokerExecIdAdopt = null;
                        try { brokerExecIdAdopt = execution.ExecutionId as string; } catch { }
                        var fillQtyAdopt = (int)fillQuantity;
                        var adoptParityBase = ComputeFillGroupId(brokerExecIdAdopt, brokerOrderIdStr, brokerOrderIdStr,
                            utcNow.ToString("o"), fillPrice, fillQtyAdopt);
                        var adoptParityKey = adoptParityBase + "|" + context.IntentId;
                        var adoptSign = string.Equals(context.Direction, "Short", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
                        JournalParityPendingLedger.TryRecordTrustedFill(context.ExecutionInstrument.Trim(), adoptParityKey,
                            adoptSign * fillQtyAdopt, context.IntentId, utcNow);
                        var isLongAdopt = !string.Equals(context.Direction, "Short", StringComparison.OrdinalIgnoreCase);
                        _pendingFillBridge?.RecordEntryFillObserved(
                            instrumentForResolve.Trim(),
                            fillQtyAdopt,
                            isLongAdopt,
                            utcNow,
                            context.IntentId,
                            brokerOrderIdStr);
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
                            brokerOrderInstrumentKey: instrumentForResolve,
                            parityPendingDedupeKey: adoptParityKey);
                        if (FeatureFlags.CanonicalOwnershipLedgerEnabled && _ownershipLedger != null)
                        {
                            var dir = string.Equals(context.Direction, "Short", StringComparison.OrdinalIgnoreCase)
                                ? SlotDirection.Short : SlotDirection.Long;
                            _ownershipLedger.RecordMappedEntryFill(
                                GetLedgerAccountName(), context.ExecutionInstrument.Trim(),
                                context.IntentId, context.Stream, dir, fillQtyAdopt, utcNow, 0);
                        }
                        _log.Write(RobotEvents.ExecutionBase(utcNow, adoptedRecord.IntentId, instrumentForResolve, "ADOPTED_ORDER_FILL_JOURNALED", new
                        {
                            broker_order_id = brokerOrderIdStr,
                            intent_id = adoptedRecord.IntentId,
                            fill_price = fillPrice,
                            fill_quantity = fillQuantity,
                            trading_date = tradingDate,
                            stream = stream
                        }));
                        var adoptQtyInt = (int)fillQuantity;
                        var adoptPartial = order != null && adoptQtyInt < order.Quantity;
                        TryAppendKeyEventEntryFilled(utcNow, instrumentForResolve, stream, adoptedRecord.IntentId, tradingDate,
                            adoptQtyInt, fillPrice, brokerOrderIdStr, adoptPartial);
                        var exposureInstrument = !string.IsNullOrWhiteSpace(context.ExecutionInstrument)
                            ? context.ExecutionInstrument
                            : context.CanonicalInstrument;
                        _coordinator?.OnEntryFill(adoptedRecord.IntentId, fillQuantity, stream, exposureInstrument ?? "", context.Direction ?? "", utcNow);
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
        var snapshot = record.Snapshot;
        var order = record.Order as Order;
        var execution = record.Execution;
        var utcNow = DateTimeOffset.UtcNow;
        var execInstKey = !string.IsNullOrWhiteSpace(snapshot?.ExecutionInstrumentKey)
            ? snapshot!.ExecutionInstrumentKey
            : (_iea?.ExecutionInstrumentKey ?? (order != null ? ExecutionUpdateRouter.GetExecutionInstrumentKeyFromOrder(order.Instrument) : ""));
        var accountName = !string.IsNullOrWhiteSpace(snapshot?.AccountName)
            ? snapshot!.AccountName
            : (_iea?.AccountName ?? (order != null ? ExecutionUpdateRouter.GetAccountNameFromOrder(order) : ""));
        var brokerOrderId = snapshot?.BrokerOrderId ?? order?.OrderId?.ToString() ?? "";
        var orderIdInternal = brokerOrderId; // NT: use OrderId as internal correlation key
        var executionSeq = GetNextExecutionSequence(execInstKey, accountName);
        string? brokerExecId = snapshot?.ExecutionId;
        if (string.IsNullOrWhiteSpace(brokerExecId))
        {
            try { dynamic d = execution; brokerExecId = d.ExecutionId as string; } catch { }
        }
        var fillGroupId = ComputeFillGroupId(brokerExecId, orderIdInternal, brokerOrderId, utcNow.ToString("o"), fillPrice, fillQuantity);
        var deferEntryFillFollowUp = _iea != null && !isProtectiveOrder && orderInfo.IsEntryOrder == true;
        var followUpEnqueueUtc = DateTimeOffset.MinValue;
        var finalFlatCheckDeferred = false;
        var syncPathSw = Stopwatch.StartNew();
        long syncJournalMs = 0;
        var deferredOwnershipActions = new List<Action>();
        var deferredCoordinatorActions = new List<Action>();
        var deferredProtectiveActions = new List<Action>();
        var deferredRepairActions = new List<Action>();

        var instFill = orderInfo.Instrument.Trim();
        var ordStateFill = snapshot?.OrderState ?? order?.OrderState.ToString() ?? "";
        _executionTrace?.WriteExecutionTrace(utcNow, "Fill", "raw_callback", instFill, intentId, brokerOrderId,
            brokerExecId ?? "", fillQuantity, ordStateFill);

        void InvokeMismatchExecutionTrigger()
        {
            _executionTrace?.WriteExecutionTrace(utcNow, "NotifyExecutionTrigger", "before_notify", instFill, intentId,
                brokerOrderId, brokerExecId ?? "", fillQuantity, ordStateFill);
            _onMismatchExecutionTrigger?.Invoke(instFill, utcNow, new MismatchExecutionTriggerDetails
            {
                IntentId = intentId,
                FillDelta = fillQuantity,
                SuppressHardJournalIntegrityActions = true
            });
            _executionTrace?.WriteExecutionTrace(utcNow, "NotifyExecutionTrigger", "after_notify", instFill, intentId,
                brokerOrderId, brokerExecId ?? "", fillQuantity, ordStateFill);
        }

        if (!deferEntryFillFollowUp)
            InvokeMismatchExecutionTrigger();

        var orderTypeForContext = orderTypeFromTag ?? orderInfo.OrderType;

        // Robot flatten orders: tag decodes to pseudo intentId "FLATTEN" — skip IntentMap and journal via coordinator path.
        if (IsRobotOwnedFlattenOrder(encodedTag, orderInfo))
        {
            if (snapshot != null)
            {
                var flattenInst = !string.IsNullOrWhiteSpace(snapshot.Instrument)
                    ? snapshot.Instrument
                    : (orderInfo.Instrument?.Trim() ?? "UNKNOWN");
                EnqueueBrokerFlattenFillPostFill(snapshot, flattenInst, fillPrice, fillQuantity, utcNow, snapshot.BrokerOrderId,
                    TryResolveFlattenRegistryEntry(snapshot.BrokerOrderId), runFlatCheck: true);
                return;
            }

            if (order != null)
            {
                var flattenInst = order.Instrument?.MasterInstrument?.Name ?? orderInfo.Instrument?.Trim() ?? "UNKNOWN";
                EnqueueBrokerFlattenFillPostFill(execution, flattenInst, fillPrice, fillQuantity, utcNow, order.OrderId, order,
                    TryResolveFlattenRegistryEntry(order.OrderId), runFlatCheck: true);
                return;
            }
        }

        // CRITICAL: Resolve intent context before any journal call
        IntentContext context;
        if (!ResolveIntentContextOrFailClosed(intentId, encodedTag, orderTypeForContext, orderInfo.Instrument,
            fillPrice, fillQuantity, utcNow, out context, order))
        {
            // Context resolution failed - orphan fill logged and execution blocked
            // Do NOT call journal with empty strings
            if (deferEntryFillFollowUp)
                InvokeMismatchExecutionTrigger();
            return; // Fail-closed
        }

        // UNIFY FILL EVENTS: trading_date must be non-null for PnL/accounting integrity
        if (string.IsNullOrWhiteSpace(context.TradingDate))
        {
            EmitUnmappedFill(orderInfo.Instrument, "TRADING_DATE_NULL", fillPrice, fillQuantity, utcNow, brokerOrderId, order, tag: encodedTag);
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
            if (deferEntryFillFollowUp)
                InvokeMismatchExecutionTrigger();
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
            var policySource = string.IsNullOrWhiteSpace(orderInfo.PolicySource) ? "UNKNOWN" : orderInfo.PolicySource;
            if (expectedQty <= 0 && orderInfo.Quantity > 0)
            {
                expectedQty = orderInfo.Quantity;
                maxQty = maxQty > 0 ? maxQty : orderInfo.Quantity;
                policySource = "ORDER_INFO_FILL_FALLBACK";
                orderInfo.ExpectedQuantity = expectedQty;
                orderInfo.MaxQuantity = maxQty;
                orderInfo.PolicySource = policySource;
                orderInfo.CanonicalInstrument = string.IsNullOrWhiteSpace(orderInfo.CanonicalInstrument)
                    ? context.CanonicalInstrument
                    : orderInfo.CanonicalInstrument;
                orderInfo.ExecutionInstrument = string.IsNullOrWhiteSpace(orderInfo.ExecutionInstrument)
                    ? context.ExecutionInstrument
                    : orderInfo.ExecutionInstrument;

                if (!string.IsNullOrWhiteSpace(intentId))
                {
                    IntentPolicy[intentId] = new IntentPolicyExpectation
                    {
                        ExpectedQuantity = expectedQty,
                        MaxQuantity = maxQty,
                        PolicySource = policySource,
                        CanonicalInstrument = orderInfo.CanonicalInstrument,
                        ExecutionInstrument = orderInfo.ExecutionInstrument
                    };
                }

                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument,
                    "INTENT_POLICY_RESTORED_FROM_ORDER_INFO", new
                    {
                        intent_id = intentId,
                        broker_order_id = order.OrderId ?? "",
                        order_qty = orderInfo.Quantity,
                        expected_qty = expectedQty,
                        max_qty = maxQty,
                        policy_source = policySource,
                        reason = "Entry fill order row had broker quantity but no expected quantity policy."
                    }));
            }
            else if (expectedQty > 0 && maxQty <= 0)
            {
                maxQty = expectedQty;
                orderInfo.MaxQuantity = maxQty;
            }

            var quantityPolicyMissing = expectedQty <= 0 || maxQty <= 0;
            var remainingQty = expectedQty > 0 ? expectedQty - filledTotal : -filledTotal;
            var overfill = expectedQty > 0 && filledTotal > expectedQty;

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument,
                "INTENT_FILL_UPDATE", new
                {
                    intent_id = intentId,
                    fill_qty = fillQuantity,
                    cumulative_filled_qty = filledTotal,
                    expected_qty = expectedQty,
                    max_qty = maxQty,
                    remaining_qty = remainingQty,
                    quantity_policy_missing = quantityPolicyMissing,
                    policy_source = policySource,
                    overfill = overfill
                }));

            if (quantityPolicyMissing)
            {
                TriggerQuantityEmergency(intentId, "INTENT_QUANTITY_POLICY_MISSING", utcNow, new Dictionary<string, object>
                {
                    { "expected_qty", expectedQty },
                    { "actual_filled_qty", filledTotal },
                    { "last_fill_qty", fillQuantity },
                    { "order_qty", orderInfo.Quantity },
                    { "reason", "Entry fill has no expected quantity policy; journal the fill but block new risk." }
                });
            }
            else if (overfill)
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
                var parityKeyAlloc = fillGroupId + "|" + allocIntentId;
                var entrySignAlloc = string.Equals(allocContext.Direction, "Short", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
                JournalParityPendingLedger.TryRecordTrustedFill(allocContext.ExecutionInstrument.Trim(), parityKeyAlloc,
                    entrySignAlloc * allocQty, allocIntentId, utcNow);
                var isLongEntry = !string.Equals(allocContext.Direction, "Short", StringComparison.OrdinalIgnoreCase);
                _pendingFillBridge?.RecordEntryFillObserved(
                    orderInfo.Instrument.Trim(),
                    allocQty,
                    isLongEntry,
                    utcNow,
                    allocContext.IntentId,
                    brokerOrderId);
                var journalSw = Stopwatch.StartNew();
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
                    brokerOrderInstrumentKey: orderInfo.Instrument,
                    parityPendingDedupeKey: parityKeyAlloc);
                syncJournalMs += journalSw.ElapsedMilliseconds;
                if (FeatureFlags.CanonicalOwnershipLedgerEnabled && _ownershipLedger != null)
                {
                    var dir = string.Equals(allocContext.Direction, "Short", StringComparison.OrdinalIgnoreCase)
                        ? SlotDirection.Short : SlotDirection.Long;
                    if (deferEntryFillFollowUp)
                    {
                        deferredOwnershipActions.Add(() => _ownershipLedger.RecordMappedEntryFill(
                            GetLedgerAccountName(), allocContext.ExecutionInstrument.Trim(),
                            allocContext.IntentId, allocContext.Stream, dir, allocQty, utcNow, executionSeq));
                    }
                    else
                    {
                        _ownershipLedger.RecordMappedEntryFill(
                            GetLedgerAccountName(), allocContext.ExecutionInstrument.Trim(),
                            allocContext.IntentId, allocContext.Stream, dir, allocQty, utcNow, executionSeq);
                    }
                }
                entryFillJournalRecordedQty += allocQty;
                var exposureInstrument = !string.IsNullOrWhiteSpace(allocContext.ExecutionInstrument)
                    ? allocContext.ExecutionInstrument
                    : (allocIntent.ExecutionInstrument ?? allocIntent.Instrument ?? allocContext.CanonicalInstrument);
                if (deferEntryFillFollowUp)
                    deferredCoordinatorActions.Add(() => _coordinator?.OnEntryFill(allocContext.IntentId, allocQty, allocContext.Stream, exposureInstrument, allocContext.Direction ?? "", utcNow));
                else
                    _coordinator?.OnEntryFill(allocContext.IntentId, allocQty, allocContext.Stream, exposureInstrument, allocContext.Direction ?? "", utcNow);
            }

            if (isEntryFill && fillQuantity > 0 && entryFillJournalRecordedQty == 0 && allocations.Count > 0)
            {
                if (deferEntryFillFollowUp)
                {
                    deferredRepairActions.Add(() =>
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
                                deferred_stage = "repair",
                                note = "Tagged broker-without-journal repair deferred into its own post-fill stage."
                            }));
                        if (posAbs > 0 &&
                            SumOpenJournalForInstrument(orderInfo.Instrument.Trim(), _iea?.ExecutionInstrumentKey ?? orderInfo.Instrument.Trim()) == 0)
                        {
                            _ = TryRepairTaggedBrokerWithoutJournalCore(orderInfo.Instrument.Trim(), posAbs, 0, utcNow, out _, out _);
                        }
                    });
                }
                else
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
                        broker_order_id = brokerOrderId,
                        note = "Deterministic allocation in lexicographic order for partial fills"
                    }));
            }
            
            // P1: Emit one EXECUTION_FILLED/PARTIAL per intent when aggregated; single when not
            var isPartial = filledTotal < orderInfo.Quantity;
            var eventType = isPartial ? "EXECUTION_PARTIAL_FILL" : "EXECUTION_FILLED";
            int ResolveReentryExpectedQuantity(string reentryIntentId, int cumulativeForIntent)
            {
                if (!string.IsNullOrWhiteSpace(reentryIntentId) &&
                    IntentPolicy.TryGetValue(reentryIntentId, out var policy) &&
                    policy.ExpectedQuantity > 0)
                    return policy.ExpectedQuantity;

                var aggregateCount = orderInfo.AggregatedIntentIds?.Count ?? 0;
                if (aggregateCount > 1 && orderInfo.ExpectedQuantity > 0)
                    return Math.Max(1, orderInfo.ExpectedQuantity / aggregateCount);

                if (orderInfo.ExpectedQuantity > 0)
                    return orderInfo.ExpectedQuantity;
                if (orderInfo.Quantity > 0)
                    return orderInfo.Quantity;
                return Math.Max(1, cumulativeForIntent);
            }

            void InvokeReentryFillCallbackAfterFullFill(string reentryIntentId, int cumulativeForIntent, int lastFillQty)
            {
                var expectedForIntent = ResolveReentryExpectedQuantity(reentryIntentId, cumulativeForIntent);
                if (ShouldDeferReentryProtectionForPartialFill(cumulativeForIntent, expectedForIntent))
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, reentryIntentId, orderInfo.Instrument, "REENTRY_PROTECTION_DEFERRED_PARTIAL_FILL",
                        new
                        {
                            intent_id = reentryIntentId,
                            last_fill_qty = lastFillQty,
                            cumulative_filled_qty = cumulativeForIntent,
                            expected_qty = expectedForIntent,
                            reason = "partial_reentry_fill_waiting_for_full_quantity",
                            protection_action = "defer"
                        }));
                    return;
                }

                _onReentryFillCallback?.Invoke(reentryIntentId, utcNow);
            }
            if (!isPartial)
            {
                orderInfo.State = "FILLED";
                var legLc = LegTagFromOrderTypeForIeResolution(orderTypeFromTag);
                var incomingOid = brokerOrderId;
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
                TryAppendKeyEventEntryFilled(utcNow, orderInfo.Instrument ?? "",
                    string.IsNullOrEmpty(allocStream) ? null : allocStream,
                    allocIntentId, allocTradingDate, allocFillQty, fillPrice, brokerOrderId, isPartial);
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
                var didImmediateFlatCheck = false;
                foreach (var alloc in allocations)
                {
                    var allocIntentId = alloc.Item1;
                    var allocQty = alloc.Item2;
                    if (allocQty <= 0) continue;
                    if (!IntentMap.TryGetValue(allocIntentId, out var allocIntent) || allocIntent == null) continue;
                    var cumulativeForIntent = (orderInfo.AggregatedFilledByIntent != null && orderInfo.AggregatedFilledByIntent.TryGetValue(allocIntentId, out var cum))
                        ? cum
                        : allocQty;
                    if (string.Equals(allocIntent.TriggerReason, "SUBMIT_MARKET_REENTRY", StringComparison.OrdinalIgnoreCase))
                    {
                        InvokeReentryFillCallbackAfterFullFill(allocIntentId, cumulativeForIntent, allocQty);
                    }
                    else if (deferEntryFillFollowUp && _iea != null)
                        deferredProtectiveActions.Add(() => _iea.HandleEntryFill(allocIntentId, allocIntent, fillPrice, allocQty, cumulativeForIntent, utcNow));
                    else if (_useInstrumentExecutionAuthority && _iea != null)
                    {
                        if (!didImmediateFlatCheck)
                        {
                            CheckAndCancelEntryStopsOnPositionFlat(orderInfo.Instrument, utcNow);
                            didImmediateFlatCheck = true;
                        }
                        _iea.HandleEntryFill(allocIntentId, allocIntent, fillPrice, allocQty, cumulativeForIntent, utcNow);
                    }
                    else
                    {
                        if (!didImmediateFlatCheck)
                        {
                            CheckAndCancelEntryStopsOnPositionFlat(orderInfo.Instrument, utcNow);
                            didImmediateFlatCheck = true;
                        }
                        HandleEntryFill(allocIntentId, allocIntent, fillPrice, allocQty, cumulativeForIntent, utcNow);
                    }
                }
            }
            else if (IntentMap.TryGetValue(intentId, out var entryIntent))
            {
                // Non-aggregated: coordinator OnEntryFill is already invoked once per allocation in the loop above (canonical instrument).

                if (string.Equals(entryIntent.TriggerReason, "SUBMIT_MARKET_REENTRY", StringComparison.OrdinalIgnoreCase))
                {
                    InvokeReentryFillCallbackAfterFullFill(intentId, filledTotal, fillQuantity);
                }
                else if (deferEntryFillFollowUp && _iea != null)
                    deferredProtectiveActions.Add(() => _iea.HandleEntryFill(intentId, entryIntent, fillPrice, fillQuantity, filledTotal, utcNow));
                else if (_useInstrumentExecutionAuthority && _iea != null)
                {
                    CheckAndCancelEntryStopsOnPositionFlat(orderInfo.Instrument, utcNow);
                    _iea.HandleEntryFill(intentId, entryIntent, fillPrice, fillQuantity, filledTotal, utcNow);
                }
                else
                {
                    CheckAndCancelEntryStopsOnPositionFlat(orderInfo.Instrument, utcNow);
                    HandleEntryFill(intentId, entryIntent, fillPrice, fillQuantity, filledTotal, utcNow);
                }
            }
            else
            {
                // This should not happen - ResolveIntentContextOrFailClosed already checked
                // But handle defensively - emit unmapped fill before flatten
                EmitUnmappedFill(orderInfo.Instrument, "INTENT_NOT_FOUND", fillPrice, fillQuantity, utcNow, brokerOrderId, order, tag: encodedTag);
                _log.Write(RobotEvents.EngineBase(utcNow, context.TradingDate, "EXECUTION_ERROR", "ENGINE",
                    new 
                    { 
                        error = "Intent not found in IntentMap after context resolution - defensive check",
                        intent_id = intentId,
                        fill_price = fillPrice,
                        fill_quantity = filledTotal,
                        order_type = orderInfo.OrderType,
                        broker_order_id = brokerOrderId,
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

            var syncTotalMs = syncPathSw.ElapsedMilliseconds;
            if (syncTotalMs >= 25 || syncJournalMs >= 10)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_UPDATE_SYNC_PATH_TIMING", new
                {
                    intent_id = intentId,
                    fill_quantity = fillQuantity,
                    allocations_count = allocations.Count,
                    total_ms = syncTotalMs,
                    journal_ms = syncJournalMs,
                    ownership_ms = 0,
                    coordinator_ms = 0,
                    deferred_follow_up = deferEntryFillFollowUp,
                    note = "Synchronous ExecutionUpdate work kept to fill mapping, journal write, and lightweight fill accounting. Ownership, coordination, mismatch, and protective work are deferred."
                }));
            }

            if (deferEntryFillFollowUp && _iea != null)
            {
                followUpEnqueueUtc = DateTimeOffset.UtcNow;
                var deferredOwnership = deferredOwnershipActions.ToArray();
                var deferredCoordinator = deferredCoordinatorActions.ToArray();
                var deferredProtective = deferredProtectiveActions.ToArray();
                var deferredRepair = deferredRepairActions.ToArray();
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_UPDATE_FOLLOWUP_ENQUEUED", new
                {
                    intent_id = intentId,
                    fill_quantity = fillQuantity,
                    deferred_action_count = deferredOwnership.Length + deferredCoordinator.Length + deferredProtective.Length + deferredRepair.Length + 2,
                    ownership_action_count = deferredOwnership.Length,
                    coordinator_action_count = deferredCoordinator.Length,
                    protective_action_count = deferredProtective.Length,
                    repair_action_count = deferredRepair.Length,
                    mismatch_trigger_deferred = true,
                    flat_check_deferred = true,
                    queued_via = "IEA_WORKER",
                    note = "Post-fill work split into staged queue items so ownership, coordination, protective handling, repair, flat-check, and mismatch trigger are timed independently."
                }));
                void EnqueuePostFillStage(string stageName, string workKind, Action action, int actionCount)
                {
                    var stageEnqueueUtc = DateTimeOffset.UtcNow;
                    _iea.EnqueueRecoveryEssential(() =>
                    {
                        var stageStartUtc = DateTimeOffset.UtcNow;
                        var stageSw = Stopwatch.StartNew();
                        action();
                        var stageTotalMs = stageSw.ElapsedMilliseconds;
                        _log.Write(RobotEvents.ExecutionBase(stageStartUtc, intentId, orderInfo.Instrument, "EXECUTION_UPDATE_POSTFILL_STAGE_TIMING", new
                        {
                            intent_id = intentId,
                            fill_quantity = fillQuantity,
                            stage = stageName,
                            work_kind = workKind,
                            action_count = actionCount,
                            queue_delay_ms = Math.Max(0L, (long)(stageStartUtc - followUpEnqueueUtc).TotalMilliseconds),
                            stage_enqueue_delay_ms = Math.Max(0L, (long)(stageStartUtc - stageEnqueueUtc).TotalMilliseconds),
                            total_ms = stageTotalMs,
                            note = "Deferred post-fill stage timing on the serialized IEA worker. NT-touching and repair-ish work now runs in its own queue stage."
                        }));
                    }, workKind);
                }

                if (deferredOwnership.Length > 0)
                {
                    EnqueuePostFillStage("ownership", "ExecutionUpdatePostFillOwnership", () =>
                    {
                        foreach (var deferredAction in deferredOwnership)
                            deferredAction();
                    }, deferredOwnership.Length);
                }

                if (deferredCoordinator.Length > 0)
                {
                    EnqueuePostFillStage("coordinator", "ExecutionUpdatePostFillCoordinator", () =>
                    {
                        foreach (var deferredAction in deferredCoordinator)
                            deferredAction();
                    }, deferredCoordinator.Length);
                }

                if (deferredProtective.Length > 0)
                {
                    EnqueuePostFillStage("protective", "ExecutionUpdatePostFillProtective", () =>
                    {
                        foreach (var deferredAction in deferredProtective)
                            deferredAction();
                    }, deferredProtective.Length);
                }

                if (deferredRepair.Length > 0)
                {
                    EnqueuePostFillStage("repair", "ExecutionUpdatePostFillRepair", () =>
                    {
                        foreach (var deferredAction in deferredRepair)
                            deferredAction();
                    }, deferredRepair.Length);
                }

                EnqueuePostFillStage("flat_check", "ExecutionUpdatePostFillFlatCheck", () =>
                {
                    var flatCheckEnqueueUtc = DateTimeOffset.UtcNow;
                    EnqueueStrategyThreadDeferredAction(
                        $"ENTRY_FLAT_CHECK:{intentId}:{flatCheckEnqueueUtc:yyyyMMddHHmmssfff}",
                        intentId,
                        orderInfo.Instrument,
                        "EXECUTION_UPDATE_ENTRY_FLAT_CHECK",
                        flatCheckEnqueueUtc,
                        () =>
                        {
                            var flatCheckStartUtc = DateTimeOffset.UtcNow;
                            var flatCheckSw = Stopwatch.StartNew();
                            CheckAndCancelEntryStopsOnPositionFlat(orderInfo.Instrument, flatCheckStartUtc);
                            _log.Write(RobotEvents.ExecutionBase(flatCheckStartUtc, intentId, orderInfo.Instrument, "EXECUTION_UPDATE_POSTFILL_STAGE_TIMING", new
                            {
                                intent_id = intentId,
                                fill_quantity = fillQuantity,
                                broker_order_id = brokerOrderId,
                                stage = "entry_flat_check_strategy_thread",
                                work_kind = "ExecutionUpdatePostFillEntryFlatCheck",
                                action_count = 1,
                                queue_delay_ms = Math.Max(0L, (long)(flatCheckStartUtc - flatCheckEnqueueUtc).TotalMilliseconds),
                                total_ms = flatCheckSw.ElapsedMilliseconds,
                                note = "Entry-fill account/order flat check ran on the strategy thread from immutable ExecutionUpdate facts."
                            }));
                        });
                }, 1);

                EnqueuePostFillStage("mismatch_trigger", "ExecutionUpdatePostFillMismatch", () =>
                {
                    InvokeMismatchExecutionTrigger();
                }, 1);
            }
        }
        else if (orderTypeForContext == "STOP" || orderTypeForContext == "TARGET")
        {
            // STOP/TARGET: do not mutate orderInfo.FilledQuantity (entry cumulative is only updated on the entry path).
            var exitFilledTotalForLog = orderInfo.FilledQuantity + fillQuantity;

            // LATE-FILL PROTECTION: If intent already completed, this is a stale/late fill (race with sibling cancel).
            var tradingDate = context.TradingDate ?? "";
            var stream = context.Stream ?? "";
            var completedEntry = _executionJournal.GetEntry(intentId, tradingDate, stream);
            if (completedEntry != null && completedEntry.TradeCompleted)
            {
                var boundedLateTerminalFill = false;
                var completionAgeMs = -1L;
                if (string.Equals(completedEntry.CompletionReason, CompletionReasons.RECONCILIATION_BROKER_FLAT, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(completedEntry.CompletedAtUtc) &&
                    DateTimeOffset.TryParse(completedEntry.CompletedAtUtc, out var completedAtUtc))
                {
                    completionAgeMs = Math.Max(0L, (long)(utcNow - completedAtUtc).TotalMilliseconds);
                    boundedLateTerminalFill = completionAgeMs <= 5000;
                }

                if (boundedLateTerminalFill)
                {
                    _executionJournal.RecordExitFill(
                        context.IntentId,
                        context.TradingDate,
                        context.Stream,
                        fillPrice,
                        fillQuantity,
                        orderTypeForContext,
                        utcNow);
                    var reconciledEntry = _executionJournal.GetEntry(intentId, tradingDate, stream);

                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "COMPLETED_INTENT_LATE_TERMINAL_FILL_RECONCILED", "ENGINE",
                        new
                        {
                            intent_id = intentId,
                            broker_order_id = brokerOrderId,
                            instrument = orderInfo.Instrument,
                            stream = stream,
                            fill_price = fillPrice,
                            fill_quantity = fillQuantity,
                            side = orderInfo.Direction,
                            exit_order_type = orderTypeForContext,
                            execution_timestamp_utc = utcNow.ToString("o"),
                            prior_completion_reason = completedEntry.CompletionReason,
                            completed_at_utc = completedEntry.CompletedAtUtc,
                            completion_age_ms = completionAgeMs,
                            mapped = true,
                            journal_completion_reason = reconciledEntry?.CompletionReason,
                            journal_exit_avg_fill_price = reconciledEntry?.ExitAvgFillPrice,
                            journal_realized_pnl_gross = reconciledEntry?.RealizedPnLGross,
                            action = reconciledEntry?.RealizedPnLGross.HasValue == true
                                ? "BOUNDED_LATE_TERMINAL_FILL_JOURNAL_UPGRADED"
                                : "BOUNDED_LATE_TERMINAL_FILL_TYPE_RECONCILED",
                            note = "Late terminal fill arrived shortly after reconciliation broker-flat completion; journal upgrade path was invoked without replaying ownership/coordinator side effects."
                        }));
                    return;
                }

                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "COMPLETED_INTENT_RECEIVED_FILL", "ENGINE",
                    new
                    {
                        error = "Fill received for intent already TradeCompleted - stale/late fill on terminal intent",
                        intent_id = intentId,
                        broker_order_id = brokerOrderId,
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
                        $"Fill received for intent {intentId} already TradeCompleted. Broker Order: {brokerOrderId}, Qty: {fillQuantity}, Price: {fillPrice}. Route to reconciliation.",
                        priority: 2);
                }
                return; // Fail-closed: do not process as normal fill
            }

            PruneIntentState(intentId, "exit_fill");
            // Intent exit: purge pending BE (target/stop fill)
            PurgePendingBEForIntent(intentId, utcNow, orderInfo.Instrument, "exit_fill");
            var exitLeg = orderTypeForContext == "STOP" ? "STOP" : orderTypeForContext == "TARGET" ? "TARGET" : null;
            var exitIncoming = brokerOrderId;
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
            if (FeatureFlags.CanonicalOwnershipLedgerEnabled && _ownershipLedger != null)
            {
                _ownershipLedger.RecordMappedExitFill(
                    GetLedgerAccountName(), context.ExecutionInstrument.Trim(),
                    context.IntentId, fillQuantity, utcNow, executionSeq);
            }
            
            // Log exit fill event - UNIFY FILL EVENTS: Use EXECUTION_FILLED (canonical) with order_type for ledger/PnL
            // P1: Enriched payload with execution_instrument_key, side, account, session_class
            // Canonical: execution_sequence, fill_group_id, order_id, broker_order_id, position_effect, mapped
            var exitSide = (context.Direction ?? "").Equals("Long", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY";
            var exitSessionClass = DeriveSessionClass(context.Stream ?? "");
            var exitBrokerOrderId = brokerOrderId;
            var exitOrderIdInternal = exitBrokerOrderId;
            var exitExecutionSeq = executionSeq;
            var exitBrokerExecId = brokerExecId ?? record.ExecutionId;
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

            void EnqueueExitFlatCheck()
            {
                if (_iea != null)
                {
                    var flatCheckEnqueueUtc = DateTimeOffset.UtcNow;
                    EnqueueStrategyThreadDeferredAction(
                        $"EXIT_FLAT_CHECK:{intentId}:{flatCheckEnqueueUtc:yyyyMMddHHmmssfff}",
                        intentId,
                        orderInfo.Instrument,
                        "EXECUTION_UPDATE_EXIT_FLAT_CHECK",
                        flatCheckEnqueueUtc,
                        () =>
                        {
                            var flatCheckStartUtc = DateTimeOffset.UtcNow;
                            var flatCheckSw = Stopwatch.StartNew();
                            CheckAndCancelEntryStopsOnPositionFlat(orderInfo.Instrument, flatCheckStartUtc);
                            CheckAllInstrumentsForFlatPositions(flatCheckStartUtc);
                            _log.Write(RobotEvents.ExecutionBase(flatCheckStartUtc, intentId, orderInfo.Instrument, "EXECUTION_UPDATE_POSTFILL_STAGE_TIMING",
                                new
                                {
                                    intent_id = intentId,
                                    fill_quantity = fillQuantity,
                                    broker_order_id = exitBrokerOrderId,
                                    stage = "exit_flat_check",
                                    work_kind = "ExecutionUpdatePostFillExitFlatCheck",
                                    action_count = 2,
                                    queue_delay_ms = Math.Max(0L, (long)(flatCheckStartUtc - flatCheckEnqueueUtc).TotalMilliseconds),
                                    total_ms = flatCheckSw.ElapsedMilliseconds,
                                    note = "Exit-fill account/order flat checks ran on the strategy thread from immutable ExecutionUpdate facts."
                                }));
                        });
                    return;
                }

                CheckAndCancelEntryStopsOnPositionFlat(orderInfo.Instrument, utcNow);
                CheckAllInstrumentsForFlatPositions(utcNow);
            }

            void RunExitFillFollowUp()
            {
                // CRITICAL FIX: Coordinator accumulates internally, so pass fillQuantity (delta) not exit cumulative
                // OnExitFill does: exposure.ExitFilledQty += qty, so passing cumulative totals causes double-counting
                _coordinator?.OnExitFill(intentId, fillQuantity, utcNow);

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

                    var postExitEntry = _executionJournal.GetEntry(intentId, filledIntent.TradingDate ?? "", filledIntent.Stream ?? "");
                    if (ShouldTerminalizeAfterExitFill(postExitEntry))
                    {
                        // TERMINALIZATION: Cancel remaining protective orders and verify invariant.
                        _iea?.TryTransitionIntentLifecycle(intentId, IntentLifecycleTransition.INTENT_COMPLETED, null, utcNow);
                        TerminalizeIntent(intentId, filledIntent.TradingDate ?? "", filledIntent.Stream ?? "", orderTypeForContext, utcNow);
                    }
                    else
                    {
                        var remainingQty = postExitEntry == null
                            ? (int?)null
                            : Math.Max(0, postExitEntry.EntryFilledQuantityTotal - postExitEntry.ExitFilledQuantityTotal);
                        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "PROTECTIVE_PARTIAL_EXIT_RETAINED",
                            new
                            {
                                intent_id = intentId,
                                stream = context.Stream,
                                exit_order_type = orderTypeForContext,
                                exit_filled_qty = postExitEntry?.ExitFilledQuantityTotal,
                                entry_filled_qty = postExitEntry?.EntryFilledQuantityTotal,
                                remaining_qty = remainingQty,
                                note = "Partial protective exit observed; remaining protection stays managed and intent is not terminalized."
                            }));
                    }
                }

                EnqueueExitFlatCheck();
            }

            if (_iea != null)
            {
                var exitFollowUpEnqueueUtc = DateTimeOffset.UtcNow;
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, "EXECUTION_UPDATE_EXIT_FOLLOWUP_ENQUEUED",
                    new
                    {
                        intent_id = intentId,
                        fill_quantity = fillQuantity,
                        broker_order_id = exitBrokerOrderId,
                        order_type = orderTypeForContext,
                        queued_via = "IEA_WORKER",
                        note = "Exit fill journal/ownership/logging completed; coordinator, cancels, terminal cleanup, and flat checks are deferred into a timed stage."
                    }));
                _iea.EnqueueRecoveryEssential(() =>
                {
                    var stageStartUtc = DateTimeOffset.UtcNow;
                    var stageSw = Stopwatch.StartNew();
                    RunExitFillFollowUp();
                    _log.Write(RobotEvents.ExecutionBase(stageStartUtc, intentId, orderInfo.Instrument, "EXECUTION_UPDATE_POSTFILL_STAGE_TIMING",
                        new
                        {
                            intent_id = intentId,
                            fill_quantity = fillQuantity,
                            broker_order_id = exitBrokerOrderId,
                            stage = "exit_followup",
                            work_kind = "ExecutionUpdatePostFillExitFollowup",
                            action_count = 1,
                            queue_delay_ms = Math.Max(0L, (long)(stageStartUtc - exitFollowUpEnqueueUtc).TotalMilliseconds),
                            total_ms = stageSw.ElapsedMilliseconds,
                            note = "Deferred exit post-fill stage timing; target/stop ExecutionUpdate is kept bounded."
                        }));
                }, "ExecutionUpdatePostFillExitFollowup");
                finalFlatCheckDeferred = true;
            }
            else
            {
                RunExitFillFollowUp();
            }
        }
        else
        {
            // Unknown exit type - orphan it
            LogOrphanFill(intentId, encodedTag, orderInfo.OrderType, orderInfo.Instrument, fillPrice, fillQuantity,
                utcNow, context.Stream, "UNKNOWN_EXIT_TYPE");
            RecordOrphanFillIfEnabled(orderInfo.Instrument, brokerOrderId, intentId,
                fillPrice, fillQuantity, utcNow, OrphanReason.IntentLostAfterContext,
                ParseSlotDirection(context.Direction), OrphanActionTaken.NoAction);
            
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
        if (!finalFlatCheckDeferred)
        {
            if (_iea != null)
            {
                var finalFlatCheckEnqueueUtc = DateTimeOffset.UtcNow;
                EnqueueStrategyThreadDeferredAction(
                    $"EXECUTION_FINAL_FLAT_CHECK:{intentId}:{finalFlatCheckEnqueueUtc:yyyyMMddHHmmssfff}",
                    intentId,
                    orderInfo.Instrument,
                    "EXECUTION_UPDATE_FINAL_FLAT_CHECK",
                    finalFlatCheckEnqueueUtc,
                    () =>
                    {
                        var finalFlatCheckStartUtc = DateTimeOffset.UtcNow;
                        var finalFlatCheckSw = Stopwatch.StartNew();
                        CheckAllInstrumentsForFlatPositions(finalFlatCheckStartUtc);
                        _log.Write(RobotEvents.ExecutionBase(finalFlatCheckStartUtc, intentId, orderInfo.Instrument, "EXECUTION_UPDATE_POSTFILL_STAGE_TIMING",
                            new
                            {
                                intent_id = intentId,
                                fill_quantity = fillQuantity,
                                broker_order_id = brokerOrderId,
                                stage = "final_flat_check",
                                work_kind = "ExecutionUpdateFinalFlatCheck",
                                action_count = 1,
                                queue_delay_ms = Math.Max(0L, (long)(finalFlatCheckStartUtc - finalFlatCheckEnqueueUtc).TotalMilliseconds),
                                total_ms = finalFlatCheckSw.ElapsedMilliseconds,
                                note = "Final ExecutionUpdate flat check ran on the strategy thread."
                            }));
                    });
            }
            else
            {
                CheckAllInstrumentsForFlatPositions(utcNow);
            }
        }
    }

}

#endif
