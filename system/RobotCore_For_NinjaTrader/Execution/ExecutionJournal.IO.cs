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
    private const int JOURNAL_IO_SLOW_THRESHOLD_MS = 100;
    private const int JOURNAL_LOCK_SLOW_WAIT_MS = 50;
    private const int JOURNAL_LOCK_SLOW_HOLD_MS = 100;

    /// <summary>Execute action under journal lock with timing. Logs JOURNAL_LOCK_SLOW when wait or hold exceeds threshold.</summary>
    /// <param name="extra">Optional extra data (e.g. cache_hit) - invoked after action, merged into log.</param>
    private void WithLockTiming(string context, string tradingDate, Action action, Func<object?>? extra = null)
    {
        var waitSw = Stopwatch.StartNew();
        lock (_lock)
        {
            var waitMs = waitSw.ElapsedMilliseconds;
            var holdSw = Stopwatch.StartNew();
            try
            {
                action();
            }
            finally
            {
                var holdMs = holdSw.ElapsedMilliseconds;
                if (waitMs >= JOURNAL_LOCK_SLOW_WAIT_MS || holdMs >= JOURNAL_LOCK_SLOW_HOLD_MS)
                {
                    var ev = new Dictionary<string, object?>
                    {
                        ["context"] = context,
                        ["lock_wait_ms"] = waitMs,
                        ["lock_hold_ms"] = holdMs,
                        ["note"] = "Correlate with disconnects"
                    };
                    var ex = extra?.Invoke();
                    if (ex != null)
                    {
                        foreach (var p in ex.GetType().GetProperties())
                            ev[p.Name] = p.GetValue(ex);
                    }
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate ?? "", "JOURNAL_LOCK_SLOW", "ENGINE", ev));
                }
            }
        }
    }

    private void SaveJournal(string path, ExecutionJournalEntry entry)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 50;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var json = JsonUtil.Serialize(entry);
                // FileShare.Read allows concurrent reads during write (avoids RECONCILIATION_QTY_MISMATCH from file lock)
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var swriter = new StreamWriter(fs))
                {
                    swriter.Write(json);
                }
                sw.Stop();
                if (sw.ElapsedMilliseconds >= JOURNAL_IO_SLOW_THRESHOLD_MS)
                {
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, entry.TradingDate ?? "", "JOURNAL_IO_SLOW", "ENGINE",
                        new { op = "write", path, elapsed_ms = sw.ElapsedMilliseconds, attempt, note = "Correlate with disconnects" }));
                }
                var fn = Path.GetFileNameWithoutExtension(path);
                if (TryParseIntentIdFromJournalFileName(fn, out var persistIntentId))
                    SyncAdoptionCandidateIndexForIntentLocked(persistIntentId, entry);
                return;
            }
            catch (Exception ex)
            {
                var isRetryable = ex is IOException;
                if (attempt < maxRetries && isRetryable)
                {
                    Thread.Sleep(retryDelayMs * attempt);
                    continue;
                }
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, entry.TradingDate ?? "", "EXECUTION_JOURNAL_ERROR", "ENGINE",
                    new { error = ex.Message, path, attempt }));
                return;
            }
        }
    }

    /// <summary>Read journal file with FileShare.ReadWrite and retry to avoid RECONCILIATION_QTY_MISMATCH from file lock.</summary>
    private string? ReadJournalFileWithRetry(string path)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 50;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                string content;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    content = sr.ReadToEnd();
                }
                sw.Stop();
                if (sw.ElapsedMilliseconds >= JOURNAL_IO_SLOW_THRESHOLD_MS)
                {
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", "JOURNAL_IO_SLOW", "ENGINE",
                        new { op = "read", path, elapsed_ms = sw.ElapsedMilliseconds, attempt, note = "Correlate with disconnects" }));
                }
                return content;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                Thread.Sleep(retryDelayMs * attempt);
            }
        }
        return null;
    }
}
