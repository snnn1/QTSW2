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

    /// <summary>Rule 1 — mapped fill: update expected immediately, enter PENDING_ALIGNMENT with bounded expiry.</summary>
    /// <remarks>
    /// <para><b>RecoveryRequired:</b> Durable Tier-1 recovery is not cleared or softened by new mapped fills — phase and
    /// <see cref="RecoveryRequiredReason"/> stay fixed until explicitly cleared elsewhere (same contract as
    /// <see cref="NotifyRecoveredReconnect"/> for RecoveryRequired).</para>
    /// <para><b>Repeated fills (PendingAlignment):</b> Each mapped fill sets a new candidate deadline
    /// <c>utcNow + PostFillAlignmentWindowMs</c>. The stored <see cref="QuantExpectedInstrumentState.PendingAlignmentExpiresUtc"/>
    /// is the <b>later</b> of that candidate and the previous expiry. This is intentional: active staggered fills keep a single
    /// rolling alignment horizon so transient lag does not race a fixed clock from the first fill only. There is no additional cap
    /// beyond wall-clock monotonicity and parity/escalation transitions.</para>
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
            LastMappedFillUtc = s.LastMappedFillUtc,
            PendingAlignmentExpiresUtc = s.PendingAlignmentExpiresUtc,
            LastProtectiveStopSubmitUtc = s.LastProtectiveStopSubmitUtc,
            LastProtectiveResizePendingUtc = s.LastProtectiveResizePendingUtc,
            PendingProtectiveResizeQty = s.PendingProtectiveResizeQty,
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

    /// <summary>
    /// True while a known protective cancel/recreate resize is legitimately in flight.
    /// This is a short audit suppressor only; expiration returns the audit to critical behavior.
    /// </summary>
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
