using System.Text.Json;
using System.Text.Json.Serialization;

namespace QTSW2.Robot.Core;

public sealed class TimetableContract
{
    [JsonPropertyName("as_of")]
    public string? AsOf { get; init; }

    [JsonPropertyName("trading_date")]
    public string TradingDate { get; init; } = "";

    [JsonPropertyName("timezone")]
    public string Timezone { get; init; } = "";

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("streams")]
    public List<TimetableStream> Streams { get; init; } = new();

    public static TimetableContract LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TimetableContract>(json, ParitySpec.JsonOptions())
               ?? throw new InvalidOperationException("Failed to parse timetable_current.json");
    }
}

public sealed class TimetableStream
{
    [JsonPropertyName("stream")]
    public string Stream { get; init; } = "";

    [JsonPropertyName("instrument")]
    public string Instrument { get; init; } = "";

    [JsonPropertyName("session")]
    public string Session { get; init; } = "";

    [JsonPropertyName("slot_time")]
    public string SlotTime { get; init; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}

