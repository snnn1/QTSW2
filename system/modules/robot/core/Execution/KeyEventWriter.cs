using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Append-only <see cref="RunRootArtifacts.KeyEventsFileName"/> under the run persistence root.
/// Signal extraction for operators — not a duplicate of full robot logs.
/// </summary>
public sealed class KeyEventWriter
{
    private readonly string _filePath;
    private readonly object _ioLock = new();

    private static readonly TimeSpan ExecutionBlockDedupeWindow = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RangeLockFailureDedupeWindow = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, DateTimeOffset> _lastExecutionBlockUtc = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _recoveryStartedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recoveryCompleteEpisodes = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _flattenPhaseKeys = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _rangeLockSuccessKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _rangeLockFailureLastUtc = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _streamStandDownKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _forcedFlattenSessionKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>STREAM_SKIPPED: one line per stream id + trading day (terminal skip only).</summary>
    private readonly HashSet<string> _streamSkippedKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>TIMETABLE_APPLY_PARTIAL_REFUSAL KEY_EVENT: trading_date + decision_type.</summary>
    private readonly HashSet<string> _timetableTradabilityKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>STREAMS_CONSTRUCTION_OUTCOME: at most one NO_STREAMS then optionally one upgrade to STREAMS_READY per trading day.</summary>
    private readonly Dictionary<string, string> _streamsConstructionOutcomeByTradingDay = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ENTRY_TERMINATED: one line per intent (terminal entry order without a recorded fill).</summary>
    private readonly HashSet<string> _entryTerminatedIntentIds = new(StringComparer.OrdinalIgnoreCase);

    public KeyEventWriter(string persistenceBase)
    {
        if (string.IsNullOrWhiteSpace(persistenceBase))
            _filePath = "";
        else
            _filePath = Path.Combine(persistenceBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                RunRootArtifacts.KeyEventsFileName);
    }

    /// <summary>Strict JSONL schema: ts_utc, event, instrument, stream, reason, optional data (minimal).</summary>
    public void AppendKeyEvent(
        DateTimeOffset tsUtc,
        string eventType,
        string? instrument = null,
        string? stream = null,
        string? reason = null,
        object? data = null)
    {
        if (string.IsNullOrEmpty(_filePath)) return;
        try
        {
            lock (_ioLock)
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var payload = new Dictionary<string, object?>
                {
                    ["ts_utc"] = tsUtc.ToString("o"),
                    ["event"] = eventType,
                    ["instrument"] = string.IsNullOrEmpty(instrument) ? null : instrument,
                    ["stream"] = string.IsNullOrEmpty(stream) ? null : stream,
                    ["reason"] = string.IsNullOrEmpty(reason) ? null : reason
                };
                if (data != null)
                    payload["data"] = data;

                File.AppendAllText(_filePath, JsonUtil.Serialize(payload) + Environment.NewLine);
            }
        }
        catch
        {
            // best-effort: never disturb trading flow
        }
    }

    /// <summary>400ms dedupe for identical execution-block signatures (instrument + dedupe key).</summary>
    public bool TryShouldEmitExecutionBlock(string instrument, string dedupeKey, DateTimeOffset utcNow)
    {
        var inst = instrument?.Trim() ?? "";
        var key = inst + "\u001f" + (dedupeKey ?? "");
        lock (_ioLock)
        {
            if (_lastExecutionBlockUtc.TryGetValue(key, out var prev) && (utcNow - prev) < ExecutionBlockDedupeWindow)
                return false;
            _lastExecutionBlockUtc[key] = utcNow;
            return true;
        }
    }

    public bool TryShouldEmitEpaBlock(string instrument, string epaDenyReason, DateTimeOffset utcNow) =>
        TryShouldEmitExecutionBlock(instrument, "epa:" + (epaDenyReason ?? ""), utcNow);

    /// <summary>
    /// <paramref name="success"/>: one emission per stream+trading day.
    /// Failure categories: dedupe identical (stream+date+category) within 60s to avoid tight-loop noise.
    /// </summary>
    public bool TryShouldEmitRangeLockOutcome(string stream, string tradingDate, bool success, string outcomeCategory, DateTimeOffset utcNow)
    {
        var s = stream?.Trim() ?? "";
        var d = tradingDate?.Trim() ?? "";
        var cat = outcomeCategory?.Trim() ?? "";
        lock (_ioLock)
        {
            if (success)
            {
                var sk = s + "\u001f" + d;
                if (_rangeLockSuccessKeys.Contains(sk)) return false;
                _rangeLockSuccessKeys.Add(sk);
                return true;
            }

            var fk = s + "\u001f" + d + "\u001f" + cat;
            if (_rangeLockFailureLastUtc.TryGetValue(fk, out var prev) && (utcNow - prev) < RangeLockFailureDedupeWindow)
                return false;
            _rangeLockFailureLastUtc[fk] = utcNow;
            return true;
        }
    }

    /// <summary>One KEY_EVENTS line per durable stream stand-down commit (stream + trading day).</summary>
    public bool TryShouldEmitStreamStandDown(string stream, string tradingDate, DateTimeOffset utcNow)
    {
        var k = (stream?.Trim() ?? "") + "\u001f" + (tradingDate?.Trim() ?? "");
        lock (_ioLock)
        {
            if (_streamStandDownKeys.Contains(k)) return false;
            _streamStandDownKeys.Add(k);
            return true;
        }
    }

    /// <summary>Aligns with journal marker: one emission per (trading day, session class).</summary>
    public bool TryShouldEmitForcedFlattenSession(string tradingDate, string sessionClass, DateTimeOffset utcNow)
    {
        var k = (tradingDate?.Trim() ?? "") + "\u001f" + (sessionClass?.Trim() ?? "");
        lock (_ioLock)
        {
            if (_forcedFlattenSessionKeys.Contains(k)) return false;
            _forcedFlattenSessionKeys.Add(k);
            return true;
        }
    }

    /// <summary>Unified <see cref="AppendKeyEvent"/> for EXECUTION_BLOCKED with payload contract.</summary>
    public void AppendExecutionBlocked(
        DateTimeOffset tsUtc,
        string instrument,
        string? stream,
        string layer,
        string reasonSummary,
        string blockedWhat,
        IReadOnlyList<string>? failedGates,
        IReadOnlyDictionary<string, object?>? extra)
    {
        var data = new Dictionary<string, object?>
        {
            ["layer"] = layer,
            ["reason"] = reasonSummary,
            ["blocked_what"] = blockedWhat
        };
        if (failedGates != null && failedGates.Count > 0)
            data["failed_gates"] = failedGates;
        if (extra != null)
        {
            foreach (var kv in extra)
                data[kv.Key] = kv.Value;
        }

        AppendKeyEvent(tsUtc, "EXECUTION_BLOCKED", instrument?.Trim(), stream, reasonSummary.Length > 160 ? reasonSummary.Substring(0, 160) : reasonSummary, data);
    }

    public bool TryShouldEmitRecoveryStarted(string episodeKey)
    {
        var k = episodeKey?.Trim() ?? "default";
        lock (_ioLock)
        {
            if (_recoveryStartedKeys.Contains(k)) return false;
            _recoveryStartedKeys.Add(k);
            return true;
        }
    }

    /// <param name="episodeKey">Typically recovery episode anchor (e.g. started_utc).</param>
    public bool TryShouldEmitRecoveryComplete(string episodeKey)
    {
        var k = string.IsNullOrWhiteSpace(episodeKey) ? "default" : episodeKey.Trim();
        lock (_ioLock)
        {
            if (_recoveryCompleteEpisodes.Contains(k)) return false;
            _recoveryCompleteEpisodes.Add(k);
            return true;
        }
    }

    public bool TryShouldEmitFlattenPhase(string phase, string instrument, string? correlationId)
    {
        var key = (phase ?? "") + "|" + (instrument?.Trim() ?? "") + "|" + (correlationId ?? "");
        lock (_ioLock)
        {
            if (_flattenPhaseKeys.Contains(key)) return false;
            _flattenPhaseKeys.Add(key);
            return true;
        }
    }

    /// <summary>One KEY_EVENTS line per stream + trading day for terminal setup skip.</summary>
    public bool TryShouldEmitStreamSkipped(string stream, string tradingDate)
    {
        var k = (stream?.Trim() ?? "") + "\u001f" + (tradingDate?.Trim() ?? "");
        lock (_ioLock)
        {
            if (_streamSkippedKeys.Contains(k)) return false;
            _streamSkippedKeys.Add(k);
            return true;
        }
    }

    /// <summary>One line per timetable application affecting tradability (trading_date + decision_type).</summary>
    public bool TryShouldEmitTimetableTradabilityDecision(string tradingDate, string decisionType)
    {
        var k = (tradingDate?.Trim() ?? "") + "\u001f" + (decisionType?.Trim() ?? "");
        lock (_ioLock)
        {
            if (_timetableTradabilityKeys.Contains(k)) return false;
            _timetableTradabilityKeys.Add(k);
            return true;
        }
    }

    /// <summary>
    /// STREAMS_CONSTRUCTION_OUTCOME: dedupe on trading day; allow a single upgrade NO_STREAMS → STREAMS_READY if construction later succeeds.
    /// </summary>
    public bool TryShouldEmitStreamsConstructionOutcome(string tradingDate, string outcomeReason)
    {
        var day = tradingDate?.Trim() ?? "";
        var r = outcomeReason?.Trim() ?? "";
        lock (_ioLock)
        {
            if (!_streamsConstructionOutcomeByTradingDay.TryGetValue(day, out var existing))
            {
                _streamsConstructionOutcomeByTradingDay[day] = r;
                return true;
            }

            if (string.Equals(existing, "NO_STREAMS", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r, "STREAMS_READY", StringComparison.OrdinalIgnoreCase))
            {
                _streamsConstructionOutcomeByTradingDay[day] = r;
                return true;
            }

            return false;
        }
    }

    /// <summary>At most one <c>ENTRY_TERMINATED</c> per intent_id (entry leg lifecycle end).</summary>
    public bool TryShouldEmitEntryTerminated(string intentId)
    {
        var k = intentId?.Trim() ?? "";
        if (string.IsNullOrEmpty(k)) return false;
        lock (_ioLock)
        {
            if (_entryTerminatedIntentIds.Contains(k)) return false;
            _entryTerminatedIntentIds.Add(k);
            return true;
        }
    }
}
