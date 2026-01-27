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
    // Rate limiting for DATA_LOSS_DETECTED per instrument (reduce log verbosity)
    private readonly Dictionary<string, DateTimeOffset> _lastDataLossLogUtcByInstrument = new(StringComparer.OrdinalIgnoreCase);
    private const int DATA_LOSS_LOG_RATE_LIMIT_MINUTES = 15; // Log at most once per 15 minutes per instrument
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
    
    // Emergency notification rate limiting is now handled by NotificationService singleton
    // This ensures rate limiting state persists across engine runs
    
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
    
    // CRITICAL FIX: Shared state across all HealthMonitor instances to prevent duplicate connection loss logging
    // When multiple strategy instances run (one per instrument), NinjaTrader calls OnConnectionStatusUpdate
    // on all instances simultaneously. We only want to log CONNECTION_LOST once per actual disconnect.
    private static readonly object _sharedConnectionLock = new object();
    private static bool _sharedConnectionLostLogged = false; // Track if CONNECTION_LOST event has been logged
    private static bool _sharedConnectionLostNotified = false; // Track if CONNECTION_LOST_SUSTAINED notification has been sent
    private static DateTimeOffset? _sharedConnectionLostFirstDetectedUtc = null;
    private static int _sharedConnectionLostInstanceCount = 0; // Track how many instances detected the disconnect
    
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
    /// CRITICAL FIX: Uses shared state to prevent duplicate logging when multiple strategy instances detect the same disconnect.
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
            // Update instance state for recovery tracking
            if (!_connectionLostActive)
            {
                _connectionLostActive = true;
                _connectionLostFirstDetectedUtc = utcNow;
                _connectionLostNotified = false;
            }
            
            // CRITICAL FIX: Use shared state to prevent duplicate logging across multiple strategy instances
            lock (_sharedConnectionLock)
            {
                if (!_sharedConnectionLostLogged)
                {
                    // First instance to detect: log the event and initialize shared state
                    _sharedConnectionLostLogged = true;
                    _sharedConnectionLostFirstDetectedUtc = utcNow;
                    _sharedConnectionLostInstanceCount = 1;
                    
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CONNECTION_LOST", state: "ENGINE",
                        new
                        {
                            connection_name = connectionName,
                            connection_status = status.ToString(),
                            timestamp_utc = utcNow.ToString("o"),
                            strategy_instance_count = 1,
                            note = "Connection lost detected, waiting for sustained period before notification"
                        }));
                }
                else
                {
                    // Another instance detected the same disconnect: increment counter but don't log
                    _sharedConnectionLostInstanceCount++;
                }
            }
            
            // Check if disconnection has been sustained for threshold period
            if (!_connectionLostNotified && _connectionLostFirstDetectedUtc.HasValue)
            {
                var elapsed = (utcNow - _connectionLostFirstDetectedUtc.Value).TotalSeconds;
                if (elapsed >= CONNECTION_LOST_SUSTAINED_SECONDS)
                {
                    // CRITICAL FIX: Only send notification once per disconnect (use shared state)
                    lock (_sharedConnectionLock)
                    {
                        if (!_sharedConnectionLostNotified && _sharedConnectionLostLogged && _sharedConnectionLostFirstDetectedUtc.HasValue)
                        {
                            var sharedElapsed = (utcNow - _sharedConnectionLostFirstDetectedUtc.Value).TotalSeconds;
                            if (sharedElapsed >= CONNECTION_LOST_SUSTAINED_SECONDS)
                            {
                                // Mark as notified in shared state to prevent duplicate notifications
                                _sharedConnectionLostNotified = true;
                                _connectionLostNotified = true;
                                
                                var hasActiveStreams = HasActiveStreams();
                                var title = "Connection Lost (Sustained)";
                                var message = $"NinjaTrader connection lost for {sharedElapsed:F0}s: {connectionName}. Status: {status}. " +
                                             $"Active streams: {(hasActiveStreams ? "Yes" : "No")}. " +
                                             $"Detected by {_sharedConnectionLostInstanceCount} strategy instance(s).";
                                
                                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CONNECTION_LOST_SUSTAINED", state: "ENGINE",
                                    new
                                    {
                                        connection_name = connectionName,
                                        connection_status = status.ToString(),
                                        elapsed_seconds = sharedElapsed,
                                        timestamp_utc = utcNow.ToString("o"),
                                        has_active_streams = hasActiveStreams,
                                        strategy_instance_count = _sharedConnectionLostInstanceCount,
                                        note = "Connection loss notification sent regardless of stream state (critical infrastructure issue)"
                                    }));
                                
                                SendNotification("CONNECTION_LOST", title, message, priority: 2, skipRateLimit: true); // Emergency priority, no rate limit
                            }
                        }
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
                
                // CRITICAL FIX: Reset shared state when connection is restored
                lock (_sharedConnectionLock)
                {
                    _sharedConnectionLostLogged = false;
                    _sharedConnectionLostNotified = false;
                    _sharedConnectionLostFirstDetectedUtc = null;
                    _sharedConnectionLostInstanceCount = 0;
                }
                
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
                    
                    // Rate limit logging to reduce verbosity (log at most once per 15 minutes per instrument)
                    var shouldLog = true;
                    if (_lastDataLossLogUtcByInstrument.TryGetValue(instrument, out var lastLogUtc))
                    {
                        var timeSinceLastLog = (utcNow - lastLogUtc).TotalMinutes;
                        shouldLog = timeSinceLastLog >= DATA_LOSS_LOG_RATE_LIMIT_MINUTES;
                    }
                    
                    if (shouldLog)
                    {
                        _lastDataLossLogUtcByInstrument[instrument] = utcNow;
                        
                        // LOG ONLY - no notification (handled by gap tolerance + range invalidation)
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DATA_LOSS_DETECTED", state: "ENGINE",
                            new
                            {
                                instrument = instrument,
                                last_bar_utc = lastBarUtc.ToString("o"),
                                elapsed_seconds = elapsed,
                                threshold_seconds = threshold,
                                note = "Notification suppressed - handled by gap tolerance + range invalidation (log only, rate-limited)"
                            }));
                    }
                    
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

        // Emergency notification rate limiting: check if we've sent this event type recently
        // This prevents spam when the same critical event happens across multiple engine runs
        // CRITICAL: Check rate limit FIRST before dedupe to prevent multiple simultaneous notifications
        // Rate limiting is now handled by NotificationService singleton for cross-run persistence
        var utcNow = DateTimeOffset.UtcNow;
        bool shouldSendEmergencyNotification = true;
        
        // Check emergency rate limit via NotificationService (singleton, persists across runs)
        if (_notificationService != null)
        {
            shouldSendEmergencyNotification = _notificationService.ShouldSendEmergencyNotification(eventType);
        }
        
        lock (_notifyLock)
        {
            // Check dedupe key (per run_id) - still prevent duplicate notifications for same run_id
            if (_criticalNotificationsSent.Contains(dedupeKey))
                return;
            _criticalNotificationsSent.Add(dedupeKey);
        }
        
        // Skip notification if rate limited
        if (!shouldSendEmergencyNotification)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: tradingDate, eventType: "CRITICAL_NOTIFICATION_SKIPPED", state: "ENGINE",
                new
                {
                    event_type = eventType,
                    run_id = _runId,
                    dedupe_key = dedupeKey,
                    reason = "EMERGENCY_RATE_LIMIT",
                    min_interval_seconds = 300, // 5 minutes (constant in NotificationService)
                    note = "Emergency notification rate limited - same event type notified within 5 minutes (rate limiting via NotificationService singleton)"
                }));
            return;
        }
        
        // Build notification message from payload
        var title = eventType.Replace("_", " ");
        var message = BuildCriticalEventMessage(eventType, payload);
        
        // Send notification: Emergency priority (2)
        // Note: Emergency rate limiting is already handled above, so we can skip the regular rate limit
        // to avoid double-checking (we've already ensured 5 minutes have passed for this event type)
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
