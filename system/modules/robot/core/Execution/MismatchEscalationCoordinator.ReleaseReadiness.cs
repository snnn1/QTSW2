using System;
using System.Globalization;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    private int EffectiveQuietWindowMs(MismatchInstrumentState state) =>
        state.StableWindowMsApplied > 0 ? state.StableWindowMsApplied : _stableWindowMs;

    /// <summary>Single-line key: unchanged across ticks while release invariants hold -> quiet window can complete.</summary>
    private static string BuildReleaseQuietFingerprint(
        MismatchObservation obs,
        StateConsistencyReleaseReadinessResult r,
        int pendingIeA,
        int preSigHash,
        int postSigHash,
        ulong externalFp,
        ulong materialReadinessFp)
    {
        var coherentFlatRelease =
            r.ReleaseReady &&
            pendingIeA == 0 &&
            obs.BrokerWorkingOrderCount == 0 &&
            obs.LocalWorkingOrderCount == 0 &&
            r.DiagnosticBrokerPositionQty == 0 &&
            (r.ResidualCleanupOnly || r.DiagnosticJournalOpenQty == 0) &&
            r.DiagnosticIeaOwnedPlusAdoptedWorking <= 0;

        var quietPreSigHash = coherentFlatRelease ? 0 : preSigHash;
        var quietPostSigHash = coherentFlatRelease ? 0 : postSigHash;
        var summaryH = r.Summary != null ? StringComparer.Ordinal.GetHashCode(r.Summary) : 0;
        return string.Format(CultureInfo.InvariantCulture,
            "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}",
            pendingIeA,
            obs.BrokerWorkingOrderCount,
            obs.LocalWorkingOrderCount,
            r.DiagnosticBrokerPositionQty,
            r.DiagnosticJournalOpenQty,
            quietPreSigHash,
            quietPostSigHash,
            externalFp,
            materialReadinessFp,
            summaryH);
    }

    /// <summary>
    /// After <see cref="MismatchEscalationPolicy.GATE_MAX_DWELL_ALERT_MS"/>, allow forced release when readiness is true,
    /// execution queue is drained, nets agree, and we are only blocked by working-count noise, fingerprint jitter, or the quiet timer.
    /// </summary>
    private static bool ShouldForceMaxDwellSoftDegradeRelease(
        long gateDwellMs,
        StateConsistencyReleaseReadinessResult r,
        int pendingIeA,
        MismatchObservation obs,
        bool fpChanged,
        bool releaseConditionsMet,
        long stableMs,
        int quietWindowMs)
    {
        if (gateDwellMs < MismatchEscalationPolicy.GATE_MAX_DWELL_ALERT_MS) return false;
        if (!r.ReleaseReady || pendingIeA != 0) return false;
        if (r.UnexplainedBrokerPositionQty != 0 || r.UnexplainedBrokerWorkingCount != 0) return false;
        if (obs.NetBrokerQty != obs.NetJournalQty) return false;

        var stuckOnSoftBlockerOnly = fpChanged || !releaseConditionsMet ||
                                     (releaseConditionsMet && !fpChanged && stableMs < quietWindowMs);
        return stuckOnSoftBlockerOnly;
    }

    private static bool ShouldAllowImmediateBrokerFlatRelease(
        StateConsistencyReleaseReadinessResult readiness,
        int pendingIeA,
        MismatchObservation obs,
        bool fpChanged,
        bool releaseConditionsMet)
    {
        if (!releaseConditionsMet || pendingIeA != 0)
            return false;
        if (!readiness.SnapshotSufficient || !readiness.ReleaseReady || readiness.BlockerInvariantViolation)
            return false;
        if (readiness.DiagnosticBrokerPositionQty != 0 ||
            readiness.DiagnosticIeaOwnedPlusAdoptedWorking > 0)
            return false;
        if (!readiness.ResidualCleanupOnly && readiness.DiagnosticJournalOpenQty != 0)
            return false;
        if (obs.BrokerWorkingOrderCount != 0 || obs.LocalWorkingOrderCount != 0)
            return false;

        return true;
    }

    private static bool HasResidualReleaseBlocker(StateConsistencyReleaseReadinessResult r) =>
        r.PendingAdoptionExists || !r.BrokerPositionExplainable || !r.BrokerWorkingExplainable || !r.LocalStateCoherent;

    private static bool IsStallLikeSkipReason(string? skipReason) =>
        skipReason is "post_alignment_stall" or "throttle_cooldown" or "reconciliation_hysteresis"
            or "reentry_loop_blocked" or "execution_cycle_cap_reached" or "hard_stop_active"
            or "absolute_gate_lifetime_cap" or "skipped";

    private static bool IsSoftTransitionReleaseReadiness(StateConsistencyReleaseReadinessResult readiness)
    {
        if (readiness.ReleaseReady || !readiness.SnapshotSufficient || !readiness.IsExplainable)
            return false;
        if (readiness.BlockerInvariantViolation)
            return false;
        if (readiness.UnexplainedBrokerWorkingCount != 0 || !readiness.BrokerWorkingExplainable ||
            !readiness.BrokerPositionExplainable || !readiness.LocalStateCoherent)
            return false;

        var resolved = readiness.ResolvedBlockers;
        if (resolved == null || resolved.Count == 0)
            return false;

        foreach (var x in resolved)
        {
            if (x.Decision == ReconciliationDecision.IGNORE)
                continue;
            if (x.Decision == ReconciliationDecision.ESCALATE)
                return false;
            if (x.Decision is not (ReconciliationDecision.ADOPT or ReconciliationDecision.RETRY or ReconciliationDecision.ALTERNATE_LANE))
                return false;
            if (!IsSoftTransitionBlocker(x.Blocker.ReasonCode))
                return false;
        }

        return true;
    }

    private static bool IsSoftTransitionBlocker(ReconciliationBlockerReasonCode reasonCode) =>
        reasonCode is ReconciliationBlockerReasonCode.BrokerVisibleAdoptableExposure
            or ReconciliationBlockerReasonCode.TagMismatchExposure
            or ReconciliationBlockerReasonCode.JournalOnlyBrokerFlat
            or ReconciliationBlockerReasonCode.AlreadyOwnedElsewhere
            or ReconciliationBlockerReasonCode.StaleSnapshot;

    private static bool IsFillBoundaryPositionDelta(StateConsistencyReleaseReadinessResult readiness,
        MismatchObservation obs, int pendingExecutionWorkload)
    {
        if (readiness.ReleaseReady || !readiness.SnapshotSufficient || readiness.BlockerInvariantViolation)
            return false;

        var hasPositionDelta = readiness.Contradictions?.Any(c =>
            c.StartsWith("position_qty_delta_", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("info_pending_alignment_position_qty_delta_", StringComparison.OrdinalIgnoreCase)) == true;
        if (!hasPositionDelta && readiness.Summary != null)
            hasPositionDelta = readiness.Summary.IndexOf("position_qty_delta_", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!hasPositionDelta)
            return false;

        return pendingExecutionWorkload > 0 ||
               readiness.DiagnosticBrokerWorkingCount > 0 ||
               readiness.DiagnosticIeaOwnedPlusAdoptedWorking > 0 ||
               readiness.DiagnosticAdoptDecisionCount > 0 ||
               readiness.PendingAdoptionExists ||
               obs.BrokerWorkingOrderCount > 0 ||
               obs.LocalWorkingOrderCount > 0;
    }

    private static ulong BuildMaterialReadinessFingerprint(StateConsistencyReleaseReadinessResult r, MismatchType mt)
    {
        var c = r.Contradictions?.Count ?? 0;
        unchecked
        {
            ulong h = 1469598103934665603UL;
            void Mix(ulong x)
            {
                h ^= x;
                h *= 1099511628211UL;
            }

            Mix(r.ReleaseReady ? 1UL : 0UL);
            Mix((ulong)(uint)r.UnexplainedBrokerWorkingCount);
            Mix((ulong)(uint)r.UnexplainedBrokerPositionQty);
            Mix((ulong)(uint)c);
            Mix((ulong)(uint)mt);
            Mix(r.PendingAdoptionExists ? 1UL : 0UL);
            return h;
        }
    }

    private static ulong BuildStallQuietFingerprint(ulong externalFp, ulong materialFp, string? skipReason, int progressSigHash32)
    {
        unchecked
        {
            ulong h = 1469598103934665603UL;
            void Mix(ulong x)
            {
                h ^= x;
                h *= 1099511628211UL;
            }

            Mix(externalFp);
            Mix(materialFp);
            Mix((ulong)(uint)progressSigHash32);
            if (!string.IsNullOrEmpty(skipReason))
                Mix((ulong)StringComparer.Ordinal.GetHashCode(skipReason));
            return h;
        }
    }

    private static string BuildFailClosedStrictStabilityKey(StateConsistencyReleaseReadinessResult r) =>
        string.Join("|",
            r.DiagnosticBrokerPositionQty,
            r.DiagnosticJournalOpenQty,
            r.DiagnosticBrokerWorkingCount,
            r.DiagnosticIeaOwnedPlusAdoptedWorking);
}
