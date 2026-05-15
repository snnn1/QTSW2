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

    /// <summary>
    /// Run reconciliation once on Realtime transition (NT context ready).
    /// Closes orphaned journals when broker position is flat.
    /// Skips when broker not connected (avoids meaningless RECONCILIATION_QTY_MISMATCH etc).
    /// </summary>
    public void RunReconciliationOnRealtimeStart()
    {
        if (IsShutdownRequested) return;
        if (!IsBrokerConnected) return;
        _reconciliationRunner?.RunOnRealtimeStart(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Run reconciliation periodically (throttled). Call from Tick.
    /// Skips reconciliation when broker not connected (avoids meaningless mismatch warnings).
    /// </summary>
    public void RunReconciliationPeriodicThrottle(DateTimeOffset utcNow)
    {
        if (TryRespectRunWideShutdownSignal(utcNow, "reconciliation_throttle"))
            return;
        if (IsTerminalShutdownLatched()) return;
        var rt = _runtimeAudit != null ? RuntimeAuditHub.CpuStart() : 0L;
        try
        {
            if (TryHandlePlaybackStallQuiescence(utcNow, requestStopIfEligible: false))
                return;
            RunPendingForceReconcile(utcNow);
            if (!IsBrokerConnected) return;
            _reconciliationRunner?.RunPeriodicThrottle(utcNow);
            RunLedgerReconciliationShadow(utcNow);
        }
        finally
        {
            if (rt != 0)
                _runtimeAudit?.CpuEnd(rt, RuntimeAuditSubsystem.ReconciliationThrottle);
        }
    }

    /// <summary>
    /// Phase 5a: Run the new classifier alongside the old runner, compare verdicts, log disagreements.
    /// No mutations. Gated behind <see cref="FeatureFlags.CanonicalOwnershipLedgerEnabled"/>.
    /// Phase 5b: If <see cref="FeatureFlags.ReconciliationRepairExecutorEnabled"/>, also execute repairs
    /// for non-stale verdicts with hard mismatch tier.
    /// </summary>
    private void RunLedgerReconciliationShadow(DateTimeOffset utcNow)
    {
        if (TryRespectRunWideShutdownSignal(utcNow, "ledger_reconciliation_shadow"))
            return;
        if (IsTerminalShutdownLatched()) return;
        if (_reconciliationClassifier == null || _ownershipLedger == null) return;
        if (!FeatureFlags.CanonicalOwnershipLedgerEnabled) return;

        try
        {
            var accountSnapshot = _executionAdapter.GetAccountSnapshot(utcNow);
            if (accountSnapshot == null) return;

            var accountName = OwnershipAccountKey;
            var instrumentsInScope = new HashSet<string>(GetEngineScopedExecutionInstrumentKeys(), StringComparer.OrdinalIgnoreCase);
            foreach (var s in _ownershipLedger.SnapshotAll(accountName))
            {
                if (!string.IsNullOrWhiteSpace(s.ExecutionInstrumentKey))
                    instrumentsInScope.Add(s.ExecutionInstrumentKey.Trim());
            }

            var verdicts = _reconciliationClassifier.Classify(accountSnapshot, accountName, instrumentsInScope.ToList(), utcNow);

            foreach (var v in verdicts)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                    eventType: "RECONCILIATION_CLASSIFIER_SHADOW_VERDICT", state: "ENGINE",
                    new
                    {
                        instrument = v.Instrument,
                        broker_qty = v.BrokerQty,
                        broker_signed_qty = v.BrokerSignedQty,
                        ledger_qty = v.LedgerQty,
                        journal_open_qty = v.JournalOpenQty,
                        mismatch_tier = v.MismatchTier.ToString(),
                        mismatch_age_ms = v.MismatchAgeMs,
                        is_stale = v.IsStale,
                        confidence = v.Confidence.ToString(),
                        ownership_version = v.OwnershipVersion,
                        active_slots = v.ActiveSlotCount,
                        orphan_slots = v.OrphanSlotCount,
                        sub_type = v.SubType.ToString()
                    }));

                if (v.MismatchTier == MismatchTier.HardMismatch)
                    _stateEmitter?.NotifyImmediateTrigger(SnapshotTrigger.ReconciliationVerdict);
            }

            var runnerQty = _lastRunnerQtyByInstrument;
            if (runnerQty != null)
            {
                foreach (var v in verdicts)
                {
                    var classifierMismatch = v.MismatchTier != MismatchTier.Convergence;
                    if (!runnerQty.TryGetValue(v.Instrument, out var rq))
                    {
                        if (classifierMismatch)
                        {
                            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                                eventType: "RECONCILIATION_CLASSIFIER_PARITY_DEFERRED", state: "ENGINE",
                                new
                                {
                                    instrument = v.Instrument,
                                    reason = "runner_quantity_missing",
                                    classifier_tier = v.MismatchTier.ToString(),
                                    classifier_broker_qty = v.BrokerQty,
                                    classifier_broker_signed_qty = v.BrokerSignedQty,
                                    classifier_ledger_qty = v.LedgerQty,
                                    classifier_says_mismatch = classifierMismatch
                                }));
                        }
                        continue;
                    }
                    var runnerMismatch = rq.AccountQty != rq.JournalQty;
                    var sameBrokerView = Math.Abs(v.BrokerQty) == Math.Abs(rq.AccountQty);
                    var canonicalLedgerMatchesBroker =
                        Math.Abs(v.LedgerQty) == Math.Abs(v.BrokerSignedQty) ||
                        Math.Abs(v.LedgerQty) == Math.Abs(v.BrokerQty) ||
                        Math.Abs(v.LedgerQty) == Math.Abs(rq.AccountQty);

                    if (classifierMismatch != runnerMismatch && !sameBrokerView)
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                            eventType: "RECONCILIATION_CLASSIFIER_PARITY_DEFERRED", state: "ENGINE",
                            new
                            {
                                instrument = v.Instrument,
                                reason = "runner_snapshot_account_qty_mismatch",
                                classifier_tier = v.MismatchTier.ToString(),
                                classifier_broker_qty = v.BrokerQty,
                                classifier_broker_signed_qty = v.BrokerSignedQty,
                                classifier_ledger_qty = v.LedgerQty,
                                runner_account_qty = rq.AccountQty,
                                runner_journal_qty = rq.JournalQty,
                                classifier_says_mismatch = classifierMismatch,
                                runner_says_mismatch = runnerMismatch,
                                note = "Deferred disagreement because classifier and runner sampled different broker/account quantities, usually at a fill or exit boundary."
                            }));
                        continue;
                    }

                    if (classifierMismatch != runnerMismatch &&
                        !classifierMismatch &&
                        sameBrokerView &&
                        canonicalLedgerMatchesBroker &&
                        Math.Abs(rq.JournalQty) != Math.Abs(v.LedgerQty))
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                            eventType: "RECONCILIATION_CLASSIFIER_PARITY_DEFERRED", state: "ENGINE",
                            new
                            {
                                instrument = v.Instrument,
                                reason = "legacy_runner_journal_lag_canonical_ledger_matches_broker",
                                classifier_tier = v.MismatchTier.ToString(),
                                classifier_broker_qty = v.BrokerQty,
                                classifier_broker_signed_qty = v.BrokerSignedQty,
                                classifier_ledger_qty = v.LedgerQty,
                                runner_account_qty = rq.AccountQty,
                                runner_journal_qty = rq.JournalQty,
                                classifier_says_mismatch = classifierMismatch,
                                runner_says_mismatch = runnerMismatch,
                                note = "Deferred disagreement because broker/account and canonical ledger agree while the legacy runner journal view is behind or represented differently."
                            }));
                        continue;
                    }

                    if (classifierMismatch != runnerMismatch)
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                            eventType: "RECONCILIATION_CLASSIFIER_DISAGREEMENT", state: "ENGINE",
                            new
                            {
                                instrument = v.Instrument,
                                classifier_tier = v.MismatchTier.ToString(),
                                classifier_broker_qty = v.BrokerQty,
                                classifier_broker_signed_qty = v.BrokerSignedQty,
                                classifier_ledger_qty = v.LedgerQty,
                                runner_account_qty = rq.AccountQty,
                                runner_journal_qty = rq.JournalQty,
                                classifier_says_mismatch = classifierMismatch,
                                runner_says_mismatch = runnerMismatch
                            }));
                    }
                }
            }

            // Phase 5b: Repair executor -- only if flag is on and classifier verdicts exist
            if (FeatureFlags.ReconciliationRepairExecutorEnabled && _reconciliationRepairExecutor != null)
            {
                var repairableVerdicts = verdicts
                    .Where(v => !v.IsStale && v.MismatchTier == MismatchTier.HardMismatch)
                    .ToList();

                if (repairableVerdicts.Count > 0)
                {
                    _reconciliationRepairExecutor.ExecuteRepairs(repairableVerdicts, utcNow);

                    foreach (var rv in repairableVerdicts)
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                            eventType: "RECONCILIATION_REPAIR_EXECUTOR_ACTION", state: "ENGINE",
                            new
                            {
                                instrument = rv.Instrument,
                                broker_qty = rv.BrokerQty,
                                broker_signed_qty = rv.BrokerSignedQty,
                                ledger_qty = rv.LedgerQty,
                                mismatch_tier = rv.MismatchTier.ToString()
                            }));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                eventType: "RECONCILIATION_CLASSIFIER_SHADOW_ERROR", state: "ENGINE",
                new { error = ex.Message }));
        }
    }

    /// <summary>
    /// IEA workload for a broker snapshot instrument: while &gt;0, mismatch/reconciliation must not treat journal as authoritative for that instrument.
    /// </summary>
    private int GetPendingIeAWorkloadForBrokerInstrument(string brokerInstrument)
    {
        if (_executionPolicy?.UseInstrumentExecutionAuthority != true) return 0;
        if (string.IsNullOrWhiteSpace(_accountName)) return 0;
        var trim = brokerInstrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(trim)) return 0;
        if (!ReconciliationIeaLookup.TryResolve(_accountName, trim, 0, GetExecutionInstrument, out var iea) || iea == null)
            return 0;
        return iea.PendingExecutionWorkloadCount;
    }

    /// <summary>
    /// Strategy-layer diagnostics use this to avoid treating fill/protective work still queued in the IEA as unowned exposure.
    /// </summary>
    public int GetPendingIeaWorkloadForInstrument(string brokerInstrument)
    {
        return GetPendingIeAWorkloadForBrokerInstrument(brokerInstrument);
    }

    private void LogAndDrainCallbackIngressBeforeReconciliation(DateTimeOffset utcNow, DateTimeOffset nowWall)
    {
        if (_executionAdapter is not NinjaTraderSimAdapter adapter) return;
        adapter.GetTotalCallbackIngressQueueLengths(out var execBefore, out var ordBefore);
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "EXECUTION_QUEUE_DRAIN_START", state: "ENGINE",
            new
            {
                instrument = "*",
                pending_execution_count = execBefore,
                run_id = _runId ?? "",
                ts_utc = nowWall.ToString("o"),
                order_ingress_queued = ordBefore,
                note = "Strategy-thread drain of non-IEA callback ingress (bounded); IEA mode uses per-instrument IEA queue gating"
            }));
        adapter.DrainCallbackIngress(nowWall);
        adapter.GetTotalCallbackIngressQueueLengths(out var execAfter, out var ordAfter);
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "EXECUTION_QUEUE_DRAIN_END", state: "ENGINE",
            new
            {
                instrument = "*",
                pending_execution_count = execAfter,
                run_id = _runId ?? "",
                ts_utc = DateTimeOffset.UtcNow.ToString("o"),
                order_ingress_queued = ordAfter,
                note = "After bounded drain; remaining items may be processed on a later tick"
            }));
    }

    private const string PENDING_FORCE_RECONCILE_FILE = "pending_force_reconcile.json";

    /// <summary>
    /// Check for pending force-reconcile trigger file under the active run root (<c>ops/pending_force_reconcile.json</c>).
    /// When operator confirms account is correct, create that file with {"instruments": ["MYM", "MCL"]} to force-close orphan journals.
    /// </summary>
    private void RunPendingForceReconcile(DateTimeOffset utcNow)
    {
        var opsDir = Path.Combine(_persistenceBase, "ops");
        try { Directory.CreateDirectory(opsDir); } catch { /* best-effort */ }
        var path = Path.Combine(opsDir, PENDING_FORCE_RECONCILE_FILE);
        if (!File.Exists(path))
            return;

        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonUtil.Deserialize<ForceReconcileTrigger>(json);
            if (parsed?.instruments == null || parsed.instruments.Count == 0)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FORCE_RECONCILE_SKIPPED", state: "ENGINE",
                    new { reason = "INVALID_FORMAT", path, note = "Expected {\"instruments\": [\"MYM\", ...]}" }));
                File.Delete(path);
                return;
            }

            var totalClosed = 0;
            foreach (var inst in parsed.instruments.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var instTrimmed = inst!.Trim();
                var closed = _executionJournal.ForceReconcileOrphanJournalsForInstrument(instTrimmed, utcNow);
                totalClosed += closed;
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "FORCE_RECONCILE_EXECUTED", state: "ENGINE",
                    new { instrument = instTrimmed, journals_closed = closed }));
            }

            File.Delete(path);
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FORCE_RECONCILE_COMPLETE", state: "ENGINE",
                new { total_journals_closed = totalClosed, instruments = parsed.instruments }));
        }
        catch (Exception ex)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FORCE_RECONCILE_ERROR", state: "ENGINE",
                new { path, error = ex.Message }));
        }
    }

    /// <summary>
    /// Reconciliation runner reports broker vs journal qty mismatch — log derived position-authority state only.
    /// Does not freeze, stand down, or request recovery; execution remains gated by structural parity and position authority.
    /// </summary>
    private void LogReconciliationQuantityMismatchDiagnostics(string instrument, DateTimeOffset utcNow, string reason, int accountQty,
        int journalQty)
    {
        var inst = (instrument ?? "").Trim();
        if (string.IsNullOrEmpty(inst)) return;
        var canonicalForJournal = GetCanonicalInstrument(inst) ?? inst;
        var (realOpen, recoveryOpen, _) = _executionJournal.GetPositionAuthorityOpenQuantitiesForInstrument(inst, canonicalForJournal);
        var authority = PositionAuthorityDerivation.DerivePositionAuthority(accountQty, realOpen, recoveryOpen);
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "",
            eventType: "RECONCILIATION_QTY_STATE", state: "ENGINE",
            new
            {
                instrument = inst,
                broker_qty = accountQty,
                journal_qty_sum = journalQty,
                real_open_qty = realOpen,
                recovery_open_qty = recoveryOpen,
                authority_state = authority.ToString(),
                context = reason,
                note = "State sample only — not an execution decision"
            }));
        ReconciliationStateTracker.Shared.NotifyMismatchHandlingDispatched(_accountName, inst, accountQty, journalQty, utcNow);
    }

    /// <summary>
    /// RiskGate gate −1b: true execution lock — instrument freeze latch + IEA supervisory (no protective coordinator global freeze).
    /// </summary>
    private bool IsInstrumentEpaExecutionLocked(string instrument)
    {
        return GetInstrumentEpaExecutionLockReason(instrument) != null;
    }

    /// <summary>
    /// EPA adapter submit gate −1b: execution lock plus protective coordinator for submits other than <c>SUBMIT_PROTECTIVE_STOP</c>.
    /// </summary>
    private bool IsInstrumentEpaAdapterSubmitBlocked(string instrument, string? submitPath)
    {
        return GetInstrumentEpaAdapterSubmitBlockReason(instrument, submitPath) != null;
    }

    private string? GetRiskLatchReasonForInstrument(string instrument)
    {
        var inst = (instrument ?? "").Trim();
        if (string.IsNullOrEmpty(inst) || _riskLatchManager == null)
            return null;
        try
        {
            var entry = _riskLatchManager.HydrateEntries()
                .FirstOrDefault(x => string.Equals(x.Instrument?.Trim(), inst, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(entry?.Reason) ? null : entry!.Reason.Trim();
        }
        catch
        {
            return null;
        }
    }

    private string? GetInstrumentEpaExecutionLockReason(string instrument)
    {
        var inst = (instrument ?? "").Trim();
        if (string.IsNullOrEmpty(inst)) return null;
        if (_frozenInstruments.Contains(inst))
        {
            var latchReason = GetRiskLatchReasonForInstrument(inst);
            return string.IsNullOrWhiteSpace(latchReason)
                ? "ENGINE_INSTRUMENT_FROZEN"
                : "DURABLE_RISK_LATCH_ACTIVE:" + latchReason;
        }

        var account = _accountName ?? "";
        foreach (var iea in InstrumentExecutionAuthorityRegistry.GetAllForAccount(account))
        {
            if (ExecutionInstrumentResolver.IsSameInstrument(iea.ExecutionInstrumentKey, inst) && iea.IsSupervisorilyBlocked)
                return "IEA_SUPERVISORY_BLOCK:" + iea.CurrentSupervisoryState;
        }
        return null;
    }

    private string? GetInstrumentEpaAdapterSubmitBlockReason(string instrument, string? submitPath)
    {
        if (!ExecutionPermissionAuthority.ShouldApplyEntryRiskBlockToSubmitPath(submitPath))
            return null;

        var lockReason = GetInstrumentEpaExecutionLockReason(instrument);
        if (!string.IsNullOrWhiteSpace(lockReason)) return lockReason;
        var protectiveReason = _protectiveCoordinator?.GetBlockReason(instrument);
        if (!string.IsNullOrWhiteSpace(protectiveReason))
            return "PROTECTIVE_COVERAGE_BLOCK:" + protectiveReason.Trim();
        if (_protectiveCoordinator != null && _protectiveCoordinator.IsInstrumentBlockedByProtective(instrument))
            return "PROTECTIVE_COVERAGE_BLOCK";
        return null;
    }

    /// <summary>
    /// Phase 5: Mismatch coordinator block, or frozen / IEA supervisory — used by RiskGate (protective is path-scoped in <see cref="IsInstrumentEpaAdapterSubmitBlocked"/>).
    /// </summary>
    private bool IsInstrumentFrozenOrSupervisorilyBlocked(string instrument)
    {
        if (_mismatchCoordinator != null && _mismatchCoordinator.IsInstrumentBlockedByMismatch(instrument)) return true;
        return IsInstrumentEpaExecutionLocked(instrument);
    }

    private bool HasInstrumentSupervisoryBlock(string instrument)
    {
        var inst = (instrument ?? "").Trim();
        if (string.IsNullOrEmpty(inst)) return false;
        var account = _accountName ?? "";
        foreach (var iea in InstrumentExecutionAuthorityRegistry.GetAllForAccount(account))
        {
            if (ExecutionInstrumentResolver.IsSameInstrument(iea.ExecutionInstrumentKey, inst) && iea.IsSupervisorilyBlocked)
                return true;
        }
        return false;
    }

    internal static bool ShouldBypassInterruptedLateSessionCloseReentryBlock(
        bool hasInterruptedSessionCloseStream,
        bool hasSupervisoryBlock,
        int brokerPositionQty,
        int brokerWorkingCount)
    {
        return hasInterruptedSessionCloseStream &&
               !hasSupervisoryBlock &&
               brokerPositionQty == 0 &&
               brokerWorkingCount == 0;
    }

    private bool TryBypassInterruptedLateSessionCloseReentryBlock(string instrument)
    {
        var inst = (instrument ?? "").Trim();
        if (string.IsNullOrEmpty(inst) || _executionAdapter == null)
            return false;
        if (!HasInterruptedSessionCloseStreamForInstrument(inst))
            return false;

        var hasSupervisoryBlock = HasInstrumentSupervisoryBlock(inst);
        if (hasSupervisoryBlock)
            return false;

        AccountSnapshot snapshot;
        try
        {
            snapshot = _executionAdapter.GetAccountSnapshot(DateTimeOffset.UtcNow);
        }
        catch
        {
            return false;
        }

        var brokerPositionQty = SumBrokerPositionQty(snapshot, inst);
        var brokerWorkingCount = CountBrokerWorkingOrders(snapshot, inst);
        if (!ShouldBypassInterruptedLateSessionCloseReentryBlock(true, hasSupervisoryBlock, brokerPositionQty, brokerWorkingCount))
            return false;

        var auditUtc = DateTimeOffset.UtcNow;
        var canonical = GetCanonicalInstrument(inst) ?? inst;
        var journalQty = _executionJournal.GetOpenJournalStructuralStateForInstrument(inst, canonical).OpenQtySum;
        var (realOpenQty, recoveryOpenQty, _) =
            _executionJournal.GetPositionAuthorityOpenQuantitiesForInstrument(inst, canonical);
        var latchReason = GetRiskLatchReasonForInstrument(inst);
        var authorityLatchReason = string.IsNullOrWhiteSpace(latchReason)
            ? "FORCED_CONVERGENCE_FAILED:IN_MEMORY_SESSION_REENTRY_BLOCK"
            : latchReason;
        var hasProtectiveBlock =
            !string.IsNullOrWhiteSpace(_protectiveCoordinator?.GetBlockReason(inst)) ||
            (_protectiveCoordinator?.IsInstrumentBlockedByProtective(inst) ?? false);
        var hasMismatchBlock = _mismatchCoordinator?.IsInstrumentBlockedByMismatch(inst) == true;
        var latchClearFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "RobotEngine.InterruptedLateSessionCloseReentryBypass",
            Account = _accountName ?? "",
            Instrument = inst,
            CanonicalInstrument = canonical,
            TradingDate = TradingDateString,
            SubmitPath = "LATCH_CLEAR",
            ExecutionMode = _executionMode.ToString(),
            DecisionUtc = auditUtc,
            FrameCreatedUtc = auditUtc,
            BrokerSnapshotCapturedUtc = snapshot.CapturedAtUtc,
            BrokerPositionQty = brokerPositionQty,
            BrokerWorkingOrdersCount = brokerWorkingCount,
            JournalOpenQty = journalQty,
            RealOpenQty = realOpenQty,
            RecoveryOpenQty = recoveryOpenQty,
            DurableLatchActive = !string.IsNullOrWhiteSpace(latchReason),
            DurableLatchReason = authorityLatchReason,
            MismatchBlockActive = hasMismatchBlock,
            IeaSupervisoryBlock = hasSupervisoryBlock,
            ProtectiveCoverageState = hasProtectiveBlock ? "BLOCKED" : "",
            AuthorityState = "LATCH_CLEAR",
            IsPlayback = _isolatedPlaybackPersistence || _playbackAccountDetected,
            IsMultiDayScenario = _playbackScenarioActive && (_playbackScenario?.dates?.Count ?? 0) > 1,
            PlaybackScenarioId = _playbackScenario?.scenario_id ?? ""
        });
        var latchClearAuthority = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.LatchClear,
            Source = "RobotEngine.InterruptedLateSessionCloseReentryBypass",
            Instrument = inst,
            UtcNow = auditUtc,
            DurableLatchReason = authorityLatchReason,
            AccountQty = brokerPositionQty,
            BrokerAbsQty = Math.Abs(brokerPositionQty),
            BrokerWorkingOrderCount = brokerWorkingCount,
            JournalOpenQty = journalQty,
            RealOpenQty = realOpenQty,
            RecoveryOpenQty = recoveryOpenQty,
            HasSupervisoryBlock = hasSupervisoryBlock,
            HasProtectiveBlock = hasProtectiveBlock,
            HasMismatchBlock = hasMismatchBlock,
            AuthorityFrame = latchClearFrame
        });
        LogEvent(RobotEvents.EngineBase(auditUtc, tradingDate: TradingDateString, eventType: "AUTHORITY_FRAME_SNAPSHOT", state: "ENGINE",
            ExecutionAuthorityFrameBuilder.ToLogPayload(
                latchClearFrame,
                action: "LATCH_CLEAR",
                decision: latchClearAuthority.Allowed ? "ALLOW" : "DENY",
                denyReason: latchClearAuthority.DenyReason)));
        if (!latchClearAuthority.Allowed)
        {
            LogEvent(RobotEvents.EngineBase(auditUtc, tradingDate: TradingDateString,
                eventType: "SESSION_REENTRY_BLOCK_BYPASS_DENIED_BY_AUTHORITY", state: "ENGINE",
                new
                {
                    instrument = inst,
                    authority_gate = latchClearAuthority.GateName,
                    deny_reason = latchClearAuthority.DenyReason,
                    detail = latchClearAuthority.Detail,
                    broker_position_qty = brokerPositionQty,
                    broker_working_count = brokerWorkingCount,
                    journal_open_qty = journalQty,
                    real_open_qty = realOpenQty,
                    recovery_open_qty = recoveryOpenQty,
                    latch_reason = authorityLatchReason
                }));
            return false;
        }

        lock (_engineLock)
        {
            _frozenInstruments.Remove(inst);
            _riskLatchManager?.Clear(inst);
        }

        _reconciliationRunner?.ForceRunGateRecoveryForInstrument(auditUtc, inst);
        LogEvent(RobotEvents.EngineBase(auditUtc, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "",
            eventType: "SESSION_REENTRY_BLOCK_BYPASS", state: "ENGINE",
            new
            {
                instrument = inst,
                broker_position_qty = brokerPositionQty,
                broker_working_count = brokerWorkingCount,
                note = "Late session-close reentry bypassed stale freeze/reconciliation block after broker-flat confirmation."
            }));
        return true;
    }

    /// <summary>
    /// IEA alignment: Check if instrument is blocked for reentry (protective, mismatch, frozen, supervisory, queue poison).
    /// </summary>
    private bool IsInstrumentBlockedForReentry(string instrument)
    {
        if (TryBypassInterruptedLateSessionCloseReentryBlock(instrument)) return false;
        if (IsInstrumentFrozenOrSupervisorilyBlocked(instrument)) return true;
        var account = _accountName ?? "";
        foreach (var iea in InstrumentExecutionAuthorityRegistry.GetAllForAccount(account))
        {
            if (ExecutionInstrumentResolver.IsSameInstrument(iea.ExecutionInstrumentKey, instrument) && iea.IsInstrumentBlocked)
                return true;
        }
        return false;
    }

    /// <summary>
    /// IEA alignment: Check if IEA queue is healthy for instrument (not blocked by timeout/overflow).
    /// Used to decide queued flatten vs emergency flatten fallback.
    /// </summary>
    private bool IsIeaQueueHealthyForInstrument(string instrument)
    {
        var account = _accountName ?? "";
        if (ReconciliationIeaLookup.TryResolve(account, instrument, 0, GetExecutionInstrument, out var iea) && iea != null)
            return !iea.IsInstrumentBlocked;
        return true;
    }

    /// <summary>
    /// Execution policy for <see cref="MismatchType.STRUCTURAL_MULTI_INTENT"/> (also used for stand-down on block).
    /// Delegates side effects to <see cref="StructuralMultiIntentPolicyRuntime.Invoke"/> — see harness test STRUCTURAL_AUTO_OFFSET.
    /// For stand-down-only: freeze instrument and stand down streams that have positions; others continue range-build; RiskGate blocks execution.
    /// </summary>
    private void ApplyStructuralMultiIntentPolicy(string instrument, DateTimeOffset utcNow)
    {
        var p = _executionPolicy?.StructuralMultiIntentPolicy ?? StructuralMultiIntentPolicy.Allow;
        var kind = StructuralMultiIntentPolicyRuntime.Invoke(
            p,
            instrument,
            utcNow,
            StandDownStreamsForInstrument,
            (inst, u) => _reconciliationRunner?.ForceRunGateRecoveryForInstrument(u, inst));
        switch (kind)
        {
            case StructuralMultiIntentPolicyActionKind.AllowObservation:
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "STRUCTURAL_MULTI_INTENT_POLICY", state: "ENGINE",
                    new
                    {
                        instrument,
                        policy = "allow",
                        note = "Structural multi-intent still drives complexity — observation-only until policy is tightened"
                    }));
                break;
            case StructuralMultiIntentPolicyActionKind.BlockNewEntries:
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "STRUCTURAL_MULTI_INTENT_POLICY", state: "ENGINE",
                    new { instrument, policy = "block_new_entries" }));
                break;
            case StructuralMultiIntentPolicyActionKind.GateRecoveryRequested:
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "STRUCTURAL_MULTI_INTENT_POLICY", state: "ENGINE",
                    new
                    {
                        instrument,
                        policy = "auto_offset_request",
                        note = "Gate recovery requested toward broker truth"
                    }));
                break;
        }
    }

    private void StandDownStreamsForInstrument(string instrument, DateTimeOffset utcNow, string reason)
    {
        lock (_engineLock)
        {
            _frozenInstruments.Add(instrument);
            var latchCreateAuthority = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
            {
                Action = ExecutionAuthorityAction.LatchCreate,
                Source = "RobotEngine.StandDownStreamsForInstrument",
                Instrument = instrument,
                DurableLatchReason = reason,
                UtcNow = utcNow
            });
            if (latchCreateAuthority.Allowed)
            {
                _riskLatchManager?.Persist(instrument, reason);
            }
            else
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                    eventType: "RISK_LATCH_CREATE_BLOCKED", state: "ENGINE",
                    new
                    {
                        instrument,
                        reason,
                        authority_gate = latchCreateAuthority.GateName,
                        deny_reason = latchCreateAuthority.DenyReason,
                        note = latchCreateAuthority.Detail
                    }));
            }

            var tradingDateStr = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
            foreach (var kvp in _streams)
            {
                var stream = kvp.Value;
                var streamInst = stream.Instrument ?? "";
                var streamExec = stream.ExecutionInstrument ?? "";
                if (!string.Equals(streamInst, instrument, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(streamExec, instrument, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Only stand down streams with entry fills (have positions). Others continue to range-build; execution blocked by RiskGate.
                var hasPosition = _executionJournal.HasEntryFillForStream(tradingDateStr, stream.Stream);
                if (hasPosition)
                {
                    stream.EnterRecoveryManage(utcNow, reason);
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr,
                        eventType: "STREAM_STAND_DOWN", state: "ENGINE",
                        new { stream_id = kvp.Key, reason = reason, instrument }));
                }
                else
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr,
                        eventType: "STREAM_FROZEN_NO_STAND_DOWN", state: "ENGINE",
                        new { stream_id = kvp.Key, reason = reason, instrument, note = "No position; range building continues; execution blocked" }));
                }
            }
        }
    }

    /// <summary>
    /// Explicit operator/engine unfreeze — not driven by reconciliation or parity convergence.
    /// Clears engine freeze + risk latch only when <see cref="NinjaTraderSimAdapter.TryValidateExplicitUnfreezeConditions"/> passes (REAL authority, structural clear, overlay clear, fresh snapshot).
    /// </summary>
    private void MaybeEmitRiskLatchAutoClearSkipped(
        string instrument,
        RiskLatchManager.RiskLatchEntry latch,
        int accountQty,
        int journalQty,
        int brokerPositionQty,
        int brokerWorkingCount,
        int realOpenQty,
        int recoveryOpenQty,
        bool hasSupervisoryBlock,
        bool hasProtectiveBlock,
        bool hasMismatchBlock,
        DateTimeOffset utcNow)
    {
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return;

        var reasonAllowsAutoClear = RiskLatchManager.IsAutoClearEligibleReason(latch.Reason);
        var failedPredicates = new List<string>();
        if (!reasonAllowsAutoClear) failedPredicates.Add("reason_auto_clear_eligible");
        if (accountQty != 0) failedPredicates.Add("account_qty_flat");
        if (journalQty != 0) failedPredicates.Add("journal_qty_flat");
        if (brokerPositionQty != 0) failedPredicates.Add("broker_position_flat");
        if (brokerWorkingCount != 0) failedPredicates.Add("broker_working_orders_flat");
        if (realOpenQty != 0) failedPredicates.Add("real_open_qty_flat");
        if (recoveryOpenQty != 0) failedPredicates.Add("recovery_open_qty_flat");
        if (hasSupervisoryBlock) failedPredicates.Add("iea_supervisory_block_clear");
        if (hasProtectiveBlock) failedPredicates.Add("protective_block_clear");
        if (hasMismatchBlock) failedPredicates.Add("mismatch_block_clear");
        var clearAllowed = failedPredicates.Count == 0;
        var throttleKey = inst + "|" + (latch.Reason ?? "");
        lock (_engineLock)
        {
            if (_riskLatchAutoClearSkippedThrottle.TryGetValue(throttleKey, out var last) &&
                (utcNow - last).TotalSeconds < RiskLatchAutoClearSkippedThrottleSeconds)
                return;
            _riskLatchAutoClearSkippedThrottle[throttleKey] = utcNow;
        }

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
            eventType: "RISK_LATCH_AUTO_CLEAR_SKIPPED", state: "ENGINE",
            new
            {
                account = _accountName ?? "",
                instrument = inst,
                latch_reason = latch.Reason,
                reason_allows_auto_clear = reasonAllowsAutoClear,
                clear_allowed = clearAllowed,
                failed_predicates = failedPredicates.ToArray(),
                account_qty = accountQty,
                account_qty_flat = accountQty == 0,
                broker_qty = brokerPositionQty,
                journal_qty = journalQty,
                journal_qty_flat = journalQty == 0,
                broker_position_qty = brokerPositionQty,
                broker_position_flat = brokerPositionQty == 0,
                working_orders = brokerWorkingCount,
                broker_working_count = brokerWorkingCount,
                broker_working_flat = brokerWorkingCount == 0,
                ownership_qty = realOpenQty + recoveryOpenQty,
                real_open_qty = realOpenQty,
                real_open_flat = realOpenQty == 0,
                recovery_open_qty = recoveryOpenQty,
                recovery_open_flat = recoveryOpenQty == 0,
                iea_supervisory_block = hasSupervisoryBlock,
                has_supervisory_block = hasSupervisoryBlock,
                protective_block = hasProtectiveBlock,
                mismatch_block = hasMismatchBlock,
                note = "Durable risk latch remains active because at least one clean-flat auto-clear predicate is not satisfied."
            }));
    }

    private void TryAutoClearResolvedFlatRiskLatch(string instrument, int accountQty, int journalQty, DateTimeOffset utcNow)
    {
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst) || _riskLatchManager == null || _executionAdapter == null)
            return;

        lock (_engineLock)
        {
            if (!_frozenInstruments.Contains(inst))
                return;
        }

        RiskLatchManager.RiskLatchEntry? latch = null;
        try
        {
            latch = _riskLatchManager.HydrateEntries()
                .FirstOrDefault(x => string.Equals(x.Instrument?.Trim(), inst, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return;
        }
        if (latch == null)
            return;

        AccountSnapshot snapshot;
        try
        {
            snapshot = _executionAdapter.GetAccountSnapshot(utcNow);
        }
        catch
        {
            return;
        }

        var brokerPositionQty = SumBrokerPositionQty(snapshot, inst);
        var brokerWorkingCount = CountBrokerWorkingOrders(snapshot, inst);
        var canonical = GetCanonicalInstrument(inst) ?? inst;
        var (realOpenQty, recoveryOpenQty, _) =
            _executionJournal.GetPositionAuthorityOpenQuantitiesForInstrument(inst, canonical);
        var hasSupervisoryBlock = HasInstrumentSupervisoryBlock(inst);
        var hasProtectiveBlock =
            !string.IsNullOrWhiteSpace(_protectiveCoordinator?.GetBlockReason(inst)) ||
            (_protectiveCoordinator?.IsInstrumentBlockedByProtective(inst) ?? false);
        var hasMismatchBlock = _mismatchCoordinator?.IsInstrumentBlockedByMismatch(inst) == true;

        var legacyLatchClearAllowed = RiskLatchManager.ShouldAutoClearResolvedFlatLatch(
            latch.Reason,
            accountQty,
            journalQty,
            brokerPositionQty,
            brokerWorkingCount,
            realOpenQty,
            recoveryOpenQty,
            hasSupervisoryBlock,
            hasProtectiveBlock,
            hasMismatchBlock);
        var latchClearFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "RobotEngine.RiskLatchClear",
            Account = _accountName ?? "",
            Instrument = inst,
            CanonicalInstrument = canonical,
            TradingDate = TradingDateString,
            SubmitPath = "LATCH_CLEAR",
            ExecutionMode = _executionMode.ToString(),
            DecisionUtc = utcNow,
            FrameCreatedUtc = utcNow,
            BrokerSnapshotCapturedUtc = snapshot.CapturedAtUtc,
            BrokerPositionQty = brokerPositionQty,
            BrokerWorkingOrdersCount = brokerWorkingCount,
            JournalOpenQty = journalQty,
            RealOpenQty = realOpenQty,
            RecoveryOpenQty = recoveryOpenQty,
            DurableLatchActive = true,
            DurableLatchReason = latch.Reason ?? "",
            MismatchBlockActive = hasMismatchBlock,
            IeaSupervisoryBlock = hasSupervisoryBlock,
            ProtectiveCoverageState = hasProtectiveBlock ? "BLOCKED" : "",
            AuthorityState = "LATCH_CLEAR",
            IsPlayback = _isolatedPlaybackPersistence || _playbackAccountDetected,
            IsMultiDayScenario = _playbackScenarioActive && (_playbackScenario?.dates?.Count ?? 0) > 1,
            PlaybackScenarioId = _playbackScenario?.scenario_id ?? ""
        });
        var latchClearAuthority = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.LatchClear,
            Source = "RobotEngine.RiskLatchClear",
            Instrument = inst,
            UtcNow = utcNow,
            DurableLatchReason = latch.Reason ?? "",
            AccountQty = accountQty,
            BrokerAbsQty = Math.Abs(brokerPositionQty),
            BrokerWorkingOrderCount = brokerWorkingCount,
            JournalOpenQty = journalQty,
            RealOpenQty = realOpenQty,
            RecoveryOpenQty = recoveryOpenQty,
            HasSupervisoryBlock = hasSupervisoryBlock,
            HasProtectiveBlock = hasProtectiveBlock,
            HasMismatchBlock = hasMismatchBlock,
            AuthorityFrame = latchClearFrame
        });
        var latchClearAllowed = latchClearAuthority.Allowed && legacyLatchClearAllowed;
        var latchClearDenyReason = latchClearAuthority.DenyReason ??
                                   (legacyLatchClearAllowed ? null : "LEGACY_LATCH_CLEAR_VALIDATOR_DENIED");
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "AUTHORITY_FRAME_SNAPSHOT", state: "ENGINE",
            ExecutionAuthorityFrameBuilder.ToLogPayload(
                latchClearFrame,
                action: "LATCH_CLEAR",
                decision: latchClearAllowed ? "ALLOW" : "DENY",
                denyReason: latchClearDenyReason)));
        if (!latchClearAllowed)
        {
            MaybeEmitRiskLatchAutoClearSkipped(inst, latch, accountQty, journalQty, brokerPositionQty, brokerWorkingCount,
                realOpenQty, recoveryOpenQty, hasSupervisoryBlock, hasProtectiveBlock, hasMismatchBlock, utcNow);
            return;
        }

        lock (_engineLock)
        {
            _frozenInstruments.Remove(inst);
            _riskLatchManager.Clear(inst);
        }

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
            eventType: "RISK_LATCH_AUTO_CLEARED_RESOLVED_FLAT", state: "ENGINE",
            new
            {
                instrument = inst,
                latch_reason = latch.Reason,
                account_qty = accountQty,
                journal_qty = journalQty,
                broker_position_qty = brokerPositionQty,
                broker_working_count = brokerWorkingCount,
                real_open_qty = realOpenQty,
                recovery_open_qty = recoveryOpenQty,
                note = "Durable forced-convergence latch cleared after broker, working-order, and journal authority all proved clean flat."
            }));
    }

    private void TryAutoClearResolvedFlatRiskLatchesAbsentFromReconciliationPass(
        IReadOnlyDictionary<string, (int AccountQty, int JournalQty)> qtyByInstrument,
        DateTimeOffset utcNow)
    {
        if (_riskLatchManager == null || _executionAdapter == null)
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (qtyByInstrument != null)
        {
            foreach (var inst in qtyByInstrument.Keys)
            {
                if (!string.IsNullOrWhiteSpace(inst))
                    seen.Add(inst.Trim());
            }
        }

        string[] missingFrozen;
        lock (_engineLock)
        {
            missingFrozen = _frozenInstruments
                .Where(inst => !string.IsNullOrWhiteSpace(inst) && !seen.Contains(inst.Trim()))
                .Select(inst => inst.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        foreach (var inst in missingFrozen)
        {
            // A fully resolved flat instrument can vanish from both broker-position and open-journal maps.
            // It still needs one clean-flat evaluation so durable forced-convergence latches do not persist forever.
            TryAutoClearResolvedFlatRiskLatch(inst, accountQty: 0, journalQty: 0, utcNow);
        }
    }

    public bool TryUnfreezeInstrument(string instrument, DateTimeOffset utcNow, out string reason)
    {
        reason = "";
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst))
        {
            reason = "missing_instrument";
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, "INSTRUMENT_UNFREEZE_DENIED", "ENGINE",
                new { instrument = inst, reason }));
            return false;
        }

        if (_executionAdapter is NinjaTraderSimAdapter sim)
        {
            if (!sim.TryValidateExplicitUnfreezeConditions(inst, utcNow, out reason))
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, "INSTRUMENT_UNFREEZE_DENIED", "ENGINE",
                    new { instrument = inst, reason }));
                return false;
            }
        }
        else
        {
            reason = "explicit_unfreeze_requires_sim_adapter";
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, "INSTRUMENT_UNFREEZE_DENIED", "ENGINE",
                new { instrument = inst, reason }));
            return false;
        }

        AccountSnapshot snapshot;
        try
        {
            snapshot = _executionAdapter.GetAccountSnapshot(utcNow);
        }
        catch
        {
            reason = "explicit_unfreeze_snapshot_failed";
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, "INSTRUMENT_UNFREEZE_DENIED", "ENGINE",
                new { instrument = inst, reason }));
            return false;
        }

        var brokerPositionQty = SumBrokerPositionQty(snapshot, inst);
        var brokerWorkingCount = CountBrokerWorkingOrders(snapshot, inst);
        var canonical = GetCanonicalInstrument(inst) ?? inst;
        var journalQty = _executionJournal.GetOpenJournalStructuralStateForInstrument(inst, canonical).OpenQtySum;
        var (realOpenQty, recoveryOpenQty, _) =
            _executionJournal.GetPositionAuthorityOpenQuantitiesForInstrument(inst, canonical);
        var latchReason = GetRiskLatchReasonForInstrument(inst) ?? "EXPLICIT_OPERATOR_UNFREEZE";
        var hasSupervisoryBlock = HasInstrumentSupervisoryBlock(inst);
        var hasProtectiveBlock =
            !string.IsNullOrWhiteSpace(_protectiveCoordinator?.GetBlockReason(inst)) ||
            (_protectiveCoordinator?.IsInstrumentBlockedByProtective(inst) ?? false);
        var hasMismatchBlock = _mismatchCoordinator?.IsInstrumentBlockedByMismatch(inst) == true;
        var latchClearFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "RobotEngine.ExplicitUnfreeze",
            Account = _accountName ?? "",
            Instrument = inst,
            CanonicalInstrument = canonical,
            TradingDate = TradingDateString,
            SubmitPath = "LATCH_CLEAR_EXPLICIT_OPERATOR",
            ExecutionMode = _executionMode.ToString(),
            DecisionUtc = utcNow,
            FrameCreatedUtc = utcNow,
            BrokerSnapshotCapturedUtc = snapshot.CapturedAtUtc,
            BrokerPositionQty = brokerPositionQty,
            BrokerWorkingOrdersCount = brokerWorkingCount,
            JournalOpenQty = journalQty,
            RealOpenQty = realOpenQty,
            RecoveryOpenQty = recoveryOpenQty,
            DurableLatchActive = !string.IsNullOrWhiteSpace(GetRiskLatchReasonForInstrument(inst)),
            DurableLatchReason = latchReason,
            MismatchBlockActive = hasMismatchBlock,
            IeaSupervisoryBlock = hasSupervisoryBlock,
            ProtectiveCoverageState = hasProtectiveBlock ? "BLOCKED" : "",
            AuthorityState = "LATCH_CLEAR_EXPLICIT_OPERATOR",
            IsPlayback = _isolatedPlaybackPersistence || _playbackAccountDetected,
            IsMultiDayScenario = _playbackScenarioActive && (_playbackScenario?.dates?.Count ?? 0) > 1,
            PlaybackScenarioId = _playbackScenario?.scenario_id ?? ""
        });
        var latchClearAuthority = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.LatchClearExplicitOperator,
            Source = "RobotEngine.ExplicitUnfreeze",
            Instrument = inst,
            UtcNow = utcNow,
            DurableLatchReason = latchReason,
            AccountQty = brokerPositionQty,
            BrokerAbsQty = Math.Abs(brokerPositionQty),
            BrokerWorkingOrderCount = brokerWorkingCount,
            JournalOpenQty = journalQty,
            RealOpenQty = realOpenQty,
            RecoveryOpenQty = recoveryOpenQty,
            HasSupervisoryBlock = hasSupervisoryBlock,
            HasProtectiveBlock = hasProtectiveBlock,
            HasMismatchBlock = hasMismatchBlock,
            AuthorityFrame = latchClearFrame
        });
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "AUTHORITY_FRAME_SNAPSHOT", state: "ENGINE",
            ExecutionAuthorityFrameBuilder.ToLogPayload(
                latchClearFrame,
                action: "LATCH_CLEAR_EXPLICIT_OPERATOR",
                decision: latchClearAuthority.Allowed ? "ALLOW" : "DENY",
                denyReason: latchClearAuthority.DenyReason)));
        if (!latchClearAuthority.Allowed)
        {
            reason = latchClearAuthority.DenyReason ?? "explicit_unfreeze_authority_denied";
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, "INSTRUMENT_UNFREEZE_DENIED", "ENGINE",
                new
                {
                    instrument = inst,
                    reason,
                    authority_gate = latchClearAuthority.GateName,
                    authority_detail = latchClearAuthority.Detail,
                    broker_position_qty = brokerPositionQty,
                    broker_working_count = brokerWorkingCount,
                    journal_open_qty = journalQty,
                    real_open_qty = realOpenQty,
                    recovery_open_qty = recoveryOpenQty,
                    latch_reason = latchReason
                }));
            return false;
        }

        lock (_engineLock)
        {
            var wasFrozen = _frozenInstruments.Contains(inst);
            _frozenInstruments.Remove(inst);
            _riskLatchManager?.Clear(inst);
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, "INSTRUMENT_UNFROZEN_EXPLICIT", "ENGINE",
                new { instrument = inst, was_frozen = wasFrozen, note = "Explicit unfreeze path" }));
        }

        reason = "";
        return true;
    }

}
