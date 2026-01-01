using System.Text.Json;
using System.Text.Json.Serialization;

namespace QTSW2.Robot.Core;

public sealed class ParitySpec
{
    [JsonPropertyName("spec_name")]
    public string SpecName { get; init; } = "";

    [JsonPropertyName("spec_revision")]
    public string SpecRevision { get; init; } = "";

    [JsonPropertyName("timezone")]
    public string Timezone { get; init; } = "";

    [JsonPropertyName("sessions")]
    public Dictionary<string, ParitySession> Sessions { get; init; } = new();

    [JsonPropertyName("entry_cutoff")]
    public EntryCutoff EntryCutoff { get; init; } = new();

    [JsonPropertyName("breakout")]
    public BreakoutSpec Breakout { get; init; } = new();

    [JsonPropertyName("timetable_validation")]
    public TimetableValidation TimetableValidation { get; init; } = new();

    [JsonPropertyName("instruments")]
    public Dictionary<string, ParityInstrument> Instruments { get; init; } = new();

    [JsonPropertyName("non_trading_symbols")]
    public Dictionary<string, JsonElement>? NonTradingSymbols { get; init; }

    public static ParitySpec LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var spec = JsonSerializer.Deserialize<ParitySpec>(json, JsonOptions()) ?? throw new InvalidOperationException("Failed to parse parity spec JSON.");
        spec.ValidateOrThrow();
        return spec;
    }

    public void ValidateOrThrow()
    {
        if (SpecName != "analyzer_robot_parity")
            throw new InvalidOperationException($"spec_name invalid: '{SpecName}'");

        if (string.IsNullOrWhiteSpace(SpecRevision))
            throw new InvalidOperationException("spec_revision missing.");

        if (Timezone != "America/Chicago")
            throw new InvalidOperationException($"timezone must be 'America/Chicago' (got '{Timezone}').");

        if (Sessions is null || Sessions.Count == 0)
            throw new InvalidOperationException("sessions missing/empty.");

        if (!Sessions.ContainsKey("S1") || !Sessions.ContainsKey("S2"))
            throw new InvalidOperationException("sessions must include S1 and S2.");

        EntryCutoff.ValidateOrThrow();
        Breakout.ValidateOrThrow();
        TimetableValidation.ValidateOrThrow();

        if (Instruments is null || Instruments.Count == 0)
            throw new InvalidOperationException("instruments missing/empty.");
    }

    public bool TryGetInstrument(string instrument, out ParityInstrument inst)
        => Instruments.TryGetValue(instrument.ToUpperInvariant(), out inst!);

    public static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };
}

public sealed class ParitySession
{
    [JsonPropertyName("range_start_time")]
    public string RangeStartTime { get; init; } = "";

    [JsonPropertyName("slot_end_times")]
    public List<string> SlotEndTimes { get; init; } = new();
}

public sealed class EntryCutoff
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("market_close_time")]
    public string MarketCloseTime { get; init; } = "";

    [JsonPropertyName("rule")]
    public string? Rule { get; init; }

    public void ValidateOrThrow()
    {
        if (Type != "MARKET_CLOSE")
            throw new InvalidOperationException($"entry_cutoff.type must be MARKET_CLOSE (got '{Type}').");
        if (string.IsNullOrWhiteSpace(MarketCloseTime))
            throw new InvalidOperationException("entry_cutoff.market_close_time missing.");
    }
}

public sealed class BreakoutSpec
{
    [JsonPropertyName("offset_ticks")]
    public int OffsetTicks { get; init; }

    [JsonPropertyName("tick_rounding")]
    public TickRounding TickRounding { get; init; } = new();

    public void ValidateOrThrow()
    {
        if (OffsetTicks != 1)
            throw new InvalidOperationException($"breakout.offset_ticks must be 1 for analyzer parity (got {OffsetTicks}).");
        TickRounding.ValidateOrThrow();
    }
}

public sealed class TickRounding
{
    [JsonPropertyName("method")]
    public string Method { get; init; } = "";

    [JsonPropertyName("definition")]
    public string? Definition { get; init; }

    [JsonPropertyName("implementation_note")]
    public string? ImplementationNote { get; init; }

    [JsonPropertyName("tie_behavior_note")]
    public string? TieBehaviorNote { get; init; }

    public void ValidateOrThrow()
    {
        if (Method != "utility_round_to_tick")
            throw new InvalidOperationException($"breakout.tick_rounding.method must be utility_round_to_tick (got '{Method}').");
        if (string.IsNullOrWhiteSpace(Definition))
            throw new InvalidOperationException("breakout.tick_rounding.definition missing.");
    }
}

public sealed class TimetableValidation
{
    [JsonPropertyName("slot_time_validation")]
    public SlotTimeValidation SlotTimeValidation { get; init; } = new();

    public void ValidateOrThrow()
    {
        SlotTimeValidation.ValidateOrThrow();
    }
}

public sealed class SlotTimeValidation
{
    [JsonPropertyName("rule")]
    public string Rule { get; init; } = "";

    [JsonPropertyName("allowed_slot_end_times_source")]
    public string AllowedSlotEndTimesSource { get; init; } = "";

    public void ValidateOrThrow()
    {
        if (string.IsNullOrWhiteSpace(Rule))
            throw new InvalidOperationException("timetable_validation.slot_time_validation.rule missing.");
        if (string.IsNullOrWhiteSpace(AllowedSlotEndTimesSource))
            throw new InvalidOperationException("timetable_validation.slot_time_validation.allowed_slot_end_times_source missing.");
    }
}

public sealed class ParityInstrument
{
    [JsonPropertyName("tick_size")]
    public decimal TickSize { get; init; }

    [JsonPropertyName("base_target")]
    public decimal BaseTarget { get; init; }

    [JsonPropertyName("is_micro")]
    public bool IsMicro { get; init; }

    [JsonPropertyName("base_instrument")]
    public string BaseInstrument { get; init; } = "";

    [JsonPropertyName("scaling_factor")]
    public decimal ScalingFactor { get; init; }
}

