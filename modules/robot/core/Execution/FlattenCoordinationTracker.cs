using System;
using System.Collections.Generic;
using System.Threading;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Per-episode lifecycle for cross-chart flatten coordination.</summary>
public enum FlattenCoordinationState
{
    IDLE,
    FLATTENING,
    VERIFYING,
    FAILED_PERSISTENT
}

/// <summary>Outcome of <see cref="FlattenCoordinationTracker.TryRequestFlattenEnqueue"/>.</summary>
public enum FlattenEnqueueGateOutcome
{
    /// <summary>Enqueue the flatten NT action.</summary>
    Proceed,
    /// <summary>Another instance owns this key; do not enqueue.</summary>
    SecondaryInstanceSkip,
    /// <summary>Episode failed persistently; owner must not restart flatten storm.</summary>
    FailedPersistentBlocked,
    /// <summary>Stale owner replaced; caller may proceed.</summary>
    TakeoverProceed
}

/// <summary>Outcome of post-flatten verify processing.</summary>
public enum FlattenVerifyProcessOutcome
{
    ResolvedFlat,
    DebounceExtendWindow,
    EnqueueRetryFlatten,
    FailedPersistent
}

/// <summary>Process-wide metrics for flatten coordination.</summary>
public sealed class FlattenCoordinationMetrics
{
    private long _ownerAssigned;
    private long _secondarySkipped;
    private long _takeover;
    private long _verifyDebounced;
    private long _verifyWorsened;
    private long _failedPersistent;
    private long _persistentStillOpen;

    public long FlattenOwnerAssignedTotal => Interlocked.Read(ref _ownerAssigned);
    public long FlattenSecondarySkippedTotal => Interlocked.Read(ref _secondarySkipped);
    public long FlattenOwnerTakeoverTotal => Interlocked.Read(ref _takeover);
    public long FlattenVerifyDebouncedTotal => Interlocked.Read(ref _verifyDebounced);
    public long FlattenVerifyWorsenedTotal => Interlocked.Read(ref _verifyWorsened);
    public long FlattenFailedPersistentTotal => Interlocked.Read(ref _failedPersistent);
    public long FlattenFailedPersistentStillOpenTotal => Interlocked.Read(ref _persistentStillOpen);

    internal void IncOwnerAssigned() => Interlocked.Increment(ref _ownerAssigned);
    internal void IncSecondarySkipped() => Interlocked.Increment(ref _secondarySkipped);
    internal void IncTakeover() => Interlocked.Increment(ref _takeover);
    internal void IncVerifyDebounced() => Interlocked.Increment(ref _verifyDebounced);
    internal void IncVerifyWorsened() => Interlocked.Increment(ref _verifyWorsened);
    internal void IncFailedPersistent() => Interlocked.Increment(ref _failedPersistent);
    internal void IncPersistentStillOpen() => Interlocked.Increment(ref _persistentStillOpen);
}

/// <summary>
/// Single active flatten owner per (account, canonical_broker_key) in-process; debounced verify retries; stale-owner takeover.
/// </summary>
public sealed class FlattenCoordinationTracker
{
    public static FlattenCoordinationTracker Shared { get; } = new();

    /// <summary>Owner considered stale if no heartbeat past this TTL (takeover allowed).</summary>
    public static readonly TimeSpan DefaultStaleOwnerTtl = TimeSpan.FromSeconds(45);

    /// <summary>Consecutive non-zero verify windows required before scheduling a retry (unless exposure worsens).</summary>
    public const int DefaultVerifyNonzeroThreshold = 2;

    /// <summary>Max verify-driven retry flatten enqueues per episode (aligned with adapter FLATTEN_VERIFY_MAX_RETRIES).</summary>
    public const int DefaultMaxVerifyRetries = 4;

    /// <summary>Min interval between FLATTEN_FAILED_PERSISTENT_STILL_OPEN for same key.</summary>
    public static readonly TimeSpan DefaultPersistentStillOpenLogCooldown = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private readonly Dictionary<(string Account, string Canonical), Episode> _episodes = new();
    private readonly FlattenCoordinationMetrics _metrics = new();

    private TimeSpan _staleOwnerTtl = DefaultStaleOwnerTtl;
    private int _verifyNonzeroThreshold = DefaultVerifyNonzeroThreshold;
    private int _maxVerifyRetries = DefaultMaxVerifyRetries;
    private TimeSpan _persistentStillOpenCooldown = DefaultPersistentStillOpenLogCooldown;

    public FlattenCoordinationMetrics Metrics => _metrics;

    /// <summary>Test / advanced configuration only.</summary>
    public void ConfigureForTests(TimeSpan? staleTtl = null, int? nonzeroThreshold = null, int? maxVerifyRetries = null, TimeSpan? persistentCooldown = null)
    {
        lock (_lock)
        {
            if (staleTtl.HasValue) _staleOwnerTtl = staleTtl.Value;
            if (nonzeroThreshold.HasValue) _verifyNonzeroThreshold = nonzeroThreshold.Value;
            if (maxVerifyRetries.HasValue) _maxVerifyRetries = maxVerifyRetries.Value;
            if (persistentCooldown.HasValue) _persistentStillOpenCooldown = persistentCooldown.Value;
        }
    }

    private sealed class Episode
    {
        public string OwnerInstanceId = "";
        public string EpisodeId = "";
        public FlattenCoordinationState State = FlattenCoordinationState.IDLE;
        public DateTimeOffset FirstStartedUtc;
        public DateTimeOffset LastUpdatedUtc;
        public int LastTotalAbsQty = -1;
        public int VerifyAttemptCount;
        public int ConsecutiveNonzeroVerifies;
        public string? LastActionReason;
        public string? HostChartInstrument;
        public DateTimeOffset? LastPersistentStillOpenLogUtc;
    }

    private static (string A, string C) Key(string? account, string? canonical) =>
        (NormAccount(account), NormCanonical(canonical));

    private static string NormAccount(string? a) =>
        string.IsNullOrWhiteSpace(a) ? "UNKNOWN" : a.Trim().ToUpperInvariant();

    private static string NormCanonical(string? c) =>
        string.IsNullOrWhiteSpace(c) ? "" : BrokerPositionResolver.NormalizeCanonicalKey(c).Trim().ToUpperInvariant();

    private bool IsStale(Episode e, DateTimeOffset utcNow) =>
        (utcNow - e.LastUpdatedUtc) > _staleOwnerTtl;

    /// <summary>
    /// Call before enqueuing a flatten command (non-verify-retry). Returns whether to enqueue and episode id for logging.
    /// </summary>
    public FlattenEnqueueGateOutcome TryRequestFlattenEnqueue(
        string? account,
        string logicalInstrument,
        string currentInstanceId,
        string? hostChartInstrument,
        string correlationId,
        DateTimeOffset utcNow,
        bool isVerifyRetry,
        out string episodeId,
        out string? previousOwnerInstanceId,
        out bool emitCoordinationOwnerAssigned,
        out bool emitFailedPersistentStillOpen,
        out long staleOwnerElapsedSecIfTakeover)
    {
        episodeId = "";
        previousOwnerInstanceId = null;
        emitCoordinationOwnerAssigned = false;
        emitFailedPersistentStillOpen = false;
        staleOwnerElapsedSecIfTakeover = -1;
        var canonical = BrokerPositionResolver.NormalizeCanonicalKey(logicalInstrument);
        if (string.IsNullOrEmpty(canonical))
            return FlattenEnqueueGateOutcome.Proceed;

        var key = Key(account, canonical);
        lock (_lock)
        {
            if (!_episodes.TryGetValue(key, out var e))
            {
                e = new Episode();
                _episodes[key] = e;
            }

            if (isVerifyRetry)
            {
                if (e.State == FlattenCoordinationState.FAILED_PERSISTENT)
                    return FlattenEnqueueGateOutcome.FailedPersistentBlocked;
                if (string.IsNullOrEmpty(e.OwnerInstanceId))
                    return FlattenEnqueueGateOutcome.SecondaryInstanceSkip;
                if (!string.Equals(e.OwnerInstanceId, currentInstanceId, StringComparison.Ordinal))
                    return FlattenEnqueueGateOutcome.SecondaryInstanceSkip;
                if (string.IsNullOrEmpty(e.EpisodeId))
                    e.EpisodeId = Guid.NewGuid().ToString("N");
                e.LastUpdatedUtc = utcNow;
                e.LastActionReason = correlationId;
                episodeId = e.EpisodeId;
                return FlattenEnqueueGateOutcome.Proceed;
            }

            if (e.State == FlattenCoordinationState.FAILED_PERSISTENT)
            {
                if (string.Equals(e.OwnerInstanceId, currentInstanceId, StringComparison.Ordinal))
                {
                    if (!e.LastPersistentStillOpenLogUtc.HasValue ||
                        (utcNow - e.LastPersistentStillOpenLogUtc.Value) >= _persistentStillOpenCooldown)
                    {
                        e.LastPersistentStillOpenLogUtc = utcNow;
                        _metrics.IncPersistentStillOpen();
                        emitFailedPersistentStillOpen = true;
                    }
                    return FlattenEnqueueGateOutcome.FailedPersistentBlocked;
                }
                if (!IsStale(e, utcNow))
                    return FlattenEnqueueGateOutcome.SecondaryInstanceSkip;
                staleOwnerElapsedSecIfTakeover = (long)(utcNow - e.LastUpdatedUtc).TotalSeconds;
                previousOwnerInstanceId = e.OwnerInstanceId;
                ResetEpisodeLocked(e, utcNow, currentInstanceId, hostChartInstrument, "STALE_TAKEOVER_AFTER_PERSISTENT");
                _metrics.IncTakeover();
                episodeId = e.EpisodeId;
                return FlattenEnqueueGateOutcome.TakeoverProceed;
            }

            if (!string.IsNullOrEmpty(e.OwnerInstanceId) &&
                !string.Equals(e.OwnerInstanceId, currentInstanceId, StringComparison.Ordinal))
            {
                if (IsStale(e, utcNow))
                {
                    staleOwnerElapsedSecIfTakeover = (long)(utcNow - e.LastUpdatedUtc).TotalSeconds;
                    previousOwnerInstanceId = e.OwnerInstanceId;
                    ResetEpisodeLocked(e, utcNow, currentInstanceId, hostChartInstrument, "STALE_OWNER_TAKEOVER");
                    _metrics.IncTakeover();
                    episodeId = e.EpisodeId;
                    _metrics.IncOwnerAssigned();
                    return FlattenEnqueueGateOutcome.TakeoverProceed;
                }
                _metrics.IncSecondarySkipped();
                return FlattenEnqueueGateOutcome.SecondaryInstanceSkip;
            }

            if (string.IsNullOrEmpty(e.OwnerInstanceId) || e.State == FlattenCoordinationState.IDLE)
            {
                e.OwnerInstanceId = currentInstanceId;
                e.EpisodeId = Guid.NewGuid().ToString("N");
                e.FirstStartedUtc = utcNow;
                e.LastUpdatedUtc = utcNow;
                e.State = FlattenCoordinationState.FLATTENING;
                e.VerifyAttemptCount = 0;
                e.ConsecutiveNonzeroVerifies = 0;
                e.LastTotalAbsQty = -1;
                e.HostChartInstrument = hostChartInstrument;
                e.LastActionReason = correlationId;
                episodeId = e.EpisodeId;
                _metrics.IncOwnerAssigned();
                emitCoordinationOwnerAssigned = true;
                return FlattenEnqueueGateOutcome.Proceed;
            }

            e.OwnerInstanceId = currentInstanceId;
            e.LastUpdatedUtc = utcNow;
            e.HostChartInstrument = hostChartInstrument ?? e.HostChartInstrument;
            e.LastActionReason = correlationId;
            e.State = FlattenCoordinationState.FLATTENING;
            episodeId = e.EpisodeId;
            return FlattenEnqueueGateOutcome.Proceed;
        }
    }

    private static void ResetEpisodeLocked(Episode e, DateTimeOffset utcNow, string newOwner, string? hostChart, string reason)
    {
        e.OwnerInstanceId = newOwner;
        e.EpisodeId = Guid.NewGuid().ToString("N");
        e.FirstStartedUtc = utcNow;
        e.LastUpdatedUtc = utcNow;
        e.State = FlattenCoordinationState.FLATTENING;
        e.VerifyAttemptCount = 0;
        e.ConsecutiveNonzeroVerifies = 0;
        e.LastTotalAbsQty = -1;
        e.LastPersistentStillOpenLogUtc = null;
        e.HostChartInstrument = hostChart;
        e.LastActionReason = reason;
    }

    /// <summary>After flatten orders submit successfully; moves FLATTENING → VERIFYING.</summary>
    public void NotifyFlattenSubmitted(string? account, string logicalInstrument, string currentInstanceId, DateTimeOffset utcNow)
    {
        var canonical = BrokerPositionResolver.NormalizeCanonicalKey(logicalInstrument);
        if (string.IsNullOrEmpty(canonical)) return;
        var key = Key(account, canonical);
        lock (_lock)
        {
            if (!_episodes.TryGetValue(key, out var e)) return;
            if (!string.Equals(e.OwnerInstanceId, currentInstanceId, StringComparison.Ordinal)) return;
            e.State = FlattenCoordinationState.VERIFYING;
            e.LastUpdatedUtc = utcNow;
        }
    }

    /// <summary>On flatten path failure before submit success — release episode for this owner.</summary>
    public void NotifyFlattenAborted(string? account, string logicalInstrument, string currentInstanceId, DateTimeOffset utcNow)
    {
        var canonical = BrokerPositionResolver.NormalizeCanonicalKey(logicalInstrument);
        if (string.IsNullOrEmpty(canonical)) return;
        var key = Key(account, canonical);
        lock (_lock)
        {
            if (!_episodes.TryGetValue(key, out var e)) return;
            if (!string.Equals(e.OwnerInstanceId, currentInstanceId, StringComparison.Ordinal)) return;
            if (e.State == FlattenCoordinationState.FAILED_PERSISTENT) return;
            e.State = FlattenCoordinationState.IDLE;
            e.OwnerInstanceId = "";
            e.EpisodeId = "";
            e.ConsecutiveNonzeroVerifies = 0;
            e.VerifyAttemptCount = 0;
            e.LastTotalAbsQty = -1;
        }
    }

    /// <summary>Broker flat confirmed — reset to IDLE.</summary>
    public void NotifyBrokerFlatConfirmed(string? account, string logicalInstrument, string currentInstanceId, DateTimeOffset utcNow)
    {
        var canonical = BrokerPositionResolver.NormalizeCanonicalKey(logicalInstrument);
        if (string.IsNullOrEmpty(canonical)) return;
        var key = Key(account, canonical);
        lock (_lock)
        {
            if (!_episodes.TryGetValue(key, out var e)) return;
            if (!string.Equals(e.OwnerInstanceId, currentInstanceId, StringComparison.Ordinal)) return;
            e.State = FlattenCoordinationState.IDLE;
            e.OwnerInstanceId = "";
            e.EpisodeId = "";
            e.ConsecutiveNonzeroVerifies = 0;
            e.VerifyAttemptCount = 0;
            e.LastTotalAbsQty = -1;
        }
    }

    /// <summary>Mark persistent failure; owner remains until stale takeover.</summary>
    public void MarkFailedPersistent(string? account, string logicalInstrument, string currentInstanceId, DateTimeOffset utcNow)
    {
        var canonical = BrokerPositionResolver.NormalizeCanonicalKey(logicalInstrument);
        if (string.IsNullOrEmpty(canonical)) return;
        var key = Key(account, canonical);
        lock (_lock)
        {
            if (!_episodes.TryGetValue(key, out var e)) return;
            if (!string.Equals(e.OwnerInstanceId, currentInstanceId, StringComparison.Ordinal)) return;
            e.State = FlattenCoordinationState.FAILED_PERSISTENT;
            e.LastUpdatedUtc = utcNow;
            _metrics.IncFailedPersistent();
        }
    }

    /// <summary>Verify tick: returns decision for adapter. Caller must own the episode.</summary>
    public FlattenVerifyProcessOutcome ProcessVerifyWindow(
        string? account,
        string logicalInstrument,
        string currentInstanceId,
        int reconciliationAbsRemaining,
        DateTimeOffset utcNow,
        out bool shouldIncrementVerifyAttemptForRetry,
        out string episodeId,
        out bool exposureWorsenedBypass)
    {
        shouldIncrementVerifyAttemptForRetry = false;
        episodeId = "";
        exposureWorsenedBypass = false;
        var canonical = BrokerPositionResolver.NormalizeCanonicalKey(logicalInstrument);
        if (string.IsNullOrEmpty(canonical))
        {
            if (reconciliationAbsRemaining == 0)
                return FlattenVerifyProcessOutcome.ResolvedFlat;
            exposureWorsenedBypass = true;
            return FlattenVerifyProcessOutcome.EnqueueRetryFlatten;
        }

        var key = Key(account, canonical);
        lock (_lock)
        {
            if (!_episodes.TryGetValue(key, out var e))
            {
                return reconciliationAbsRemaining == 0
                    ? FlattenVerifyProcessOutcome.ResolvedFlat
                    : FlattenVerifyProcessOutcome.EnqueueRetryFlatten;
            }

            if (!string.Equals(e.OwnerInstanceId, currentInstanceId, StringComparison.Ordinal))
                return FlattenVerifyProcessOutcome.DebounceExtendWindow;

            episodeId = e.EpisodeId;
            e.LastUpdatedUtc = utcNow;

            if (reconciliationAbsRemaining == 0)
            {
                e.ConsecutiveNonzeroVerifies = 0;
                e.LastTotalAbsQty = 0;
                e.State = FlattenCoordinationState.IDLE;
                e.OwnerInstanceId = "";
                e.EpisodeId = "";
                e.VerifyAttemptCount = 0;
                return FlattenVerifyProcessOutcome.ResolvedFlat;
            }

            if (e.LastTotalAbsQty >= 0 && reconciliationAbsRemaining > e.LastTotalAbsQty)
            {
                _metrics.IncVerifyWorsened();
                e.ConsecutiveNonzeroVerifies = 0;
                e.LastTotalAbsQty = reconciliationAbsRemaining;
                if (e.VerifyAttemptCount >= _maxVerifyRetries)
                    return FlattenVerifyProcessOutcome.FailedPersistent;
                e.VerifyAttemptCount++;
                shouldIncrementVerifyAttemptForRetry = true;
                exposureWorsenedBypass = true;
                return FlattenVerifyProcessOutcome.EnqueueRetryFlatten;
            }

            e.LastTotalAbsQty = reconciliationAbsRemaining;
            e.ConsecutiveNonzeroVerifies++;

            if (e.ConsecutiveNonzeroVerifies < _verifyNonzeroThreshold)
            {
                _metrics.IncVerifyDebounced();
                return FlattenVerifyProcessOutcome.DebounceExtendWindow;
            }

            e.ConsecutiveNonzeroVerifies = 0;
            if (e.VerifyAttemptCount >= _maxVerifyRetries)
                return FlattenVerifyProcessOutcome.FailedPersistent;
            e.VerifyAttemptCount++;
            shouldIncrementVerifyAttemptForRetry = true;
            return FlattenVerifyProcessOutcome.EnqueueRetryFlatten;
        }
    }

    /// <summary>Peek current owner for a key (for secondary-skip diagnostics).</summary>
    public bool TryPeekKey(string? account, string logicalInstrument, out string ownerInstanceId, out string episodeId, out FlattenCoordinationState state)
    {
        ownerInstanceId = "";
        episodeId = "";
        state = FlattenCoordinationState.IDLE;
        var canonical = BrokerPositionResolver.NormalizeCanonicalKey(logicalInstrument);
        if (string.IsNullOrEmpty(canonical)) return false;
        var key = Key(account, canonical);
        lock (_lock)
        {
            if (!_episodes.TryGetValue(key, out var e)) return false;
            ownerInstanceId = e.OwnerInstanceId;
            episodeId = e.EpisodeId;
            state = e.State;
            return true;
        }
    }

    /// <summary>Read episode fields for logging / pending registration (owner must match).</summary>
    public bool TryPeekEpisodeForOwner(string? account, string logicalInstrument, string currentInstanceId, out string episodeId, out int verifyAttemptCount, out int consecutiveNonzeroVerifies)
    {
        episodeId = "";
        verifyAttemptCount = 0;
        consecutiveNonzeroVerifies = 0;
        var canonical = BrokerPositionResolver.NormalizeCanonicalKey(logicalInstrument);
        if (string.IsNullOrEmpty(canonical)) return false;
        var key = Key(account, canonical);
        lock (_lock)
        {
            if (!_episodes.TryGetValue(key, out var e)) return false;
            if (!string.Equals(e.OwnerInstanceId, currentInstanceId, StringComparison.Ordinal)) return false;
            episodeId = e.EpisodeId;
            verifyAttemptCount = e.VerifyAttemptCount;
            consecutiveNonzeroVerifies = e.ConsecutiveNonzeroVerifies;
            return true;
        }
    }

    /// <summary>True if no episode, no owner recorded, or this instance is the recorded owner (execute may proceed).</summary>
    public bool IsActiveFlattenOwner(string? account, string logicalInstrument, string currentInstanceId)
    {
        var canonical = BrokerPositionResolver.NormalizeCanonicalKey(logicalInstrument);
        if (string.IsNullOrEmpty(canonical)) return true;
        var key = Key(account, canonical);
        lock (_lock)
        {
            if (!_episodes.TryGetValue(key, out var e)) return true;
            if (string.IsNullOrEmpty(e.OwnerInstanceId)) return true;
            return string.Equals(e.OwnerInstanceId, currentInstanceId, StringComparison.Ordinal);
        }
    }

    /// <summary>Test helper: clear all state.</summary>
    public void ClearAllForTests()
    {
        lock (_lock) { _episodes.Clear(); }
    }
}
