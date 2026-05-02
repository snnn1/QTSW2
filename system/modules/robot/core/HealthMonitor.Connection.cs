using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Core.Notifications;

namespace QTSW2.Robot.Core;

public sealed partial class HealthMonitor
{
    public void OnConnectionStatusUpdate(ConnectionStatus status, string connectionName, DateTimeOffset utcNow)
    {
        if (!_config.enabled)
            return;
        
        bool isDisconnected = status == ConnectionStatus.ConnectionLost ||
                             status == ConnectionStatus.Disconnected ||
                             status == ConnectionStatus.ConnectionError;
        
        if (isDisconnected)
        {
            // Update or create incident
            if (_currentIncident == null)
            {
                _currentIncident = new ConnectionIncident
                {
                    FirstDetectedUtc = utcNow,
                    LastStatus = status,
                    LastStatusUtc = utcNow,
                    InstanceCountAtDetection = 1,
                    TradingDateAtDetection = _currentTradingDate,
                    ConnectionNameAtDetection = connectionName
                };
                // Track for "4+ losses in 5 min" - mirrors NinjaTrader's strategy disable condition
                lock (_sharedConnectionLock)
                {
                    _connectionLossTimestamps.Add(utcNow);
                    var cutoff = utcNow.AddSeconds(-PRICE_CONNECTION_LOSS_WINDOW_SECONDS);
                    _connectionLossTimestamps.RemoveAll(t => t < cutoff);
                    if (_connectionLossTimestamps.Count >= PRICE_CONNECTION_LOSS_THRESHOLD &&
                        (utcNow - _lastPriceConnectionLossRepeatedLogUtc).TotalSeconds >= PRICE_CONNECTION_LOSS_WINDOW_SECONDS)
                    {
                        _lastPriceConnectionLossRepeatedLogUtc = utcNow;
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _currentTradingDate ?? "", eventType: "PRICE_CONNECTION_LOSS_REPEATED", state: "ENGINE",
                            new
                            {
                                connection_name = connectionName,
                                loss_count_in_window = _connectionLossTimestamps.Count,
                                window_seconds = PRICE_CONNECTION_LOSS_WINDOW_SECONDS,
                                note = "Connection lost 4+ times in 5 minutes. NinjaTrader may disable strategy with 'lost price connection more than 4 times in the past 5 minutes'."
                            }));

                        TrySignalConnectivityShutdown("PRICE_CONNECTION_LOSS_REPEATED", utcNow,
                            new Dictionary<string, object>
                            {
                                ["connection_name"] = connectionName,
                                ["loss_count_in_window"] = _connectionLossTimestamps.Count,
                                ["window_seconds"] = PRICE_CONNECTION_LOSS_WINDOW_SECONDS,
                                ["trading_date"] = _currentTradingDate ?? "",
                                ["note"] = "Repeated connection loss crossed the NinjaTrader disable threshold. Prefer controlled engine stop before platform-side strategy disable cascade."
                            });
                    }
                }
            }
            else
            {
                // Update existing incident
                _currentIncident.LastStatus = status;
                _currentIncident.LastStatusUtc = utcNow;
            }
            
            var incidentId = _currentIncident.GetIncidentId();
            if (!incidentId.HasValue)
                return; // Should not happen, but guard
            
            // Bucket incident ID by 60 seconds so multiple strategy instances (detecting at ms-apart) share the same key
            var sharedIncidentKey = incidentId.Value / TICKS_PER_60_SECONDS;
            
            // CRITICAL FIX: Use shared state keyed to bucketed incident ID to prevent duplicate logging across multiple strategy instances
            lock (_sharedConnectionLock)
            {
                if (!_sharedConnectionLostLoggedByIncident.TryGetValue(sharedIncidentKey, out var logged) || !logged)
                {
                    // First instance to detect: log the event and initialize shared state
                    _sharedConnectionLostLoggedByIncident[sharedIncidentKey] = true;
                    _sharedConnectionLostInstanceCountByIncident[sharedIncidentKey] = 1;
                    
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _currentTradingDate ?? "", eventType: "CONNECTION_LOST", state: "ENGINE",
                        new
                        {
                            connection_name = connectionName,
                            connection_status = status.ToString(),
                            timestamp_utc = utcNow.ToString("o"),
                            strategy_instance_count = 1,
                            incident_id = incidentId.Value,
                            trading_date = _currentTradingDate,
                            note = "Connection lost detected, waiting for sustained period before notification"
                        }));
                }
                else
                {
                    // Another instance detected the same disconnect: increment counter but don't log
                    if (!_sharedConnectionLostInstanceCountByIncident.TryGetValue(sharedIncidentKey, out var currentCount))
                    {
                        currentCount = 0;
                    }
                    _sharedConnectionLostInstanceCountByIncident[sharedIncidentKey] = currentCount + 1;
                }
            }
            
            // Check if disconnection has been sustained for threshold period
            if (_currentIncident.FirstDetectedUtc.HasValue && !_currentIncident.NotifiedUtc.HasValue)
            {
                var elapsed = (utcNow - _currentIncident.FirstDetectedUtc.Value).TotalSeconds;
                if (elapsed >= CONNECTION_LOST_SUSTAINED_SECONDS)
                {
                    // CRITICAL FIX: Only send notification once per disconnect (use shared state keyed to bucketed incident ID)
                    lock (_sharedConnectionLock)
                    {
                        if (!_sharedConnectionLostNotifiedByIncident.TryGetValue(sharedIncidentKey, out var notified) || !notified)
                        {
                            var sharedElapsed = (utcNow - _currentIncident.FirstDetectedUtc.Value).TotalSeconds;
                            if (sharedElapsed >= CONNECTION_LOST_SUSTAINED_SECONDS)
                            {
                                // Mark as notified in shared state to prevent duplicate notifications
                                _sharedConnectionLostNotifiedByIncident[sharedIncidentKey] = true;
                                _currentIncident.NotifiedUtc = utcNow;
                                
                                if (!_sharedConnectionLostInstanceCountByIncident.TryGetValue(sharedIncidentKey, out var instanceCount))
                                {
                                    instanceCount = 1;
                                }
                                var hasActiveStreams = HasActiveStreams();
                                var title = "Connection Lost (Sustained)";
                                var message = $"NinjaTrader connection lost for {sharedElapsed:F0}s: {connectionName}. Status: {status}. " +
                                             $"Active streams: {(hasActiveStreams ? "Yes" : "No")}. " +
                                             $"Detected by {instanceCount} strategy instance(s).";
                                
                                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _currentTradingDate ?? "", eventType: "CONNECTION_LOST_SUSTAINED", state: "ENGINE",
                                    new
                                    {
                                        connection_name = connectionName,
                                        connection_status = status.ToString(),
                                        elapsed_seconds = sharedElapsed,
                                        timestamp_utc = utcNow.ToString("o"),
                                        has_active_streams = hasActiveStreams,
                                        strategy_instance_count = instanceCount,
                                        incident_id = incidentId.Value,
                                        trading_date = _currentTradingDate,
                                        note = "Connection loss notification sent regardless of stream state (critical infrastructure issue)"
                                    }));
                                
                                // NOTE: CONNECTION_LOST notifications intentionally bypass the emergency rate limiter.
                                // Each sustained disconnect is treated as a distinct operational incident and must notify immediately.
                                // Deduplication is handled per incident ID (via _sharedConnectionLostNotifiedByIncident) to prevent
                                // duplicate notifications when multiple strategy instances detect the same disconnect.
                                SendNotification("CONNECTION_LOST", title, message, priority: 2, skipPerKeyRateLimit: true); // Intentional: incident-based notification, bypasses rate limiter
                            }
                        }
                    }
                }
            }
        }
        else
        {
            // Connection restored: check if we should send recovery notification
            if (_currentIncident != null && _currentIncident.FirstDetectedUtc.HasValue)
            {
                var downtime = (utcNow - _currentIncident.FirstDetectedUtc.Value).TotalSeconds;
                
                // Canonical rule: Send recovered notification if downtime >= threshold, regardless of whether "lost sustained" was sent
                if (downtime >= CONNECTION_LOST_SUSTAINED_SECONDS && !_currentIncident.RecoveredNotifiedUtc.HasValue)
                {
                    var incidentId = _currentIncident.GetIncidentId();
                    if (incidentId.HasValue)
                    {
                        // Check per-event-type rate limiter for CONNECTION_RECOVERED
                        bool shouldSendRecovered = true;
                        if (_notificationService != null)
                        {
                            shouldSendRecovered = _notificationService.ShouldSendEmergencyNotification("CONNECTION_RECOVERED");
                        }
                        
                        if (shouldSendRecovered)
                        {
                            _currentIncident.RecoveredNotifiedUtc = utcNow;
                            
                            var hasActiveStreams = HasActiveStreams();
                            var title = "Connection Recovered";
                            var message = $"Recovered after {downtime:F0}s. Connection: {_currentIncident.ConnectionNameAtDetection}. " +
                                         $"Active streams: {(hasActiveStreams ? "Yes" : "No")}. " +
                                         $"Recovery state: {status}.";
                            
                            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _currentTradingDate ?? "", eventType: "CONNECTION_RECOVERED_NOTIFICATION", state: "ENGINE",
                                new
                                {
                                    connection_name = _currentIncident.ConnectionNameAtDetection,
                                    connection_status = status.ToString(),
                                    downtime_seconds = downtime,
                                    timestamp_utc = utcNow.ToString("o"),
                                    has_active_streams = hasActiveStreams,
                                    incident_id = incidentId.Value,
                                    trading_date = _currentTradingDate,
                                    note = "Connection recovered after sustained disconnect"
                                }));
                            
                            SendNotification("CONNECTION_RECOVERED", title, message, priority: 2, skipPerKeyRateLimit: true); // Emergency priority, respects per-event-type limiter
                        }
                    }
                }
                
                // Clear incident state (or mark resolved)
                // Clean up shared state for this incident (use same bucketed key as for add)
                var incidentIdToClean = _currentIncident.GetIncidentId();
                if (incidentIdToClean.HasValue)
                {
                    var sharedKeyToClean = incidentIdToClean.Value / TICKS_PER_60_SECONDS;
                    lock (_sharedConnectionLock)
                    {
                        _sharedConnectionLostLoggedByIncident.Remove(sharedKeyToClean);
                        _sharedConnectionLostNotifiedByIncident.Remove(sharedKeyToClean);
                        _sharedConnectionLostInstanceCountByIncident.Remove(sharedKeyToClean);
                    }
                }
                
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _currentTradingDate ?? "", eventType: "CONNECTION_RECOVERED", state: "ENGINE",
                    new
                    {
                        connection_name = connectionName,
                        connection_status = status.ToString(),
                        timestamp_utc = utcNow.ToString("o"),
                        trading_date = _currentTradingDate
                    }));
                
                _currentIncident = null; // Clear incident - if it flaps again, it becomes a new incident
            }
        }
    }
    
    /// <summary>
    /// Called when a mid-session restart is detected (NinjaTrader/robot restarted while stream was active).
    /// Sends push notification because OnConnectionStatusUpdate never fires when the process crashes.
    /// </summary>
    public void OnMidSessionRestartDetected(string streamId, string tradingDate, string previousState, DateTimeOffset previousUpdateUtc, DateTimeOffset restartUtc)
    {
        if (!_config.enabled || _notificationService == null)
            return;
        
        var title = "NinjaTrader Mid-Session Restart";
        var message = $"Robot restarted mid-session. Stream: {streamId}, trading date: {tradingDate}. " +
                      $"Previous state: {previousState}. Restart at {restartUtc:HH:mm} UTC.";
        
        _log.Write(RobotEvents.EngineBase(restartUtc, tradingDate: tradingDate ?? "", eventType: "MID_SESSION_RESTART_NOTIFICATION", state: "ENGINE",
            new
            {
                stream_id = streamId,
                trading_date = tradingDate,
                previous_state = previousState,
                previous_update_utc = previousUpdateUtc.ToString("o"),
                restart_utc = restartUtc.ToString("o"),
                note = "Push notification sent - OnConnectionStatusUpdate does not fire when process crashes"
            }));
        
        SendNotification("MID_SESSION_RESTART", title, message, priority: 2, skipPerKeyRateLimit: true);
    }
    
    /// <summary>
    /// Record bar reception for an instrument.
    /// </summary>
}
