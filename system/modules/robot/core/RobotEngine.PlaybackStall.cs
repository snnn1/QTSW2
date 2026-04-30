using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core;

public sealed partial class RobotEngine
{
    private bool TryHandlePlaybackStallQuiescence(DateTimeOffset utcNow, bool requestStopIfEligible)
    {
        if (!_isolatedPlaybackPersistence)
        {
            ResetPlaybackStallQuiescence();
            return false;
        }

        if (!(_healthMonitor?.IsPlaybackEngineTickStallActive ?? false))
        {
            ResetPlaybackStallQuiescence();
            return false;
        }

        var execIngressQueued = 0;
        var orderIngressQueued = 0;
        NinjaTraderSimAdapter? ingressProbe = null;
        if (_executionAdapter is NinjaTraderSimAdapter adapterProbe)
        {
            ingressProbe = adapterProbe;
            ingressProbe.GetTotalCallbackIngressQueueLengths(out execIngressQueued, out orderIngressQueued);
            if (execIngressQueued > 0 || orderIngressQueued > 0)
            {
                ResetPlaybackStallQuiescence();
                return false;
            }
        }

        if (_playbackStallQuiesceEligibleSinceUtc != DateTimeOffset.MinValue)
        {
            Volatile.Write(ref _playbackStallQuiesceArmed, 1);

            if (!requestStopIfEligible)
                return true;

            var stallIdleSeconds = (utcNow - _playbackStallQuiesceEligibleSinceUtc).TotalSeconds;
            if (Volatile.Read(ref _playbackStallQuiesceStopRequested) == 0
                && stallIdleSeconds < PlaybackStallQuiesceGraceSeconds)
                return true;

            if (Interlocked.Exchange(ref _playbackStallQuiesceStopRequested, 1) == 0)
            {
                _playbackStallQuiesceStopRequestedUtc = utcNow;

                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ENGINE_PLAYBACK_STALL_QUIESCENCE_STOP_REQUESTED", state: "ENGINE",
                    new
                    {
                        source = "playback_stall_quiesce",
                        idle_since_utc = _playbackStallQuiesceEligibleSinceUtc.ToString("o"),
                        idle_duration_seconds = Math.Round(stallIdleSeconds, 3),
                        callback_ingress_queued = execIngressQueued,
                        order_ingress_queued = orderIngressQueued,
                        note = "Playback tick stall remained active with zero active streams, zero live recovery instruments, and no callback ingress. Requesting clean engine stop."
                    }));

                ThreadPool.QueueUserWorkItem(static state =>
                {
                    try
                    {
                        ((RobotEngine)state!).Stop();
                    }
                    catch
                    {
                        // Best-effort quiescence stop; never throw on thread pool.
                    }
                }, this);
            }

            if (Volatile.Read(ref _shutdownCompleted) == 0
                && _playbackStallQuiesceStopRequestedUtc != DateTimeOffset.MinValue
                && (utcNow - _playbackStallQuiesceStopRequestedUtc).TotalSeconds >= PlaybackStallQuiesceForceFinalizeSeconds)
            {
                TryForcePlaybackStallQuiescenceFinalize(utcNow, execIngressQueued, orderIngressQueued, stallIdleSeconds);
            }

            return true;
        }

        AccountSnapshot? quiesceSnap = null;
        if (_executionAdapter == null)
        {
            ResetPlaybackStallQuiescence();
            return false;
        }

        try
        {
            quiesceSnap = _executionAdapter.GetAccountSnapshot(utcNow);
        }
        catch
        {
            ResetPlaybackStallQuiescence();
            return false;
        }

        if (quiesceSnap == null)
        {
            ResetPlaybackStallQuiescence();
            return false;
        }

        if (HasPlaybackStallBrokerExposureBlocker(quiesceSnap,
                out var brokerPositionGross,
                out var robotWorkingOrders,
                out var totalWorkingOrders,
                out var positionInstruments,
                out var robotWorkingInstruments))
        {
            if (_playbackStallQuiesceExposureDeferFirstUtc == DateTimeOffset.MinValue)
                _playbackStallQuiesceExposureDeferFirstUtc = utcNow;

            var exposureDeferSeconds = Math.Max(0.0, (utcNow - _playbackStallQuiesceExposureDeferFirstUtc).TotalSeconds);
            LogPlaybackStallQuiescenceDeferredByExposure(
                utcNow,
                brokerPositionGross,
                robotWorkingOrders,
                totalWorkingOrders,
                positionInstruments,
                robotWorkingInstruments,
                exposureDeferSeconds,
                execIngressQueued,
                orderIngressQueued);

            if (requestStopIfEligible && exposureDeferSeconds >= PlaybackStallQuiesceLiveExposureForceFinalizeSeconds)
            {
                TryForcePlaybackStallLiveExposureFinalize(
                    utcNow,
                    brokerPositionGross,
                    robotWorkingOrders,
                    totalWorkingOrders,
                    positionInstruments,
                    robotWorkingInstruments,
                    exposureDeferSeconds,
                    execIngressQueued,
                    orderIngressQueued);
                return true;
            }

            ResetPlaybackStallQuiescenceArm();
            _playbackStallQuiesceIeaDeferFirstUtc = DateTimeOffset.MinValue;
            return false;
        }

        _playbackStallQuiesceExposureDeferFirstUtc = DateTimeOffset.MinValue;
        var liveRecoveryScope = GetPlaybackStallLiveRecoveryExecutionInstrumentKeys(quiesceSnap);
        if (liveRecoveryScope.Count > 0)
        {
            ResetPlaybackStallQuiescence();
            return false;
        }

        if (ingressProbe != null)
        {
            ingressProbe.GetTotalIeaPendingExecutionWorkload(out var ieaPendingWork, out var ieaPendingInstruments);
            if (ieaPendingWork > 0)
            {
                if (_playbackStallQuiesceIeaDeferFirstUtc == DateTimeOffset.MinValue)
                    _playbackStallQuiesceIeaDeferFirstUtc = utcNow;

                var pendingAgeSeconds = Math.Max(0.0, (utcNow - _playbackStallQuiesceIeaDeferFirstUtc).TotalSeconds);
                if (pendingAgeSeconds < PlaybackStallQuiesceIeaStaleReleaseSeconds)
                {
                    LogPlaybackStallQuiescenceDeferredByIeaWork(
                        utcNow,
                        ieaPendingWork,
                        ieaPendingInstruments,
                        pendingAgeSeconds,
                        execIngressQueued,
                        orderIngressQueued);
                    ResetPlaybackStallQuiescenceArm();
                    return false;
                }

                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                    eventType: "ENGINE_PLAYBACK_STALL_QUIESCENCE_STALE_IEA_WORK_RELEASED", state: "ENGINE",
                    new
                    {
                        source = "playback_stall_quiesce",
                        iea_pending_work = ieaPendingWork,
                        iea_pending_instruments = ieaPendingInstruments,
                        iea_pending_age_seconds = Math.Round(pendingAgeSeconds, 3),
                        stale_release_seconds = PlaybackStallQuiesceIeaStaleReleaseSeconds,
                        callback_ingress_queued = execIngressQueued,
                        order_ingress_queued = orderIngressQueued,
                        note = "IEA workload remained pending after broker exposure, robot working orders, live recovery, and callback ingress were all clear. Treating it as stale for playback quiescence so shutdown can complete with an explicit stall signal."
                    }));
            }
            else
            {
                _playbackStallQuiesceIeaDeferFirstUtc = DateTimeOffset.MinValue;
            }
        }

        if (_playbackStallQuiesceEligibleSinceUtc == DateTimeOffset.MinValue)
        {
            _playbackStallQuiesceEligibleSinceUtc = utcNow;
            Volatile.Write(ref _playbackStallQuiesceArmed, 1);
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ENGINE_PLAYBACK_STALL_QUIESCENCE_ARMED", state: "ENGINE",
                new
                {
                    source = "playback_stall_quiesce",
                    active_streams = false,
                    live_recovery_instrument_count = 0,
                    callback_ingress_queued = execIngressQueued,
                    order_ingress_queued = orderIngressQueued,
                    grace_seconds = PlaybackStallQuiesceGraceSeconds
                }));
        }

        return true;
    }

    private void ResetPlaybackStallQuiescence()
    {
        ResetPlaybackStallQuiescenceArm();
        _playbackStallQuiesceExposureDeferFirstUtc = DateTimeOffset.MinValue;
        _playbackStallQuiesceIeaDeferFirstUtc = DateTimeOffset.MinValue;
    }

    private void ResetPlaybackStallQuiescenceArm()
    {
        _playbackStallQuiesceEligibleSinceUtc = DateTimeOffset.MinValue;
        _playbackStallQuiesceStopRequestedUtc = DateTimeOffset.MinValue;
        Volatile.Write(ref _playbackStallQuiesceArmed, 0);
        Volatile.Write(ref _playbackStallQuiesceStopRequested, 0);
        Volatile.Write(ref _playbackStallQuiesceForceFinalizeRequested, 0);
    }

    public bool IsPlaybackStallQuiescenceBlockingNtCalls() =>
        Volatile.Read(ref _playbackStallQuiesceArmed) != 0;

    private bool HasPlaybackStallBrokerExposureBlocker(
        AccountSnapshot snap,
        out int brokerPositionGross,
        out int robotWorkingOrders,
        out int totalWorkingOrders,
        out string positionInstruments,
        out string robotWorkingInstruments)
    {
        brokerPositionGross = 0;
        robotWorkingOrders = 0;
        totalWorkingOrders = 0;

        var positionInstrumentSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in snap.Positions ?? new List<PositionSnapshot>())
        {
            if (p == null || p.Quantity == 0)
                continue;

            brokerPositionGross += Math.Abs(p.Quantity);
            if (!string.IsNullOrWhiteSpace(p.Instrument))
                positionInstrumentSet.Add(p.Instrument.Trim());
        }

        var robotWorkingInstrumentSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var order in snap.WorkingOrders ?? new List<WorkingOrderSnapshot>())
        {
            if (order == null)
                continue;

            totalWorkingOrders++;
            if (!IsRobotOwnedOrder(order))
                continue;

            robotWorkingOrders++;
            if (!string.IsNullOrWhiteSpace(order.Instrument))
                robotWorkingInstrumentSet.Add(order.Instrument.Trim());
        }

        positionInstruments = string.Join(",", positionInstrumentSet.Take(8));
        robotWorkingInstruments = string.Join(",", robotWorkingInstrumentSet.Take(8));
        return brokerPositionGross > 0 || robotWorkingOrders > 0;
    }

    private void LogPlaybackStallQuiescenceDeferredByExposure(
        DateTimeOffset utcNow,
        int brokerPositionGross,
        int robotWorkingOrders,
        int totalWorkingOrders,
        string positionInstruments,
        string robotWorkingInstruments,
        double exposureDeferSeconds,
        int execIngressQueued,
        int orderIngressQueued)
    {
        if (_playbackStallQuiesceExposureDeferLastLogUtc != DateTimeOffset.MinValue &&
            (utcNow - _playbackStallQuiesceExposureDeferLastLogUtc).TotalSeconds < 10)
            return;

        _playbackStallQuiesceExposureDeferLastLogUtc = utcNow;
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
            eventType: "ENGINE_PLAYBACK_STALL_QUIESCENCE_DEFERRED_LIVE_EXPOSURE", state: "ENGINE",
            new
            {
                source = "playback_stall_quiesce",
                broker_position_gross = brokerPositionGross,
                robot_working_orders = robotWorkingOrders,
                total_working_orders = totalWorkingOrders,
                position_instruments = positionInstruments,
                robot_working_instruments = robotWorkingInstruments,
                exposure_defer_seconds = Math.Round(exposureDeferSeconds, 3),
                force_finalize_after_seconds = PlaybackStallQuiesceLiveExposureForceFinalizeSeconds,
                callback_ingress_queued = execIngressQueued,
                order_ingress_queued = orderIngressQueued,
                note = "Playback stall quiescence refused to arm because broker exposure or robot-owned working orders are still live."
            }));
    }

    private void LogPlaybackStallQuiescenceDeferredByIeaWork(
        DateTimeOffset utcNow,
        int ieaPendingWork,
        string ieaPendingInstruments,
        double pendingAgeSeconds,
        int execIngressQueued,
        int orderIngressQueued)
    {
        if (_playbackStallQuiesceIeaDeferLastLogUtc != DateTimeOffset.MinValue &&
            (utcNow - _playbackStallQuiesceIeaDeferLastLogUtc).TotalSeconds < 10)
            return;

        _playbackStallQuiesceIeaDeferLastLogUtc = utcNow;
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
            eventType: "ENGINE_PLAYBACK_STALL_QUIESCENCE_DEFERRED_IEA_WORK", state: "ENGINE",
            new
            {
                source = "playback_stall_quiesce",
                iea_pending_work = ieaPendingWork,
                iea_pending_instruments = ieaPendingInstruments,
                iea_pending_age_seconds = Math.Round(pendingAgeSeconds, 3),
                stale_release_seconds = PlaybackStallQuiesceIeaStaleReleaseSeconds,
                callback_ingress_queued = execIngressQueued,
                order_ingress_queued = orderIngressQueued,
                note = "Playback stall quiescence refused to arm because serialized IEA work is still pending or in-flight."
            }));
    }

    private void RequestConnectivityHealthShutdown(string reason, DateTimeOffset utcNow, Dictionary<string, object> payload)
    {
        if (IsShutdownRequested || Volatile.Read(ref _shutdownCompleted) != 0)
            return;

        if (ShouldDeferConnectivityHealthShutdown(reason, utcNow, out var suppressionPayload))
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                eventType: "CONNECTIVITY_SHUTDOWN_DEFERRED", state: "ENGINE", suppressionPayload));
            return;
        }

        if (Interlocked.Exchange(ref _connectivityShutdownStopRequested, 1) != 0)
            return;

        var eventPayload = new Dictionary<string, object>(payload ?? new Dictionary<string, object>())
        {
            ["reason"] = reason,
            ["source"] = "health_monitor_connectivity_guard"
        };

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
            eventType: "CONNECTIVITY_SHUTDOWN_REQUESTED", state: "ENGINE", eventPayload));

        LatchRunWideShutdownSignal(utcNow, reason, "health_monitor_connectivity_guard");

        ThreadPool.QueueUserWorkItem(static state =>
        {
            try
            {
                ((RobotEngine)state!).Stop();
            }
            catch
            {
                // Best-effort connectivity stop; never throw on thread pool.
            }
        }, this);
    }

    private bool ShouldDeferConnectivityHealthShutdown(
        string reason,
        DateTimeOffset utcNow,
        out Dictionary<string, object> suppressionPayload)
    {
        suppressionPayload = new Dictionary<string, object>
        {
            ["reason"] = reason,
            ["source"] = "engine_connectivity_guard"
        };

        if (!string.Equals(reason, "DISCONNECT_CLASSIFIED_DATA_LOSS_PLAYBACK_IMMEDIATE", StringComparison.Ordinal) &&
            !string.Equals(reason, "DISCONNECT_CLASSIFIED_DATA_LOSS_BURST", StringComparison.Ordinal))
            return false;

        AccountSnapshot? snap = null;
        try
        {
            snap = _executionAdapter?.GetAccountSnapshot(utcNow);
        }
        catch (Exception ex)
        {
            suppressionPayload["snapshot_error"] = ex.GetType().Name;
            return false;
        }

        if (snap == null)
        {
            suppressionPayload["snapshot_available"] = false;
            return false;
        }

        var brokerPositionGross = 0;
        if (snap.Positions != null)
        {
            foreach (var p in snap.Positions)
            {
                if (p == null)
                    continue;
                brokerPositionGross += Math.Abs(p.Quantity);
            }
        }

        var robotWorkingOrders = 0;
        var totalWorkingOrders = 0;
        if (snap.WorkingOrders != null)
        {
            totalWorkingOrders = snap.WorkingOrders.Count;
            foreach (var order in snap.WorkingOrders)
            {
                if (order != null && IsRobotOwnedOrder(order))
                    robotWorkingOrders++;
            }
        }

        suppressionPayload["snapshot_available"] = true;
        suppressionPayload["broker_position_gross"] = brokerPositionGross;
        suppressionPayload["robot_working_orders"] = robotWorkingOrders;
        suppressionPayload["total_working_orders"] = totalWorkingOrders;
        suppressionPayload["note"] = "Connectivity shutdown deferred because broker still shows live exposure or robot-owned working orders. Allow flatten/protective lifecycle to continue instead of terminating the run mid-trade.";

        return brokerPositionGross > 0 || robotWorkingOrders > 0;
    }

    private void TryForcePlaybackStallQuiescenceFinalize(
        DateTimeOffset utcNow,
        int execIngressQueued,
        int orderIngressQueued,
        double stallIdleSeconds)
    {
        if (Volatile.Read(ref _shutdownCompleted) != 0)
            return;

        if (Interlocked.Exchange(ref _playbackStallQuiesceForceFinalizeRequested, 1) != 0)
            return;

        Interlocked.Exchange(ref _shutdownRequested, 1);
        StopEngineHeartbeatTimer();
        LatchRunWideShutdownSignal(utcNow, "playback_stall_force_finalize", "playback_stall_quiesce");

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ENGINE_PLAYBACK_STALL_QUIESCENCE_FORCE_FINALIZE", state: "ENGINE",
            new
            {
                source = "playback_stall_quiesce",
                idle_since_utc = _playbackStallQuiesceEligibleSinceUtc.ToString("o"),
                stop_requested_utc = _playbackStallQuiesceStopRequestedUtc.ToString("o"),
                idle_duration_seconds = Math.Round(stallIdleSeconds, 3),
                callback_ingress_queued = execIngressQueued,
                order_ingress_queued = orderIngressQueued,
                force_finalize_after_seconds = PlaybackStallQuiesceForceFinalizeSeconds,
                note = "Clean stop did not complete inside fallback window; writing best-effort run summary and halting heartbeat timer."
            }));

        TryWritePlaybackStallForcedRunSummary(utcNow);
        Volatile.Write(ref _shutdownCompleted, 1);
    }

    private void TryForcePlaybackStallLiveExposureFinalize(
        DateTimeOffset utcNow,
        int brokerPositionGross,
        int robotWorkingOrders,
        int totalWorkingOrders,
        string positionInstruments,
        string robotWorkingInstruments,
        double exposureDeferSeconds,
        int execIngressQueued,
        int orderIngressQueued)
    {
        if (Volatile.Read(ref _shutdownCompleted) != 0)
            return;

        if (Interlocked.Exchange(ref _playbackStallQuiesceForceFinalizeRequested, 1) != 0)
            return;

        Interlocked.Exchange(ref _shutdownRequested, 1);
        StopEngineHeartbeatTimer();
        LatchRunWideShutdownSignal(utcNow, "playback_stall_live_exposure_timeout", "playback_stall_quiesce");

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
            eventType: "ENGINE_PLAYBACK_STALL_QUIESCENCE_FORCE_FINALIZE_LIVE_EXPOSURE", state: "ENGINE",
            new
            {
                source = "playback_stall_quiesce",
                broker_position_gross = brokerPositionGross,
                robot_working_orders = robotWorkingOrders,
                total_working_orders = totalWorkingOrders,
                position_instruments = positionInstruments,
                robot_working_instruments = robotWorkingInstruments,
                exposure_defer_seconds = Math.Round(exposureDeferSeconds, 3),
                force_finalize_after_seconds = PlaybackStallQuiesceLiveExposureForceFinalizeSeconds,
                callback_ingress_queued = execIngressQueued,
                order_ingress_queued = orderIngressQueued,
                note = "Playback stall remained blocked by live broker exposure or robot-owned working orders after the timeout. Finalizing the run as unsafe instead of leaving the engine in an endless heartbeat loop."
            }));

        TryWritePlaybackStallForcedRunSummary(utcNow);
        Volatile.Write(ref _shutdownCompleted, 1);
    }

    private void TryWritePlaybackStallForcedRunSummary(DateTimeOffset utcNow)
    {
        try
        {
            _runtimeAudit?.EmitEngineAuditSummary(utcNow, TradingDateString);
        }
        catch
        {
            // Best-effort emergency finalize.
        }

        try
        {
            var summarySnap = _executionSummary.GetSnapshot();
            summarySnap = HydrateExecutionSummaryFromKeyEventsIfEmpty(_persistenceBase, summarySnap);

            if (_isolatedPlaybackPersistence)
            {
                var isolatedInstruments = new List<string>();
                if (_spec?.instruments != null)
                {
                    isolatedInstruments.AddRange(_spec.instruments.Keys
                        .Select(k => k.ToUpperInvariant())
                        .OrderBy(x => x, StringComparer.Ordinal));
                }

                RunRootArtifacts.WriteRunSummaryJson(
                    _persistenceBase,
                    _root,
                    _runId,
                    _engineStartUtc,
                    _executionMode,
                    isolatedInstruments,
                    summarySnap);
            }
        }
        catch
        {
            // Best-effort emergency finalize.
        }

        try
        {
            RunRootArtifacts.WriteAuditManifestJson(
                _persistenceBase,
                _runId,
                _engineStartUtc,
                TradingDateString,
                _isolatedPlaybackPersistence,
                FeatureFlags.CanonicalOwnershipLedgerEnabled,
                FeatureFlags.UnifiedExecutionAuthorityShadowEnabled,
                FeatureFlags.UnifiedExecutionAuthorityEnabled,
                FeatureFlags.ReconciliationRepairExecutorEnabled,
                FeatureFlags.StructuralLayerUseLedgerOwnership);
        }
        catch
        {
            // Best-effort emergency finalize.
        }
    }

    private IReadOnlyList<string> GetPlaybackStallLiveRecoveryExecutionInstrumentKeys(AccountSnapshot snap)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_engineLock)
        {
            foreach (var s in _streams.Values)
            {
                if (!DoesStreamBlockPlaybackStallQuiescence(s, snap))
                    continue;

                var ex = s.ExecutionInstrument?.Trim();
                if (!string.IsNullOrEmpty(ex))
                    set.Add(ex);
            }
        }

        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool DoesStreamBlockPlaybackStallQuiescence(StreamStateMachine stream, AccountSnapshot snap)
    {
        if (stream.Committed)
            return false;

        if (stream.ExecutionInterruptedByClose)
            return true;

        if (stream.State != StreamState.RANGE_LOCKED)
            return true;

        var instrument = stream.ExecutionInstrument?.Trim();
        if (string.IsNullOrEmpty(instrument))
            return true;

        return SumBrokerPositionQty(snap, instrument) != 0 ||
               CountBrokerWorkingOrders(snap, instrument) != 0;
    }
}
