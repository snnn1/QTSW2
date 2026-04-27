using System;
using System.Collections.Concurrent;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Tier 1 broker-first control: expected state + phase per instrument. Thread-safe.
/// Journal/parity/reconciliation remain Tier 2/3 — they must not be the sole authority for hot-path allow/deny;
/// this store is the explicit home for mapped-fill and unmapped transitions per quant architecture.
/// </summary>
public static class QuantExecutionControlStore
{
    private sealed class Entry
    {
        public QuantExecutionInstrumentPhase Phase = QuantExecutionInstrumentPhase.Normal;
        public QuantExpectedInstrumentState Expected = new();
        public string? LockOrUnmappedReason;
        public string? RecoveryRequiredReason;
    }

    private static readonly ConcurrentDictionary<string, Entry> ByInstrument = new(StringComparer.OrdinalIgnoreCase);

    private static string Norm(string? instrument) =>
        string.IsNullOrWhiteSpace(instrument) ? "" : instrument.Trim();

    /// <summary>Harness / engine session boundary.</summary>
    public static void Clear()
    {
        ByInstrument.Clear();
    }

    /// <summary>
    /// Phase 4A / Tier 1: record successful protective stop submission (new or idempotent). Not implied by
    /// <see cref="NotifyMappedTrustedFill"/> — that runs earlier on the fill path before submit completes.
    /// </summary>
    public static void NotifyProtectiveStopSubmitted(string instrument, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst)) return;
        if (!ByInstrument.TryGetValue(inst, out var e)) return;
        if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution or QuantExecutionInstrumentPhase.ExecutionLocked)
            return;
        e.Expected.LastProtectiveStopSubmitUtc = utcNow;
    }

    /// <summary>
    /// Records that an existing protective pair is being resized on the strategy thread after a
    /// mapped fill increased exposure. This is intentionally bounded by the same post-fill window
    /// as normal protective convergence; it suppresses audit races, not real stale protection.
    /// </summary>
    public static void NotifyProtectiveResizePending(string instrument, int expectedProtectiveQty, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || expectedProtectiveQty <= 0) return;

        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        var expiry = utcNow.AddMilliseconds(windowMs);

        ByInstrument.AddOrUpdate(
            inst,
            _ => new Entry
            {
                Phase = QuantExecutionInstrumentPhase.PendingAlignment,
                Expected =
                {
                    LastProtectiveResizePendingUtc = utcNow,
                    PendingProtectiveResizeQty = Math.Abs(expectedProtectiveQty),
                    PendingAlignmentExpiresUtc = expiry
                },
                LockOrUnmappedReason = null
            },
            (_, e) =>
            {
                if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution or QuantExecutionInstrumentPhase.ExecutionLocked)
                    return e;
                if (e.Phase != QuantExecutionInstrumentPhase.RecoveryRequired)
                    e.Phase = QuantExecutionInstrumentPhase.PendingAlignment;
                e.Expected.LastProtectiveResizePendingUtc = utcNow;
                e.Expected.PendingProtectiveResizeQty = Math.Abs(expectedProtectiveQty);
                e.Expected.PendingAlignmentExpiresUtc =
                    !e.Expected.PendingAlignmentExpiresUtc.HasValue || expiry > e.Expected.PendingAlignmentExpiresUtc.Value
                        ? expiry
                        : e.Expected.PendingAlignmentExpiresUtc;
                return e;
            });
    }

    /// <summary>
    /// Records an intentional protective OCO cancel/replace, such as BE replacement when NT will not
    /// accept an in-place Change() on the OCO stop. This is an audit convergence window only.
    /// </summary>
    public static void NotifyProtectiveCancelReplacePending(string instrument, int expectedWorkingOrderCount, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || expectedWorkingOrderCount <= 0) return;

        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        var expiry = utcNow.AddMilliseconds(windowMs);
        var expectedCount = Math.Abs(expectedWorkingOrderCount);

        ByInstrument.AddOrUpdate(
            inst,
            _ => new Entry
            {
                Phase = QuantExecutionInstrumentPhase.PendingAlignment,
                Expected =
                {
                    ExpectedWorkingOrderCount = expectedCount,
                    LastProtectiveCancelReplacePendingUtc = utcNow,
                    PendingProtectiveCancelReplaceWorkingCount = expectedCount,
                    PendingAlignmentExpiresUtc = expiry
                },
                LockOrUnmappedReason = null
            },
            (_, e) =>
            {
                if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution or QuantExecutionInstrumentPhase.ExecutionLocked)
                    return e;
                if (e.Phase is not QuantExecutionInstrumentPhase.RecoveryRequired and not QuantExecutionInstrumentPhase.Recovered)
                    e.Phase = QuantExecutionInstrumentPhase.PendingAlignment;
                e.Expected.ExpectedWorkingOrderCount = Math.Max(e.Expected.ExpectedWorkingOrderCount, expectedCount);
                e.Expected.LastProtectiveCancelReplacePendingUtc = utcNow;
                e.Expected.PendingProtectiveCancelReplaceWorkingCount = expectedCount;
                e.Expected.PendingAlignmentExpiresUtc =
                    !e.Expected.PendingAlignmentExpiresUtc.HasValue || expiry > e.Expected.PendingAlignmentExpiresUtc.Value
                        ? expiry
                        : e.Expected.PendingAlignmentExpiresUtc;
                return e;
            });
    }

    /// <summary>
    /// Records a broker working-order submit handoff before registry/order-update convergence is guaranteed.
    /// This is not exposure; it is a short suppressor for submit/register races.
    /// </summary>
    public static void NotifyWorkingOrderSubmitTransition(string instrument, int expectedWorkingOrderCount, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || expectedWorkingOrderCount <= 0) return;

        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        var expiry = utcNow.AddMilliseconds(windowMs);
        var expectedCount = Math.Abs(expectedWorkingOrderCount);

        ByInstrument.AddOrUpdate(
            inst,
            _ => new Entry
            {
                Phase = QuantExecutionInstrumentPhase.PendingAlignment,
                Expected =
                {
                    ExpectedWorkingOrderCount = expectedCount,
                    LastWorkingOrderSubmitUtc = utcNow,
                    PendingWorkingOrderSubmitCount = expectedCount,
                    PendingAlignmentExpiresUtc = expiry
                },
                LockOrUnmappedReason = null
            },
            (_, e) =>
            {
                if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution or QuantExecutionInstrumentPhase.ExecutionLocked)
                    return e;
                if (e.Phase is not QuantExecutionInstrumentPhase.RecoveryRequired and not QuantExecutionInstrumentPhase.Recovered)
                    e.Phase = QuantExecutionInstrumentPhase.PendingAlignment;
                e.Expected.ExpectedWorkingOrderCount = Math.Max(e.Expected.ExpectedWorkingOrderCount, expectedCount);
                e.Expected.LastWorkingOrderSubmitUtc = utcNow;
                e.Expected.PendingWorkingOrderSubmitCount = expectedCount;
                e.Expected.PendingAlignmentExpiresUtc =
                    !e.Expected.PendingAlignmentExpiresUtc.HasValue || expiry > e.Expected.PendingAlignmentExpiresUtc.Value
                        ? expiry
                        : e.Expected.PendingAlignmentExpiresUtc;
                return e;
            });
    }

    /// <summary>Rule 1: mapped fill updates expected state immediately and enters bounded pending alignment.</summary>
    /// <remarks>
    /// <para><b>RecoveryRequired:</b> Durable Tier-1 recovery is not cleared or softened by new mapped fills; phase and
    /// <see cref="RecoveryRequiredReason"/> stay fixed until explicitly cleared elsewhere.</para>
    /// <para><b>Repeated fills:</b> Each mapped fill extends the stored alignment deadline to the later of the
    /// new candidate deadline and the previous expiry.</para>
    /// </remarks>
    public static void NotifyMappedTrustedFill(string instrument, int signedDelta, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || signedDelta == 0) return;

        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        var expiry = utcNow.AddMilliseconds(windowMs);

        ByInstrument.AddOrUpdate(
            inst,
            _ => new Entry
            {
                Phase = QuantExecutionInstrumentPhase.PendingAlignment,
                Expected =
                {
                    ExpectedSignedNetPosition = signedDelta,
                    LastMappedFillUtc = utcNow,
                    PendingAlignmentExpiresUtc = expiry
                },
                LockOrUnmappedReason = null
            },
            (_, e) =>
            {
                if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution or QuantExecutionInstrumentPhase.ExecutionLocked
                    or QuantExecutionInstrumentPhase.RecoveryRequired)
                    return e;
                e.Expected.ExpectedSignedNetPosition += signedDelta;
                e.Expected.LastMappedFillUtc = utcNow;
                e.Expected.PendingAlignmentExpiresUtc = expiry > (e.Expected.PendingAlignmentExpiresUtc ?? DateTimeOffset.MinValue)
                    ? expiry
                    : e.Expected.PendingAlignmentExpiresUtc;
                e.Phase = QuantExecutionInstrumentPhase.PendingAlignment;
                e.LockOrUnmappedReason = null;
                return e;
            });
    }

    /// <summary>
    /// Broker/order lifecycle observed a fill before the execution callback may have finished journaling it.
    /// This arms the same short alignment horizon as mapped fills, but without asserting signed quantity yet.
    /// </summary>
    public static void NotifyBrokerExecutionCallbackPending(
        string instrument,
        DateTimeOffset utcNow,
        string? orderRole = null,
        string? lifecycleState = null)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst)) return;

        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        var expiry = utcNow.AddMilliseconds(windowMs);
        var role = string.IsNullOrWhiteSpace(orderRole) ? null : orderRole.Trim();
        var state = string.IsNullOrWhiteSpace(lifecycleState) ? null : lifecycleState.Trim();

        ByInstrument.AddOrUpdate(
            inst,
            _ => new Entry
            {
                Phase = QuantExecutionInstrumentPhase.PendingAlignment,
                Expected =
                {
                    LastBrokerExecutionCallbackUtc = utcNow,
                    BrokerExecutionCallbackExpiresUtc = expiry,
                    LastBrokerExecutionCallbackRole = role,
                    LastBrokerExecutionCallbackState = state,
                    PendingAlignmentExpiresUtc = expiry
                },
                LockOrUnmappedReason = null
            },
            (_, e) =>
            {
                if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution
                    or QuantExecutionInstrumentPhase.ExecutionLocked
                    or QuantExecutionInstrumentPhase.RecoveryRequired)
                    return e;

                if (e.Phase != QuantExecutionInstrumentPhase.Recovered)
                    e.Phase = QuantExecutionInstrumentPhase.PendingAlignment;

                e.Expected.LastBrokerExecutionCallbackUtc = utcNow;
                e.Expected.BrokerExecutionCallbackExpiresUtc = expiry;
                e.Expected.LastBrokerExecutionCallbackRole = role;
                e.Expected.LastBrokerExecutionCallbackState = state;
                e.Expected.PendingAlignmentExpiresUtc =
                    !e.Expected.PendingAlignmentExpiresUtc.HasValue || expiry > e.Expected.PendingAlignmentExpiresUtc.Value
                        ? expiry
                        : e.Expected.PendingAlignmentExpiresUtc;
                e.LockOrUnmappedReason = null;
                return e;
            });
    }

    /// <summary>Rule 2 — unmapped fill: terminal bad path for this instrument until cleared.</summary>
    public static void NotifyUnmappedExecution(string instrument, DateTimeOffset utcNow, string? reason = null)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst)) return;

        ByInstrument[inst] = new Entry
        {
            Phase = QuantExecutionInstrumentPhase.UnmappedExecution,
            Expected = new QuantExpectedInstrumentState(),
            LockOrUnmappedReason = string.IsNullOrWhiteSpace(reason) ? "UNMAPPED_FILL" : reason.Trim(),
            RecoveryRequiredReason = null
        };
    }

    /// <summary>Explicit operator/supervisory lock or post-catastrophic flatten.</summary>
    public static void NotifyExecutionLocked(string instrument, string reason, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst)) return;

        ByInstrument[inst] = new Entry
        {
            Phase = QuantExecutionInstrumentPhase.ExecutionLocked,
            Expected = new QuantExpectedInstrumentState(),
            LockOrUnmappedReason = reason.Trim(),
            RecoveryRequiredReason = null
        };
    }

    /// <summary>Reconnect adoption: broker risk adopted without full history (Rule 5).</summary>
    public static void NotifyRecoveredReconnect(string instrument, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst)) return;

        ByInstrument.AddOrUpdate(
            inst,
            _ => new Entry
            {
                Phase = QuantExecutionInstrumentPhase.Recovered,
                Expected =
                {
                    RecoveryMode = true,
                    LastMappedFillUtc = utcNow,
                    RecoveryEnteredUtc = utcNow
                },
                LockOrUnmappedReason = null
            },
            (_, e) =>
            {
                if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution or QuantExecutionInstrumentPhase.ExecutionLocked)
                    return e;
                if (e.Phase == QuantExecutionInstrumentPhase.RecoveryRequired)
                    return e;
                var wasRecovered = e.Phase == QuantExecutionInstrumentPhase.Recovered;
                e.Phase = QuantExecutionInstrumentPhase.Recovered;
                e.Expected.RecoveryMode = true;
                e.Expected.LastMappedFillUtc = utcNow;
                if (!wasRecovered && e.Expected.RecoveryEnteredUtc == null)
                    e.Expected.RecoveryEnteredUtc = utcNow;
                return e;
            });
    }

    /// <summary>When broker and journal reach PARITY_OK — leave PENDING_ALIGNMENT toward Normal.</summary>
    public static void NotifyParityOkBrokerJournalAligned(string instrument, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst)) return;

        if (!ByInstrument.TryGetValue(inst, out var e)) return;
        if (e.Phase != QuantExecutionInstrumentPhase.PendingAlignment) return;

        e.Phase = QuantExecutionInstrumentPhase.Normal;
        e.Expected.PendingAlignmentExpiresUtc = null;
        e.Expected.LastBrokerExecutionCallbackUtc = null;
        e.Expected.BrokerExecutionCallbackExpiresUtc = null;
        e.Expected.LastBrokerExecutionCallbackRole = null;
        e.Expected.LastBrokerExecutionCallbackState = null;
        e.LockOrUnmappedReason = null;
    }

    public static QuantExecutionControlSnapshot GetSnapshot(string instrument)
    {
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || !ByInstrument.TryGetValue(inst, out var e))
        {
            return new QuantExecutionControlSnapshot
            {
                Phase = QuantExecutionInstrumentPhase.Normal,
                Expected = new QuantExpectedInstrumentState(),
                LockOrUnmappedReason = null,
                RecoveryRequiredReason = null
            };
        }

        return new QuantExecutionControlSnapshot
        {
            Phase = e.Phase,
            Expected = CloneExpected(e.Expected),
            LockOrUnmappedReason = e.LockOrUnmappedReason,
            RecoveryRequiredReason = e.RecoveryRequiredReason
        };
    }

    private static QuantExpectedInstrumentState CloneExpected(QuantExpectedInstrumentState s) =>
        new()
        {
            ExpectedSignedNetPosition = s.ExpectedSignedNetPosition,
            ExpectedWorkingOrderCount = s.ExpectedWorkingOrderCount,
            LastWorkingOrderSubmitUtc = s.LastWorkingOrderSubmitUtc,
            PendingWorkingOrderSubmitCount = s.PendingWorkingOrderSubmitCount,
            LastMappedFillUtc = s.LastMappedFillUtc,
            LastBrokerExecutionCallbackUtc = s.LastBrokerExecutionCallbackUtc,
            BrokerExecutionCallbackExpiresUtc = s.BrokerExecutionCallbackExpiresUtc,
            LastBrokerExecutionCallbackRole = s.LastBrokerExecutionCallbackRole,
            LastBrokerExecutionCallbackState = s.LastBrokerExecutionCallbackState,
            PendingAlignmentExpiresUtc = s.PendingAlignmentExpiresUtc,
            LastProtectiveStopSubmitUtc = s.LastProtectiveStopSubmitUtc,
            LastProtectiveResizePendingUtc = s.LastProtectiveResizePendingUtc,
            PendingProtectiveResizeQty = s.PendingProtectiveResizeQty,
            LastProtectiveCancelReplacePendingUtc = s.LastProtectiveCancelReplacePendingUtc,
            PendingProtectiveCancelReplaceWorkingCount = s.PendingProtectiveCancelReplaceWorkingCount,
            RecoveryMode = s.RecoveryMode,
            RecoveryEnteredUtc = s.RecoveryEnteredUtc
        };

    /// <summary>
    /// Phase 4B: shared bounded post-fill gate for BE, mismatch escalation deferral, registry diagnostics, and ownership —
    /// same wall clock as <see cref="NotifyMappedTrustedFill"/> / <see cref="QuantExpectedInstrumentState.PendingAlignmentExpiresUtc"/>.
    /// Does not require protective submit (unlike protective coverage); <see cref="QuantExecutionInstrumentPhase.UnmappedExecution"/>
    /// and <see cref="QuantExecutionInstrumentPhase.ExecutionLocked"/> never return true here.
    /// </summary>
    public static bool IsPostFillAlignmentWindowActive(string instrument, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return false;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || !ByInstrument.TryGetValue(inst, out var e)) return false;
        if (e.Phase != QuantExecutionInstrumentPhase.PendingAlignment) return false;
        if (!e.Expected.LastMappedFillUtc.HasValue) return false;
        var expiry = e.Expected.PendingAlignmentExpiresUtc;
        if (expiry.HasValue)
            return utcNow <= expiry.Value;
        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        return (utcNow - e.Expected.LastMappedFillUtc.Value).TotalMilliseconds <= windowMs;
    }

    /// <summary>True while a working-order submit/register/order-update transition is still allowed to converge.</summary>
    public static bool IsWorkingOrderSubmitWindowActive(string instrument, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return false;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || !ByInstrument.TryGetValue(inst, out var e)) return false;
        if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution or QuantExecutionInstrumentPhase.ExecutionLocked)
            return false;
        if (!e.Expected.LastWorkingOrderSubmitUtc.HasValue)
            return false;

        var expiry = e.Expected.PendingAlignmentExpiresUtc;
        if (expiry.HasValue)
            return utcNow <= expiry.Value;

        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        return (utcNow - e.Expected.LastWorkingOrderSubmitUtc.Value).TotalMilliseconds <= windowMs;
    }

    /// <summary>
    /// True while broker lifecycle has observed an owned fill but execution/journal/ownership callbacks may still be catching up.
    /// </summary>
    public static bool IsBrokerExecutionCallbackPendingActive(string instrument, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return false;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || !ByInstrument.TryGetValue(inst, out var e)) return false;
        if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution
            or QuantExecutionInstrumentPhase.ExecutionLocked
            or QuantExecutionInstrumentPhase.RecoveryRequired)
            return false;
        if (!e.Expected.LastBrokerExecutionCallbackUtc.HasValue)
            return false;

        var expiry = e.Expected.BrokerExecutionCallbackExpiresUtc ?? e.Expected.PendingAlignmentExpiresUtc;
        if (expiry.HasValue)
            return utcNow <= expiry.Value;

        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        return (utcNow - e.Expected.LastBrokerExecutionCallbackUtc.Value).TotalMilliseconds <= windowMs;
    }

    /// <summary>Any bounded broker-visible-before-bookkeeping window currently active for this instrument.</summary>
    public static bool IsAnyBrokerJournalAlignmentWindowActive(string instrument, DateTimeOffset utcNow) =>
        IsPostFillAlignmentWindowActive(instrument, utcNow) ||
        IsWorkingOrderSubmitWindowActive(instrument, utcNow) ||
        IsBrokerExecutionCallbackPendingActive(instrument, utcNow) ||
        IsProtectiveResizePendingActive(instrument, utcNow) ||
        IsProtectiveCancelReplacePendingActive(instrument, utcNow);

    /// <summary>
    /// True while a known protective cancel/recreate resize is legitimately in flight.
    /// This is a short audit suppressor only; expiration returns the audit to critical behavior.
    /// </summary>
    public static bool IsProtectiveResizePendingActive(string instrument, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return false;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || !ByInstrument.TryGetValue(inst, out var e)) return false;
        if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution or QuantExecutionInstrumentPhase.ExecutionLocked)
            return false;
        if (!e.Expected.LastProtectiveResizePendingUtc.HasValue)
            return false;

        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        return (utcNow - e.Expected.LastProtectiveResizePendingUtc.Value).TotalMilliseconds <= windowMs;
    }

    public static bool IsProtectiveResizePendingActive(string instrument, int expectedProtectiveQty, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return false;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || expectedProtectiveQty <= 0 || !ByInstrument.TryGetValue(inst, out var e)) return false;
        if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution or QuantExecutionInstrumentPhase.ExecutionLocked)
            return false;
        if (!e.Expected.LastProtectiveResizePendingUtc.HasValue)
            return false;
        if (e.Expected.PendingProtectiveResizeQty.HasValue &&
            e.Expected.PendingProtectiveResizeQty.Value != Math.Abs(expectedProtectiveQty))
            return false;

        var expiry = e.Expected.PendingAlignmentExpiresUtc;
        if (expiry.HasValue)
            return utcNow <= expiry.Value;

        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        return (utcNow - e.Expected.LastProtectiveResizePendingUtc.Value).TotalMilliseconds <= windowMs;
    }

    /// <summary>
    /// True while a known protective OCO cancel/replace is in flight. This is bounded and expires
    /// back to normal critical-audit behavior.
    /// </summary>
    public static bool IsProtectiveCancelReplacePendingActive(string instrument, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return false;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || !ByInstrument.TryGetValue(inst, out var e)) return false;
        if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution or QuantExecutionInstrumentPhase.ExecutionLocked)
            return false;
        if (!e.Expected.LastProtectiveCancelReplacePendingUtc.HasValue)
            return false;

        var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
        return (utcNow - e.Expected.LastProtectiveCancelReplacePendingUtc.Value).TotalMilliseconds <= windowMs;
    }

    /// <summary>
    /// Tier 1 bookkeeping deferral: allow structural submit to tolerate journal/parity lag when phase says so.
    /// </summary>
    public static bool OffersBookkeepingLagProtection(string instrument, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return false;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || !ByInstrument.TryGetValue(inst, out var e)) return false;

        return e.Phase switch
        {
            QuantExecutionInstrumentPhase.PendingAlignment =>
                e.Expected.PendingAlignmentExpiresUtc == null || utcNow <= e.Expected.PendingAlignmentExpiresUtc.Value,
            QuantExecutionInstrumentPhase.Recovered when e.Expected.RecoveryMode => true,
            _ => false
        };
    }

    /// <summary>
    /// Compare broker abs position to expected abs — for future timeout escalation (Rule 3). Returns null if no expected baseline.
    /// </summary>
    public static bool? BrokerMatchesExpectedGross(string instrument, int brokerAbsQty)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled) return null;
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst) || !ByInstrument.TryGetValue(inst, out var e)) return null;
        return e.Expected.ExpectedGrossPositionAbs == brokerAbsQty;
    }

    /// <summary>
    /// Single Tier-1 evaluation: should the runtime escalate (repair, incident, policy flatten) vs still tolerate lag?
    /// Read-only — does not mutate store. Unmapped and execution-lock phases return <see cref="QuantEscalationKind.NoAction"/> (handled elsewhere).
    /// </summary>
    /// <param name="instrument">Execution instrument key (same normalization as other store APIs).</param>
    /// <param name="utcNow">Wall clock for window comparisons.</param>
    /// <param name="brokerGrossPositionAbs">Non-negative absolute broker position size.</param>
    /// <param name="brokerSignedNetPosition">Optional signed net; used for impossible-state sign check when expected signed is known.</param>
    /// <param name="expectedGrossOverride">If set, compares to broker gross instead of store expected gross.</param>
    /// <param name="expectedSignedNetOverride">If set, compares to broker signed and store for coherence.</param>
    /// <param name="recoveryStabilizationWindowMsOverride">If set, overrides <see cref="FeatureFlags.QuantEscalationRecoveryStabilizationWindowMs"/> for recovered phase.</param>
    public static QuantEscalationResult EvaluateEscalation(
        string? instrument,
        DateTimeOffset utcNow,
        int brokerGrossPositionAbs,
        int? brokerSignedNetPosition = null,
        int? expectedGrossOverride = null,
        int? expectedSignedNetOverride = null,
        int? recoveryStabilizationWindowMsOverride = null)
    {
        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst))
            return new QuantEscalationResult { Kind = QuantEscalationKind.NoAction, Reason = "missing_instrument" };

        if (!FeatureFlags.QuantExecutionControlStoreEnabled)
            return new QuantEscalationResult { Kind = QuantEscalationKind.NoAction, Reason = "store_disabled" };

        brokerGrossPositionAbs = System.Math.Abs(brokerGrossPositionAbs);

        if (!ByInstrument.TryGetValue(inst, out var e))
            return new QuantEscalationResult { Kind = QuantEscalationKind.NoAction, Reason = "no_store_entry" };

        var phase = e.Phase;
        if (phase is QuantExecutionInstrumentPhase.UnmappedExecution or QuantExecutionInstrumentPhase.ExecutionLocked)
            return new QuantEscalationResult { Kind = QuantEscalationKind.NoAction, Reason = "terminal_phase_handled_elsewhere" };

        if (phase == QuantExecutionInstrumentPhase.RecoveryRequired)
            return new QuantEscalationResult { Kind = QuantEscalationKind.NoAction, Reason = "already_recovery_required" };

        var expSigned = expectedSignedNetOverride ?? e.Expected.ExpectedSignedNetPosition;
        var expGross = expectedGrossOverride ?? e.Expected.ExpectedGrossPositionAbs;

        if (brokerSignedNetPosition.HasValue && expSigned != 0)
        {
            var b = brokerSignedNetPosition.Value;
            if (b != 0 && System.Math.Sign(b) != System.Math.Sign(expSigned))
                return new QuantEscalationResult
                {
                    Kind = QuantEscalationKind.EscalationRequired,
                    Reason = "impossible_signed_net_direction"
                };
        }

        var grossCoherent = brokerGrossPositionAbs == expGross;

        switch (phase)
        {
            case QuantExecutionInstrumentPhase.Normal:
                return new QuantEscalationResult { Kind = QuantEscalationKind.NoAction, Reason = "normal" };

            case QuantExecutionInstrumentPhase.PendingAlignment:
            {
                var expiry = e.Expected.PendingAlignmentExpiresUtc;
                var withinWindow = expiry == null || utcNow <= expiry.Value;
                if (withinWindow)
                {
                    return new QuantEscalationResult
                    {
                        Kind = QuantEscalationKind.StillPendingAlignment,
                        Reason = grossCoherent ? "pending_alignment_within_window_coherent_gross" : "pending_alignment_within_window_mismatch"
                    };
                }

                if (!grossCoherent)
                    return new QuantEscalationResult
                    {
                        Kind = QuantEscalationKind.EscalationRequired,
                        Reason = "pending_alignment_expired_broker_gross_mismatch"
                    };

                return new QuantEscalationResult
                {
                    Kind = QuantEscalationKind.NoAction,
                    Reason = "pending_alignment_expired_gross_match_stale_phase"
                };
            }

            case QuantExecutionInstrumentPhase.Recovered:
            {
                if (!e.Expected.RecoveryMode)
                    return new QuantEscalationResult { Kind = QuantEscalationKind.NoAction, Reason = "recovered_flag_off" };

                var recoveryStart = e.Expected.RecoveryEnteredUtc ?? e.Expected.LastMappedFillUtc;
                var recoveryMs = recoveryStabilizationWindowMsOverride ??
                                 (FeatureFlags.QuantEscalationRecoveryStabilizationWindowMs > 0
                                     ? FeatureFlags.QuantEscalationRecoveryStabilizationWindowMs
                                     : 60000);
                var deadline = recoveryStart?.AddMilliseconds(recoveryMs);

                if (deadline == null || utcNow <= deadline.Value)
                {
                    return new QuantEscalationResult
                    {
                        Kind = QuantEscalationKind.RecoveredLagTolerated,
                        Reason = grossCoherent ? "recovery_within_window_coherent" : "recovery_within_window_mismatch"
                    };
                }

                if (!grossCoherent)
                    return new QuantEscalationResult
                    {
                        Kind = QuantEscalationKind.EscalationRequired,
                        Reason = "recovery_stabilization_window_expired_broker_gross_mismatch"
                    };

                return new QuantEscalationResult
                {
                    Kind = QuantEscalationKind.NoAction,
                    Reason = "recovery_stabilized_gross_match"
                };
            }

            default:
                return new QuantEscalationResult { Kind = QuantEscalationKind.NoAction, Reason = "unknown_phase" };
        }
    }

    /// <summary>
    /// Evaluates Tier-1 escalation; if <see cref="QuantEscalationKind.EscalationRequired"/>, transitions instrument to
    /// <see cref="QuantExecutionInstrumentPhase.RecoveryRequired"/> (durable control state). Idempotent while already RecoveryRequired.
    /// </summary>
    public static QuantEscalationResult EvaluateEscalationAndApplyIfRequired(
        string? instrument,
        DateTimeOffset utcNow,
        int brokerGrossPositionAbs,
        int? brokerSignedNetPosition = null,
        int? expectedGrossOverride = null,
        int? expectedSignedNetOverride = null,
        int? recoveryStabilizationWindowMsOverride = null)
    {
        var r = EvaluateEscalation(
            instrument,
            utcNow,
            brokerGrossPositionAbs,
            brokerSignedNetPosition,
            expectedGrossOverride,
            expectedSignedNetOverride,
            recoveryStabilizationWindowMsOverride);
        if (r.Kind != QuantEscalationKind.EscalationRequired || !FeatureFlags.QuantExecutionControlStoreEnabled)
            return r;

        var inst = Norm(instrument);
        if (string.IsNullOrEmpty(inst))
            return r;

        var reason = string.IsNullOrWhiteSpace(r.Reason) ? "escalation" : r.Reason.Trim();
        ByInstrument.AddOrUpdate(
            inst,
            _ => new Entry
            {
                Phase = QuantExecutionInstrumentPhase.RecoveryRequired,
                Expected = new QuantExpectedInstrumentState(),
                RecoveryRequiredReason = reason
            },
            (_, e) =>
            {
                if (e.Phase is QuantExecutionInstrumentPhase.UnmappedExecution or QuantExecutionInstrumentPhase.ExecutionLocked)
                    return e;
                e.Phase = QuantExecutionInstrumentPhase.RecoveryRequired;
                e.RecoveryRequiredReason = reason;
                return e;
            });

        return r;
    }
}
