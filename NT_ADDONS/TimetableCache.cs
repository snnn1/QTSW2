using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QTSW2.Robot.Core;

/// <summary>
/// Static shared cache for timetable file: last read timestamp, hash, parsed object.
/// Reduces disk reads, hashing, and parsing across all engines/charts.
/// When multiple strategies poll the same file, only the first does I/O; others hit cache.
/// </summary>
public static class TimetableCache
{
    private static readonly object _lock = new();
    private static string? _cachedPath;
    private static DateTime _cachedLastWriteUtc;
    private static byte[]? _cachedBytes;
    private static string? _cachedHash;
    private static TimetableContract? _cachedTimetable;

    /// <summary>
    /// Get or load timetable. Returns (hash, timetable, changed from lastHash, wasCacheHit).
    /// Uses file LastWriteTimeUtc as cache key; skips disk read when file unchanged.
    /// Single read path per process; atomic swap of cached object.
    /// </summary>
    public static (string? Hash, TimetableContract? Timetable, bool Changed, bool WasCacheHit) GetOrLoad(string path, string? lastHash)
    {
        if (!File.Exists(path))
            return (null, null, false, false);

        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            // File replaced or locked; retain previous if available
            lock (_lock)
            {
                if (_cachedPath == path && _cachedTimetable != null)
                    return (_cachedHash, _cachedTimetable, false, true);
            }
            return (null, null, false, false);
        }

        lock (_lock)
        {
            if (_cachedPath == path && _cachedLastWriteUtc == lastWriteUtc && _cachedBytes != null)
            {
                // Cache hit: file unchanged since last read
                var changed = lastHash is null || !string.Equals(_cachedHash, lastHash, StringComparison.OrdinalIgnoreCase);
                return (_cachedHash, _cachedTimetable, changed, true);
            }

            // Cache miss: read, parse, content-hash (excludes as_of), store
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                // File in use (e.g. pipeline writing); retain previous snapshot, retry next interval
                if (_cachedPath == path && _cachedTimetable != null)
                    return (_cachedHash, _cachedTimetable, false, true);
                return (null, null, false, false);
            }

            TimetableContract? timetable;
            try
            {
                timetable = TimetableContract.LoadFromBytes(bytes);
            }
            catch
            {
                // Partial/corrupt file; do not overwrite cache
                var rawHash = Sha256Hex(bytes);
                return (rawHash, null, true, false);
            }
            var hash = TimetableContentHasher.ComputeFromTimetable(timetable);

            _cachedPath = path;
            _cachedLastWriteUtc = lastWriteUtc;
            _cachedBytes = bytes;
            _cachedHash = hash;
            _cachedTimetable = timetable;

            var changedResult = lastHash is null || !string.Equals(hash, lastHash, StringComparison.OrdinalIgnoreCase);
            return (hash, timetable, changedResult, false);
        }
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using (var sha = SHA256.Create())
        {
            var h = sha.ComputeHash(bytes);
            var sb = new StringBuilder(h.Length * 2);
            foreach (var b in h)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
