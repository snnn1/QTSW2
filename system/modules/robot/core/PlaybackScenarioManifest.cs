using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace QTSW2.Robot.Core;

/// <summary>
/// Explicit multi-day playback scenario contract. This is intentionally opt-in via
/// QTSW2_PLAYBACK_SCENARIO or configs/robot/playback_scenario_current.json so ordinary
/// Playback-account runs keep their single-day isolation behavior.
/// </summary>
public sealed class PlaybackScenarioManifest
{
    public const string EnvVarName = "QTSW2_PLAYBACK_SCENARIO";
    public static readonly string ConfigPointerRelativePath =
        Path.Combine("configs", "robot", "playback_scenario_current.json");

    public string scenario_id { get; set; } = "";
    public string mode { get; set; } = "";
    public string? run_id { get; set; }
    public List<string> dates { get; set; } = new();
    public Dictionary<string, PlaybackScenarioTimetableInfo> timetables { get; set; } = new();
    public string? manifest_path { get; set; }

    private string ManifestDirectory =>
        string.IsNullOrWhiteSpace(manifest_path)
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(Path.GetFullPath(manifest_path!)) ?? Directory.GetCurrentDirectory();

    public static bool HasConfigured(string projectRoot)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVarName)))
            return true;

        var pointerPath = Path.Combine(projectRoot, ConfigPointerRelativePath);
        return File.Exists(pointerPath);
    }

    public static bool TryLoadConfigured(string projectRoot, out PlaybackScenarioManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        var configured = (Environment.GetEnvironmentVariable(EnvVarName) ?? "").Trim();
        var source = EnvVarName;
        string? expectedManifestHash = null;

        if (string.IsNullOrWhiteSpace(configured))
        {
            var pointerPath = Path.Combine(projectRoot, ConfigPointerRelativePath);
            if (!File.Exists(pointerPath))
                return false;

            source = ConfigPointerRelativePath;
            try
            {
                var pointer = JsonUtil.Deserialize<PlaybackScenarioPointer>(File.ReadAllText(pointerPath));
                if (pointer == null)
                {
                    error = "PLAYBACK_SCENARIO_INVALID: config pointer deserialized null";
                    return false;
                }

                configured = (pointer.manifest_path ?? "").Trim();
                expectedManifestHash = string.IsNullOrWhiteSpace(pointer.manifest_sha256)
                    ? null
                    : pointer.manifest_sha256.Trim();
                if (string.IsNullOrWhiteSpace(configured))
                {
                    error = "PLAYBACK_SCENARIO_INVALID: config pointer manifest_path is empty";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = $"PLAYBACK_SCENARIO_INVALID: failed to read config pointer {pointerPath}: {ex.Message}";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(configured))
            return false;

        var path = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(projectRoot, configured.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        try
        {
            if (!string.IsNullOrWhiteSpace(expectedManifestHash))
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException("playback scenario manifest missing", path);
                var actualHash = ComputeSha256(path);
                if (!string.Equals(actualHash, expectedManifestHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"manifest hash mismatch from {source}: expected {expectedManifestHash}, actual {actualHash}");
            }
            manifest = Load(path);
            return true;
        }
        catch (Exception ex)
        {
            error = $"PLAYBACK_SCENARIO_INVALID: {ex.Message}";
            manifest = null;
            return false;
        }
    }

    public static PlaybackScenarioManifest Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("scenario path is empty");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("playback scenario manifest missing", fullPath);

        var loaded = JsonUtil.Deserialize<PlaybackScenarioManifest>(File.ReadAllText(fullPath));
        if (loaded == null)
            throw new InvalidOperationException("scenario manifest deserialized null");

        loaded.manifest_path = fullPath;
        loaded.dates ??= new List<string>();
        loaded.timetables ??= new Dictionary<string, PlaybackScenarioTimetableInfo>();
        loaded.Validate();
        return loaded;
    }

    public void Validate()
    {
        if (!string.Equals((mode ?? "").Trim(), "multi_day_carryover", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("scenario mode must be multi_day_carryover");

        if (dates == null || dates.Count == 0)
            throw new InvalidOperationException("scenario dates must not be empty");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        DateOnly? previous = null;
        foreach (var raw in dates)
        {
            if (!DateOnly.TryParse(raw, out var parsed))
                throw new InvalidOperationException($"invalid scenario date '{raw}'");
            var canonical = parsed.ToString("yyyy-MM-dd");
            if (!seen.Add(canonical))
                throw new InvalidOperationException($"duplicate scenario date '{canonical}'");
            if (previous.HasValue && parsed <= previous.Value)
                throw new InvalidOperationException("scenario dates must be strictly ascending");
            previous = parsed;
        }
    }

    public bool ContainsDate(string sessionTradingDate)
    {
        var key = NormalizeDate(sessionTradingDate);
        return dates.Any(d => string.Equals(NormalizeDate(d), key, StringComparison.Ordinal));
    }

    public bool TryResolveScenarioDate(DateTimeOffset eventUtc, out string sessionTradingDate, out string? error)
        => TryResolveScenarioDate(eventUtc, allowPreStartClamp: false, out sessionTradingDate, out error, out _);

    public bool TryResolveScenarioDate(
        DateTimeOffset eventUtc,
        bool allowPreStartClamp,
        out string sessionTradingDate,
        out string? error,
        out bool clampedPreStart)
    {
        sessionTradingDate = ComputeCmeSessionTradingDate(eventUtc);
        error = null;
        clampedPreStart = false;
        if (ContainsDate(sessionTradingDate))
            return true;

        if (allowPreStartClamp &&
            TryGetFirstScenarioDate(out var firstScenarioDate) &&
            TryParseDate(sessionTradingDate, out var eventDate) &&
            TryParseDate(firstScenarioDate, out var firstDate) &&
            eventDate < firstDate)
        {
            sessionTradingDate = firstScenarioDate;
            clampedPreStart = true;
            return true;
        }

        error = $"event session date {sessionTradingDate} is outside playback scenario dates [{string.Join(",", dates)}]";
        return false;
    }

    public bool TryGetFirstScenarioDate(out string firstScenarioDate)
    {
        firstScenarioDate = "";
        if (dates == null || dates.Count == 0)
            return false;

        firstScenarioDate = NormalizeDate(dates[0]);
        return DateOnly.TryParse(firstScenarioDate, out _);
    }

    public bool TryGetLastScenarioDate(out string lastScenarioDate)
    {
        lastScenarioDate = "";
        if (dates == null || dates.Count == 0)
            return false;

        lastScenarioDate = NormalizeDate(dates[dates.Count - 1]);
        return DateOnly.TryParse(lastScenarioDate, out _);
    }

    public bool IsBeforeFirstScenarioDate(
        DateTimeOffset eventUtc,
        out string eventSessionTradingDate,
        out string firstScenarioDate)
    {
        eventSessionTradingDate = ComputeCmeSessionTradingDate(eventUtc);
        firstScenarioDate = "";

        if (!TryGetFirstScenarioDate(out firstScenarioDate))
            return false;

        return TryParseDate(eventSessionTradingDate, out var eventDate) &&
               TryParseDate(firstScenarioDate, out var firstDate) &&
               eventDate < firstDate;
    }

    public string? GetTimetableIdentityHash(string sessionTradingDate)
    {
        var info = FindTimetableInfo(sessionTradingDate);
        if (info == null)
            return null;
        if (!string.IsNullOrWhiteSpace(info.timetable_hash))
            return info.timetable_hash.Trim();
        return string.IsNullOrWhiteSpace(info.hash) ? null : info.hash.Trim();
    }

    public bool TryResolveTimetablePath(string projectRoot, string sessionTradingDate, out string path, out string? expectedHash, out string? error)
    {
        path = "";
        expectedHash = null;
        error = null;
        var date = NormalizeDate(sessionTradingDate);

        var info = FindTimetableInfo(date);

        if (info == null || string.IsNullOrWhiteSpace(info.path))
        {
            error = $"scenario timetable missing for {date}";
            return false;
        }

        expectedHash = string.IsNullOrWhiteSpace(info.hash) ? null : info.hash.Trim();
        var candidate = info.path.Trim();
        path = Path.IsPathRooted(candidate)
            ? candidate
            : Path.Combine(ManifestDirectory, candidate.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (!File.Exists(path))
        {
            error = $"scenario timetable file missing for {date}: {path}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedHash))
        {
            var actual = ComputeSha256(path);
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                error = $"scenario timetable hash mismatch for {date}: expected {expectedHash}, actual {actual}";
                return false;
            }
        }

        return true;
    }

    private PlaybackScenarioTimetableInfo? FindTimetableInfo(string sessionTradingDate)
    {
        var date = NormalizeDate(sessionTradingDate);
        if (timetables == null)
            return null;

        foreach (var kvp in timetables)
        {
            if (string.Equals(NormalizeDate(kvp.Key), date, StringComparison.Ordinal))
                return kvp.Value;
        }

        return null;
    }

    public static string ComputeCmeSessionTradingDate(DateTimeOffset utc)
    {
        var chicago = ConvertUtcToChicago(utc);
        var date = DateOnly.FromDateTime(chicago.DateTime);
        if (chicago.Hour >= 18)
            date = date.AddDays(1);

        return NormalizeWeekendSession(date).ToString("yyyy-MM-dd");
    }

    private static DateTimeOffset ConvertUtcToChicago(DateTimeOffset utc)
    {
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        }
        catch
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        return TimeZoneInfo.ConvertTime(utc, tz);
    }

    private static DateOnly NormalizeWeekendSession(DateOnly date)
    {
        return date.GetDayOfWeek() switch
        {
            DayOfWeek.Saturday => date.AddDays(2),
            DayOfWeek.Sunday => date.AddDays(1),
            _ => date
        };
    }

    private static string NormalizeDate(string raw)
    {
        if (!DateOnly.TryParse((raw ?? "").Trim(), out var parsed))
            return (raw ?? "").Trim();
        return parsed.ToString("yyyy-MM-dd");
    }

    private static bool TryParseDate(string raw, out DateOnly parsed)
        => DateOnly.TryParse(NormalizeDate(raw), out parsed);

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(File.ReadAllBytes(path));
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}

public sealed class PlaybackScenarioTimetableInfo
{
    public string path { get; set; } = "";
    public string? hash { get; set; }
    public string? timetable_hash { get; set; }
}

public sealed class PlaybackScenarioPointer
{
    public string manifest_path { get; set; } = "";
    public string? manifest_sha256 { get; set; }
    public string? scenario_id { get; set; }
    public string? run_id { get; set; }
    public string? created_utc { get; set; }
}
