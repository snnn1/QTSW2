using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// [DEPRECATED — P8 refactor] Transient in-memory bridge between execution fill handling and mismatch assembly when
/// <see cref="ExecutionJournal.GetOpenJournalEntriesByInstrument"/> lags durable journal state.
/// Not a second ledger: short TTL, capped by broker-vs-journal gap, cleared when journal aligns.
/// 
/// Deprecation gate criteria (all must pass before removal):
/// 1. Ledger dual-run comparison assertions pass for 5+ full trading sessions with zero discrepancies.
/// 2. RestoreFromJournal determinism test passes at every reconciliation tick.
/// 3. No UnexplainedQty persists beyond PostFillAlignmentWindowMs during dual-run.
/// 4. Event-triggered snapshots confirm fill → ledger write → snapshot without gap.
/// </summary>
[Obsolete("P8: Targeted for deprecation once InstrumentOwnershipLedger dual-run is proven. See gate criteria above.")]
public sealed class PendingFillBridge
{
    private readonly object _lock = new();
    private readonly List<PendingEntry> _entries = new();
    private readonly RobotLogger _log;

    /// <summary>Drop pending rows older than this (wall clock).</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(5);

    public PendingFillBridge(RobotLogger log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    private sealed class PendingEntry
    {
        public string InstrumentKey { get; init; } = "";
        public int Qty { get; init; }
        public int SignedNet { get; init; }
        public DateTimeOffset ObservedUtc { get; init; }
        public string? IntentId { get; init; }
        public string? BrokerOrderId { get; init; }
    }

    /// <summary>Record an entry fill observed on the execution path (before journal read may catch up).</summary>
    public void RecordEntryFillObserved(
        string executionInstrumentKey,
        int qtyDelta,
        bool isLong,
        DateTimeOffset utcNow,
        string? intentId,
        string? brokerOrderId)
    {
        if (string.IsNullOrWhiteSpace(executionInstrumentKey) || qtyDelta <= 0) return;
        var key = executionInstrumentKey.Trim();
        var signedNet = isLong ? qtyDelta : -qtyDelta;
        lock (_lock)
        {
            PruneExpiredLocked(utcNow);
            _entries.Add(new PendingEntry
            {
                InstrumentKey = key,
                Qty = qtyDelta,
                SignedNet = signedNet,
                ObservedUtc = utcNow,
                IntentId = intentId,
                BrokerOrderId = brokerOrderId
            });
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "PENDING_FILL_BRIDGE_ADDED", state: "ENGINE",
                new
                {
                    instrument = key,
                    qty_delta = qtyDelta,
                    signed_net = signedNet,
                    intent_id = intentId ?? "",
                    broker_order_id = brokerOrderId ?? ""
                }));
        }
    }

    /// <summary>
    /// Compute gross/net overlays capped by the broker-vs-journal gap so external large positions are not masked.
    /// </summary>
    public (int GrossOverlay, int NetOverlay) GetEffectiveOverlays(
        string brokerInstrumentKey,
        string? canonicalInstrument,
        int journalGrossQty,
        int journalNetQty,
        int brokerGrossQtyAbs,
        int brokerNetQty,
        DateTimeOffset utcNow)
    {
        lock (_lock)
        {
            PruneExpiredLocked(utcNow);
            ClearIfJournalAlignedWithBrokerLocked(brokerInstrumentKey, canonicalInstrument, journalGrossQty, journalNetQty,
                brokerGrossQtyAbs, brokerNetQty, utcNow);

            var pendingGross = SumMatchingGrossLocked(brokerInstrumentKey, canonicalInstrument);
            var pendingNet = SumMatchingSignedNetLocked(brokerInstrumentKey, canonicalInstrument);

            var grossGap = Math.Max(0, brokerGrossQtyAbs - journalGrossQty);
            var grossOverlay = Math.Min(pendingGross, grossGap);

            var netGap = brokerNetQty - journalNetQty;
            var netOverlay = ComputeNetOverlay(pendingNet, netGap);

            return (grossOverlay, netOverlay);
        }
    }

    private static int ComputeNetOverlay(int pendingNetSum, int netGap)
    {
        if (pendingNetSum == 0 || netGap == 0) return 0;
        if (Math.Sign(pendingNetSum) != Math.Sign(netGap)) return 0;
        return Math.Sign(netGap) * Math.Min(Math.Abs(pendingNetSum), Math.Abs(netGap));
    }

    private void ClearIfJournalAlignedWithBrokerLocked(
        string brokerInstrumentKey,
        string? canonicalInstrument,
        int journalGrossQty,
        int journalNetQty,
        int brokerGrossQtyAbs,
        int brokerNetQty,
        DateTimeOffset utcNow)
    {
        if (journalGrossQty != brokerGrossQtyAbs || journalNetQty != brokerNetQty) return;
        var removed = _entries.RemoveAll(e =>
            MatchesInstrument(e.InstrumentKey, brokerInstrumentKey, canonicalInstrument));
        if (removed > 0)
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "PENDING_FILL_BRIDGE_CONSUMED", state: "ENGINE",
                new
                {
                    instrument = brokerInstrumentKey,
                    reason = "journal_aligned_with_broker",
                    removed = removed,
                    journal_gross = journalGrossQty,
                    broker_gross_abs = brokerGrossQtyAbs
                }));
    }

    private void PruneExpiredLocked(DateTimeOffset utcNow)
    {
        var cutoff = utcNow - DefaultTtl;
        var removed = _entries.RemoveAll(e => e.ObservedUtc < cutoff);
        if (removed > 0)
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "PENDING_FILL_BRIDGE_EXPIRED", state: "ENGINE",
                new { removed = removed, ttl_seconds = DefaultTtl.TotalSeconds }));
    }

    /// <summary>
    /// Match pending row to broker/journal assembly keys. Symmetric: micro execution (e.g. MNG) may be recorded while
    /// the account snapshot uses the base symbol (e.g. NG); <see cref="ExecutionJournal.OpenJournalMapBucketMatches"/> is directional.
    /// </summary>
    private static bool MatchesInstrument(string entryKey, string brokerInstrumentKey, string? canonicalInstrument)
    {
        var b = brokerInstrumentKey.Trim();
        if (string.IsNullOrEmpty(b)) return false;
        return ExecutionJournal.OpenJournalMapBucketMatches(entryKey, b, canonicalInstrument)
            || ExecutionJournal.OpenJournalMapBucketMatches(b, entryKey, canonicalInstrument);
    }

    private int SumMatchingGrossLocked(string brokerInstrumentKey, string? canonicalInstrument)
    {
        var sum = 0;
        foreach (var e in _entries)
        {
            if (MatchesInstrument(e.InstrumentKey, brokerInstrumentKey, canonicalInstrument))
                sum += e.Qty;
        }
        return sum;
    }

    private int SumMatchingSignedNetLocked(string brokerInstrumentKey, string? canonicalInstrument)
    {
        var sum = 0;
        foreach (var e in _entries)
        {
            if (MatchesInstrument(e.InstrumentKey, brokerInstrumentKey, canonicalInstrument))
                sum += e.SignedNet;
        }
        return sum;
    }
}
