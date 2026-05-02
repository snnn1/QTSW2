using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Core.Notifications;

namespace QTSW2.Robot.Core;

public sealed partial class HealthMonitor
{
    public void UpdateEngineTick(DateTimeOffset tickUtc)
    {
        UpdateEngineTick(tickUtc, DateTimeOffset.UtcNow);
    }

    public void UpdateEngineTick(DateTimeOffset tickUtc, DateTimeOffset observedUtc)
    {
        _lastEngineTickUtc = tickUtc;
        _lastEngineTickObservedUtc = observedUtc;
        
        // Clear engine tick stall flag if it was active (recovery)
        if (_engineTickStallActive)
        {
            _engineTickStallActive = false;
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "ENGINE_TICK_STALL_RECOVERED", state: "ENGINE",
                new
                {
                    last_tick_utc = tickUtc.ToString("o"),
                    last_tick_observed_utc = observedUtc.ToString("o"),
                    time_basis = _playbackTelemetryMode ? "playback_wall_clock_observed" : "event_clock"
                }));
        }
    }
    
    /// <summary>
    /// PHASE 3: Update timetable poll timestamp.
    /// </summary>
    public void UpdateTimetablePoll(DateTimeOffset pollUtc)
    {
        _lastTimetablePollUtc = pollUtc;
        
        // Clear timetable poll stall flag if it was active (recovery)
        if (_timetablePollStallActive)
        {
            _timetablePollStallActive = false;
            var shouldLogRecovery = false;
            lock (_sharedTimetablePollLock)
            {
                if (_sharedTimetablePollRecoveryLoggedForPollTicks != pollUtc.UtcTicks)
                {
                    _sharedTimetablePollRecoveryLoggedForPollTicks = pollUtc.UtcTicks;
                    shouldLogRecovery = true;
                }
            }

            if (shouldLogRecovery)
            {
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "TIMETABLE_POLL_STALL_RECOVERED", state: "ENGINE",
                    new { last_poll_utc = pollUtc.ToString("o") }));
            }
        }
    }
    
    /// <summary>
    /// Evaluate data loss and trigger alerts if needed.
    /// Rate-limited internally to max 1/sec.
    /// </summary>
    public void Evaluate(DateTimeOffset utcNow)
    {
        if (!_config.enabled)
            return;
        
        // Rate limit evaluation
        var timeSinceLastEvaluate = (utcNow - _lastEvaluateUtc).TotalSeconds;
        if (timeSinceLastEvaluate < EVALUATE_RATE_LIMIT_SECONDS)
            return;
        
        _lastEvaluateUtc = utcNow;
        
        // PHASE 3: Check engine tick stall
        EvaluateEngineTickStall(utcNow);
        
        // PHASE 3: Check timetable poll stall
        EvaluateTimetablePollStall(utcNow);
        
        // PHASE 4: Check data stalls (session-aware)
        EvaluateDataStalls(utcNow);
        
        // PHASE 4: Check for "never received initial data" (session-aware)
        EvaluateInitialDataMissing(utcNow);
    }
    
    /// <summary>
    /// PHASE 3: Evaluate engine tick stall.
    /// Session-aware: only evaluates during active trading windows.
    /// </summary>
    private void EvaluateEngineTickStall(DateTimeOffset utcNow)
    {
        if (_lastEngineTickUtc == DateTimeOffset.MinValue)
            return; // Engine not started yet
        
        // Session awareness: only check during active trading
        if (!HasActiveStreams())
            return; // Suppress during startup/shutdown/outside sessions

        if (_isMarketOpenCallback != null && !_isMarketOpenCallback())
            return;

        var elapsedReferenceUtc = _playbackTelemetryMode && _lastEngineTickObservedUtc != DateTimeOffset.MinValue
            ? _lastEngineTickObservedUtc
            : _lastEngineTickUtc;
        var elapsed = Math.Max(0.0, (utcNow - elapsedReferenceUtc).TotalSeconds);
        if (elapsed < ENGINE_TICK_STALL_SECONDS)
        {
            _engineTickStallConsecutiveCount = 0;
            return;
        }

        _engineTickStallConsecutiveCount++;
        if (_engineTickStallConsecutiveCount < ENGINE_TICK_STALL_HYSTERESIS_COUNT)
            return;

        if (!_engineTickStallActive)
        {
            _engineTickStallActive = true;
            var inStartupGrace = _engineStartUtc != DateTimeOffset.MinValue &&
                (utcNow - _engineStartUtc).TotalSeconds < ENGINE_START_GRACE_PERIOD_SECONDS;
            var eventType = inStartupGrace
                ? "ENGINE_TICK_STALL_STARTUP"
                : _playbackTelemetryMode
                    ? "ENGINE_TICK_STALL_PLAYBACK"
                    : "ENGINE_TICK_STALL_RUNTIME";
            var causeHint = inStartupGrace ? "hydration_or_bars_loading" : _playbackTelemetryMode ? "isolated_playback_wallclock_gap" : null;

            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: eventType, state: "ENGINE",
                new
                {
                    last_tick_utc = _lastEngineTickUtc.ToString("o"),
                    last_tick_observed_utc = _lastEngineTickObservedUtc == DateTimeOffset.MinValue ? null : _lastEngineTickObservedUtc.ToString("o"),
                    elapsed_reference_utc = elapsedReferenceUtc.ToString("o"),
                    time_basis = _playbackTelemetryMode ? "playback_wall_clock_observed" : "event_clock",
                    elapsed_seconds = elapsed,
                    threshold_seconds = ENGINE_TICK_STALL_SECONDS,
                    consecutive_count = _engineTickStallConsecutiveCount,
                    cause_hint = causeHint,
                    in_startup_grace = inStartupGrace,
                    playback_mode = _playbackTelemetryMode
                }));

            if (!inStartupGrace && !_playbackTelemetryMode)
            {
                var title = "Engine Tick Stall Detected";
                var message = $"Engine Tick() has not been called for {elapsed:F0}s. Last tick: {_lastEngineTickUtc:o} UTC. Threshold: {ENGINE_TICK_STALL_SECONDS}s";
                SendNotification("ENGINE_TICK_STALL", title, message, priority: 2, skipPerKeyRateLimit: true);
            }
        }
    }
    
    /// <summary>
    /// PHASE 3: Evaluate timetable poll stall.
    /// DISABLED: Notifications suppressed (log only) - non-execution-critical.
    /// </summary>
    private void EvaluateTimetablePollStall(DateTimeOffset utcNow)
    {
        if (_lastTimetablePollUtc == DateTimeOffset.MinValue)
            return; // Timetable not polled yet
        
        var elapsed = (utcNow - _lastTimetablePollUtc).TotalSeconds;
        
        if (elapsed >= TIMETABLE_POLL_STALL_SECONDS)
        {
            if (!_timetablePollStallActive)
            {
                _timetablePollStallActive = true;
                var shouldLogDetected = false;
                lock (_sharedTimetablePollLock)
                {
                    if (_sharedTimetablePollStallLoggedForLastPollTicks != _lastTimetablePollUtc.UtcTicks)
                    {
                        _sharedTimetablePollStallLoggedForLastPollTicks = _lastTimetablePollUtc.UtcTicks;
                        shouldLogDetected = true;
                    }
                }

                if (shouldLogDetected)
                {
                    // LOG ONLY - no notification (non-execution-critical)
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "TIMETABLE_POLL_STALL_DETECTED", state: "ENGINE",
                        new
                        {
                            last_poll_utc = _lastTimetablePollUtc.ToString("o"),
                            elapsed_seconds = elapsed,
                            threshold_seconds = TIMETABLE_POLL_STALL_SECONDS,
                            note = "Notification suppressed - non-execution-critical (log only; shared-process dedupe enabled)"
                        }));
                }
                
                // NOTIFICATION DISABLED: Timetable poll stall is non-execution-critical
                // SendNotification("TIMETABLE_POLL_STALL", title, message, priority: 1);
            }
        }
    }
    
    /// <summary>
    /// PHASE 4: Evaluate initial data missing (never received first bar for instrument).
    /// This is session-aware and only checks during active monitoring windows.
    /// </summary>
    private void EvaluateInitialDataMissing(DateTimeOffset utcNow)
    {
        // TODO: Implement session-aware check using timetable/session definitions
        // For now, this is a placeholder - full implementation requires access to timetable and session windows
        // This will be called but won't trigger alerts until session-aware logic is implemented
    }
    
    /// <summary>
    /// PHASE 4: Evaluate data stalls (quant-grade: instrument-aware, debounced, activity-aware).
    /// </summary>
    private void EvaluateDataStalls(DateTimeOffset utcNow)
    {
        if (_playbackTelemetryMode)
        {
            // Playback bars are replay/chart time while utcNow is wall time. Engine tick stall
            // detection owns playback quiescence; per-instrument data loss would be false-positive.
            _dataStallActive.Clear();
            _stallDetectedAtUtc.Clear();
            _disconnectClassifiedDataLossUtcByInstrument.Clear();
            return;
        }

        var instrumentsToCheck = new List<string>(_lastBarUtcByInstrument.Keys);

        foreach (var instrument in instrumentsToCheck)
        {
            var lastBarUtc = _lastBarUtcByInstrument[instrument];
            var elapsed = (utcNow - lastBarUtc).TotalSeconds;

            var thresholdSeconds = _config.GetStallThresholdSeconds(instrument);
            var expectedInterval = GetExpectedIntervalSeconds(instrument);
            var debounceThreshold = expectedInterval > 0 ? Math.Max(thresholdSeconds, expectedInterval * 3) : thresholdSeconds;

            if (_recoveryCooldownUntil.TryGetValue(instrument, out var cooldownUntil) && utcNow < cooldownUntil)
                continue;

            var isLowActivity = IsLowActivity(instrument);
            if (isLowActivity && elapsed < thresholdSeconds * 2)
                continue;

            if (elapsed >= debounceThreshold)
            {
                if (!_dataStallActive.TryGetValue(instrument, out var isActive) || !isActive)
                {
                    _dataStallActive[instrument] = true;
                    _stallDetectedAtUtc[instrument] = utcNow;

                    var classification = ClassifyStall(instrument, elapsed, expectedInterval, thresholdSeconds, isLowActivity, utcNow);

                    var shouldLog = true;
                    if (_lastDataLossLogUtcByInstrument.TryGetValue(instrument, out var lastLogUtc))
                    {
                        var timeSinceLastLog = (utcNow - lastLogUtc).TotalMinutes;
                        shouldLog = timeSinceLastLog >= DATA_LOSS_LOG_RATE_LIMIT_MINUTES;
                    }

                    if (shouldLog)
                    {
                        _lastDataLossLogUtcByInstrument[instrument] = utcNow;

                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DATA_LOSS_DETECTED", state: "ENGINE",
                            new
                            {
                                instrument = instrument,
                                last_bar_utc = lastBarUtc.ToString("o"),
                                elapsed_seconds = Math.Round(elapsed, 1),
                                expected_interval = expectedInterval > 0 ? Math.Round(expectedInterval, 1) : (double?)null,
                                threshold_used = thresholdSeconds,
                                stall_classification = classification,
                                is_low_activity = isLowActivity,
                                note = "Notification suppressed - handled by gap tolerance + range invalidation (log only, rate-limited)"
                            }));
                    }

                    if (string.Equals(classification, "DISCONNECT", StringComparison.OrdinalIgnoreCase))
                    {
                        _disconnectClassifiedDataLossUtcByInstrument[instrument] = utcNow;

                        if (_playbackTelemetryMode && !IsInStartupGrace(utcNow))
                        {
                            TrySignalConnectivityShutdown("DISCONNECT_CLASSIFIED_DATA_LOSS_PLAYBACK_IMMEDIATE", utcNow,
                                new Dictionary<string, object>
                                {
                                    ["instrument"] = instrument,
                                    ["instrument_count"] = 1,
                                    ["window_seconds"] = CONNECTIVITY_SHUTDOWN_DATA_LOSS_WINDOW_SECONDS,
                                    ["active_streams"] = HasActiveStreams(),
                                    ["trading_date"] = _currentTradingDate ?? "",
                                    ["note"] = "Playback-mode disconnect-classified data loss detected. Prefer immediate controlled engine stop before NinjaTrader enters the disable/freeze cascade."
                                });
                        }

                        var cutoff = utcNow.AddSeconds(-CONNECTIVITY_SHUTDOWN_DATA_LOSS_WINDOW_SECONDS);
                        foreach (var staleInstrument in _disconnectClassifiedDataLossUtcByInstrument
                                     .Where(kvp => kvp.Value < cutoff)
                                     .Select(kvp => kvp.Key)
                                     .ToArray())
                        {
                            _disconnectClassifiedDataLossUtcByInstrument.Remove(staleInstrument);
                        }

                        var disconnectBurstInstruments = _disconnectClassifiedDataLossUtcByInstrument.Keys
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                            .ToArray();

                        if (disconnectBurstInstruments.Length >= CONNECTIVITY_SHUTDOWN_DATA_LOSS_THRESHOLD)
                        {
                            var hasActiveStreams = HasActiveStreams();
                            TrySignalConnectivityShutdown("DISCONNECT_CLASSIFIED_DATA_LOSS_BURST", utcNow,
                                new Dictionary<string, object>
                                {
                                    ["instrument_count"] = disconnectBurstInstruments.Length,
                                    ["instruments"] = disconnectBurstInstruments,
                                    ["window_seconds"] = CONNECTIVITY_SHUTDOWN_DATA_LOSS_WINDOW_SECONDS,
                                    ["active_streams"] = hasActiveStreams,
                                    ["trading_date"] = _currentTradingDate ?? "",
                                    ["note"] = hasActiveStreams
                                        ? "Multiple instruments entered disconnect-classified data loss while streams were still active. Prefer controlled engine stop before NinjaTrader mass-disables strategies."
                                        : "Multiple instruments entered disconnect-classified data loss in a short burst. Prefer controlled engine stop before NinjaTrader mass-disables strategies."
                                });
                        }
                    }
                }
            }
        }
    }

    private void TrySignalConnectivityShutdown(string reason, DateTimeOffset utcNow, Dictionary<string, object> payload)
    {
        if (_connectivityShutdownCallback == null)
            return;

        if ((utcNow - _lastConnectivityShutdownSignalUtc).TotalSeconds < CONNECTIVITY_SHUTDOWN_SIGNAL_COOLDOWN_SECONDS)
            return;

        _lastConnectivityShutdownSignalUtc = utcNow;
        _connectivityShutdownCallback.Invoke(reason, utcNow, payload);
    }

    private bool IsInStartupGrace(DateTimeOffset utcNow)
    {
        return _engineStartUtc != DateTimeOffset.MinValue &&
               (utcNow - _engineStartUtc).TotalSeconds < ENGINE_START_GRACE_PERIOD_SECONDS;
    }

    private bool IsLowActivity(string instrument)
    {
        if (_lastVolume.TryGetValue(instrument, out var vol) && vol == 0)
            return true;
        var expected = GetExpectedIntervalSeconds(instrument);
        return expected > 120;
    }

    private string ClassifyStall(string instrument, double elapsed, double expectedInterval, int thresholdSeconds, bool isLowActivity, DateTimeOffset utcNow)
    {
        if (isLowActivity && elapsed >= thresholdSeconds)
            return "LOW_LIQUIDITY";

        if (_currentIncident != null && _currentIncident.FirstDetectedUtc.HasValue)
        {
            var incidentAge = (utcNow - _currentIncident.FirstDetectedUtc.Value).TotalSeconds;
            var incidentStillDisconnected =
                _currentIncident.LastStatus == ConnectionStatus.ConnectionLost ||
                _currentIncident.LastStatus == ConnectionStatus.Disconnected ||
                _currentIncident.LastStatus == ConnectionStatus.ConnectionError;
            if (incidentStillDisconnected || incidentAge < 300)
                return "DISCONNECT";
        }

        var simultaneousCount = 0;
        foreach (var kv in _stallDetectedAtUtc)
        {
            if (kv.Key == instrument) continue;
            var age = (utcNow - kv.Value).TotalSeconds;
            if (age <= AGGREGATION_DELAY_WINDOW_SECONDS)
                simultaneousCount++;
        }
        if (simultaneousCount >= 1)
            return "AGGREGATION_DELAY";

        if (expectedInterval > 0 && elapsed >= expectedInterval * 2 && elapsed <= expectedInterval * 3 + 60)
            return "FEED_DELAY";

        return "DISCONNECT";
    }
    
    /// <summary>
    /// Report a critical event for notification.
    /// Only whitelisted event types are allowed: EXECUTION_GATE_INVARIANT_VIOLATION, DISCONNECT_FAIL_CLOSED_ENTERED
    /// Enforces: One notification per (eventType, run_id) to keep behavior deterministic and auditable.
    /// </summary>
    /// <param name="eventType">Event type (must be whitelisted)</param>
    /// <param name="payload">Event payload dictionary (for audit readability; dedupe is engine-run scoped)</param>
}
