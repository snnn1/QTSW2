using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Core.Notifications;

namespace QTSW2.Robot.Core;

/// <summary>
/// Health monitor: detects connection loss and data loss, sends Pushover notifications.
/// Simplified to only monitor external observable facts: connection status and bar arrivals.
/// </summary>
public sealed class HealthMonitor
{
    private readonly HealthMonitorConfig _config;
    private readonly RobotLogger _log;
    private readonly NotificationService? _notificationService;
    
    // Data loss tracking: timestamp of last received bar per instrument
    private readonly Dictionary<string, DateTimeOffset> _lastBarUtcByInstrument = new(StringComparer.OrdinalIgnoreCase);
    
    // PHASE 3: Engine heartbeat and timetable poll tracking
    private DateTimeOffset _lastEngineTickUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTimetablePollUtc = DateTimeOffset.MinValue;
    
    // PHASE 4: Session-aware monitoring state
    private readonly Dictionary<string, bool> _instrumentHasReceivedData = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _instrumentFirstBarUtc = new(StringComparer.OrdinalIgnoreCase);
    
    // Incident state
    private readonly Dictionary<string, bool> _dataStallActive = new(StringComparer.OrdinalIgnoreCase);
    private bool _connectionLostActive = false;
    private bool _engineTickStallActive = false;
    private bool _timetablePollStallActive = false;
    
    // Rate limiting
    private readonly Dictionary<string, DateTimeOffset> _lastNotifyUtcByKey = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastEvaluateUtc = DateTimeOffset.MinValue;
    private const int EVALUATE_RATE_LIMIT_SECONDS = 1; // Max 1 evaluation per second
    
    // Background evaluation thread for data loss detection
    private CancellationTokenSource? _evaluationCancellationTokenSource;
    private Task? _evaluationTask;
    private const int EVALUATION_INTERVAL_SECONDS = 5; // Evaluate every 5 seconds
    
    // PHASE 3: Stall detection thresholds
    private const int ENGINE_TICK_STALL_SECONDS = 30; // Engine tick should occur at least every 30 seconds
    private const int TIMETABLE_POLL_STALL_SECONDS = 60; // Timetable poll should occur at least every 60 seconds
    
    public HealthMonitor(string projectRoot, HealthMonitorConfig config, RobotLogger log)
    {
        _config = config;
        _log = log;
        
        if (config.pushover_enabled && !string.IsNullOrWhiteSpace(config.pushover_user_key) && !string.IsNullOrWhiteSpace(config.pushover_app_token))
        {
            _notificationService = NotificationService.GetOrCreate(projectRoot, config);
        }
    }
    
    /// <summary>
    /// Handle NinjaTrader connection status update.
    /// Triggers immediate alert on first transition to disconnected state.
    /// </summary>
    public void OnConnectionStatusUpdate(ConnectionStatus status, string connectionName, DateTimeOffset utcNow)
    {
        if (!_config.enabled)
            return;
        
        bool isDisconnected = status == ConnectionStatus.ConnectionLost ||
                             status == ConnectionStatus.Disconnected ||
                             status == ConnectionStatus.ConnectionError;
        
        if (isDisconnected)
        {
            if (!_connectionLostActive)
            {
                // First detection: mark active and notify
                _connectionLostActive = true;
                
                var title = "Connection Lost";
                var message = $"NinjaTrader connection lost: {connectionName}. Status: {status}";
                
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CONNECTION_LOST", state: "ENGINE",
                    new
                    {
                        connection_name = connectionName,
                        connection_status = status.ToString(),
                        timestamp_utc = utcNow.ToString("o")
                    }));
                
                SendNotification("CONNECTION_LOST", title, message, priority: 2); // Emergency priority
            }
        }
        else
        {
            // Connection restored: clear flag and log recovery (no notification)
            if (_connectionLostActive)
            {
                _connectionLostActive = false;
                
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CONNECTION_RECOVERED", state: "ENGINE",
                    new
                    {
                        connection_name = connectionName,
                        connection_status = status.ToString(),
                        timestamp_utc = utcNow.ToString("o")
                    }));
            }
        }
    }
    
    /// <summary>
    /// Record bar reception for an instrument.
    /// </summary>
    public void OnBar(string instrument, DateTimeOffset barUtc)
    {
        _lastBarUtcByInstrument[instrument] = barUtc;
        
        // PHASE 4: Track that instrument has received initial data
        if (!_instrumentHasReceivedData.ContainsKey(instrument))
        {
            _instrumentHasReceivedData[instrument] = true;
            _instrumentFirstBarUtc[instrument] = barUtc;
        }
        
        // Clear data loss flag if it was active (recovery)
        if (_dataStallActive.TryGetValue(instrument, out var isActive) && isActive)
        {
            _dataStallActive[instrument] = false;
            
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "DATA_STALL_RECOVERED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    last_bar_utc = barUtc.ToString("o")
                }));
        }
    }
    
    /// <summary>
    /// PHASE 3: Update engine heartbeat timestamp.
    /// </summary>
    public void UpdateEngineTick(DateTimeOffset tickUtc)
    {
        _lastEngineTickUtc = tickUtc;
        
        // Clear engine tick stall flag if it was active (recovery)
        if (_engineTickStallActive)
        {
            _engineTickStallActive = false;
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "ENGINE_TICK_STALL_RECOVERED", state: "ENGINE",
                new { last_tick_utc = tickUtc.ToString("o") }));
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
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "TIMETABLE_POLL_STALL_RECOVERED", state: "ENGINE",
                new { last_poll_utc = pollUtc.ToString("o") }));
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
    /// </summary>
    private void EvaluateEngineTickStall(DateTimeOffset utcNow)
    {
        if (_lastEngineTickUtc == DateTimeOffset.MinValue)
            return; // Engine not started yet
        
        var elapsed = (utcNow - _lastEngineTickUtc).TotalSeconds;
        
        if (elapsed >= ENGINE_TICK_STALL_SECONDS)
        {
            if (!_engineTickStallActive)
            {
                _engineTickStallActive = true;
                
                var title = "Engine Tick Stall Detected";
                var message = $"Engine Tick() has not been called for {elapsed:F0}s. Last tick: {_lastEngineTickUtc:o} UTC. Threshold: {ENGINE_TICK_STALL_SECONDS}s";
                
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_TICK_STALL_DETECTED", state: "ENGINE",
                    new
                    {
                        last_tick_utc = _lastEngineTickUtc.ToString("o"),
                        elapsed_seconds = elapsed,
                        threshold_seconds = ENGINE_TICK_STALL_SECONDS
                    }));
                
                SendNotification("ENGINE_TICK_STALL", title, message, priority: 2); // Emergency priority
            }
        }
    }
    
    /// <summary>
    /// PHASE 3: Evaluate timetable poll stall.
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
                
                var title = "Timetable Poll Stall Detected";
                var message = $"Timetable polling has not occurred for {elapsed:F0}s. Last poll: {_lastTimetablePollUtc:o} UTC. Threshold: {TIMETABLE_POLL_STALL_SECONDS}s";
                
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "TIMETABLE_POLL_STALL_DETECTED", state: "ENGINE",
                    new
                    {
                        last_poll_utc = _lastTimetablePollUtc.ToString("o"),
                        elapsed_seconds = elapsed,
                        threshold_seconds = TIMETABLE_POLL_STALL_SECONDS
                    }));
                
                SendNotification("TIMETABLE_POLL_STALL", title, message, priority: 1); // High priority
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
    /// PHASE 4: Evaluate data stalls (session-aware).
    /// Only checks during active monitoring windows (relaxed outside sessions).
    /// </summary>
    private void EvaluateDataStalls(DateTimeOffset utcNow)
    {
        // Check each instrument that has received bars
        var instrumentsToCheck = new List<string>(_lastBarUtcByInstrument.Keys);
        
        foreach (var instrument in instrumentsToCheck)
        {
            var lastBarUtc = _lastBarUtcByInstrument[instrument];
            var elapsed = (utcNow - lastBarUtc).TotalSeconds;
            
            // PHASE 4: Session-aware check - for now, use standard threshold
            // TODO: Adjust threshold based on active session windows
            var threshold = _config.data_stall_seconds;
            
            if (elapsed >= threshold)
            {
                if (!_dataStallActive.TryGetValue(instrument, out var isActive) || !isActive)
                {
                    // First detection: mark active and notify
                    _dataStallActive[instrument] = true;
                    
                    var title = $"Data Loss: {instrument}";
                    var message = $"No bars received for {instrument}. Last bar: {lastBarUtc:o} UTC ({elapsed:F0}s ago). Threshold: {threshold}s";
                    
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DATA_LOSS_DETECTED", state: "ENGINE",
                        new
                        {
                            instrument = instrument,
                            last_bar_utc = lastBarUtc.ToString("o"),
                            elapsed_seconds = elapsed,
                            threshold_seconds = threshold
                        }));
                    
                    SendNotification($"DATA_LOSS:{instrument}", title, message, priority: 1); // High priority
                }
            }
        }
    }
    
    private void SendNotification(string key, string title, string message, int priority)
    {
        if (_notificationService == null)
            return;
        
        // Check rate limit
        if (_lastNotifyUtcByKey.TryGetValue(key, out var lastNotify))
        {
            var timeSinceLastNotify = (DateTimeOffset.UtcNow - lastNotify).TotalSeconds;
            if (timeSinceLastNotify < _config.min_notify_interval_seconds)
                return; // Rate limited
        }
        
        _lastNotifyUtcByKey[key] = DateTimeOffset.UtcNow;
        
        // Enqueue notification (non-blocking)
        _notificationService.EnqueueNotification(key, title, message, priority);
        
        // Log notification enqueued
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "PUSHOVER_NOTIFY_ENQUEUED", state: "ENGINE",
            new
            {
                notification_key = key,
                title = title,
                message = message,
                priority = priority,
                pushover_configured = true,
                note = "Notification enqueued (actual send handled by background worker, check notification_errors.log for send results)"
            }));
    }
    
    /// <summary>
    /// Start the notification service background worker and evaluation thread.
    /// </summary>
    public void Start()
    {
        _notificationService?.Start();
        
        // Start background evaluation thread for data loss detection
        if (_evaluationTask == null || _evaluationTask.IsCompleted)
        {
            if (_evaluationCancellationTokenSource != null && !_evaluationCancellationTokenSource.IsCancellationRequested)
            {
                _evaluationCancellationTokenSource.Cancel();
                _evaluationCancellationTokenSource.Dispose();
            }
            
            _evaluationCancellationTokenSource = new CancellationTokenSource();
            _evaluationTask = Task.Run(() => EvaluationLoop(_evaluationCancellationTokenSource.Token));
        }
        
        // Log health monitor started
        var now = DateTimeOffset.UtcNow;
        _log.Write(RobotEvents.EngineBase(now, tradingDate: "", eventType: "HEALTH_MONITOR_STARTED", state: "ENGINE",
            new
            {
                enabled = _config.enabled,
                data_stall_seconds = _config.data_stall_seconds,
                min_notify_interval_seconds = _config.min_notify_interval_seconds,
                pushover_configured = _config.pushover_enabled && !string.IsNullOrWhiteSpace(_config.pushover_user_key) && !string.IsNullOrWhiteSpace(_config.pushover_app_token),
                pushover_priority = _config.pushover_priority,
                evaluation_thread_started = true
            }));
    }
    
    /// <summary>
    /// Background evaluation loop - runs independently to check for data loss.
    /// </summary>
    private void EvaluationLoop(CancellationToken cancellationToken)
    {
        Thread.CurrentThread.IsBackground = true;
        Thread.CurrentThread.Name = "HealthMonitor-Evaluation";
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var utcNow = DateTimeOffset.UtcNow;
                Evaluate(utcNow); // Evaluate data loss and trigger alerts if needed
                
                // Sleep for evaluation interval
                Thread.Sleep(TimeSpan.FromSeconds(EVALUATION_INTERVAL_SECONDS));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log error but continue monitoring
                try
                {
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HEALTH_MONITOR_EVALUATION_ERROR", state: "ENGINE",
                        new { error = ex.Message, error_type = ex.GetType().Name, stack_trace = ex.StackTrace }));
                }
                catch
                {
                    // If logging fails, just continue
                }
                
                // Sleep before retrying
                Thread.Sleep(TimeSpan.FromSeconds(EVALUATION_INTERVAL_SECONDS));
            }
        }
    }
    
    /// <summary>
    /// Stop the notification service and evaluation thread.
    /// </summary>
    public void Stop()
    {
        _notificationService?.Stop();
        
        // Stop evaluation thread
        if (_evaluationCancellationTokenSource != null)
        {
            _evaluationCancellationTokenSource.Cancel();
            try
            {
                _evaluationTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore timeout/errors during shutdown
            }
            _evaluationCancellationTokenSource.Dispose();
            _evaluationCancellationTokenSource = null;
        }
        _evaluationTask = null;
    }
    
    /// <summary>
    /// Get notification service for external use (e.g., engine startup alerts).
    /// </summary>
    public NotificationService? GetNotificationService() => _notificationService;
}

/// <summary>
/// NinjaTrader connection status enumeration.
/// </summary>
public enum ConnectionStatus
{
    Connected,
    ConnectionLost,
    Disconnected,
    ConnectionError
}
