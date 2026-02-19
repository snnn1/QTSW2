using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Replay.IncidentPack;

/// <summary>
/// Extracts incident packs from day logs. Deterministic slice + prerequisite expansion.
/// </summary>
public static class IncidentPackExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public sealed class ExtractOptions
    {
        public string FromPath { get; set; } = "";
        public string OutDir { get; set; } = "";
        public string? ErrorEventType { get; set; }
        public string? MessageContains { get; set; }
        public string? Instrument { get; set; }
        public string? Account { get; set; }
        public int PreEvents { get; set; } = 200;
        public int PostEvents { get; set; } = 200;
    }

    /// <summary>
    /// Extract incident pack. Returns incident ID and event count.
    /// </summary>
    public static (string incidentId, int eventCount, string canonicalSha256) Extract(ExtractOptions opts)
    {
        var lines = File.ReadAllLines(opts.FromPath);
        var parsed = new List<(int lineIndex, string line, ReplayEventEnvelope? envelope)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var envelope = TryParseEnvelope(line, i + 1);
            parsed.Add((i, line, envelope));
        }

        var anchors = new List<int>();
        for (var i = 0; i < parsed.Count; i++)
        {
            var (_, line, envelope) = parsed[i];
            if (!MatchesSelector(line, envelope, opts)) continue;
            anchors.Add(i);
        }

        if (anchors.Count == 0 && !string.IsNullOrEmpty(opts.ErrorEventType))
            throw new InvalidOperationException($"No anchor events found matching selector (type={opts.ErrorEventType}, messageContains={opts.MessageContains})");

        if (anchors.Count == 0)
            anchors.Add(0);

        var incidentId = Path.GetFileName(opts.OutDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(incidentId))
            incidentId = $"INC_{DateTime.UtcNow:yyyyMMddHHmmss}";

        var anchorIndex = anchors[0];
        var sliceStart = Math.Max(0, anchorIndex - opts.PreEvents);
        var sliceEnd = Math.Min(parsed.Count - 1, anchorIndex + opts.PostEvents);

        var sliceIndices = new HashSet<int>();
        for (var i = sliceStart; i <= sliceEnd; i++)
            sliceIndices.Add(i);

        var intentIdsInSlice = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in sliceIndices)
        {
            var env = parsed[i].envelope;
            if (env == null) continue;
            CollectIntentIds(env, intentIdsInSlice);
        }

        for (var i = 0; i < sliceStart; i++)
        {
            var env = parsed[i].envelope;
            if (env == null) continue;
            if (env.Type == ReplayEventType.IntentRegistered)
            {
                var ir = (ReplayIntentRegistered)env.Payload;
                if (intentIdsInSlice.Contains(ir.IntentId))
                    sliceIndices.Add(i);
            }
            else if (env.Type == ReplayEventType.IntentPolicyRegistered)
            {
                var ipr = (ReplayIntentPolicyRegistered)env.Payload;
                if (intentIdsInSlice.Contains(ipr.IntentId))
                    sliceIndices.Add(i);
            }
        }

        var orderedIndices = sliceIndices.OrderBy(x => x).ToList();
        var rawLines = orderedIndices.Select(i => parsed[i].line).ToList();
        var events = orderedIndices
            .Select(i => parsed[i].envelope)
            .Where(e => e != null)
            .Cast<ReplayEventEnvelope>()
            .ToList();

        Directory.CreateDirectory(opts.OutDir);

        var eventsPath = Path.Combine(opts.OutDir, "events.jsonl");
        File.WriteAllLines(eventsPath, rawLines);

        var canonicalPath = Path.Combine(opts.OutDir, "canonical.json");
        var eventsForCanonical = RenumberSequences(events);
        ReplayLoader.WriteCanonical(canonicalPath, eventsForCanonical);

        var canonicalBytes = File.ReadAllBytes(canonicalPath);
        var canonicalSha256 = ComputeSha256(canonicalBytes);

        var firstSeq = events.Count > 0 ? events[0].Sequence : 0L;
        var lastSeq = events.Count > 0 ? events[^1].Sequence : 0L;
        var execKey = events.Count > 0 ? events[0].ExecutionInstrumentKey ?? "" : "";

        var metadata = new IncidentMetadata
        {
            IncidentId = incidentId,
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            SourceLog = opts.FromPath,
            Account = opts.Account,
            ExecutionInstrumentKey = execKey,
            Start = new SliceBound { Sequence = firstSeq, TsUtc = "" },
            End = new SliceBound { Sequence = lastSeq, TsUtc = "" },
            Selector = new SelectorInfo
            {
                ErrorEventType = opts.ErrorEventType,
                MessageContains = opts.MessageContains,
                Instrument = opts.Instrument,
                Account = opts.Account,
                PreEvents = opts.PreEvents,
                PostEvents = opts.PostEvents
            },
            ErrorSignature = new ErrorSignature
            {
                EventType = opts.ErrorEventType,
                MessageContains = opts.MessageContains
            },
            CanonicalSha256 = canonicalSha256
        };

        var metadataPath = Path.Combine(opts.OutDir, "metadata.json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

        var expectedPath = Path.Combine(opts.OutDir, "expected.json");
        if (!File.Exists(expectedPath))
        {
            var expected = new QTSW2.Robot.Contracts.InvariantSpec { Invariants = new List<QTSW2.Robot.Contracts.InvariantExpectation>() };
            File.WriteAllText(expectedPath, JsonSerializer.Serialize(expected, new JsonSerializerOptions { WriteIndented = true }));
        }

        return (incidentId, events.Count, canonicalSha256);
    }

    private static bool MatchesSelector(string line, ReplayEventEnvelope? envelope, ExtractOptions opts)
    {
        if (!string.IsNullOrEmpty(opts.ErrorEventType))
        {
            var typeStr = envelope?.Type.ToString() ?? TryGetString(line, "type") ?? TryGetString(line, "eventType");
            if (string.IsNullOrEmpty(typeStr) || !string.Equals(typeStr, opts.ErrorEventType, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (!string.IsNullOrEmpty(opts.MessageContains))
        {
            if (string.IsNullOrEmpty(line) || line.IndexOf(opts.MessageContains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }
        if (!string.IsNullOrEmpty(opts.Instrument) && envelope != null)
        {
            if (!string.Equals(envelope.ExecutionInstrumentKey, opts.Instrument, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static string? TryGetString(string json, string prop)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(prop, out var el))
                return el.GetString();
        }
        catch { }
        return null;
    }

    private static ReplayEventEnvelope? TryParseEnvelope(string line, int lineNum)
    {
        try
        {
            var root = JsonDocument.Parse(line).RootElement;
            if (!root.TryGetProperty("type", out var typeEl) && !root.TryGetProperty("eventType", out typeEl))
                return null;
            var typeStr = typeEl.GetString();
            if (string.IsNullOrEmpty(typeStr) || !Enum.TryParse<ReplayEventType>(typeStr, ignoreCase: true, out var type))
                return null;
            var source = root.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
            var sequence = root.TryGetProperty("sequence", out var seq) ? seq.GetInt64() : 0L;
            var execKey = root.TryGetProperty("executionInstrumentKey", out var ek) ? ek.GetString() ?? "" : "";
            var payloadEl = root.TryGetProperty("payload", out var p) ? p : default;
            object payload = type switch
            {
                ReplayEventType.IntentRegistered => JsonSerializer.Deserialize<ReplayIntentRegistered>(payloadEl.GetRawText(), JsonOptions)!,
                ReplayEventType.IntentPolicyRegistered => JsonSerializer.Deserialize<ReplayIntentPolicyRegistered>(payloadEl.GetRawText(), JsonOptions)!,
                ReplayEventType.ExecutionUpdate => JsonSerializer.Deserialize<ReplayExecutionUpdate>(payloadEl.GetRawText(), JsonOptions)!,
                ReplayEventType.OrderUpdate => JsonSerializer.Deserialize<ReplayOrderUpdate>(payloadEl.GetRawText(), JsonOptions)!,
                ReplayEventType.Tick => JsonSerializer.Deserialize<ReplayTick>(payloadEl.GetRawText(), JsonOptions)!,
                _ => new object()
            };
            return new ReplayEventEnvelope { Source = source, Sequence = sequence, ExecutionInstrumentKey = execKey, Type = type, Payload = payload };
        }
        catch
        {
            return null;
        }
    }

    private static void CollectIntentIds(ReplayEventEnvelope env, HashSet<string> ids)
    {
        switch (env.Type)
        {
            case ReplayEventType.IntentRegistered:
                if (env.Payload is ReplayIntentRegistered ir)
                    ids.Add(ir.IntentId);
                break;
            case ReplayEventType.ExecutionUpdate:
                if (env.Payload is ReplayExecutionUpdate eu)
                {
                    if (!string.IsNullOrEmpty(eu.IntentId)) ids.Add(eu.IntentId);
                    if (!string.IsNullOrEmpty(eu.Tag)) { var id = DecodeIntentIdFromTag(eu.Tag); if (!string.IsNullOrEmpty(id)) ids.Add(id); }
                }
                break;
            case ReplayEventType.OrderUpdate:
                if (env.Payload is ReplayOrderUpdate ou)
                {
                    if (!string.IsNullOrEmpty(ou.IntentId)) ids.Add(ou.IntentId);
                    if (!string.IsNullOrEmpty(ou.Tag)) { var id = DecodeIntentIdFromTag(ou.Tag); if (!string.IsNullOrEmpty(id)) ids.Add(id); }
                }
                break;
        }
    }

    private static List<ReplayEventEnvelope> RenumberSequences(List<ReplayEventEnvelope> events)
    {
        var result = new List<ReplayEventEnvelope>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            result.Add(new ReplayEventEnvelope
            {
                Source = e.Source,
                Sequence = i,
                ExecutionInstrumentKey = e.ExecutionInstrumentKey,
                Type = e.Type,
                Payload = e.Payload
            });
        }
        return result;
    }

    private static string? DecodeIntentIdFromTag(string tag)
    {
        if (string.IsNullOrEmpty(tag) || !tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase))
            return null;
        var remainder = tag.Substring(6);
        var idx = remainder.IndexOf(':');
        return idx < 0 ? remainder : remainder.Substring(0, idx);
    }

    private static string ComputeSha256(byte[] bytes)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
