using System;
using System.Collections.Concurrent;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Registry for Instrument Execution Authority (IEA) instances.
/// Key: (accountName, executionInstrumentKey) — one IEA per instrument per account.
/// Used when use_instrument_execution_authority is enabled to fix wrong-instance behavior.
/// 
/// INVARIANT: Key uses exact executionInstrumentKey (from ResolveExecutionInstrumentKey).
/// MNQ and NQ are distinct keys — no cross-authority merge. IsSameInstrument is for matching only.
/// </summary>
public static class InstrumentExecutionAuthorityRegistry
{
    private static readonly ConcurrentDictionary<(string Account, string ExecutionInstrumentKey), InstrumentExecutionAuthority> _registry = new();

    /// <summary>
    /// Get or create IEA for the given account and execution instrument.
    /// </summary>
    /// <param name="accountName">Account name (e.g., Sim101).</param>
    /// <param name="executionInstrumentKey">Execution instrument key from ResolveExecutionInstrumentKey (e.g., MNQ, MGC).</param>
    /// <param name="factory">Factory to create new IEA when none exists.</param>
    /// <returns>IEA instance for this (account, instrument).</returns>
    public static InstrumentExecutionAuthority GetOrCreate(
        string accountName,
        string executionInstrumentKey,
        Func<InstrumentExecutionAuthority> factory)
    {
        var key = (accountName ?? "", (executionInstrumentKey ?? "").Trim().ToUpperInvariant());
        return _registry.GetOrAdd(key, _ => factory());
    }

    /// <summary>
    /// Try get existing IEA (for diagnostics).
    /// </summary>
    public static bool TryGet(string accountName, string executionInstrumentKey, out InstrumentExecutionAuthority? iea)
    {
        var key = (accountName ?? "", (executionInstrumentKey ?? "").Trim().ToUpperInvariant());
        var found = _registry.TryGetValue(key, out var existing);
        iea = existing;
        return found;
    }
}
