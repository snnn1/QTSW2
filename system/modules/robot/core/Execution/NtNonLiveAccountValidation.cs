using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Centralized checks for accounts where algorithmic SIM-style execution is allowed
/// (NinjaTrader simulation / playback — not live brokerage).
/// </summary>
public static class NtNonLiveAccountValidation
{
    /// <summary>Name fallback when connection mode is unavailable. Matches Sim101, Playback101, Demo*, etc.</summary>
    public static bool IsAllowedNonLiveAccountName(string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return false;
        var trimmed = accountName.Trim();
        if (trimmed.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("Playback", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("Demo", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

#if NINJATRADER
    /// <summary>
    /// Prefer NinjaTrader <see cref="NinjaTrader.Cbi.ConnectOptions.Mode"/>:
    /// <see cref="NinjaTrader.Cbi.Mode.Simulation"/> covers Sim and Playback connections.
    /// If mode is unavailable, use <see cref="IsAllowedNonLiveAccountName"/>.
    /// </summary>
    public static bool IsAllowedAlgorithmicPaperAccount(NinjaTrader.Cbi.Account? account)
    {
        if (account == null) return false;
        try
        {
            var opts = account.Connection?.Options;
            if (opts != null && opts.Mode == NinjaTrader.Cbi.Mode.Simulation)
                return true;
        }
        catch
        {
            // Fall through to name rules
        }

        return IsAllowedNonLiveAccountName(account.Name);
    }
#endif
}
