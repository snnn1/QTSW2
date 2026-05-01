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
    // Note: Rate-limiting fields (_lastInstrumentMismatchLogUtc, INSTRUMENT_MISMATCH_RATE_LIMIT_MINUTES,
    // _lastInstrumentMismatchDiagLogUtc) are defined in the base class file (NinjaTraderSimAdapter.cs)

    /// <summary>
    /// Helper method to safely get order tag/name using dynamic typing.
    /// </summary>
    private static string? GetOrderTag(Order? order)
    {
        var (tag, _) = GetOrderTagWithSource(order);
        return tag;
    }

    /// <summary>
    /// Get best available tag string and which NT field it came from.
    /// NT may use Tag, Name, FromEntrySignal, or SignalName. Log tag_source for diagnostics.
    /// </summary>
    private static (string? tag, string tagSource) GetOrderTagWithSource(Order? order)
    {
        if (order == null)
            return (null, "None");

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

    private bool IsRobotOwnedOrderUpdateRace(string? encodedTag, string? intentId) =>
        (!string.IsNullOrWhiteSpace(intentId) && IntentMap.ContainsKey(intentId.Trim())) ||
        (!string.IsNullOrWhiteSpace(encodedTag) &&
         encodedTag.StartsWith(RobotOrderIds.Prefix, StringComparison.OrdinalIgnoreCase));

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

    private static long SafeReadExecutionTimeTicks(object executionObj)
    {
        try
        {
            dynamic execution = executionObj;
            var time = execution.Time;
            if (time is DateTime dt) return dt.Ticks;
            if (time is DateTimeOffset dto) return dto.UtcTicks;
            try { return ((dynamic)time).Ticks; } catch { return 0; }
        }
        catch { return 0; }
    }

    private void SetPendingEntryTerminationReason(string intentId, string reason)
    {
        if (string.IsNullOrWhiteSpace(intentId) || string.IsNullOrWhiteSpace(reason)) return;
        var r = reason.Trim();
        if (!string.Equals(r, "no_fill", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r, "cancelled", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r, "flattened", StringComparison.OrdinalIgnoreCase))
            return;
        _pendingEntryTerminationReason[intentId.Trim()] = r.ToLowerInvariant();
    }

    private void TryAppendKeyEventEntryFilled(
        DateTimeOffset utcNow,
        string instrument,
        string? stream,
        string intentId,
        string tradingDate,
        int fillQty,
        decimal fillPrice,
        string? brokerOrderId,
        bool isPartial)
    {
        if (_keyEventWriter == null || string.IsNullOrEmpty(intentId)) return;
        _keyEventWriter.AppendKeyEvent(utcNow, "ENTRY_FILLED", instrument?.Trim(),
            string.IsNullOrEmpty(stream) ? null : stream, null,
            new Dictionary<string, object?>
            {
                ["intent_id"] = intentId,
                ["trading_date"] = string.IsNullOrEmpty(tradingDate) ? null : tradingDate,
                ["fill_quantity"] = fillQty,
                ["fill_price"] = fillPrice,
                ["broker_order_id"] = brokerOrderId,
                ["partial"] = isPartial
            });
    }

    private void TryAppendKeyEventEntryTerminated(
        DateTimeOffset utcNow,
        string instrument,
        string? stream,
        string intentId,
        string tradingDate,
        string defaultReason)
    {
        if (_keyEventWriter == null || string.IsNullOrEmpty(intentId)) return;
        if (!_keyEventWriter.TryShouldEmitEntryTerminated(intentId)) return;
        var reason = defaultReason;
        if (_pendingEntryTerminationReason.TryRemove(intentId, out var pending) &&
            (pending == "no_fill" || pending == "cancelled" || pending == "flattened"))
            reason = pending;
        _keyEventWriter.AppendKeyEvent(utcNow, "ENTRY_TERMINATED", instrument?.Trim(),
            string.IsNullOrEmpty(stream) ? null : stream, null,
            new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["intent_id"] = intentId,
                ["trading_date"] = string.IsNullOrEmpty(tradingDate) ? null : tradingDate
            });
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

    OrderSubmissionResult IIEAOrderExecutor.SubmitProtectiveStop(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow) =>
        SubmitProtectiveStop(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);

    OrderSubmissionResult IIEAOrderExecutor.SubmitTargetOrder(string intentId, string instrument, string direction, decimal targetPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow) =>
        SubmitTargetOrder(intentId, instrument, direction, targetPrice, quantity, ocoGroup, utcNow);

    bool IIEAOrderExecutor.CanSubmitExit(string intentId, int quantity) =>
        _coordinator == null || _coordinator.CanSubmitExit(intentId, quantity);

    bool IIEAOrderExecutor.HasWorkingProtectivesForIntent(string intentId)
    {
        var account = _ntAccount as Account;
        var orders = SnapshotAccountOrders(account);
        if (orders.Count == 0) return false;
        var stopTag = RobotOrderIds.EncodeStopTag(intentId);
        var targetTag = RobotOrderIds.EncodeTargetTag(intentId);
        bool hasStop = false, hasTarget = false;
        foreach (Order o in orders)
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
        var orders = SnapshotAccountOrders(account);
        if (orders.Count == 0) return (null, null, null, null);
        var stopTag = RobotOrderIds.EncodeStopTag(intentId);
        var targetTag = RobotOrderIds.EncodeTargetTag(intentId);
        decimal? stopPrice = null, targetPrice = null;
        int? stopQty = null, targetQty = null;
        foreach (Order o in orders)
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

    bool IIEAOrderExecutor.IsExecutionAllowed()
    {
        var recoveryOk = _isRecoveryExecutionAllowedCallback == null || _isRecoveryExecutionAllowedCallback();
        var killOff = _isGlobalKillSwitchActive == null || !_isGlobalKillSwitchActive();
        return recoveryOk && killOff;
    }

    IReadOnlyList<(string intentId, Intent intent, decimal beTriggerPrice, decimal entryPrice, decimal? actualFillPrice, string direction)> IIEAOrderExecutor.GetActiveIntentsForBEMonitoring(string? executionInstrument) =>
        GetActiveIntentsForBEMonitoring(executionInstrument);

    IReadOnlyCollection<string> IIEAOrderExecutor.GetAdoptionCandidateIntentIds(string? executionInstrument) =>
        GetAdoptionCandidateIntentIds(executionInstrument);

    (string JournalDir, int FileCount, bool DirectoryExists) IIEAOrderExecutor.GetJournalDiagnostics(string? executionInstrument) =>
        GetJournalDiagnostics(executionInstrument);

    void IIEAOrderExecutor.EmitRecoveryAdoptionZeroDeltaDiagnostics(
        string executionInstrumentKey,
        string adoptionScanEpisodeId,
        int adoptedDelta,
        bool isRecoveryAdoptionScan,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds) =>
        EmitRecoveryAdoptionZeroDeltaDiagnosticsCore(
            executionInstrumentKey,
            adoptionScanEpisodeId,
            adoptedDelta,
            isRecoveryAdoptionScan,
            registryMismatchTrustedIntentIds);

    private void EmitRecoveryAdoptionZeroDeltaDiagnosticsCore(
        string executionInstrumentKey,
        string adoptionScanEpisodeId,
        int adoptedDelta,
        bool isRecoveryAdoptionScan,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds)
    {
        if (!isRecoveryAdoptionScan || adoptedDelta != 0 || _log == null || string.IsNullOrWhiteSpace(executionInstrumentKey))
            return;
        var execVariant = executionInstrumentKey.StartsWith("M", StringComparison.OrdinalIgnoreCase) && executionInstrumentKey.Length > 1
            ? executionInstrumentKey
            : "M" + executionInstrumentKey;
        var canonical = DeriveCanonicalFromExecutionInstrument(executionInstrumentKey);
        var exposure = GetBrokerCanonicalExposureInternal(executionInstrumentKey);
        var brokerAbs = exposure.ReconciliationAbsQuantityTotal;
        var brokerSigned = 0;
        foreach (var leg in exposure.Legs)
            brokerSigned += leg.SignedQuantity;
        var robotTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var account = _ntAccount as Account;
        var orders = SnapshotAccountOrders(account);
        if (orders.Count > 0)
        {
            var instRoot = (executionInstrumentKey ?? "").Split(' ').FirstOrDefault() ?? executionInstrumentKey;
            foreach (var o in orders)
            {
                if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue;
                if (!ExecutionInstrumentResolver.IsSameInstrument(o.Instrument?.MasterInstrument?.Name ?? o.Instrument?.FullName ?? "", instRoot))
                    continue;
                var tag = GetOrderTag(o);
                if (string.IsNullOrEmpty(tag) || !tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) continue;
                var id = RobotOrderIds.DecodeIntentId(tag);
                if (!string.IsNullOrEmpty(id)) robotTags.Add(id);
            }
        }

        var diagnostics = _executionJournal.BuildReleaseBlockingCandidateDiagnostics(
            executionInstrumentKey,
            canonical,
            brokerAbs,
            brokerSigned,
            robotTags,
            registryMismatchTrustedIntentIds);
        var rows = new object[diagnostics.Count];
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            rows[i] = new
            {
                intent_id = d.IntentId,
                category = d.Category.ToString(),
                disposition = d.Disposition.ToString(),
                d.RecoveryAdoptionShouldConsume,
                non_adoption_reason = d.NonAdoptionReason
            };
        }
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "RECOVERY_ADOPTION_ZERO_DELTA_BLOCKERS", state: "ENGINE",
            new
            {
                adoption_scan_episode_id = adoptionScanEpisodeId,
                execution_instrument_key = executionInstrumentKey,
                broker_position_qty_abs = brokerAbs,
                broker_position_qty_signed = brokerSigned,
                blocking_candidates = diagnostics.Count,
                candidates = rows
            }));
    }

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
                var brokerMarketPosition = position.MarketPosition.ToString();
                var signedQuantity = BrokerPositionResolver.ApplyMarketPositionSign(position.Quantity, brokerMarketPosition);
                if (signedQuantity == 0) continue;
                legs.Add(new BrokerPositionLeg
                {
                    SignedQuantity = signedQuantity,
                    ContractLabel = label,
                    BrokerMarketPosition = brokerMarketPosition,
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
        if (!IsStrategyThreadContext())
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "NT_THREAD_VIOLATION", state: "CRITICAL",
                new
                {
                    method = "SubmitFlattenOrder",
                    instrument,
                    side,
                    quantity,
                    latch_request_id = snapshot.LatchRequestId,
                    note = "IEA flatten submit requires an immediate broker order id and must already be inside the strategy-thread funnel."
                }));
            _guardViolationCallback?.Invoke("SubmitFlattenOrder");
            return OrderSubmissionResult.FailureResult("NT_THREAD_VIOLATION:SubmitFlattenOrder", utcNow);
        }

        if (!TrySessionIdentityGateDestructiveFlatten(instrument, utcNow, out var sessionFailFlatten))
            return sessionFailFlatten!;
        if (!TryExecutionSafetyFlattenGuard(instrument, null, utcNow, "SUBMIT_FLATTEN_ORDER", snapshot.LatchRequestId, out _))
            return OrderSubmissionResult.FailureResult("EXECUTION_BLOCKED_UNSAFE_STATE", utcNow);
        var account = _ntAccount as Account;
        var ntInstrument = (nativeInstrumentForBrokerOrder ?? (object?)_ntInstrument) as Instrument;
        if (account == null || ntInstrument == null)
            return OrderSubmissionResult.FailureResult("NT context not set", utcNow);

        var (validDirection, directionError) = FlattenInvariantValidator.ValidateFlattenReducesExposure(
            snapshot.AccountQuantityAtDecision,
            side,
            quantity);
        if (!validDirection)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "FLATTEN_DIRECTION_INVALID", new
            {
                instrument,
                account_quantity = snapshot.AccountQuantityAtDecision,
                account_direction = snapshot.AccountDirectionAtDecision,
                broker_market_position = snapshot.BrokerMarketPositionAtDecision,
                chosen_side = side,
                chosen_quantity = quantity,
                error = directionError,
                latch_request_id = snapshot.LatchRequestId
            }));
            return OrderSubmissionResult.FailureResult(directionError ?? "FLATTEN_DIRECTION_INVALID", utcNow);
        }

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

    /// <inheritdoc cref="IExecutionAdapter.TryTriggerHardFlatten"/>
    public bool TryTriggerHardFlatten(string instrument, string reason, DateTimeOffset utcNow)
    {
        var methodSw = Stopwatch.StartNew();
        var inst = instrument?.Trim() ?? "";
        OrderUpdateIntegrityForensicTrace.Step("TRY_TRIGGER_HARD_FLATTEN_ENTER", detail: inst + "|" + (reason ?? ""));
        try
        {
            if (string.IsNullOrEmpty(inst)) return false;
            if (!IsStrategyThreadContext())
            {
                var cid = $"HARD_FLATTEN:{inst}:{utcNow:yyyyMMddHHmmssfff}";
                if (_ntActionQueue != null)
                {
                    _ntActionQueue.EnqueueNtAction(
                        new NtDeferredAction(cid, null, inst, "HARD_FLATTEN_STRATEGY_THREAD",
                            () => TryTriggerHardFlatten(inst, reason, DateTimeOffset.UtcNow)),
                        out _);
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "HARD_FLATTEN_ENQUEUED_STRATEGY_THREAD", state: "ENGINE",
                        new
                        {
                            instrument = inst,
                            reason,
                            correlation_id = cid,
                            note = "Hard flatten requested off strategy thread; queued before arming one-shot broker flatten."
                        }));
                    return true;
                }

                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "HARD_FLATTEN_BLOCKED", state: "CRITICAL",
                    new
                    {
                        instrument = inst,
                        reason,
                        correlation_id = cid,
                        blocked_reason = "NT_ACTION_QUEUE_UNAVAILABLE",
                        note = "Hard flatten cannot safely call Account.Flatten off strategy thread without an NT action queue."
                    }));
                _guardViolationCallback?.Invoke("TryTriggerHardFlatten");
                return false;
            }

            if (!HardFailClosedExecutionModel.TryArmOneShotBrokerFlatten(inst))
                return true;

            _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "CRITICAL_UNSAFE_STATE_DETECTED",
            new { instrument = inst, reason, model = "hard_fail_closed" }));
        _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "HARD_FLATTEN_TRIGGERED",
            new { instrument = inst, reason, path = "account_flatten_broker_only" }));

        var account = _ntAccount as Account;
        Instrument? ntInst = _ntInstrument as Instrument;
        if (ntInst != null &&
            !string.Equals(ntInst.MasterInstrument?.Name?.Trim() ?? "", inst, StringComparison.OrdinalIgnoreCase))
        {
            try { ntInst = Instrument.GetInstrument(inst); }
            catch { ntInst = _ntInstrument as Instrument; }
        }
        else if (ntInst == null)
        {
            try { ntInst = Instrument.GetInstrument(inst); }
            catch { ntInst = null; }
        }

        if (account == null || ntInst == null)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "HARD_FLATTEN_BROKER_CONTEXT_MISSING", "ENGINE",
                new { instrument = inst, reason }));
            ExecutionSafetyGate.ApplyUnmappedExecutionKillSwitch(inst, reason ?? "hard_flatten_no_context", utcNow);
            HardFailClosedExecutionModel.MarkExecutionLocked(inst);
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "EXECUTION_LOCKED_UNSAFE_STATE",
                new { instrument = inst, reason = "lock_without_broker_flatten", broker_context_missing = true }));
            return false;
        }

            var flattenSw = Stopwatch.StartNew();
            OrderUpdateIntegrityForensicTrace.Step("NT_ACCOUNT_FLATTEN_BEFORE", detail: inst);
            try
            {
                account.Flatten(new List<Instrument> { ntInst });
                // Seed broker-flatten recognition (same as SubmitFlattenOrder) so NT "Close" fills from Account.Flatten
                // route to ProcessBrokerFlattenFill instead of EmitUnmappedFill / UNTrackED_TAG.
                lock (_flattenRecognitionLock)
                {
                    _lastFlattenInstrument = ntInst?.MasterInstrument?.Name ?? inst;
                    _lastFlattenUtc = utcNow;
                }
                _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "FLATTEN_RECOGNITION_ARMED",
                    new { instrument = inst, source = "TryTriggerHardFlatten" }));
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, "", "HARD_FLATTEN_BROKER_FAILED", "ENGINE",
                    new { instrument = inst, error = ex.Message, exception_type = ex.GetType().Name }));
            }
            finally
            {
                OrderUpdateIntegrityForensicTrace.Step("NT_ACCOUNT_FLATTEN_AFTER", sectionElapsedMs: flattenSw.ElapsedMilliseconds,
                    detail: inst);
            }

            ExecutionSafetyGate.ApplyUnmappedExecutionKillSwitch(inst, reason ?? "hard_fail_closed_flatten", utcNow);
            HardFailClosedExecutionModel.MarkExecutionLocked(inst);
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "EXECUTION_LOCKED_UNSAFE_STATE",
                new { instrument = inst, reason }));
            return true;
        }
        finally
        {
            OrderUpdateIntegrityForensicTrace.Step("TRY_TRIGGER_HARD_FLATTEN_EXIT", sectionElapsedMs: methodSw.ElapsedMilliseconds,
                detail: inst);
        }
    }

    /// <inheritdoc cref="IExecutionAdapter.TryRecognizeSelfInitiatedFlattenCloseFill"/>
    public bool TryRecognizeSelfInitiatedFlattenCloseFill(string instrument, DateTimeOffset utcNow)
    {
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return false;
        lock (_flattenRecognitionLock)
        {
            if (string.IsNullOrEmpty(_lastFlattenInstrument)) return false;
            if (!ExecutionInstrumentResolver.IsSameInstrument(_lastFlattenInstrument, inst)) return false;
            return (utcNow - _lastFlattenUtc).TotalSeconds < FLATTEN_RECOGNITION_WINDOW_SECONDS;
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

        if (!TryExecutionSafetyFlattenGuard(instrument, null, utcNow, "EMERGENCY_FLATTEN_DIRECT", null, out _))
            return FlattenResult.FailureResult("EXECUTION_BLOCKED_UNSAFE_STATE", utcNow);

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
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_REQUESTED", state: "ENGINE", new
        {
            source = DestructiveActionSource.EMERGENCY.ToString(),
            trigger = DestructiveTriggerReason.DIRECT_EMERGENCY_FLATTEN.ToString(),
            execution_instrument_key = execKey,
            phase = "emergency_flatten_direct"
        }));
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_DECISION", state: "ENGINE", new
        {
            allowed = emDecision.AllowInstrumentScope,
            reason = emDecision.ReasonCode,
            scope = emDecision.CancelScopeMode,
            policy_path = emDecision.PolicyPath,
            phase = "emergency_flatten_direct"
        }));
        if (!emDecision.AllowInstrumentScope)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_BLOCKED", state: "ENGINE", new
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
                LegContractLabel = leg.ContractLabel,
                BrokerMarketPositionAtDecision = leg.BrokerMarketPosition
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
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DESTRUCTIVE_ACTION_EXECUTED", state: "ENGINE", new
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

    void IIEAOrderExecutor.ProcessExecutionUpdate(object execution, object order)
    {
        if (execution is ExecutionUpdateSnapshot snapshot)
        {
            HandleExecutionUpdateSnapshot(snapshot);
            return;
        }

        HandleExecutionUpdateReal(execution, order);
    }

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
                _onMismatchExecutionTrigger?.Invoke(intent.Instrument!.Trim(), cmd.UtcNow, new MismatchExecutionTriggerDetails
                {
                    IntentId = intentId,
                    EntryToProtectivesTransition = true
                });
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
        if (!string.IsNullOrEmpty(cmd.IntentId))
        {
            if (cmd.ProtectiveOrdersOnly)
            {
                CancelProtectiveOrdersForIntent(cmd.IntentId, cmd.UtcNow);
                if (cmd.VerifyWorkingProtectivesClearedAfter)
                {
                    VerifyTerminalProtectiveCleanup(
                        cmd.IntentId,
                        cmd.PostCancelTradingDate ?? "",
                        cmd.PostCancelStream ?? "",
                        cmd.PostCancelCompletionReason ?? cmd.Reason,
                        cmd.UtcNow);
                }
            }
            else
            {
                CancelIntentOrdersReal(cmd.IntentId, cmd.UtcNow);
            }
        }
    }

}

#endif
