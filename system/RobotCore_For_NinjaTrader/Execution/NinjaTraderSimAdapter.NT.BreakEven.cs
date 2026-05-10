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
                stopOrder = SnapshotAccountOrders(account).FirstOrDefault(o =>
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
        var journalPath = System.IO.Path.Combine(RobotRunArtifactPaths.StateExecutionJournals(_stateRoot), $"{pending.TradingDate}_{pending.Stream}_{pending.IntentId}.json");
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
            var stopOrder = SnapshotAccountOrders(account).FirstOrDefault(o =>
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
            var targetOrder = SnapshotAccountOrders(account).FirstOrDefault(o =>
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
                execution_instrument_key = executionInstrumentKey,
                note = "OCO Change() reverts - using cancel+replace for BE"
            }));

            var beReplaceExecutionKey = (executionInstrumentKey ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(beReplaceExecutionKey) &&
                !string.Equals(beReplaceExecutionKey, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            {
                QuantExecutionControlStore.NotifyProtectiveCancelReplacePending(beReplaceExecutionKey, 2, utcNow);
            }
            var beReplaceLogicalInstrument = (instrument ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(beReplaceLogicalInstrument) &&
                !string.Equals(beReplaceLogicalInstrument, beReplaceExecutionKey, StringComparison.OrdinalIgnoreCase))
            {
                QuantExecutionControlStore.NotifyProtectiveCancelReplacePending(beReplaceLogicalInstrument, 2, utcNow);
            }

            // 1. Cancel both OCO orders
            CancelProtectiveOrdersForIntent(intentId, utcNow);

            // 2. Submit new stop at BE price
            var stopResult = SubmitProtectiveStop(intentId, instrument, direction, beStopQuantized, quantity, newOcoGroup, utcNow);
            if (!stopResult.Success)
            {
                var flattenQueued = TryEnqueueEmergencyFlattenProtective(instrument, utcNow);
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "BE_REPLACE_STOP_FAIL", new
                {
                    error = stopResult.ErrorMessage,
                    emergency_flatten_queued = flattenQueued,
                    note = "BE replace: stop submit failed after cancel; emergency flatten queued because the position may be unprotected."
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


}

#endif
