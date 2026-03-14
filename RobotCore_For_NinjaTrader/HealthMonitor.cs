using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Core.Notifications;

namespace QTSW2.Robot.Core;

/// <summary>
/// Health monitor: detects connection loss and data loss, sends Pushover notifications.
/// Simplified to only monitor external observable facts: connection status and bar arrivals.
/// 
/// Deduplication scope is per NinjaTrader process (static fields).
/// Does not dedupe across multiple NinjaTrader processes or machines.
/// Cross-process dedupe requires file lock or external coordinator (watchdog/daemon).
/// Shared state only dedupes notifications/log spam, not truth.
/// </summary>
public sealed class HealthMonitor
{
    /// <summary>
    /// Connection incident state - single source of truth for connection tracking.
    /// </summary>
    private sealed class ConnectionIncident
    {
        public DateTimeOffset? FirstDetectedUtc { get; set; } // Incident identity (use ticks as incident ID)
        public DateTimeOffset? NotifiedUtc { get; set; } // When sustained disconnect notification was sent
        public DateTimeOffset? RecoveredNotifiedUtc { get; set; } // When recovery notification was sent (prevents spam on flaps)
        public ConnectionStatus LastStatus { get; set; } // Internal enum, not NinjaTrader type
        public DateTimeOffset LastStatusUtc { get; set; }
        public int InstanceCountAtDetection { get; set; }
        public string? TradingDateAtDetection { get; set; } // Optional
        public string ConnectionNameAtDetection { get; set; } = ""; // So recovery can reference same connection name even if later events pass null/Unknown
        
        /// <summary>
        /// Get incident ID from FirstDetectedUtc ticks (stable enough within one process).
        /// </summary>
        public long? GetIncidentId() => FirstDetectedUtc?.UtcTicks;
    }
    
    private readonly HealthMonitorConfig _config;
    private readonly RobotLogger _log;
    private readonly NotificationService? _notificationService;
    private string? _runId; // Engine run identifier (GUID per engine start)
    private readonly object _notifyLock = new object(); // Guards dedupe + rate-limit state
    private string? _currentTradingDate; // Current trading date (nullable, never empty string)
    
    // Data loss tracking: timestamp of last received bar per instrument
    private readonly Dictionary<string, DateTimeOffset> _lastBarUtcByInstrument = new(StringComparer.OrdinalIgnoreCase);
    
    // PHASE 3: Engine heartbeat and timetable poll tracking
    private DateTimeOffset _lastEngineTickUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTimetablePollUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _engineStartUtc = DateTimeOffset.MinValue; // For startup grace period
    
    // PHASE 4: Session-aware monitoring state
    private readonly Dictionary<string, bool> _instrumentHasReceivedData = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _instrumentFirstBarUtc = new(StringComparer.OrdinalIgnoreCase);
    
    // Incident state
    private readonly Dictionary<string, bool> _dataStallActive = new(StringComparer.OrdinalIgnoreCase);
    // Rate limiting for DATA_LOSS_DETECTED per instrument (reduce log verbosity)
    private readonly Dictionary<string, DateTimeOffset> _lastDataLossLogUtcByInstrument = new(StringComparer.OrdinalIgnoreCase);
    private const int DATA_LOSS_LOG_RATE_LIMIT_MINUTES = 15; // Log at most once per 15 minutes per instrument
    private ConnectionIncident? _currentIncident = null; // Single source of truth for connection tracking
    private bool _engineTickStallActive = false;
    private int _engineTickStallConsecutiveCount = 0; // Hysteresis: require N consecutive evaluations before notifying
    private bool _timetablePollStallActive = false;
    
    // Session awareness: callback to check if any streams are in active trading state
    private Func<bool>? _hasActiveStreamsCallback;
    // Market open: when false, suppress engine tick stall (no bars expected = no Tick calls)
    private Func<bool>? _isMarketOpenCallback;
    
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
        "DISCONNECT_FAIL_CLOSED_ENTERED",
        "SLOT_FAILED_RUNTIME",
        "REENTRY_PROTECTION_FAILED",
        "RECONCILIATION_QTY_MISMATCH",
        // Phase 5 supervisory
        "INSTRUMENT_HALTED",
        "OPERATOR_ACK_REQUIRED",
        "GLOBAL_KILL_SWITCH_ACTIVATED",
        "SUPERVISORY_THRESHOLD_EXCEEDED"
    };
    
    // Background evaluation thread for data loss detection
    private CancellationTokenSource? _evaluationCancellationTokenSource;
    private Task? _evaluationTask;
    private const int EVALUATION_INTERVAL_SECONDS = 5; // Evaluate every 5 seconds
    
    // PHASE 3: Stall detection thresholds
    private const int ENGINE_TICK_STALL_SECONDS = 180; // Engine tick should occur at least every 180 seconds (reduced false positives for low-volume instruments)
    private const int ENGINE_TICK_STALL_HYSTERESIS_COUNT = 2; // Require stall for 2 consecutive evaluations before notifying (filters brief spikes)
    private const int ENGINE_TICK_STALL_NOTIFY_COOLDOWN_MINUTES = 30; // Max 1 push per 30 min per process
    private const int ENGINE_START_GRACE_PERIOD_SECONDS = 180; // Suppress stall detection for 3 min after engine start (hydration/startup)
    private const int TIMETABLE_POLL_STALL_SECONDS = 60; // Timetable poll should occur at least every 60 seconds
    
    private const int CONNECTION_LOST_SUSTAINED_SECONDS = 60; // Must be disconnected for 60+ seconds before notifying
    private const long TICKS_PER_60_SECONDS = 600_000_000L; // 60 seconds in 100-nanosecond ticks (for incident bucketing)
    
    // CRITICAL FIX: Shared state across all HealthMonitor instances to prevent duplicate connection loss logging.
    // When multiple strategy instances run (one per instrument), NinjaTrader calls OnConnectionStatusUpdate
    // on each instance at slightly different times (ms apart), so each gets a different incidentId (UtcTicks).
    // We bucket incident IDs by 60 seconds so all instances detecting the same disconnect share one key.
    private static readonly object _sharedConnectionLock = new object();
    private static readonly Dictionary<long, bool> _sharedConnectionLostLoggedByIncident = new(); // Key: incident ID (ticks)
    private static readonly Dictionary<long, bool> _sharedConnectionLostNotifiedByIncident = new(); // Key: incident ID (ticks)
    private static readonly Dictionary<long, int> _sharedConnectionLostInstanceCountByIncident = new(); // Key: incident ID (ticks)
    // NinjaTrader disables strategy after "lost price connection more than 4 times in 5 minutes"
    private static readonly List<DateTimeOffset> _connectionLossTimestamps = new();
    private static DateTimeOffset _lastPriceConnectionLossRepeatedLogUtc = DateTimeOffset.MinValue;
    private const int PRICE_CONNECTION_LOSS_WINDOW_SECONDS = 300; // 5 minutes
    private const int PRICE_CONNECTION_LOSS_THRESHOLD = 4;

    // CONNECTIVITY_DAILY_SUMMARY: per-trading-day aggregation (shared across instances to avoid double-count)
    private static readonly object _connectivityMetricsLock = new();
    private static string? _connectivityTradingDate;
    private static int _connectivityDisconnectCount;
    private static double _connectivityTotalDowntimeSeconds;
    private static double _connectivityMaxDowntimeSeconds;
    private static DateTimeOffset? _connectivityDisconnectStartUtc;
    private static readonly HashSet<long> _connectivityRecoveredIncidentIds = new();
    private static int _connectivityShortDisconnects;  // <10s
    private static int _connectivityLongDisconnects;  // >60s
    private const int SHORT_DISCONNECT_THRESHOLD_SECONDS = 10;
    private const int LONG_DISCONNECT_THRESHOLD_SECONDS = 60;

    // CONNECTIVITY_INCIDENT: institutional-style alerts (5+ disconnects in 1h, or single disconnect >120s)
    private static readonly List<DateTimeOffset> _connectivityDisconnectTimestampsLastHour = new();
    private static DateTimeOffset? _lastConnectivityIncidentEmittedUtc;
    private const int CONNECTIVITY_INCIDENT_WINDOW_SECONDS = 3600;  // 1 hour
    private const int CONNECTIVITY_INCIDENT_COUNT_THRESHOLD = 5;
    private const int CONNECTIVITY_INCIDENT_DURATION_THRESHOLD_SECONDS = 120;
    private const int CONNECTIVITY_INCIDENT_COOLDOWN_SECONDS = 1800;  // 30 min between alerts

    // CRITICAL FIX: Shared engine tick across all HealthMonitor instances to prevent false stall notifications.
    // Each strategy instance only gets Tick() when its instrument's bars arrive. Low-volume instruments can have 3+ min gaps.
    private static readonly object _sharedEngineTickLock = new object();
    private static DateTimeOffset _sharedLastEngineTickUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset _sharedLastEngineTickStallNotifyUtc = DateTimeOffset.MinValue;
    
    public HealthMonitor(string projectRoot, HealthMonitorConfig config, RobotLogger log)
    {
        _config = config;
        _log = log;
        
        if (config.pushover_enabled && !string.IsNullOrWhiteSpace(config.pushover_user_key) && !string.IsNullOrWhiteSpace(config.pushover_app_token))
        {
            _notificationService = NotificationService.GetOrCreate(projectRoot, config);
            
            // Set failure callback to emit ERROR events for notification failures
            _notificationService.SetFailureCallback((notificationKey, errorMessage, exception) =>
            {
                var utcNow = DateTimeOffset.UtcNow;
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _currentTradingDate ?? "", eventType: "NOTIFICATION_SEND_FAILED", state: "ENGINE",
                    new
                    {
                        notification_key = notificationKey,
                        error_message = errorMessage,
                        exception_type = exception?.GetType().Name,
                        exception_message = exception?.Message,
                        note = "Pushover notification send failed - check notification_errors.log for details"
                    }));
            });
        }
    }

    /// <summary>
    /// Set engine run_id (GUID per engine start). Used for deterministic notification dedupe.
    /// Must be set by RobotEngine.Start() before any notifications are reported.
    /// </summary>
    public void SetRunId(string runId)
    {
        _runId = runId;
        _engineStartUtc = DateTimeOffset.UtcNow; // For startup grace period (suppress stall during hydration)
    }
    
    /// <summary>
    /// Set current trading date. Normalizes empty/whitespace to null, validates format.
    /// Called on every connection status update to prevent regression to empty string.
    /// When trading date changes, emits CONNECTIVITY_DAILY_SUMMARY for the previous day.
    /// </summary>
    public void SetTradingDate(string? tradingDate)
    {
        string? newTradingDate = null;
        if (tradingDate != null)
        {
            var trimmed = tradingDate.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.Length == 10 && trimmed[4] == '-' && trimmed[7] == '-')
            {
                newTradingDate = trimmed;
            }
        }

        var previousTradingDate = _currentTradingDate;
        _currentTradingDate = newTradingDate;

        // Emit CONNECTIVITY_DAILY_SUMMARY when trading date changes and we had a previous date
        if (!string.IsNullOrEmpty(previousTradingDate) && previousTradingDate != newTradingDate)
        {
            EmitConnectivityDailySummary(previousTradingDate);
        }

        // Initialize metrics for new trading date if we have one
        if (!string.IsNullOrEmpty(newTradingDate))
        {
            lock (_connectivityMetricsLock)
            {
                if (_connectivityTradingDate != newTradingDate)
                {
                    _connectivityTradingDate = newTradingDate;
                    _connectivityDisconnectCount = 0;
                    _connectivityTotalDowntimeSeconds = 0;
                    _connectivityMaxDowntimeSeconds = 0;
                    _connectivityDisconnectStartUtc = null;
                    _connectivityRecoveredIncidentIds.Clear();
                    _connectivityShortDisconnects = 0;
                    _connectivityLongDisconnects = 0;
                }
            }
        }
    }

    /// <summary>
    /// Emit CONNECTIVITY_DAILY_SUMMARY and reset metrics for the given trading date.
    /// </summary>
    private void EmitConnectivityDailySummary(string tradingDate)
    {
        int count;
        double total;
        double max;
        int shortCount;
        int longCount;
        lock (_connectivityMetricsLock)
        {
            if (_connectivityTradingDate != tradingDate)
                return; // Metrics already emitted or belong to different date
            count = _connectivityDisconnectCount;
            total = _connectivityTotalDowntimeSeconds;
            max = _connectivityMaxDowntimeSeconds;
            shortCount = _connectivityShortDisconnects;
            longCount = _connectivityLongDisconnects;
            _connectivityTradingDate = null;
            _connectivityDisconnectCount = 0;
            _connectivityTotalDowntimeSeconds = 0;
            _connectivityMaxDowntimeSeconds = 0;
            _connectivityDisconnectStartUtc = null;
            _connectivityRecoveredIncidentIds.Clear();
            _connectivityShortDisconnects = 0;
            _connectivityLongDisconnects = 0;
        }

        var avg = count == 0 ? 0.0 : total / count;
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: tradingDate, eventType: "CONNECTIVITY_DAILY_SUMMARY", state: "ENGINE",
            new
            {
                disconnect_count = count,
                avg_duration_seconds = Math.Round(avg, 2),
                max_duration_seconds = Math.Round(max, 2),
                total_downtime_seconds = Math.Round(total, 2),
                short_disconnects = shortCount,
                long_disconnects = longCount
            }));
    }

    /// <summary>
    /// Emit CONNECTIVITY_INCIDENT when disconnect_count >= 5 in 1h or single disconnect >120s.
    /// Institutional-style alert with Pushover notification. Cooldown 30 min to avoid spam.
    /// </summary>
    private void TryEmitConnectivityIncident(DateTimeOffset utcNow, string trigger, int? disconnectCountInWindow = null, double? durationSeconds = null)
    {
        lock (_connectivityMetricsLock)
        {
            if (_lastConnectivityIncidentEmittedUtc.HasValue &&
                (utcNow - _lastConnectivityIncidentEmittedUtc.Value).TotalSeconds < CONNECTIVITY_INCIDENT_COOLDOWN_SECONDS)
                return;
            _lastConnectivityIncidentEmittedUtc = utcNow;
        }

        var payload = new Dictionary<string, object>
        {
            ["trigger"] = trigger,
            ["trading_date"] = _currentTradingDate ?? "",
            ["timestamp_utc"] = utcNow.ToString("o")
        };
        if (disconnectCountInWindow.HasValue)
            payload["disconnect_count_in_window"] = disconnectCountInWindow.Value;
        if (durationSeconds.HasValue)
            payload["disconnect_duration_seconds"] = Math.Round(durationSeconds.Value, 2);

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _currentTradingDate ?? "", eventType: "CONNECTIVITY_INCIDENT", state: "ENGINE", payload));

        var title = "Connectivity Incident";
        var message = trigger == "disconnect_count_5_in_hour"
            ? $"5+ disconnects in 1 hour ({disconnectCountInWindow} detected). Check network/broker stability."
            : $"Single disconnect exceeded 120s ({durationSeconds:F0}s). Serious connectivity failure.";
        SendNotification("CONNECTIVITY_INCIDENT", title, message, priority: 2, skipPerKeyRateLimit: true);
    }
    
    /// <summary>
    /// Set callback to check if any streams are in active trading state (for session awareness).
    /// </summary>
    public void SetActiveStreamsCallback(Func<bool>? callback)
    {
        _hasActiveStreamsCallback = callback;
    }

    /// <summary>
    /// Set callback to check if CME market is open. When closed, suppress engine tick stall
    /// (no bars expected = OnBarUpdate/Tick not called = false positives).
    /// </summary>
    public void SetMarketOpenCallback(Func<bool>? callback)
    {
        _isMarketOpenCallback = callback;
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
    /// CRITICAL FIX: Uses shared state keyed to incident ID to prevent duplicate logging when multiple strategy instances detect the same disconnect.
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
                    // First instance to detect: emit CONNECTION_CONTEXT then log the event and initialize shared state
                    _sharedConnectionLostLoggedByIncident[sharedIncidentKey] = true;
                    _sharedConnectionLostInstanceCountByIncident[sharedIncidentKey] = 1;

                    // PRICE_CONNECTION_LOSS_REPEATED: one timestamp per incident (deduplicated across instances)
                    _connectionLossTimestamps.Add(utcNow);
                    var lossCutoff = utcNow.AddSeconds(-PRICE_CONNECTION_LOSS_WINDOW_SECONDS);
                    _connectionLossTimestamps.RemoveAll(t => t < lossCutoff);
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
                    }

                    // CONNECTIVITY_DAILY_SUMMARY: record disconnect start and count (first instance only)
                    var metricsTradingDate = _currentTradingDate ?? "UNKNOWN";
                    lock (_connectivityMetricsLock)
                    {
                        if (_connectivityTradingDate != metricsTradingDate)
                        {
                            _connectivityTradingDate = metricsTradingDate;
                            _connectivityDisconnectCount = 0;
                            _connectivityTotalDowntimeSeconds = 0;
                            _connectivityMaxDowntimeSeconds = 0;
                            _connectivityRecoveredIncidentIds.Clear();
                            _connectivityShortDisconnects = 0;
                            _connectivityLongDisconnects = 0;
                        }
                        _connectivityDisconnectStartUtc = _currentIncident?.FirstDetectedUtc ?? utcNow;
                        _connectivityDisconnectCount++;
                    }

                    // CONNECTIVITY_INCIDENT: 5+ disconnects in 1 hour
                    int countInWindow = 0;
                    lock (_connectivityMetricsLock)
                    {
                        _connectivityDisconnectTimestampsLastHour.Add(utcNow);
                        var cutoff = utcNow.AddSeconds(-CONNECTIVITY_INCIDENT_WINDOW_SECONDS);
                        _connectivityDisconnectTimestampsLastHour.RemoveAll(t => t < cutoff);
                        countInWindow = _connectivityDisconnectTimestampsLastHour.Count;
                    }
                    if (countInWindow >= CONNECTIVITY_INCIDENT_COUNT_THRESHOLD)
                    {
                        TryEmitConnectivityIncident(utcNow, "disconnect_count_5_in_hour", countInWindow);
                    }
                    
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _currentTradingDate ?? "", eventType: "CONNECTION_CONTEXT", state: "ENGINE",
                        new
                        {
                            connection_name = connectionName,
                            connection_status = status.ToString(),
                            last_engine_tick_utc = _lastEngineTickUtc != DateTimeOffset.MinValue ? _lastEngineTickUtc.ToString("o") : null,
                            last_timetable_poll_utc = _lastTimetablePollUtc != DateTimeOffset.MinValue ? _lastTimetablePollUtc.ToString("o") : null,
                            engine_start_utc = _engineStartUtc != DateTimeOffset.MinValue ? _engineStartUtc.ToString("o") : null,
                            trading_date = _currentTradingDate,
                            incident_id = incidentId.Value,
                            note = "Context at disconnect for ops diagnostics"
                        }));
                    
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

                // CONNECTIVITY_DAILY_SUMMARY: record duration (once per incident via shared recovere set)
                var incidentIdForRecovery = _currentIncident?.GetIncidentId();
                if (incidentIdForRecovery.HasValue && _currentIncident?.FirstDetectedUtc != null)
                {
                    var durationSeconds = (utcNow - _currentIncident.FirstDetectedUtc.Value).TotalSeconds;
                    lock (_connectivityMetricsLock)
                    {
                        if (!_connectivityRecoveredIncidentIds.Contains(incidentIdForRecovery.Value))
                        {
                            _connectivityRecoveredIncidentIds.Add(incidentIdForRecovery.Value);
                            _connectivityTotalDowntimeSeconds += durationSeconds;
                            _connectivityMaxDowntimeSeconds = Math.Max(_connectivityMaxDowntimeSeconds, durationSeconds);
                            if (durationSeconds < SHORT_DISCONNECT_THRESHOLD_SECONDS)
                                _connectivityShortDisconnects++;
                            else if (durationSeconds > LONG_DISCONNECT_THRESHOLD_SECONDS)
                                _connectivityLongDisconnects++;
                        }
                        _connectivityDisconnectStartUtc = null;
                    }

                    // CONNECTIVITY_INCIDENT: single disconnect >120s
                    if (durationSeconds > CONNECTIVITY_INCIDENT_DURATION_THRESHOLD_SECONDS)
                    {
                        TryEmitConnectivityIncident(utcNow, "disconnect_duration_120s", durationSeconds: durationSeconds);
                    }
                }
                
                _currentIncident = null; // Clear incident - if it flaps again, it becomes a new incident
            }
            else
            {
                // Connected from start (no prior incident): emit so Watchdog shows Connected instead of Unknown
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _currentTradingDate ?? "", eventType: "CONNECTION_CONFIRMED", state: "ENGINE",
                    new
                    {
                        connection_name = connectionName,
                        connection_status = status.ToString(),
                        timestamp_utc = utcNow.ToString("o"),
                        trading_date = _currentTradingDate,
                        note = "Broker connected at startup or status refresh"
                    }));
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
        
        // Update shared state: any instance ticking = engine alive (prevents false stalls for low-volume instruments)
        lock (_sharedEngineTickLock)
        {
            if (tickUtc > _sharedLastEngineTickUtc)
                _sharedLastEngineTickUtc = tickUtc;
        }
        
        // Clear engine tick stall flag and hysteresis if it was active (recovery)
        if (_engineTickStallActive)
        {
            _engineTickStallActive = false;
            _engineTickStallConsecutiveCount = 0;
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
    /// Split into STALL_STARTUP (info, during grace) vs STALL_RUNTIME (critical, after grace).
    /// Session-aware: only evaluates during active trading windows.
    /// </summary>
    private void EvaluateEngineTickStall(DateTimeOffset utcNow)
    {
        DateTimeOffset lastTick;
        lock (_sharedEngineTickLock)
        {
            lastTick = _sharedLastEngineTickUtc;
        }
        if (lastTick == DateTimeOffset.MinValue)
            return; // Engine not started yet
        
        // Session awareness: only check during active trading
        if (!HasActiveStreams())
            return; // Suppress during startup/shutdown/outside sessions
        
        // Market closed: no bars expected, so Tick() won't be called. Suppress false positives.
        if (_isMarketOpenCallback != null && !_isMarketOpenCallback())
            return;
        
        var elapsed = (utcNow - lastTick).TotalSeconds;
        if (elapsed < ENGINE_TICK_STALL_SECONDS)
        {
            _engineTickStallConsecutiveCount = 0; // Reset hysteresis when not in stall
            return;
        }
        
        // Hysteresis: require stall for N consecutive evaluations before notifying (reduces false positives from brief spikes)
        _engineTickStallConsecutiveCount++;
        if (_engineTickStallConsecutiveCount < ENGINE_TICK_STALL_HYSTERESIS_COUNT)
            return;
        
        var inStartupGrace = _engineStartUtc != DateTimeOffset.MinValue &&
            (utcNow - _engineStartUtc).TotalSeconds < ENGINE_START_GRACE_PERIOD_SECONDS;
        
        if (!_engineTickStallActive)
        {
            _engineTickStallActive = true;
            var eventType = inStartupGrace ? "ENGINE_TICK_STALL_STARTUP" : "ENGINE_TICK_STALL_RUNTIME";
            var causeHint = inStartupGrace ? "hydration_or_bars_loading" : null;
            
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: eventType, state: "ENGINE",
                new
                {
                    last_tick_utc = lastTick.ToString("o"),
                    elapsed_seconds = elapsed,
                    threshold_seconds = ENGINE_TICK_STALL_SECONDS,
                    consecutive_count = _engineTickStallConsecutiveCount,
                    cause_hint = causeHint,
                    in_startup_grace = inStartupGrace
                }));
            
            // Only notify for runtime stall (critical); startup stall is info-only
            // Cooldown: max 1 push per 30 min per process to avoid spam
            if (!inStartupGrace)
            {
                lock (_sharedEngineTickLock)
                {
                    var cooldownElapsed = (utcNow - _sharedLastEngineTickStallNotifyUtc).TotalMinutes;
                    if (_sharedLastEngineTickStallNotifyUtc != DateTimeOffset.MinValue && cooldownElapsed < ENGINE_TICK_STALL_NOTIFY_COOLDOWN_MINUTES)
                        return;
                    _sharedLastEngineTickStallNotifyUtc = utcNow;
                }
                var title = "Engine Tick Stall Detected (Runtime)";
                var message = $"Engine Tick() has not been called for {elapsed:F0}s. Last tick: {lastTick:o} UTC. Threshold: {ENGINE_TICK_STALL_SECONDS}s";
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
        SendNotification(dedupeKey, title, message, priority: 2, skipPerKeyRateLimit: true);
        
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
    
    private void SendNotification(string key, string title, string message, int priority, bool skipPerKeyRateLimit = false)
    {
        if (_notificationService == null)
            return;

        var utcNow = DateTimeOffset.UtcNow;
        bool shouldSend = true;

        // Decide under lock (thread-safe), keep lock scope minimal
        lock (_notifyLock)
        {
            // Two-tier rate limiting:
            // A) Per-event-type emergency limiter (5 minutes, persistent) - handled by ShouldSendEmergencyNotification() before calling this method
            // B) Per-key/burst limiter (short window, in-memory) - handled here
            // Emergency notifications (priority 2) respect the per-event-type limiter but may skip per-key limiter
            // All other notifications respect both limiters
            if (!skipPerKeyRateLimit && priority < 2)
            {
                // Check per-key rate limit for non-emergency notifications
                if (_lastNotifyUtcByKey.TryGetValue(key, out var lastNotify))
                {
                    var timeSinceLastNotify = (utcNow - lastNotify).TotalSeconds;
                    if (timeSinceLastNotify < _config.min_notify_interval_seconds)
                        shouldSend = false;
                }
            }
            // Note: skipPerKeyRateLimit bypasses only the per-key limiter, not the per-event-type limiter
            // Emergency notifications respect the 5-minute per-event-type limiter (persistent)

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
                skip_per_key_rate_limit = skipPerKeyRateLimit,
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
    /// Emits CONNECTIVITY_DAILY_SUMMARY for current trading date on shutdown.
    /// </summary>
    public void Stop()
    {
        // Emit connectivity summary for current trading date before shutdown
        var tradingDateToEmit = _currentTradingDate;
        if (!string.IsNullOrEmpty(tradingDateToEmit))
        {
            EmitConnectivityDailySummary(tradingDateToEmit);
        }

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
    /// Get notification service metrics (queue depth, success/failure counts, watchdog restarts).
    /// Returns null if notification service is not available.
    /// </summary>
    public NotificationService.NotificationMetrics? GetNotificationMetrics()
    {
        return _notificationService?.GetMetrics();
    }
    
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
        SendNotification(testKey, title, message, priority: 0, skipPerKeyRateLimit: true);
        
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
