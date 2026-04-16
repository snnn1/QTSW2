using System;
using System.Collections.Concurrent;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Secondary execution overlay only: kill-switch latch, hard execution lock, unsafe-lock classification, and broker snapshot freshness.
/// Does not derive position authority, parity, reconciliation, or recovery — those are handled before this gate.
/// </summary>
public static class ExecutionSafetyGate
{
    public const string InstrumentStateUnsafeLocked = "UNSAFE_LOCKED";

    /// <summary>Reject broker snapshots older than this (ms) for overlay — fail-closed if exceeded.</summary>
    public static int MaxBrokerSnapshotAgeMilliseconds { get; set; } = 3000;

    /// <summary>Seconds: block duplicate flatten while broker exposure unchanged after a prior flatten submit.</summary>
    public static int DuplicateFlattenSuppressionSeconds { get; set; } = 45;

    private sealed class UnsafeLockEntry
    {
        public string Reason = "";
        public DateTimeOffset LockedUtc;
    }

    private static readonly ConcurrentDictionary<string, UnsafeLockEntry> UnsafeLockedByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class FlattenIdempotencyEntry
    {
        public DateTimeOffset Utc;
        public int BrokerAbs;
    }

    private static readonly ConcurrentDictionary<string, FlattenIdempotencyEntry> LastFlattenSubmitByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeKey(string instrument) =>
        string.IsNullOrWhiteSpace(instrument) ? "" : instrument.Trim();

    /// <summary>Kill-switch: first unmapped / critical unmapped execution path — clear via <see cref="TryManualClearUnsafeLockWhenOverlayAllows"/>.</summary>
    public static void ApplyUnmappedExecutionKillSwitch(string instrument, string reason, DateTimeOffset utcNow)
    {
        var key = NormalizeKey(instrument);
        if (string.IsNullOrEmpty(key)) return;
        UnsafeLockedByInstrument[key] = new UnsafeLockEntry
        {
            Reason = string.IsNullOrWhiteSpace(reason) ? "EXECUTION_FILL_UNMAPPED" : reason.Trim(),
            LockedUtc = utcNow
        };
    }

    public static bool IsInstrumentUnsafeLocked(string instrument)
    {
        var key = NormalizeKey(instrument);
        return !string.IsNullOrEmpty(key) && UnsafeLockedByInstrument.ContainsKey(key);
    }

    /// <summary>Dangerous: clears lock without safety checks — use only in tests.</summary>
    public static bool TryManualClearUnsafeLockUnsafeForTestsOnly(string instrument)
    {
        var key = NormalizeKey(instrument);
        if (string.IsNullOrEmpty(key)) return false;
        return UnsafeLockedByInstrument.TryRemove(key, out _);
    }

    /// <summary>
    /// Removes unsafe lock when the broker snapshot is fresh enough for overlay purposes.
    /// Callers (e.g. <c>NinjaTraderSimAdapter.TryManualClearExecutionSafetyUnsafeLock</c>) must first pass the same structural safety baseline as explicit unfreeze (REAL authority + structural layer).
    /// </summary>
    public static bool TryManualClearUnsafeLockWhenOverlayAllows(
        ExecutionSafetyEvaluationRequest req,
        out string denyReason)
    {
        denyReason = "";
        var key = NormalizeKey(req.Instrument);
        if (string.IsNullOrEmpty(key))
        {
            denyReason = "missing_instrument";
            return false;
        }

        if (!UnsafeLockedByInstrument.ContainsKey(key))
        {
            denyReason = "not_locked";
            return false;
        }

        if (!IsBrokerSnapshotAcceptableForOverlay(req, out denyReason))
            return false;

        UnsafeLockedByInstrument.TryRemove(key, out _);
        return true;
    }

    /// <summary>Harness / tests.</summary>
    public static void ClearAllUnsafeLocksForTests()
    {
        UnsafeLockedByInstrument.Clear();
        LastFlattenSubmitByInstrument.Clear();
    }

    /// <summary>Call after a flatten submit succeeds (broker order path) for idempotency tracking.</summary>
    public static void RecordFlattenSubmitted(string instrument, int brokerAbsAtSubmit, DateTimeOffset utcNow)
    {
        var key = NormalizeKey(instrument);
        if (string.IsNullOrEmpty(key)) return;
        LastFlattenSubmitByInstrument[key] = new FlattenIdempotencyEntry { Utc = utcNow, BrokerAbs = brokerAbsAtSubmit };
    }

    /// <summary>Block repeated flatten while broker abs qty unchanged within the suppression window.</summary>
    public static bool ShouldBlockRepeatedFlatten(string instrument, int brokerAbsNow, DateTimeOffset utcNow)
    {
        var key = NormalizeKey(instrument);
        if (string.IsNullOrEmpty(key)) return false;
        if (!LastFlattenSubmitByInstrument.TryGetValue(key, out var prev)) return false;
        if ((utcNow - prev.Utc).TotalSeconds > DuplicateFlattenSuppressionSeconds) return false;
        return prev.BrokerAbs == brokerAbsNow;
    }

    /// <summary>
    /// Final overlay evaluation: unsafe lock, hard lock, snapshot freshness. Ignores journal parity and position authority.
    /// </summary>
    public static bool TryEvaluateExecutionOverlay(ExecutionSafetyEvaluationRequest req, out ExecutionOverlayResult result)
    {
        result = new ExecutionOverlayResult { IsBlocked = false, BlockReason = ExecutionOverlayBlockReason.None };
        var inst = NormalizeKey(req.Instrument);
        if (string.IsNullOrEmpty(inst))
        {
            result = new ExecutionOverlayResult
            {
                IsBlocked = true,
                BlockReason = ExecutionOverlayBlockReason.STALE_SNAPSHOT,
                Detail = "missing_instrument"
            };
            return false;
        }

        if (UnsafeLockedByInstrument.TryGetValue(inst, out var locked))
        {
            var r = locked.Reason ?? "";
            var reason = ClassifyUnsafeLockReason(r);
            result = new ExecutionOverlayResult
            {
                IsBlocked = true,
                BlockReason = reason,
                Detail = r
            };
            return false;
        }

        if (HardFailClosedExecutionModel.IsHardExecutionLocked(inst))
        {
            result = new ExecutionOverlayResult
            {
                IsBlocked = true,
                BlockReason = ExecutionOverlayBlockReason.HARD_LOCK,
                Detail = "hard_flatten_model"
            };
            return false;
        }

        if (req.AccountSnapshot == null)
        {
            result = new ExecutionOverlayResult
            {
                IsBlocked = true,
                BlockReason = ExecutionOverlayBlockReason.STALE_SNAPSHOT,
                Detail = "account_snapshot_null"
            };
            return false;
        }

        if (req.AccountSnapshot.CapturedAtUtc == null)
        {
            result = new ExecutionOverlayResult
            {
                IsBlocked = true,
                BlockReason = ExecutionOverlayBlockReason.STALE_SNAPSHOT,
                Detail = "broker_snapshot_untimestamped"
            };
            return false;
        }

        var maxAge = req.MaxBrokerSnapshotAgeMs > 0 ? req.MaxBrokerSnapshotAgeMs : MaxBrokerSnapshotAgeMilliseconds;
        var ageMs = (req.UtcNow - req.AccountSnapshot.CapturedAtUtc.Value).TotalMilliseconds;
        if (ageMs < 0 || ageMs > maxAge)
        {
            result = new ExecutionOverlayResult
            {
                IsBlocked = true,
                BlockReason = ExecutionOverlayBlockReason.STALE_SNAPSHOT,
                Detail = $"age_ms={ageMs:F0},max_ms={maxAge}"
            };
            return false;
        }

        return true;
    }

    private static ExecutionOverlayBlockReason ClassifyUnsafeLockReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return ExecutionOverlayBlockReason.UNMAPPED_FILL;
        if (reason.IndexOf("MANUAL", StringComparison.OrdinalIgnoreCase) >= 0)
            return ExecutionOverlayBlockReason.MANUAL_LOCK;
        if (reason.IndexOf("UNMAPPED", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("EXECUTION_FILL", StringComparison.OrdinalIgnoreCase) >= 0)
            return ExecutionOverlayBlockReason.UNMAPPED_FILL;
        return ExecutionOverlayBlockReason.KILL_SWITCH;
    }

    private static bool IsBrokerSnapshotAcceptableForOverlay(ExecutionSafetyEvaluationRequest req, out string denyReason)
    {
        denyReason = "";
        if (req.AccountSnapshot == null)
        {
            denyReason = "insufficient_data";
            return false;
        }

        if (req.AccountSnapshot.CapturedAtUtc == null)
        {
            denyReason = "broker_snapshot_untimestamped";
            return false;
        }

        var maxAge = req.MaxBrokerSnapshotAgeMs > 0 ? req.MaxBrokerSnapshotAgeMs : MaxBrokerSnapshotAgeMilliseconds;
        var ageMs = (req.UtcNow - req.AccountSnapshot.CapturedAtUtc.Value).TotalMilliseconds;
        if (ageMs < 0 || ageMs > maxAge)
        {
            denyReason = "broker_snapshot_stale";
            return false;
        }

        return true;
    }
}

/// <summary>Overlay evaluation result — authority and parity are not represented here.</summary>
public readonly struct ExecutionOverlayResult
{
    public bool IsBlocked { get; init; }
    public ExecutionOverlayBlockReason BlockReason { get; init; }
    public string? Detail { get; init; }
}

public enum ExecutionOverlayBlockReason
{
    None = 0,
    KILL_SWITCH,
    UNMAPPED_FILL,
    HARD_LOCK,
    STALE_SNAPSHOT,
    MANUAL_LOCK
}

/// <summary>Inputs for structural evaluation and overlay; authority callback removed — adapter logs POSITION_AUTHORITY_EVALUATED.</summary>
public sealed class ExecutionSafetyEvaluationRequest
{
    public string Instrument { get; set; } = "";
    public string? CanonicalInstrument { get; set; }
    /// <summary>IEA execution instrument key (e.g. MES) when different from <see cref="Instrument"/>.</summary>
    public string? ExecutionInstrumentKey { get; set; }
    public AccountSnapshot? AccountSnapshot { get; set; }
    public DateTimeOffset? SnapshotTakenUtc { get; set; }
    public DateTimeOffset UtcNow { get; set; }
    public ExecutionJournal Journal { get; set; } = null!;
    public bool UseInstrumentExecutionAuthority { get; set; }
    /// <summary>From IEA <c>GetMismatchTrustedWorkingCount</c>; use -1 when IEA required but unavailable.</summary>
    public int IeaOwnedPlusAdoptedWorking { get; set; }
    public InstrumentIntentCoordinator? Coordinator { get; set; }
    /// <summary>True when <c>IsExecutionAllowed</c> is false (recovery / stand-down).</summary>
    public bool RecoveryExecutionDisallowed { get; set; }
    /// <summary>Optional explicit repair latch from engine (reconciliation, mismatch handling).</summary>
    public bool JournalIntegrityOrReconciliationRepairActive { get; set; }

    /// <summary>Override <see cref="ExecutionSafetyGate.MaxBrokerSnapshotAgeMilliseconds"/> when &gt; 0.</summary>
    public int MaxBrokerSnapshotAgeMs { get; set; }

    /// <summary>
    /// Phase 8: Optional ledger ownership snapshot. When present and
    /// <see cref="FeatureFlags.StructuralLayerUseLedgerOwnership"/> is on, <see cref="ExecutionStructuralLayer"/>
    /// derives position authority from <see cref="InstrumentOwnershipSnapshot.LedgerSignedNetQty"/> instead of
    /// journal-based <see cref="PositionAuthorityDerivation"/>.
    /// </summary>
    public InstrumentOwnershipSnapshot? LedgerOwnershipSnapshot { get; set; }
}

/// <summary>Payload for POSITION_AUTHORITY_EVALUATED (emitted by adapter after local derivation).</summary>
public readonly struct PositionAuthorityEvaluatedArgs
{
    public string Instrument { get; init; }
    public int BrokerQty { get; init; }
    public int RealOpenQty { get; init; }
    public int RecoveryOpenQty { get; init; }
    public int JournalOpenQty { get; init; }
    public string AuthorityState { get; init; }
}

/// <summary>Diagnostic payload for blocked execution events.</summary>
public sealed class ExecutionSafetySnapshot
{
    public string Instrument { get; set; } = "";
    public int BrokerQty { get; set; }
    public int JournalQty { get; set; }
    public int RealOpenQty { get; set; }
    public int RecoveredOpenQty { get; set; }
    /// <summary>Journal open total used for authority (real + recovery open).</summary>
    public int JournalOpenQty { get; set; }
    /// <summary>Last parity status from <see cref="JournalParityChecker"/> (e.g. PARITY_OK, POSITION_MISMATCH).</summary>
    public string ParityStatus { get; set; } = "";
    /// <summary>True when engine repair / stand-down latch would block structural pass.</summary>
    public bool StructuralRepairActive { get; set; }
    /// <summary>True when broker has open qty but coordinator shows no robot exposure (structural anomaly).</summary>
    public bool NoActiveExposuresWithBrokerPosition { get; set; }
    public string AuthorityState { get; set; } = "";
    public string Reason { get; set; } = "";
    public string InstrumentOperationalState { get; set; } = "OK";
    public string? Detail { get; set; }
}
