using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Tests;

public static class ForcedFlattenPolicyTests
{
    public static (bool Pass, string? Error) RunForcedFlattenPolicyTests()
    {
        var spec = BuildSpec();
        spec.ValidateOrThrow();

        var loggingConfig = new LoggingConfig();
        if (!loggingConfig.prefer_internal_calendar_over_nt_holiday)
            return (false, "Expected NT holiday conflict override to default on for active timetable days");

        var defaultBuffer = SessionTimingPolicy.ResolveForcedFlattenBufferSeconds(spec);
        if (defaultBuffer != SessionTimingPolicy.DefaultForcedFlattenBufferSeconds)
            return (false, $"Expected default forced flatten buffer {SessionTimingPolicy.DefaultForcedFlattenBufferSeconds}, got {defaultBuffer}");

        var defaultReopen = SessionTimingPolicy.ResolveMarketReopenTime(spec);
        if (defaultReopen != "17:00")
            return (false, $"Expected legacy reentry fallback 17:00, got {defaultReopen}");

        spec.forced_flatten.buffer_seconds = 420;
        spec.forced_flatten.market_reopen_time = "18:30";

        var configuredBuffer = SessionTimingPolicy.ResolveForcedFlattenBufferSeconds(spec);
        if (configuredBuffer != 420)
            return (false, $"Expected configured forced flatten buffer 420, got {configuredBuffer}");

        var configuredReopen = SessionTimingPolicy.ResolveMarketReopenTime(spec);
        if (configuredReopen != "18:30")
            return (false, $"Expected configured forced flatten reopen 18:30, got {configuredReopen}");

        spec.forced_flatten.market_reopen_time = "";
        spec.entry_cutoff.market_reopen_time = "17:15";
        var legacyFallbackReopen = SessionTimingPolicy.ResolveMarketReopenTime(spec);
        if (legacyFallbackReopen != "17:15")
            return (false, $"Expected entry_cutoff fallback reopen 17:15, got {legacyFallbackReopen}");

        spec.forced_flatten.buffer_seconds = 420;
        spec.forced_flatten.local_time = "15:50";
        spec.entry_cutoff.market_close_time = "16:00";
        var time = new TimeService(spec.timezone);
        var tradingDate = new DateOnly(2026, 3, 16);
        var closeUtc = time.ConvertChicagoToUtc(time.ConstructChicagoTime(tradingDate, spec.entry_cutoff.market_close_time));
        var explicitTriggerUtc = SessionTimingPolicy.ResolveForcedFlattenTriggerUtc(tradingDate, closeUtc, time, spec, out var effectiveLeadSeconds);
        var expectedTriggerUtc = time.ConvertChicagoToUtc(time.ConstructChicagoTime(tradingDate, "15:50"));
        if (explicitTriggerUtc != expectedTriggerUtc)
            return (false, $"Expected explicit forced flatten trigger {expectedTriggerUtc:o}, got {explicitTriggerUtc:o}");
        if (effectiveLeadSeconds != 600)
            return (false, $"Expected explicit local_time to imply 600-second lead, got {effectiveLeadSeconds}");

        return (true, null);
    }

    private static ParitySpec BuildSpec()
    {
        return new ParitySpec
        {
            spec_name = "analyzer_robot_parity",
            spec_revision = "test",
            timezone = "America/Chicago",
            sessions = new Dictionary<string, ParitySession>
            {
                ["S1"] = new ParitySession { range_start_time = "02:00", slot_end_times = new List<string> { "07:30" } },
                ["S2"] = new ParitySession { range_start_time = "08:00", slot_end_times = new List<string> { "09:30" } }
            },
            entry_cutoff = new EntryCutoff
            {
                type = "MARKET_CLOSE",
                market_close_time = "16:00",
                market_reopen_time = "17:00"
            },
            breakout = new BreakoutSpec
            {
                offset_ticks = 1,
                tick_rounding = new TickRounding
                {
                    method = "utility_round_to_tick",
                    definition = "test"
                }
            },
            timetable_validation = new TimetableValidation
            {
                slot_time_validation = new SlotTimeValidation
                {
                    rule = "test",
                    allowed_slot_end_times_source = "test"
                }
            },
            instruments = new Dictionary<string, ParityInstrument>
            {
                ["MES"] = new ParityInstrument
                {
                    tick_size = 0.25m,
                    base_target = 1m,
                    is_micro = true,
                    base_instrument = "ES",
                    scaling_factor = 0.1m
                }
            }
        };
    }
}
