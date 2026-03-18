using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Single canonical truth for resolving execution instrument key.
/// Also provides instrument matching for order tracking (NQ vs MNQ, ES vs MES, etc.).
/// Uses the same mapping as StreamStateMachine/engine when emitting intents.
/// Do NOT trust chart instrument identity — use engineExecutionInstrument when available.
/// </summary>
public static class ExecutionInstrumentResolver
{
    /// <summary>
    /// Resolve execution instrument key for IEA registry, intent.ExecutionInstrument, BE/protectives routing.
    /// </summary>
    /// <param name="accountName">Account name (for future use, e.g., multi-account).</param>
    /// <param name="instrument">NT Instrument or master instrument name — do NOT trust for key; used only as fallback when engineExecutionInstrument is empty.</param>
    /// <param name="engineExecutionInstrument">Engine's execution instrument — same value used when creating intents. Prefer this.</param>
    /// <returns>Execution instrument key (e.g., MNQ, MYM, M2K, MGC, MNG).</returns>
    public static string ResolveExecutionInstrumentKey(
        string? accountName,
        object? instrument,
        string? engineExecutionInstrument)
    {
        // Prefer engine execution instrument — it's the same mapping used when creating intents
        if (!string.IsNullOrWhiteSpace(engineExecutionInstrument))
        {
            return engineExecutionInstrument.Trim().ToUpperInvariant();
        }

        // Fallback: derive from instrument when engine value not available
        if (instrument != null)
        {
            var s = instrument.ToString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                var parts = s.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var root = parts.Length > 0 ? parts[0] : s.Trim();
                return root.ToUpperInvariant();
            }
        }

        return "UNKNOWN";
    }

    /// <summary>
    /// Check if two instrument names refer to the same market (handles NQ/MNQ, ES/MES, etc.).
    /// Used for hasAnyOrderForInstrument when order.Instrument.MasterInstrument.Name (e.g. NQ)
    /// may differ from orderInfo.Instrument (e.g. MNQ).
    /// 
    /// INVARIANT: Matching logic only. NEVER use for IEA registry keying or authority scoping.
    /// Registry key is (accountName, executionInstrumentKey) — exact string from ResolveExecutionInstrumentKey.
    /// MNQ IEA and NQ IEA are separate authorities; IsSameInstrument must not merge them.
    /// </summary>
    public static bool IsSameInstrument(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var aNorm = a.Trim().ToUpperInvariant();
        var bNorm = b.Trim().ToUpperInvariant();
        if (aNorm == bNorm) return true;
        return GetCanonicalForInstrumentMatch(aNorm) == GetCanonicalForInstrumentMatch(bNorm);
    }

    private static string GetCanonicalForInstrumentMatch(string instrument)
    {
        switch (instrument)
        {
            case "MES": return "ES";
            case "MNQ": return "NQ";
            case "M2K": return "RTY";  // M2K = micro Russell 2000, RTY = Russell 2000
            case "MYM": return "YM";
            case "MCL": return "CL";
            case "MGC": return "GC";
            case "MNG": return "NG";
            case "RTY": return "RTY";
            default: return instrument;
        }
    }
}
