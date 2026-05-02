using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class ExecutionJournal
{
    public bool HasEntryFillForStream(string tradingDate, string stream)
    {
        lock (_lock)
        {
            var cacheKey = $"{tradingDate}_{stream}";
            if (_entryFillByStream.TryGetValue(cacheKey, out var cached))
                return cached;

            // Cache miss: scan journal directory for entries matching tradingDate_stream_*
            var pattern = $"{tradingDate}_{stream}_*.json";
            var result = false;
            try
            {
                if (!Directory.Exists(_journalDir)) return false;

                var files = Directory.GetFiles(_journalDir, pattern);
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                        if (entry != null)
                        {
                            if (entry.EntryFilled || entry.EntryFilledQuantityTotal > 0)
                            {
                                result = true;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Skip corrupted files, continue scanning
                    }
                }
                _entryFillByStream[cacheKey] = result;
            }
            catch
            {
                // Fail-safe: return false on error (assume no fill)
                return false;
            }

            return result;
        }
    }

    /// <summary>
    /// Check if any trade was completed for the given stream and trading day.
    /// Used for determining terminal state (TRADE_COMPLETED vs NO_TRADE).
    /// </summary>
    public bool HasCompletedTradeForStream(string tradingDate, string stream)
    {
        lock (_lock)
        {
            // Scan journal directory for entries matching tradingDate_stream_*
            var pattern = $"{tradingDate}_{stream}_*.json";

            try
            {
                if (!Directory.Exists(_journalDir)) return false;

                var files = Directory.GetFiles(_journalDir, pattern);
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                        if (entry != null &&
                            entry.TradeCompleted &&
                            (entry.EntryFilled || entry.EntryFilledQuantityTotal > 0))
                        {
                            return true; // Found at least one completed trade
                        }
                    }
                    catch
                    {
                        // Skip corrupted files, continue scanning
                    }
                }
            }
            catch
            {
                // Fail-safe: return false on error (assume no completed trade)
                return false;
            }

            return false;
        }
    }

    /// <summary>
    /// Check if an intent is terminal (TradeCompleted).
    /// Used for late-fill protection and orphan detection.
    /// </summary>
    public bool IsIntentCompleted(string intentId, string tradingDate, string stream)
    {
        var entry = GetEntry(intentId, tradingDate, stream);
        return entry != null && entry.TradeCompleted;
    }

    /// <summary>
    /// Get execution journal entry for an intent.
    /// Returns null if entry doesn't exist.
    /// </summary>
    public ExecutionJournalEntry? GetEntry(string intentId, string tradingDate, string stream)
    {
        lock (_lock)
        {
            var key = $"{tradingDate}_{stream}_{intentId}";

            // Check cache first
            if (_cache.TryGetValue(key, out var cachedEntry))
            {
                return cachedEntry;
            }

            // Check disk
            var journalPath = GetJournalPath(tradingDate, stream, intentId);
            if (File.Exists(journalPath))
            {
                try
                {
                    var json = File.ReadAllText(journalPath);
                    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                    {
                        _cache[key] = entry;
                        if (TryParseIntentIdFromCacheKey(key, out var gid))
                            SyncAdoptionCandidateIndexForIntentLocked(gid, entry);
                        return entry;
                    }
                }
                catch
                {
                    // If read fails, return null
                }
            }

            return null;
        }
    }

    private string GetJournalPath(string tradingDate, string stream, string intentId)
        => Path.Combine(_journalDir, $"{tradingDate}_{stream}_{intentId}.json");

    /// <summary>Extract instrument root (e.g. "M2K 03-26" -> "M2K") for broker/reconciliation key matching.</summary>
    private static string GetInstrumentRoot(string instrumentKey)
    {
        if (string.IsNullOrWhiteSpace(instrumentKey)) return "";
        var s = instrumentKey.Trim();
        var idx = s.IndexOf(' ');
        return idx >= 0 ? s.Substring(0, idx) : s;
    }

    /// <summary>
    /// Get all open journal entries (EntryFilled && !TradeCompleted) grouped by execution instrument.
    /// Used by ReconciliationRunner to find orphaned journals when broker is flat.
    /// </summary>
    public Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> GetOpenJournalEntriesByInstrument()
    {
        var result = new Dictionary<string, List<(string, string, string, ExecutionJournalEntry)>>(StringComparer.OrdinalIgnoreCase);
        string[] files;
        try
        {
            files = Directory.GetFiles(_journalDir, "*.json");
        }
        catch
        {
            return result;
        }

        foreach (var path in files)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                var parts = fileName.Split('_');
                if (parts.Length < 3) continue;

                var tradingDate = parts[0];
                var intentId = parts[parts.Length - 1];
                var stream = string.Join("_", parts.Skip(1).Take(parts.Length - 2));

                // Always read from disk for reconciliation - bypass cache to avoid stale data across
                // multiple RobotEngine instances (each has its own ExecutionJournal cache). See
                // docs/robot/incidents/2026-03-11_MYM_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md
                ExecutionJournalEntry? entry;
                lock (_lock)
                {
                    var json = ReadJournalFileWithRetry(path);
                    if (json == null)
                    {
                        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "EXECUTION_JOURNAL_READ_SKIPPED", "ENGINE",
                            new { path, error = "Read failed after retries (file lock?)" }));
                        continue;
                    }
                    entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                    {
                        _cache[fileName] = entry; // Update cache for consistency with disk
                        if (TryParseIntentIdFromJournalFileName(fileName, out var oid))
                            SyncAdoptionCandidateIndexForIntentLocked(oid, entry);
                    }
                }

                if (entry == null || !entry.EntryFilled || entry.TradeCompleted) continue;
                if (entry.EntryFilledQuantityTotal <= 0) continue;

                var instrument = string.IsNullOrWhiteSpace(entry.Instrument) ? "UNKNOWN" : entry.Instrument.Trim();
                if (!result.TryGetValue(instrument, out var list))
                {
                    list = new List<(string, string, string, ExecutionJournalEntry)>();
                    result[instrument] = list;
                }
                list.Add((tradingDate, stream, intentId, entry));
            }
            catch (Exception ex)
            {
                // Log read failures (file lock, corrupt JSON) for RECONCILIATION_QTY_MISMATCH diagnostics
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "EXECUTION_JOURNAL_READ_SKIPPED", "ENGINE",
                    new { path, error = ex.Message, exception_type = ex.GetType().Name }));
            }
        }

        return result;
    }

    /// <summary>
    /// Strip NinjaTrader full contract name to root symbol (e.g. "MES 03-26" → "MES") for adoption matching.
    /// </summary>
}
