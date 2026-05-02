using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
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

                if (instruments.Count == 0 && snapshot.Positions != null && _isInstrumentInEngineScope == null)
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

                // Retain instruments with active gate phase even when IsExecutionInstrumentInThisEngineScope is false
                // (e.g. streams committed and journals closed while StablePendingRelease still needs ticks to release).
                if (_isInstrumentInEngineScope != null)
                {
                    instrumentsSet.RemoveWhere(i =>
                    {
                        if (string.IsNullOrWhiteSpace(i)) return true;
                        var trimmed = i.Trim();
                        if (_isInstrumentInEngineScope(trimmed)) return false;
                        if (_stateByInstrument.TryGetValue(trimmed, out var gateSt) &&
                            gateSt.GateLifecyclePhase != GateLifecyclePhase.None)
                        {
                            MaybeEmitGateScopeRetainedActivePhase(utcNow, trimmed, gateSt.GateLifecyclePhase);
                            return false;
                        }

                        return true;
                    });
                }

                var deferredMismatchPendingIeA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                _mismatchEvalInvariantCycleActive = true;
                _mismatchEvalScratch.Clear();
                foreach (var inst in instrumentsSet)
                    _mismatchEvalScratch[inst] = new MismatchEvalScratchRow();

                try
                {
                    foreach (var inst in instrumentsSet)
                    {
                        obsByInstrument.TryGetValue(inst, out var scopedObs);
                        if (_stateByInstrument.TryGetValue(inst, out var inactiveState) &&
                            TryQuiesceInactiveInstrumentState(inst, utcNow, inactiveState, scopedObs))
                            continue;

                        var pendingIeA = _getPendingExecutionWorkloadForInstrument?.Invoke(inst) ?? 0;
                        if (pendingIeA > 0)
                        {
                            var pendingDecision = ObservePendingIeaDefer(inst, pendingIeA, utcNow, forGateAdvance: true);
                            deferredMismatchPendingIeA.Add(inst);
                            if (pendingDecision.EmitDiagnostic)
                            {
                                _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "MISMATCH_EVAL_SKIPPED_PENDING_EXECUTION", state: "ENGINE",
                                    new
                                    {
                                        instrument = inst,
                                        pending_execution_count = pendingIeA,
                                        run_id = _getRunIdForMismatchDiagnostics?.Invoke() ?? "",
                                        ts_utc = utcNow.ToString("o"),
                                        stable_pending_signature_ms = pendingDecision.StableSignatureMs > 0
                                            ? Math.Round(pendingDecision.StableSignatureMs, 0)
                                            : 0,
                                        gate_advance_backoff_armed = pendingDecision.GateAdvanceBackoffArmed,
                                        gate_advance_cooldown_remaining_ms = pendingDecision.GateAdvanceCooldownRemainingMs > 0
                                            ? Math.Round(pendingDecision.GateAdvanceCooldownRemainingMs, 0)
                                            : 0
                                    }));
                            }
                            continue;
                        }

                        ObservePendingIeaDefer(inst, 0, utcNow, forGateAdvance: false);

                        _log?.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "MISMATCH_EVAL_START", state: "ENGINE",
                            new
                            {
                                instrument = inst,
                                pending_execution_count = 0,
                                run_id = _getRunIdForMismatchDiagnostics?.Invoke() ?? "",
                                ts_utc = utcNow.ToString("o")
                            }));

                        if (scopedObs?.Present == true)
                            ProcessMismatchPresent(scopedObs);
                        else if (obsByInstrument.TryGetValue(inst, out var obs))
                            ProcessMismatchPresent(obs);
                        else
                            ProcessMismatchSignalAbsent(inst, utcNow);
                    }

                    // Gate advancement must run even when mismatch eval is deferred for pending IEA work.
                    foreach (var inst in instrumentsSet.Where(IsGateActiveForInstrument))
                    {
                        obsByInstrument.TryGetValue(inst, out var gateObsScope);
                        if (_stateByInstrument.TryGetValue(inst, out var inactiveGateState) &&
                            TryQuiesceInactiveInstrumentState(inst, utcNow, inactiveGateState, gateObsScope))
                            continue;

                        var gatePendingIeA = _getPendingExecutionWorkloadForInstrument?.Invoke(inst) ?? 0;
                        if (gatePendingIeA > 0)
                        {
                            var pendingDecision = ObservePendingIeaDefer(inst, gatePendingIeA, utcNow, forGateAdvance: true);
                            if (pendingDecision.EmitDiagnostic)
                            {
                                _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "STATE_CONSISTENCY_GATE_DEFERRED_PENDING_IEA_EXECUTION",
                                    new
                                    {
                                        instrument = inst,
                                        pending_execution_count = gatePendingIeA,
                                        gate_advance_skipped = pendingDecision.SkipGateAdvance,
                                        gate_advance_backoff_armed = pendingDecision.GateAdvanceBackoffArmed,
                                        stable_pending_signature_ms = pendingDecision.StableSignatureMs > 0
                                            ? Math.Round(pendingDecision.StableSignatureMs, 0)
                                            : 0,
                                        gate_advance_cooldown_remaining_ms = pendingDecision.GateAdvanceCooldownRemainingMs > 0
                                            ? Math.Round(pendingDecision.GateAdvanceCooldownRemainingMs, 0)
                                            : 0,
                                        note = pendingDecision.SkipGateAdvance
                                            ? "Skipping repeated gate advancement until pending execution signature changes or cooldown elapses"
                                            : "Pending execution workload unchanged; permitting a cooled gate advancement pass"
                                    }));
                            }

                            if (pendingDecision.SkipGateAdvance)
                                continue;
                        }
                        else
                        {
                            ObservePendingIeaDefer(inst, 0, utcNow, forGateAdvance: true);
                        }

                        AdvanceStateConsistencyGate(inst, snapshot, utcNow, gateObsScope);
                    }

                    foreach (var inst in instrumentsSet)
                        EmitMismatchEvaluationInvariant(inst, utcNow);
                }
                finally
                {
                    _mismatchEvalInvariantCycleActive = false;
                    _mismatchEvalScratch.Clear();
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
            if (!(_isInstrumentRecoveryRelevant?.Invoke(kv.Key) ?? true) &&
                s.EscalationState != MismatchEscalationState.FAIL_CLOSED &&
                s.GateLifecyclePhase != GateLifecyclePhase.FailClosed)
                continue;
            if (s.GateLifecyclePhase != GateLifecyclePhase.None) return true;
            if (s.EscalationState != MismatchEscalationState.NONE) return true;
        }

        return false;
    }

    private bool TryQuiesceInactiveInstrumentState(string inst, DateTimeOffset utcNow, MismatchInstrumentState state,
        MismatchObservation? latestObs)
    {
        if ((_isInstrumentRecoveryRelevant?.Invoke(inst) ?? true))
            return false;
        if (state.GateLifecyclePhase == GateLifecyclePhase.None &&
            state.EscalationState == MismatchEscalationState.NONE)
            return false;
        if (state.EscalationState == MismatchEscalationState.FAIL_CLOSED ||
            state.GateLifecyclePhase == GateLifecyclePhase.FailClosed)
            return false;
        if ((_getPendingExecutionWorkloadForInstrument?.Invoke(inst) ?? 0) > 0)
            return false;
        if (latestObs?.Present == true)
            return false;

        var wasBlocked = state.Blocked;
        state.Blocked = false;
        state.BlockReason = "";
        state.EscalationState = MismatchEscalationState.NONE;
        state.GateLifecyclePhase = GateLifecyclePhase.None;
        state.LastReleaseUtc = utcNow;
        state.FirstConsistentUtc = default;
        state.LastConsistentUtc = default;
        PublishMismatchExecutionBlockAuthorityIfChanged(inst, state, wasBlocked, utcNow);

        _log?.Write(RobotEvents.ExecutionBase(utcNow, "", inst, "GATE_RELEASED",
            new
            {
                instrument = inst,
                release_source = "inactive_scope_quiesce",
                note = "No live stream or interrupted reentry work remains for this instrument; stale gate state cleared to stop end-of-run recovery churn."
            }));
        return true;
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
}
