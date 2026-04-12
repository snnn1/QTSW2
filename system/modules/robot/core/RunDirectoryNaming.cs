using System;
using System.Globalization;
using System.IO;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core;

/// <summary>
/// Human-scoped folder names under <c>runs/</c> for isolated playback (SIM + journal bypass).
/// Format: <c>YYYY-MM-DD__SESSION__RUNID_SHORT</c> (Chicago calendar date).
/// </summary>
public static class RunDirectoryNaming
{
    public static string ModeLabel(ExecutionMode mode) => mode switch
    {
        ExecutionMode.DRYRUN => "DRYRUN",
        ExecutionMode.SIM => "SIM",
        ExecutionMode.LIVE => "LIVE",
        _ => "UNK"
    };

    public static string ShortRunId(string? runIdFull)
    {
        if (string.IsNullOrWhiteSpace(runIdFull)) return "unknown";
        var s = runIdFull.Trim();
        return s.Length <= 6 ? s.ToUpperInvariant() : s.Substring(0, 6).ToUpperInvariant();
    }

    /// <summary>Trading-style calendar date in America/Chicago for folder sorting.</summary>
    public static string ChicagoCalendarDateYyyyMmDd(DateTimeOffset utc)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, tz);
            return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch
        {
            return utc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    public static string BuildFolderName(string yyyyMmDd, string modeLabel, string shortRunId)
        => $"{yyyyMmDd}__{modeLabel}__{shortRunId}";

    /// <summary>Creates <paramref name="parentDir"/> if needed and returns <c>parentDir/folderName</c>, or adds a numeric suffix if that path already exists.</summary>
    public static string AllocateUniquePath(string parentDir, string folderName)
    {
        Directory.CreateDirectory(parentDir);
        var path = Path.Combine(parentDir, folderName);
        if (!Directory.Exists(path)) return path;
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(parentDir, $"{folderName}_{i}");
            if (!Directory.Exists(candidate)) return candidate;
        }

        return Path.Combine(parentDir, $"{folderName}_{DateTimeOffset.UtcNow:HHmmssfff}");
    }
}
