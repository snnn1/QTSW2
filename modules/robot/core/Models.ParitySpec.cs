using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

public sealed class ParitySpec
{
    public string spec_name { get; set; } = null!;

    public string spec_revision { get; set; } = "";

    public string Timezone { get; set; } = "";

    public Dictionary<string, ParitySession> Sessions { get; set; } = new();

    public EntryCutoff EntryCutoff { get; set; } = new();

    public BreakoutSpec Breakout { get; set; } = new();

    public TimetableValidation TimetableValidation { get; set; } = new();

    public Dictionary<string, ParityInstrument> Instruments { get; set; } = new();

    public Dictionary<string, object>? NonTradingSymbols { get; set; }

    public static ParitySpec LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var spec = JsonUtil.Deserialize<ParitySpec>(json) ?? throw new InvalidOperationException("Failed to parse parity spec JSON.");
        spec.ValidateOrThrow();
        return spec;
    }

    public void ValidateOrThrow()
    {
        if (string.IsNullOrWhiteSpace(spec_name))
            throw new InvalidOperationException("spec_name invalid: spec_name is required and cannot be empty.");
        if (spec_name != "analyzer_robot_parity")
            throw new InvalidOperationException($"spec_name invalid: '{spec_name}'");

        if (string.IsNullOrWhiteSpace(spec_revision))
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

}

public sealed class ParitySession
{
    public string RangeStartTime { get; set; } = "";

    public List<string> SlotEndTimes { get; set; } = new();
}

public sealed class EntryCutoff
{
    public string type { get; set; } = "";

    public string market_close_time { get; set; } = "";

    public string? rule { get; set; }

    public void ValidateOrThrow()
    {
        if (type != "MARKET_CLOSE")
            throw new InvalidOperationException($"entry_cutoff.type must be MARKET_CLOSE (got '{type}').");
        if (string.IsNullOrWhiteSpace(market_close_time))
            throw new InvalidOperationException("entry_cutoff.market_close_time missing.");
    }
}

public sealed class BreakoutSpec
{
    public int OffsetTicks { get; set; }

    public TickRounding TickRounding { get; set; } = new();

    public void ValidateOrThrow()
    {
        if (OffsetTicks != 1)
            throw new InvalidOperationException($"breakout.offset_ticks must be 1 for analyzer parity (got {OffsetTicks}).");
        TickRounding.ValidateOrThrow();
    }
}

public sealed class TickRounding
{
    public string Method { get; set; } = "";

    public string? Definition { get; set; }

    public string? ImplementationNote { get; set; }

    public string? TieBehaviorNote { get; set; }

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
    public SlotTimeValidation SlotTimeValidation { get; set; } = new();

    public void ValidateOrThrow()
    {
        SlotTimeValidation.ValidateOrThrow();
    }
}

public sealed class SlotTimeValidation
{
    public string Rule { get; set; } = "";

    public string AllowedSlotEndTimesSource { get; set; } = "";

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
    public decimal TickSize { get; set; }

    public decimal BaseTarget { get; set; }

    public bool IsMicro { get; set; }

    public string BaseInstrument { get; set; } = "";

    public decimal ScalingFactor { get; set; }
}

