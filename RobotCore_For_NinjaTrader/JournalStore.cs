using System.IO;

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
        return JsonUtil.Deserialize<StreamJournal>(json);
    }

    public void Save(StreamJournal journal)
    {
        var path = GetJournalPath(journal.TradingDate, journal.Stream);
        var json = JsonUtil.Serialize(journal);
        File.WriteAllText(path, json);
    }
}

public sealed class StreamJournal
{
    public string TradingDate { get; set; } = "";

    public string Stream { get; set; } = "";

    public bool Committed { get; set; }

    public string? CommitReason { get; set; } // ENTRY_FILLED | NO_TRADE_MARKET_CLOSE | FORCED_FLATTEN

    public string LastState { get; set; } = "";

    public string LastUpdateUtc { get; set; } = "";

    public string? TimetableHashAtCommit { get; set; }
}

