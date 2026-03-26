using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Canonical runtime order registry. Order-centric; broker order id is primary identity.
/// Intent-based aliases are secondary for compatibility.
/// Phase 2: Lifecycle validation, cleanup, integrity.
/// </summary>
public sealed class OrderRegistry
{
    private readonly ConcurrentDictionary<string, OrderRegistryEntry> _byBrokerOrderId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _aliasToBrokerOrderId = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Alternate native/broker id -> canonical id stored at Register (pre-ack vs post-ack Sim transition).</summary>
    private readonly ConcurrentDictionary<string, string> _brokerOrderIdAliasToCanonical = new(StringComparer.OrdinalIgnoreCase);

    private int _unownedOrdersDetected;
    private int _registryIntegrityFailures;

    /// <summary>Phase 2: Validate lifecycle transition. Returns true if allowed.</summary>
    public static bool ValidateLifecycleTransition(OrderLifecycleState current, OrderLifecycleState next)
    {
        return (current, next) switch
        {
            (OrderLifecycleState.CREATED, OrderLifecycleState.SUBMITTED) => true,
            (OrderLifecycleState.SUBMITTED, OrderLifecycleState.WORKING) => true,
            (OrderLifecycleState.SUBMITTED, OrderLifecycleState.FILLED) => true,  // Fast fill before WORKING
            (OrderLifecycleState.SUBMITTED, OrderLifecycleState.REJECTED) => true,
            (OrderLifecycleState.WORKING, OrderLifecycleState.PART_FILLED) => true,
            (OrderLifecycleState.WORKING, OrderLifecycleState.FILLED) => true,
            (OrderLifecycleState.WORKING, OrderLifecycleState.CANCELED) => true,
            (OrderLifecycleState.PART_FILLED, OrderLifecycleState.FILLED) => true,
            (OrderLifecycleState.PART_FILLED, OrderLifecycleState.CANCELED) => true,
            (OrderLifecycleState.FILLED, _) => false,
            (OrderLifecycleState.CANCELED, _) => false,
            (OrderLifecycleState.REJECTED, _) => false,
            _ => false
        };
    }

    /// <summary>Try resolve by broker order id (canonical path).</summary>
    public bool TryResolveByBrokerOrderId(string brokerOrderId, out OrderRegistryEntry? entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(brokerOrderId)) return false;
        if (_byBrokerOrderId.TryGetValue(brokerOrderId, out var e))
        {
            e.LastResolutionPath = "DirectId";
            entry = e;
            return true;
        }
        if (_brokerOrderIdAliasToCanonical.TryGetValue(brokerOrderId, out var canonicalId) &&
            _byBrokerOrderId.TryGetValue(canonicalId, out e))
        {
            e.LastResolutionPath = "BrokerOrderIdAlias";
            entry = e;
            return true;
        }
        return false;
    }

    /// <summary>Try resolve by alias (intentId, intentId:STOP, intentId:TARGET).</summary>
    public bool TryResolveByAlias(string alias, out OrderRegistryEntry? entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(alias)) return false;
        if (_aliasToBrokerOrderId.TryGetValue(alias, out var brokerId) && _byBrokerOrderId.TryGetValue(brokerId, out var e))
        {
            e.LastResolutionPath = "Alias";
            entry = e;
            return true;
        }
        return false;
    }

    /// <summary>Register order. Returns true if added.</summary>
    public bool Register(OrderRegistryEntry entry)
    {
        if (string.IsNullOrEmpty(entry.BrokerOrderId)) return false;
        _byBrokerOrderId[entry.BrokerOrderId] = entry;
        if (!string.IsNullOrEmpty(entry.IntentId))
        {
            _aliasToBrokerOrderId[entry.IntentId] = entry.BrokerOrderId;
            if (entry.OrderRole == OrderRole.STOP)
                _aliasToBrokerOrderId[$"{entry.IntentId}:STOP"] = entry.BrokerOrderId;
            else if (entry.OrderRole == OrderRole.TARGET)
                _aliasToBrokerOrderId[$"{entry.IntentId}:TARGET"] = entry.BrokerOrderId;
        }
        return true;
    }

    /// <summary>Add alias for existing entry.</summary>
    public void AddAlias(string alias, string brokerOrderId)
    {
        if (string.IsNullOrEmpty(alias) || string.IsNullOrEmpty(brokerOrderId)) return;
        _aliasToBrokerOrderId[alias] = brokerOrderId;
    }

    /// <summary>
    /// Link a broker-native order id observed after submit (e.g. Sim post-ack) to the canonical id used at <see cref="Register"/>.
    /// Keeps intent aliases separate — uses a dedicated map so intent strings never resolve as broker ids.
    /// </summary>
    public bool LinkBrokerOrderIdAlias(string alternateBrokerOrderId, string canonicalBrokerOrderId)
    {
        if (string.IsNullOrEmpty(alternateBrokerOrderId) || string.IsNullOrEmpty(canonicalBrokerOrderId)) return false;
        if (string.Equals(alternateBrokerOrderId, canonicalBrokerOrderId, StringComparison.OrdinalIgnoreCase)) return false;
        if (!_byBrokerOrderId.ContainsKey(canonicalBrokerOrderId)) return false;
        _brokerOrderIdAliasToCanonical[alternateBrokerOrderId] = canonicalBrokerOrderId;
        return true;
    }

    /// <summary>Update lifecycle state. Returns false if transition invalid (caller should emit ORDER_LIFECYCLE_TRANSITION_INVALID).</summary>
    public bool UpdateLifecycle(string brokerOrderId, OrderLifecycleState newState, DateTimeOffset? terminalUtc = null)
    {
        OrderRegistryEntry? entry = null;
        if (_byBrokerOrderId.TryGetValue(brokerOrderId, out var e))
            entry = e;
        else if (_brokerOrderIdAliasToCanonical.TryGetValue(brokerOrderId, out var canon) && _byBrokerOrderId.TryGetValue(canon, out e))
            entry = e;
        if (entry == null) return true; // not found, no validation
        var current = entry.LifecycleState;
        if (!ValidateLifecycleTransition(current, newState))
            return false;
        entry.LifecycleState = newState;
        if (terminalUtc.HasValue || newState == OrderLifecycleState.FILLED || newState == OrderLifecycleState.CANCELED || newState == OrderLifecycleState.REJECTED)
        {
            entry.TerminalUtc = terminalUtc ?? DateTimeOffset.UtcNow;
            entry.OwnershipStatus = OrderOwnershipStatus.TERMINAL;
        }
        return true;
    }

    /// <summary>Check if broker order id is registered.</summary>
    public bool Contains(string brokerOrderId) =>
        !string.IsNullOrEmpty(brokerOrderId) && _byBrokerOrderId.ContainsKey(brokerOrderId);

    /// <summary>Phase 2: Get all entries for integrity/cleanup.</summary>
    public IReadOnlyList<OrderRegistryEntry> GetAllEntries() =>
        _byBrokerOrderId.Values.ToList();

    /// <summary>Phase 2: Get broker order ids of WORKING orders.</summary>
    public IReadOnlyList<string> GetWorkingOrderIds() =>
        _byBrokerOrderId.Where(kvp => kvp.Value.LifecycleState == OrderLifecycleState.WORKING).Select(kvp => kvp.Key).ToList();

    /// <summary>Count of owned+adopted orders in SUBMITTED, WORKING, or PART_FILLED (non-terminal live).
    /// Used for ORDER_REGISTRY_MISSING reconciliation — avoids false positives during SUBMITTED→WORKING transition.</summary>
    public int GetOwnedPlusAdoptedWorkingCount()
    {
        var entries = _byBrokerOrderId.Values;
        var count = 0;
        foreach (var e in entries)
        {
            if (e.OwnershipStatus != OrderOwnershipStatus.OWNED && e.OwnershipStatus != OrderOwnershipStatus.ADOPTED)
                continue;
            if (e.LifecycleState == OrderLifecycleState.SUBMITTED || e.LifecycleState == OrderLifecycleState.WORKING || e.LifecycleState == OrderLifecycleState.PART_FILLED)
                count++;
        }
        return count;
    }

    /// <summary>Phase 2: Remove terminal entries older than retention cutoff. Returns count removed.</summary>
    public int CleanupTerminalOrders(DateTimeOffset cutoffUtc, Func<string, bool>? excludeIfReferencedByIntent = null)
    {
        var toRemove = new List<string>();
        foreach (var kvp in _byBrokerOrderId)
        {
            var e = kvp.Value;
            if (e.LifecycleState != OrderLifecycleState.FILLED && e.LifecycleState != OrderLifecycleState.CANCELED && e.LifecycleState != OrderLifecycleState.REJECTED)
                continue;
            if (e.TerminalUtc == null || e.TerminalUtc.Value >= cutoffUtc)
                continue;
            if (excludeIfReferencedByIntent != null && !string.IsNullOrEmpty(e.IntentId) && excludeIfReferencedByIntent(e.IntentId))
                continue;
            toRemove.Add(kvp.Key);
        }
        foreach (var k in toRemove)
        {
            _byBrokerOrderId.TryRemove(k, out _);
            foreach (var alias in _aliasToBrokerOrderId.Where(x => x.Value == k).Select(x => x.Key).ToList())
                _aliasToBrokerOrderId.TryRemove(alias, out _);
            foreach (var alt in _brokerOrderIdAliasToCanonical.Where(x => x.Value == k).Select(x => x.Key).ToList())
                _brokerOrderIdAliasToCanonical.TryRemove(alt, out _);
        }
        return toRemove.Count;
    }

    /// <summary>Phase 2: Increment unowned counter.</summary>
    public void IncrementUnownedDetected() => System.Threading.Interlocked.Increment(ref _unownedOrdersDetected);

    /// <summary>Phase 2: Increment integrity failure counter.</summary>
    public void IncrementIntegrityFailure() => System.Threading.Interlocked.Increment(ref _registryIntegrityFailures);

    /// <summary>Phase 2: Get metrics snapshot.</summary>
    public OrderRegistryMetrics GetMetrics()
    {
        var entries = _byBrokerOrderId.Values.ToList();
        return new OrderRegistryMetrics
        {
            OwnedOrdersActive = entries.Count(e => e.OwnershipStatus == OrderOwnershipStatus.OWNED && e.LifecycleState == OrderLifecycleState.WORKING),
            AdoptedOrdersActive = entries.Count(e => e.OwnershipStatus == OrderOwnershipStatus.ADOPTED && e.LifecycleState == OrderLifecycleState.WORKING),
            TerminalOrdersRecent = entries.Count(e => e.OwnershipStatus == OrderOwnershipStatus.TERMINAL),
            UnownedOrdersDetected = _unownedOrdersDetected,
            RegistryIntegrityFailures = _registryIntegrityFailures
        };
    }
}

/// <summary>Phase 2: Registry metrics for observability.</summary>
public sealed class OrderRegistryMetrics
{
    public int OwnedOrdersActive { get; set; }
    public int AdoptedOrdersActive { get; set; }
    public int TerminalOrdersRecent { get; set; }
    public int UnownedOrdersDetected { get; set; }
    public int RegistryIntegrityFailures { get; set; }
}
