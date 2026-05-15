using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Single entry point for execution permission. Replaces the procedural gate chain in
/// <see cref="NinjaTraderSimAdapter"/> (<c>TryExecutionSafetyGateForOrderSubmit</c>).
/// Phase 1 (facade): delegates to existing gate classes internally.
/// Phase 2: inline gate logic and remove standalone static classes.
/// 
/// Hard rule: UEA must NEVER derive ownership independently. It consumes a single
/// <see cref="InstrumentOwnershipSnapshot"/> obtained at the start of Evaluate().
/// </summary>
public sealed class UnifiedExecutionAuthority
{
    private readonly RobotLogger _log;
    private readonly InstrumentOwnershipLedger? _ownershipLedger;

    public UnifiedExecutionAuthority(RobotLogger log, InstrumentOwnershipLedger? ownershipLedger = null)
    {
        _log = log;
        _ownershipLedger = ownershipLedger;
    }

    /// <summary>
    /// Central authority surface for non-submit lifecycle/session actions. This is the first
    /// consolidation step for paths that historically made decisions outside UEA.
    /// </summary>
    public static ExecutionAuthorityActionDecision EvaluateAction(ExecutionAuthorityActionEvaluationRequest request)
    {
        var utcNow = request.UtcNow == default ? DateTimeOffset.UtcNow : request.UtcNow;

        switch (request.Action)
        {
            case ExecutionAuthorityAction.EntrySubmit:
                if (request.AuthorityFrame == null)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityEntrySubmit",
                        "AUTHORITY_FRAME_MISSING",
                        request.AuthorityFrame,
                        utcNow,
                        "Entry submit is denied without a canonical authority frame.");
                }

                if (!request.SnapshotSufficient ||
                    !string.IsNullOrWhiteSpace(request.AuthorityFrame.SnapshotError))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityEntrySubmit",
                        "SNAPSHOT_INSUFFICIENT",
                        request.AuthorityFrame,
                        utcNow,
                        "Entry submit is denied without a fresh sufficient broker/account snapshot.");
                }

                if (request.AuthorityFrame.PreflightGlobalKillSwitchActive)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityEntrySubmit",
                        "GLOBAL_KILL_SWITCH_ACTIVE",
                        request.AuthorityFrame,
                        utcNow,
                        "Entry submit is denied while the global kill switch is active.");
                }

                if (request.AuthorityFrame.PreflightMismatchExecutionBlockedForSubmit == true ||
                    (request.AuthorityFrame.PreflightMismatchExecutionBlockedForSubmit == null &&
                     request.AuthorityFrame.PreflightMismatchExecutionBlocked))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityEntrySubmit",
                        "MISMATCH_EXECUTION_BLOCK",
                        request.AuthorityFrame,
                        utcNow,
                        "Entry submit is denied while mismatch execution authority blocks risk-increasing submits.");
                }

                if (request.AuthorityFrame.PreflightInstrumentFrozenOrEpaBlocked)
                {
                    var reason = string.IsNullOrWhiteSpace(request.AuthorityFrame.PreflightInstrumentFrozenOrEpaBlockReason)
                        ? "INSTRUMENT_FROZEN_OR_EPA_BLOCKED"
                        : request.AuthorityFrame.PreflightInstrumentFrozenOrEpaBlockReason!.Trim();
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityEntrySubmit",
                        reason,
                        request.AuthorityFrame,
                        utcNow,
                        "Entry submit is denied while instrument-level execution authority blocks risk-increasing submits.");
                }

                if (request.AuthorityFrame.RecoveryExecutionDisallowed)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityEntrySubmit",
                        "RECOVERY_EXECUTION_DISALLOWED",
                        request.AuthorityFrame,
                        utcNow,
                        "Entry submit is denied while recovery execution is disabled.");
                }

                if (request.AuthorityFrame.HasUntrackedExposure)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityEntrySubmit",
                        "UNTRACKED_BROKER_EXPOSURE",
                        request.AuthorityFrame,
                        utcNow,
                        "Entry submit is denied because broker exposure or working orders are not explained by robot evidence.");
                }

                if (request.AuthorityFrame.HasContradiction)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityEntrySubmit",
                        "AUTHORITY_FRAME_CONTRADICTION",
                        request.AuthorityFrame,
                        utcNow,
                        "Entry submit is denied because broker, journal, ownership, registry, or snapshot evidence contradicts.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityEntrySubmit",
                    request.AuthorityFrame,
                    utcNow);

            case ExecutionAuthorityAction.MarketReentry:
                if (request.AuthorityFrame == null)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMarketReentry",
                        "AUTHORITY_FRAME_MISSING",
                        request.AuthorityFrame,
                        utcNow,
                        "Market reentry submit is denied without a canonical authority frame.");
                }

                if (!request.SnapshotSufficient ||
                    !string.IsNullOrWhiteSpace(request.AuthorityFrame.SnapshotError))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMarketReentry",
                        "SNAPSHOT_INSUFFICIENT",
                        request.AuthorityFrame,
                        utcNow,
                        "Market reentry submit is denied without a fresh sufficient broker/account snapshot.");
                }

                if (request.AuthorityFrame.PreflightGlobalKillSwitchActive)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMarketReentry",
                        "GLOBAL_KILL_SWITCH_ACTIVE",
                        request.AuthorityFrame,
                        utcNow,
                        "Market reentry submit is denied while the global kill switch is active.");
                }

                if (request.AuthorityFrame.PreflightMismatchExecutionBlockedForSubmit == true ||
                    (request.AuthorityFrame.PreflightMismatchExecutionBlockedForSubmit == null &&
                     request.AuthorityFrame.PreflightMismatchExecutionBlocked))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMarketReentry",
                        "MISMATCH_EXECUTION_BLOCK",
                        request.AuthorityFrame,
                        utcNow,
                        "Market reentry submit is denied while mismatch execution authority blocks risk-increasing submits.");
                }

                if (request.AuthorityFrame.PreflightInstrumentFrozenOrEpaBlocked)
                {
                    var reason = string.IsNullOrWhiteSpace(request.AuthorityFrame.PreflightInstrumentFrozenOrEpaBlockReason)
                        ? "INSTRUMENT_FROZEN_OR_EPA_BLOCKED"
                        : request.AuthorityFrame.PreflightInstrumentFrozenOrEpaBlockReason!.Trim();
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMarketReentry",
                        reason,
                        request.AuthorityFrame,
                        utcNow,
                        "Market reentry submit is denied while instrument-level execution authority blocks risk-increasing submits.");
                }

                if (request.AuthorityFrame.RecoveryExecutionDisallowed)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMarketReentry",
                        "RECOVERY_EXECUTION_DISALLOWED",
                        request.AuthorityFrame,
                        utcNow,
                        "Market reentry submit is denied while recovery execution is disabled.");
                }

                if (request.AuthorityFrame.HasUntrackedExposure &&
                    !IsBoundedMarketReentryAlignmentLag(request.AuthorityFrame))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMarketReentry",
                        "UNTRACKED_BROKER_EXPOSURE",
                        request.AuthorityFrame,
                        utcNow,
                        "Market reentry submit is denied because broker exposure or working orders are not explained by bounded robot evidence.");
                }

                if (request.AuthorityFrame.HasContradiction &&
                    !IsBoundedMarketReentryAlignmentLag(request.AuthorityFrame))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMarketReentry",
                        "AUTHORITY_FRAME_CONTRADICTION",
                        request.AuthorityFrame,
                        utcNow,
                        "Market reentry submit is denied because broker, journal, ownership, registry, or snapshot evidence contradicts.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityMarketReentry",
                    request.AuthorityFrame,
                    utcNow);

            case ExecutionAuthorityAction.ProtectiveSubmit:
                if (request.AuthorityFrame == null)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityProtectiveSubmit",
                        "AUTHORITY_FRAME_MISSING",
                        request.AuthorityFrame,
                        utcNow,
                        "Protective submit is denied without a canonical authority frame.");
                }

                if (!request.SnapshotSufficient ||
                    !string.IsNullOrWhiteSpace(request.AuthorityFrame.SnapshotError))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityProtectiveSubmit",
                        "SNAPSHOT_INSUFFICIENT",
                        request.AuthorityFrame,
                        utcNow,
                        "Protective submit is denied without a fresh sufficient broker/account snapshot.");
                }

                if (request.AuthorityFrame.RecoveryExecutionDisallowed)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityProtectiveSubmit",
                        "RECOVERY_EXECUTION_DISALLOWED",
                        request.AuthorityFrame,
                        utcNow,
                        "Protective submit is denied while recovery execution is disabled.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityProtectiveSubmit",
                    request.AuthorityFrame,
                    utcNow);

            case ExecutionAuthorityAction.ProtectiveBlockCreate:
                if (string.IsNullOrWhiteSpace(request.Instrument))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityProtectiveBlockCreate",
                        "INSTRUMENT_REQUIRED",
                        request.AuthorityFrame,
                        utcNow,
                        "Protective block creation is denied without an instrument scope.");
                }

                if (request.HasProtectiveBlock == false &&
                    !string.Equals(request.AuthorityFrame?.ProtectiveCoverageState, "PROTECTIVE_MISSING_STOP", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(request.AuthorityFrame?.ProtectiveCoverageState, "PROTECTIVE_STOP_QTY_MISMATCH", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(request.AuthorityFrame?.ProtectiveCoverageState, "PROTECTIVE_STOP_PRICE_INVALID", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(request.AuthorityFrame?.ProtectiveCoverageState, "PROTECTIVE_CONFLICTING_ORDERS", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(request.AuthorityFrame?.ProtectiveCoverageState, "PROTECTIVE_UNRESOLVED_POSITION", StringComparison.OrdinalIgnoreCase))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityProtectiveBlockCreate",
                        "PROTECTIVE_CRITICAL_REQUIRED",
                        request.AuthorityFrame,
                        utcNow,
                        "Protective block creation is denied unless protective coverage evidence is critical.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityProtectiveBlockCreate",
                    request.AuthorityFrame,
                    utcNow);

            case ExecutionAuthorityAction.ProtectiveBlockClear:
                if (string.IsNullOrWhiteSpace(request.Instrument))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityProtectiveBlockClear",
                        "INSTRUMENT_REQUIRED",
                        request.AuthorityFrame,
                        utcNow,
                        "Protective block clear is denied without an instrument scope.");
                }

                if (!request.SnapshotSufficient ||
                    !string.IsNullOrWhiteSpace(request.AuthorityFrame?.SnapshotError))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityProtectiveBlockClear",
                        "SNAPSHOT_INSUFFICIENT",
                        request.AuthorityFrame,
                        utcNow,
                        "Protective block clear is denied without a fresh sufficient broker/account snapshot.");
                }

                if (request.PendingExecutionWorkload > 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityProtectiveBlockClear",
                        "PENDING_EXECUTION_WORKLOAD",
                        request.AuthorityFrame,
                        utcNow,
                        "Protective block clear is denied while IEA execution work remains pending.");
                }

                if (request.HasProtectiveBlock)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityProtectiveBlockClear",
                        "PROTECTIVE_BLOCK_STILL_ACTIVE",
                        request.AuthorityFrame,
                        utcNow,
                        "Protective block clear is denied while the latest protective audit is still critical.");
                }

                if (request.BrokerAbsQty > 0 &&
                    request.AuthorityFrame != null &&
                    request.AuthorityFrame.BrokerStopQty < request.BrokerAbsQty &&
                    !request.AuthorityFrame.ProtectivePending &&
                    !request.AuthorityFrame.QecPendingAlignment)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityProtectiveBlockClear",
                        "PROTECTIVE_STOP_COVERAGE_INSUFFICIENT",
                        request.AuthorityFrame,
                        utcNow,
                        "Protective block clear is denied while broker exposure lacks aggregate stop coverage.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityProtectiveBlockClear",
                    request.AuthorityFrame,
                    utcNow);

            case ExecutionAuthorityAction.Flatten:
                if (request.AuthorityFrame == null)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityFlatten",
                        "AUTHORITY_FRAME_MISSING",
                        request.AuthorityFrame,
                        utcNow,
                        "Flatten submit is denied without a canonical authority frame.");
                }

                if (!request.SnapshotSufficient ||
                    !string.IsNullOrWhiteSpace(request.AuthorityFrame.SnapshotError))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityFlatten",
                        "SNAPSHOT_INSUFFICIENT",
                        request.AuthorityFrame,
                        utcNow,
                        "Flatten submit is denied without a fresh sufficient broker/account snapshot.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityFlatten",
                    request.AuthorityFrame,
                    utcNow);

            case ExecutionAuthorityAction.CancelSubmit:
                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityCancelSubmit",
                    request.AuthorityFrame,
                    utcNow,
                    "Cancel submit is a safety action; entry, mismatch, and kill-switch blocks do not block cancel authority.");

            case ExecutionAuthorityAction.TerminalCommit:
                if (request.HasOpenLifecycleExposureOrPendingReentry &&
                    !request.HasCompletedTradeForCurrentStream &&
                    !request.IsIntentionalOpenLifecycleTerminalCommit)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityTerminalCommit",
                        "OPEN_LIFECYCLE_EXPOSURE_OR_REENTRY",
                        request.AuthorityFrame,
                        utcNow,
                        "Terminal stream commit is denied while lifecycle/reentry evidence remains open.");
                }

                if (request.HasOpenLifecycleExposureOrPendingReentry &&
                    !request.HasCompletedTradeForCurrentStream &&
                    request.IsIntentionalOpenLifecycleTerminalCommit &&
                    request.AuthorityFrame?.HasContradiction == true)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityTerminalCommit",
                        "OPEN_LIFECYCLE_AUTHORITY_CONTRADICTION",
                        request.AuthorityFrame,
                        utcNow,
                        "Intentional terminal commit is denied because the canonical frame still contains contradictory broker/journal/ownership evidence.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityTerminalCommit",
                    request.AuthorityFrame,
                    utcNow);

            case ExecutionAuthorityAction.LatchCreate:
                if (string.IsNullOrWhiteSpace(request.Instrument))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityLatchCreate",
                        "INSTRUMENT_REQUIRED",
                        request.AuthorityFrame,
                        utcNow,
                        "Durable risk latch creation is denied without an instrument scope.");
                }

                if (string.IsNullOrWhiteSpace(request.DurableLatchReason))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityLatchCreate",
                        "LATCH_REASON_REQUIRED",
                        request.AuthorityFrame,
                        utcNow,
                        "Durable risk latch creation is denied without an explicit safety reason.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityLatchCreate",
                    request.AuthorityFrame,
                    utcNow);

            case ExecutionAuthorityAction.SessionCloseGlobalSweep:
                if (request.HasActiveTrackedReentry)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthoritySessionCloseGlobalSweep",
                        "TRACKED_ACTIVE_REENTRY",
                        request.AuthorityFrame,
                        utcNow,
                        "Session-close sweep is denied when exposure is explained by an active tracked reentry.");
                }

                if (!request.SessionCloseSweepWindowFresh)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthoritySessionCloseGlobalSweep",
                        "SESSION_CLOSE_SWEEP_WINDOW_STALE",
                        request.AuthorityFrame,
                        utcNow,
                        "Session-close sweep is denied because the originating close-window evidence is stale.");
                }

                if (request.JournalOpenQty > 0 &&
                    !request.HasActiveLifecycleEvidence)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthoritySessionCloseGlobalSweep",
                        "JOURNAL_OPEN_WITHOUT_ACTIVE_LIFECYCLE",
                        request.AuthorityFrame,
                        utcNow,
                        "Session-close sweep is denied because open journal exposure has no active stream/reentry lifecycle evidence.");
                }

                if ((request.BrokerAbsQty > 0 || request.BrokerWorkingOrderCount > 0) &&
                    !request.HasRobotEvidence)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthoritySessionCloseGlobalSweep",
                        "NO_ROBOT_EVIDENCE",
                        request.AuthorityFrame,
                        utcNow,
                        "Session-close sweep is denied when broker exposure has no journal or robot-tag evidence.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    "AuthoritySessionCloseGlobalSweep",
                    request.AuthorityFrame,
                    utcNow);

            case ExecutionAuthorityAction.JournalCompleteBrokerFlat:
                if (!request.SnapshotSufficient)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityJournalCompleteBrokerFlat",
                        "SNAPSHOT_INSUFFICIENT",
                        request.AuthorityFrame,
                        utcNow,
                        "Journal broker-flat completion is denied without sufficient broker/account evidence.");
                }

                if (request.BrokerAbsQty > 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityJournalCompleteBrokerFlat",
                        "BROKER_NOT_FLAT",
                        request.AuthorityFrame,
                        utcNow,
                        "Journal broker-flat completion is denied while broker position remains open.");
                }

                if (request.BrokerWorkingOrderCount > 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityJournalCompleteBrokerFlat",
                        "WORKING_ORDERS_OPEN",
                        request.AuthorityFrame,
                        utcNow,
                        "Journal broker-flat completion is denied while broker working orders remain.");
                }

                if (request.OwnershipOpenQty > 0 ||
                    request.OwnershipActiveSlotCount > 0 ||
                    request.OwnershipOrphanSlotCount > 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityJournalCompleteBrokerFlat",
                        "OWNERSHIP_GROSS_OPEN",
                        request.AuthorityFrame,
                        utcNow,
                        "Journal broker-flat completion is denied while ownership ledger still has gross open exposure.");
                }

                if (request.HasOpenLifecycleExposureOrPendingReentry)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityJournalCompleteBrokerFlat",
                        "OPEN_LIFECYCLE_EXPOSURE_OR_REENTRY",
                        request.AuthorityFrame,
                        utcNow,
                        "Journal broker-flat completion is denied while stream/reentry lifecycle evidence remains open.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityJournalCompleteBrokerFlat",
                    request.AuthorityFrame,
                    utcNow);

            case ExecutionAuthorityAction.LatchClear:
            case ExecutionAuthorityAction.LatchClearExplicitOperator:
            {
                var latchClearGateName = request.Action == ExecutionAuthorityAction.LatchClearExplicitOperator
                    ? "AuthorityLatchClearExplicitOperator"
                    : "AuthorityLatchClear";

                if (request.Action == ExecutionAuthorityAction.LatchClear &&
                    !QTSW2.Robot.Core.RiskLatchManager.IsAutoClearEligibleReason(request.DurableLatchReason))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        latchClearGateName,
                        "LATCH_REASON_NOT_AUTO_CLEAR_ELIGIBLE",
                        request.AuthorityFrame,
                        utcNow,
                        "Durable risk latch auto-clear is denied because the latch reason is not auto-clear eligible.");
                }

                if (request.AccountQty != 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        latchClearGateName,
                        "ACCOUNT_NOT_FLAT",
                        request.AuthorityFrame,
                        utcNow,
                        "Durable risk latch auto-clear is denied while reconciliation account quantity is not flat.");
                }

                if (request.BrokerAbsQty != 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        latchClearGateName,
                        "BROKER_NOT_FLAT",
                        request.AuthorityFrame,
                        utcNow,
                        "Durable risk latch auto-clear is denied while broker position is not flat.");
                }

                if (request.BrokerWorkingOrderCount != 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        latchClearGateName,
                        "WORKING_ORDERS_OPEN",
                        request.AuthorityFrame,
                        utcNow,
                        "Durable risk latch auto-clear is denied while broker working orders remain.");
                }

                if (request.JournalOpenQty != 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        latchClearGateName,
                        "JOURNAL_OPEN_QTY",
                        request.AuthorityFrame,
                        utcNow,
                        "Durable risk latch auto-clear is denied while execution journal open quantity remains.");
                }

                if (request.RealOpenQty != 0 || request.RecoveryOpenQty != 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        latchClearGateName,
                        "POSITION_AUTHORITY_OPEN_QTY",
                        request.AuthorityFrame,
                        utcNow,
                        "Durable risk latch auto-clear is denied while real/recovery authority still has open quantity.");
                }

                if (request.HasSupervisoryBlock)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        latchClearGateName,
                        "IEA_SUPERVISORY_BLOCK_ACTIVE",
                        request.AuthorityFrame,
                        utcNow,
                        "Durable risk latch auto-clear is denied while IEA supervisory block remains active.");
                }

                if (request.HasProtectiveBlock)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        latchClearGateName,
                        "PROTECTIVE_BLOCK_ACTIVE",
                        request.AuthorityFrame,
                        utcNow,
                        "Durable risk latch auto-clear is denied while protective coverage block remains active.");
                }

                if (request.HasMismatchBlock)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        latchClearGateName,
                        "MISMATCH_BLOCK_ACTIVE",
                        request.AuthorityFrame,
                        utcNow,
                        "Durable risk latch auto-clear is denied while mismatch block remains active.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    latchClearGateName,
                    request.AuthorityFrame,
                    utcNow);
            }

            case ExecutionAuthorityAction.MismatchRelease:
                if (request.AuthorityFrame == null)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMismatchRelease",
                        "AUTHORITY_FRAME_MISSING",
                        request.AuthorityFrame,
                        utcNow,
                        "Mismatch release is denied without a canonical authority frame.");
                }

                if (!request.SnapshotSufficient ||
                    !string.IsNullOrWhiteSpace(request.AuthorityFrame.SnapshotError))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMismatchRelease",
                        "SNAPSHOT_INSUFFICIENT",
                        request.AuthorityFrame,
                        utcNow,
                        "Mismatch release is denied without a fresh sufficient broker/account snapshot.");
                }

                if (request.PendingExecutionWorkload > 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMismatchRelease",
                        "PENDING_EXECUTION_WORKLOAD",
                        request.AuthorityFrame,
                        utcNow,
                        "Mismatch release is denied while IEA execution work remains pending.");
                }

                if (request.ReleaseValidatorReady != true)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMismatchRelease",
                        "STATE_CONSISTENCY_NOT_RELEASE_READY",
                        request.AuthorityFrame,
                        utcNow,
                        "Mismatch release is denied because the state-consistency validator is not release-ready.");
                }

                if (request.AuthorityFrame.HasContradiction)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityMismatchRelease",
                        "AUTHORITY_FRAME_CONTRADICTION",
                        request.AuthorityFrame,
                        utcNow,
                        "Mismatch release is denied because broker, journal, ownership, registry, or snapshot evidence contradicts.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityMismatchRelease",
                    request.AuthorityFrame,
                    utcNow);

            case ExecutionAuthorityAction.ShutdownSafeVerdict:
                if (request.AuthorityFrame == null)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityShutdownSafeVerdict",
                        "AUTHORITY_FRAME_MISSING",
                        request.AuthorityFrame,
                        utcNow,
                        "Shutdown safe verdict is denied without a canonical authority frame.");
                }

                if (!request.SnapshotSufficient ||
                    !string.IsNullOrWhiteSpace(request.AuthorityFrame.SnapshotError))
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityShutdownSafeVerdict",
                        "SNAPSHOT_INSUFFICIENT",
                        request.AuthorityFrame,
                        utcNow,
                        "Shutdown safe verdict is denied without sufficient shutdown evidence.");
                }

                if (request.BrokerAbsQty != 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityShutdownSafeVerdict",
                        "BROKER_NOT_FLAT",
                        request.AuthorityFrame,
                        utcNow,
                        "Shutdown safe verdict is denied while broker position remains open.");
                }

                if (request.BrokerWorkingOrderCount != 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityShutdownSafeVerdict",
                        "WORKING_ORDERS_OPEN",
                        request.AuthorityFrame,
                        utcNow,
                        "Shutdown safe verdict is denied while broker working orders remain.");
                }

                if (request.JournalOpenQty != 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityShutdownSafeVerdict",
                        "JOURNAL_OPEN_QTY",
                        request.AuthorityFrame,
                        utcNow,
                        "Shutdown safe verdict is denied while execution journal open quantity remains.");
                }

                if (request.OwnershipOpenQty != 0 ||
                    request.OwnershipActiveSlotCount != 0 ||
                    request.OwnershipOrphanSlotCount != 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityShutdownSafeVerdict",
                        "OWNERSHIP_OPEN_QTY",
                        request.AuthorityFrame,
                        utcNow,
                        "Shutdown safe verdict is denied while ownership ledger evidence remains open.");
                }

                if (request.ActiveIntentCount != 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityShutdownSafeVerdict",
                        "ACTIVE_INTENTS_OPEN",
                        request.AuthorityFrame,
                        utcNow,
                        "Shutdown safe verdict is denied while active submitted intents remain.");
                }

                if (request.NonTerminalStreamCount != 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityShutdownSafeVerdict",
                        "NONTERMINAL_STREAMS_OPEN",
                        request.AuthorityFrame,
                        utcNow,
                        "Shutdown safe verdict is denied while nonterminal streams remain.");
                }

                if (request.RealOpenQty != 0 ||
                    request.RecoveryOpenQty != 0)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityShutdownSafeVerdict",
                        "POSITION_AUTHORITY_OPEN_QTY",
                        request.AuthorityFrame,
                        utcNow,
                        "Shutdown safe verdict is denied while real/recovery authority still has open quantity.");
                }

                if (request.AuthorityFrame.HasContradiction)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityShutdownSafeVerdict",
                        "AUTHORITY_FRAME_CONTRADICTION",
                        request.AuthorityFrame,
                        utcNow,
                        "Shutdown safe verdict is denied because broker, journal, ownership, registry, or snapshot evidence contradicts.");
                }

                if (!request.AuthorityFrame.IsCleanFlat)
                {
                    return ExecutionAuthorityActionDecision.Deny(
                        "AuthorityShutdownSafeVerdict",
                        "CLEAN_FLAT_PREDICATES_FAILED",
                        request.AuthorityFrame,
                        utcNow,
                        "Shutdown safe verdict is denied because one or more clean-flat predicates failed.");
                }

                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityShutdownSafeVerdict",
                    request.AuthorityFrame,
                    utcNow);

            default:
                return ExecutionAuthorityActionDecision.Allow(
                    "AuthorityActionShadow",
                    request.AuthorityFrame,
                    utcNow,
                    "No central lifecycle policy is defined for this action yet.");
        }
    }

    private static bool IsMarketReentrySubmitPath(string? submitPath) =>
        string.Equals(submitPath, "SUBMIT_MARKET_REENTRY", StringComparison.Ordinal);

    private static bool IsSafetySubmitIntent(SubmitIntent intent) =>
        intent == SubmitIntent.RiskCoverage ||
        intent == SubmitIntent.RiskReducing ||
        intent == SubmitIntent.Emergency;

    private static ExecutionAuthorityAction? ResolveSubmitAuthorityAction(AuthorityEvaluationRequest request)
    {
        if (IsMarketReentrySubmitPath(request.SubmitPath))
            return ExecutionAuthorityAction.MarketReentry;
        if (request.SubmitIntent == SubmitIntent.RiskCoverage)
            return ExecutionAuthorityAction.ProtectiveSubmit;
        if (request.SubmitIntent == SubmitIntent.RiskReducing)
            return ExecutionAuthorityAction.CancelSubmit;
        if (request.SubmitIntent == SubmitIntent.OpeningEntry ||
            request.SubmitIntent == SubmitIntent.RiskIncreasing)
            return ExecutionAuthorityAction.EntrySubmit;
        if (request.SubmitIntent == SubmitIntent.Emergency)
            return ExecutionAuthorityAction.Flatten;
        return null;
    }

    private static bool IsBoundedMarketReentryAlignmentLag(ExecutionAuthorityFrame frame)
    {
        if (!frame.QecPendingAlignment)
            return false;

        if (!string.IsNullOrWhiteSpace(frame.SnapshotError))
            return false;

        if (frame.PreflightGlobalKillSwitchActive ||
            frame.PreflightMismatchExecutionBlocked ||
            frame.PreflightMismatchExecutionBlockedForSubmit == true ||
            frame.PreflightInstrumentFrozenOrEpaBlocked ||
            frame.RecoveryExecutionDisallowed)
            return false;

        if (frame.BrokerWorkingOrdersCount > 0 &&
            frame.IeaOwnedPlusAdoptedWorking == 0 &&
            frame.IeaRegistryWorkingCount == 0 &&
            frame.IeaMismatchTrustedWorkingCount == 0)
            return false;

        return true;
    }

    private static bool ResolveGlobalKillSwitchActive(AuthorityEvaluationRequest request) =>
        request.PreflightGlobalKillSwitchActive ?? (request.GlobalKillSwitchActive?.Invoke() == true);

    private static bool ResolveMismatchExecutionBlocked(AuthorityEvaluationRequest request, string instrument)
    {
        if (string.IsNullOrEmpty(instrument))
            return false;
        if (request.PreflightMismatchExecutionBlockedForSubmit.HasValue)
            return request.PreflightMismatchExecutionBlockedForSubmit.Value;
        if (request.PreflightMismatchExecutionBlocked.HasValue)
            return request.PreflightMismatchExecutionBlocked.Value;
        if (request.MismatchExecutionBlockedForSubmit != null)
            return request.MismatchExecutionBlockedForSubmit(instrument, request.SubmitPath);
        return request.MismatchExecutionBlocked?.Invoke(instrument) == true;
    }

    private static bool ResolveInstrumentFrozenOrEpaBlocked(AuthorityEvaluationRequest request, string instrument) =>
        !string.IsNullOrEmpty(instrument) &&
        (request.PreflightInstrumentFrozenOrEpaBlocked ??
         (request.InstrumentFrozenOrEpaBlocked?.Invoke(instrument, request.SubmitPath) == true));

    private static string? ResolveInstrumentFrozenOrEpaBlockReason(AuthorityEvaluationRequest request, string instrument)
    {
        if (string.IsNullOrEmpty(instrument))
            return null;
        if (!string.IsNullOrWhiteSpace(request.PreflightInstrumentFrozenOrEpaBlockReason))
            return request.PreflightInstrumentFrozenOrEpaBlockReason;
        var callbackReason = request.InstrumentFrozenOrEpaBlockReason?.Invoke(instrument, request.SubmitPath);
        return string.IsNullOrWhiteSpace(callbackReason)
            ? null
            : callbackReason.Trim();
    }

    /// <summary>
    /// Evaluate all four gates in order. Short-circuits on first deny.
    /// A single <see cref="InstrumentOwnershipSnapshot"/> is captured once and passed to all gates.
    /// </summary>
    public AuthorityDecision Evaluate(AuthorityEvaluationRequest request)
    {
        var trail = new List<GateEvaluation>();
        var utcNow = request.UtcNow;
        var instrument = request.Instrument?.Trim() ?? "";
        ExecutionAuthorityFrame? authorityFrame = request.AuthorityFrame ?? request.PrebuiltSafetyRequest?.AuthorityFrame;

        InstrumentOwnershipSnapshot? ownershipSnapshot = authorityFrame?.OwnershipSnapshot;
        if (FeatureFlags.CanonicalOwnershipLedgerEnabled && _ownershipLedger != null && !string.IsNullOrEmpty(instrument))
        {
            ownershipSnapshot = _ownershipLedger.GetOwnershipSnapshot(
                request.AccountName ?? "default", instrument);
            if (authorityFrame == null)
            {
                authorityFrame = new ExecutionAuthorityFrame
                {
                    FrameId = ExecutionAuthorityFrame.CreateFrameId(utcNow),
                    Source = "uea_ownership_preflight",
                    Instrument = instrument,
                    CanonicalInstrument = request.CanonicalInstrument,
                    IntentId = request.IntentId,
                    SubmitPath = request.SubmitPath,
                    DecisionUtc = utcNow,
                    FrameCreatedUtc = DateTimeOffset.UtcNow,
                    LedgerAccountName = request.AccountName,
                    LedgerOwnershipVersion = ownershipSnapshot.OwnershipVersion,
                    LedgerSignedNetQty = ownershipSnapshot.LedgerSignedNetQty,
                    LedgerActiveSlotCount = ownershipSnapshot.ActiveSlotCount,
                    LedgerOrphanSlotCount = ownershipSnapshot.OrphanSlotCount,
                    OwnershipSnapshot = ownershipSnapshot
                };
            }
        }

        var globalKillSwitchActive = ResolveGlobalKillSwitchActive(request);
        var mismatchBlocked = ResolveMismatchExecutionBlocked(request, instrument);
        var instrumentFrozenOrEpaBlocked = ResolveInstrumentFrozenOrEpaBlocked(request, instrument);
        var instrumentFrozenOrEpaBlockReason = ResolveInstrumentFrozenOrEpaBlockReason(request, instrument);

        // Gate 1: KillSwitch + Recovery (EPA preflight)
        var skipMismatch = IsSafetySubmitIntent(request.SubmitIntent);
        string? epaDeny = null;
        if (globalKillSwitchActive && !IsSafetySubmitIntent(request.SubmitIntent))
            epaDeny = "GLOBAL_KILL_SWITCH_ACTIVE";
        else if (!skipMismatch && mismatchBlocked)
            epaDeny = "MISMATCH_EXECUTION_BLOCK";
        else if (instrumentFrozenOrEpaBlocked)
            epaDeny = string.IsNullOrWhiteSpace(instrumentFrozenOrEpaBlockReason)
                ? "INSTRUMENT_FROZEN_OR_EPA_BLOCKED"
                : instrumentFrozenOrEpaBlockReason;

        if (!string.IsNullOrEmpty(epaDeny))
        {
            trail.Add(new GateEvaluation { GateName = "Gate1_KillSwitch_Recovery", Passed = false, DenyReason = epaDeny });
            return AuthorityDecision.Deny("Gate1_KillSwitch_Recovery", epaDeny, ownershipSnapshot, authorityFrame, trail, utcNow);
        }
        trail.Add(new GateEvaluation { GateName = "Gate1_KillSwitch_Recovery", Passed = true });

        // Gate 2: Explicit Mismatch Block (first-class authority input)
        // Gate1 checks mismatch via EPA preflight, but this gate is the explicit mismatch authority
        // for non-coverage submits. When Gate1+Gate2 merge in Phase 2, this becomes the single check.
        if (mismatchBlocked
            && !IsSafetySubmitIntent(request.SubmitIntent))
        {
            trail.Add(new GateEvaluation { GateName = "Gate2_MismatchBlock", Passed = false, DenyReason = "MISMATCH_EXECUTION_BLOCKED" });
            return AuthorityDecision.Deny("Gate2_MismatchBlock", "MISMATCH_EXECUTION_BLOCKED", ownershipSnapshot, authorityFrame, trail, utcNow);
        }
        trail.Add(new GateEvaluation { GateName = "Gate2_MismatchBlock", Passed = true });

        // Gate 3: Quant Phase + Parity + Ownership Authority (Structural Layer)
        if (request.BuildSafetyRequest == null)
        {
            trail.Add(new GateEvaluation { GateName = "Gate3_Structural", Passed = true, Detail = "no_safety_request_builder" });
        }
        else
        {
            ExecutionSafetyEvaluationRequest safetyReq;
            try
            {
                safetyReq = request.PrebuiltSafetyRequest ??
                            request.BuildSafetyRequest(instrument, request.IntentId, utcNow);
                authorityFrame = safetyReq.AuthorityFrame ?? authorityFrame;
                ownershipSnapshot = authorityFrame?.OwnershipSnapshot ?? ownershipSnapshot;
            }
            catch
            {
                trail.Add(new GateEvaluation { GateName = "Gate3_Structural", Passed = false, DenyReason = "account_snapshot_failed" });
                return AuthorityDecision.Deny("Gate3_Structural", "account_snapshot_failed", ownershipSnapshot, authorityFrame, trail, utcNow);
            }

            var submitAuthorityAction = ResolveSubmitAuthorityAction(request);
            if (submitAuthorityAction.HasValue)
            {
                var submitAuthority = EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
                {
                    Action = submitAuthorityAction.Value,
                    Source = "UnifiedExecutionAuthority.Submit",
                    Instrument = instrument,
                    IntentId = request.IntentId,
                    UtcNow = utcNow,
                    SnapshotSufficient = safetyReq.AccountSnapshot != null,
                    BrokerAbsQty = Math.Abs(authorityFrame?.BrokerPositionQty ?? 0),
                    BrokerWorkingOrderCount = authorityFrame?.BrokerWorkingOrdersCount ?? 0,
                    JournalOpenQty = authorityFrame?.JournalOpenQty ?? 0,
                    OwnershipOpenQty = authorityFrame?.OwnershipOpenQty ?? 0,
                    OwnershipActiveSlotCount = authorityFrame?.OwnershipActiveSlots ?? 0,
                    OwnershipOrphanSlotCount = authorityFrame?.OwnershipOrphanSlots ?? 0,
                    RealOpenQty = authorityFrame?.RealOpenQty ?? 0,
                    RecoveryOpenQty = authorityFrame?.RecoveryOpenQty ?? 0,
                    AuthorityFrame = authorityFrame
                });
                if (!submitAuthority.Allowed)
                {
                    trail.Add(new GateEvaluation
                    {
                        GateName = submitAuthority.GateName,
                        Passed = false,
                        DenyReason = submitAuthority.DenyReason,
                        Detail = submitAuthority.Detail
                    });
                    return AuthorityDecision.Deny(
                        submitAuthority.GateName,
                        submitAuthority.DenyReason ?? "SUBMIT_AUTHORITY_DENIED",
                        ownershipSnapshot,
                        authorityFrame,
                        trail,
                        utcNow);
                }

                trail.Add(new GateEvaluation
                {
                    GateName = submitAuthority.GateName,
                    Passed = true,
                    Detail = submitAuthority.Detail
                });
            }

            var flattenBypass = request.SubmitIntent == SubmitIntent.Emergency;
            if (!ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure(safetyReq, request.SubmitPath, flattenBypass, out var structSnap))
            {
                trail.Add(new GateEvaluation
                {
                    GateName = "Gate3_Structural",
                    Passed = false,
                    DenyReason = structSnap.Reason,
                    Detail = structSnap.Detail
                });
                return AuthorityDecision.Deny("Gate3_Structural", structSnap.Reason ?? "structural_deny", ownershipSnapshot, authorityFrame, trail, utcNow);
            }
            trail.Add(new GateEvaluation { GateName = "Gate3_Structural", Passed = true, Detail = structSnap.Detail });

            // Gate 4: Overlay (snapshot freshness, unsafe lock)
            if (!ExecutionSafetyGate.TryEvaluateExecutionOverlay(safetyReq, out var overlay) || overlay.IsBlocked)
            {
                trail.Add(new GateEvaluation
                {
                    GateName = "Gate4_Overlay",
                    Passed = false,
                    DenyReason = overlay.BlockReason.ToString(),
                    Detail = overlay.Detail
                });
                return AuthorityDecision.Deny("Gate4_Overlay", $"OVERLAY:{overlay.BlockReason}", ownershipSnapshot, authorityFrame, trail, utcNow);
            }
            trail.Add(new GateEvaluation { GateName = "Gate4_Overlay", Passed = true });
        }

        return AuthorityDecision.Allow(ownershipSnapshot, authorityFrame, trail, utcNow);
    }
}
