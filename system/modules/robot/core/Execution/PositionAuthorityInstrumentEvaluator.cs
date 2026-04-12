using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Derives <see cref="DerivedPositionAuthority"/> for a stream using broker snapshot + journal split (same inputs as execution safety gate).
/// </summary>
public static class PositionAuthorityInstrumentEvaluator
{
    public static DerivedPositionAuthority Derive(
        AccountSnapshot? snapshot,
        ExecutionJournal journal,
        string executionInstrument,
        string? canonicalInstrument)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(executionInstrument))
            return DerivedPositionAuthority.UNKNOWN;

        var inst = executionInstrument.Trim();
        var canon = string.IsNullOrWhiteSpace(canonicalInstrument) ? inst : canonicalInstrument.Trim();
        var brokerAbs = SumAbsBrokerPositionForInstrument(snapshot, inst);
        var (real, rec, _) = journal.GetPositionAuthorityOpenQuantitiesForInstrument(inst, canon);
        return PositionAuthorityDerivation.DerivePositionAuthority(brokerAbs, real, rec);
    }

    /// <summary>Builds the payload for POSITION_AUTHORITY_EVALUATED (adapter — same inputs as <see cref="Derive"/>).</summary>
    public static PositionAuthorityEvaluatedArgs BuildEvaluatedArgs(
        AccountSnapshot? snapshot,
        ExecutionJournal journal,
        string executionInstrument,
        string? canonicalInstrument)
    {
        var inst = executionInstrument?.Trim() ?? "";
        var canon = string.IsNullOrWhiteSpace(canonicalInstrument) ? inst : canonicalInstrument.Trim();
        var brokerAbs = snapshot == null ? 0 : SumAbsBrokerPositionForInstrument(snapshot, inst);
        var (real, rec, _) = journal.GetPositionAuthorityOpenQuantitiesForInstrument(inst, canon);
        var derived = PositionAuthorityDerivation.DerivePositionAuthority(brokerAbs, real, rec);
        return new PositionAuthorityEvaluatedArgs
        {
            Instrument = inst,
            BrokerQty = brokerAbs,
            RealOpenQty = real,
            RecoveryOpenQty = rec,
            JournalOpenQty = real + rec,
            AuthorityState = derived.ToString()
        };
    }

    private static int SumAbsBrokerPositionForInstrument(AccountSnapshot snap, string inst)
    {
        var sum = 0;
        foreach (var p in snap.Positions ?? new List<PositionSnapshot>())
        {
            if (string.IsNullOrWhiteSpace(p.Instrument)) continue;
            if (!string.Equals(p.Instrument.Trim(), inst, StringComparison.OrdinalIgnoreCase)) continue;
            sum += Math.Abs(p.Quantity);
        }

        return sum;
    }
}
