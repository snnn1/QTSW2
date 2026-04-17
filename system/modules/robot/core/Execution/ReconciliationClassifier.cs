using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Pure classifier: <c>(AccountSnapshot, OwnershipSnapshot[]) -> ReconciliationVerdict[]</c>.
/// Reads broker state and ledger state only — does NOT read ExecutionJournal rows.
/// If a ledger write occurs mid-pass (detected by version check), the verdict for that
/// instrument is marked STALE and the orchestrator re-runs classification for that instrument only.
/// </summary>
public sealed class ReconciliationClassifier
{
    /// <summary>
    /// Wall-clock window (ms) before an orphan slot that persists beyond this window is escalated to HARD_MISMATCH.
    /// Defined here because it is a classification threshold, not a supervisor policy or ledger parameter.
    /// </summary>
    public int OrphanEscalationWindowMs { get; set; } = FeatureFlags.TransientMismatchWindowMs;

    private readonly InstrumentOwnershipLedger _ledger;
    private readonly RobotLogger _log;

    public ReconciliationClassifier(InstrumentOwnershipLedger ledger, RobotLogger log)
    {
        _ledger = ledger;
        _log = log;
    }

    /// <summary>
    /// Classify reconciliation verdicts for the caller's engine-scoped instruments.
    /// Each verdict operates on a consistent (AccountSnapshot, OwnershipSnapshot@version_V) pair.
    /// </summary>
    public IReadOnlyList<ReconciliationVerdict> Classify(
        AccountSnapshot accountSnapshot,
        string account,
        IReadOnlyList<string> instrumentsInScope,
        DateTimeOffset utcNow)
    {
        var verdicts = new List<ReconciliationVerdict>();

        var brokerQtyByInstrument = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (accountSnapshot.Positions != null)
        {
            foreach (var pos in accountSnapshot.Positions)
            {
                var instKey = pos.Instrument?.Trim() ?? "";
                if (string.IsNullOrEmpty(instKey)) continue;
                brokerQtyByInstrument[instKey] = pos.Quantity;
            }
        }

        var allInstruments = new HashSet<string>(instrumentsInScope, StringComparer.OrdinalIgnoreCase);

        foreach (var instrument in allInstruments)
        {
            var snapshot = _ledger.GetOwnershipSnapshot(account, instrument);
            var versionAtStart = snapshot.OwnershipVersion;
            brokerQtyByInstrument.TryGetValue(instrument, out var brokerQty);

            var brokerSignedQty = NormalizeBrokerSignedQty(brokerQty, snapshot);
            var unexplainedQty = snapshot.ComputeUnexplainedQty(brokerSignedQty);
            var mismatchAgeMs = snapshot.ComputeMismatchAgeMs();

            MismatchTier tier;
            if (unexplainedQty == 0)
            {
                tier = MismatchTier.Convergence;
            }
            else if (mismatchAgeMs < FeatureFlags.TransientMismatchWindowMs)
            {
                tier = MismatchTier.TransientMismatch;
            }
            else
            {
                tier = MismatchTier.HardMismatch;
            }

            var subType = VerdictSubType.None;
            if (snapshot.OrphanSlotCount > 0)
            {
                var hasLongLivedOrphan = snapshot.Slots
                    .Any(s => s.State == SlotState.Orphan && s.Remaining > 0 && mismatchAgeMs > OrphanEscalationWindowMs);
                if (hasLongLivedOrphan)
                {
                    tier = MismatchTier.HardMismatch;
                    subType = VerdictSubType.OrphanPersistsBeyondWindow;
                }
            }

            var snapshotAfter = _ledger.GetOwnershipSnapshot(account, instrument);
            var isStale = snapshotAfter.OwnershipVersion != versionAtStart;

            var confidence = VerdictConfidence.High;
            if (isStale) confidence = VerdictConfidence.Low;
            else if (tier == MismatchTier.TransientMismatch) confidence = VerdictConfidence.Medium;

            verdicts.Add(new ReconciliationVerdict
            {
                Instrument = instrument,
                BrokerQty = brokerQty,
                BrokerSignedQty = brokerSignedQty,
                LedgerQty = snapshot.LedgerSignedNetQty,
                JournalOpenQty = snapshot.Slots.Where(s => s.State != SlotState.Closed).Sum(s => s.Remaining),
                OwnershipVersion = versionAtStart,
                MismatchTier = tier,
                MismatchAgeMs = mismatchAgeMs,
                IsStale = isStale,
                Confidence = confidence,
                ClassifiedUtc = utcNow,
                ActiveSlotCount = snapshot.ActiveSlotCount,
                OrphanSlotCount = snapshot.OrphanSlotCount,
                SubType = subType
            });
        }

        return verdicts;
    }

    private static int NormalizeBrokerSignedQty(int brokerQty, InstrumentOwnershipSnapshot snapshot)
    {
        if (brokerQty <= 0) return brokerQty;

        if (snapshot.LedgerSignedNetQty < 0)
            return -Math.Abs(brokerQty);

        if (snapshot.LedgerSignedNetQty > 0)
            return Math.Abs(brokerQty);

        var directions = snapshot.Slots
            .Where(s => s.State != SlotState.Closed && s.Remaining > 0)
            .Select(s => s.Direction)
            .Distinct()
            .ToList();

        return directions.Count == 1 && directions[0] == SlotDirection.Short
            ? -Math.Abs(brokerQty)
            : brokerQty;
    }
}
