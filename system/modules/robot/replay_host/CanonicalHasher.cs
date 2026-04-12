using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QTSW2.Robot.ReplayHost;

/// <summary>
/// SHA-256 hash of canonical input file. Input fingerprint for traceability.
/// </summary>
public static class CanonicalHasher
{
    /// <summary>
    /// Compute SHA-256 of file content. Lowercase hex.
    /// </summary>
    public static string ComputeFileHash(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
