// Gap 4 + P1.5: Persistent mismatch escalation + state-consistency gate (closed-loop).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Periodic mismatch audits + P1.5 instrument-scoped state-consistency gate:
/// engage → gate-safe reconciliation → release readiness → stability window → release.
/// </summary>
public sealed class MismatchEscalationCoordinator
{
    private readonly Func<AccountSnapshot> _getSnapshot;
    private readonly Func<IReadOnlyList<string>> _getActiveInstruments;
    private readonly Func<AccountSnapshot, DateTimeOffset, IReadOnlyList<MismatchObservation>> _getMismatchObservations;
    private readonly Func<string, bool> _isInstrumentBlocked;
    private readonly Func<string, bool> _isFlattenInProgress;
    private readonly Func<string, bool> _isRecoveryInProgress;
    private readonly RobotLogger? _log;
    private readonly ExecutionEventWriter? _eventWriter;
    private readonly RuntimeAuditHub? _runtimeAudit;
    private readonly Func<string, DateTimeOffset, int, GateReconciliationResult?>? _runInstrumentGateReconciliation;
    private readonly Func<string, AccountSnapshot, DateTimeOffset, StateConsistencyReleaseReadinessResult>? _evaluateReleaseReadiness;
    private readonly Func<ReconciliationForcedConvergenceContext, ReconciliationForcedConvergenceResult>? _runForcedBrokerAlignment;
    private readonly Action<string, ReconciliationForcedConvergenceContext, ReconciliationForcedConvergenceResult>?
        _onForcedConvergenceFailure;
    private readonly Func<long>? _getExecutionActivityGeneration;
    private readonly object _auditRunLock = new();
    private long _scheduleFingerprintPrev = long.MinValue;
    private long _scheduleActivityGenPrev = long.MinValue;
    private readonly int _stableWindowMs;
    private readonly Timer _auditTimer;
    private DateTimeOffset _lastAuditUtc = DateTimeOffset.MinValue;
    private ulong _nextConvergenceEpisodeId;

    private readonly ConcurrentDictionary<string, MismatchInstrumentState> _stateByInstrument = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Coalesce identical gate telemetry when expensive reconciliation is skipped (same signatures + skip reason).</summary>
    private readonly Dictionary<string, (int Hash, DateTimeOffset Utc)> _quietGateTelemetry = new(StringComparer.OrdinalIgnoreCase);

    private const int QuietGateTelemetrySeconds = 30;

    /// <summary>Test harness: counts for gate RESULT emission vs quiet suppression.</summary>
    internal int TestGateResultEmitCount { get; private set; }

    internal int TestGateResultSuppressCount { get; private set; }

    /// <summary>Rate-limit identical post-flat release-blocker payloads per instrument.</summary>
    private readonly Dictionary<string, (int Hash, DateTimeOffset Utc)> _releaseBlockedAfterFlatTelemetry =
        new(StringComparer.OrdinalIgnoreCase);

    private const int ReleaseBlockedAfterFlatQuietSeconds = 30;

    private int _mismatchDetectedCount;
    private int _mismatchPersistentCount;
    private int _mismatchFailClosedCount;
    private int _mismatchClearedCount;
    private int _mismatchBrokerAheadCount;
    private int _mismatchJournalAheadCount;
    private int _mismatchPositionQtyCount;
    private int _mismatchRegistryMissingCount;
    private int _mismatchProtectiveDivergenceCount;

    private readonly record struct GateReconciliationResultTelemetry(
        bool ExpensiveInvoked,
        bool ThrottleSuppressedExpensive,
        int NoProgressIterations,
        double? TimeSinceLastProgressMs,
        bool WarmupDone,
        string? NextAllowedExpensiveUtc,
        int ProgressSignatureHash,
        bool ExternalFingerprintChanged,
        string? SkipReason,
        int ExecutionCycleCount,
        bool HardStopActive,
        int TotalExpensiveSinceGateEngaged);

    public MismatchEscalationCoordinator(
        Func<AccountSnapshot> getSnapshot,
        Func<IReadOnlyList<string>> getActiveInstruments,
        Func<AccountSnapshot, DateTimeOffset, IReadOnlyList<MismatchObservation>> getMismatchObservations,
        Func<string, bool> isInstrumentBlocked,
        Func<string, bool> isFlattenInProgress,
        Func<string, bool> isRecoveryInProgress,
        RobotLogger? log,
        ExecutionEventWriter? eventWriter = null,
        Func<string, DateTimeOffset, int, GateReconciliationResult?>? runInstrumentGateReconciliation = null,
        Func<string, AccountSnapshot, DateTimeOffset, StateConsistencyReleaseReadinessResult>? evaluateReleaseReadiness = null,
        int stateConsistencyStableWindowMs = 0,
        RuntimeAuditHub? runtimeAudit = null,
        Func<ReconciliationForcedConvergenceContext, ReconciliationForcedConvergenceResult>? runForcedBrokerAlignment = null,
        Action<string, ReconciliationForcedConvergenceContext, ReconciliationForcedConvergenceResult>? onForcedConvergenceFailure =
            null,
        Func<long>? getExecutionActivityGeneration = null)
    {
        _getSnapshot = getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));
        _getActiveInstruments = getActiveInstruments ?? (() => Array.Empty<string>());
        _getMismatchObservations = getMismatchObservations ?? ((_, _) => Array.Empty<MismatchObservation>());
        _isInstrumentBlocked = isInstrumentBlocked ?? (_ => false);
        _isFlattenInProgress = isFlattenInProgress ?? (_ => false);
        _isRecoveryInProgress = isRecoveryInProgress ?? (_ => false);
        _log = log;
        _eventWriter = eventWriter;
        _runtimeAudit = runtimeAudit;
        _runInstrumentGateReconciliation = runInstrumentGateReconciliation;
        _evaluateReleaseReadiness = evaluateReleaseReadiness;
        _runForcedBrokerAlignment = runForcedBrokerAlignment;
        _onForcedConvergenceFailure = onForcedConvergenceFailure;
        _getExecutionActivityGeneration = getExecutionActivityGeneration;
        _stableWindowMs = stateConsistencyStableWindowMs > 0
            ? stateConsistencyStableWindowMs
            : MismatchEscalationPolicy.STATE_CONSISTENCY_STABLE_WINDOW_MS_LIVE;

        var initialDue = getExecutionActivityGeneration != null
            ? MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_ACTIVE_MS
            : MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS;
        _auditTimer = new Timer(OnAuditTick, null, initialDue, Timeout.Infinite);
    }

    /// <summary>Wakes the mismatch audit on the active cadence (execution activity, order updates, etc.).</summary>
    public void NotifyReconciliationAuditWake()
    {
        try
        {
            _auditTimer.Change(0, Timeout.Infinite);
        }
        catch
        {
            // Timer disposed or host shutting down
        }
    }

    public bool IsInstrumentBlockedByMismatch(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return false;
        return _stateByInstrument.TryGetValue(instrument.Trim(), out var s) && s.Blocked;
    }

    public string? GetBlockReason(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return null;
        if (!_stateByInstrument.TryGetValue(instrument.Trim(), out var s) || !s.Blocked) return null;
        return string.IsNullOrEmpty(s.BlockReason) ? MismatchEscalationPolicy.BLOCK_REASON_PERSISTENT_MISMATCH : s.BlockReason;
    }

    /// <summary>P1.5: Current gate phase for diagnostics/tests.</summary>
    public GateLifecyclePhase GetGateLifecyclePhase(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return GateLifecyclePhase.None;
        return _stateByInstrument.TryGetValue(instrument.Trim(), out var s) ? s.GateLifecyclePhase : GateLifecyclePhase.None;
    }

    /// <summary>
    /// Call from execution fills / order updates. Resets the per-burst expensive counter on structured state changes
    /// (intent, fill delta, position qty change, entry→protectives); time-gap fallback only when <paramref name="details"/> is default.
    /// </summary>
    public void NotifyExecutionTrigger(string instrument, DateTimeOffset utcNow,
        MismatchExecutionTriggerDetails details = default)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        var inst = instrument.Trim();
        if (!_stateByInstrument.TryGetValue(inst, out var state)) return;
        if (state.GateLifecyclePhase == GateLifecyclePhase.None) return;
        var gp = state.GateProgress;

        var resetBurst = false;
        if (details.EntryToProtectivesTransition)
            resetBurst = true;
        if (!string.IsNullOrEmpty(details.IntentId))
        {
            if (!string.IsNullOrEmpty(gp.LastTriggerIntentId) &&
                !string.Equals(details.IntentId, gp.LastTriggerIntentId, StringComparison.OrdinalIgnoreCase))
                resetBurst = true;
            gp.LastTriggerIntentId = details.IntentId;
        }

        if (details.FillDelta != 0)
            resetBurst = true;

        if (details.InstrumentPositionQty.HasValue)
        {
            if (gp.LastTriggerPositionQty.HasValue &&
                gp.LastTriggerPositionQty.Value != details.InstrumentPositionQty.Value)
                resetBurst = true;
            gp.LastTriggerPositionQty = details.InstrumentPositionQty.Value;
        }

        if (!resetBurst && details.Equals(default) && gp.LastExecutionTriggerUtc != default &&
            (utcNow - gp.LastExecutionTriggerUtc).TotalMilliseconds >
            MismatchEscalationPolicy.GATE_EXECUTION_TRIGGER_RESET_GAP_MS)
            resetBurst = true;

        if (resetBurst)
            gp.ReconciliationCyclesThisExecution = 0;

        gp.LastExecutionTriggerUtc = utcNow;
    }

    private void OnAuditTick(object? _)
    {
        lock (_auditRunLock)
        {
            var prevAuditUtc = _lastAuditUtc;
            var utcNow = DateTimeOffset.UtcNow;
            AccountSnapshot? snapshot = null;
            IReadOnlyList<MismatchObservation> observations = Array.Empty<MismatchObservation>();
            var cpu = _runtimeAudit != null ? RuntimeAuditHub.CpuStart() : 0L;
            try
            {
                snapshot = _getSnapshot();
                var instruments = _getActiveInstruments();

                if (instruments.Count == 0 && snapshot.Positions != null)
                {
                    var fromPositions = snapshot.Positions
                        .Where(p => (p.Quantity != 0 || !string.IsNullOrWhiteSpace(p.Instrument)) && !string.IsNullOrWhiteSpace(p.Instrument))
                        .Select(p => p.Instrument!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    instruments = fromPositions;
                }

                observations = _getMismatchObservations(snapshot, utcNow);
                var obsByInstrument = observations
                    .Where(o => !string.IsNullOrWhiteSpace(o.Instrument))
                    .GroupBy(o => o.Instrument.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var instrumentsSet = new HashSet<string>(instruments.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()), StringComparer.OrdinalIgnoreCase);
                foreach (var k in obsByInstrument.Keys)
                    instrumentsSet.Add(k);
                foreach (var kv in _stateByInstrument)
                {
                    if (kv.Value.GateLifecyclePhase != GateLifecyclePhase.None)
                        instrumentsSet.Add(kv.Key);
                }

                foreach (var inst in instrumentsSet)
                {
                    if (obsByInstrument.TryGetValue(inst, out var obs))
                        ProcessMismatchPresent(obs);
                    else
                        ProcessMismatchSignalAbsent(inst, utcNow);
                }

                foreach (var inst in instrumentsSet.Where(IsGateActiveForInstrument))
                {
                    obsByInstrument.TryGetValue(inst, out var gateObs);
                    AdvanceStateConsistencyGate(inst, snapshot, utcNow, gateObs);
                }

                _lastAuditUtc = utcNow;
            }
            catch (Exception ex)
            {
                _log?.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                    new { error = ex.Message, context = "MismatchEscalationCoordinator.OnAuditTick" }));
            }
            finally
            {
                if (cpu != 0)
                    _runtimeAudit?.CpuEnd(cpu, RuntimeAuditSubsystem.MismatchTimerTotal, instrument: "", stream: "", onIeaWorker: false);
                ScheduleNextAudit(utcNow, prevAuditUtc, snapshot, observations);
            }
        }
    }

    private static long ComputeSnapshotAuditFingerprint(AccountSnapshot? snap)
    {
        if (snap == null) return 0L;
        unchecked
        {
            long h = 17;
            var pos = snap.Positions;
            if (pos != null)
            {
                for (var i = 0; i < pos.Count; i++)
                {
                    var p = pos[i];
                    if (string.IsNullOrWhiteSpace(p.Instrument)) continue;
                    h = h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(p.Instrument.Trim());
                    h = h * 31 + p.Quantity;
                    h = h * 31 + p.AveragePrice.GetHashCode();
                }
            }

            var wo = snap.WorkingOrders;
            if (wo == null || wo.Count == 0)
                return h;

            var keys = new string[wo.Count];
            var n = 0;
            for (var i = 0; i < wo.Count; i++)
            {
                var w = wo[i];
                keys[n++] = string.Concat(
                    w.OrderId, "\x1F", w.Instrument, "\x1F", w.Quantity, "\x1F", w.OrderType ?? "", "\x1F",
                    w.StopPrice?.ToString(CultureInfo.InvariantCulture) ?? "", "\x1F",
                    w.Price?.ToString(CultureInfo.InvariantCulture) ?? "");
            }
            Array.Sort(keys, 0, n, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < n; i++)
                h = h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(keys[i]);
            return h;
        }
    }

    private bool AnyGateOrEscalationActive()
    {
        foreach (var kv in _stateByInstrument)
        {
            var s = kv.Value;
            if (s.GateLifecyclePhase != GateLifecyclePhase.None) return true;
            if (s.EscalationState != MismatchEscalationState.NONE) return true;
        }

        return false;
    }

    private void ScheduleNextAudit(DateTimeOffset utcNow, DateTimeOffset prevAuditUtc, AccountSnapshot? snapshot,
        IReadOnlyList<MismatchObservation> observations)
    {
        int nextMs;
        if (_getExecutionActivityGeneration == null)
        {
            nextMs = MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS;
        }
        else
        {
            var fp = ComputeSnapshotAuditFingerprint(snapshot);
            var gen = _getExecutionActivityGeneration();
            var fpChanged = _scheduleFingerprintPrev != long.MinValue && fp != _scheduleFingerprintPrev;
            var genChanged = _scheduleActivityGenPrev != long.MinValue && gen != _scheduleActivityGenPrev;
            var anyPresentObs = observations.Any(o => o.Present);
            var firstScheduleComplete = _scheduleFingerprintPrev == long.MinValue;
            var busy = fpChanged || genChanged || anyPresentObs || AnyGateOrEscalationActive() || firstScheduleComplete;

            _scheduleFingerprintPrev = fp;
            _scheduleActivityGenPrev = gen;

            if (busy)
                nextMs = MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_ACTIVE_MS;
            else
            {
                nextMs = MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_IDLE_MS;
                if (prevAuditUtc != DateTimeOffset.MinValue)
                {
                    var sincePrevMs = (utcNow - prevAuditUtc).TotalMilliseconds;
                    var maxGap = (double)MismatchEscalationPolicy.MISMATCH_AUDIT_MANDATORY_MAX_GAP_MS;
                    if (sincePrevMs + nextMs > maxGap)
                    {
                        var shortened = (int)(maxGap - sincePrevMs);
                        nextMs = Math.Max(MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_ACTIVE_MS, shortened);
                    }
                }
            }
        }

        nextMs = Math.Max(1, nextMs);
        try
        {
            _auditTimer.Change(nextMs, Timeout.Infinite);
        }
        catch
        {
            // Timer disposed
        }
    }

    private bool IsGateActiveForInstrument(string inst)
    {
        if (!_stateByInstrument.TryGetValue(inst, out var s)) return false;
        return s.GateLifecyclePhase != GateLifecyclePhase.None;
    }

    private void EmitCanonical(string inst, string eventType, DateTimeOffset utc, object payload, string severity = "INFO")
    {
        _eventWriter?.Emit(new CanonicalExecutionEvent
        {
            TimestampUtc = utc.ToString("o"),
            Instrument = inst,
            EventType = eventType,
            Severity = severity,
            Source = "MismatchEscalationCoordinator",
            Payload = payload
        });
    }

    private static bool HasResidualReleaseBlocker(StateConsistencyReleaseReadinessResult r) =>
        r.PendingAdoptionExists || !r.BrokerPositionExplainable || !r.BrokerWorkingExplainable || !r.LocalStateCoherent;

    /// <summary>
    /// Broker is flat (per release diagnostics) but gate cannot release — log structured blockers for ops.
    /// </summary>
    private void TryEmitReleaseBlockedAfterFlat(
        string inst,
        DateTimeOffset utcNow,
        MismatchInstrumentState state,
        StateConsistencyReleaseReadinessResult readiness,
        GateReconciliationResult? recon,
        bool skipExpensive)
    {
        if (_log == null) return;
        if (readiness.ReleaseReady || !readiness.SnapshotSufficient) return;
        if (readiness.DiagnosticBrokerPositionQty != 0) return;
        if (!HasResidualReleaseBlocker(readiness)) return;

        var contradictions = string.Join(";", readiness.Contradictions ?? new List<string>());
        var quietHash = unchecked((StringComparer.OrdinalIgnoreCase.GetHashCode(inst) * 397) ^
                                  (contradictions != null ? StringComparer.Ordinal.GetHashCode(contradictions) : 0));
        if (_releaseBlockedAfterFlatTelemetry.TryGetValue(inst, out var prev) && prev.Hash == quietHash &&
            (utcNow - prev.Utc).TotalSeconds < ReleaseBlockedAfterFlatQuietSeconds)
            return;
        _releaseBlockedAfterFlatTelemetry[inst] = (quietHash, utcNow);

        var payload = new
        {
            instrument = inst,
            gate_phase = state.GateLifecyclePhase.ToString(),
            broker_position_qty = readiness.DiagnosticBrokerPositionQty,
            journal_open_qty = readiness.DiagnosticJournalOpenQty,
            broker_working_count = readiness.DiagnosticBrokerWorkingCount,
            iea_owned_plus_adopted_working = readiness.DiagnosticIeaOwnedPlusAdoptedWorking,
            pending_adoption_candidate_count = readiness.DiagnosticPendingAdoptionCandidateCount,
            release_ready = readiness.ReleaseReady,
            contradictions,
            release_summary = readiness.Summary,
            safe_cleanup_attempted = !skipExpensive,
            gate_reconciliation_outcome = recon?.OutcomeStatus.ToString(),
            gate_reconciliation_release_ready_after = recon?.ReleaseReadyAfter,
            gate_reconciliation_reason = recon?.Reason,
            gate_reconciliation_duration_ms = recon?.DurationMs,
            note = "Gate active, broker flat per release probe, but ReleaseReady false — residual journal/adoption/working/IEA coherence"
        };
        _log.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RELEASE_BLOCKED_AFTER_FLAT", payload));
        EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RELEASE_BLOCKED_AFTER_FLAT", utcNow, payload, "WARN");
    }

    /// <summary>P1.5-H: Clean signal absent does not clear gate; only stability release clears.</summary>
    private void ProcessMismatchSignalAbsent(string instrument, DateTimeOffset utcNow)
    {
        if (!_stateByInstrument.TryGetValue(instrument, out var state))
            return;

        state.LastSeenUtc = utcNow;
        state.MismatchStillPresent = false;

        if (state.GateLifecyclePhase != GateLifecyclePhase.None)
            return;

        if (state.EscalationState == MismatchEscalationState.NONE)
            return;
        if (state.EscalationState == MismatchEscalationState.FAIL_CLOSED)
            return;

        state.ConsecutiveCleanPassCount++;
    }

    private void ProcessMismatchPresent(MismatchObservation obs)
    {
        var inst = obs.Instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return;

        var state = _stateByInstrument.GetOrAdd(inst, _ => new MismatchInstrumentState());
        var utcNow = obs.ObservedUtc;

        if (!obs.Present)
        {
            ProcessMismatchSignalAbsent(inst, utcNow);
            return;
        }

        state.MismatchStillPresent = true;
        state.LastSeenUtc = utcNow;
        state.LastSummary = obs.Summary;

        if (state.EscalationState == MismatchEscalationState.NONE)
        {
            state.EscalationState = MismatchEscalationState.DETECTED;
            state.MismatchType = obs.MismatchType;
            state.FirstDetectedUtc = utcNow;
            state.LastDetectedUtc = utcNow;
            state.Blocked = true;
            state.BlockReason = MismatchEscalationPolicy.BLOCK_REASON_STATE_CONSISTENCY_GATE;
            state.GateLifecyclePhase = GateLifecyclePhase.DetectedBlocked;
            state.LastGateEngagedUtc = utcNow;
            state.StableWindowMsApplied = _stableWindowMs;
            state.FirstConsistentUtc = default;
            state.LastConsistentUtc = default;
            state.GateProgress.Reset();
            state.ConvergenceEpisode.Clear();
            state.ReconciliationHysteresisUntilUtc = default;
            state.HysteresisMismatchTypeAtFreeze = null;
            state.PostForcedConvergenceFingerprint = 0;
            state.ForcedConvergenceSucceeded = false;
            _mismatchDetectedCount++;
            IncrementTypeMetric(obs.MismatchType);
            var detectedPayload = ToGatePayload(state, inst, utcNow, obs, null, null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_DETECTED", detectedPayload));
            EmitCanonical(inst, ExecutionEventTypes.MISMATCH_DETECTED, utcNow, detectedPayload, "WARN");
            var gatePayload = ToGatePayload(state, inst, utcNow, obs, null, null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_ENGAGED", gatePayload));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_ENGAGED", utcNow, gatePayload, "WARN");
            var tsv = StateConsistencyAuditFormat.BuildFourStateTsvLine(utcNow, inst, obs, state.MismatchType);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_FOUR_STATE_TSV",
                new { four_state_tsv = tsv, note = "Tab-separated; columns match STATE_CONSISTENCY_GATE audit spec" }));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_FOUR_STATE_TSV", utcNow, new { four_state_tsv = tsv }, "INFO");
            return;
        }

        state.LastDetectedUtc = utcNow;
        state.PersistenceMs = (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds;

        if (state.EscalationState == MismatchEscalationState.DETECTED)
        {
            if (state.PersistenceMs >= MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS)
            {
                state.EscalationState = MismatchEscalationState.PERSISTENT_MISMATCH;
                state.Blocked = true;
                state.BlockReason = MismatchEscalationPolicy.BLOCK_REASON_PERSISTENT_MISMATCH;
                if (state.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
                    state.GateLifecyclePhase = GateLifecyclePhase.PersistentMismatch;
                _mismatchPersistentCount++;
                var p = ToGatePayload(state, inst, utcNow, obs, null, null);
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_PERSISTENT", p));
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_BLOCKED", p));
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH", p));
                EmitCanonical(inst, "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH", utcNow, p, "ERROR");
            }
            return;
        }

        if (state.EscalationState == MismatchEscalationState.PERSISTENT_MISMATCH)
        {
            state.PersistenceMs = (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds;
            if (state.PersistenceMs >= MismatchEscalationPolicy.MISMATCH_FAIL_CLOSED_THRESHOLD_MS ||
                state.RetryCount >= MismatchEscalationPolicy.MISMATCH_MAX_RETRIES)
            {
                state.EscalationState = MismatchEscalationState.FAIL_CLOSED;
                state.GateLifecyclePhase = GateLifecyclePhase.FailClosed;
                _mismatchFailClosedCount++;
                var failClosedPayload = ToGatePayload(state, inst, utcNow, obs, null, null);
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_FAIL_CLOSED", failClosedPayload));
                EmitCanonical(inst, ExecutionEventTypes.MISMATCH_FAIL_CLOSED, utcNow, failClosedPayload, "CRITICAL");
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RECOVERY_FAILED", failClosedPayload));
                EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RECOVERY_FAILED", utcNow, failClosedPayload, "CRITICAL");
            }
        }
    }

    private static bool IsStallLikeSkipReason(string? skipReason) =>
        skipReason is "post_alignment_stall" or "throttle_cooldown" or "reconciliation_hysteresis"
            or "reentry_loop_blocked" or "execution_cycle_cap_reached" or "hard_stop_active"
            or "absolute_gate_lifetime_cap" or "skipped";

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

    private void AdvanceStateConsistencyGate(string inst, AccountSnapshot snapshot, DateTimeOffset utcNow,
        MismatchObservation? latestObs)
    {
        if (!_stateByInstrument.TryGetValue(inst, out var state))
            return;
        if (state.GateLifecyclePhase == GateLifecyclePhase.None)
            return;
        if (state.EscalationState == MismatchEscalationState.FAIL_CLOSED)
            return;

        var obsForSig = CoalesceGateObservation(inst, state, latestObs, utcNow);
        var gp = state.GateProgress;

        EnsureConvergenceEpisodeStarted(state, obsForSig, utcNow);

        if (gp.ProgressHardStopped && utcNow >= gp.ProgressHardStopUntilUtc)
        {
            gp.ProgressHardStopped = false;
            gp.ProgressHardStopUntilUtc = DateTimeOffset.MinValue;
        }

        state.PersistenceMs = state.FirstDetectedUtc != default
            ? (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds
            : 0;

        if (state.EscalationState == MismatchEscalationState.DETECTED &&
            state.PersistenceMs >= MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS)
        {
            state.EscalationState = MismatchEscalationState.PERSISTENT_MISMATCH;
            state.Blocked = true;
            state.BlockReason = MismatchEscalationPolicy.BLOCK_REASON_PERSISTENT_MISMATCH;
            if (state.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
                state.GateLifecyclePhase = GateLifecyclePhase.PersistentMismatch;
            _mismatchPersistentCount++;
            var p = ToGatePayload(state, inst, utcNow, obsForSig, null, null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH", p));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH", utcNow, p, "ERROR");
        }

        if (state.GateLifecyclePhase == GateLifecyclePhase.DetectedBlocked)
        {
            state.GateLifecyclePhase = GateLifecyclePhase.Reconciling;
            gp.TotalExpensiveSinceGateEngaged = 0;
            var startPayload = ToGatePayload(state, inst, utcNow, obsForSig, null, null);
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED", startPayload));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED", utcNow, startPayload, "INFO");
        }

        // --- External fingerprint: reset throttle only on meaningful snapshot/mismatch change (hash can jitter on noise) ---
        GetThrottleBaselineComponents(inst, snapshot, obsForSig, state, out var posQty, out var bwObs, out var lwObs,
            out var mismatchType);
        var fpNow = GateProgressEvaluator.BuildExternalFingerprint(inst, snapshot, obsForSig);
        var externalFingerprintChanged = gp.LastExternalFingerprint != 0 && fpNow != gp.LastExternalFingerprint;
        if (externalFingerprintChanged)
        {
            if (IsMeaningfulThrottleBaselineChange(gp, posQty, bwObs, lwObs, mismatchType))
            {
                EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_THROTTLE_RESET", new
                {
                    reason = "meaningful_external_change",
                    signature_hash = GateProgressEvaluator.ComputeSignatureHash32(
                        GateProgressEvaluator.BuildSignature(state, obsForSig,
                            StateConsistencyReleaseEvaluator.Indeterminate(inst), null)),
                    no_progress_iterations = gp.NoProgressIterations,
                    backoff_level = gp.BackoffLevel
                });
                ResetGateThrottle(gp);
                CaptureThrottleBaseline(gp, posQty, bwObs, lwObs, mismatchType);
            }
            else
            {
                EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_THROTTLE_PRESERVED", new
                {
                    reason = "noise_fingerprint_change",
                    last_external_fingerprint = gp.LastExternalFingerprint,
                    new_external_fingerprint = fpNow,
                    no_progress_iterations = gp.NoProgressIterations,
                    next_allowed_expensive_utc = gp.NextAllowedExpensiveReconciliationUtc != DateTimeOffset.MinValue
                        ? gp.NextAllowedExpensiveReconciliationUtc.ToString("o")
                        : null
                });
            }
        }

        gp.LastExternalFingerprint = fpNow;
        if (!gp.ThrottleBaselineInitialized)
            CaptureThrottleBaseline(gp, posQty, bwObs, lwObs, mismatchType);

        StateConsistencyReleaseReadinessResult readinessProbe;
        try
        {
            readinessProbe = _evaluateReleaseReadiness?.Invoke(inst, snapshot, utcNow)
                             ?? StateConsistencyReleaseEvaluator.Indeterminate(inst);
        }
        catch (Exception ex)
        {
            _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                new { error = ex.Message, context = "evaluateReleaseReadiness_probe", instrument = inst }));
            readinessProbe = StateConsistencyReleaseEvaluator.Indeterminate(inst, "evaluate_exception");
        }

        var phaseProbe = GateProgressEvaluator.ProjectGatePhaseForProgressSignature(state, readinessProbe);
        var sigProbe = GateProgressEvaluator.BuildSignature(state, obsForSig, readinessProbe, null, phaseProbe);
        var warmupDone = gp.ExpensivePassesCompleted >= MismatchEscalationPolicy.GATE_PROGRESS_WARMUP_EXPENSIVE_PASSES;
        var refProgressTime = gp.LastMeasurableProgressUtc ?? state.LastGateEngagedUtc;
        var timeSinceProgressMs = refProgressTime != default
            ? (utcNow - refProgressTime).TotalMilliseconds
            : 0;
        var noProgressTimeExceeded = warmupDone && timeSinceProgressMs >= MismatchEscalationPolicy.GATE_NO_PROGRESS_TIME_THRESHOLD_MS;
        var noProgressIterExceeded = warmupDone &&
                                     gp.NoProgressIterations >= MismatchEscalationPolicy.GATE_NO_PROGRESS_ITERATION_THRESHOLD;
        var throttleScheduleActive = noProgressTimeExceeded || noProgressIterExceeded;
        if (warmupDone && throttleScheduleActive && gp.NextAllowedExpensiveReconciliationUtc == DateTimeOffset.MinValue)
        {
            var interval = GateProgressEvaluator.CurrentBackoffIntervalMs(gp.BackoffLevel);
            gp.NextAllowedExpensiveReconciliationUtc = utcNow.AddMilliseconds(interval);
        }

        var inCooldown = gp.NextAllowedExpensiveReconciliationUtc != DateTimeOffset.MinValue &&
                         utcNow < gp.NextAllowedExpensiveReconciliationUtc;
        var throttleScheduleSkip = warmupDone && throttleScheduleActive && inCooldown;
        var hardStopSkip = gp.ProgressHardStopped && utcNow < gp.ProgressHardStopUntilUtc;
        var executionCycleCapReached =
            gp.ReconciliationCyclesThisExecution >= MismatchEscalationPolicy.GATE_EXECUTION_RECONCILIATION_CAP;
        var reentryLoopNested = gp.InReconciliationLoop;
        var reentryTooSoon = gp.LastReconciliationExitUtc != default &&
                             (utcNow - gp.LastReconciliationExitUtc).TotalMilliseconds <
                             MismatchEscalationPolicy.GATE_REENTRY_BLOCK_MS;
        var reentrySkip = reentryLoopNested || reentryTooSoon;

        var hysteresisSkip = state.ReconciliationHysteresisUntilUtc != default &&
                             utcNow < state.ReconciliationHysteresisUntilUtc &&
                             state.HysteresisMismatchTypeAtFreeze.HasValue &&
                             state.HysteresisMismatchTypeAtFreeze.Value == obsForSig.MismatchType;

        var postAlignmentStallSkip = state.ForcedConvergenceSucceeded &&
                                     state.PostForcedConvergenceFingerprint != 0 &&
                                     fpNow == state.PostForcedConvergenceFingerprint;

        var antiOscillationSkip = hysteresisSkip || postAlignmentStallSkip;

        var skipExpensive = throttleScheduleSkip || hardStopSkip || executionCycleCapReached || reentrySkip ||
                            antiOscillationSkip;

        string? skipReason = null;
        if (skipExpensive)
        {
            if (postAlignmentStallSkip)
                skipReason = "post_alignment_stall";
            else if (hysteresisSkip)
                skipReason = "reconciliation_hysteresis";
            else if (reentryLoopNested || reentryTooSoon)
                skipReason = "reentry_loop_blocked";
            else if (hardStopSkip)
                skipReason = gp.TotalExpensiveSinceGateEngaged >=
                             MismatchEscalationPolicy.GATE_ABSOLUTE_MAX_EXPENSIVE_RECONCILIATIONS
                    ? "absolute_gate_lifetime_cap"
                    : "hard_stop_active";
            else if (executionCycleCapReached)
                skipReason = "execution_cycle_cap_reached";
            else if (throttleScheduleSkip)
                skipReason = "throttle_cooldown";
            else
                skipReason = "skipped";
        }

        if (skipExpensive && executionCycleCapReached)
            EmitExecutionCapIfNeeded(inst, utcNow, state, gp);
        if (skipExpensive && (reentryLoopNested || reentryTooSoon))
            EmitReentryBlockedIfNeeded(inst, utcNow, state, gp, reentryLoopNested, reentryTooSoon);

        GateReconciliationResult? recon = null;
        if (!skipExpensive)
        {
            {
                var epRun = state.ConvergenceEpisode;
                epRun.LastActionAttempted = "gate_recovery_runner_and_adoption_scan";
                epRun.AttemptCount++;
            }

            try
            {
                gp.InReconciliationLoop = true;
                try
                {
                    recon = _runInstrumentGateReconciliation?.Invoke(inst, utcNow, gp.ReconciliationCyclesThisExecution + 1);
                }
                catch (Exception ex)
                {
                    _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                        new { error = ex.Message, context = "runInstrumentGateReconciliation", instrument = inst }));
                }
            }
            finally
            {
                gp.InReconciliationLoop = false;
                gp.LastReconciliationExitUtc = utcNow;
            }

            gp.ExpensivePassesCompleted++;
            gp.LastExpensiveReconciliationUtc = utcNow;
            gp.ReconciliationCyclesThisExecution++;
            gp.TotalExpensiveSinceGateEngaged++;
        }
        else if (throttleScheduleSkip)
        {
            EmitThrottledIfNeeded(inst, utcNow, state, gp, sigProbe);
        }

        StateConsistencyReleaseReadinessResult readiness;
        if (skipExpensive)
            readiness = readinessProbe;
        else
        {
            try
            {
                readiness = _evaluateReleaseReadiness?.Invoke(inst, snapshot, utcNow)
                            ?? StateConsistencyReleaseEvaluator.Indeterminate(inst);
            }
            catch (Exception ex)
            {
                _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_ERROR", state: "ENGINE",
                    new { error = ex.Message, context = "evaluateReleaseReadiness", instrument = inst }));
                readiness = StateConsistencyReleaseEvaluator.Indeterminate(inst, "evaluate_exception");
            }
        }

        if (recon != null)
            recon.ReleaseReadyAfter = readiness.ReleaseReady;

        GateProgressSignature? sigAfterNullable = null;
        if (!skipExpensive)
        {
            var phaseForProgress = GateProgressEvaluator.ProjectGatePhaseForProgressSignature(state, readiness);
            var sigAfter = GateProgressEvaluator.BuildSignature(state, obsForSig, readiness, recon, phaseForProgress);
            sigAfterNullable = sigAfter;
            UpdateProgressAfterExpensivePass(inst, utcNow, state, gp, sigAfter);
        }

        var refProgressForTelemetry = gp.LastMeasurableProgressUtc ?? state.LastGateEngagedUtc;
        var timeSinceProgressTelemetryMs = refProgressForTelemetry != default
            ? (utcNow - refProgressForTelemetry).TotalMilliseconds
            : (double?)null;
        var progressSigHash = GateProgressEvaluator.ComputeSignatureHash32(
            sigAfterNullable ?? sigProbe);
        var hardStopActive = gp.ProgressHardStopped && utcNow < gp.ProgressHardStopUntilUtc;
        var resultTelemetry = new GateReconciliationResultTelemetry(
            ExpensiveInvoked: !skipExpensive,
            ThrottleSuppressedExpensive: skipExpensive,
            NoProgressIterations: gp.NoProgressIterations,
            TimeSinceLastProgressMs: timeSinceProgressTelemetryMs,
            WarmupDone: warmupDone,
            NextAllowedExpensiveUtc: gp.NextAllowedExpensiveReconciliationUtc != DateTimeOffset.MinValue
                ? gp.NextAllowedExpensiveReconciliationUtc.ToString("o")
                : null,
            ProgressSignatureHash: progressSigHash,
            ExternalFingerprintChanged: externalFingerprintChanged,
            SkipReason: skipReason,
            ExecutionCycleCount: gp.ReconciliationCyclesThisExecution,
            HardStopActive: hardStopActive,
            TotalExpensiveSinceGateEngaged: gp.TotalExpensiveSinceGateEngaged);

        var preSigHash = GateProgressEvaluator.ComputeSignatureHash32(sigProbe);
        var postSig = sigAfterNullable ?? sigProbe;
        var postSigHash = GateProgressEvaluator.ComputeSignatureHash32(postSig);
        var progressChanged = preSigHash != postSigHash;
        var progressChangeType = skipExpensive
            ? $"skip:{skipReason ?? "unspecified"}"
            : StateConsistencyAuditFormat.DescribeProgressChange(sigProbe, postSig);

        var materialReadinessFp = BuildMaterialReadinessFingerprint(readiness, state.MismatchType);
        var identicalStallCycle = skipExpensive && !externalFingerprintChanged && !progressChanged &&
                                  IsStallLikeSkipReason(skipReason);
        if (identicalStallCycle)
        {
            var stallQuietFp = BuildStallQuietFingerprint(fpNow, materialReadinessFp, skipReason, postSigHash);
            if (stallQuietFp == gp.LastStallQuietFingerprint && gp.LastStallQuietFingerprint != 0)
                gp.IdenticalSkippedEvaluationCount++;
            else
            {
                gp.LastStallQuietFingerprint = stallQuietFp;
                gp.IdenticalSkippedEvaluationCount = 1;
            }
        }
        else
        {
            gp.IdenticalSkippedEvaluationCount = 0;
            gp.LastStallQuietFingerprint = 0;
        }

        var suppressQuietGateTelemetry = false;
        if (skipExpensive && !progressChanged)
        {
            var quietHash = unchecked((((StringComparer.OrdinalIgnoreCase.GetHashCode(inst) * 397) ^ preSigHash) * 397 ^ postSigHash) * 397 ^
                                      (skipReason != null ? StringComparer.Ordinal.GetHashCode(skipReason) : 0));
            if (_quietGateTelemetry.TryGetValue(inst, out var prevQuiet) && prevQuiet.Hash == quietHash &&
                (utcNow - prevQuiet.Utc).TotalSeconds < QuietGateTelemetrySeconds)
                suppressQuietGateTelemetry = true;
            else
                _quietGateTelemetry[inst] = (quietHash, utcNow);
            if (identicalStallCycle && gp.IdenticalSkippedEvaluationCount > 1)
            {
                suppressQuietGateTelemetry = true;
                if (!gp.IdenticalStallSuppressionDebugLogged)
                {
                    gp.IdenticalStallSuppressionDebugLogged = true;
                    var stallDbg = new
                    {
                        timestamp_utc = utcNow.ToString("o"),
                        instrument = inst,
                        skip_reason = skipReason,
                        identical_skipped_count = gp.IdenticalSkippedEvaluationCount,
                        external_fingerprint = fpNow,
                        stall_quiet_fingerprint = gp.LastStallQuietFingerprint,
                        note = "TEMP_DEBUG: identical skipped cycles suppressing RESULT/delta; remove when stable"
                    };
                    _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "GATE_STALL_SUPPRESSED_IDENTICAL_CYCLE", stallDbg));
                    EmitCanonical(inst, "GATE_STALL_SUPPRESSED_IDENTICAL_CYCLE", utcNow, stallDbg, "INFO");
                }
            }
        }

        if (!suppressQuietGateTelemetry)
        {
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_PROGRESS_DELTA",
                new
                {
                    timestamp_utc = utcNow.ToString("o"),
                    instrument = inst,
                    intent_id = obsForSig.IntentIdsCsv ?? "",
                    pre_state_hash = preSigHash,
                    post_state_hash = postSigHash,
                    state_changed = progressChanged,
                    change_type = progressChangeType,
                    expensive_invoked = !skipExpensive
                }));
            EmitCanonical(inst, "RECONCILIATION_PROGRESS_DELTA", utcNow,
                new
                {
                    pre_state_hash = preSigHash,
                    post_state_hash = postSigHash,
                    state_changed = progressChanged,
                    change_type = progressChangeType
                }, "INFO");
        }

        var attemptSuccess = !skipExpensive && recon != null &&
                             recon.OutcomeStatus == ReconciliationOutcomeStatus.Success;
        string? failReason = skipExpensive
            ? skipReason
            : recon?.Reason;
        if (!skipExpensive && recon != null && recon.OutcomeStatus != ReconciliationOutcomeStatus.Success)
            failReason = string.IsNullOrEmpty(failReason) ? recon.OutcomeStatus.ToString() : failReason;
        if (!suppressQuietGateTelemetry)
        {
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_ATTEMPT_AUDIT",
                new
                {
                    timestamp_utc = utcNow.ToString("o"),
                    instrument = inst,
                    intent_id = obsForSig.IntentIdsCsv ?? "",
                    mismatch_detected_type = state.MismatchType.ToString(),
                    action_attempted = skipExpensive ? "skip_expensive_reconciliation" : "gate_recovery_runner_and_adoption_scan",
                    target_layer = skipExpensive ? "n/a" : "registry+journal_via_runner",
                    expected_result = "converge_working_counts_or_release_ready",
                    actual_result = skipExpensive
                        ? "not_run"
                        : (recon?.OutcomeStatus.ToString() ?? "null_recon"),
                    success = attemptSuccess,
                    reason_if_failed = attemptSuccess ? null : failReason,
                    gate_phase = state.GateLifecyclePhase.ToString(),
                    broker_working_before = recon?.BrokerWorkingCountBefore,
                    broker_working_after = recon?.BrokerWorkingCountAfter,
                    iea_owned_after = recon?.IeaOwnedCountAfter
                }));
            EmitCanonical(inst, "RECONCILIATION_ATTEMPT_AUDIT", utcNow,
                new
                {
                    action_attempted = skipExpensive ? "skip" : "gate_recovery",
                    success = attemptSuccess
                }, !attemptSuccess && !skipExpensive ? "WARN" : "INFO");
        }

        if (!skipExpensive)
        {
            var ep = state.ConvergenceEpisode;
            if (progressChanged)
            {
                ep.NoProgressStreak = 0;
                ep.LastProgressUtc = utcNow;
            }
            else
                ep.NoProgressStreak++;
        }

        TryInvokeForcedConvergenceIfLimitsExceeded(inst, state, gp, obsForSig, utcNow, fpNow);

        var resultPayload = ToGatePayloadWithResultTelemetry(state, inst, utcNow, obsForSig, recon, readiness, skipExpensive,
            resultTelemetry);
        if (!suppressQuietGateTelemetry)
        {
            TestGateResultEmitCount++;
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT", resultPayload));
            EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT", utcNow, resultPayload, "INFO");
        }
        else
            TestGateResultSuppressCount++;

        if (state.EscalationState == MismatchEscalationState.FAIL_CLOSED ||
            state.GateLifecyclePhase == GateLifecyclePhase.FailClosed)
            return;

        // Fail-closed unchanged. Persistent mismatch blocks only while not release-ready; when ready, stability window + release still apply (no stuck-forever).
        if ((state.EscalationState == MismatchEscalationState.PERSISTENT_MISMATCH ||
             state.GateLifecyclePhase == GateLifecyclePhase.PersistentMismatch) &&
            !readiness.ReleaseReady)
            return;

        var windowMs = state.StableWindowMsApplied > 0 ? state.StableWindowMsApplied : _stableWindowMs;

        if (!readiness.ReleaseReady)
        {
            TryEmitReleaseBlockedAfterFlat(inst, utcNow, state, readiness, recon, skipExpensive);
            if (state.GateLifecyclePhase == GateLifecyclePhase.StablePendingRelease)
            {
                var resetPayload = new
                {
                    gate = ToGatePayload(state, inst, utcNow, obsForSig, recon, readiness),
                    stable_reset_reason = readiness.Summary
                };
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RESTABILIZATION_RESET", resetPayload));
                EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RESTABILIZATION_RESET", utcNow, resetPayload, "WARN");
                state.GateLifecyclePhase = GateLifecyclePhase.Reconciling;
                state.FirstConsistentUtc = default;
            }
            return;
        }

        state.LastConsistentUtc = utcNow;

        if (state.GateLifecyclePhase != GateLifecyclePhase.StablePendingRelease)
        {
            state.GateLifecyclePhase = GateLifecyclePhase.StablePendingRelease;
            var preserveStableClock = readiness.ReleaseReady && skipExpensive && !externalFingerprintChanged &&
                                      IsStallLikeSkipReason(skipReason) && state.FirstConsistentUtc != default;
            if (!preserveStableClock)
                state.FirstConsistentUtc = utcNow;
            if (!preserveStableClock)
            {
                var spPayload = ToGatePayload(state, inst, utcNow, obsForSig, recon, readiness);
                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_STABLE_PENDING_RELEASE", spPayload));
                EmitCanonical(inst, "STATE_CONSISTENCY_GATE_STABLE_PENDING_RELEASE", utcNow, spPayload, "INFO");
            }
            return;
        }

        var stableMs = state.FirstConsistentUtc != default
            ? (long)(utcNow - state.FirstConsistentUtc).TotalMilliseconds
            : 0;

        if (stableMs < windowMs)
            return;

        var releaseCompletedUnderStall = skipExpensive && IsStallLikeSkipReason(skipReason);

        var releaseSource = state.EscalationState == MismatchEscalationState.PERSISTENT_MISMATCH
            ? "persistent_recovery"
            : "standard";

        state.Blocked = false;
        state.BlockReason = "";
        state.EscalationState = MismatchEscalationState.NONE;
        state.GateLifecyclePhase = GateLifecyclePhase.None;
        state.LastReleaseUtc = utcNow;
        state.FirstConsistentUtc = default;
        state.ConsecutiveCleanPassCount = 0;
        state.MismatchStillPresent = false;
        state.GateProgress.Reset();
        state.ConvergenceEpisode.Clear();
        state.ReconciliationHysteresisUntilUtc = default;
        state.HysteresisMismatchTypeAtFreeze = null;
        state.PostForcedConvergenceFingerprint = 0;
        state.ForcedConvergenceSucceeded = false;
        _mismatchClearedCount++;

        var releasedPayload = new
        {
            gate = ToGatePayload(state, inst, utcNow, obsForSig, recon, readiness),
            stable_for_ms = stableMs,
            stable_window_ms = windowMs,
            release_source = releaseSource
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RELEASED", releasedPayload));
        EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RELEASED", utcNow, releasedPayload, "INFO");
        if (releaseCompletedUnderStall)
        {
            var underStallDbg = new
            {
                timestamp_utc = utcNow.ToString("o"),
                instrument = inst,
                skip_reason = skipReason,
                stable_for_ms = stableMs,
                stable_window_ms = windowMs,
                release_source = releaseSource,
                note = "TEMP_DEBUG: gate released while expensive recon skipped (stall); remove when stable"
            };
            _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "GATE_RELEASE_COMPLETED_UNDER_STALL", underStallDbg));
            EmitCanonical(inst, "GATE_RELEASE_COMPLETED_UNDER_STALL", utcNow, underStallDbg, "INFO");
        }
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_CLEARED", ToGatePayload(state, inst, utcNow, obsForSig, null, null)));
    }

    private static MismatchObservation CoalesceGateObservation(string inst, MismatchInstrumentState state,
        MismatchObservation? latest, DateTimeOffset utcNow)
    {
        if (latest != null)
            return latest;
        return new MismatchObservation
        {
            Instrument = inst,
            MismatchType = state.MismatchType,
            Present = state.MismatchStillPresent,
            ObservedUtc = utcNow,
            BrokerWorkingOrderCount = 0,
            LocalWorkingOrderCount = 0
        };
    }

    private void ResetGateThrottle(GateReconciliationProgressState gp)
    {
        gp.NoProgressIterations = 0;
        gp.BackoffLevel = 0;
        gp.NextAllowedExpensiveReconciliationUtc = DateTimeOffset.MinValue;
        gp.LastProgressSignature = null;
        gp.ReconciliationCyclesThisExecution = 0;
        gp.ProgressHardStopped = false;
        gp.ProgressHardStopUntilUtc = DateTimeOffset.MinValue;
    }

    private void EmitExecutionCapIfNeeded(string inst, DateTimeOffset utcNow, MismatchInstrumentState state,
        GateReconciliationProgressState gp)
    {
        const int minEmitGapMs = 5000;
        if (gp.LastExecutionCapEmitUtc != DateTimeOffset.MinValue &&
            (utcNow - gp.LastExecutionCapEmitUtc).TotalMilliseconds < minEmitGapMs)
            return;
        EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_EXECUTION_CAP_REACHED", new
        {
            cap = MismatchEscalationPolicy.GATE_EXECUTION_RECONCILIATION_CAP,
            reconciliation_cycles_this_execution = gp.ReconciliationCyclesThisExecution
        });
        gp.LastExecutionCapEmitUtc = utcNow;
    }

    private void EmitReentryBlockedIfNeeded(string inst, DateTimeOffset utcNow, MismatchInstrumentState state,
        GateReconciliationProgressState gp, bool nested, bool tooSoon)
    {
        const int minEmitGapMs = 2000;
        if (gp.LastReentryBlockEmitUtc != DateTimeOffset.MinValue &&
            (utcNow - gp.LastReentryBlockEmitUtc).TotalMilliseconds < minEmitGapMs)
            return;
        EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_REENTRY_BLOCKED", new
        {
            nested_reconciliation_loop = nested,
            within_reentry_window_ms = tooSoon,
            reentry_block_ms = MismatchEscalationPolicy.GATE_REENTRY_BLOCK_MS
        });
        gp.LastReentryBlockEmitUtc = utcNow;
    }

    private void EmitThrottledIfNeeded(string inst, DateTimeOffset utcNow, MismatchInstrumentState state,
        GateReconciliationProgressState gp, GateProgressSignature sigProbe)
    {
        const int minEmitGapMs = 5000;
        if (gp.LastEmittedThrottleKind == "RECONCILIATION_THROTTLED" &&
            (utcNow - gp.LastThrottleEmitUtc).TotalMilliseconds < minEmitGapMs)
            return;

        var nextMs = (gp.NextAllowedExpensiveReconciliationUtc - utcNow).TotalMilliseconds;
        var payload = new
        {
            instrument = inst,
            gate_phase = state.GateLifecyclePhase.ToString(),
            signature_hash = GateProgressEvaluator.ComputeSignatureHash32(sigProbe),
            no_progress_iterations = gp.NoProgressIterations,
            time_since_last_progress_ms = gp.LastMeasurableProgressUtc != null
                ? (utcNow - gp.LastMeasurableProgressUtc.Value).TotalMilliseconds
                : (double?)null,
            backoff_interval_ms = GateProgressEvaluator.CurrentBackoffIntervalMs(gp.BackoffLevel),
            next_allowed_in_ms = nextMs,
            next_allowed_expensive_utc = gp.NextAllowedExpensiveReconciliationUtc != DateTimeOffset.MinValue
                ? gp.NextAllowedExpensiveReconciliationUtc.ToString("o")
                : null,
            backoff_level = gp.BackoffLevel
        };
        EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_THROTTLED", payload);
        gp.LastThrottleEmitUtc = utcNow;
        gp.LastEmittedThrottleKind = "RECONCILIATION_THROTTLED";
    }

    private void UpdateProgressAfterExpensivePass(string inst, DateTimeOffset utcNow, MismatchInstrumentState state,
        GateReconciliationProgressState gp, GateProgressSignature sigAfter)
    {
        var hash = GateProgressEvaluator.ComputeSignatureHash32(sigAfter);
        if (gp.LastProgressSignature == null)
        {
            gp.LastProgressSignature = sigAfter;
            return;
        }

        var prev = gp.LastProgressSignature.Value;
        if (GateProgressEvaluator.IsMeasurableProgress(prev, sigAfter))
        {
            gp.LastMeasurableProgressUtc = utcNow;
            gp.NoProgressIterations = 0;
            gp.BackoffLevel = 0;
            gp.NextAllowedExpensiveReconciliationUtc = DateTimeOffset.MinValue;
            gp.ProgressHardStopped = false;
            gp.ProgressHardStopUntilUtc = DateTimeOffset.MinValue;
            gp.LastProgressSignature = sigAfter;
            EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_PROGRESS_OBSERVED", new
            {
                kind = "measurable",
                signature_hash = hash,
                gate_phase = state.GateLifecyclePhase.ToString(),
                no_progress_iterations = 0,
                prior_signature_hash = GateProgressEvaluator.ComputeSignatureHash32(prev)
            });
            EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_THROTTLE_BACKOFF_UPDATED", new
            {
                backoff_level = 0,
                next_interval_ms = MismatchEscalationPolicy.GATE_RECONCILIATION_MIN_INTERVAL_MS,
                reason = "progress"
            });
            return;
        }

        gp.NoProgressIterations++;
        gp.LastProgressSignature = sigAfter;
        var warmupDone = gp.ExpensivePassesCompleted >= MismatchEscalationPolicy.GATE_PROGRESS_WARMUP_EXPENSIVE_PASSES;
        var refProgressTime = gp.LastMeasurableProgressUtc ?? state.LastGateEngagedUtc;
        var timeSinceProgressMs = refProgressTime != default
            ? (utcNow - refProgressTime).TotalMilliseconds
            : 0;
        var noProgressTimeExceeded = warmupDone && timeSinceProgressMs >= MismatchEscalationPolicy.GATE_NO_PROGRESS_TIME_THRESHOLD_MS;
        var noProgressIterExceeded = warmupDone &&
                                     gp.NoProgressIterations >= MismatchEscalationPolicy.GATE_NO_PROGRESS_ITERATION_THRESHOLD;

        const int noProgressEmitMinGapMs = 4000;
        if (gp.LastNoProgressEmitUtc == DateTimeOffset.MinValue ||
            (utcNow - gp.LastNoProgressEmitUtc).TotalMilliseconds >= noProgressEmitMinGapMs)
        {
            EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_NO_PROGRESS_DETECTED", new
            {
                signature_hash = hash,
                no_progress_iterations = gp.NoProgressIterations,
                time_since_last_progress_ms = timeSinceProgressMs,
                iteration_threshold_hit = noProgressIterExceeded,
                time_threshold_hit = noProgressTimeExceeded
            });
            gp.LastNoProgressEmitUtc = utcNow;
        }

        if (warmupDone && (noProgressIterExceeded || noProgressTimeExceeded))
        {
            var interval = GateProgressEvaluator.CurrentBackoffIntervalMs(gp.BackoffLevel);
            gp.NextAllowedExpensiveReconciliationUtc = utcNow.AddMilliseconds(interval);
            EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_THROTTLE_BACKOFF_UPDATED", new
            {
                backoff_level = gp.BackoffLevel,
                next_interval_ms = interval,
                next_allowed_utc = gp.NextAllowedExpensiveReconciliationUtc.ToString("o"),
                reason = noProgressIterExceeded ? "no_progress_iterations" : "no_progress_time"
            });
            gp.BackoffLevel = Math.Min(gp.BackoffLevel + 1, 12);
        }

        if (warmupDone &&
            gp.NoProgressIterations >= MismatchEscalationPolicy.GATE_HARD_STOP_NO_PROGRESS_ITERATIONS &&
            timeSinceProgressMs >= MismatchEscalationPolicy.GATE_HARD_STOP_NO_PROGRESS_TIME_MS)
        {
            if (!gp.ProgressHardStopped)
            {
                gp.ProgressHardStopped = true;
                gp.ProgressHardStopUntilUtc =
                    utcNow.AddMilliseconds(MismatchEscalationPolicy.GATE_HARD_STOP_DURATION_MS);
                EmitGateProgressControlEvent(inst, utcNow, state, "RECONCILIATION_HARD_STOPPED", new
                {
                    no_progress_iterations = gp.NoProgressIterations,
                    time_since_last_progress_ms = timeSinceProgressMs,
                    hard_stop_until_utc = gp.ProgressHardStopUntilUtc.ToString("o"),
                    duration_ms = MismatchEscalationPolicy.GATE_HARD_STOP_DURATION_MS
                });
            }
        }
    }

    private void EmitGateProgressControlEvent(string inst, DateTimeOffset utcNow, MismatchInstrumentState state,
        string eventType, object payload)
    {
        var merged = new Dictionary<string, object?>
        {
            ["instrument"] = inst,
            ["gate_phase"] = state.GateLifecyclePhase.ToString(),
            ["timestamp_utc"] = utcNow.ToString("o")
        };
        foreach (var p in payload.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            merged[p.Name] = p.GetValue(payload);

        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, eventType, merged));
        EmitCanonical(inst, eventType, utcNow, merged, "INFO");
    }

    private object ToGatePayload(
        MismatchInstrumentState state,
        string instrument,
        DateTimeOffset utcNow,
        MismatchObservation? obs,
        GateReconciliationResult? recon,
        StateConsistencyReleaseReadinessResult? readiness,
        bool gateReconciliationThrottled = false)
    {
        var mismatchAge = state.FirstDetectedUtc != default
            ? (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds
            : 0L;
        var stableFor = state.FirstConsistentUtc != default && state.GateLifecyclePhase == GateLifecyclePhase.StablePendingRelease
            ? (long)(utcNow - state.FirstConsistentUtc).TotalMilliseconds
            : 0L;

        return new
        {
            instrument,
            gate_state = state.GateLifecyclePhase.ToString(),
            escalation_state = state.EscalationState.ToString(),
            mismatch_age_ms = mismatchAge,
            blocked = state.Blocked,
            block_reason = state.BlockReason,
            broker_position_qty = obs?.BrokerQty ?? 0,
            broker_working_count = obs?.BrokerWorkingOrderCount ?? 0,
            iea_owned_count = obs?.LocalWorkingOrderCount,
            iea_adopted_count = (int?)null,
            pending_adoption_count = (int?)null,
            unexplained_working_count = readiness?.UnexplainedBrokerWorkingCount,
            unexplained_position_qty = readiness?.UnexplainedBrokerPositionQty,
            release_ready = readiness?.ReleaseReady,
            stable_for_ms = stableFor,
            stable_window_ms = state.StableWindowMsApplied > 0 ? state.StableWindowMsApplied : _stableWindowMs,
            reconciliation_outcome = recon == null
                ? null
                : new
                {
                    recon.Mode,
                    recon.OutcomeStatus,
                    recon.BrokerWorkingCountBefore,
                    recon.BrokerWorkingCountAfter,
                    recon.IeaOwnedCountBefore,
                    recon.IeaOwnedCountAfter,
                    recon.AdoptionCandidateCountBefore,
                    recon.AdoptionCandidateCountAfter,
                    recon.UnexplainedWorkingCountAfter,
                    recon.UnexplainedPositionQtyAfter,
                    recon.ReleaseReadyAfter,
                    recon.Reason,
                    recon.DurationMs,
                    recon.RunnerInvoked
                },
            readiness_summary = readiness?.Summary,
            readiness_contradictions = readiness?.Contradictions,
            broker_qty = obs?.BrokerQty ?? 0,
            local_qty = obs?.LocalQty ?? 0,
            mismatch_type = state.MismatchType.ToString(),
            persistence_ms = state.PersistenceMs,
            timestamp_utc = utcNow.ToString("o"),
            gate_action_policy = StateConsistencyGateActionPolicy.PolicyNote,
            gate_reconciliation_throttled = gateReconciliationThrottled
        };
    }

    private object ToGatePayloadWithResultTelemetry(
        MismatchInstrumentState state,
        string instrument,
        DateTimeOffset utcNow,
        MismatchObservation? obs,
        GateReconciliationResult? recon,
        StateConsistencyReleaseReadinessResult? readiness,
        bool gateReconciliationThrottled,
        GateReconciliationResultTelemetry telemetry)
    {
        var mismatchAge = state.FirstDetectedUtc != default
            ? (long)(utcNow - state.FirstDetectedUtc).TotalMilliseconds
            : 0L;
        var stableFor = state.FirstConsistentUtc != default && state.GateLifecyclePhase == GateLifecyclePhase.StablePendingRelease
            ? (long)(utcNow - state.FirstConsistentUtc).TotalMilliseconds
            : 0L;

        return new
        {
            instrument,
            gate_state = state.GateLifecyclePhase.ToString(),
            escalation_state = state.EscalationState.ToString(),
            mismatch_age_ms = mismatchAge,
            blocked = state.Blocked,
            block_reason = state.BlockReason,
            broker_position_qty = obs?.BrokerQty ?? 0,
            broker_working_count = obs?.BrokerWorkingOrderCount ?? 0,
            iea_owned_count = obs?.LocalWorkingOrderCount,
            iea_adopted_count = (int?)null,
            pending_adoption_count = (int?)null,
            unexplained_working_count = readiness?.UnexplainedBrokerWorkingCount,
            unexplained_position_qty = readiness?.UnexplainedBrokerPositionQty,
            release_ready = readiness?.ReleaseReady,
            stable_for_ms = stableFor,
            stable_window_ms = state.StableWindowMsApplied > 0 ? state.StableWindowMsApplied : _stableWindowMs,
            reconciliation_outcome = recon == null
                ? null
                : new
                {
                    recon.Mode,
                    recon.OutcomeStatus,
                    recon.BrokerWorkingCountBefore,
                    recon.BrokerWorkingCountAfter,
                    recon.IeaOwnedCountBefore,
                    recon.IeaOwnedCountAfter,
                    recon.AdoptionCandidateCountBefore,
                    recon.AdoptionCandidateCountAfter,
                    recon.UnexplainedWorkingCountAfter,
                    recon.UnexplainedPositionQtyAfter,
                    recon.ReleaseReadyAfter,
                    recon.Reason,
                    recon.DurationMs,
                    recon.RunnerInvoked
                },
            readiness_summary = readiness?.Summary,
            readiness_contradictions = readiness?.Contradictions,
            broker_qty = obs?.BrokerQty ?? 0,
            local_qty = obs?.LocalQty ?? 0,
            mismatch_type = state.MismatchType.ToString(),
            persistence_ms = state.PersistenceMs,
            timestamp_utc = utcNow.ToString("o"),
            gate_action_policy = StateConsistencyGateActionPolicy.PolicyNote,
            gate_reconciliation_throttled = gateReconciliationThrottled,
            expensive_invoked = telemetry.ExpensiveInvoked,
            throttle_suppressed_expensive = telemetry.ThrottleSuppressedExpensive,
            skip_reason = telemetry.SkipReason,
            no_progress_iterations = telemetry.NoProgressIterations,
            time_since_last_progress_ms = telemetry.TimeSinceLastProgressMs,
            warmup_done = telemetry.WarmupDone,
            next_allowed_expensive_utc = telemetry.NextAllowedExpensiveUtc,
            progress_signature_hash = telemetry.ProgressSignatureHash,
            external_fingerprint_changed = telemetry.ExternalFingerprintChanged,
            execution_cycle_count = telemetry.ExecutionCycleCount,
            hard_stop_active = telemetry.HardStopActive,
            total_expensive_since_gate_engaged = telemetry.TotalExpensiveSinceGateEngaged
        };
    }

    private static void GetThrottleBaselineComponents(string inst, AccountSnapshot snapshot, MismatchObservation? obs,
        MismatchInstrumentState st, out int posQty, out int bw, out int lw, out MismatchType mt)
    {
        posQty = 0;
        if (snapshot.Positions != null)
        {
            var p = snapshot.Positions.FirstOrDefault(x =>
                string.Equals(x.Instrument?.Trim(), inst, StringComparison.OrdinalIgnoreCase));
            if (p != null)
                posQty = p.Quantity;
        }

        bw = obs?.BrokerWorkingOrderCount ?? 0;
        lw = obs?.LocalWorkingOrderCount ?? 0;
        mt = st.MismatchType;
    }

    private static bool IsMeaningfulThrottleBaselineChange(GateReconciliationProgressState gp, int posQty, int bw, int lw,
        MismatchType mt)
    {
        if (!gp.ThrottleBaselineInitialized)
            return false;
        return posQty != gp.ThrottleBaselinePositionQty
            || bw != gp.ThrottleBaselineBrokerWorking
            || lw != gp.ThrottleBaselineLocalWorking
            || mt != gp.ThrottleBaselineMismatchType;
    }

    private static void CaptureThrottleBaseline(GateReconciliationProgressState gp, int posQty, int bw, int lw,
        MismatchType mt)
    {
        gp.ThrottleBaselinePositionQty = posQty;
        gp.ThrottleBaselineBrokerWorking = bw;
        gp.ThrottleBaselineLocalWorking = lw;
        gp.ThrottleBaselineMismatchType = mt;
        gp.ThrottleBaselineInitialized = true;
    }

    private void EnsureConvergenceEpisodeStarted(MismatchInstrumentState state, MismatchObservation obs, DateTimeOffset utcNow)
    {
        if (state.GateLifecyclePhase == GateLifecyclePhase.None)
            return;
        var key = obs.IntentIdsCsv ?? "";
        var ep = state.ConvergenceEpisode;
        if (ep.EpisodeId == 0)
        {
            ep.StartNew(++_nextConvergenceEpisodeId, utcNow, key);
            return;
        }

        if (!string.Equals(ep.EpisodeIntentKey, key, StringComparison.Ordinal))
            ep.StartNew(++_nextConvergenceEpisodeId, utcNow, key);
    }

    private void TryInvokeForcedConvergenceIfLimitsExceeded(
        string inst,
        MismatchInstrumentState state,
        GateReconciliationProgressState gp,
        MismatchObservation obsForSig,
        DateTimeOffset utcNow,
        ulong fpNow)
    {
        if (state.EscalationState == MismatchEscalationState.FAIL_CLOSED)
            return;
        if (state.GateLifecyclePhase == GateLifecyclePhase.None)
            return;
        var ep = state.ConvergenceEpisode;
        if (ep.EpisodeId == 0)
            return;

        var durationMs = ep.FirstSeenUtc != default
            ? (utcNow - ep.FirstSeenUtc).TotalMilliseconds
            : 0;
        string? limitReason = null;
        if (ep.AttemptCount >= MismatchEscalationPolicy.RECONCILIATION_CONVERGENCE_MAX_ATTEMPTS)
            limitReason = "max_attempts";
        else if (ep.NoProgressStreak >= MismatchEscalationPolicy.RECONCILIATION_CONVERGENCE_MAX_NO_PROGRESS)
            limitReason = "no_progress";
        else if (durationMs >= MismatchEscalationPolicy.RECONCILIATION_CONVERGENCE_MAX_DURATION_MS)
            limitReason = "timeout";

        if (limitReason == null)
            return;

        var ctx = new ReconciliationForcedConvergenceContext
        {
            Instrument = inst,
            IntentIdsCsv = obsForSig.IntentIdsCsv,
            LimitReason = limitReason,
            Attempts = ep.AttemptCount,
            NoProgressCount = ep.NoProgressStreak
        };

        var result = _runForcedBrokerAlignment?.Invoke(ctx) ?? ReconciliationForcedConvergenceResult.NoHandler();

        var eventPayload = new
        {
            instrument = inst,
            intent_id = obsForSig.IntentIdsCsv ?? "",
            reason = limitReason,
            attempts = ep.AttemptCount,
            no_progress_count = ep.NoProgressStreak,
            final_action = "broker_alignment",
            aligned = result.AlignedWithBroker,
            failure_reason = result.FailureReason
        };
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_FORCED_CONVERGENCE", eventPayload));
        EmitCanonical(inst, "RECONCILIATION_FORCED_CONVERGENCE", utcNow, eventPayload,
            result.AlignedWithBroker ? "WARN" : "CRITICAL");

        if (result.AlignedWithBroker)
        {
            ep.StartNew(++_nextConvergenceEpisodeId, utcNow, obsForSig.IntentIdsCsv);
            state.ForcedConvergenceSucceeded = true;
            state.PostForcedConvergenceFingerprint = result.PostAlignmentFingerprint != 0
                ? result.PostAlignmentFingerprint
                : fpNow;
            state.ReconciliationHysteresisUntilUtc =
                utcNow.AddMilliseconds(MismatchEscalationPolicy.RECONCILIATION_CONVERGENCE_HYSTERESIS_MS);
            state.HysteresisMismatchTypeAtFreeze = state.MismatchType;
            ResetGateThrottle(gp);
            gp.LastExternalFingerprint = fpNow;
            return;
        }

        state.EscalationState = MismatchEscalationState.FAIL_CLOSED;
        state.GateLifecyclePhase = GateLifecyclePhase.FailClosed;
        state.Blocked = true;
        state.BlockReason = string.IsNullOrEmpty(result.FailureReason)
            ? MismatchEscalationPolicy.BLOCK_REASON_PERSISTENT_MISMATCH
            : $"FORCED_CONVERGENCE:{result.FailureReason}";
        _mismatchFailClosedCount++;
        var failPayload = ToGatePayload(state, inst, utcNow, obsForSig, null, null);
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "RECONCILIATION_MISMATCH_FAIL_CLOSED", failPayload));
        EmitCanonical(inst, ExecutionEventTypes.MISMATCH_FAIL_CLOSED, utcNow, failPayload, "CRITICAL");
        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_RECOVERY_FAILED", failPayload));
        EmitCanonical(inst, "STATE_CONSISTENCY_GATE_RECOVERY_FAILED", utcNow, failPayload, "CRITICAL");
        _onForcedConvergenceFailure?.Invoke(inst, ctx, result);
    }

    private void IncrementTypeMetric(MismatchType type)
    {
        switch (type)
        {
            case MismatchType.BROKER_AHEAD: _mismatchBrokerAheadCount++; break;
            case MismatchType.JOURNAL_AHEAD: _mismatchJournalAheadCount++; break;
            case MismatchType.POSITION_QTY_MISMATCH: _mismatchPositionQtyCount++; break;
            case MismatchType.ORDER_REGISTRY_MISSING: _mismatchRegistryMissingCount++; break;
            case MismatchType.PROTECTIVE_STATE_DIVERGENCE: _mismatchProtectiveDivergenceCount++; break;
        }
    }

    internal void ProcessObservationForTest(MismatchObservation obs) => ProcessMismatchPresent(obs);

    internal void ProcessCleanPassForTest(string instrument, DateTimeOffset utcNow) =>
        ProcessMismatchSignalAbsent(instrument, utcNow);

    internal MismatchInstrumentState? GetStateForTest(string instrument) =>
        _stateByInstrument.TryGetValue(instrument, out var s) ? s : null;

    /// <summary>For P1.5 tests: advance gate for one instrument (same as audit tail).</summary>
    internal void AdvanceStateConsistencyGateForTest(string instrument, AccountSnapshot snapshot, DateTimeOffset utcNow,
        MismatchObservation? observation = null) =>
        AdvanceStateConsistencyGate(instrument.Trim(), snapshot, utcNow, observation);

    internal void SetStableWindowForTest(int ms)
    {
        foreach (var kv in _stateByInstrument)
            kv.Value.StableWindowMsApplied = ms;
    }

    internal void SetForcedConvergenceStallForTest(string instrument, bool succeeded, ulong postAlignmentFingerprint)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        if (!_stateByInstrument.TryGetValue(instrument.Trim(), out var s)) return;
        s.ForcedConvergenceSucceeded = succeeded;
        s.PostForcedConvergenceFingerprint = postAlignmentFingerprint;
    }

    internal void ResetTestGateTelemetryCounters()
    {
        TestGateResultEmitCount = 0;
        TestGateResultSuppressCount = 0;
    }

    public void EmitMetrics(DateTimeOffset utcNow)
    {
        var instruments = _stateByInstrument
            .Where(kv => kv.Value.EscalationState != MismatchEscalationState.NONE || kv.Value.GateLifecyclePhase != GateLifecyclePhase.None)
            .Select(kv => new
            {
                instrument = kv.Key,
                mismatch_type = kv.Value.MismatchType.ToString(),
                escalation_state = kv.Value.EscalationState.ToString(),
                gate_phase = kv.Value.GateLifecyclePhase.ToString(),
                blocked = kv.Value.Blocked,
                persistence_ms = kv.Value.PersistenceMs,
                retry_count = kv.Value.RetryCount,
                first_detected_utc = kv.Value.FirstDetectedUtc.ToString("o"),
                last_detected_utc = kv.Value.LastDetectedUtc.ToString("o"),
                block_reason = kv.Value.BlockReason
            })
            .ToList();

        _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "RECONCILIATION_MISMATCH_METRICS", state: "ENGINE",
            new
            {
                mismatch_detected_count = _mismatchDetectedCount,
                mismatch_persistent_count = _mismatchPersistentCount,
                mismatch_fail_closed_count = _mismatchFailClosedCount,
                mismatch_cleared_count = _mismatchClearedCount,
                mismatch_broker_ahead_count = _mismatchBrokerAheadCount,
                mismatch_journal_ahead_count = _mismatchJournalAheadCount,
                mismatch_position_qty_count = _mismatchPositionQtyCount,
                mismatch_registry_missing_count = _mismatchRegistryMissingCount,
                mismatch_protective_divergence_count = _mismatchProtectiveDivergenceCount,
                last_audit_utc = _lastAuditUtc != DateTimeOffset.MinValue ? _lastAuditUtc.ToString("o") : null,
                instruments
            }));
    }
}
