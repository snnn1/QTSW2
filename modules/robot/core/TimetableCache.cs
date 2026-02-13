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
    /// Get or load timetable. Returns (hash, timetable, changed from lastHash).
    /// Uses file LastWriteTimeUtc as cache key; skips disk read when file unchanged.
    /// </summary>
    public static (string? Hash, TimetableContract? Timetable, bool Changed) GetOrLoad(string path, string? lastHash)
    {
        if (!File.Exists(path))
            return (null, null, false);

        var lastWriteUtc = File.GetLastWriteTimeUtc(path);

        lock (_lock)
        {
            if (_cachedPath == path && _cachedLastWriteUtc == lastWriteUtc && _cachedBytes != null)
            {
                // Cache hit: file unchanged since last read
                var changed = lastHash is null || !string.Equals(_cachedHash, lastHash, StringComparison.OrdinalIgnoreCase);
                return (_cachedHash, _cachedTimetable, changed);
            }

            // Cache miss: read, hash, parse, store
            var bytes = File.ReadAllBytes(path);
            var hash = Sha256Hex(bytes);
            TimetableContract? timetable;
            try
            {
                timetable = TimetableContract.LoadFromBytes(bytes);
            }
            catch
            {
                return (hash, null, true);
            }

            _cachedPath = path;
            _cachedLastWriteUtc = lastWriteUtc;
            _cachedBytes = bytes;
            _cachedHash = hash;
            _cachedTimetable = timetable;

            var changedResult = lastHash is null || !string.Equals(hash, lastHash, StringComparison.OrdinalIgnoreCase);
            return (hash, timetable, changedResult);
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
