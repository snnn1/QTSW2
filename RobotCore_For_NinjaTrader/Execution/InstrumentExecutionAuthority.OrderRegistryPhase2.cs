using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NinjaTrader.Cbi;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Phase 2: Registry cleanup, integrity verification, manual order handling, metrics.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    /// <summary>Phase 2: Terminal order retention window (minutes). Default 10.</summary>
    private const int TerminalRetentionMinutes = 10;

    /// <summary>Phase 2: Register an unowned order (manual/external). Classifies as UNOWNED.</summary>
    internal void RegisterUnownedOrder(
        string brokerOrderId,
        string? intentId,
        string instrument,
        string? sourceContext,
        DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(brokerOrderId)) return;

        var orderInfo = new OrderInfo
        {
            OrderId = brokerOrderId,
            Instrument = instrument ?? "",
            OrderType = "UNKNOWN",
            State = "UNOWNED",
            IsEntryOrder = false,
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
            OwnershipStatus = OrderOwnershipStatus.UNOWNED,
            LifecycleState = OrderLifecycleState.WORKING,
            SourceContext = sourceContext ?? "MANUAL_OR_EXTERNAL",
            CreatedUtc = utcNow,
            OrderInfo = orderInfo,
            LastResolutionPath = "Unresolved"
        };

        _orderRegistry.Register(entry);
        _orderRegistry.IncrementUnownedDetected();

        Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instrument, "MANUAL_OR_EXTERNAL_ORDER_DETECTED", new
        {
            broker_order_id = brokerOrderId,
            intent_id = intentId,
            instrument,
            ownership_status = OrderOwnershipStatus.UNOWNED.ToString(),
            source_context = sourceContext,
            iea_instance_id = InstanceId
        }));
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
        }
        return removed;
    }

    /// <summary>Phase 2: Verify registry integrity vs broker. Emits REGISTRY_BROKER_DIVERGENCE on mismatch.</summary>
    internal void VerifyRegistryIntegrity(DateTimeOffset utcNow)
    {
        var account = Executor?.GetAccount() as Account;
        if (account?.Orders == null) return;

        var brokerOrderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Order o in account.Orders)
        {
            if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue;
            var id = o.OrderId?.ToString();
            if (!string.IsNullOrEmpty(id)) brokerOrderIds.Add(id);
        }

        var workingRegistryIds = _orderRegistry.GetWorkingOrderIds().ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Registry has WORKING order but broker does not
        foreach (var regId in workingRegistryIds)
        {
            if (!brokerOrderIds.Contains(regId))
            {
                _orderRegistry.IncrementIntegrityFailure();
                Log?.Write(RobotEvents.ExecutionBase(utcNow, "", ExecutionInstrumentKey, "REGISTRY_BROKER_DIVERGENCE", new
                {
                    broker_order_id = regId,
                    direction = "registry_has_broker_missing",
                    note = "Registry has WORKING order but broker does not",
                    iea_instance_id = InstanceId
                }));
#if NINJATRADER
                RequestRecovery(ExecutionInstrumentKey, "REGISTRY_BROKER_DIVERGENCE", new { broker_order_id = regId, direction = "registry_has_broker_missing" }, utcNow);
                RequestSupervisoryAction(ExecutionInstrumentKey, SupervisoryTriggerReason.REPEATED_REGISTRY_DIVERGENCE, SupervisorySeverity.MEDIUM, new { broker_order_id = regId, direction = "registry_has_broker_missing" }, utcNow);
#endif
            }
        }

        // Broker has live order but registry does not
        foreach (var brokerId in brokerOrderIds)
        {
            if (!_orderRegistry.Contains(brokerId))
            {
                _orderRegistry.IncrementIntegrityFailure();
                Log?.Write(RobotEvents.ExecutionBase(utcNow, "", ExecutionInstrumentKey, "REGISTRY_BROKER_DIVERGENCE", new
                {
                    broker_order_id = brokerId,
                    direction = "broker_has_registry_missing",
                    note = "Broker has live order but registry does not",
                    iea_instance_id = InstanceId
                }));
#if NINJATRADER
                RequestRecovery(ExecutionInstrumentKey, "REGISTRY_BROKER_DIVERGENCE", new { broker_order_id = brokerId, direction = "broker_has_registry_missing" }, utcNow);
                RequestSupervisoryAction(ExecutionInstrumentKey, SupervisoryTriggerReason.REPEATED_REGISTRY_DIVERGENCE, SupervisorySeverity.MEDIUM, new { broker_order_id = brokerId, direction = "broker_has_registry_missing" }, utcNow);
#endif
            }
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
