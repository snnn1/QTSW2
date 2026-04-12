using System;
using System.Collections.Concurrent;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Deterministic router for execution updates. Routes by (accountName, executionInstrumentKey)
/// so fills are always processed by the correct adapter/IEA regardless of which strategy
/// instance receives the NinjaTrader callback.
/// 
/// Key: (accountName, executionInstrumentKey) — derived from Order.Instrument, not chart.
/// Value: (Endpoint, InstanceId) — exactly one owner per key; no overwrite.
/// </summary>
public static class ExecutionUpdateRouter
{
    private static readonly ConcurrentDictionary<(string Account, string ExecutionInstrumentKey), (Action<object, object> Endpoint, string InstanceId)> _endpoints = new();

    /// <summary>
    /// Try register an endpoint. Returns false on conflict (different instance); caller must fail closed.
    /// Idempotent when same instance re-registers.
    /// </summary>
    public static bool TryRegisterEndpoint(string accountName, string executionInstrumentKey, Action<object, object> endpoint, string instanceId, RobotLogger? log)
    {
        if (string.IsNullOrEmpty(accountName) || string.IsNullOrEmpty(executionInstrumentKey))
            return false;
        var ep = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        var id = instanceId ?? "";
        var key = (accountName ?? "", (executionInstrumentKey ?? "").Trim().ToUpperInvariant());

        var existing = _endpoints.TryGetValue(key, out var current);
        if (!existing)
        {
            _endpoints[key] = (ep, id);
            log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXEC_ROUTER_ENDPOINT_REGISTERED", state: "ENGINE",
                new { account_name = accountName, execution_instrument_key = key.Item2, instance_id = id }));
            return true;
        }
        if (string.Equals(current.InstanceId, id, StringComparison.OrdinalIgnoreCase))
        {
            log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXEC_ROUTER_ENDPOINT_IDEMPOTENT", state: "ENGINE",
                new { account_name = accountName, execution_instrument_key = key.Item2, instance_id = id }));
            return true;
        }
            log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXEC_ROUTER_ENDPOINT_CONFLICT", state: "CRITICAL",
            new
            {
                account_name = accountName,
                execution_instrument_key = key.Item2,
                existing_instance_id = current.InstanceId,
                new_instance_id = id,
                note = "Duplicate registration rejected - exactly one owner per (account, executionInstrumentKey)"
            }));
        return false;
    }

    /// <summary>
    /// Register an endpoint. Throws on conflict. Use TryRegisterEndpoint when caller must handle failure.
    /// </summary>
    public static void RegisterEndpoint(string accountName, string executionInstrumentKey, Action<object, object> endpoint, string instanceId, RobotLogger? log)
    {
        if (!TryRegisterEndpoint(accountName, executionInstrumentKey, endpoint, instanceId, log))
            throw new InvalidOperationException($"EXEC_ROUTER_ENDPOINT_CONFLICT: Cannot register endpoint for ({accountName}, {executionInstrumentKey}) - different instance already owns key.");
    }

    /// <summary>
    /// Try get endpoint for routing. Returns true if found.
    /// </summary>
    public static bool TryGetEndpoint(string accountName, string executionInstrumentKey, out Action<object, object>? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrEmpty(accountName) || string.IsNullOrEmpty(executionInstrumentKey))
            return false;
        var key = (accountName ?? "", (executionInstrumentKey ?? "").Trim().ToUpperInvariant());
        var found = _endpoints.TryGetValue(key, out var pair);
        endpoint = pair.Endpoint;
        return found;
    }

    /// <summary>
    /// Derive execution instrument key from order instrument (e.g. "MGC 04-26" → "MGC").
    /// </summary>
    public static string GetExecutionInstrumentKeyFromOrder(object? orderInstrument)
    {
        if (orderInstrument == null) return "UNKNOWN";
        var fullName = orderInstrument.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(fullName)) return "UNKNOWN";
        var parts = fullName.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var root = parts.Length > 0 ? parts[0] : fullName.Trim();
        return root.ToUpperInvariant();
    }

    /// <summary>
    /// Get account name from order (Order.Account.Name). Uses dynamic for NT API compatibility.
    /// </summary>
    public static string GetAccountNameFromOrder(object? order)
    {
        if (order == null) return "";
        try
        {
            dynamic o = order;
            var acc = o.Account;
            if (acc == null) return "";
            return acc.Name as string ?? "";
        }
        catch { return ""; }
    }
}
