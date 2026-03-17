using System;
using System.IO;

namespace QTSW2.Robot.Core;

/// <summary>
/// Polls timetable file using content-only hash (excludes as_of, source).
/// Avoids unnecessary restarts when only metadata changes (e.g. Matrix app resequence).
/// Same interface as FilePoller for drop-in replacement.
/// </summary>
public sealed class TimetableFilePoller
{
    private readonly TimeSpan _pollInterval;
    private DateTimeOffset _lastPollUtc = DateTimeOffset.MinValue;
    private string? _lastHash;

    public TimetableFilePoller(TimeSpan pollInterval)
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
        var hash = TimetableContentHasher.ComputeFromBytes(bytes);
        if (hash is null)
            return new FilePollResult(true, null, "PARSE_ERROR");

        var changed = _lastHash is null || !string.Equals(_lastHash, hash, StringComparison.OrdinalIgnoreCase);
        _lastHash = hash;
        return new FilePollResult(changed, hash, null);
    }
}
