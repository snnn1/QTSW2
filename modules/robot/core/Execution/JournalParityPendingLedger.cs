using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// In-process only: explains broker vs journal timing gaps for <see cref="JournalParityStatus.PARITY_PENDING_ALIGNMENT"/>.
/// Cleared on new <see cref="RobotEngine"/> session; never persisted (recovery mode must not use pending).
/// </summary>
public static class JournalParityPendingLedger
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, JournalParityPendingEntry>> ByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Process / engine session boundary: empty pending (R1 — not persistent).</summary>
    public static void Clear() => ByInstrument.Clear();

    /// <summary>
    /// Trusted execution path only (I3). Idempotent per <paramref name="executionDedupeKey"/> (I1).
    /// Call before journal persists the same fill so parity can classify the gap as pending.
    /// </summary>
    public static void TryRecordTrustedFill(
        string instrument,
        string executionDedupeKey,
        int signedQuantity,
        string intentId,
        DateTimeOffset utcNow)
    {
        var inst = instrument?.Trim() ?? "";
        var key = executionDedupeKey?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst) || string.IsNullOrEmpty(key)) return;
        var inner = ByInstrument.GetOrAdd(inst,
            _ => new ConcurrentDictionary<string, JournalParityPendingEntry>(StringComparer.Ordinal));
        inner.TryAdd(key, new JournalParityPendingEntry
        {
            ExecutionDedupeKey = key,
            SignedQuantity = signedQuantity,
            IntentId = intentId?.Trim() ?? "",
            RecordedUtc = utcNow
        });
    }

    /// <summary>Remove when journal has persisted the matching fill (idempotent).</summary>
    public static void TryRemove(string instrument, string? executionDedupeKey)
    {
        if (string.IsNullOrWhiteSpace(executionDedupeKey)) return;
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return;
        if (!ByInstrument.TryGetValue(inst, out var inner)) return;
        inner.TryRemove(executionDedupeKey.Trim(), out _);
        if (inner.Count == 0)
            ByInstrument.TryRemove(inst, out _);
    }

    /// <summary>Test hook: remove entry without journal callback.</summary>
    public static void TryRemoveForTests(string instrument, string executionDedupeKey) =>
        TryRemove(instrument, executionDedupeKey);

    internal static List<JournalParityPendingEntry> SnapshotOrdered(string instrument)
    {
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return new List<JournalParityPendingEntry>();
        if (!ByInstrument.TryGetValue(inst, out var inner)) return new List<JournalParityPendingEntry>();
        return inner.Values.OrderBy(e => e.RecordedUtc).ToList();
    }
}

/// <summary>One trusted fill not yet cleared from the ledger (may still be valid for I5).</summary>
public sealed class JournalParityPendingEntry
{
    public string ExecutionDedupeKey { get; init; } = "";
    public int SignedQuantity { get; init; }
    public string IntentId { get; init; } = "";
    public DateTimeOffset RecordedUtc { get; init; }
}
