using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Auditable exclusion reason for <see cref="ExecutionJournal.BuildReleaseBlockingCandidateAudit"/> (logging only).</summary>
public static class ReleaseBlockingExclusionReasons
{
    public const string NO_OPEN_QTY = "NO_OPEN_QTY";
    public const string NO_TAG = "NO_TAG";
    public const string NOT_IN_REGISTRY = "NOT_IN_REGISTRY";
    public const string DIRECTION_MISMATCH = "DIRECTION_MISMATCH";
    public const string LIVE_TAGGED_UNFILLED_ENTRY = "LIVE_TAGGED_UNFILLED_ENTRY";
    public const string REGISTRY_TRUSTED_ACTIVE_EXPOSURE = "REGISTRY_TRUSTED_ACTIVE_EXPOSURE";
    /// <summary>Direction matches broker but row has no open qty and is not tied to live tag/registry working state.</summary>
    public const string NON_LIVE_STALE_ADOPTION = "NON_LIVE_STALE_ADOPTION";
    public const string OTHER = "OTHER";
}

/// <summary>Snapshot for RELEASE_BLOCKING_CANDIDATE_AUDIT events.</summary>
public sealed class ReleaseBlockingCandidateAuditData
{
    public int RawCandidateCount { get; init; }
    public int BlockingCandidateCount { get; init; }
    public int ExcludedCandidateCount { get; init; }
    /// <summary>64-bit blocking adoption intent set surrogate (diagnostics only; no raw ids).</summary>
    public long BlockingIntentSetHash { get; init; }
    public IReadOnlyList<string> BlockingIntentIdsSample { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedIntentIdsSample { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExclusionReasonsSample { get; init; } = Array.Empty<string>();
}

/// <summary>Result of <see cref="ExecutionJournal.UpsertRecoveredIntentForBrokerIntegrity"/>.</summary>
public readonly struct RecoveredIntentUpsertResult
{
    public int Writes { get; init; }
    public bool Conflict { get; init; }
    public string? ConflictReason { get; init; }
}

/// <summary>
/// Execution journal for idempotency and audit trail.
/// Persists per (trading_date, stream, intent_id) to prevent double-submission.
/// </summary>
/// <remarks>
/// RULE: No logging allowed before <see cref="RobotEngine.Start"/> completes (see <see cref="RobotLogger.RebindLogging"/>).
/// Constructor and cold-index rebuild must not call <c>_log.Write</c> — isolated playback defers logger rebind until Start().
/// </remarks>
public class ExecutionJournalEntry
{
    public string IntentId { get; set; } = "";

    public string? TradingDate { get; set; }

    public string Stream { get; set; } = "";

    public string Instrument { get; set; } = "";

    public bool EntrySubmitted { get; set; }

    /// <summary>Legacy mirror of <see cref="EntrySubmittedAtUtc"/> for older serializers.</summary>
    public string? EntrySubmittedAt { get; set; }

    /// <summary>Canonical entry submission event time (ISO-8601 UTC).</summary>
    public string? EntrySubmittedAtUtc { get; set; }

    /// <summary>When the system recorded/learned submission (ISO-8601 UTC).</summary>
    public string? EntrySubmittedObservedAtUtc { get; set; }

    public bool EntryFilled { get; set; }

    /// <summary>Legacy mirror of <see cref="EntryFilledAtUtc"/> for older serializers.</summary>
    public string? EntryFilledAt { get; set; }

    /// <summary>When the system last recorded entry-fill-related observations (ISO-8601 UTC).</summary>
    public string? EntryFilledObservedAtUtc { get; set; }

    /// <summary>True when submission was recorded after first-fill canonical time was already known (ordering handled explicitly).</summary>
    public bool IsReconstructedSubmission { get; set; }

    public string? BrokerOrderId { get; set; }

    public string? EntryOrderType { get; set; }

    public decimal? FillPrice { get; set; }

    public int? FillQuantity { get; set; }

    public bool Rejected { get; set; }

    public string? RejectedAt { get; set; }

    public string? RejectionReason { get; set; }

    public string? ProtectiveRejectedAt { get; set; }

    public string? ProtectiveRejectionReason { get; set; }

    public string? ProtectiveRejectionOrderType { get; set; }

    public decimal? ProtectiveRejectedPrice { get; set; }

    public int? ProtectiveRejectedQuantity { get; set; }

    public bool BEModified { get; set; }

    public string? BEModifiedAt { get; set; }

    public decimal? BEStopPrice { get; set; }
    
    // BE modification context
    public decimal? PreviousStopPrice { get; set; } // Stop price before BE modification
    public decimal? BETriggerPrice { get; set; } // BE trigger price (65% of target)
    
    // Minimal extension for recovery: deterministic rebuild fields
    public string? Direction { get; set; }
    
    public decimal? EntryPrice { get; set; }
    
    public decimal? StopPrice { get; set; }
    
    public decimal? TargetPrice { get; set; }
    
    public string? OcoGroup { get; set; }
    
    // Rejection context
    public string? RejectionOrderType { get; set; } // Order type that was rejected (ENTRY, STOP, TARGET)
    public decimal? RejectedPrice { get; set; } // Price that was rejected
    public int? RejectedQuantity { get; set; } // Quantity that was rejected
    
    // Slippage and cost tracking
    public decimal? ExpectedEntryPrice { get; set; } // Price we intended to fill at
    public decimal? ActualFillPrice { get; set; } // Price we actually filled at (same as FillPrice, kept for clarity)
    public decimal? SlippagePoints { get; set; } // ActualFillPrice - ExpectedEntryPrice (signed)
    public decimal? SlippageDollars { get; set; } // SlippagePoints * contract_multiplier * quantity
    public decimal? Commission { get; set; } // Broker commission (if available)
    public decimal? Fees { get; set; } // Exchange fees (if available)
    public decimal? TotalCost { get; set; } // SlippageDollars + Commission + Fees
    
    // Core identity (for journal-alone determinism)
    // NOTE: TradingDate and Stream may be nullable for backwards compatibility,
    // but persistence MUST reject empty/whitespace values
    // TradingDate and Stream already exist above - ensure they are populated
    
    public decimal? ContractMultiplier { get; set; } // Contract multiplier (required for P&L calculation)
    
    // Entry fill tracking (cumulative, weighted average)
    public int EntryFilledQuantityTotal { get; set; } // Cumulative total entry quantity
    public decimal? EntryAvgFillPrice { get; set; } // Weighted average entry fill price
    public decimal? EntryFillNotional { get; set; } // Sum(price * qty) for weighted avg calculation
    /// <summary>Canonical first entry fill event time (ISO-8601 UTC).</summary>
    public string? EntryFilledAtUtc { get; set; }
    
    // Exit fill tracking (cumulative, weighted average)
    public int ExitFilledQuantityTotal { get; set; } // Cumulative total exit quantity
    public decimal? ExitAvgFillPrice { get; set; } // Weighted average exit fill price
    public decimal? ExitFillNotional { get; set; } // Sum(price * qty) for weighted avg calculation
    public string? ExitOrderType { get; set; } // "STOP", "TARGET", "EMERGENCY", "MANUAL"
    /// <summary>Canonical first exit fill event time (ISO-8601 UTC).</summary>
    public string? ExitFilledAtUtc { get; set; }

    /// <summary>When the system last recorded exit-fill-related observations (ISO-8601 UTC).</summary>
    public string? ExitFilledObservedAtUtc { get; set; }

    // Journal integrity: synthetic recovered intent (broker truth when journal was empty/deficient)
    /// <summary>When set to <see cref="ExecutionJournal.IntentTypeRecovered"/>, row is synthetic integrity recovery, not a strategy intent.</summary>
    public string? IntentType { get; set; }

    /// <summary>e.g. <see cref="ExecutionJournal.RecoverySourceBrokerSnapshot"/>.</summary>
    public string? RecoverySource { get; set; }

    public string? RecoveryTimestampUtc { get; set; }
    public int? RecoveredQuantity { get; set; }
    public decimal? RecoveredPrice { get; set; }
    public bool IsRecovered { get; set; }

    /// <summary>Null for integrity-recovered rows (no upstream intent).</summary>
    public string? OriginalIntentId { get; set; }
    
    // Trade completion
    public bool TradeCompleted { get; set; } // True when exit qty == entry qty
    public decimal? RealizedPnLGross { get; set; } // Gross realized P&L in dollars
    public decimal? RealizedPnLNet { get; set; } // Net realized P&L (gross - commission - fees - slippage)
    public decimal? RealizedPnLPoints { get; set; } // Gross points before multiplier
    public string? CompletionReason { get; set; } // Exit order type that completed the trade
    public string? CompletedAtUtc { get; set; } // Trade completion timestamp (ISO-8601 UTC)
}

/// <summary>
/// Completion reason constants for execution journal (copy-safe, no enum).
/// </summary>
public static class CompletionReasons
{
    /// <summary>
    /// Trade was closed externally; broker position flat; journal reconciled.
    /// </summary>
    public const string RECONCILIATION_BROKER_FLAT = "RECONCILIATION_BROKER_FLAT";

    /// <summary>
    /// A row previously closed as broker-flat was restored because restart recovery found live QTSW2 working orders
    /// for the same intent. The stream remains carried exposure until broker, journal, ownership, and orders prove flat.
    /// </summary>
    public const string CARRYOVER_REOPENED_FROM_FALSE_BROKER_FLAT = "CARRYOVER_REOPENED_FROM_FALSE_BROKER_FLAT";

    /// <summary>
    /// Operator confirmed account correct; orphan journals force-closed via manual trigger.
    /// </summary>
    public const string RECONCILIATION_MANUAL_OVERRIDE = "RECONCILIATION_MANUAL_OVERRIDE";

    /// <summary>
    /// Journal row had pending adoption semantics but broker/registry reality showed no exposure or tagged working refs;
    /// closed so release gate and recovery index can converge (no fill event changed).
    /// </summary>
    public const string RECONCILIATION_STALE_JOURNAL_RELEASE = "RECONCILIATION_STALE_JOURNAL_RELEASE";

    /// <summary>
    /// Entry order was canceled by the broker before any fill occurred; row is terminal and must not stay adoptable.
    /// </summary>
    public const string ENTRY_CANCELLED_UNFILLED = "ENTRY_CANCELLED_UNFILLED";

    /// <summary>
    /// Open journal quantity exceeded broker position; excess exit quantity applied so release readiness reflects broker truth.
    /// Does not remove tagged/registry-attributed rows first; trim order is unprotected before protected.
    /// </summary>
    public const string RECONCILIATION_POSITION_ALIGNMENT = "RECONCILIATION_POSITION_ALIGNMENT";

    /// <summary>Virtual alignment trimmed exit qty but row cannot be marked complete (broker exposure, tags, or clock).</summary>
    public const string RECONCILIATION_ALIGNMENT_PENDING = "RECONCILIATION_ALIGNMENT_PENDING";

    /// <summary>
    /// Integrity-layer recovered row closed because non-recovered journal open qty equals broker abs exposure
    /// (real strategy rows fully explain the position; recovered remainder is redundant).
    /// </summary>
    public const string RECONCILIATION_RECOVERED_SUPERSEDED_BY_REAL = "RECONCILIATION_RECOVERED_SUPERSEDED_BY_REAL";

    /// <summary>Journal row requires recovery path; do not treat as flat-completed.</summary>
    public const string RECOVERY_REQUIRED = "RECOVERY_REQUIRED";

    /// <summary>
    /// Untracked / untagged fill recovery marker closed after broker flat was verified (flatten verify pass or reconciliation snapshot).
    /// </summary>
    public const string UNTRACKED_FILL_RECOVERY_FLAT = "UNTRACKED_FILL_RECOVERY_FLAT";
}
