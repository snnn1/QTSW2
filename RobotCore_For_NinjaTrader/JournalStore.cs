using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace QTSW2.Robot.Core;

public sealed class JournalStore
{
    private readonly string _journalDir;
    private static readonly Dictionary<string, object> _fileLocks = new();
    private static readonly object _locksLock = new();

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
        
        // Use file lock for reads too to prevent reading while writing
        var fileLock = GetFileLock(path);
        lock (fileLock)
        {
            try
            {
                var json = File.ReadAllText(path);
                return JsonUtil.Deserialize<StreamJournal>(json);
            }
            catch (IOException)
            {
                // File might be locked by another process, return null (fail open)
                return null;
            }
        }
    }

    public void Save(StreamJournal journal)
    {
        var path = GetJournalPath(journal.TradingDate, journal.Stream);
        var json = JsonUtil.Serialize(journal);
        
        // Use per-file locking to prevent concurrent writes to the same journal file
        var fileLock = GetFileLock(path);
        lock (fileLock)
        {
            try
            {
                // Use atomic write: write to temp file, then rename (prevents partial writes)
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, json);
                // .NET Framework doesn't support overwrite parameter, so delete target first if exists
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tempPath, path);
            }
            catch (IOException ex)
            {
                // If file is locked, retry once after a short delay
                Thread.Sleep(10);
                try
                {
                    var tempPath = path + ".tmp";
                    File.WriteAllText(tempPath, json);
                    // .NET Framework doesn't support overwrite parameter, so delete target first if exists
                    if (File.Exists(path))
                        File.Delete(path);
                    File.Move(tempPath, path);
                }
                catch
                {
                    // If still failing, log error but don't crash (fail open)
                    // Journal writes are not critical for execution
                }
            }
        }
    }

    private static object GetFileLock(string path)
    {
        lock (_locksLock)
        {
            if (!_fileLocks.TryGetValue(path, out var fileLock))
            {
                fileLock = new object();
                _fileLocks[path] = fileLock;
            }
            return fileLock;
        }
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

