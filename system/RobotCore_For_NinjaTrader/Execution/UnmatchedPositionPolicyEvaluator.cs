using System;
using System.Collections.Generic;
using System.Linq;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Deterministic policy for RECOVERY_POSITION_UNMATCHED: ownership_proven with strict evidence gates, else FLATTEN.
/// Behavior: IF ownership_proven AND adoption_supported → adopt; ELSE → flatten.
/// No heuristic guessing. Bias: false flatten over false adoption.
/// </summary>
public enum UnmatchedPositionPolicyDecision
{
    OWNERSHIP_PROVEN,
    FLATTEN
}

/// <summary>
/// Result of policy evaluation for a single unmatched position.
/// </summary>
public sealed class UnmatchedPositionPolicyResult
{
    public UnmatchedPositionPolicyDecision Decision { get; set; }
    public string? CandidateIntentId { get; set; }
    public string Reason { get; set; } = "";
    public int CandidateCount { get; set; }
    public string Instrument { get; set; } = "";
    public int BrokerQty { get; set; }
}

/// <summary>
/// Deterministic evaluator for unmatched broker positions during recovery.
/// Implements: prove ownership or flatten. No indefinite hold. No operator dependency.
/// </summary>
public static class UnmatchedPositionPolicyEvaluator
{
    /// <summary>
    /// Evaluate policy for a single unmatched position.
    /// Returns OWNERSHIP_PROVEN only if ALL hard gates pass AND at least one continuity evidence (A/B/C) passes.
    /// </summary>
    public static UnmatchedPositionPolicyResult Evaluate(
        PositionSnapshot position,
        AccountSnapshot snapshot,
        ExecutionJournal journal,
        string tradingDate,
        string? runId,
        RobotLogger? log)
    {
        var instrument = (position.Instrument ?? "").Trim();
        var brokerQty = Math.Abs(position.Quantity);
        var result = new UnmatchedPositionPolicyResult
        {
            Instrument = instrument,
            BrokerQty = brokerQty,
            Decision = UnmatchedPositionPolicyDecision.FLATTEN,
            Reason = "UNEVALUATED"
        };

        if (string.IsNullOrEmpty(instrument))
        {
            result.Reason = "INSTRUMENT_EMPTY";
            return result;
        }

        // Resolve execution instrument for journal lookup (YM→MYM, ES→MES, etc.)
        var execVariant = instrument.StartsWith("M", StringComparison.OrdinalIgnoreCase) && instrument.Length > 1
            ? instrument
            : "M" + instrument;
        var canonical = GetCanonicalForPositionInstrument(instrument);

        // Get adoption candidates: EntrySubmitted && !TradeCompleted
        var candidateIds = journal.GetAdoptionCandidateIntentIdsForInstrument(execVariant, canonical);
        if (candidateIds.Count == 0)
        {
            candidateIds = journal.GetAdoptionCandidateIntentIdsForInstrument(instrument, null);
        }

        result.CandidateCount = candidateIds.Count;

        // Hard gate: Single unambiguous candidate
        if (candidateIds.Count == 0)
        {
            result.Reason = "NO_CANDIDATE";
            return result;
        }
        if (candidateIds.Count > 1)
        {
            result.Reason = "MULTIPLE_CANDIDATES";
            return result;
        }

        var intentId = candidateIds.First();

        // Hard gate: Journal evidence - EntrySubmitted, !TradeCompleted
        var journalEntry = journal.TryGetAdoptionCandidateEntry(intentId, execVariant, canonical)
            ?? journal.TryGetAdoptionCandidateEntry(intentId, instrument, null);
        if (journalEntry == null)
        {
            result.Reason = "NO_JOURNAL_ENTRY";
            return result;
        }
        if (!journalEntry.Value.Entry.EntrySubmitted || journalEntry.Value.Entry.TradeCompleted)
        {
            result.Reason = "JOURNAL_STATE_INVALID";
            return result;
        }

        // Hard gate: Quantity match (exact)
        var journalOpenQty = Math.Max(0, journalEntry.Value.Entry.EntryFilledQuantityTotal - journalEntry.Value.Entry.ExitFilledQuantityTotal);
        if (journalOpenQty != brokerQty)
        {
            result.Reason = $"QTY_MISMATCH:broker={brokerQty},journal_open={journalOpenQty}";
            return result;
        }

        // Hard gate: Session/trading-date validity
        var entryTradingDate = journalEntry.Value.Entry.TradingDate ?? "";
        if (string.IsNullOrEmpty(entryTradingDate) || !string.Equals(entryTradingDate, tradingDate, StringComparison.OrdinalIgnoreCase))
        {
            result.Reason = $"TRADING_DATE_MISMATCH:candidate={entryTradingDate},current={tradingDate}";
            return result;
        }

        // Consistency layer: at least ONE of A, B, or C must pass
        var workingOrders = snapshot.WorkingOrders ?? new List<WorkingOrderSnapshot>();
        var hasContinuityA = false; // QTSW2 tag / intent linkage
        var hasContinuityB = false; // Broker working orders for candidate
        var hasContinuityC = false; // Strong journal continuity

        foreach (var o in workingOrders)
        {
            if (string.IsNullOrWhiteSpace(o.Instrument)) continue;
            if (!ExecutionInstrumentResolver.IsSameInstrument(o.Instrument, instrument) &&
                !ExecutionInstrumentResolver.IsSameInstrument(o.Instrument, execVariant))
                continue;

            var tag = o.Tag ?? "";
            if (!tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) continue;

            var decoded = RobotOrderIds.DecodeIntentId(tag);
            if (string.Equals(decoded, intentId, StringComparison.OrdinalIgnoreCase))
            {
                hasContinuityA = true;
                hasContinuityB = true;
                break;
            }
        }

        // C: Strong journal continuity - EntryFilled, remaining qty matches, !TradeCompleted
        if (journalEntry.Value.Entry.EntryFilled && journalOpenQty == brokerQty && !journalEntry.Value.Entry.TradeCompleted)
        {
            hasContinuityC = true;
        }

        if (!hasContinuityA && !hasContinuityB && !hasContinuityC)
        {
            result.Reason = "NO_CONTINUITY_EVIDENCE";
            result.CandidateIntentId = intentId;
            return result;
        }

        // All hard gates passed and at least one continuity evidence
        result.Decision = UnmatchedPositionPolicyDecision.OWNERSHIP_PROVEN;
        result.CandidateIntentId = intentId;
        result.Reason = "OWNERSHIP_PROVEN";
        return result;
    }

    private static string GetCanonicalForPositionInstrument(string instrument)
    {
        var u = (instrument ?? "").Trim().ToUpperInvariant();
        return u switch
        {
            "MES" => "ES",
            "MNQ" => "NQ",
            "M2K" => "RTY",
            "MYM" => "YM",
            "MCL" => "CL",
            "MGC" => "GC",
            "MNG" => "NG",
            _ => instrument ?? ""
        };
    }
}
