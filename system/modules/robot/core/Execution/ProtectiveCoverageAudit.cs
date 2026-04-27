// Gap 3: Protective Coverage Audit — pure audit logic only.
// Broker-truth based. Reads AccountSnapshot; produces ProtectiveAuditResult.
// Phase 4A: Bounded post-fill convergence — complements broker snapshot with Tier-1 mapped-fill window (see IsWithinBoundedPostFillConvergenceWindow).
// No remediation. No side effects.

using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Pure audit: for one instrument with non-flat broker position, verify protective coverage.
/// Uses broker position and broker working orders as primary truth; Tier-1 control store supplies a bounded pending-valid window after mapped fills.
/// </summary>
public static class ProtectiveCoverageAudit
{
    private const string Qtsw2Prefix = "QTSW2:";
    private const string StopSuffix = ":STOP";
    private const string TargetSuffix = ":TARGET";

    /// <summary>
    /// Audit one instrument. Call only when broker position is non-flat for that instrument.
    /// </summary>
    /// <param name="instrument">Instrument key (e.g. MNQ, MGC)</param>
    /// <param name="snapshot">Broker account snapshot</param>
    /// <param name="activeIntentIds">Optional: known active intent ids for association</param>
    /// <param name="flattenInProgress">True if flatten already underway</param>
    /// <param name="recoveryInProgress">True if corrective recovery already running</param>
    /// <param name="instrumentBlocked">True if instrument already blocked</param>
    /// <param name="utcNow">Audit timestamp</param>
    public static ProtectiveAuditResult Audit(
        string instrument,
        AccountSnapshot snapshot,
        IReadOnlyCollection<string>? activeIntentIds,
        bool flattenInProgress,
        bool recoveryInProgress,
        bool instrumentBlocked,
        DateTimeOffset utcNow)
    {
        var result = new ProtectiveAuditResult
        {
            Instrument = instrument ?? "",
            Status = ProtectiveAuditStatus.PROTECTIVE_OK,
            AuditUtc = utcNow,
            FlattenInProgress = flattenInProgress,
            RecoveryInProgress = recoveryInProgress,
            InstrumentBlocked = instrumentBlocked
        };

        if (flattenInProgress)
        {
            result.Status = ProtectiveAuditStatus.PROTECTIVE_FLATTEN_IN_PROGRESS;
            return result;
        }

        if (recoveryInProgress)
        {
            result.Status = ProtectiveAuditStatus.PROTECTIVE_RECOVERY_IN_PROGRESS;
            return result;
        }

        var positions = snapshot.Positions ?? new List<PositionSnapshot>();
        var workingOrders = snapshot.WorkingOrders ?? new List<WorkingOrderSnapshot>();

        var pos = positions.FirstOrDefault(p =>
            string.Equals(p.Instrument?.Trim(), instrument?.Trim(), StringComparison.OrdinalIgnoreCase) && p.Quantity != 0);
        if (pos == null)
        {
            result.Status = ProtectiveAuditStatus.PROTECTIVE_OK;
            result.BrokerPositionQty = 0;
            return result;
        }

        var brokerQty = pos.Quantity;
        var absQty = Math.Abs(brokerQty);
        result.BrokerPositionQty = brokerQty;
        result.BrokerDirection = brokerQty > 0 ? "Long" : "Short";

        var instrumentOrders = workingOrders
            .Where(o => string.Equals(o.Instrument?.Trim(), instrument?.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var robotProtectiveStops = instrumentOrders
            .Where(o => IsRobotProtectiveStop(o))
            .ToList();
        var robotProtectiveTargets = instrumentOrders
            .Where(o => IsRobotProtectiveTarget(o))
            .ToList();

        // Flatten orders (QTSW2:FLATTEN:...) are not protective; exclude
        robotProtectiveStops = robotProtectiveStops.Where(o => !IsFlattenOrder(o)).ToList();
        robotProtectiveTargets = robotProtectiveTargets.Where(o => !IsFlattenOrder(o)).ToList();

        var totalStopQty = robotProtectiveStops.Sum(o => o.Quantity);
        var totalTargetQty = robotProtectiveTargets.Sum(o => o.Quantity);
        result.StopQty = totalStopQty;
        result.TargetQty = totalTargetQty;

        // Conflicting: multiple stops for same exposure without clear association.
        // Exception: CL1+CL2-style multi-intent — multiple stops with distinct tags are OK when total stop qty
        // matches broker exposure and total target qty matches (single-wave protectives still sum to position).
        if (robotProtectiveStops.Count > 1 && absQty > 0)
        {
            var distinctIntents = robotProtectiveStops.Select(o => GetIntentIdFromTag(o.Tag)).Where(id => id != null).Distinct().Count();
            if (distinctIntents > 1 && totalStopQty != absQty)
            {
                result.Status = ProtectiveAuditStatus.PROTECTIVE_CONFLICTING_ORDERS;
                result.Detail = $"Multiple stops ({robotProtectiveStops.Count}) from different intents (stop qty sum {totalStopQty} vs abs {absQty})";
                return result;
            }
        }

        // Missing stop: critical unless bounded post-fill convergence (mapped fill → PendingAlignment window)
        if (robotProtectiveStops.Count == 0)
        {
            if (IsWithinBoundedPostFillConvergenceWindow(instrument, utcNow) ||
                IsWithinBrokerExecutionCallbackWindow(instrument, utcNow))
            {
                result.Status = ProtectiveAuditStatus.PROTECTIVE_PENDING_CONVERGENCE;
                result.Detail =
                    "No broker-visible protective stop in snapshot; bounded convergence: mapped fill pending protective submit visibility (QuantExecutionControlStore)";
                return result;
            }

            result.Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP;
            result.Detail = $"Broker position {brokerQty} has no protective stop";
            return result;
        }

        // Stop qty mismatch: under-covered is critical
        if (totalStopQty < absQty)
        {
            if (IsWithinBoundedProtectiveResizeWindow(instrument, absQty, utcNow) ||
                IsWithinBrokerExecutionCallbackWindow(instrument, utcNow))
            {
                result.Status = ProtectiveAuditStatus.PROTECTIVE_PENDING_CONVERGENCE;
                result.Detail =
                    $"Stop qty {totalStopQty} < broker exposure {absQty}; bounded convergence: protective coverage pending";
                return result;
            }

            result.Status = ProtectiveAuditStatus.PROTECTIVE_STOP_QTY_MISMATCH;
            result.Detail = $"Stop qty {totalStopQty} < broker exposure {absQty}";
            return result;
        }

        // Stop price invalid: check first stop has sane price
        var firstStop = robotProtectiveStops.First();
        if (firstStop.StopPrice == null || firstStop.StopPrice < ProtectiveAuditPolicy.MIN_STOP_PRICE)
        {
            result.Status = ProtectiveAuditStatus.PROTECTIVE_STOP_PRICE_INVALID;
            result.StopPrice = firstStop.StopPrice;
            result.Detail = $"Stop price invalid or null: {firstStop.StopPrice}";
            return result;
        }
        result.StopPrice = firstStop.StopPrice;
        if (robotProtectiveTargets.Count > 0)
            result.TargetPrice = robotProtectiveTargets.First().Price;

        // Target validation (secondary)
        if (robotProtectiveTargets.Count == 0)
        {
            result.Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_TARGET;
            result.Detail = "Stop present but target missing";
            return result;
        }

        if (totalTargetQty < absQty)
        {
            if (IsWithinBoundedProtectiveResizeWindow(instrument, absQty, utcNow) ||
                IsWithinBrokerExecutionCallbackWindow(instrument, utcNow))
            {
                result.Status = ProtectiveAuditStatus.PROTECTIVE_PENDING_CONVERGENCE;
                result.Detail =
                    $"Target qty {totalTargetQty} < broker exposure {absQty}; bounded convergence: protective coverage pending";
                return result;
            }

            result.Status = ProtectiveAuditStatus.PROTECTIVE_TARGET_QTY_MISMATCH;
            result.Detail = $"Target qty {totalTargetQty} < broker exposure {absQty}";
            return result;
        }

        result.Status = ProtectiveAuditStatus.PROTECTIVE_OK;
        return result;
    }

    private static bool IsRobotProtectiveStop(WorkingOrderSnapshot o)
    {
        var tag = o.Tag ?? "";
        return tag.StartsWith(Qtsw2Prefix, StringComparison.OrdinalIgnoreCase) &&
               tag.EndsWith(StopSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRobotProtectiveTarget(WorkingOrderSnapshot o)
    {
        var tag = o.Tag ?? "";
        return tag.StartsWith(Qtsw2Prefix, StringComparison.OrdinalIgnoreCase) &&
               tag.EndsWith(TargetSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFlattenOrder(WorkingOrderSnapshot o)
    {
        var tag = o.Tag ?? "";
        return tag.StartsWith("QTSW2:FLATTEN:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetIntentIdFromTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || !tag.StartsWith(Qtsw2Prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = tag.Substring(Qtsw2Prefix.Length);
        if (rest.EndsWith(StopSuffix, StringComparison.OrdinalIgnoreCase))
            return rest.Substring(0, rest.Length - StopSuffix.Length).TrimEnd(':');
        if (rest.EndsWith(TargetSuffix, StringComparison.OrdinalIgnoreCase))
            return rest.Substring(0, rest.Length - TargetSuffix.Length).TrimEnd(':');
        return rest;
    }

    /// <summary>True if status is critical (missing stop, qty mismatch, invalid price, conflicting).</summary>
    public static bool IsCritical(ProtectiveAuditStatus status)
    {
        return status == ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP ||
               status == ProtectiveAuditStatus.PROTECTIVE_STOP_QTY_MISMATCH ||
               status == ProtectiveAuditStatus.PROTECTIVE_STOP_PRICE_INVALID ||
               status == ProtectiveAuditStatus.PROTECTIVE_CONFLICTING_ORDERS ||
               status == ProtectiveAuditStatus.PROTECTIVE_UNRESOLVED_POSITION;
    }

    /// <summary>
    /// Tier-1 bounded window after a <b>mapped trusted fill</b>: <see cref="JournalParityPendingLedger.TryRecordTrustedFill"/>
    /// calls <see cref="QuantExecutionControlStore.NotifyMappedTrustedFill"/>, which sets
    /// <see cref="QuantExecutionInstrumentPhase.PendingAlignment"/>, <see cref="QuantExpectedInstrumentState.LastMappedFillUtc"/>,
    /// and <see cref="QuantExpectedInstrumentState.PendingAlignmentExpiresUtc"/>.
    /// </summary>
    /// <remarks>
    /// <para><see cref="QuantExecutionInstrumentPhase.PendingAlignment"/> is entered on mapped trusted fill before protective submit
    /// completes — it does <b>not</b> alone imply submit. Convergence additionally requires
    /// <see cref="QuantExpectedInstrumentState.LastProtectiveStopSubmitUtc"/> &gt;= <see cref="QuantExpectedInstrumentState.LastMappedFillUtc"/>.</para>
    /// <para>Does not apply to <see cref="QuantExecutionInstrumentPhase.UnmappedExecution"/>,
    /// <see cref="QuantExecutionInstrumentPhase.ExecutionLocked"/>, or <see cref="QuantExecutionInstrumentPhase.RecoveryRequired"/> —
    /// those phases are never <c>PendingAlignment</c> for this audit, and <see cref="QuantExecutionControlStore.NotifyMappedTrustedFill"/>
    /// does not move an instrument from RecoveryRequired into PendingAlignment (durable recovery is not softened by mapped fills).
    /// Repeated PendingAlignment fills extend <see cref="QuantExpectedInstrumentState.PendingAlignmentExpiresUtc"/> per store remarks.</para>
    /// </remarks>
    private static bool IsWithinBoundedPostFillConvergenceWindow(string instrument, DateTimeOffset utcNow)
    {
        if (!FeatureFlags.QuantExecutionControlStoreEnabled)
            return false;

        var snap = QuantExecutionControlStore.GetSnapshot(instrument);
        if (snap.Phase != QuantExecutionInstrumentPhase.PendingAlignment)
            return false;

        if (!snap.Expected.LastMappedFillUtc.HasValue)
            return false;

        var expiry = snap.Expected.PendingAlignmentExpiresUtc;
        bool withinAlignmentWindow;
        if (expiry.HasValue)
            withinAlignmentWindow = utcNow <= expiry.Value;
        else
        {
            var windowMs = FeatureFlags.PostFillAlignmentWindowMs > 0 ? FeatureFlags.PostFillAlignmentWindowMs : 5000;
            withinAlignmentWindow =
                (utcNow - snap.Expected.LastMappedFillUtc.Value).TotalMilliseconds <= windowMs;
        }

        if (!withinAlignmentWindow)
            return false;

        if (!snap.Expected.LastProtectiveStopSubmitUtc.HasValue)
            return true;

        if (snap.Expected.LastProtectiveStopSubmitUtc.Value < snap.Expected.LastMappedFillUtc.Value)
            return false;

        return true;
    }

    private static bool IsWithinBoundedProtectiveResizeWindow(string instrument, int expectedProtectiveQty, DateTimeOffset utcNow)
    {
        return QuantExecutionControlStore.IsProtectiveResizePendingActive(instrument, expectedProtectiveQty, utcNow);
    }

    private static bool IsWithinBrokerExecutionCallbackWindow(string instrument, DateTimeOffset utcNow)
    {
        return QuantExecutionControlStore.IsBrokerExecutionCallbackPendingActive(instrument, utcNow);
    }
}
