using System;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// IEA adoption / reconciliation: broker order must match this IEA execution instrument before any heavy work.
/// </summary>
public static class AdoptionScanInstrumentGate
{
    /// <summary>
    /// True if the broker order's master instrument belongs to this IEA (same root as ExecutionInstrumentKey).
    /// </summary>
    public static bool BrokerOrderMatchesExecutionInstrument(string? ieaExecutionInstrumentKey, string? brokerMasterInstrumentName)
    {
        var key = RootInstrumentToken(ieaExecutionInstrumentKey);
        var bro = RootInstrumentToken(brokerMasterInstrumentName);
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(bro))
            return false;
        return string.Equals(key, bro, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>First non-empty token (e.g. "MES 12-25" → "MES").</summary>
    public static string RootInstrumentToken(string? instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument))
            return "";
        var parts = instrument.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0].Trim() : instrument.Trim();
    }
}
