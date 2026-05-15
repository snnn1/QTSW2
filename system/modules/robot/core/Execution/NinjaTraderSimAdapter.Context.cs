using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class NinjaTraderSimAdapter
{
    private static string NormalizeMarketDataInstrumentKey(string? instrument)
    {
        var trimmed = (instrument ?? "").Trim();
        if (trimmed.Length == 0) return "";
        return trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? trimmed;
    }

    public void RecordLatestMarketDataLast(string instrument, decimal price, DateTimeOffset utcNow)
    {
        if (price <= 0) return;
        var key = NormalizeMarketDataInstrumentKey(instrument);
        if (key.Length == 0) return;
        _latestMarketDataLastByInstrument[key] = new LatestMarketDataLast { Price = price, Utc = utcNow };
    }

    private bool TryGetLatestMarketDataLast(string instrument, DateTimeOffset utcNow, out decimal price, out DateTimeOffset lastUtc)
    {
        price = 0m;
        lastUtc = default;
        var key = NormalizeMarketDataInstrumentKey(instrument);
        if (key.Length == 0) return false;
        if (!_latestMarketDataLastByInstrument.TryGetValue(key, out var latest)) return false;
        if (latest.Price <= 0) return false;
        var age = DateTimeOffset.UtcNow - latest.Utc;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age > LatestMarketDataLastFreshWindow) return false;
        price = latest.Price;
        lastUtc = latest.Utc;
        return true;
    }

    /// <inheritdoc />
    public bool IsExecutionContextReady => _simAccountVerified && _ntContextSet;
    
    // PHASE 2: Callback to stand down stream on protective order failure
    private Action<string, DateTimeOffset, string>? _standDownStreamCallback;

    /// <summary>P2 Phase 1: true when state-consistency / mismatch gate blocks the instrument (prevents aggregation sibling cancel).</summary>
    private Func<string, bool>? _instrumentMismatchGateEngaged;

    /// <summary>P2 Phase 1: host stands down implicated streams after stream-scoped containment decision.</summary>
    private Action<StateOwnershipAttributionResult, DateTimeOffset>? _p2StreamContainmentEngineCallback;

    /// <summary>P2 Phase 1: set mismatch gate probe (invoked with NT instrument name or execution key).</summary>
    public void SetInstrumentMismatchGateEngagedCallback(Func<string, bool>? callback) =>
        _instrumentMismatchGateEngaged = callback;

    /// <summary>P2 Phase 1: engine callback after IEA chooses stream containment (no instrument flatten).</summary>
    public void SetP2StreamContainmentEngineCallback(Action<StateOwnershipAttributionResult, DateTimeOffset>? callback) =>
        _p2StreamContainmentEngineCallback = callback;

    /// <summary>Optional: notify mismatch gate coordinator of structured execution activity.</summary>
    private Action<string, DateTimeOffset, MismatchExecutionTriggerDetails>? _onMismatchExecutionTrigger;

    /// <summary>Transient bridge so mismatch assembly does not see broker-ahead while journal disk read lags fills.</summary>
    private PendingFillBridge? _pendingFillBridge;

    /// <summary>In-memory pending fill observations for reconciliation only (not a position ledger).</summary>
    public void SetPendingFillBridge(PendingFillBridge? bridge) => _pendingFillBridge = bridge;

    /// <summary>Canonical ownership ledger (P1). Receives writes alongside existing stores when enabled via feature flag.</summary>
    private InstrumentOwnershipLedger? _ownershipLedger;

    /// <summary>Wires the canonical ownership ledger for dual-run write path.</summary>
    public void SetOwnershipLedger(InstrumentOwnershipLedger? ledger) => _ownershipLedger = ledger;

    /// <summary>Durable orphan fill journal (P4). Records untracked/unknown fills for post-hoc attribution.</summary>
    private OrphanFillJournal? _orphanFillJournal;

    /// <summary>Wires the orphan fill journal.</summary>
    public void SetOrphanFillJournal(OrphanFillJournal? journal) => _orphanFillJournal = journal;

    /// <summary>Unified execution authority (P4a). Shadow-eval or activation mode via feature flags.</summary>
    private UnifiedExecutionAuthority? _unifiedAuthority;

    /// <summary>Wires the unified execution authority for shadow/active evaluation.</summary>
    public void SetUnifiedExecutionAuthority(UnifiedExecutionAuthority? uea) => _unifiedAuthority = uea;

    private ExecutionMode _authorityExecutionMode = ExecutionMode.DRYRUN;
    private bool _authorityIsPlayback;
    private bool _authorityIsMultiDayScenario;
    private string _authorityPlaybackScenarioId = "";

    /// <summary>Read-only context for authority frame snapshots.</summary>
    public void SetAuthorityRuntimeContext(
        ExecutionMode executionMode,
        bool isPlayback,
        bool isMultiDayScenario,
        string? playbackScenarioId)
    {
        _authorityExecutionMode = executionMode;
        _authorityIsPlayback = isPlayback;
        _authorityIsMultiDayScenario = isMultiDayScenario;
        _authorityPlaybackScenarioId = string.IsNullOrWhiteSpace(playbackScenarioId)
            ? ""
            : playbackScenarioId.Trim();
    }

    /// <summary>Wires execution/fill activity to <see cref="MismatchEscalationCoordinator.NotifyExecutionTrigger"/> (optional).</summary>
    public void SetMismatchExecutionTriggerCallback(Action<string, DateTimeOffset, MismatchExecutionTriggerDetails>? callback) =>
        _onMismatchExecutionTrigger = callback;
    
    // PHASE 2: Callback to get notification service for alerts
    private Func<object?>? _getNotificationServiceCallback;
    
    // PHASE 2: Callback to check if execution is allowed (recovery state guard)
    private Func<bool>? _isExecutionAllowedCallback;
    private Func<bool>? _isPlaybackStallNtCallBlockedCallback;

    /// <summary>Optional: per-instrument journal integrity / reconciliation repair in progress (execution safety gate).</summary>
    private Func<string, bool>? _journalIntegrityRepairActiveForInstrumentCallback;

    /// <summary>
    /// Integration/harness tests only: when non-null, <see cref="BuildExecutionSafetyEvaluationRequest"/> uses this factory
    /// instead of <see cref="GetAccountSnapshot"/> (avoids NT context). Set to null to restore production behavior.
    /// </summary>
    private Func<DateTimeOffset, AccountSnapshot?>? _executionSafetyTestGetAccountSnapshot;

    /// <summary>RiskGate gate 1: global kill switch â€” adapter order submits must mirror stream path.</summary>
    private Func<bool>? _isGlobalKillSwitchActive;

    /// <summary>G1: RiskGate gate âˆ’1a â€” EPA/engine mismatch execution block.</summary>
    private Func<string, bool>? _isMismatchExecutionBlocked;
    private Func<string, string?, bool>? _isMismatchExecutionBlockedForSubmit;

    /// <summary>RiskGate gate âˆ’1b: execution lock + path-aware policy (instrument, submit_path) â€” excludes mismatch authority.</summary>
    private Func<string, string?, bool>? _isInstrumentFrozenOrEpaBlocked;
    private Func<string, string?, string?>? _getInstrumentFrozenOrEpaBlockReason;

    /// <summary>Authoritative engine session day (<see cref="RobotEngine.TradingDateString"/>). When null, session-identity gate is skipped (harness/tests).</summary>
    private Func<string?>? _getActiveTradingDateString;

    /// <summary>Global (adapter-wide) latch â€” session mismatch is systemic; one flag blocks all instruments until restart.</summary>
    private int _sessionMismatchBlocked;

    /// <summary>Incremented once when SESSION_IDENTITY_MISMATCH_BLOCKED is emitted (test/diagnostics).</summary>
    private int _sessionIdentityMismatchCriticalEmitCount;

    /// <summary>Monotonic count of session-identity rejections (mismatch + post-latch fast-fail). For metrics / alerting.</summary>
    private long _sessionIdentityBlockCount;

    /// <summary>Dedup CRITICAL_UNSAFE_STATE_DETECTED per (instrument, reason) for the process.</summary>
    private readonly ConcurrentDictionary<string, string> _criticalUnsafeStateEmittedOnce = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gap 5: Callback when IEA EnqueueAndWait fails (timeout/overflow). Engine blocks instrument and stands down streams.</summary>
    private Action<string, DateTimeOffset, string>? _blockInstrumentCallback;

    /// <summary>Gap 5: Canonical event writer. Set by engine before SetNTContext.</summary>
    private ExecutionEventWriter? _eventWriter;

    /// <summary>Gap 5: Set canonical event writer for replay. Call before SetNTContext when using IEA.</summary>
    public void SetEventWriter(ExecutionEventWriter? writer) => _eventWriter = writer;

    /// <summary>Cross-chart flatten owner id (align with reconciliation writer / engine instance). When unset, uses nta:&lt;adapter#&gt;.</summary>
    private string? _flattenCoordinationInstanceIdOverride;

    /// <summary>Set process-stable instance id for flatten coordination (e.g. engine reconciliation writer id).</summary>
    public void SetFlattenCoordinationInstanceId(string? instanceId) =>
        _flattenCoordinationInstanceIdOverride = string.IsNullOrWhiteSpace(instanceId) ? null : instanceId.Trim();

    /// <summary>Optional: execution root â†’ canonical (e.g. MESâ†’ES) for journal open-qty aggregation in adapter guard paths.</summary>
    private Func<string, string?>? _getCanonicalInstrumentForJournalAggregation;

    /// <summary>Wires spec-backed canonical resolution so journal rows keyed under ES count when execution is MES.</summary>
    public void SetCanonicalInstrumentForJournalAggregation(Func<string, string?>? resolver) =>
        _getCanonicalInstrumentForJournalAggregation = resolver;

    /// <summary>Second arg to <see cref="ExecutionJournal.GetOpenJournalQuantitySumForInstrument"/>; null falls back to execution-only.</summary>
    private string? CanonicalInstrumentForOpenJournalSum(string executionInstrumentRoot)
    {
        try
        {
            return _getCanonicalInstrumentForJournalAggregation?.Invoke(executionInstrumentRoot);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Journal open qty for logical NT instrument / execution key, with canonical-aware bucket matching (MES vs ES).</summary>
    private int SumOpenJournalForInstrument(string logicalInstrument, string executionInstrumentKeyFallback)
    {
        var root = BrokerPositionResolver.NormalizeCanonicalKey(logicalInstrument);
        if (string.IsNullOrEmpty(root))
            root = BrokerPositionResolver.NormalizeCanonicalKey(executionInstrumentKeyFallback);
        var canon = CanonicalInstrumentForOpenJournalSum(root);
        return _executionJournal.GetOpenJournalQuantitySumForInstrument(root, canon);
    }

    private string GetFlattenCoordinationInstanceId() =>
        !string.IsNullOrEmpty(_flattenCoordinationInstanceIdOverride)
            ? _flattenCoordinationInstanceIdOverride!
            : "nta:" + _adapterInstanceId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Stable executor id for investigation logs (matches flatten coordination prefix).</summary>
    public int InvestigationAdapterInstanceId => _adapterInstanceId;

    /// <summary>Set strategy instance id for RUNTIME_FINGERPRINT / EXECUTOR_REBOUND correlation. Call before SetNTContext.</summary>
    public void SetInvestigationRuntimeContext(string? strategyInstanceId) =>
        _strategyInstanceIdForAudit = string.IsNullOrWhiteSpace(strategyInstanceId) ? null : strategyInstanceId.Trim();

    private static string TryComputeRobotCoreAssemblyFingerprint()
    {
        try
        {
            var a = typeof(NinjaTraderSimAdapter).Assembly;
            var loc = a.Location;
            if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
            {
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(loc);
                var h = sha.ComputeHash(fs);
                var hex = BitConverter.ToString(h).Replace("-", "");
                return hex.Length >= 24 ? hex.Substring(0, 24).ToLowerInvariant() : hex.ToLowerInvariant();
            }
            return a.FullName ?? "unknown_no_location";
        }
        catch
        {
            return "assembly_hash_error";
        }
    }

    private void TryEmitInvestigationRuntimeFingerprint(string accountName, string executionInstrumentKey)
    {
        if (_investigationRuntimeFingerprintEmitted) return;
        _investigationRuntimeFingerprintEmitted = true;
        var utc = DateTimeOffset.UtcNow;
        string procStart;
        try
        {
            procStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }
        catch
        {
            procStart = "";
        }
        int? ieaId = _iea?.InstanceId;
        _log.Write(RobotEvents.EngineBase(utc, tradingDate: "", eventType: "RUNTIME_FINGERPRINT", state: "ENGINE",
            new
            {
                assembly_hash = TryComputeRobotCoreAssemblyFingerprint(),
                process_start_time = procStart,
                strategy_instance_id = _strategyInstanceIdForAudit ?? "",
                executor_id = "nta:" + _adapterInstanceId.ToString(CultureInfo.InvariantCulture),
                iea_instance_id = ieaId,
                account = accountName ?? "",
                instrument = executionInstrumentKey ?? ""
            }));
    }

    private string GetCoordinationAccountName()
    {
        if (!string.IsNullOrEmpty(_iea?.AccountName)) return _iea.AccountName;
        if (!string.IsNullOrEmpty(_ieaAccountName)) return _ieaAccountName;
        try
        {
            if (_ntAccount != null)
            {
                dynamic acc = _ntAccount;
                string? n = acc.Name;
                if (!string.IsNullOrEmpty(n)) return n;
            }
        }
        catch { }
        return "UNKNOWN";
    }

    private string GetLedgerAccountName()
    {
        var account = GetCoordinationAccountName();
        return string.IsNullOrWhiteSpace(account) || string.Equals(account, "UNKNOWN", StringComparison.OrdinalIgnoreCase)
            ? "default"
            : account.Trim();
    }

    private string GetAuditTradingDate(DateTimeOffset utcNow)
    {
        try
        {
            var getActive = _getActiveTradingDateString;
            var tradingDate = getActive?.Invoke();
            if (!string.IsNullOrWhiteSpace(tradingDate))
                return tradingDate.Trim();
        }
        catch { }

        return utcNow.ToString("yyyy-MM-dd");
    }

    private string GetHostChartInstrumentName()
    {
        try
        {
            if (_ntInstrument == null) return "";
            dynamic inst = _ntInstrument;
            return inst.FullName ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Enqueue gate: one active flatten owner per (account, canonical_broker_key).</summary>
    private bool TryCoordinationGateFlattenEnqueue(NtFlattenInstrumentCommand cmd, out string episodeIdFromGate)
    {
        episodeIdFromGate = "";
        var canonical = BrokerPositionResolver.NormalizeCanonicalKey(cmd.Instrument);
        if (string.IsNullOrEmpty(canonical))
            return true;

        var utcNow = DateTimeOffset.UtcNow;
        var account = GetCoordinationAccountName();
        var instId = GetFlattenCoordinationInstanceId();
        var hostChart = GetHostChartInstrumentName();

        var gate = FlattenCoordinationTracker.Shared.TryRequestFlattenEnqueue(
            account,
            cmd.Instrument,
            instId,
            hostChart,
            cmd.CorrelationId,
            utcNow,
            cmd.IsVerifyRetryFlatten,
            out var episodeId,
            out var prevOwner,
            out var emitOwnerAssigned,
            out var emitPersistentStillOpen,
            out var staleElapsed);

        episodeIdFromGate = episodeId;
        var m = FlattenCoordinationTracker.Shared.Metrics;

        switch (gate)
        {
            case FlattenEnqueueGateOutcome.SecondaryInstanceSkip:
                FlattenCoordinationTracker.Shared.TryPeekKey(account, cmd.Instrument, out var skipOwner, out var skipEpi, out _);
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_SECONDARY_INSTANCE_SKIPPED", state: "ENGINE",
                    new
                    {
                        account,
                        canonical_broker_key = canonical,
                        owner_instance_id = skipOwner,
                        current_instance_id = instId,
                        episode_id = skipEpi,
                        host_chart_instrument = hostChart,
                        correlation_id = cmd.CorrelationId,
                        reason = cmd.IsVerifyRetryFlatten ? "verify_retry_non_owner_or_no_episode" : "flatten_enqueue_non_owner_active",
                        metrics_flatten_secondary_skipped_total = m.FlattenSecondarySkippedTotal,
                        critical_flatten = IsCriticalFlattenCommand(cmd)
                    }));
                if (IsCriticalFlattenCommand(cmd))
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_COORDINATION_SKIP_ESCALATION", state: "CRITICAL",
                        new
                        {
                            account,
                            canonical_broker_key = canonical,
                            correlation_id = cmd.CorrelationId,
                            instrument = cmd.Instrument,
                            note = "Non-owner skipped critical flatten enqueue â€” escalating block"
                        }));
                    _blockInstrumentCallback?.Invoke(cmd.Instrument ?? "", utcNow, "FLATTEN_COORDINATION_NON_OWNER_CRITICAL");
                    ScheduleCriticalFlattenCoordinationRetryIfNeeded(cmd, "flatten_enqueue_non_owner");
                }
                return false;

            case FlattenEnqueueGateOutcome.FailedPersistentBlocked:
                FlattenCoordinationTracker.Shared.TryPeekKey(account, cmd.Instrument, out var fpOwner, out var fpEpi, out _);
                if (emitPersistentStillOpen)
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_FAILED_PERSISTENT_STILL_OPEN", state: "CRITICAL",
                        new
                        {
                            account,
                            canonical_broker_key = canonical,
                            owner_instance_id = fpOwner,
                            current_instance_id = instId,
                            episode_id = fpEpi,
                            host_chart_instrument = hostChart,
                            correlation_id = cmd.CorrelationId,
                            reason = "persistent_failure_episode_active_coordination_blocked",
                            metrics_flatten_failed_persistent_still_open_total = m.FlattenFailedPersistentStillOpenTotal
                        }));
                }
                return false;

            case FlattenEnqueueGateOutcome.TakeoverProceed:
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_OWNER_TAKEOVER", state: "ENGINE",
                    new
                    {
                        account,
                        canonical_broker_key = canonical,
                        previous_owner_instance_id = prevOwner,
                        new_owner_instance_id = instId,
                        elapsed_since_last_update_sec = staleElapsed >= 0 ? (long?)staleElapsed : null,
                        host_chart_instrument = hostChart,
                        correlation_id = cmd.CorrelationId,
                        episode_id = episodeId,
                        metrics_flatten_owner_takeover_total = m.FlattenOwnerTakeoverTotal
                    }));
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_COORDINATION_OWNER_ASSIGNED", state: "ENGINE",
                    new
                    {
                        account,
                        canonical_broker_key = canonical,
                        owner_instance_id = instId,
                        current_instance_id = instId,
                        episode_id = episodeId,
                        host_chart_instrument = hostChart,
                        correlation_id = cmd.CorrelationId,
                        reason = "after_stale_owner_takeover",
                        metrics_flatten_owner_assigned_total = m.FlattenOwnerAssignedTotal
                    }));
                return true;

            case FlattenEnqueueGateOutcome.Proceed:
                if (emitOwnerAssigned)
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_COORDINATION_OWNER_ASSIGNED", state: "ENGINE",
                        new
                        {
                            account,
                            canonical_broker_key = canonical,
                            owner_instance_id = instId,
                            current_instance_id = instId,
                            episode_id = episodeId,
                            host_chart_instrument = hostChart,
                            correlation_id = cmd.CorrelationId,
                            metrics_flatten_owner_assigned_total = m.FlattenOwnerAssignedTotal
                        }));
                }
                return true;

            default:
                return true;
        }
    }

}
