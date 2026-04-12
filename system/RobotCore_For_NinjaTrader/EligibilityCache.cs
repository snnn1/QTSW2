using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QTSW2.Robot.Core;

/// <summary>
/// Static shared cache for eligibility file: last read timestamp, hash, parsed object.
/// Similar to TimetableCache. Uses file LastWriteTimeUtc as cache key.
/// </summary>
public static class EligibilityCache
{
    private static readonly object _lock = new();
    private static string? _cachedPath;
    private static DateTime _cachedLastWriteUtc;
    private static byte[]? _cachedBytes;
    private static string? _cachedHash;
    private static EligibilityContract? _cachedEligibility;

    /// <summary>
    /// Get or load eligibility. Returns (hash, contract, changed from lastHash).
    /// Returns (null, null, false) if file does not exist.
    /// </summary>
    public static (string? Hash, EligibilityContract? Contract, bool Changed) GetOrLoad(string path, string? lastHash)
    {
        if (!File.Exists(path))
            return (null, null, false);

        var lastWriteUtc = File.GetLastWriteTimeUtc(path);

        lock (_lock)
        {
            if (_cachedPath == path && _cachedLastWriteUtc == lastWriteUtc && _cachedBytes != null)
            {
                var changed = lastHash is null || !string.Equals(_cachedHash, lastHash, StringComparison.OrdinalIgnoreCase);
                return (_cachedHash, _cachedEligibility, changed);
            }

            var bytes = File.ReadAllBytes(path);
            var hash = Sha256Hex(bytes);
            EligibilityContract? contract;
            try
            {
                contract = EligibilityContract.LoadFromBytes(bytes);
            }
            catch
            {
                return (hash, null, true);
            }

            if (contract == null)
                return (hash, null, true);

            _cachedPath = path;
            _cachedLastWriteUtc = lastWriteUtc;
            _cachedBytes = bytes;
            _cachedHash = hash;
            _cachedEligibility = contract;

            var changedResult = lastHash is null || !string.Equals(hash, lastHash, StringComparison.OrdinalIgnoreCase);
            return (hash, contract, changedResult);
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
