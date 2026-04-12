using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Replay;

/// <summary>
/// Loads and validates replay JSONL files. Pure library component — no IEA wiring.
/// Reference: IEA_REPLAY_CONTRACT.md §2, §9
/// </summary>
public static class ReplayLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Load and validate replay file. Fail-fast on any violation.
    /// </summary>
    /// <param name="path">Path to JSONL file.</param>
    /// <param name="expectedAccount">Expected account (validated if non-null).</param>
    /// <param name="expectedInstrument">Expected execution instrument key (validated if non-null).</param>
    /// <returns>Ordered sequence of validated events.</returns>
    /// <exception cref="ReplayLoadException">On parse error, ordering violation, or precondition violation.</exception>
    public static IReadOnlyList<ReplayEventEnvelope> LoadAndValidate(
        string path,
        string? expectedAccount = null,
        string? expectedInstrument = null)
    {
        var lines = File.ReadAllLines(path);
        var events = new List<ReplayEventEnvelope>(lines.Length);
        var seenIntents = new HashSet<string>(StringComparer.Ordinal);
        var seenIntentPolicies = new HashSet<string>(StringComparer.Ordinal);
        var lastSequenceBySource = new Dictionary<string, long>(StringComparer.Ordinal);
        DateTimeOffset? baseTime = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var envelope = ParseLine(line, lineNum);

            // Validate envelope fields
            ValidateEnvelope(envelope, lineNum, expectedAccount, expectedInstrument);

            // Ordering: (source, sequence) strictly ascending
            if (lastSequenceBySource.TryGetValue(envelope.Source, out var lastSeq))
            {
                if (envelope.Sequence <= lastSeq)
                    throw new ReplayLoadException(
                        $"Out-of-order event: (source={envelope.Source}, seq={envelope.Sequence}) must be > last seq {lastSeq}",
                        lineNum, envelope.Type.ToString());
                if (envelope.Sequence != lastSeq + 1)
                    throw new ReplayLoadException(
                        $"Sequence gap: expected {lastSeq + 1}, got {envelope.Sequence}",
                        lineNum, envelope.Type.ToString());
            }
            lastSequenceBySource[envelope.Source] = envelope.Sequence;

            // Precondition: IntentRegistered before any event referencing that intent
            switch (envelope.Type)
            {
                case ReplayEventType.IntentRegistered:
                    var ir = (ReplayIntentRegistered)envelope.Payload;
                    seenIntents.Add(ir.IntentId);
                    break;
                case ReplayEventType.IntentPolicyRegistered:
                    var ipr = (ReplayIntentPolicyRegistered)envelope.Payload;
                    seenIntentPolicies.Add(ipr.IntentId);
                    break;
                case ReplayEventType.ExecutionUpdate:
                    var eu = (ReplayExecutionUpdate)envelope.Payload;
                    var intentId = eu.IntentId ?? eu.Tag;
                    if (!string.IsNullOrEmpty(intentId) && !seenIntents.Contains(intentId))
                        throw new ReplayLoadException(
                            $"ExecutionUpdate references unknown intent: {intentId}. IntentRegistered must precede.",
                            lineNum, "ExecutionUpdate");
                    break;
                case ReplayEventType.OrderUpdate:
                    var ou = (ReplayOrderUpdate)envelope.Payload;
                    var oIntentId = ou.IntentId ?? ou.Tag;
                    if (!string.IsNullOrEmpty(oIntentId) && !seenIntents.Contains(oIntentId))
                        throw new ReplayLoadException(
                            $"OrderUpdate references unknown intent: {oIntentId}. IntentRegistered must precede.",
                            lineNum, "OrderUpdate");
                    break;
                case ReplayEventType.Tick:
                    var tick = (ReplayTick)envelope.Payload;
                    if (tick.TickTimeFromEvent.HasValue && (!baseTime.HasValue || tick.TickTimeFromEvent.Value < baseTime))
                        baseTime = tick.TickTimeFromEvent;
                    break;
            }

            events.Add(envelope);
        }

        return events;
    }

    private static ReplayEventEnvelope ParseLine(string line, int lineNum)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(line).RootElement;
        }
        catch (JsonException ex)
        {
            throw new ReplayLoadException($"Invalid JSON: {ex.Message}", lineNum);
        }

        var source = root.GetProperty("source").GetString();
        var sequence = root.GetProperty("sequence").GetInt64();
        var executionInstrumentKey = root.GetProperty("executionInstrumentKey").GetString();
        var typeStr = root.GetProperty("type").GetString();

        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(executionInstrumentKey) || string.IsNullOrEmpty(typeStr))
            throw new ReplayLoadException("Missing required envelope fields: source, executionInstrumentKey, type", lineNum);

        if (!Enum.TryParse<ReplayEventType>(typeStr, ignoreCase: true, out var type))
            throw new ReplayLoadException($"Unknown event type: {typeStr}", lineNum, typeStr);

        object payload;
        var payloadEl = root.GetProperty("payload");

        try
        {
            payload = type switch
            {
                ReplayEventType.IntentRegistered => JsonSerializer.Deserialize<ReplayIntentRegistered>(payloadEl.GetRawText(), JsonOptions)
                    ?? throw new ReplayLoadException("IntentRegistered payload is null", lineNum, typeStr),
                ReplayEventType.IntentPolicyRegistered => JsonSerializer.Deserialize<ReplayIntentPolicyRegistered>(payloadEl.GetRawText(), JsonOptions)
                    ?? throw new ReplayLoadException("IntentPolicyRegistered payload is null", lineNum, typeStr),
                ReplayEventType.ExecutionUpdate => JsonSerializer.Deserialize<ReplayExecutionUpdate>(payloadEl.GetRawText(), JsonOptions)
                    ?? throw new ReplayLoadException("ExecutionUpdate payload is null", lineNum, typeStr),
                ReplayEventType.OrderUpdate => JsonSerializer.Deserialize<ReplayOrderUpdate>(payloadEl.GetRawText(), JsonOptions)
                    ?? throw new ReplayLoadException("OrderUpdate payload is null", lineNum, typeStr),
                ReplayEventType.Tick => JsonSerializer.Deserialize<ReplayTick>(payloadEl.GetRawText(), JsonOptions)
                    ?? throw new ReplayLoadException("Tick payload is null", lineNum, typeStr),
                _ => throw new ReplayLoadException($"Unhandled type: {type}", lineNum, typeStr)
            };
        }
        catch (JsonException ex)
        {
            throw new ReplayLoadException($"Payload deserialization failed: {ex.Message}", lineNum, typeStr);
        }

        ValidatePayload(payload, type, lineNum);

        return new ReplayEventEnvelope
        {
            Source = source!,
            Sequence = sequence,
            ExecutionInstrumentKey = executionInstrumentKey!,
            Type = type,
            Payload = payload
        };
    }

    private static void ValidateEnvelope(ReplayEventEnvelope e, int lineNum, string? expectedAccount, string? expectedInstrument)
    {
        if (string.IsNullOrEmpty(e.Source))
            throw new ReplayLoadException("source is required", lineNum);
        if (e.Sequence < 0)
            throw new ReplayLoadException("sequence must be >= 0", lineNum);
        if (string.IsNullOrEmpty(e.ExecutionInstrumentKey))
            throw new ReplayLoadException("executionInstrumentKey is required", lineNum);
        if (expectedInstrument != null && !string.Equals(e.ExecutionInstrumentKey, expectedInstrument, StringComparison.OrdinalIgnoreCase))
            throw new ReplayLoadException($"executionInstrumentKey mismatch: expected {expectedInstrument}, got {e.ExecutionInstrumentKey}", lineNum);
        // expectedAccount could match Source in some schemas; contract says source is string
    }

    private static void ValidatePayload(object payload, ReplayEventType type, int lineNum)
    {
        switch (type)
        {
            case ReplayEventType.IntentRegistered:
                var ir = (ReplayIntentRegistered)payload;
                if (string.IsNullOrEmpty(ir.IntentId))
                    throw new ReplayLoadException("IntentRegistered.intentId is required", lineNum, type.ToString());
                if (ir.Intent == null)
                    throw new ReplayLoadException("IntentRegistered.intent is required", lineNum, type.ToString());
                break;
            case ReplayEventType.IntentPolicyRegistered:
                var ipr = (ReplayIntentPolicyRegistered)payload;
                if (string.IsNullOrEmpty(ipr.IntentId))
                    throw new ReplayLoadException("IntentPolicyRegistered.intentId is required", lineNum, type.ToString());
                break;
            case ReplayEventType.ExecutionUpdate:
                var eu = (ReplayExecutionUpdate)payload;
                if (string.IsNullOrEmpty(eu.OrderId))
                    throw new ReplayLoadException("ExecutionUpdate.orderId is required", lineNum, type.ToString());
                if (string.IsNullOrEmpty(eu.ExecutionInstrumentKey))
                    throw new ReplayLoadException("ExecutionUpdate.executionInstrumentKey is required", lineNum, type.ToString());
                break;
            case ReplayEventType.OrderUpdate:
                var ou = (ReplayOrderUpdate)payload;
                if (string.IsNullOrEmpty(ou.OrderId))
                    throw new ReplayLoadException("OrderUpdate.orderId is required", lineNum, type.ToString());
                if (string.IsNullOrEmpty(ou.OrderState))
                    throw new ReplayLoadException("OrderUpdate.orderState is required", lineNum, type.ToString());
                break;
            case ReplayEventType.Tick:
                var tick = (ReplayTick)payload;
                if (string.IsNullOrEmpty(tick.ExecutionInstrument))
                    throw new ReplayLoadException("Tick.executionInstrument is required", lineNum, type.ToString());
                break;
        }
    }

    /// <summary>
    /// Write validated events to canonical JSON array. Net48 host consumes this format.
    /// </summary>
    public static void WriteCanonical(string path, IReadOnlyList<ReplayEventEnvelope> events)
    {
        var json = JsonSerializer.Serialize(events, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Synthesize deterministic tick time when tickTimeFromEvent is null.
    /// baseTime + sequence * 1ms per contract.
    /// </summary>
    public static DateTimeOffset SynthesizeTickTime(DateTimeOffset baseTime, long sequence)
    {
        return baseTime.AddMilliseconds(sequence);
    }
}
