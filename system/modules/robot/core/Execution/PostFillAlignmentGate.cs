using System;
using System.Collections.Concurrent;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Bounded post-fill alignment window: when a mapped trusted fill arms this gate, broker-vs-journal deltas
/// within the accumulated expected magnitude and before expiry classify as <see cref="JournalParityStatus.PARITY_PENDING_ALIGNMENT"/>
/// even if <see cref="JournalParityPendingLedger"/> entries were already removed (e.g. journal persisted dedupe key first).
/// In-process only; cleared on session boundary with <see cref="JournalParityPendingLedger.Clear"/>.
/// </summary>
public static class PostFillAlignmentGate
{
    private sealed class GateState
    {
        public int CumulativeExpectedAbs;
        public DateTimeOffset ExpiryUtc;
        public string Cause = "mapped_trusted_fill";
    }

    private static readonly ConcurrentDictionary<string, GateState> ByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Process / engine session boundary (with <see cref="JournalParityPendingLedger.Clear"/>).</summary>
    public static void Clear() => ByInstrument.Clear();

    /// <summary>
    /// Trusted mapped fill path only — pairs with <see cref="JournalParityPendingLedger.TryRecordTrustedFill"/>.
    /// </summary>
    public static void ArmTrustedMappedFill(string instrument, int absSignedQuantity, string? cause, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.EnablePostFillAlignmentGate || absSignedQuantity <= 0) return;
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return;
        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        var expiry = utcNow.AddMilliseconds(windowMs);
        ByInstrument.AddOrUpdate(
            inst,
            _ => new GateState
            {
                CumulativeExpectedAbs = absSignedQuantity,
                ExpiryUtc = expiry,
                Cause = string.IsNullOrWhiteSpace(cause) ? "mapped_trusted_fill" : cause.Trim()
            },
            (_, st) =>
            {
                st.CumulativeExpectedAbs += absSignedQuantity;
                if (expiry > st.ExpiryUtc)
                    st.ExpiryUtc = expiry;
                if (!string.IsNullOrWhiteSpace(cause))
                    st.Cause = cause.Trim();
                return st;
            });
    }

    /// <summary>Clears gate state when broker and journal structural view match (PARITY_OK path).</summary>
    public static void ClearOnParityOk(string instrument)
    {
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return;
        ByInstrument.TryRemove(inst, out _);
        QuantExecutionControlStore.NotifyParityOkBrokerJournalAligned(inst, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// True when a mapped trusted fill armed a non-expired alignment window for this instrument
    /// (<see cref="PendingAlignmentAuthority"/> shared truth).
    /// </summary>
    public static bool IsActiveAlignmentWindow(string instrument, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.EnablePostFillAlignmentGate) return false;
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return false;
        if (!ByInstrument.TryGetValue(inst, out var st)) return false;
        if (utcNow > st.ExpiryUtc)
        {
            ByInstrument.TryRemove(inst, out _);
            return false;
        }

        return true;
    }

    internal static bool TryClassifyPendingAlignment(
        string instrument,
        DateTimeOffset utcNow,
        int posDiff,
        int absSignedDelta,
        int brokerAbs,
        int journalStructural,
        int unexplainedW,
        long? ageMs,
        out JournalParityResult result)
    {
        result = null!;
        if (!FeatureFlags.EnablePostFillAlignmentGate) return false;
        var inst = instrument.Trim();
        if (!ByInstrument.TryGetValue(inst, out var st)) return false;

        if (utcNow > st.ExpiryUtc)
        {
            ByInstrument.TryRemove(inst, out _);
            return false;
        }

        if (posDiff > st.CumulativeExpectedAbs)
            return false;

        if (posDiff != absSignedDelta)
            return false;

        result = new JournalParityResult
        {
            Status = JournalParityStatus.PARITY_PENDING_ALIGNMENT,
            BrokerPositionQty = brokerAbs,
            JournalOpenQty = journalStructural,
            UnexplainedPositionQty = 0,
            UnexplainedWorkingOrders = unexplainedW,
            OrphanOrdersDetected = 0,
            SnapshotAgeMs = ageMs,
            PendingAlignmentCause = st.Cause,
            ExpectedFillDeltaAbs = st.CumulativeExpectedAbs,
            EscalationSuppressed = true
        };
        return true;
    }
}
