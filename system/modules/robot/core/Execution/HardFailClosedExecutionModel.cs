using System;
using System.Collections.Concurrent;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Minimal hard fail-closed model: mismatch → broker <c>Account.Flatten</c> (once) → execution lock until operator unlock.
/// Separate from IEA queue, leg flatten, and journal auto-repair.
/// </summary>
public static class HardFailClosedExecutionModel
{
    private static readonly ConcurrentDictionary<string, byte> FlattenTriggered =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, byte> ExecutionLocked =
        new(StringComparer.OrdinalIgnoreCase);

    private static string Norm(string? instrument) =>
        string.IsNullOrWhiteSpace(instrument) ? "" : instrument.Trim();

    /// <summary>Returns true if this is the first time we arm one-shot broker flatten for this instrument in the process.</summary>
    public static bool TryArmOneShotBrokerFlatten(string instrument)
    {
        var k = Norm(instrument);
        if (string.IsNullOrEmpty(k)) return false;
        return FlattenTriggered.TryAdd(k, 1);
    }

    public static bool WasBrokerFlattenOneShotConsumed(string instrument) =>
        !string.IsNullOrEmpty(Norm(instrument)) && FlattenTriggered.ContainsKey(Norm(instrument));

    public static void MarkExecutionLocked(string instrument)
    {
        var k = Norm(instrument);
        if (string.IsNullOrEmpty(k)) return;
        ExecutionLocked[k] = 1;
    }

    public static bool IsHardExecutionLocked(string instrument) =>
        !string.IsNullOrEmpty(Norm(instrument)) && ExecutionLocked.ContainsKey(Norm(instrument));

    /// <summary>Clears one-shot + execution lock (call after successful operator unlock / structural safe clear).</summary>
    public static void ClearInstrumentHardState(string instrument)
    {
        var k = Norm(instrument);
        if (string.IsNullOrEmpty(k)) return;
        FlattenTriggered.TryRemove(k, out _);
        ExecutionLocked.TryRemove(k, out _);
    }

    public static void ClearAllForTests()
    {
        FlattenTriggered.Clear();
        ExecutionLocked.Clear();
    }

    /// <summary>Unsafe for automatic journal repair: parity broken or authority not real-dominant. Defers <see cref="JournalParityStatus.INSUFFICIENT_DATA"/> to the existing deferred path.</summary>
    public static bool IsJournalRepairUnsafe(JournalParityResult parity, PositionAuthorityState authority)
    {
        if (parity == null) return true;
        if (parity.Status == JournalParityStatus.INSUFFICIENT_DATA)
            return false;
        if (parity.Status == JournalParityStatus.PARITY_PENDING_ALIGNMENT)
            return false;
        if (!parity.IsOk) return true;
        return authority != PositionAuthorityState.REAL_DOMINANT;
    }
}
