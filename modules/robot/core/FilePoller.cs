using System.Security.Cryptography;
using System.Text;

namespace QTSW2.Robot.Core;

public sealed class FilePoller
{
    private readonly TimeSpan _pollInterval;
    private DateTimeOffset _lastPollUtc = DateTimeOffset.MinValue;
    private string? _lastHash;

    public FilePoller(TimeSpan pollInterval)
    {
        _pollInterval = pollInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : pollInterval;
    }

    public bool ShouldPoll(DateTimeOffset utcNow) => utcNow - _lastPollUtc >= _pollInterval;

    public FilePollResult Poll(string path, DateTimeOffset utcNow)
    {
        _lastPollUtc = utcNow;
        if (!File.Exists(path))
            return new FilePollResult(false, null, "MISSING");

        var bytes = File.ReadAllBytes(path);
        var hash = Sha256Hex(bytes);
        var changed = _lastHash is null || !string.Equals(_lastHash, hash, StringComparison.OrdinalIgnoreCase);
        _lastHash = hash;
        return new FilePollResult(changed, hash, null);
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var h = sha.ComputeHash(bytes);
        var sb = new StringBuilder(h.Length * 2);
        foreach (var b in h)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}

public readonly record struct FilePollResult(bool Changed, string? Hash, string? Error);

