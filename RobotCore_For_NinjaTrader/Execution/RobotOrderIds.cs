using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Centralized, strict robot order identity envelope.
/// - All robot orders MUST have Tag starting with "QTSW2:".
/// - All robot OCO groups MUST start with "QTSW2:".
/// - Internal identity remains the raw intentId; Tag is an envelope.
/// - Aggregated orders use "QTSW2:AGG:id1,id2,..." for multi-stream attribution.
/// </summary>
public static class RobotOrderIds
{
    public const string Prefix = "QTSW2:";
    public const string AggregatedPrefix = "QTSW2:AGG:";

    public static string EncodeTag(string intentId) =>
        $"{Prefix}{intentId}";

    /// <summary>
    /// Encode tag for aggregated order (multiple streams, one broker order).
    /// Format: QTSW2:AGG:intent1,intent2,...
    /// </summary>
    public static string EncodeAggregatedTag(IReadOnlyList<string> intentIds)
    {
        if (intentIds == null || intentIds.Count == 0)
            throw new ArgumentException("At least one intent ID required", nameof(intentIds));
        if (intentIds.Count == 1)
            return EncodeTag(intentIds[0]);
        return $"{AggregatedPrefix}{string.Join(",", intentIds)}";
    }

    public static string EncodeStopTag(string intentId) =>
        $"{Prefix}{intentId}:STOP";

    public static string EncodeTargetTag(string intentId) =>
        $"{Prefix}{intentId}:TARGET";

    /// <summary>
    /// Encode OCO group ID for entry orders.
    /// CRITICAL: Must be unique per OCO group - NinjaTrader doesn't allow reusing OCO IDs.
    /// Adds GUID to ensure uniqueness even if same stream locks multiple times on same date/slot.
    /// </summary>
    public static string EncodeEntryOco(string tradingDate, string stream, string slotTimeChicago) =>
        $"{Prefix}OCO_ENTRY:{tradingDate}:{stream}:{slotTimeChicago}:{Guid.NewGuid():N}";

    /// <summary>
    /// Check if tag represents an aggregated order.
    /// </summary>
    public static bool IsAggregatedTag(string? tag) =>
        !string.IsNullOrEmpty(tag) && tag.StartsWith(AggregatedPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decode aggregated intent IDs from tag. Returns null if not an aggregated tag.
    /// </summary>
    public static IReadOnlyList<string>? DecodeAggregatedIntentIds(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || !tag.StartsWith(AggregatedPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var remainder = tag.Substring(AggregatedPrefix.Length);
        if (string.IsNullOrEmpty(remainder))
            return null;
        return remainder.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    /// <summary>
    /// Strict decode:
    /// - Require StartsWith("QTSW2:")
    /// - For AGG tags, returns first intent ID (call DecodeAggregatedIntentIds for full list)
    /// - Extract base token after prefix up to first ':' (role suffix delimiter)
    /// - Everything after is treated as role metadata (STOP/TARGET/ENTRY/BE/etc)
    /// - If not prefixed, treat as non-robot (return null)
    /// </summary>
    public static string? DecodeIntentId(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
            return null;

        if (!tag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var remainder = tag.Substring(Prefix.Length);
        if (string.IsNullOrEmpty(remainder))
            return null;

        // AGG:id1,id2,... - return first intent for backward compat
        if (remainder.StartsWith("AGG:", StringComparison.OrdinalIgnoreCase))
        {
            var ids = DecodeAggregatedIntentIds(tag);
            return ids?.Count > 0 ? ids[0] : null;
        }

        var idx = remainder.IndexOf(':');
        if (idx < 0)
            return remainder;

        var baseToken = remainder.Substring(0, idx);
        return string.IsNullOrEmpty(baseToken) ? null : baseToken;
    }
}

