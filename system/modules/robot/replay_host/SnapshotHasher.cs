using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.ReplayHost;

/// <summary>
/// Deterministic SHA-256 hash of IEASnapshot. Decimal as string, invariant culture, ISO8601 timestamps.
/// </summary>
public static class SnapshotHasher
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Compute SHA-256 hash of IEASnapshot. Lowercase hex.
    /// </summary>
    public static string ComputeHash(IEASnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, CanonicalOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
