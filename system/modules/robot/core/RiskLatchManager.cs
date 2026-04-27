using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QTSW2.Robot.Core;

/// <summary>
/// Persists instrument block state so risk latches survive restart.
/// </summary>
public sealed class RiskLatchManager
{
    public const string ObsoleteLegacyClassifierGapMarker = "release_blocker_legacy_count_without_classifier";

    private readonly string _account;
    private readonly string _latchDir;

    public RiskLatchManager(string persistenceBaseRoot, string accountName)
    {
        var root = persistenceBaseRoot ?? "";
        _account = string.IsNullOrWhiteSpace(accountName) ? "UNKNOWN" : accountName.Trim();
        _latchDir = Path.Combine(root, "data", "risk_latches");
    }

    private static string SanitizeForFilename(string s)
    {
        if (string.IsNullOrEmpty(s)) return "_";
        var invalids = Path.GetInvalidFileNameChars();
        var arr = s.ToCharArray();
        for (var i = 0; i < arr.Length; i++)
        {
            if (Array.IndexOf(invalids, arr[i]) >= 0)
                arr[i] = '_';
        }

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

    public void Persist(string instrument, string reason)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        try
        {
            Directory.CreateDirectory(_latchDir);
            var payload = new RiskLatchPayload
            {
                Account = _account,
                Instrument = instrument,
                BlockedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                Reason = reason ?? ""
            };
            File.WriteAllText(GetLatchPath(instrument), JsonUtil.Serialize(payload));
        }
        catch
        {
            // Best effort; risk-latch persistence must not throw into the block path.
        }
    }

    public void Clear(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        try
        {
            var path = GetLatchPath(instrument);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort.
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
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<string> Hydrate() =>
        HydrateEntries().Select(x => x.Instrument).ToList();

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
                    var payload = JsonUtil.Deserialize<RiskLatchPayload>(File.ReadAllText(path));
                    if (string.IsNullOrWhiteSpace(payload?.Instrument)) continue;
                    result.Add(new RiskLatchEntry
                    {
                        Account = payload.Account ?? "",
                        Instrument = payload.Instrument,
                        BlockedAtUtc = payload.BlockedAtUtc ?? "",
                        Reason = payload.Reason ?? "",
                        FilePath = path
                    });
                }
                catch
                {
                    // Skip malformed files.
                }
            }
        }
        catch
        {
            // Best effort.
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

    private sealed class RiskLatchPayload
    {
        public string Account { get; set; } = "";
        public string Instrument { get; set; } = "";
        public string BlockedAtUtc { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}
