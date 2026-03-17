using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace QTSW2.Robot.Core;

/// <summary>
/// Computes a content-only hash of the timetable, excluding as_of and source.
/// Used to avoid unnecessary restarts when only metadata (e.g. as_of timestamp) changes.
/// </summary>
public static class TimetableContentHasher
{
    /// <summary>
    /// Compute content hash from raw file bytes. Returns null if parse fails.
    /// </summary>
    public static string? ComputeFromBytes(byte[] bytes)
    {
        var timetable = TimetableContract.LoadFromBytes(bytes);
        return timetable != null ? ComputeFromTimetable(timetable) : null;
    }

    /// <summary>
    /// Compute content hash from parsed timetable. Excludes as_of, source, metadata.
    /// </summary>
    public static string ComputeFromTimetable(TimetableContract timetable)
    {
        var content = new TimetableContentForHash
        {
            trading_date = timetable.trading_date ?? "",
            timezone = timetable.timezone ?? "",
            streams = (timetable.streams ?? new List<TimetableStream>())
                .OrderBy(s => s.stream ?? "", StringComparer.Ordinal)
                .Select(s => new TimetableStreamForHash
                {
                    stream = s.stream ?? "",
                    instrument = s.instrument ?? "",
                    session = s.session ?? "",
                    slot_time = s.slot_time ?? "",
                    enabled = s.enabled,
                    block_reason = s.block_reason ?? "",
                    decision_time = s.decision_time ?? ""
                })
                .ToList()
        };
        var json = JsonUtil.Serialize(content);
        var utf8 = Encoding.UTF8.GetBytes(json);
        return Sha256Hex(utf8);
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

    private sealed class TimetableContentForHash
    {
        public string trading_date { get; set; } = "";
        public string timezone { get; set; } = "";
        public List<TimetableStreamForHash> streams { get; set; } = new();
    }

    private sealed class TimetableStreamForHash
    {
        public string stream { get; set; } = "";
        public string instrument { get; set; } = "";
        public string session { get; set; } = "";
        public string slot_time { get; set; } = "";
        public bool enabled { get; set; }
        public string block_reason { get; set; } = "";
        public string decision_time { get; set; } = "";
    }
}
