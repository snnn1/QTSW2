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

    private static readonly TimeSpan EpaDedupeWindow = TimeSpan.FromMilliseconds(400);
    private readonly Dictionary<string, DateTimeOffset> _lastEpaBlockUtc = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _recoveryStartedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recoveryCompleteEpisodes = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _flattenPhaseKeys = new(StringComparer.OrdinalIgnoreCase);

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

    public bool TryShouldEmitEpaBlock(string instrument, string epaDenyReason, DateTimeOffset utcNow)
    {
        var inst = instrument?.Trim() ?? "";
        var key = inst + "\u001f" + (epaDenyReason ?? "");
        lock (_ioLock)
        {
            if (_lastEpaBlockUtc.TryGetValue(key, out var prev) && (utcNow - prev) < EpaDedupeWindow)
                return false;
            _lastEpaBlockUtc[key] = utcNow;
            return true;
        }
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
}
