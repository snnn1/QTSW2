using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Core.Notifications;

namespace QTSW2.Robot.Core;

public sealed partial class HealthMonitor
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

    // Phase 2: Multi-bar confirmation (debounce)
    private readonly Dictionary<string, List<double>> _lastBarIntervals = new(StringComparer.OrdinalIgnoreCase);
    private const int BAR_INTERVAL_HISTORY_SIZE = 20;

    // Phase 3: Activity-aware
    private readonly Dictionary<string, long> _lastVolume = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal> _lastClose = new(StringComparer.OrdinalIgnoreCase);

    // Phase 5: Cooldown after recovery
    private readonly Dictionary<string, DateTimeOffset> _recoveryCooldownUntil = new(StringComparer.OrdinalIgnoreCase);

    // Phase 4: Aggregation delay detection
    private readonly Dictionary<string, DateTimeOffset> _stallDetectedAtUtc = new(StringComparer.OrdinalIgnoreCase);
    private const int AGGREGATION_DELAY_WINDOW_SECONDS = 60;
    
    // PHASE 3: Engine heartbeat and timetable poll tracking
    private DateTimeOffset _lastEngineTickUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEngineTickObservedUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTimetablePollUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _engineStartUtc = DateTimeOffset.MinValue;
    
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
    private int _engineTickStallConsecutiveCount = 0;
    private bool _timetablePollStallActive = false;
    private bool _playbackTelemetryMode = false;
    
    // Session awareness: callback to check if any streams are in active trading state
    private Func<bool>? _hasActiveStreamsCallback;
    /// <summary>When set and returns false, suppress engine tick stall (market closed — no bars/ticks expected).</summary>
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
        "MISMATCH_FAIL_CLOSED",
        "RECONCILIATION_MISMATCH_FAIL_CLOSED",
        "STATE_CONSISTENCY_GATE_RECOVERY_FAILED",
    };
    
    // Background evaluation thread for data loss detection
    private CancellationTokenSource? _evaluationCancellationTokenSource;
    private Task? _evaluationTask;
    private const int EVALUATION_INTERVAL_SECONDS = 5; // Evaluate every 5 seconds
    
    // PHASE 3: Stall detection thresholds
    private const int ENGINE_TICK_STALL_SECONDS = 120; // Engine tick should occur at least every 120 seconds (increased for noise reduction)
    private const int ENGINE_TICK_STALL_HYSTERESIS_COUNT = 2;
    private const int ENGINE_START_GRACE_PERIOD_SECONDS = 180;
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
    private static readonly object _sharedTimetablePollLock = new object();
    private static long? _sharedTimetablePollStallLoggedForLastPollTicks;
    private static long? _sharedTimetablePollRecoveryLoggedForPollTicks;
    // NinjaTrader disables strategy after "lost price connection more than 4 times in 5 minutes"
    private static readonly List<DateTimeOffset> _connectionLossTimestamps = new();
    private static DateTimeOffset _lastPriceConnectionLossRepeatedLogUtc = DateTimeOffset.MinValue;
    private const int PRICE_CONNECTION_LOSS_WINDOW_SECONDS = 300; // 5 minutes
    private const int PRICE_CONNECTION_LOSS_THRESHOLD = 4;
    private readonly Dictionary<string, DateTimeOffset> _disconnectClassifiedDataLossUtcByInstrument = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastConnectivityShutdownSignalUtc = DateTimeOffset.MinValue;
    private const int CONNECTIVITY_SHUTDOWN_DATA_LOSS_WINDOW_SECONDS = 15;
    private const int CONNECTIVITY_SHUTDOWN_DATA_LOSS_THRESHOLD = 3;
    private const int CONNECTIVITY_SHUTDOWN_SIGNAL_COOLDOWN_SECONDS = 60;
    private Action<string, DateTimeOffset, Dictionary<string, object>>? _connectivityShutdownCallback;
    
    /// <param name="persistenceBaseRoot">Run artifact root (engine <c>_persistenceBase</c>) for notification-sidecar logs.</param>
    public HealthMonitor(string persistenceBaseRoot, HealthMonitorConfig config, RobotLogger log)
    {
        _config = config;
        _log = log;
        
        if (config.pushover_enabled && !string.IsNullOrWhiteSpace(config.pushover_user_key) && !string.IsNullOrWhiteSpace(config.pushover_app_token))
        {
            _notificationService = NotificationService.GetOrCreate(persistenceBaseRoot, config);
            
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
        _engineStartUtc = DateTimeOffset.UtcNow;
    }
    
    /// <summary>
    /// Set current trading date. Normalizes empty/whitespace to null, validates format.
    /// Called on every connection status update to prevent regression to empty string.
    /// </summary>
    public void SetTradingDate(string? tradingDate)
    {
        if (tradingDate == null)
        {
            _currentTradingDate = null;
            return;
        }
        
        var trimmed = tradingDate.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            _currentTradingDate = null;
            return;
        }
        
        // Optionally validate yyyy-MM-dd format
        if (trimmed.Length == 10 && trimmed[4] == '-' && trimmed[7] == '-')
        {
            // Basic format check passed, store trimmed value
            _currentTradingDate = trimmed;
        }
        else
        {
            // Invalid format - set null and log WARN once
            _currentTradingDate = null;
            // Note: We could add a rate-limited warning here, but for now just set null
        }
    }
    
    /// <summary>
    /// Set callback to check if any streams are in active trading state (for session awareness).
    /// </summary>
    public void SetActiveStreamsCallback(Func<bool>? callback)
    {
        _hasActiveStreamsCallback = callback;
    }

    /// <summary>
    /// Set callback to check if CME market is open. When closed, suppress engine tick stall false positives.
    /// </summary>
    public void SetMarketOpenCallback(Func<bool>? callback)
    {
        _isMarketOpenCallback = callback;
    }

    public void SetPlaybackTelemetryMode(bool enabled)
    {
        _playbackTelemetryMode = enabled;
    }

    public void SetConnectivityShutdownCallback(Action<string, DateTimeOffset, Dictionary<string, object>>? callback)
    {
        _connectivityShutdownCallback = callback;
    }

    public bool IsEngineTickStallActive => _engineTickStallActive;

    public bool IsPlaybackEngineTickStallActive => _playbackTelemetryMode && _engineTickStallActive;

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
}
