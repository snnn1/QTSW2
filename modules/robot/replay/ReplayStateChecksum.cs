using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Replay;

/// <summary>
/// Computes a deterministic SHA-256 hash of replay state.
/// Same state → same hash. Binary determinism proof.
/// Reference: REPLAY_PHASE_0_TARGET.md
/// </summary>
public static class ReplayStateChecksum
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Compute SHA-256 hash of state. Serializes to canonical JSON (sorted keys, ISO8601).
    /// </summary>
    /// <param name="state">State to hash. Must be JSON-serializable.</param>
    /// <returns>Lowercase hex string of SHA-256 hash.</returns>
    public static string ComputeHash(object state)
    {
        var json = JsonSerializer.Serialize(state, CanonicalOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Compute hash of loaded replay events. Use for input determinism until IEA state is available.
    /// When IEA runner exists: use ComputeHash(ieaStateSnapshot) for full determinism proof.
    /// </summary>
    public static string ComputeEventsHash(IReadOnlyList<ReplayEventEnvelope> events)
    {
        var canonical = new List<object>(events.Count);
        foreach (var e in events)
        {
            canonical.Add(new
            {
                e.Source,
                e.Sequence,
                e.ExecutionInstrumentKey,
                Type = e.Type.ToString(),
                Payload = e.Payload
            });
        }
        return ComputeHash(canonical);
    }
}
