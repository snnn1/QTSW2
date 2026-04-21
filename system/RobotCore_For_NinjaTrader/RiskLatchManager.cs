using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QTSW2.Robot.Core;

/// <summary>
/// Persists instrument block state (risk latch) to disk so blocks survive restarts.
/// Fail-closed posture: if flatten fails persistently or IEA blocks, the instrument stays blocked
/// until reconciliation restores qty match or an operator manually clears the latch file.
/// </summary>
public sealed class RiskLatchManager
{
    public const string ObsoleteLegacyClassifierGapMarker = "release_blocker_legacy_count_without_classifier";

    private readonly string _root;
    private readonly string _account;
    private readonly string _latchDir;

    /// <param name="persistenceBaseRoot">Run artifact root (engine <c>_persistenceBase</c>); latch files live under <c>data/risk_latches</c> beneath it.</param>
    public RiskLatchManager(string persistenceBaseRoot, string accountName)
    {
        _root = persistenceBaseRoot ?? "";
        _account = string.IsNullOrWhiteSpace(accountName) ? "UNKNOWN" : accountName.Trim();
        _latchDir = Path.Combine(_root, "data", "risk_latches");
    }

    private static string SanitizeForFilename(string s)
    {
        if (string.IsNullOrEmpty(s)) return "_";
        var invalids = Path.GetInvalidFileNameChars();
        var arr = s.ToCharArray();
        for (int i = 0; i < arr.Length; i++)
            if (Array.IndexOf(invalids, arr[i]) >= 0)
                arr[i] = '_';
        return new string(arr);
    }

    private string GetLatchPath(string instrument)
    {
        var safeAccount = SanitizeForFilename(_account);
        var safeInstrument = SanitizeForFilename(instrument);
        return Path.Combine(_latchDir, $"risk_latch_{safeAccount}_{safeInstrument}.json");
    }

    public static bool IsObsoleteLegacyClassifierGapReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) &&
        reason.IndexOf(ObsoleteLegacyClassifierGapMarker, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Persist a risk latch for the given instrument. Call when blocking.</summary>
    public void Persist(string instrument, string reason)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        try
        {
            Directory.CreateDirectory(_latchDir);
            var path = GetLatchPath(instrument);
            var payload = new RiskLatchPayload
            {
                Account = _account,
                Instrument = instrument,
                BlockedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                Reason = reason ?? ""
            };
            File.WriteAllText(path, JsonUtil.Serialize(payload));
        }
        catch (Exception)
        {
            // Best-effort; do not throw into block path
        }
    }

    /// <summary>Remove the risk latch file. Call when unfreezing (e.g. reconciliation).</summary>
    public void Clear(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        try
        {
            var path = GetLatchPath(instrument);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort
        }
    }

    public bool TryDeleteLatchFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!File.Exists(path))
                return true;

            var fullPath = Path.GetFullPath(path);
            var latchRoot = Path.GetFullPath(_latchDir);
            if (!fullPath.StartsWith(latchRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            File.Delete(fullPath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Read all persisted risk latches and return instrument keys. Call at startup before RiskGate.</summary>
    public IReadOnlyList<string> Hydrate()
    {
        return HydrateEntries().Select(x => x.Instrument).ToList();
    }

    public IReadOnlyList<RiskLatchEntry> HydrateEntries()
    {
        var result = new List<RiskLatchEntry>();
        if (!Directory.Exists(_latchDir)) return result;
        try
        {
            var pattern = $"risk_latch_{SanitizeForFilename(_account)}_*.json";
            foreach (var path in Directory.GetFiles(_latchDir, pattern))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var payload = JsonUtil.Deserialize<RiskLatchPayload>(json);
                    if (!string.IsNullOrWhiteSpace(payload?.Instrument))
                    {
                        result.Add(new RiskLatchEntry
                        {
                            Account = payload.Account ?? "",
                            Instrument = payload.Instrument,
                            BlockedAtUtc = payload.BlockedAtUtc ?? "",
                            Reason = payload.Reason ?? "",
                            FilePath = path
                        });
                    }
                }
                catch
                {
                    // Skip malformed files
                }
            }
        }
        catch (Exception)
        {
            // Best-effort
        }
        return result;
    }

    public sealed class RiskLatchEntry
    {
        public string Account { get; init; } = "";
        public string Instrument { get; init; } = "";
        public string BlockedAtUtc { get; init; } = "";
        public string Reason { get; init; } = "";
        public string FilePath { get; init; } = "";
    }

    private class RiskLatchPayload
    {
        public string Account { get; set; } = "";
        public string Instrument { get; set; } = "";
        public string BlockedAtUtc { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}
