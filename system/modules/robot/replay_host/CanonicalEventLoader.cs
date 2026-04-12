using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.ReplayHost;

/// <summary>
/// Loads validated canonical JSON array. No heavy validation — net8 already validated.
/// Sanity checks only.
/// </summary>
public static class CanonicalEventLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Load events from canonical JSON array file. Throws on parse error.
    /// </summary>
    public static IReadOnlyList<ReplayEventEnvelope> Load(string path)
    {
        var json = System.IO.File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Expected JSON array root");

        var events = new List<ReplayEventEnvelope>(root.GetArrayLength());
        var index = 0;
        foreach (var el in root.EnumerateArray())
        {
            var envelope = ParseElement(el, index);
            events.Add(envelope);
            index++;
        }
        return events;
    }

    private static ReplayEventEnvelope ParseElement(JsonElement root, int index)
    {
        var source = root.GetProperty("source").GetString() ?? "";
        var sequence = root.GetProperty("sequence").GetInt64();
        var executionInstrumentKey = root.GetProperty("executionInstrumentKey").GetString() ?? "";
        var typeStr = root.GetProperty("type").GetString() ?? "";

        if (!Enum.TryParse<ReplayEventType>(typeStr, ignoreCase: true, out var type))
            throw new InvalidOperationException($"Unknown event type at index {index}: {typeStr}");

        var payloadEl = root.GetProperty("payload");
        object payload = type switch
        {
            ReplayEventType.IntentRegistered => JsonSerializer.Deserialize<ReplayIntentRegistered>(payloadEl.GetRawText(), JsonOptions)
                ?? throw new InvalidOperationException($"IntentRegistered payload null at index {index}"),
            ReplayEventType.IntentPolicyRegistered => JsonSerializer.Deserialize<ReplayIntentPolicyRegistered>(payloadEl.GetRawText(), JsonOptions)
                ?? throw new InvalidOperationException($"IntentPolicyRegistered payload null at index {index}"),
            ReplayEventType.ExecutionUpdate => JsonSerializer.Deserialize<ReplayExecutionUpdate>(payloadEl.GetRawText(), JsonOptions)
                ?? throw new InvalidOperationException($"ExecutionUpdate payload null at index {index}"),
            ReplayEventType.OrderUpdate => JsonSerializer.Deserialize<ReplayOrderUpdate>(payloadEl.GetRawText(), JsonOptions)
                ?? throw new InvalidOperationException($"OrderUpdate payload null at index {index}"),
            ReplayEventType.Tick => JsonSerializer.Deserialize<ReplayTick>(payloadEl.GetRawText(), JsonOptions)
                ?? throw new InvalidOperationException($"Tick payload null at index {index}"),
            _ => throw new InvalidOperationException($"Unhandled type at index {index}: {type}")
        };

        return new ReplayEventEnvelope
        {
            Source = source,
            Sequence = sequence,
            ExecutionInstrumentKey = executionInstrumentKey,
            Type = type,
            Payload = payload
        };
    }
}
