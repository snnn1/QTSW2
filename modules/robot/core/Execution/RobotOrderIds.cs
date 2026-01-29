using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Centralized, strict robot order identity envelope.
/// - All robot orders MUST have Tag starting with "QTSW2:".
/// - All robot OCO groups MUST start with "QTSW2:".
/// - Internal identity remains the raw intentId; Tag is an envelope.
/// </summary>
public static class RobotOrderIds
{
    public const string Prefix = "QTSW2:";

    public static string EncodeTag(string intentId) =>
        $"{Prefix}{intentId}";

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
    /// Strict decode:
    /// - Require StartsWith("QTSW2:")
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

        var idx = remainder.IndexOf(':');
        if (idx < 0)
            return remainder;

        var baseToken = remainder.Substring(0, idx);
        return string.IsNullOrEmpty(baseToken) ? null : baseToken;
    }
}

