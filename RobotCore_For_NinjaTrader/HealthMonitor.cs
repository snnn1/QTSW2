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
    private string? _runId; // Engine run identifier (GUID per engine start)
    private readonly object _notifyLock = new object(); // Guards dedupe + rate-limit state
    
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
    private bool _connectionLostNotified = false; // Track if notification already sent
    private bool _engineTickStallActive = false;
    private bool _timetablePollStallActive = false;
    
    // Session awareness: callback to check if any streams are in active trading state
    private Func<bool>? _hasActiveStreamsCallback;
    
    // Rate limiting
    private readonly Dictionary<string, DateTimeOffset> _lastNotifyUtcByKey = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastEvaluateUtc = DateTimeOffset.MinValue;
    private const int EVALUATE_RATE_LIMIT_SECONDS = 1; // Max 1 evaluation per second
    
    // Critical event notification tracking: one notification per (eventType, run_id)
    // Key format: "{eventType}:{run_id}" where run_id defaults to trading_date if not provided
    private readonly HashSet<string> _criticalNotificationsSent = new(StringComparer.OrdinalIgnoreCase);
    private bool _missingRunIdWarned = false; // Rate-limit missing run_id warning (once per process)
    
    // Whitelist of allowed critical event types
    private static readonly HashSet<string> ALLOWED_CRITICAL_EVENT_TYPES = new(StringComparer.OrdinalIgnoreCase)
    {
        "EXECUTION_GATE_INVARIANT_VIOLATION",
        "DISCONNECT_FAIL_CLOSED_ENTERED"
    };
    
    // Background evaluation thread for data loss detection
    private CancellationTokenSource? _evaluationCancellationTokenSource;
    private Task? _evaluationTask;
    private const int EVALUATION_INTERVAL_SECONDS = 5; // Evaluate every 5 seconds
    
    // PHASE 3: Stall detection thresholds
    private const int ENGINE_TICK_STALL_SECONDS = 120; // Engine tick should occur at least every 120 seconds (increased for noise reduction)
    private const int TIMETABLE_POLL_STALL_SECONDS = 60; // Timetable poll should occur at least every 60 seconds
    
    // Connection loss: require sustained disconnection before notifying
    private DateTimeOffset? _connectionLostFirstDetectedUtc = null;
    private const int CONNECTION_LOST_SUSTAINED_SECONDS = 60; // Must be disconnected for 60+ seconds before notifying
    
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
    /// Set engine run_id (GUID per engine start). Used for deterministic notification dedupe.
    /// Must be set by RobotEngine.Start() before any notifications are reported.
    /// </summary>
    public void SetRunId(string runId)
    {
        _runId = runId;
    }
    
    /// <summary>
    /// Set callback to check if any streams are in active trading state (for session awareness).
    /// </summary>
    public void SetActiveStreamsCallback(Func<bool>? callback)
    {
        _hasActiveStreamsCallback = callback;
    }
    
    /// <summary>
    /// Check if any streams are in active trading state (ARMED, RANGE_BUILDING, or RANGE_LOCKED).
    /// </summary>
    private bool HasActiveStreams()
    {
        return _hasActiveStreamsCallback?.Invoke() ?? false;
    }
    
    /// <summary>
    /// Handle NinjaTrader connection status update.
    /// Notifies only if sustained disconnection (60+ seconds) during active trading.
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
                // First detection: mark active and record timestamp
                _connectionLostActive = true;
                _connectionLostFirstDetectedUtc = utcNow;
                _connectionLostNotified = false;
                
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CONNECTION_LOST", state: "ENGINE",
                    new
                    {
                        connection_name = connectionName,
                        connection_status = status.ToString(),
                        timestamp_utc = utcNow.ToString("o"),
                        note = "Connection lost detected, waiting for sustained period before notification"
                    }));
            }
            else if (!_connectionLostNotified && _connectionLostFirstDetectedUtc.HasValue)
            {
                // Check if disconnection has been sustained for threshold period
                var elapsed = (utcNow - _connectionLostFirstDetectedUtc.Value).TotalSeconds;
                if (elapsed >= CONNECTION_LOST_SUSTAINED_SECONDS)
                {
                    // Only notify if streams are active (session-aware)
                    if (HasActiveStreams())
                    {
                        _connectionLostNotified = true;
                        
                        var title = "Connection Lost (Sustained)";
                        var message = $"NinjaTrader connection lost for {elapsed:F0}s: {connectionName}. Status: {status}";
                        
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CONNECTION_LOST_SUSTAINED", state: "ENGINE",
                            new
                            {
                                connection_name = connectionName,
                                connection_status = status.ToString(),
                                elapsed_seconds = elapsed,
                                timestamp_utc = utcNow.ToString("o")
                            }));
                        
                        SendNotification("CONNECTION_LOST", title, message, priority: 2, skipRateLimit: true); // Emergency priority, no rate limit
                    }
                }
            }
        }
        else
        {
            // Connection restored: clear flags and log recovery (no notification)
            if (_connectionLostActive)
            {
                _connectionLostActive = false;
                _connectionLostFirstDetectedUtc = null;
                _connectionLostNotified = false;
                
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
    /// Session-aware: only evaluates during active trading windows.
    /// </summary>
    private void EvaluateEngineTickStall(DateTimeOffset utcNow)
    {
        if (_lastEngineTickUtc == DateTimeOffset.MinValue)
            return; // Engine not started yet
        
        // Session awareness: only check during active trading
        if (!HasActiveStreams())
            return; // Suppress during startup/shutdown/outside sessions
        
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
                
                SendNotification("ENGINE_TICK_STALL", title, message, priority: 2, skipRateLimit: true); // Emergency priority, no rate limit
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
                
                // LOG ONLY - no notification (non-execution-critical)
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "TIMETABLE_POLL_STALL_DETECTED", state: "ENGINE",
                    new
                    {
                        last_poll_utc = _lastTimetablePollUtc.ToString("o"),
                        elapsed_seconds = elapsed,
                        threshold_seconds = TIMETABLE_POLL_STALL_SECONDS,
                        note = "Notification suppressed - non-execution-critical (log only)"
                    }));
                
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
    /// PHASE 4: Evaluate data stalls (session-aware).
    /// DISABLED: Notifications suppressed (log only) - handled by gap tolerance + range invalidation.
    /// </summary>
    private void EvaluateDataStalls(DateTimeOffset utcNow)
    {
        // Check each instrument that has received bars
        var instrumentsToCheck = new List<string>(_lastBarUtcByInstrument.Keys);
        
        foreach (var instrument in instrumentsToCheck)
        {
            var lastBarUtc = _lastBarUtcByInstrument[instrument];
            var elapsed = (utcNow - lastBarUtc).TotalSeconds;
            
            var threshold = _config.data_stall_seconds;
            
            if (elapsed >= threshold)
            {
                if (!_dataStallActive.TryGetValue(instrument, out var isActive) || !isActive)
                {
                    // First detection: mark active and log (no notification)
                    _dataStallActive[instrument] = true;
                    
                    // LOG ONLY - no notification (handled by gap tolerance + range invalidation)
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DATA_LOSS_DETECTED", state: "ENGINE",
                        new
                        {
                            instrument = instrument,
                            last_bar_utc = lastBarUtc.ToString("o"),
                            elapsed_seconds = elapsed,
                            threshold_seconds = threshold,
                            note = "Notification suppressed - handled by gap tolerance + range invalidation (log only)"
                        }));
                    
                    // NOTIFICATION DISABLED: Data loss is handled by gap tolerance + range invalidation
                    // SendNotification($"DATA_LOSS:{instrument}", title, message, priority: 1);
                }
            }
        }
    }
    
    /// <summary>
    /// Report a critical event for notification.
    /// Only whitelisted event types are allowed: EXECUTION_GATE_INVARIANT_VIOLATION, DISCONNECT_FAIL_CLOSED_ENTERED
    /// Enforces: One notification per (eventType, run_id) to keep behavior deterministic and auditable.
    /// </summary>
    /// <param name="eventType">Event type (must be whitelisted)</param>
    /// <param name="payload">Event payload dictionary (for audit readability; dedupe is engine-run scoped)</param>
    public void ReportCritical(string eventType, Dictionary<string, object> payload)
    {
        if (!_config.enabled || _notificationService == null)
            return;
        
        // Whitelist check: only allow specific critical event types
        if (!ALLOWED_CRITICAL_EVENT_TYPES.Contains(eventType))
        {
            var td = payload != null && payload.TryGetValue("trading_date", out var tdObj) ? tdObj?.ToString() ?? "" : "";
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: td, eventType: "CRITICAL_NOTIFICATION_REJECTED", state: "ENGINE",
                new
                {
                    event_type = eventType,
                    reason = "NOT_WHITELISTED",
                    allowed_types = string.Join(", ", ALLOWED_CRITICAL_EVENT_TYPES),
                    note = "Critical event type not in whitelist - notification rejected for safety"
                }));
            return;
        }

        // Dedupe key ladder:
        // 1) eventType:_runId (preferred; deterministic per engine run)
        // 2) eventType:trading_date (only if trading_date is known/locked)
        // 3) eventType:UNKNOWN_RUN (last resort; warn once to surface bug)
        string dedupeKey;
        var tradingDate = payload != null && payload.TryGetValue("trading_date", out var tradingDateObj)
            ? tradingDateObj?.ToString() ?? ""
            : "";

        if (!string.IsNullOrWhiteSpace(_runId))
        {
            dedupeKey = $"{eventType}:{_runId}";
        }
        else if (!string.IsNullOrWhiteSpace(tradingDate))
        {
            dedupeKey = $"{eventType}:{tradingDate}";
        }
        else
        {
            dedupeKey = $"{eventType}:UNKNOWN_RUN";
            // Rate-limited warning: once per process to avoid spam
            lock (_notifyLock)
            {
                if (!_missingRunIdWarned)
                {
                    _missingRunIdWarned = true;
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "CRITICAL_DEDUPE_MISSING_RUN_ID", state: "ENGINE",
                        new
                        {
                            event_type = eventType,
                            note = "HealthMonitor missing run_id and trading_date; using UNKNOWN_RUN dedupe key (bug)"
                        }));
                }
            }
        }

        // Decide + mark sent under lock (thread-safe)
        lock (_notifyLock)
        {
            if (_criticalNotificationsSent.Contains(dedupeKey))
                return;
            _criticalNotificationsSent.Add(dedupeKey);
        }
        
        // Build notification message from payload
        var title = eventType.Replace("_", " ");
        var message = BuildCriticalEventMessage(eventType, payload);
        
        // Send notification: Emergency priority (2), immediate enqueue (skip rate limit)
        SendNotification(dedupeKey, title, message, priority: 2, skipRateLimit: true);
        
        // Log that critical event was reported
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: tradingDate, eventType: "CRITICAL_EVENT_REPORTED", state: "ENGINE",
            new
            {
                event_type = eventType,
                run_id = _runId,
                dedupe_key = dedupeKey,
                notification_sent = true,
                priority = 2,
                note = "Critical event reported to notification system (one per eventType:run_id)"
            }));
    }
    
    /// <summary>
    /// Build human-readable message from critical event payload.
    /// </summary>
    private string BuildCriticalEventMessage(string eventType, Dictionary<string, object>? payload)
    {
        if (payload == null)
            return $"Critical event: {eventType}";
        
        var parts = new List<string>();
        
        if (eventType == "EXECUTION_GATE_INVARIANT_VIOLATION")
        {
            parts.Add("Execution Gate Invariant Violation");
            if (payload.TryGetValue("instrument", out var inst) && inst != null)
                parts.Add($"Instrument: {inst}");
            if (payload.TryGetValue("stream", out var stream) && stream != null)
                parts.Add($"Stream: {stream}");
            if (payload.TryGetValue("error", out var error) && error != null)
                parts.Add($"Error: {error}");
            if (payload.TryGetValue("message", out var msg) && msg != null)
                parts.Add($"Details: {msg}");
        }
        else if (eventType == "DISCONNECT_FAIL_CLOSED_ENTERED")
        {
            parts.Add("Robot Entered Fail-Closed Mode");
            if (payload.TryGetValue("connection_name", out var connName) && connName != null)
                parts.Add($"Connection: {connName}");
            if (payload.TryGetValue("active_stream_count", out var streamCount) && streamCount != null)
                parts.Add($"Active Streams: {streamCount}");
            parts.Add("All execution blocked - requires operator intervention");
        }
        else
        {
            parts.Add($"Critical event: {eventType}");
            if (payload.TryGetValue("message", out var msg) && msg != null)
                parts.Add($"{msg}");
        }
        
        return string.Join(". ", parts);
    }
    
    private void SendNotification(string key, string title, string message, int priority, bool skipRateLimit = false)
    {
        if (_notificationService == null)
            return;

        var utcNow = DateTimeOffset.UtcNow;
        bool shouldSend = true;

        // Decide under lock (thread-safe), keep lock scope minimal
        lock (_notifyLock)
        {
            // Emergency notifications (priority 2) skip rate limiting
            // All other notifications respect rate limit
            if (!skipRateLimit && priority < 2)
            {
                // Check rate limit for non-emergency notifications
                if (_lastNotifyUtcByKey.TryGetValue(key, out var lastNotify))
                {
                    var timeSinceLastNotify = (utcNow - lastNotify).TotalSeconds;
                    if (timeSinceLastNotify < _config.min_notify_interval_seconds)
                        shouldSend = false;
                }
            }

            if (shouldSend)
            {
                _lastNotifyUtcByKey[key] = utcNow;
            }
        }

        if (!shouldSend)
            return;
        
        // Enqueue notification (non-blocking) - outside lock
        _notificationService.EnqueueNotification(key, title, message, priority);
        
        // Log notification enqueued
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "PUSHOVER_NOTIFY_ENQUEUED", state: "ENGINE",
            new
            {
                notification_key = key,
                title = title,
                message = message,
                priority = priority,
                skip_rate_limit = skipRateLimit,
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
    
    /// <summary>
    /// Send a test notification to verify Pushover is working.
    /// This bypasses the critical event whitelist and sends a test message.
    /// </summary>
    public void SendTestNotification()
    {
        if (!_config.enabled || _notificationService == null)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "TEST_NOTIFICATION_SKIPPED", state: "ENGINE",
                new
                {
                    reason = _config.enabled ? "NOTIFICATION_SERVICE_NULL" : "HEALTH_MONITOR_DISABLED",
                    note = "Test notification skipped - health monitor disabled or notification service not configured"
                }));
            return;
        }
        
        var testKey = $"TEST_NOTIFICATION:{_runId ?? "UNKNOWN_RUN"}";
        var title = "Robot Test Notification";
        var message = $"Test notification from robot. Run ID: {_runId ?? "UNKNOWN_RUN"}. If you receive this, Pushover is working correctly.";
        
        // Send test notification: Normal priority (0), skip rate limit for testing
        SendNotification(testKey, title, message, priority: 0, skipRateLimit: true);
        
        // Log test notification sent
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "TEST_NOTIFICATION_SENT", state: "ENGINE",
            new
            {
                run_id = _runId,
                notification_key = testKey,
                title = title,
                message = message,
                priority = 0,
                note = "Test notification sent to verify Pushover connectivity"
            }));
    }
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
