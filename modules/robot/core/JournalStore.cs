using System.Text.Json;
using System.Text.Json.Serialization;

namespace QTSW2.Robot.Core;

public sealed class JournalStore
{
    private readonly string _journalDir;

    public JournalStore(string projectRoot)
    {
        _journalDir = Path.Combine(projectRoot, "logs", "robot", "journal");
        Directory.CreateDirectory(_journalDir);
    }

    public string GetJournalPath(string tradingDate, string stream)
        => Path.Combine(_journalDir, $"{tradingDate}_{stream}.json");

    public StreamJournal? TryLoad(string tradingDate, string stream)
    {
        var path = GetJournalPath(tradingDate, stream);
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<StreamJournal>(json, ParitySpec.JsonOptions());
    }

    public void Save(StreamJournal journal)
    {
        var path = GetJournalPath(journal.TradingDate, journal.Stream);
        var json = JsonSerializer.Serialize(journal, ParitySpec.JsonOptions());
        File.WriteAllText(path, json);
    }
}

public sealed class StreamJournal
{
    [JsonPropertyName("trading_date")]
    public string TradingDate { get; set; } = "";

    [JsonPropertyName("stream")]
    public string Stream { get; set; } = "";

    [JsonPropertyName("committed")]
    public bool Committed { get; set; }

    [JsonPropertyName("commit_reason")]
    public string? CommitReason { get; set; } // ENTRY_FILLED | NO_TRADE_MARKET_CLOSE | FORCED_FLATTEN

    [JsonPropertyName("last_state")]
    public string LastState { get; set; } = "";

    [JsonPropertyName("last_update_utc")]
    public string LastUpdateUtc { get; set; } = "";

    [JsonPropertyName("timetable_hash_at_commit")]
    public string? TimetableHashAtCommit { get; set; }
}

