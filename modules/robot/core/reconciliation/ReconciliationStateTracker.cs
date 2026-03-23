using System;
using System.Collections.Generic;
using System.Threading;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Recovery coordination state for a single (account, instrument) mismatch episode.</summary>
public enum ReconciliationTrackerRecoveryState
{
    NORMAL,
    ACTIVE,
    ESCALATED
}

/// <summary>Outcome of runner-level gating before emitting critical mismatch logs or invoking the engine callback.</summary>
public enum ReconciliationMismatchGateOutcome
{
    /// <summary>Owner: emit full critical chain and invoke mismatch callback (engine handles recovery).</summary>
    EmitFullAndInvokeCallback,
    /// <summary>Owner: same qty still open within debounce — informational only, no callback.</summary>
    EmitStillOpenInfoOnly,
    /// <summary>Non-owner chart/instance — skip critical logs and callback.</summary>
    SecondaryInstanceSkip
}

/// <summary>Result of <see cref="ReconciliationStateTracker.EvaluateRunnerMismatch"/>.</summary>
public readonly struct ReconciliationMismatchGateResult
{
    public ReconciliationMismatchGateResult(ReconciliationMismatchGateOutcome outcome, string? ownerInstanceId)
    {
        Outcome = outcome;
        OwnerInstanceId = ownerInstanceId;
    }

    public ReconciliationMismatchGateOutcome Outcome { get; }
    public string? OwnerInstanceId { get; }
}

/// <summary>Process-wide metrics for reconciliation control layer (best-effort; monotonic counters).</summary>
public sealed class ReconciliationControlMetrics
{
    private long _mismatchTotal;
    private long _debouncedTotal;
    private long _secondarySkippedTotal;
    private long _resolvedTotal;

    public long ReconciliationMismatchTotal => Interlocked.Read(ref _mismatchTotal);
    public long ReconciliationDebouncedTotal => Interlocked.Read(ref _debouncedTotal);
    public long ReconciliationSecondarySkippedTotal => Interlocked.Read(ref _secondarySkippedTotal);
    public long ReconciliationResolvedTotal => Interlocked.Read(ref _resolvedTotal);

    internal void IncrementMismatchTotal() => Interlocked.Increment(ref _mismatchTotal);
    internal void IncrementDebouncedTotal() => Interlocked.Increment(ref _debouncedTotal);
    internal void IncrementSecondarySkippedTotal() => Interlocked.Increment(ref _secondarySkippedTotal);
    internal void IncrementResolvedTotal() => Interlocked.Increment(ref _resolvedTotal);
}

/// <summary>
/// Per-(account, instrument) debounce and single-writer coordination for reconciliation qty mismatches.
/// Shared across all <see cref="RobotEngine"/> instances in the AppDomain (multi-chart same account).
/// </summary>
public sealed class ReconciliationStateTracker
{
    public static ReconciliationStateTracker Shared { get; } = new();

    private readonly object _lock = new();
    private readonly Dictionary<(string Account, string Instrument), Entry> _entries = new();

    private readonly ReconciliationControlMetrics _metrics = new();

    public ReconciliationControlMetrics Metrics => _metrics;

    /// <summary>Default debounce window for unchanged qty while a mismatch episode is active.</summary>
    public static readonly TimeSpan DefaultDebounceWindow = TimeSpan.FromSeconds(45);

    private sealed class Entry
    {
        public DateTimeOffset LastDetectedUtc;
        /// <summary>Updated when engine completes a mismatch handling pass (arms qty debounce).</summary>
        public DateTimeOffset LastHandledUtc;
        /// <summary>Last time runner emitted the full critical mismatch chain (coalesce before dispatch ack).</summary>
        public DateTimeOffset LastRunnerFullSurfaceUtc = DateTimeOffset.MinValue;
        public bool IsActive;
        public ReconciliationTrackerRecoveryState RecoveryState = ReconciliationTrackerRecoveryState.NORMAL;
        public int LastAccountQty = int.MinValue;
        public int LastJournalQty = int.MinValue;
        public string? OwnerInstanceId;
        /// <summary>Per-episode: only one RECOVERY_ESCALATED for RECONCILIATION_QTY_MISMATCH duplicates.</summary>
        public bool ReconciliationEscalationLogged;
    }

    /// <summary>Suppress duplicate full critical emissions before engine has called <see cref="NotifyMismatchHandlingDispatched"/>.</summary>
    public static readonly TimeSpan PreDispatchResurfaceCoalesce = TimeSpan.FromSeconds(15);

    private static string NormAccount(string? account) =>
        string.IsNullOrWhiteSpace(account) ? "UNKNOWN" : account.Trim();

    private static string NormInstrument(string? instrument) =>
        string.IsNullOrWhiteSpace(instrument) ? "" : instrument.Trim();

    /// <summary>Canonical dictionary key (case-insensitive account/instrument).</summary>
    private static (string Account, string Instrument) CanonKey(string? account, string? instrument) =>
        (NormAccount(account).ToUpperInvariant(), NormInstrument(instrument).ToUpperInvariant());

    /// <summary>
    /// Runner calls this before logging RECONCILIATION_QTY_MISMATCH / context / drift events.
    /// </summary>
    public ReconciliationMismatchGateResult EvaluateRunnerMismatch(
        string? account,
        string instrument,
        int accountQty,
        int journalQty,
        DateTimeOffset utcNow,
        string currentInstanceId,
        TimeSpan debounceWindow)
    {
        var acct = NormAccount(account);
        var inst = NormInstrument(instrument);
        if (string.IsNullOrEmpty(inst))
            return new ReconciliationMismatchGateResult(ReconciliationMismatchGateOutcome.EmitFullAndInvokeCallback, null);

        var id = string.IsNullOrWhiteSpace(currentInstanceId) ? "UNKNOWN_INSTANCE" : currentInstanceId.Trim();

        lock (_lock)
        {
            var key = CanonKey(acct, inst);
            if (!_entries.TryGetValue(key, out var e))
            {
                e = new Entry();
                _entries[key] = e;
            }

            e.LastDetectedUtc = utcNow;
            var qtyChanged = e.LastAccountQty != accountQty || e.LastJournalQty != journalQty;
            if (qtyChanged)
            {
                e.LastAccountQty = accountQty;
                e.LastJournalQty = journalQty;
                e.ReconciliationEscalationLogged = false;
                if (e.RecoveryState == ReconciliationTrackerRecoveryState.ESCALATED)
                    e.RecoveryState = ReconciliationTrackerRecoveryState.ACTIVE;
            }

            if (string.IsNullOrEmpty(e.OwnerInstanceId))
                e.OwnerInstanceId = id;

            if (!string.Equals(e.OwnerInstanceId, id, StringComparison.Ordinal))
            {
                _metrics.IncrementSecondarySkippedTotal();
                return new ReconciliationMismatchGateResult(ReconciliationMismatchGateOutcome.SecondaryInstanceSkip, e.OwnerInstanceId);
            }

            // First episode or quantity changed: always full path.
            if (!e.IsActive || qtyChanged)
            {
                e.IsActive = true;
                e.LastRunnerFullSurfaceUtc = utcNow;
                _metrics.IncrementMismatchTotal();
                return new ReconciliationMismatchGateResult(ReconciliationMismatchGateOutcome.EmitFullAndInvokeCallback, e.OwnerInstanceId);
            }

            // Same qty, active episode: coalesce rapid re-surfaces before engine ack.
            if (e.LastHandledUtc == DateTimeOffset.MinValue &&
                (utcNow - e.LastRunnerFullSurfaceUtc) < PreDispatchResurfaceCoalesce)
            {
                _metrics.IncrementDebouncedTotal();
                return new ReconciliationMismatchGateResult(ReconciliationMismatchGateOutcome.EmitStillOpenInfoOnly, e.OwnerInstanceId);
            }

            // Active episode, same qty, within debounce since last engine dispatch.
            var sinceHandled = utcNow - e.LastHandledUtc;
            if (e.LastHandledUtc != DateTimeOffset.MinValue && sinceHandled < debounceWindow)
            {
                _metrics.IncrementDebouncedTotal();
                return new ReconciliationMismatchGateResult(ReconciliationMismatchGateOutcome.EmitStillOpenInfoOnly, e.OwnerInstanceId);
            }

            // Debounce expired — surface full critical again (ops re-alert).
            e.LastRunnerFullSurfaceUtc = utcNow;
            _metrics.IncrementMismatchTotal();
            return new ReconciliationMismatchGateResult(ReconciliationMismatchGateOutcome.EmitFullAndInvokeCallback, e.OwnerInstanceId);
        }
    }

    /// <summary>
    /// Engine calls after it has completed a full mismatch handling pass (any branch) that should arm debounce.
    /// </summary>
    public void NotifyMismatchHandlingDispatched(
        string? account,
        string instrument,
        int accountQty,
        int journalQty,
        DateTimeOffset utcNow)
    {
        var acct = NormAccount(account);
        var inst = NormInstrument(instrument);
        if (string.IsNullOrEmpty(inst)) return;

        lock (_lock)
        {
            var key = CanonKey(acct, inst);
            if (!_entries.TryGetValue(key, out var e))
            {
                e = new Entry();
                _entries[key] = e;
            }

            e.LastHandledUtc = utcNow;
            e.LastDetectedUtc = utcNow;
            e.LastAccountQty = accountQty;
            e.LastJournalQty = journalQty;
            e.IsActive = true;
            if (e.RecoveryState == ReconciliationTrackerRecoveryState.NORMAL)
                e.RecoveryState = ReconciliationTrackerRecoveryState.ACTIVE;
        }
    }

    /// <summary>
    /// When reconciliation pass shows matching qtys, clear episode and count resolution.
    /// Returns true if an active episode was cleared.
    /// </summary>
    public bool TryMarkResolved(
        string? account,
        string instrument,
        int accountQty,
        int journalQty,
        DateTimeOffset utcNow,
        out string? previousOwnerInstanceId,
        out ReconciliationTrackerRecoveryState previousRecoveryState)
    {
        previousOwnerInstanceId = null;
        previousRecoveryState = ReconciliationTrackerRecoveryState.NORMAL;
        if (accountQty != journalQty) return false;

        var acct = NormAccount(account);
        var inst = NormInstrument(instrument);
        if (string.IsNullOrEmpty(inst)) return false;

        lock (_lock)
        {
            var key = CanonKey(acct, inst);
            if (!_entries.TryGetValue(key, out var e) || !e.IsActive)
                return false;

            previousOwnerInstanceId = e.OwnerInstanceId;
            previousRecoveryState = e.RecoveryState;
            e.IsActive = false;
            e.RecoveryState = ReconciliationTrackerRecoveryState.NORMAL;
            e.OwnerInstanceId = null;
            e.LastHandledUtc = DateTimeOffset.MinValue;
            e.LastRunnerFullSurfaceUtc = DateTimeOffset.MinValue;
            e.ReconciliationEscalationLogged = false;
            e.LastAccountQty = int.MinValue;
            e.LastJournalQty = int.MinValue;
            _metrics.IncrementResolvedTotal();
            return true;
        }
    }

    /// <summary>
    /// IEA duplicate RequestRecovery path: emit at most one RECOVERY_ESCALATED per episode for RECONCILIATION_QTY_MISMATCH.
    /// </summary>
    public bool ShouldEmitRecoveryEscalatedLog(string? account, string instrument, string reason)
    {
        if (!string.Equals(reason, "RECONCILIATION_QTY_MISMATCH", StringComparison.Ordinal))
            return true;

        var acct = NormAccount(account);
        var inst = NormInstrument(instrument);
        if (string.IsNullOrEmpty(inst)) return true;

        lock (_lock)
        {
            if (!_entries.TryGetValue(CanonKey(acct, inst), out var e) || !e.IsActive)
                return true;

            if (e.ReconciliationEscalationLogged)
                return false;

            e.ReconciliationEscalationLogged = true;
            e.RecoveryState = ReconciliationTrackerRecoveryState.ESCALATED;
            return true;
        }
    }

    /// <summary>For unit tests only — clear all state.</summary>
    internal void ResetForTests()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
