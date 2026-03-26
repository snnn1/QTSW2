using System;
using System.Collections.Concurrent;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// IEA Order Registry: canonical runtime order ownership.
/// Canonical identity is broker/native order id. Intent-based aliases are secondary.
/// </summary>
public sealed partial class InstrumentExecutionAuthority
{
    /// <summary>Canonical order registry. Broker order id is primary.</summary>
    private readonly OrderRegistry _orderRegistry = new();

    /// <summary>
    /// Register an order in the canonical registry. Call when order is submitted.
    /// </summary>
    internal void RegisterOrder(
        string brokerOrderId,
        string? intentId,
        string instrument,
        string? stream,
        OrderRole orderRole,
        OrderOwnershipStatus ownershipStatus,
        string? sourceContext,
        OrderInfo orderInfo,
        DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(brokerOrderId)) return;

        var entry = new OrderRegistryEntry
        {
            BrokerOrderId = brokerOrderId,
            IntentId = intentId,
            Instrument = instrument ?? "",
            Stream = stream,
            OrderRole = orderRole,
            OwnershipStatus = ownershipStatus,
            LifecycleState = OrderLifecycleState.SUBMITTED,
            SourceContext = sourceContext,
            CreatedUtc = utcNow,
            OrderInfo = orderInfo,
            LastResolutionPath = "REGISTERED"
        };

        _orderRegistry.Register(entry);

        Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instrument, "ORDER_REGISTRY_REGISTERED", new
        {
            broker_order_id = brokerOrderId,
            intent_id = intentId,
            instrument,
            stream,
            order_role = orderRole.ToString(),
            ownership_status = ownershipStatus.ToString(),
            lifecycle_state = entry.LifecycleState.ToString(),
            source_context = sourceContext,
            iea_instance_id = InstanceId
        }));
    }

    /// <summary>
    /// Register a flatten order. Flatten orders have no intent id.
    /// </summary>
    internal void RegisterFlattenOrder(
        string brokerOrderId,
        string instrument,
        OrderOwnershipStatus ownershipStatus,
        string? sourceContext,
        DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(brokerOrderId)) return;

        var orderInfo = new OrderInfo
        {
            OrderId = brokerOrderId,
            Instrument = instrument,
            OrderType = "FLATTEN",
            State = "SUBMITTED",
            IsEntryOrder = false,
            Quantity = 0,
            FilledQuantity = 0
        };

        var entry = new OrderRegistryEntry
        {
            BrokerOrderId = brokerOrderId,
            IntentId = null,
            Instrument = instrument,
            Stream = null,
            OrderRole = ownershipStatus == OrderOwnershipStatus.ADOPTED ? OrderRole.RECOVERY_FLATTEN : OrderRole.FLATTEN,
            OwnershipStatus = ownershipStatus,
            LifecycleState = OrderLifecycleState.SUBMITTED,
            SourceContext = sourceContext ?? "RequestFlatten",
            CreatedUtc = utcNow,
            OrderInfo = orderInfo,
            LastResolutionPath = "REGISTERED"
        };

        _orderRegistry.Register(entry);

        Log?.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "ORDER_REGISTRY_FLATTEN_REGISTERED", new
        {
            broker_order_id = brokerOrderId,
            instrument,
            order_role = entry.OrderRole.ToString(),
            ownership_status = ownershipStatus.ToString(),
            source_context = sourceContext,
            iea_instance_id = InstanceId
        }));
    }

    /// <summary>Count of owned+adopted orders in SUBMITTED, WORKING, or PART_FILLED.
    /// Used for ORDER_REGISTRY_MISSING reconciliation — broker working vs IEA registry, NOT journal.</summary>
    internal int GetOwnedPlusAdoptedWorkingCount() =>
        _orderRegistry.GetOwnedPlusAdoptedWorkingCount();

    /// <summary>Try resolve by broker order id first (canonical path).</summary>
    internal bool TryResolveByBrokerOrderId(string brokerOrderId, out OrderRegistryEntry? entry) =>
        _orderRegistry.TryResolveByBrokerOrderId(brokerOrderId, out entry);

    /// <summary>Try resolve by alias (intentId, intentId:STOP, intentId:TARGET). Compatibility path.</summary>
    internal bool TryResolveByAlias(string alias, out OrderRegistryEntry? entry) =>
        _orderRegistry.TryResolveByAlias(alias, out entry);

    /// <summary>Resolve execution update: try broker order id first, then alias from tag.</summary>
    internal bool TryResolveForExecutionUpdate(string brokerOrderId, string? intentIdFromTag, string? legFromTag, out OrderRegistryEntry? entry, out string resolutionPath)
    {
        resolutionPath = "Unresolved";
        entry = null;

        if (TryResolveByBrokerOrderId(brokerOrderId, out entry))
        {
            resolutionPath = "DirectId";
            return true;
        }

        if (!string.IsNullOrEmpty(intentIdFromTag))
        {
            var alias = legFromTag == "STOP" ? $"{intentIdFromTag}:STOP" : legFromTag == "TARGET" ? $"{intentIdFromTag}:TARGET" : intentIdFromTag;
            if (TryResolveByAlias(alias, out entry))
            {
                resolutionPath = "Alias";
                return true;
            }
            if (TryResolveByAlias(intentIdFromTag, out entry))
            {
                resolutionPath = "Alias";
                return true;
            }
        }

        return false;
    }

    /// <summary>Update lifecycle state and optionally set terminal time. Phase 2: Validates transition; emits ORDER_LIFECYCLE_TRANSITION_INVALID if invalid.</summary>
    internal void UpdateOrderLifecycle(string brokerOrderId, OrderLifecycleState newState, DateTimeOffset utcNow)
    {
        if (!TryResolveByBrokerOrderId(brokerOrderId, out var entry)) return;

        var prev = entry!.LifecycleState;
        if (!_orderRegistry.UpdateLifecycle(brokerOrderId, newState, utcNow))
        {
            _orderRegistry.IncrementIntegrityFailure();
            Log?.Write(RobotEvents.ExecutionBase(utcNow, entry.IntentId ?? "", entry.Instrument, "ORDER_LIFECYCLE_TRANSITION_INVALID", new
            {
                broker_order_id = brokerOrderId,
                intent_id = entry.IntentId,
                previous_state = prev.ToString(),
                attempted_state = newState.ToString(),
                note = "Illegal lifecycle transition rejected",
                iea_instance_id = InstanceId
            }));
            return;
        }

        Log?.Write(RobotEvents.ExecutionBase(utcNow, entry.IntentId ?? "", entry.Instrument, "ORDER_REGISTRY_LIFECYCLE", new
        {
            broker_order_id = brokerOrderId,
            intent_id = entry.IntentId,
            previous_state = prev.ToString(),
            new_state = newState.ToString(),
            ownership_status = entry.OwnershipStatus.ToString(),
            iea_instance_id = InstanceId
        }));
    }

    /// <summary>Add alias for an existing registry entry (e.g. when adopting).</summary>
    internal void AddAlias(string alias, string brokerOrderId) =>
        _orderRegistry.AddAlias(alias, brokerOrderId);

    /// <summary>
    /// When NT/Sim reports a new native order id on updates while <see cref="RegisterOrder"/> used the pre-ack id,
    /// link the new id to the canonical registry row so <see cref="TryResolveByBrokerOrderId"/> and lifecycle updates match.
    /// </summary>
    internal bool LinkBrokerOrderIdAlias(string alternateBrokerOrderId, string canonicalBrokerOrderId, DateTimeOffset utcNow, string intentId, string instrument)
    {
        if (!_orderRegistry.LinkBrokerOrderIdAlias(alternateBrokerOrderId, canonicalBrokerOrderId)) return false;
        Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId ?? "", instrument, "ORDER_REGISTRY_BROKER_ID_LINKED", new
        {
            canonical_broker_order_id = canonicalBrokerOrderId,
            broker_order_id = alternateBrokerOrderId,
            intent_id = intentId,
            iea_instance_id = InstanceId
        }));
        return true;
    }

    /// <summary>Register an adopted order (restart protectives). Ownership = ADOPTED.</summary>
    internal void RegisterAdoptedOrder(
        string brokerOrderId,
        string intentId,
        string instrument,
        OrderRole orderRole,
        string? sourceContext,
        OrderInfo orderInfo,
        DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(brokerOrderId)) return;

        var entry = new OrderRegistryEntry
        {
            BrokerOrderId = brokerOrderId,
            IntentId = intentId,
            Instrument = instrument,
            OrderRole = OrderRole.ADOPTED,
            OwnershipStatus = OrderOwnershipStatus.ADOPTED,
            LifecycleState = OrderLifecycleState.WORKING,
            SourceContext = sourceContext ?? "ScanAndAdoptExistingOrders",
            CreatedUtc = utcNow,
            OrderInfo = orderInfo,
            LastResolutionPath = "Adopted"
        };

        _orderRegistry.Register(entry);
        _orderRegistry.AddAlias(intentId, brokerOrderId);
        if (orderRole == OrderRole.STOP)
            _orderRegistry.AddAlias($"{intentId}:STOP", brokerOrderId);
        else if (orderRole == OrderRole.TARGET)
            _orderRegistry.AddAlias($"{intentId}:TARGET", brokerOrderId);

        var isEntry = orderRole == OrderRole.ENTRY;
        SharedAdoptedOrderRegistry.Register(brokerOrderId, intentId, instrument ?? "", null, null, isEntry);

        Log?.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_REGISTRY_ADOPTED", new
        {
            broker_order_id = brokerOrderId,
            intent_id = intentId,
            instrument,
            order_role = orderRole.ToString(),
            ownership_status = OrderOwnershipStatus.ADOPTED.ToString(),
            source_context = sourceContext,
            iea_instance_id = InstanceId
        }));
    }
}
