using System;
using System.Collections.Concurrent;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Shared registry for adopted/unowned orders keyed by broker_order_id.
/// Enables fill journaling when a fill arrives at an IEA instance that did not perform adoption
/// (e.g. cross-instance or timing race). Idempotent; safe for concurrent access.
/// </summary>
public static class SharedAdoptedOrderRegistry
{
    private static readonly ConcurrentDictionary<string, AdoptedOrderRecord> _byBrokerOrderId = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxEntries = 5000;
    private const double RetentionMinutes = 60;

    /// <summary>Record for adopted order resolution.</summary>
    public sealed class AdoptedOrderRecord
    {
        public string BrokerOrderId { get; set; } = "";
        public string IntentId { get; set; } = "";
        public string Instrument { get; set; } = "";
        public string? TradingDate { get; set; }
        public string? Stream { get; set; }
        public bool IsEntryOrder { get; set; }
        public DateTimeOffset RegisteredAtUtc { get; set; }
    }

    /// <summary>Register an adopted or unowned order for cross-instance fill resolution.</summary>
    public static void Register(string brokerOrderId, string intentId, string instrument, string? tradingDate, string? stream, bool isEntryOrder)
    {
        if (string.IsNullOrEmpty(brokerOrderId) || string.IsNullOrEmpty(intentId)) return;
        EvictIfNeeded();
        _byBrokerOrderId[brokerOrderId] = new AdoptedOrderRecord
        {
            BrokerOrderId = brokerOrderId,
            IntentId = intentId,
            Instrument = instrument ?? "",
            TradingDate = tradingDate,
            Stream = stream,
            IsEntryOrder = isEntryOrder,
            RegisteredAtUtc = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Try resolve by broker order id. Returns true if found.</summary>
    public static bool TryResolve(string brokerOrderId, out AdoptedOrderRecord? record)
    {
        record = null;
        if (string.IsNullOrEmpty(brokerOrderId)) return false;
        if (!_byBrokerOrderId.TryGetValue(brokerOrderId, out var r)) return false;
        if ((DateTimeOffset.UtcNow - r.RegisteredAtUtc).TotalMinutes > RetentionMinutes)
        {
            _byBrokerOrderId.TryRemove(brokerOrderId, out _);
            return false;
        }
        record = r;
        return true;
    }

    private static void EvictIfNeeded()
    {
        if (_byBrokerOrderId.Count < MaxEntries) return;
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-RetentionMinutes);
        foreach (var kvp in _byBrokerOrderId)
        {
            if (kvp.Value.RegisteredAtUtc < cutoff)
                _byBrokerOrderId.TryRemove(kvp.Key, out _);
        }
    }
}
