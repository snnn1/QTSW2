using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NinjaTrader.Cbi;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Phase 2: Registry cleanup, integrity verification, manual order handling, metrics.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    private DateTimeOffset _lastVerifyRegistryThreadAttrUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastVerifyRegistryDiagUtc = DateTimeOffset.MinValue;

    /// <summary>Phase 2: Terminal order retention window (minutes). Default 10.</summary>
    private const int TerminalRetentionMinutes = 10;

    /// <summary>Phase 2: Register an unowned order (manual/external). Default UNOWNED; robot-recoverable paths may use RECOVERABLE_ROBOT_OWNED.</summary>
    /// <param name="isEntryOrder">True when order is an entry order from adoption path — enables fill journaling.</param>
    /// <param name="classAsRecoverableRobotOwned">When true, ownership is RECOVERABLE_ROBOT_OWNED (robot-tagged / adoption-pending — counts toward mismatch-trusted working).</param>
    internal void RegisterUnownedOrder(
        string brokerOrderId,
        string? intentId,
        string instrument,
        string? sourceContext,
        DateTimeOffset utcNow,
        bool isEntryOrder = false,
        bool classAsRecoverableRobotOwned = false)
    {
        if (string.IsNullOrEmpty(brokerOrderId)) return;

        var ownership = classAsRecoverableRobotOwned
            ? OrderOwnershipStatus.RECOVERABLE_ROBOT_OWNED
            : OrderOwnershipStatus.UNOWNED;
        var stateLabel = classAsRecoverableRobotOwned ? "RECOVERABLE_ROBOT_OWNED" : "UNOWNED";

        var orderInfo = new OrderInfo
        {
            OrderId = brokerOrderId,
            Instrument = instrument ?? "",
            OrderType = "UNKNOWN",
            State = stateLabel,
            IsEntryOrder = isEntryOrder,
            Quantity = 0,
            FilledQuantity = 0
        };

        var entry = new OrderRegistryEntry
        {
            BrokerOrderId = brokerOrderId,
            IntentId = intentId,
            Instrument = instrument ?? "",
            Stream = null,
            OrderRole = OrderRole.EXTERNAL,
            OwnershipStatus = ownership,
            LifecycleState = OrderLifecycleState.WORKING,
            SourceContext = sourceContext ?? "MANUAL_OR_EXTERNAL",
            CreatedUtc = utcNow,
            OrderInfo = orderInfo,
            LastResolutionPath = "Unresolved"
        };

        _orderRegistry.Register(entry);
        if (!classAsRecoverableRobotOwned)
            _orderRegistry.IncrementUnownedDetected();

        if (!string.IsNullOrEmpty(intentId))
            SharedAdoptedOrderRegistry.Register(brokerOrderId, intentId, instrument ?? "", null, null, isEntryOrder);

        Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instrument, "MANUAL_OR_EXTERNAL_ORDER_DETECTED", new
        {
            broker_order_id = brokerOrderId,
            intent_id = intentId,
            instrument,
            ownership_status = ownership.ToString(),
            source_context = sourceContext,
            iea_instance_id = InstanceId
        }));
        if (classAsRecoverableRobotOwned)
        {
            Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instrument, "ORDER_REGISTRY_RECOVERABLE_UNOWNED_DETECTED", new
            {
                broker_order_id = brokerOrderId,
                intent_id = intentId,
                instrument,
                source_context = sourceContext,
                iea_instance_id = InstanceId,
                note = "Classified recoverable robot-owned pending adoption / id remap"
            }));
        }

        NotifyReleaseSuppressionActivity();
    }

    /// <summary>
    /// Mismatch assembly ordering: reclassify UNOWNED→RECOVERABLE when snapshot shows QTSW2 tag; attempt pre-ack→post-ack alias link via OrderMap.
    /// </summary>
    internal void PrepareRegistryForMismatchAssemblyFromSnapshot(string instrument, AccountSnapshot? snap, DateTimeOffset utcNow)
    {
        if (snap?.WorkingOrders == null || string.IsNullOrWhiteSpace(instrument)) return;
        var inst = instrument.Trim();
        foreach (var w in snap.WorkingOrders)
        {
            if (string.IsNullOrWhiteSpace(w.Instrument)) continue;
            if (!ExecutionInstrumentResolver.IsSameInstrument(inst, w.Instrument.Trim())) continue;
            if (string.IsNullOrEmpty(w.OrderId)) continue;
            if (!IsRobotTaggedWorkingSnapshot(w)) continue;

            if (TryResolveByBrokerOrderId(w.OrderId, out var entry) && entry != null &&
                entry.OwnershipStatus == OrderOwnershipStatus.UNOWNED &&
                (entry.LifecycleState == OrderLifecycleState.WORKING ||
                 entry.LifecycleState == OrderLifecycleState.SUBMITTED ||
                 entry.LifecycleState == OrderLifecycleState.PART_FILLED))
            {
                entry.OwnershipStatus = OrderOwnershipStatus.RECOVERABLE_ROBOT_OWNED;
                entry.OrderInfo.State = "RECOVERABLE_ROBOT_OWNED";
                Log?.Write(RobotEvents.ExecutionBase(utcNow, entry.IntentId ?? "", inst, "ORDER_REGISTRY_OWNERSHIP_RECLASSIFIED", new
                {
                    broker_order_id = w.OrderId,
                    intent_id = entry.IntentId,
                    instrument = inst,
                    from_ownership = OrderOwnershipStatus.UNOWNED.ToString(),
                    to_ownership = OrderOwnershipStatus.RECOVERABLE_ROBOT_OWNED.ToString(),
                    iea_instance_id = InstanceId
                }));
                Log?.Write(RobotEvents.ExecutionBase(utcNow, entry.IntentId ?? "", inst, "ORDER_REGISTRY_RECOVERABLE_UNOWNED_DETECTED", new
                {
                    broker_order_id = w.OrderId,
                    tag = w.Tag,
                    instrument = inst,
                    iea_instance_id = InstanceId,
                    note = "Reclassified from UNOWNED using broker snapshot tag"
                }));
                NotifyReleaseSuppressionActivity();
            }

            var tag = w.Tag ?? "";
            if (!tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) continue;
            var parsed = RobotOrderIds.ParseTag(tag);
            var baseIntent = parsed.IntentId ?? RobotOrderIds.DecodeIntentId(tag);
            if (string.IsNullOrEmpty(baseIntent)) continue;
            var leg = parsed.Leg == "STOP" || parsed.Leg == "TARGET" ? parsed.Leg : null;
            TryLinkBrokerOrderIdAliasIfResolvable(w.OrderId, baseIntent, leg, inst, utcNow);
        }
    }

    private static bool IsRobotTaggedWorkingSnapshot(WorkingOrderSnapshot w) =>
        (!string.IsNullOrEmpty(w.Tag) && w.Tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrEmpty(w.OcoGroup) && w.OcoGroup.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase));

    /// <summary>Mirror of NinjaTraderSimAdapter TryLinkBrokerOrderIdFromOrderUpdate — resolves canonical from OrderMap and links incoming broker id.</summary>
    internal bool TryLinkBrokerOrderIdAliasIfResolvable(string incomingBrokerOrderId, string baseIntentId, string? leg,
        string instrument, DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(incomingBrokerOrderId)) return false;
        if (TryResolveByBrokerOrderId(incomingBrokerOrderId, out _)) return false;

        OrderInfo? orderInfo = null;
        if (OrderMap.TryGetValue(baseIntentId, out var oi0) && oi0 != null)
            orderInfo = oi0;
        else if (leg == "STOP" && OrderMap.TryGetValue($"{baseIntentId}:STOP", out var oi1) && oi1 != null)
            orderInfo = oi1;
        else if (leg == "TARGET" && OrderMap.TryGetValue($"{baseIntentId}:TARGET", out var oi2) && oi2 != null)
            orderInfo = oi2;

        if (orderInfo == null) return false;

        var legForResolve = leg == "STOP" || leg == "TARGET" ? leg : null;
        if (!TryResolveForExecutionUpdate(orderInfo.OrderId ?? "", baseIntentId, legForResolve, out var regEntry, out _) ||
            regEntry == null)
            return false;

        var canonical = regEntry.BrokerOrderId ?? "";
        if (string.IsNullOrEmpty(canonical) ||
            string.Equals(incomingBrokerOrderId, canonical, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!LinkBrokerOrderIdAlias(incomingBrokerOrderId, canonical, utcNow, baseIntentId, instrument)) return false;
        orderInfo.OrderId = incomingBrokerOrderId;
        return true;
    }

    /// <summary>Phase 2: Run registry cleanup. Removes terminal orders older than retention window.</summary>
    internal int RunRegistryCleanup(DateTimeOffset utcNow, Func<string, bool>? intentIsActive = null)
    {
        var cutoff = utcNow.AddMinutes(-TerminalRetentionMinutes);
        var exclude = intentIsActive != null ? (Func<string, bool>)(id => intentIsActive(id)) : null;
        var removed = _orderRegistry.CleanupTerminalOrders(cutoff, exclude);
        if (removed > 0)
        {
            Log?.Write(RobotEvents.ExecutionBase(utcNow, "", ExecutionInstrumentKey, "ORDER_REGISTRY_CLEANUP", new
            {
                removed_count = removed,
                retention_minutes = TerminalRetentionMinutes,
                iea_instance_id = InstanceId
            }));
            NotifyReleaseSuppressionActivity();
        }
        return removed;
    }

    /// <summary>Phase 2: Verify registry integrity vs broker. Emits REGISTRY_BROKER_DIVERGENCE on mismatch.</summary>
    internal void VerifyRegistryIntegrity(DateTimeOffset utcNow)
    {
        var account = Executor?.GetAccount() as Account;
        if (account?.Orders == null) return;

        var regCpu = RuntimeAuditHubRef.Active != null ? RuntimeAuditHub.CpuStart() : 0L;
        try
        {
        var sw = Stopwatch.StartNew();
        var accountOrdersTotal = account.Orders.Count;
        var ordersScannedPass1 = 0;
        var recoveryRequestsEmitted = 0;
        var supervisoryRequestsEmitted = 0;
        var divergenceEventsLogged = 0;

        if (Log != null && (utcNow - _lastVerifyRegistryThreadAttrUtc).TotalSeconds >= 60)
        {
            _lastVerifyRegistryThreadAttrUtc = utcNow;
            var t = Thread.CurrentThread;
            Log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "IEA_EXPENSIVE_PATH_THREAD", state: "ENGINE",
                new
                {
                    path = "VerifyRegistryIntegrity",
                    thread_id = t.ManagedThreadId,
                    thread_name = t.Name,
                    on_iea_worker = t == _workerThread,
                    iea_instance_id = InstanceId,
                    execution_instrument_key = ExecutionInstrumentKey
                }));
        }

        var brokerOrderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Order o in account.Orders)
        {
            ordersScannedPass1++;
            if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue;
            var id = o.OrderId?.ToString();
            if (!string.IsNullOrEmpty(id)) brokerOrderIds.Add(id);
        }

        var workingRegistryIds = _orderRegistry.GetWorkingOrderIds().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registryWorkingCount = workingRegistryIds.Count;

        // Registry has WORKING order but broker does not
        foreach (var regId in workingRegistryIds)
        {
            if (!brokerOrderIds.Contains(regId))
            {
                divergenceEventsLogged++;
                _orderRegistry.IncrementIntegrityFailure();
                Log?.Write(RobotEvents.ExecutionBase(utcNow, "", ExecutionInstrumentKey, "REGISTRY_BROKER_DIVERGENCE", new
                {
                    broker_order_id = regId,
                    direction = "registry_has_broker_missing",
                    note = "Registry has WORKING order but broker does not",
                    iea_instance_id = InstanceId
                }));
#if NINJATRADER
                recoveryRequestsEmitted++;
                supervisoryRequestsEmitted++;
                RequestRecovery(ExecutionInstrumentKey, "REGISTRY_BROKER_DIVERGENCE", new { broker_order_id = regId, direction = "registry_has_broker_missing" }, utcNow);
                RequestSupervisoryAction(ExecutionInstrumentKey, SupervisoryTriggerReason.REPEATED_REGISTRY_DIVERGENCE, SupervisorySeverity.MEDIUM, new { broker_order_id = regId, direction = "registry_has_broker_missing" }, utcNow);
#endif
            }
        }

        var ordersScannedPass2 = 0;
        // Broker has live order but registry does not — IEA robustness: adopt immediately to restore consistency
        foreach (Order o in account.Orders)
        {
            ordersScannedPass2++;
            if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue;
            var brokerId = o.OrderId?.ToString();
            if (string.IsNullOrEmpty(brokerId) || _orderRegistry.Contains(brokerId)) continue;

            divergenceEventsLogged++;
            _orderRegistry.IncrementIntegrityFailure();
            Log?.Write(RobotEvents.ExecutionBase(utcNow, "", ExecutionInstrumentKey, "REGISTRY_BROKER_DIVERGENCE", new
            {
                broker_order_id = brokerId,
                direction = "broker_has_registry_missing",
                note = "Broker has live order but registry does not — adopting to restore consistency",
                iea_instance_id = InstanceId
            }));
            if (TryAdoptBrokerOrderIfNotInRegistry(o))
            {
                Log?.Write(RobotEvents.ExecutionBase(utcNow, "", ExecutionInstrumentKey, "REGISTRY_BROKER_DIVERGENCE_ADOPTED", new
                {
                    broker_order_id = brokerId,
                    note = "Order adopted into registry",
                    iea_instance_id = InstanceId
                }));
            }
#if NINJATRADER
            recoveryRequestsEmitted++;
            supervisoryRequestsEmitted++;
            RequestRecovery(ExecutionInstrumentKey, "REGISTRY_BROKER_DIVERGENCE", new { broker_order_id = brokerId, direction = "broker_has_registry_missing" }, utcNow);
            RequestSupervisoryAction(ExecutionInstrumentKey, SupervisoryTriggerReason.REPEATED_REGISTRY_DIVERGENCE, SupervisorySeverity.MEDIUM, new { broker_order_id = brokerId, direction = "broker_has_registry_missing" }, utcNow);
#endif
        }

        sw.Stop();
        var wallMs = sw.ElapsedMilliseconds;
        var emitDiag = wallMs >= 75
                       || recoveryRequestsEmitted > 0
                       || supervisoryRequestsEmitted > 0
                       || divergenceEventsLogged > 0
                       || _lastVerifyRegistryDiagUtc == DateTimeOffset.MinValue
                       || (utcNow - _lastVerifyRegistryDiagUtc).TotalSeconds >= 55;
        if (Log != null && emitDiag)
        {
            _lastVerifyRegistryDiagUtc = utcNow;
            Log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "IEA_VERIFY_REGISTRY_INTEGRITY_DIAG", state: "ENGINE",
                new
                {
                    iea_instance_id = InstanceId,
                    execution_instrument_key = ExecutionInstrumentKey,
                    wall_ms = wallMs,
                    account_orders_total = accountOrdersTotal,
                    orders_iterated_pass1 = ordersScannedPass1,
                    orders_iterated_pass2 = ordersScannedPass2,
                    registry_working_items = registryWorkingCount,
                    broker_working_ids_count = brokerOrderIds.Count,
                    divergence_events_logged = divergenceEventsLogged,
                    recovery_requests_emitted = recoveryRequestsEmitted,
                    supervisory_requests_emitted = supervisoryRequestsEmitted,
                    note = "Rate-limited / thresholded proof diag for VerifyRegistryIntegrity"
                }));
        }
        }
        finally
        {
            if (regCpu != 0)
                RuntimeAuditHubRef.Active?.CpuEnd(regCpu, RuntimeAuditSubsystem.RegistryVerify, ExecutionInstrumentKey, stream: "", onIeaWorker: true);
        }
    }

    /// <summary>Phase 2: Increment unowned counter (for EXECUTION_UNOWNED).</summary>
    internal void IncrementUnownedDetected() => _orderRegistry.IncrementUnownedDetected();

    /// <summary>Phase 2: Emit registry metrics.</summary>
    internal void EmitRegistryMetrics(DateTimeOffset utcNow)
    {
        var m = _orderRegistry.GetMetrics();
        Log?.Write(RobotEvents.ExecutionBase(utcNow, "", ExecutionInstrumentKey, "ORDER_REGISTRY_METRICS", new
        {
            owned_orders_active = m.OwnedOrdersActive,
            adopted_orders_active = m.AdoptedOrdersActive,
            terminal_orders_recent = m.TerminalOrdersRecent,
            unowned_orders_detected = m.UnownedOrdersDetected,
            registry_integrity_failures = m.RegistryIntegrityFailures,
            iea_instance_id = InstanceId
        }));
    }

    /// <summary>Emit execution ordering hardening metrics (deferred, duplicate, retries).</summary>
    internal void EmitExecutionOrderingMetrics(DateTimeOffset utcNow)
    {
        var deferred = Interlocked.CompareExchange(ref _deferredExecutionCount, 0, 0);
        var duplicates = Interlocked.CompareExchange(ref _duplicateExecutionCount, 0, 0);
        var retries = Interlocked.CompareExchange(ref _executionResolutionRetries, 0, 0);
        if (deferred == 0 && duplicates == 0 && retries == 0) return;
        Log?.Write(RobotEvents.ExecutionBase(utcNow, "", ExecutionInstrumentKey, "EXECUTION_ORDERING_METRICS", new
        {
            deferred_execution_count = deferred,
            duplicate_execution_count = duplicates,
            execution_resolution_retries = retries,
            iea_instance_id = InstanceId
        }));
    }
}
