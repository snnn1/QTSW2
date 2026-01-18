using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

public sealed class ParitySpec
{
    public string spec_name { get; set; } = null!;

    public string spec_revision { get; set; } = "";

    public string timezone { get; set; } = "";

    public Dictionary<string, ParitySession> sessions { get; set; } = new();

    public EntryCutoff entry_cutoff { get; set; } = new();

    public BreakoutSpec breakout { get; set; } = new();

    public TimetableValidation timetable_validation { get; set; } = new();

    public Dictionary<string, ParityInstrument> instruments { get; set; } = new();

    public Dictionary<string, object>? non_trading_symbols { get; set; }

    public static ParitySpec LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        ParitySpec? spec;
        try
        {
            spec = JsonUtil.Deserialize<ParitySpec>(json);
        }
        catch (System.IO.FileNotFoundException)
        {
            // Fallback to System.Text.Json if System.Web.Extensions is not available (.NET 8.0)
            spec = System.Text.Json.JsonSerializer.Deserialize<ParitySpec>(json);
        }
        if (spec == null)
            throw new InvalidOperationException("Failed to parse parity spec JSON.");
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

        if (timezone != "America/Chicago")
            throw new InvalidOperationException($"timezone must be 'America/Chicago' (got '{timezone}').");

        if (sessions is null || sessions.Count == 0)
            throw new InvalidOperationException("sessions missing/empty.");

        if (!sessions.ContainsKey("S1") || !sessions.ContainsKey("S2"))
            throw new InvalidOperationException("sessions must include S1 and S2.");

        entry_cutoff.ValidateOrThrow();
        breakout.ValidateOrThrow();
        timetable_validation.ValidateOrThrow();

        if (instruments is null || instruments.Count == 0)
            throw new InvalidOperationException("instruments missing/empty.");
    }

    public bool TryGetInstrument(string instrument, out ParityInstrument inst)
        => instruments.TryGetValue(instrument.ToUpperInvariant(), out inst!);

}

public sealed class ParitySession
{
    public string range_start_time { get; set; } = "";

    public List<string> slot_end_times { get; set; } = new();
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
    public int offset_ticks { get; set; }

    public TickRounding tick_rounding { get; set; } = new();

    public void ValidateOrThrow()
    {
        if (offset_ticks != 1)
            throw new InvalidOperationException($"breakout.offset_ticks must be 1 for analyzer parity (got {offset_ticks}).");
        tick_rounding.ValidateOrThrow();
    }
}

public sealed class TickRounding
{
    public string method { get; set; } = "";

    public string? definition { get; set; }

    public string? implementation_note { get; set; }

    public string? tie_behavior_note { get; set; }

    public void ValidateOrThrow()
    {
        if (method != "utility_round_to_tick")
            throw new InvalidOperationException($"breakout.tick_rounding.method must be utility_round_to_tick (got '{method}').");
        if (string.IsNullOrWhiteSpace(definition))
            throw new InvalidOperationException("breakout.tick_rounding.definition missing.");
    }
}

public sealed class TimetableValidation
{
    public SlotTimeValidation slot_time_validation { get; set; } = new();

    public void ValidateOrThrow()
    {
        slot_time_validation.ValidateOrThrow();
    }
}

public sealed class SlotTimeValidation
{
    public string rule { get; set; } = "";

    public string allowed_slot_end_times_source { get; set; } = "";

    public void ValidateOrThrow()
    {
        if (string.IsNullOrWhiteSpace(rule))
            throw new InvalidOperationException("timetable_validation.slot_time_validation.rule missing.");
        if (string.IsNullOrWhiteSpace(allowed_slot_end_times_source))
            throw new InvalidOperationException("timetable_validation.slot_time_validation.allowed_slot_end_times_source missing.");
    }
}

public sealed class ParityInstrument
{
    public decimal tick_size { get; set; }

    public decimal base_target { get; set; }

    public bool is_micro { get; set; }

    public string base_instrument { get; set; } = "";

    public decimal scaling_factor { get; set; }
}

