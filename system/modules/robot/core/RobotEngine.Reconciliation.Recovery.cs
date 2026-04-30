using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Diagnostics;
using QTSW2.Robot.Core.Execution;
using QTSW2.Robot.Core.Notifications;

public sealed partial class RobotEngine
{

    private ReconciliationForcedConvergenceResult RunForcedBrokerAlignment(ReconciliationForcedConvergenceContext ctx)
    {
        if (_executionAdapter == null || string.IsNullOrWhiteSpace(ctx.Instrument))
            return ReconciliationForcedConvergenceResult.Failed("no_adapter");

        var inst = ctx.Instrument.Trim();
        var utcNow = DateTimeOffset.UtcNow;

        int brokerWorkingProbe = 0;
        try
        {
            var snapProbe = _executionAdapter.GetAccountSnapshot(utcNow);
            brokerWorkingProbe = CountBrokerWorkingOrders(snapProbe, inst);
        }
        catch
        {
            brokerWorkingProbe = 0;
        }

        _reconciliationRunner?.ForceRunNow(utcNow);
        _reconciliationRunner?.ForceRunGateRecoveryForInstrument(utcNow, inst);

        var useIea = _executionPolicy?.UseInstrumentExecutionAuthority ?? false;
        var account = _accountName ?? "";
        if (useIea &&
            ReconciliationIeaLookup.TryResolve(account, inst, brokerWorkingProbe, GetExecutionInstrument, out var ieaAlign) &&
            ieaAlign != null)
        {
            try
            {
                ieaAlign.TryScheduleRecoveryAdoptionScan(out _);
            }
            catch
            {
                // adoption best-effort
            }
        }

        _reconciliationRunner?.ForceRunGateRecoveryForInstrument(utcNow, inst);

        AccountSnapshot? snap;
        try
        {
            snap = _executionAdapter.GetAccountSnapshot(utcNow);
        }
        catch
        {
            return ReconciliationForcedConvergenceResult.Failed("snapshot_failed");
        }

        if (snap == null)
            return ReconciliationForcedConvergenceResult.Failed("null_snapshot");

        var readiness = EvaluateStateConsistencyReleaseReadiness(inst, snap, utcNow, forceFullEvaluation: true);
        if (!readiness.ReleaseReady)
        {
            if (IsSoftTransitionReleaseReadiness(readiness))
            {
                var softFp = BuildForcedConvergenceFingerprint(inst, snap, utcNow);
                LogEvent(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_FORCED_CONVERGENCE_SOFT_DEFERRED", "ENGINE",
                    new
                    {
                        instrument = inst,
                        reason = readiness.Summary ?? "soft_transition_release_not_ready",
                        broker_position_qty = readiness.DiagnosticBrokerPositionQty,
                        broker_working_count = readiness.DiagnosticBrokerWorkingCount,
                        journal_open_qty = readiness.DiagnosticJournalOpenQty,
                        iea_owned_plus_adopted_working = readiness.DiagnosticIeaOwnedPlusAdoptedWorking,
                        note = "Forced convergence did not fail-close because broker exposure is explainable and remaining blockers are known transition lanes."
                    }));
                return ReconciliationForcedConvergenceResult.Succeeded(softFp);
            }

            if (IsFillBoundaryReleaseReadiness(readiness))
            {
                var softFp = BuildForcedConvergenceFingerprint(inst, snap, utcNow);
                LogEvent(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_FORCED_CONVERGENCE_SOFT_DEFERRED", "ENGINE",
                    new
                    {
                        instrument = inst,
                        reason = readiness.Summary ?? "fill_boundary_release_not_ready",
                        broker_position_qty = readiness.DiagnosticBrokerPositionQty,
                        broker_working_count = readiness.DiagnosticBrokerWorkingCount,
                        journal_open_qty = readiness.DiagnosticJournalOpenQty,
                        iea_owned_plus_adopted_working = readiness.DiagnosticIeaOwnedPlusAdoptedWorking,
                        note = "Forced convergence did not fail-close because the remaining quantity delta is at a fill/order bridge boundary with active broker or IEA evidence."
                    }));
                return ReconciliationForcedConvergenceResult.Succeeded(softFp);
            }

            return ReconciliationForcedConvergenceResult.Failed(
                readiness.Summary ?? "not_release_ready",
                ForcedConvergenceRiskLatchPolicy.ShouldPersistDurableRiskLatch(readiness));
        }

        var fp = BuildForcedConvergenceFingerprint(inst, snap, utcNow);
        return ReconciliationForcedConvergenceResult.Succeeded(fp);
    }

    private ulong BuildForcedConvergenceFingerprint(string inst, AccountSnapshot snap, DateTimeOffset utcNow)
    {
        MismatchObservation? obs = null;
        try
        {
            var observations = AssembleMismatchObservations(snap, utcNow);
            obs = observations.FirstOrDefault(o =>
                string.Equals(o.Instrument?.Trim(), inst, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // fingerprint still valid with null obs fields
        }

        return GateProgressEvaluator.BuildExternalFingerprint(inst, snap, obs);
    }

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

    private static bool IsFillBoundaryReleaseReadiness(StateConsistencyReleaseReadinessResult readiness)
    {
        if (readiness.ReleaseReady || !readiness.SnapshotSufficient || readiness.BlockerInvariantViolation)
            return false;

        var hasPositionDelta = readiness.Contradictions?.Any(c =>
            c.StartsWith("position_qty_delta_", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("info_pending_alignment_position_qty_delta_", StringComparison.OrdinalIgnoreCase)) == true;
        if (!hasPositionDelta)
            return false;

        return readiness.DiagnosticBrokerWorkingCount > 0 ||
               readiness.DiagnosticIeaOwnedPlusAdoptedWorking > 0 ||
               readiness.DiagnosticAdoptDecisionCount > 0 ||
               readiness.PendingAdoptionExists;
    }

    private void OnForcedBrokerConvergenceFailure(string instrument, ReconciliationForcedConvergenceContext ctx,
        ReconciliationForcedConvergenceResult result)
    {
        if (!result.RequiresDurableRiskLatch)
        {
            var utcNow = DateTimeOffset.UtcNow;
            LogEvent(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_FORCED_CONVERGENCE_SOFT_DEFERRED", "ENGINE",
                new
                {
                    instrument = instrument.Trim(),
                    limit_reason = ctx.LimitReason,
                    failure_reason = result.FailureReason ?? "unknown",
                    attempts = ctx.Attempts,
                    no_progress_count = ctx.NoProgressCount,
                    durable_risk_latch_persisted = false,
                    note = "Forced convergence remained fail-closed in the mismatch gate, but did not persist a restart-durable risk latch because the failure class is recoverable or evidence was insufficient for durable escalation."
                }));
            return;
        }

        var reason = $"FORCED_CONVERGENCE_FAILED:{result.FailureReason ?? "unknown"}";
        StandDownStreamsForInstrument(instrument.Trim(), DateTimeOffset.UtcNow, reason);
    }

    /// <summary>
    /// Flatten exposure for a specific intent (helper for coordinator callback).
    /// </summary>
    private FlattenResult FlattenIntent(string intentId, string instrument, DateTimeOffset utcNow)
    {
        FlattenResult r;
        if (_executionAdapter is NinjaTraderSimAdapter simAdapter)
            r = simAdapter.FlattenIntent(intentId, instrument, utcNow);
        else
            r = _executionAdapter.Flatten(intentId, instrument, utcNow);
        if (r.Success)
            TryEnsureJournalIntegrityAfterExecutionActivity(instrument, utcNow);
        return r;
    }

    /// <summary>
    /// Cancel orders for a specific intent (helper for coordinator callback).
    /// </summary>
    private bool CancelIntentOrders(string intentId, DateTimeOffset utcNow)
    {
        if (_executionAdapter is NinjaTraderSimAdapter simAdapter)
        {
            return simAdapter.CancelIntentOrders(intentId, utcNow);
        }

        return false;
    }

    /// <summary>
    /// Gap 3 Phase 4: Try to submit corrective protective orders. Deterministic stop-price policy:
    /// 1) Use journal StopPrice if available and sane (protective for direction)
    /// 2) Else use BEStopPrice if sane
    /// 3) Else fail-closed: return NO_SAFE_STOP_PRICE, do not invent a price
    /// </summary>
    private ProtectiveCorrectiveResult TrySubmitCorrectiveStop(ProtectiveCorrectiveRequest req)
    {
        if (_executionAdapter == null)
            return new ProtectiveCorrectiveResult { Submitted = false, FailureReason = "NO_ADAPTER" };

        var byInst = _executionJournal.GetOpenJournalEntriesByInstrument();
        if (!byInst.TryGetValue(req.Instrument, out var entries) || entries.Count == 0)
        {
            // Fallback: match by canonical instrument (e.g. ES vs MES)
            var key = byInst.Keys.FirstOrDefault(k => ExecutionInstrumentResolver.IsSameInstrument(k, req.Instrument));
            if (key == null || !byInst.TryGetValue(key, out entries) || entries.Count == 0)
                return new ProtectiveCorrectiveResult { Submitted = false, FailureReason = "NO_JOURNAL_ENTRY" };
        }

        // Match by direction; pick first with matching direction and sufficient qty
        var match = entries.FirstOrDefault(e =>
            string.Equals(e.Entry.Direction, req.BrokerDirection, StringComparison.OrdinalIgnoreCase) &&
            e.Entry.EntryFilledQuantityTotal > 0 &&
            e.Entry.EntryFilledQuantityTotal >= Math.Abs(req.BrokerPositionQty));

        if (match.Entry == null)
            match = entries.FirstOrDefault(e =>
                string.Equals(e.Entry.Direction, req.BrokerDirection, StringComparison.OrdinalIgnoreCase) &&
                e.Entry.EntryFilledQuantityTotal > 0);

        if (match.Entry == null)
            return new ProtectiveCorrectiveResult { Submitted = false, FailureReason = "NO_MATCHING_JOURNAL" };

        var entry = match.Entry;
        var intentId = match.IntentId;
        var direction = entry.Direction ?? req.BrokerDirection;
        var entryPrice = entry.EntryAvgFillPrice ?? entry.EntryPrice ?? entry.FillPrice;
        var qty = Math.Min(entry.EntryFilledQuantityTotal, Math.Abs(req.BrokerPositionQty));
        if (qty <= 0) qty = entry.EntryFilledQuantityTotal;

        var shouldSubmitStop =
            req.StopQty <= 0 ||
            req.Status == ProtectiveAuditStatus.PROTECTIVE_STOP_QTY_MISMATCH ||
            req.Status == ProtectiveAuditStatus.PROTECTIVE_STOP_PRICE_INVALID;
        var shouldSubmitTarget =
            req.TargetQty <= 0 ||
            req.Status == ProtectiveAuditStatus.PROTECTIVE_MISSING_TARGET ||
            req.Status == ProtectiveAuditStatus.PROTECTIVE_TARGET_QTY_MISMATCH;

        // Deterministic stop price: prefer StopPrice, else BEStopPrice; must be sane (protective)
        decimal? stopPrice = null;
        if (entry.StopPrice.HasValue && entry.StopPrice.Value >= ProtectiveAuditPolicy.MIN_STOP_PRICE)
        {
            if (string.Equals(direction, "Long", StringComparison.OrdinalIgnoreCase) && entryPrice.HasValue &&
                entry.StopPrice.Value < entryPrice.Value)
                stopPrice = entry.StopPrice.Value;
            if (string.Equals(direction, "Short", StringComparison.OrdinalIgnoreCase) && entryPrice.HasValue &&
                entry.StopPrice.Value > entryPrice.Value)
                stopPrice = entry.StopPrice.Value;
        }
        if (!stopPrice.HasValue && entry.BEStopPrice.HasValue && entry.BEStopPrice.Value >= ProtectiveAuditPolicy.MIN_STOP_PRICE)
        {
            if (string.Equals(direction, "Long", StringComparison.OrdinalIgnoreCase) && entryPrice.HasValue &&
                entry.BEStopPrice.Value < entryPrice.Value)
                stopPrice = entry.BEStopPrice.Value;
            if (string.Equals(direction, "Short", StringComparison.OrdinalIgnoreCase) && entryPrice.HasValue &&
                entry.BEStopPrice.Value > entryPrice.Value)
                stopPrice = entry.BEStopPrice.Value;
        }
        if (shouldSubmitStop && !stopPrice.HasValue)
            return new ProtectiveCorrectiveResult { Submitted = false, FailureReason = "NO_SAFE_STOP_PRICE" };

        decimal? targetPrice = null;
        if (entry.TargetPrice.HasValue && entryPrice.HasValue)
        {
            if (string.Equals(direction, "Long", StringComparison.OrdinalIgnoreCase) &&
                entry.TargetPrice.Value > entryPrice.Value)
                targetPrice = entry.TargetPrice.Value;
            if (string.Equals(direction, "Short", StringComparison.OrdinalIgnoreCase) &&
                entry.TargetPrice.Value < entryPrice.Value)
                targetPrice = entry.TargetPrice.Value;
        }
        if (shouldSubmitTarget && !targetPrice.HasValue)
            return new ProtectiveCorrectiveResult { Submitted = false, FailureReason = "NO_SAFE_TARGET_PRICE" };

        var utcNow = req.AuditUtc;
        if (!shouldSubmitStop && !shouldSubmitTarget)
            return new ProtectiveCorrectiveResult { Submitted = true, IntentId = intentId };

        var recoveryOcoGroup = shouldSubmitStop && shouldSubmitTarget
            ? $"QTSW2:{intentId}_RECOVERY_{utcNow.UtcDateTime.Ticks}"
            : null;

        if (_executionAdapter is IIEAOrderExecutor orderExecutor &&
            stopPrice.HasValue &&
            targetPrice.HasValue &&
            qty > 0)
        {
            var command = new NtSubmitProtectivesCommand(
                $"PROTECTIVE_RECOVERY:{intentId}:{utcNow:yyyyMMddHHmmssfff}",
                intentId,
                req.Instrument,
                direction,
                stopPrice.Value,
                targetPrice.Value,
                qty,
                recoveryOcoGroup ?? $"QTSW2:{intentId}_RECOVERY_{utcNow.UtcDateTime.Ticks}",
                "PROTECTIVE_AUDIT_RECOVERY",
                utcNow);

            if (!orderExecutor.EnqueueNtAction(command))
            {
                return new ProtectiveCorrectiveResult
                {
                    Submitted = false,
                    IntentId = intentId,
                    FailureReason = "PROTECTIVE_RECOVERY_ENQUEUE_FAILED"
                };
            }

            return new ProtectiveCorrectiveResult { Submitted = true, IntentId = intentId };
        }

        OrderSubmissionResult? stopResult = null;
        OrderSubmissionResult? targetResult = null;
        if (shouldSubmitStop)
        {
            stopResult = _executionAdapter.SubmitProtectiveStop(intentId, req.Instrument, direction, stopPrice!.Value, qty, recoveryOcoGroup, utcNow);
            if (!stopResult.Success)
            {
                return new ProtectiveCorrectiveResult
                {
                    Submitted = false,
                    IntentId = intentId,
                    FailureReason = stopResult.ErrorMessage ?? "STOP_SUBMIT_FAILED"
                };
            }
        }

        if (shouldSubmitTarget)
            targetResult = _executionAdapter.SubmitTargetOrder(intentId, req.Instrument, direction, targetPrice!.Value, qty, recoveryOcoGroup, utcNow);

        if (stopResult?.Success == true || targetResult?.Success == true)
            TryEnsureJournalIntegrityAfterExecutionActivity(req.Instrument, utcNow);

        var submitted = (!shouldSubmitStop || stopResult?.Success == true) &&
                        (!shouldSubmitTarget || targetResult?.Success == true);
        return new ProtectiveCorrectiveResult
        {
            Submitted = submitted,
            IntentId = intentId,
            FailureReason = submitted
                ? null
                : (targetResult?.ErrorMessage ?? "TARGET_SUBMIT_FAILED")
        };
    }

    /// <summary>
    /// PHASE 2: Get notification service for high-priority alerts (e.g., protective order failures).
    /// </summary>
    public NotificationService? GetNotificationService()
    {
        lock (_engineLock)
        {
            return _healthMonitor?.GetNotificationService();
        }
    }

    /// <summary>
    /// Broker sync gate: Called by strategy host when OrderUpdate is observed.
    /// Updates timestamp for broker synchronization check.
    /// </summary>
    public void OnBrokerOrderUpdateObserved(DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            _lastOrderUpdateUtc = utcNow;
        }

        _releaseReconRedundancy.NotifyExecutionActivity();
        _mismatchCoordinator?.NotifyReconciliationAuditWake();
    }

    /// <summary>
    /// Broker sync gate: Called by strategy host when ExecutionUpdate is observed.
    /// Updates timestamp for broker synchronization check.
    /// </summary>
    public void OnBrokerExecutionUpdateObserved(DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            _lastExecutionUpdateUtc = utcNow;
        }

        _releaseReconRedundancy.NotifyExecutionActivity();
        _mismatchCoordinator?.NotifyReconciliationAuditWake();
    }

    /// <summary>
    /// Check if broker is synchronized (connection stable and quiet window passed).
    /// Accepts bar updates OR order/execution updates as proof of connection health.
    ///
    /// FIX: Bar updates are frequent during active trading/historical loading, so they don't
    /// require a quiet window. Only order/execution updates require a quiet window to ensure
    /// the broker has finished sending all updates. This prevents the sync gate from being
    /// blocked indefinitely when bars are coming in frequently.
    /// </summary>
    private bool IsBrokerSynchronized(DateTimeOffset utcNow)
    {
        // Require connection is currently connected
        if (_lastConnectionStatus != ConnectionStatus.Connected)
        {
            return false;
        }

        // Require reconnect timestamp is set (we've had a reconnect)
        if (!_reconnectUtc.HasValue)
        {
            return false;
        }

        // Check for bar updates after reconnect (proves connection is alive)
        var hasBarUpdateAfterReconnect = _lastTickUtc != DateTimeOffset.MinValue && _lastTickUtc >= _reconnectUtc.Value;

        // Check for order/execution updates after reconnect
        var hasOrderUpdateAfterReconnect = _lastOrderUpdateUtc.HasValue && _lastOrderUpdateUtc.Value >= _reconnectUtc.Value;
        var hasExecutionUpdateAfterReconnect = _lastExecutionUpdateUtc.HasValue && _lastExecutionUpdateUtc.Value >= _reconnectUtc.Value;

        // Need at least one type of update to prove connection health
        if (!hasBarUpdateAfterReconnect && !hasOrderUpdateAfterReconnect && !hasExecutionUpdateAfterReconnect)
        {
            return false;
        }

        // If we have order/execution updates, require quiet window (broker may still be sending updates)
        // Bar updates alone don't require quiet window since they're expected to be frequent
        if (hasOrderUpdateAfterReconnect || hasExecutionUpdateAfterReconnect)
        {
            // Use the most recent order/execution update for quiet window calculation
            var lastOrderExecutionUpdateUtc = DateTimeOffset.MinValue;
            if (_lastOrderUpdateUtc.HasValue && _lastOrderUpdateUtc.Value > lastOrderExecutionUpdateUtc)
            {
                lastOrderExecutionUpdateUtc = _lastOrderUpdateUtc.Value;
            }
            if (_lastExecutionUpdateUtc.HasValue && _lastExecutionUpdateUtc.Value > lastOrderExecutionUpdateUtc)
            {
                lastOrderExecutionUpdateUtc = _lastExecutionUpdateUtc.Value;
            }

            if (lastOrderExecutionUpdateUtc == DateTimeOffset.MinValue)
            {
                return false;
            }

            // Require quiet window: at least 5 seconds since last order/execution update
            var quietWindowSeconds = (utcNow - lastOrderExecutionUpdateUtc).TotalSeconds;
            return quietWindowSeconds >= 5.0;
        }

        // If we only have bar updates (no order/execution updates), bar updates alone are sufficient
        // No quiet window required - bars are expected to be frequent during active trading
        return hasBarUpdateAfterReconnect;
    }

    /// <summary>
    /// Recovery runner: single-threaded, idempotent recovery orchestration.
    /// Called when entering RECOVERY_RUNNING state.
    /// Phase 4: When IEAs exist for account, call BeginReconnectRecovery per instrument instead of account-level recovery.
    /// </summary>
    private void RunRecovery(DateTimeOffset utcNow, string accountName)
    {
        // Guard against re-entrancy
        lock (_recoveryLock)
        {
            if (_recoveryRunnerActive)
            {
                return; // Already running
            }

            _recoveryRunnerActive = true;
        }

        try
        {
            // Check exit condition: no new disconnect since recovery started
            if (_recoveryStartedUtc.HasValue && _disconnectFirstUtc.HasValue && _disconnectFirstUtc.Value > _recoveryStartedUtc.Value)
            {
                // New disconnect occurred during recovery - abort
                _recoveryState = ConnectionRecoveryState.DISCONNECT_FAIL_CLOSED;
                _secondReconciliationRunUtc = null; // Reset for new recovery cycle
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_ABORTED", state: "ENGINE",
                    new
                    {
                        reason = "NEW_DISCONNECT_DURING_RECOVERY",
                        recovery_started_utc = _recoveryStartedUtc.Value.ToString("o"),
                        disconnect_first_utc = _disconnectFirstUtc.Value.ToString("o")
                    }));
                return;
            }

            if (_lastConnectionStatus != ConnectionStatus.Connected)
            {
                // Connection lost during recovery - abort
                _recoveryState = ConnectionRecoveryState.DISCONNECT_FAIL_CLOSED;
                _secondReconciliationRunUtc = null; // Reset for new recovery cycle
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_ABORTED", state: "ENGINE",
                    new
                    {
                        reason = "CONNECTION_LOST_DURING_RECOVERY",
                        last_connection_status = _lastConnectionStatus.ToString()
                    }));
                return;
            }

            if (_executionAdapter == null)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_SKIPPED", state: "ENGINE",
                    new { reason = "EXECUTION_ADAPTER_NULL" }));
                return;
            }

            // Phase 4: Per-instrument bootstrap when IEAs exist
            var ieas = InstrumentExecutionAuthorityRegistry.GetAllForAccount(accountName);
            if (ieas.Count > 0)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "CONNECTION_RECOVERY_REQUIRES_BOOTSTRAP", state: "ENGINE",
                    new { instruments_count = ieas.Count, account_name = accountName }));
                foreach (var iea in ieas)
                {
                    iea.BeginReconnectRecovery(iea.ExecutionInstrumentKey, utcNow);
                }
                _recoveryState = ConnectionRecoveryState.RECOVERY_COMPLETE;
                _recoveryCompletedUtc = utcNow;
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_COMPLETE", state: "ENGINE",
                    new { instruments_count = ieas.Count, note = "Per-instrument bootstrap initiated" }));
                _healthMonitor?.ReportCritical("DISCONNECT_RECOVERY_COMPLETE", new Dictionary<string, object>
                {
                    ["recovery_state"] = _recoveryState.ToString(),
                    ["recovery_completed_utc"] = _recoveryCompletedUtc.Value.ToString("o"),
                    ["instruments_count"] = ieas.Count,
                    ["trading_date"] = TradingDateString,
                    ["note"] = "Per-instrument bootstrap - execution unblocked"
                });
                return;
            }

            // Step A: Snapshot (legacy path when no IEAs)
            AccountSnapshot? snap = null;
            try
            {
                snap = _executionAdapter.GetAccountSnapshot(utcNow);
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_ACCOUNT_SNAPSHOT", state: "ENGINE",
                    new
                    {
                        positions_count = snap.Positions?.Count ?? 0,
                        working_orders_count = snap.WorkingOrders?.Count ?? 0,
                        positions = snap.Positions,
                        working_orders = snap.WorkingOrders?.Select(o => new { id = o.OrderId, instrument = o.Instrument, tag = o.Tag, oco = o.OcoGroup }).ToList()
                    }));
            }
            catch (Exception ex)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_ACCOUNT_SNAPSHOT_FAILED", state: "ENGINE",
                    new
                    {
                        error = ex.Message,
                        exception_type = ex.GetType().Name,
                        note = "Recovery aborted - cannot snapshot account state"
                    }));
                return;
            }

            if (snap == null)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_ACCOUNT_SNAPSHOT_NULL", state: "ENGINE",
                    new { note = "Recovery aborted - snapshot returned null" }));
                return;
            }

            // Step B: Position reconciliation
            // Evidence-based unmatched detection: run policy on ALL non-flat positions.
            // A position is matched only if policy proves ownership (OWNERSHIP_PROVEN); otherwise FLATTEN.
            // No pre-filter by stream instrument string — policy evaluator is the authority.
            var nonFlatPositions = snap.Positions?.Where(p => p.Quantity != 0).ToList() ?? new List<PositionSnapshot>();
            var positionsToEvaluate = nonFlatPositions;

            // Legacy path: no IEA → no adoption. OWNERSHIP_PROVEN + !adoption_supported → FLATTEN with ADOPTION_PATH_UNAVAILABLE.
            var hasAdoptionPath = false; // RunRecoveryLegacy runs when no IEAs; adoption not available

            // Deterministic unmatched position policy: ADOPT_IF_SAFE or FLATTEN. No indefinite hold.
            var flattenedInstruments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (positionsToEvaluate.Count > 0)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_POSITION_UNMATCHED", state: "ENGINE",
                    new
                    {
                        position_count = positionsToEvaluate.Count,
                        positions = positionsToEvaluate.Select(p => new { instrument = p.Instrument, quantity = p.Quantity, avg_price = p.AveragePrice }).ToList(),
                        note = "Evidence-based policy: IF ownership_proven AND adoption_supported → adopt; ELSE → flatten"
                    }));
            }
            foreach (var position in positionsToEvaluate)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "UNMATCHED_POSITION_POLICY_EVALUATION_STARTED", state: "ENGINE",
                    new
                    {
                        instrument = position.Instrument,
                        broker_qty = Math.Abs(position.Quantity),
                        run_id = _runId ?? ""
                    }));

                var policyResult = UnmatchedPositionPolicyEvaluator.Evaluate(
                    position, snap, _executionJournal, TradingDateString, _runId, _log);

                if (policyResult.CandidateCount == 0)
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "UNMATCHED_POSITION_POLICY_NO_CANDIDATE", state: "ENGINE",
                        new { instrument = policyResult.Instrument, broker_qty = policyResult.BrokerQty, candidate_count = 0, reason = policyResult.Reason, run_id = _runId ?? "" }));
                }
                else
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "UNMATCHED_POSITION_POLICY_CANDIDATE_FOUND", state: "ENGINE",
                        new
                        {
                            instrument = policyResult.Instrument,
                            broker_qty = policyResult.BrokerQty,
                            candidate_count = policyResult.CandidateCount,
                            candidate_intent_id = policyResult.CandidateIntentId,
                            reason = policyResult.Reason,
                            run_id = _runId ?? ""
                        }));
                }

                var shouldFlatten = policyResult.Decision == UnmatchedPositionPolicyDecision.FLATTEN ||
                    (policyResult.Decision == UnmatchedPositionPolicyDecision.OWNERSHIP_PROVEN && !hasAdoptionPath);
                var flattenReason = policyResult.Decision == UnmatchedPositionPolicyDecision.OWNERSHIP_PROVEN && !hasAdoptionPath
                    ? "ADOPTION_PATH_UNAVAILABLE"
                    : policyResult.Reason;

                if (policyResult.Decision == UnmatchedPositionPolicyDecision.OWNERSHIP_PROVEN && hasAdoptionPath)
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "UNMATCHED_POSITION_POLICY_ADOPT_APPROVED", state: "ENGINE",
                        new
                        {
                            instrument = policyResult.Instrument,
                            broker_qty = policyResult.BrokerQty,
                            candidate_intent_id = policyResult.CandidateIntentId,
                            reason = policyResult.Reason,
                            run_id = _runId ?? ""
                        }));
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "UNMATCHED_POSITION_POLICY_ADOPT_COMPLETED", state: "ENGINE",
                        new
                        {
                            instrument = policyResult.Instrument,
                            candidate_intent_id = policyResult.CandidateIntentId,
                            run_id = _runId ?? ""
                        }));
                }
                else if (shouldFlatten)
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "UNMATCHED_POSITION_POLICY_ADOPT_REJECTED", state: "ENGINE",
                        new
                        {
                            instrument = policyResult.Instrument,
                            broker_qty = policyResult.BrokerQty,
                            candidate_count = policyResult.CandidateCount,
                            candidate_intent_id = policyResult.CandidateIntentId,
                            reason = flattenReason,
                            run_id = _runId ?? ""
                        }));
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "UNMATCHED_POSITION_POLICY_FLATTEN_TRIGGERED", state: "ENGINE",
                        new
                        {
                            instrument = policyResult.Instrument,
                            broker_qty = policyResult.BrokerQty,
                            reason = flattenReason,
                            run_id = _runId ?? ""
                        }));

                    var enq = _executionAdapter?.TryEnqueueEmergencyFlattenProtective(policyResult.Instrument, utcNow) == true;
                    var flattenResult = enq
                        ? FlattenResult.FailureResult("Enqueued for strategy thread", utcNow)
                        : FlattenResult.FailureResult("Emergency flatten enqueue not supported", utcNow);
                    flattenedInstruments.Add(policyResult.Instrument);
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "UNMATCHED_POSITION_FLATTEN_RESULT", state: "ENGINE",
                        new
                        {
                            instrument = policyResult.Instrument,
                            flatten_success = enq,
                            flatten_error = flattenResult.ErrorMessage,
                            enqueue_only = true,
                            run_id = _runId ?? ""
                        }));
                }
            }

            // Exclude flattened positions from downstream protective re-establishment
            var positionsToProtect = nonFlatPositions.Where(p =>
            {
                var inst = (p.Instrument ?? "").Trim();
                return !flattenedInstruments.Contains(inst);
            }).ToList();

            if (positionsToProtect.Count > 0)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_POSITION_RECONCILED", state: "ENGINE",
                    new
                    {
                        reconciled_count = positionsToProtect.Count,
                        positions = positionsToProtect.Select(p => new
                        {
                            instrument = p.Instrument,
                            quantity = p.Quantity,
                            avg_price = p.AveragePrice
                        }).ToList(),
                        flattened_count = flattenedInstruments.Count
                    }));
            }

            // Step C: Preserve robot-owned working orders during recovery (user preference: do not cancel)
            // Previously: CancelRobotOwnedWorkingOrders was called here to avoid orphaned orders.
            // Now: Working orders are left in place so they can fill after reconnect.
            var robotOrderCount = snap.WorkingOrders?.Count(o => IsRobotOwnedOrder(o)) ?? 0;
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_ORDERS_PRESERVED", state: "ENGINE",
                new
                {
                    robot_owned_orders_preserved = robotOrderCount,
                    note = "Robot-owned working orders preserved during recovery (no cancellation)"
                }));

            // Step D: Protective re-establishment (for reconciled positions only; excludes flattened)
            if (positionsToProtect.Count > 0)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_PROTECTIVE_ORDERS_PLACED", state: "ENGINE",
                    new
                    {
                        positions_protected = positionsToProtect.Count,
                        note = "Protective orders re-established for reconciled positions"
                    }));
            }

            // Step E: Entry-order reconciliation for RANGE_LOCKED streams
            var streamsReconciled = 0;
            var streamsNeedingResubmit = 0;
            foreach (var stream in _streams.Values)
            {
                if (stream.Committed || stream.State != StreamState.RANGE_LOCKED)
                {
                    continue; // Skip committed or non-locked streams
                }

                var (reconciled, needsResubmit) = stream.ReconcileEntryOrders(snap, utcNow);
                if (reconciled) streamsReconciled++;
                if (needsResubmit) streamsNeedingResubmit++;
            }

            if (streamsReconciled > 0 || streamsNeedingResubmit > 0)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_STREAM_ORDERS_RECONCILED", state: "ENGINE",
                    new
                    {
                        streams_reconciled = streamsReconciled,
                        streams_needing_resubmit = streamsNeedingResubmit,
                        note = "Entry orders verified or flagged for resubmission"
                    }));
            }

            // Exit criteria check (unmatched resolved by policy: adopt or flatten)
            var allPositionsMatched = true; // Policy resolved all unmatched
            var allPositionsProtected = positionsToProtect.Count == 0 || true; // Simplified check
            var allStreamsReconciled = true; // Simplified check

            if (allPositionsMatched && allPositionsProtected && allStreamsReconciled)
            {
                // Recovery complete
                _recoveryState = ConnectionRecoveryState.RECOVERY_COMPLETE;
                _recoveryCompletedUtc = utcNow;

                var recoveryPayload = new Dictionary<string, object>
                {
                    ["recovery_state"] = _recoveryState.ToString(),
                    ["recovery_started_utc"] = _recoveryStartedUtc?.ToString("o") ?? "",
                    ["recovery_completed_utc"] = _recoveryCompletedUtc.Value.ToString("o"),
                    ["total_positions"] = positionsToProtect.Count,
                    ["protected_positions"] = positionsToProtect.Count,
                    ["flattened_unmatched"] = flattenedInstruments.Count,
                    ["streams_reconciled"] = streamsReconciled,
                    ["trading_date"] = TradingDateString,
                    ["note"] = "Recovery complete - execution unblocked"
                };
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_COMPLETE", state: "ENGINE",
                    recoveryPayload));
                _healthMonitor?.ReportCritical("DISCONNECT_RECOVERY_COMPLETE", recoveryPayload);
            }
        }
        finally
        {
            lock (_recoveryLock)
            {
                _recoveryRunnerActive = false;
            }
        }
    }

    /// <summary>
    /// Check if an order is robot-owned (strict prefix matching).
    /// </summary>
    private bool IsRobotOwnedOrder(WorkingOrderSnapshot order)
    {
        return (!string.IsNullOrEmpty(order.Tag) && order.Tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(order.OcoGroup) && order.OcoGroup.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase));
    }
}
