using System;
using System.Collections.Generic;
using System.Threading;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Execution;

public sealed partial class RobotEngine
{
    /// <summary>
    /// Start timer-based engine heartbeat. Called when strategy enters Realtime.
    /// Timer fires every 1s to drive RuntimeAuditHub sampling; ENGINE_CPU_PROFILE emits at the hub interval (5s wall window); ENGINE_TIMER_HEARTBEAT stays every 5s for watchdog volume parity.
    /// </summary>
    public void StartEngineHeartbeatTimer()
    {
        lock (_engineLock)
        {
            _engineHeartbeatTimer?.Dispose();
            _engineHeartbeatTimer = null;
            _heartbeatTradingDateCache = TradingDateString;
            _engineHeartbeatWallTick = 0;
            _engineHeartbeatTimer = new Timer(
                _ => EmitTimerHeartbeatUnsafe(),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Stop and dispose the engine heartbeat timer. Called on engine Stop or StandDown.
    /// </summary>
    public void StopEngineHeartbeatTimer()
    {
        lock (_engineLock)
        {
            _engineHeartbeatTimer?.Dispose();
            _engineHeartbeatTimer = null;
        }
    }

    /// <summary>
    /// Emit ENGINE_TIMER_HEARTBEAT from timer callback. Must not acquire _engineLock (runs on thread pool).
    /// </summary>
    private void EmitTimerHeartbeatUnsafe()
    {
        var utcNow = DateTimeOffset.UtcNow;
        if (TryRespectRunWideShutdownSignal(utcNow, "engine_timer"))
            return;
        if (IsTerminalShutdownLatched()) return;
        try
        {
            _runtimeAudit?.TryEmitPeriodicWallClock(utcNow);
            var tick = Interlocked.Increment(ref _engineHeartbeatWallTick);
            if (tick % 5 == 0)
            {
                var tradingDate = _heartbeatTradingDateCache ?? "";
                if (_executionAdapter is NinjaTraderSimAdapter simHb)
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDate, eventType: "ENGINE_TIMER_HEARTBEAT", state: "ENGINE",
                        new
                        {
                            source = "engine_timer",
                            session_identity_latched = simHb.IsSessionIdentityLatched,
                            session_identity_block_count = simHb.SessionIdentityBlockCount,
                            timetable_hash = _currentTimetableHash,
                            trading_date = tradingDate
                        }));
                }
                else
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDate, eventType: "ENGINE_TIMER_HEARTBEAT", state: "ENGINE",
                        new { source = "engine_timer", timetable_hash = _currentTimetableHash, trading_date = tradingDate }));
            }
            if (TryHandlePlaybackStallQuiescence(utcNow, requestStopIfEligible: true))
                return;
            if (_executionPolicy?.UseInstrumentExecutionAuthority == true && !string.IsNullOrEmpty(_accountName))
            {
                var liveRecoveryScope = GetLiveRecoveryExecutionInstrumentKeys();
                if (liveRecoveryScope.Count > 0)
                {
                    var scopeSet = new HashSet<string>(liveRecoveryScope, StringComparer.OrdinalIgnoreCase);
                    InstrumentExecutionAuthorityRegistry.RetryDeferredAdoptionScansForAccount(
                        _accountName!,
                        _log,
                        executionInstrumentKey => scopeSet.Contains(executionInstrumentKey));
                }
            }
            else
                _executionAdapter?.TryRetryDeferredAdoptionScan();
        }
        catch (Exception ex)
        {
            try { _log.Write(new { ts_utc = DateTimeOffset.UtcNow.ToString("o"), event_type = "ENGINE_TIMER_HEARTBEAT_ERROR", error = ex.Message }); } catch { /* must not throw */ }
        }
    }
}
