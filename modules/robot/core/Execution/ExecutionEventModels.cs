// Gap 5: Canonical execution event envelope and models.
// Single authoritative replay source for execution behavior, lifecycle, protective recovery, mismatch escalation.

using System;
using System.Text.Json.Serialization;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// High-level event family for canonical execution events.
/// </summary>
public enum ExecutionEventFamily
{
    COMMAND,
    ORDER,
    EXECUTION,
    LIFECYCLE,
    PROTECTIVE,
    MISMATCH,
    SUPERVISORY,
    RECONCILIATION,
    TERMINAL
}

/// <summary>
/// Severity for canonical events.
/// </summary>
public enum CanonicalEventSeverity
{
    INFO,
    WARN,
    ERROR,
    CRITICAL
}

/// <summary>
/// Canonical execution event envelope. Every execution-relevant change must produce one of these.
/// Stable schema, append-only, sufficient for deterministic replay.
/// </summary>
public sealed class CanonicalExecutionEvent
{
    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = "";

    [JsonPropertyName("event_sequence")]
    public long EventSequence { get; set; }

    [JsonPropertyName("event_family")]
    public string EventFamily { get; set; } = "";

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = "";

    [JsonPropertyName("timestamp_utc")]
    public string TimestampUtc { get; set; } = "";

    [JsonPropertyName("trading_date")]
    public string? TradingDate { get; set; }

    [JsonPropertyName("instrument")]
    public string? Instrument { get; set; }

    [JsonPropertyName("stream_key")]
    public string? StreamKey { get; set; }

    [JsonPropertyName("intent_id")]
    public string? IntentId { get; set; }

    [JsonPropertyName("command_id")]
    public string? CommandId { get; set; }

    [JsonPropertyName("broker_order_id")]
    public string? BrokerOrderId { get; set; }

    [JsonPropertyName("order_id")]
    public string? OrderId { get; set; }

    [JsonPropertyName("execution_id")]
    public string? ExecutionId { get; set; }

    [JsonPropertyName("lifecycle_state_before")]
    public string? LifecycleStateBefore { get; set; }

    [JsonPropertyName("lifecycle_state_after")]
    public string? LifecycleStateAfter { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "INFO";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
}
