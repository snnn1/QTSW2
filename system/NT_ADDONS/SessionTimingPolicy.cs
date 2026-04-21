using System;

namespace QTSW2.Robot.Core;

/// <summary>
/// Shared policy for forced-flatten timing fields so runtime paths do not infer exit timing from entry-cutoff rules.
/// </summary>
public static class SessionTimingPolicy
{
    public const int DefaultForcedFlattenBufferSeconds = 300;
    public const string DefaultMarketReopenTime = "17:00";

    public static string? ResolveForcedFlattenLocalTime(ParitySpec? spec)
    {
        var configured = spec?.forced_flatten?.local_time?.Trim();
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    public static int ResolveForcedFlattenBufferSeconds(ParitySpec? spec)
    {
        var configured = spec?.forced_flatten?.buffer_seconds ?? DefaultForcedFlattenBufferSeconds;
        return configured > 0 ? configured : DefaultForcedFlattenBufferSeconds;
    }

    public static DateTimeOffset ResolveForcedFlattenTriggerUtc(
        DateOnly tradingDate,
        DateTimeOffset sessionCloseUtc,
        TimeService time,
        ParitySpec? spec,
        out int effectiveLeadSeconds)
    {
        var explicitLocalTime = ResolveForcedFlattenLocalTime(spec);
        if (!string.IsNullOrWhiteSpace(explicitLocalTime))
        {
            var flattenChicago = time.ConstructChicagoTime(tradingDate, explicitLocalTime);
            var flattenUtc = time.ConvertChicagoToUtc(flattenChicago);
            var leadSeconds = (int)System.Math.Round((sessionCloseUtc - flattenUtc).TotalSeconds);
            effectiveLeadSeconds = leadSeconds > 0 ? leadSeconds : 0;
            return flattenUtc;
        }

        effectiveLeadSeconds = ResolveForcedFlattenBufferSeconds(spec);
        return sessionCloseUtc.AddSeconds(-effectiveLeadSeconds);
    }

    public static string ResolveMarketReopenTime(ParitySpec? spec)
    {
        var forcedFlattenReopen = spec?.forced_flatten?.market_reopen_time?.Trim();
        if (!string.IsNullOrWhiteSpace(forcedFlattenReopen))
            return forcedFlattenReopen;

        var entryCutoffReopen = spec?.entry_cutoff?.market_reopen_time?.Trim();
        if (!string.IsNullOrWhiteSpace(entryCutoffReopen))
            return entryCutoffReopen;

        return DefaultMarketReopenTime;
    }
}
