using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

/// <summary>
/// Persists instrument block state (risk latch) to disk so blocks survive restarts.
/// Fail-closed posture: if flatten fails persistently or IEA blocks, the instrument stays blocked
/// until reconciliation restores qty match or an operator manually clears the latch file.
/// </summary>
public sealed class RiskLatchManager
{
    private readonly string _root;
    private readonly string _account;
    private readonly string _latchDir;

    public RiskLatchManager(string projectRoot, string accountName)
    {
        _root = projectRoot ?? "";
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

    /// <summary>Read all persisted risk latches and return instrument keys. Call at startup before RiskGate.</summary>
    public IReadOnlyList<string> Hydrate()
    {
        var result = new List<string>();
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
                        result.Add(payload.Instrument);
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

    private class RiskLatchPayload
    {
        public string Account { get; set; } = "";
        public string Instrument { get; set; } = "";
        public string BlockedAtUtc { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}
