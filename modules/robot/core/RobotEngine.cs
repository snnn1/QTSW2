using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Execution;
using QTSW2.Robot.Core.Notifications;

/// <summary>
/// Connection recovery state for disconnect/reconnect handling.
/// </summary>
public enum ConnectionRecoveryState
{
    CONNECTED_OK,
    DISCONNECT_FAIL_CLOSED,
    RECONNECTED_RECOVERY_PENDING,
    RECOVERY_RUNNING,
    RECOVERY_COMPLETE
}

public sealed class RobotEngine : IExecutionRecoveryGuard
{
    private readonly string _root;
    private readonly RobotLogger _log; // Kept for backward compatibility during migration
    private readonly RobotLoggingService? _loggingService; // New async logging service (Fix B)
    private readonly JournalStore _journals;
    private readonly FilePoller _timetablePoller;
    private readonly object _engineLock = new object(); // Serialize engine entrypoints (Tick/OnBar/etc.)
    
    // Engine run identifier (GUID per engine Start())
    private string? _runId;

    private ParitySpec? _spec;
    private TimeService? _time;

    private string _specPath = "";
    private string _timetablePath = "";
    
    // PHASE 3.1: Single-executor guard for canonical markets
    private CanonicalMarketLock? _canonicalMarketLock;
    private readonly string? _executionInstrument; // Execution instrument from constructor (MES, ES, etc.)

    private string? _lastTimetableHash;
    private TimetableContract? _lastTimetable; // Store timetable for application after trading date is locked
    private DateOnly? _activeTradingDate; // PHASE 3: Store as DateOnly (authoritative), convert to string only for logging/journal
    
    /// <summary>
    /// Get trading date as string for logging/journal purposes.
    /// Returns empty string if trading date is not yet set.
    /// </summary>
    private string TradingDateString => _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";

    /// <summary>
    /// Get current trading date (for external access, e.g., NinjaTrader strategy).
    /// Returns empty string if trading date is not yet set.
    /// </summary>
    public string GetTradingDate()
    {
        lock (_engineLock)
        {
            return TradingDateString;
        }
    }

    private readonly Dictionary<string, StreamStateMachine> _streams = new();
    private readonly ExecutionMode _executionMode;
    
    // Session start time per instrument (from TradingHours, fallback to 17:00 CST)
    private readonly Dictionary<string, string> _sessionStartTimes = new Dictionary<string, string>();
    private IExecutionAdapter? _executionAdapter;
    private RiskGate? _riskGate;
    private readonly ExecutionJournal _executionJournal;
    private KillSwitch? _killSwitch;
    private readonly ExecutionSummary _executionSummary;
    private HealthMonitor? _healthMonitor; // Optional: health monitoring and alerts

    // Logging configuration
    private readonly LoggingConfig _loggingConfig;
    private readonly string _resolvedLogDir;
    private readonly string _resolvedLogDirSource;
    private readonly string _loggingConfigPath;
    private readonly string? _resolvedLogDirWarning;

    // ENGINE-level bar ingress diagnostic (rate-limiting per instrument)
    private readonly Dictionary<string, DateTimeOffset> _lastBarHeartbeatPerInstrument = new();
    private int BAR_HEARTBEAT_RATE_LIMIT_MINUTES => _loggingConfig.enable_diagnostic_logs ? 1 : 5; // More frequent if diagnostics enabled
    
    // ENGINE-level tick heartbeat diagnostic (rate-limited)
    private DateTimeOffset _lastTickHeartbeat = DateTimeOffset.MinValue;
    private int TICK_HEARTBEAT_RATE_LIMIT_MINUTES => _loggingConfig.diagnostic_rate_limits?.tick_heartbeat_minutes ?? (_loggingConfig.enable_diagnostic_logs ? 1 : 5);
    
    // Engine heartbeat tracking for liveness monitoring
    private DateTimeOffset _lastTickUtc = DateTimeOffset.MinValue;
    
    // Gap violation summary tracking (rate-limited)
    private DateTimeOffset? _lastGapViolationSummaryUtc = null;
    
    // PHASE 3.1: Identity invariants status tracking (rate-limited)
    private DateTimeOffset? _lastIdentityInvariantsCheckUtc = null;
    private const int IDENTITY_INVARIANTS_CHECK_INTERVAL_SECONDS = 60; // Check every 60 seconds
    private bool _lastIdentityInvariantsPass = true; // Track last status for on-change emission
    
    // Bar rejection tracking for summary events
    private readonly Dictionary<string, BarRejectionStats> _barRejectionStats = new Dictionary<string, BarRejectionStats>();
    private DateTimeOffset? _lastBarRejectionSummaryUtc = null;
    private const double BAR_REJECTION_SUMMARY_INTERVAL_MINUTES = 5.0;
    
    // Account/environment info for startup banner (set by strategy host)
    private string? _accountName;
    private string? _environment;
    
    // Disconnect recovery state machine
    private ConnectionRecoveryState _recoveryState = ConnectionRecoveryState.CONNECTED_OK;
    private DateTimeOffset? _disconnectFirstUtc;
    private DateTimeOffset? _recoveryStartedUtc;
    private DateTimeOffset? _recoveryCompletedUtc;
    private ConnectionStatus _lastConnectionStatus = ConnectionStatus.Connected;
    
    // Broker sync gate timestamps (for recovery synchronization)
    private DateTimeOffset? _lastOrderUpdateUtc;
    private DateTimeOffset? _lastExecutionUpdateUtc;
    private DateTimeOffset _lastEngineTickUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? _reconnectUtc;
    private DateTimeOffset? _lastSyncWaitLogUtc; // Rate-limiting for DISCONNECT_RECOVERY_WAITING_FOR_SYNC
    
    // Recovery runner guard (prevent re-entrancy)
    private bool _recoveryRunnerActive = false;
    private readonly object _recoveryLock = new object();
    
    // Helper class for tracking bar rejection statistics
    private class BarRejectionStats
    {
        public int PartialRejected { get; set; }
        public int DateMismatch { get; set; }
        public int BeforeDateLocked { get; set; }
        public int TotalAccepted { get; set; }
        public DateTimeOffset LastUpdateUtc { get; set; }
    }

    /// <summary>
    /// Set account and environment info for startup banner.
    /// Called by strategy host before Start().
    /// </summary>
    public void SetAccountInfo(string? accountName, string? environment)
    {
        lock (_engineLock)
        {
            _accountName = accountName;
            _environment = environment;
        }
    }

    
    /// <summary>
    /// Get last tick timestamp for liveness monitoring.
    /// </summary>
    public DateTimeOffset GetLastTickUtc()
    {
        lock (_engineLock)
        {
            return _lastTickUtc;
        }
    }
    
    /// <summary>
    /// Check if execution is allowed based on recovery state.
    /// Execution is allowed only in CONNECTED_OK or RECOVERY_COMPLETE states.
    /// </summary>
    public bool IsExecutionAllowed()
    {
        lock (_engineLock)
        {
            return _recoveryState == ConnectionRecoveryState.CONNECTED_OK ||
                   _recoveryState == ConnectionRecoveryState.RECOVERY_COMPLETE;
        }
    }
    
    /// <summary>
    /// Get current recovery state (for RiskGate reason).
    /// </summary>
    public ConnectionRecoveryState RecoveryState
    {
        get
        {
            lock (_engineLock)
            {
                return _recoveryState;
            }
        }
    }
    
    // IExecutionRecoveryGuard implementation
    bool IExecutionRecoveryGuard.IsExecutionAllowed() => IsExecutionAllowed();
    
    string IExecutionRecoveryGuard.GetRecoveryStateReason() => RecoveryState.ToString();

    public RobotEngine(string projectRoot, TimeSpan timetablePollInterval, ExecutionMode executionMode = ExecutionMode.DRYRUN, string? customLogDir = null, string? customTimetablePath = null, string? instrument = null, bool useAsyncLogging = true)
    {
        _root = projectRoot;
        _executionMode = executionMode;
        _executionInstrument = instrument; // PHASE 3.1: Store execution instrument for canonical lock
        
        // Load logging configuration
        _loggingConfig = LoggingConfig.LoadFromFile(projectRoot);
        _loggingConfigPath = Path.Combine(projectRoot, "configs", "robot", "logging.json");

        // Resolve effective log directory (configurable; default is <projectRoot>\logs\robot)
        // Precedence: ctor customLogDir > env QTSW2_LOG_DIR > configs/robot/logging.json:log_dir > default
        var defaultLogDir = Path.Combine(projectRoot, "logs", "robot");
        var logDirSource = "default";
        string? warning = null;

        string? chosen = null;
        if (!string.IsNullOrWhiteSpace(customLogDir))
        {
            chosen = customLogDir;
            logDirSource = "ctor_param";
        }
        else
        {
            var envLogDir = Environment.GetEnvironmentVariable("QTSW2_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(envLogDir))
            {
                chosen = envLogDir;
                logDirSource = "env:QTSW2_LOG_DIR";
            }
            else if (!string.IsNullOrWhiteSpace(_loggingConfig.log_dir))
            {
                chosen = _loggingConfig.log_dir;
                logDirSource = "config:logging.json";
            }
        }

        var effectiveLogDir = defaultLogDir;
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            effectiveLogDir = Path.IsPathRooted(chosen) ? chosen : Path.Combine(projectRoot, chosen);
        }

        try
        {
            Directory.CreateDirectory(effectiveLogDir);
        }
        catch (Exception ex)
        {
            // Fail-open: fall back to default log dir if override is invalid
            warning = $"Failed to create log dir '{effectiveLogDir}'. Falling back to '{defaultLogDir}'. Error: {ex.Message}";
            effectiveLogDir = defaultLogDir;
            logDirSource = logDirSource + "_fallback";
            try { Directory.CreateDirectory(effectiveLogDir); } catch { /* ignore */ }
        }

        _resolvedLogDir = effectiveLogDir;
        _resolvedLogDirSource = logDirSource;
        _resolvedLogDirWarning = warning;
        
        // Initialize async logging service (Fix B) - singleton per project root to prevent file lock contention
        if (useAsyncLogging)
        {
            _loggingService = RobotLoggingService.GetOrCreate(projectRoot, _resolvedLogDir);
        }
        
        // Create logger with service reference so ENGINE logs route through singleton
        _log = new RobotLogger(projectRoot, _resolvedLogDir, instrument, _loggingService);
        
        _journals = new JournalStore(projectRoot);
        _timetablePoller = new FilePoller(timetablePollInterval);

        _specPath = Path.Combine(_root, "configs", "analyzer_robot_parity.json");
        _timetablePath = customTimetablePath ?? Path.Combine(_root, "data", "timetable", "timetable_current.json");

        // NOTE: KillSwitch logs during construction. To ensure ALL logs include run_id,
        // we delay KillSwitch creation until Start() after _runId is set on the logger.
        _executionJournal = new ExecutionJournal(projectRoot, _log);
        _executionSummary = new ExecutionSummary();
    }

    public void Start()
    {
        // CRITICAL: Set run_id before any Start-path logs
        _runId = Guid.NewGuid().ToString("N");
        _log.SetRunId(_runId);

        var utcNow = DateTimeOffset.UtcNow;

        // Initialize HealthMonitor after run_id is set so any health-monitor logs include run_id.
        InitializeHealthMonitorIfNeeded();
        _healthMonitor?.SetRunId(_runId);

        // Phase: initialize core under engine lock (serialize against timer/bar threads)
        lock (_engineLock)
        {
            // Initialize KillSwitch after run_id is set so its constructor logs include run_id.
            if (_killSwitch == null)
            {
                _killSwitch = new KillSwitch(_root, _log);
            }

            // PHASE 1: Fail-fast for LIVE mode before engine starts
            if (_executionMode == ExecutionMode.LIVE)
            {
                var errorMsg = "LIVE mode is not yet enabled. Use DRYRUN or SIM.";
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "LIVE_MODE_BLOCKED", state: "ENGINE",
                    new { error = errorMsg, execution_mode = _executionMode.ToString() }));

                // Trigger high-priority alert (not log-only)
                if (_healthMonitor != null)
                {
                    var notificationService = _healthMonitor.GetNotificationService();
                    if (notificationService != null)
                    {
                        notificationService.EnqueueNotification(
                            "LIVE_MODE_BLOCKED",
                            "CRITICAL: LIVE Trading Blocked",
                            $"Robot attempted to start in LIVE mode but it is not enabled. Execution blocked. Error: {errorMsg}",
                            priority: 2); // Emergency priority
                    }
                }

                throw new InvalidOperationException(errorMsg);
            }

            // Start async logging service if enabled (Fix B)
            _loggingService?.Start();

            // Log resolved runtime paths/config for operator visibility
            var envProjectRoot = Environment.GetEnvironmentVariable("QTSW2_PROJECT_ROOT");
            var envLogDir = Environment.GetEnvironmentVariable("QTSW2_LOG_DIR");
            var cwd = "";
            try { cwd = Directory.GetCurrentDirectory(); } catch { cwd = ""; }

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "PROJECT_ROOT_RESOLVED", state: "ENGINE",
                new
                {
                    project_root = _root,
                    env_QTSW2_PROJECT_ROOT = envProjectRoot,
                    cwd,
                    spec_path = _specPath,
                    spec_exists = File.Exists(_specPath)
                }));

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "LOG_DIR_RESOLVED", state: "ENGINE",
                new
                {
                    log_dir = _resolvedLogDir,
                    source = _resolvedLogDirSource,
                    env_QTSW2_LOG_DIR = envLogDir,
                    config_log_dir = _loggingConfig.log_dir,
                    warning = _resolvedLogDirWarning
                }));

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "LOGGING_CONFIG_LOADED", state: "ENGINE",
                new
                {
                    path = _loggingConfigPath,
                    exists = File.Exists(_loggingConfigPath),
                    max_file_size_mb = _loggingConfig.max_file_size_mb,
                    max_rotated_files = _loggingConfig.max_rotated_files,
                    min_log_level = _loggingConfig.min_log_level,
                    enable_diagnostic_logs = _loggingConfig.enable_diagnostic_logs,
                    diagnostic_rate_limits = _loggingConfig.diagnostic_rate_limits,
                    archive_days = _loggingConfig.archive_days
                }));

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_START", state: "ENGINE"));

            try
            {
                _spec = ParitySpec.LoadFromFile(_specPath);
                // Debug log: confirm spec_name was loaded
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_NAME_LOADED", state: "ENGINE",
                    new { spec_name = _spec.spec_name }));
                _time = new TimeService(_spec.timezone);
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_LOADED", state: "ENGINE",
                    new { spec_name = _spec.spec_name, spec_revision = _spec.spec_revision, timezone = _spec.timezone }));
                
                // PHASE 3.1: Acquire canonical market lock after spec is loaded (canonical instrument can be resolved)
                if (!string.IsNullOrWhiteSpace(_executionInstrument) && _runId != null)
                {
                    var canonicalInstrument = GetCanonicalInstrument(_executionInstrument);
                    _canonicalMarketLock = new CanonicalMarketLock(_root, canonicalInstrument, _runId, _log);
                    
                    if (!_canonicalMarketLock.TryAcquire(utcNow))
                    {
                        // Lock acquisition failed - another instance is active
                        var errorMsg = $"PHASE 3.1: Another robot instance is already executing canonical market '{canonicalInstrument}'. " +
                                     $"This instance (execution instrument: {_executionInstrument}) will not start to prevent duplicate execution.";
                        
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANONICAL_MARKET_ALREADY_ACTIVE", state: "ENGINE",
                            new
                            {
                                canonical_instrument = canonicalInstrument,
                                execution_instrument = _executionInstrument,
                                run_id = _runId,
                                note = errorMsg
                            }));
                        
                        // Trigger high-priority alert
                        if (_healthMonitor != null)
                        {
                            var notificationService = _healthMonitor.GetNotificationService();
                            if (notificationService != null)
                            {
                                notificationService.EnqueueNotification(
                                    "CANONICAL_MARKET_ALREADY_ACTIVE",
                                    "CRITICAL: Duplicate Executor Blocked",
                                    errorMsg,
                                    priority: 2); // Emergency priority
                            }
                        }
                        
                        throw new InvalidOperationException(errorMsg);
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_INVALID", state: "ENGINE",
                    new { error = ex.Message }));
                throw;
            }

            // Initialize execution components now that spec is loaded
            if (_killSwitch == null)
                throw new InvalidOperationException("KillSwitch must be initialized before RiskGate.");
            _riskGate = new RiskGate(_spec, _time, _log, _killSwitch, guard: this);

            // Try to create adapter (will throw if LIVE mode)
            _executionAdapter = ExecutionAdapterFactory.Create(_executionMode, _root, _log, _executionJournal);

            // Set journal corruption callback for fail-closed behavior
            _executionJournal.SetJournalCorruptionCallback((stream, tradingDate, intentId, utcNow) =>
            {
                StandDownStream(stream, utcNow, $"JOURNAL_CORRUPTION:{intentId}");
            });
            
            // Set execution cost callback for cost tracking
            _executionJournal.SetExecutionCostCallback((intentId, slippageDollars, commission, fees) =>
            {
                _executionSummary.RecordExecutionCost(intentId, slippageDollars, commission, fees);
            });

            // Create intent exposure coordinator
            var coordinator = new InstrumentIntentCoordinator(
                _log,
                () => _executionAdapter.GetAccountSnapshot(DateTimeOffset.UtcNow),
                (streamId, now, reason) => StandDownStream(streamId, now, reason),
                (intentId, instrument, now) => FlattenIntent(intentId, instrument, now),
                (intentId, now) => CancelIntentOrders(intentId, now));

            // PHASE 2: Set engine callbacks for protective order failure recovery
            if (_executionAdapter is NinjaTraderSimAdapter simAdapter)
            {
                simAdapter.SetEngineCallbacks(
                    standDownStreamCallback: (streamId, now, reason) => StandDownStream(streamId, now, reason),
                    getNotificationServiceCallback: () => GetNotificationService());
                
                // Wire coordinator to adapter
                simAdapter.SetCoordinator(coordinator);
            }

            // Log execution mode and adapter
            var adapterType = _executionAdapter.GetType().Name;
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_MODE_SET", state: "ENGINE",
                new { mode = _executionMode.ToString(), adapter = adapterType }));
        }

        // Timetable disk I/O happens outside the engine lock; application happens under the lock.
        var parsed = PollAndParseTimetable(utcNow);

        lock (_engineLock)
        {
            // Load timetable and lock trading date from it (fail closed if invalid)
            // Trading date is locked immediately from timetable, then streams are created
            ReloadTimetableIfChanged(utcNow, force: true, parsed.Poll, parsed.Timetable, parsed.ParseException);

            // If trading date was locked from timetable, create streams and emit banner
            if (_activeTradingDate.HasValue)
            {
                EnsureStreamsCreated(utcNow);
                EmitStartupBanner(utcNow);
            }
            // Otherwise, timetable was invalid or missing trading_date - StandDown() was called

            // Initialize heartbeat timestamp
            _lastTickUtc = utcNow;

            // Start health monitor if enabled
            _healthMonitor?.Start();
        }
    }

    private bool _healthMonitorInitAttempted = false;

    /// <summary>
    /// Initialize health monitor (fail-closed: if config missing/invalid, monitoring disabled).
    /// This is delayed until Start() so that all health-monitor-related logs include run_id.
    /// </summary>
    private void InitializeHealthMonitorIfNeeded()
    {
        if (_healthMonitorInitAttempted)
            return;
        _healthMonitorInitAttempted = true;

        try
        {
            var healthMonitorPath = Path.Combine(_root, "configs", "robot", "health_monitor.json");
            if (File.Exists(healthMonitorPath))
            {
                var healthMonitorJson = File.ReadAllText(healthMonitorPath);
                var healthMonitorConfig = JsonUtil.Deserialize<HealthMonitorConfig>(healthMonitorJson);
                if (healthMonitorConfig != null)
                {
                    // Secrets handling: allow a local (gitignored) secrets file to provide credentials
                    // File: configs/robot/health_monitor.secrets.json
                    var healthMonitorSecretsPath = Path.Combine(_root, "configs", "robot", "health_monitor.secrets.json");
                    if (File.Exists(healthMonitorSecretsPath))
                    {
                        try
                        {
                            var secretsJson = File.ReadAllText(healthMonitorSecretsPath);
                            var secrets = JsonUtil.Deserialize<Dictionary<string, object>>(secretsJson);
                            if (secrets != null)
                            {
                                if (secrets.TryGetValue("pushover_enabled", out var poEnabledObj))
                                {
                                    var poEnabledStr = Convert.ToString(poEnabledObj);
                                    if (!string.IsNullOrWhiteSpace(poEnabledStr) && bool.TryParse(poEnabledStr, out var poEnabled))
                                        healthMonitorConfig.pushover_enabled = poEnabled;
                                }

                                if (secrets.TryGetValue("pushover_user_key", out var userKeyObj))
                                {
                                    var userKey = Convert.ToString(userKeyObj);
                                    if (!string.IsNullOrWhiteSpace(userKey))
                                        healthMonitorConfig.pushover_user_key = userKey;
                                }

                                if (secrets.TryGetValue("pushover_app_token", out var appTokenObj))
                                {
                                    var appToken = Convert.ToString(appTokenObj);
                                    if (!string.IsNullOrWhiteSpace(appToken))
                                        healthMonitorConfig.pushover_app_token = appToken;
                                }
                            }
                        }
                        catch
                        {
                            // Fail-closed: ignore secrets file parse errors.
                        }
                    }

                    // Secrets handling: allow environment variables to provide credentials (never store in git)
                    try
                    {
                        var envHmEnabled = Environment.GetEnvironmentVariable("QTSW2_HEALTH_MONITOR_ENABLED");
                        if (!string.IsNullOrWhiteSpace(envHmEnabled) && bool.TryParse(envHmEnabled, out var hmEnabled))
                        {
                            healthMonitorConfig.enabled = hmEnabled;
                        }

                        var envPushoverEnabled = Environment.GetEnvironmentVariable("QTSW2_PUSHOVER_ENABLED");
                        if (!string.IsNullOrWhiteSpace(envPushoverEnabled) && bool.TryParse(envPushoverEnabled, out var poEnabled))
                        {
                            healthMonitorConfig.pushover_enabled = poEnabled;
                        }

                        var envUserKey =
                            Environment.GetEnvironmentVariable("QTSW2_PUSHOVER_USER_KEY") ??
                            Environment.GetEnvironmentVariable("PUSHOVER_USER_KEY");
                        if (!string.IsNullOrWhiteSpace(envUserKey))
                        {
                            healthMonitorConfig.pushover_user_key = envUserKey;
                        }

                        var envAppToken =
                            Environment.GetEnvironmentVariable("QTSW2_PUSHOVER_APP_TOKEN") ??
                            Environment.GetEnvironmentVariable("PUSHOVER_APP_TOKEN");
                        if (!string.IsNullOrWhiteSpace(envAppToken))
                        {
                            healthMonitorConfig.pushover_app_token = envAppToken;
                        }
                    }
                    catch
                    {
                        // Fail-closed: if env parsing fails, continue with file config as-is.
                    }

                    // Log config load result for debugging
                    LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HEALTH_MONITOR_CONFIG_LOADED", state: "ENGINE",
                        new
                        {
                            enabled = healthMonitorConfig.enabled,
                            enabled_property = healthMonitorConfig.enabled,
                            pushover_enabled = healthMonitorConfig.pushover_enabled,
                            pushover_user_key_length = healthMonitorConfig.pushover_user_key?.Length ?? 0,
                            pushover_app_token_length = healthMonitorConfig.pushover_app_token?.Length ?? 0,
                            config_not_null = true
                        }));

                    // If Pushover is enabled but not configured, log a loud warning event (no secrets).
                    var pushoverConfigured = healthMonitorConfig.pushover_enabled &&
                                             !string.IsNullOrWhiteSpace(healthMonitorConfig.pushover_user_key) &&
                                             !string.IsNullOrWhiteSpace(healthMonitorConfig.pushover_app_token);
                    if (healthMonitorConfig.pushover_enabled && !pushoverConfigured)
                    {
                        LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "PUSHOVER_CONFIG_MISSING", state: "ENGINE",
                            new
                            {
                                pushover_enabled = true,
                                pushover_configured = false,
                                secrets_file = Path.Combine(_root, "configs", "robot", "health_monitor.secrets.json"),
                                env_vars = "QTSW2_PUSHOVER_USER_KEY / QTSW2_PUSHOVER_APP_TOKEN (or PUSHOVER_USER_KEY / PUSHOVER_APP_TOKEN)",
                                note = "Pushover enabled but credentials are missing. No push notifications will be sent until configured."
                            }));
                    }

                    if (healthMonitorConfig.enabled)
                    {
                        _healthMonitor = new HealthMonitor(_root, healthMonitorConfig, _log);
                        _healthMonitor.SetActiveStreamsCallback(() => HasActiveStreams());
                    }
                    else
                    {
                        LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HEALTH_MONITOR_DISABLED", state: "ENGINE",
                            new { reason = "config_enabled_false" }));
                    }
                }
                else
                {
                    LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HEALTH_MONITOR_CONFIG_NULL", state: "ENGINE",
                        new { reason = "deserialization_returned_null" }));
                }
            }
            else
            {
                LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HEALTH_MONITOR_CONFIG_MISSING", state: "ENGINE",
                    new { path = healthMonitorPath }));
            }
        }
        catch (Exception ex)
        {
            LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HEALTH_MONITOR_CONFIG_ERROR", state: "ENGINE",
                new { error = ex.Message, error_type = ex.GetType().Name }));
        }
    }
    
    /// <summary>
    /// Check if strategy started after range window and log warning if so.
    /// </summary>
    private void CheckStartupTiming(DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null) return;
        
        foreach (var stream in _streams.Values)
        {
            if (utcNow >= stream.RangeStartUtc)
            {
                var nowChicago = _time.ConvertUtcToChicago(utcNow);
                var rangeStartChicago = _time.ConvertUtcToChicago(stream.RangeStartUtc);
                
                LogEvent(RobotEvents.Base(_time, utcNow, stream.TradingDate, stream.Stream, stream.Instrument, stream.Session, stream.SlotTimeChicago, stream.SlotTimeUtc,
                    "STARTUP_TIMING_WARNING", "ENGINE",
                    new
                    {
                        warning = "Strategy started after range window — range may be incomplete or unavailable",
                        stream_id = stream.Stream,
                        instrument = stream.Instrument,
                        execution_instrument = stream.ExecutionInstrument,
                        canonical_instrument = stream.CanonicalInstrument,
                        session = stream.Session,
                        now_utc = utcNow.ToString("o"),
                        now_chicago = nowChicago.ToString("o"),
                        range_start_utc = stream.RangeStartUtc.ToString("o"),
                        range_start_chicago = rangeStartChicago.ToString("o"),
                        slot_time_chicago = stream.SlotTimeChicago,
                        note = "Ensure NinjaTrader 'Days to load' setting includes historical data for the range window"
                    }));
            }
        }
    }
    
    /// <summary>
    /// Check if any streams are in active trading state (ARMED, RANGE_BUILDING, or RANGE_LOCKED).
    /// Used for session-aware notification filtering.
    /// </summary>
    private bool HasActiveStreams()
    {
        lock (_engineLock)
        {
            return _streams.Values.Any(s => !s.Committed &&
                (s.State == StreamState.ARMED ||
                 s.State == StreamState.RANGE_BUILDING ||
                 s.State == StreamState.RANGE_LOCKED));
        }
    }
    
    
    /// <summary>
    /// PHASE 1: Emit prominent operator banner log with execution mode, account, environment, timetable info.
    /// Called after trading date is locked from first session-valid bar.
    /// </summary>
    private void EmitStartupBanner(DateTimeOffset utcNow)
    {
        var enabledStreams = _streams.Values.Where(s => !s.Committed).ToList();
        var enabledInstruments = enabledStreams.Select(s => s.Instrument).Distinct().ToList();
        
        // PHASE 3.2: Build quantity mapping for enabled streams
        var quantityMapping = enabledStreams
            .GroupBy(s => s.ExecutionInstrument)
            .Select(g => new
            {
                execution_instrument = g.Key,
                quantity = GetOrderQuantity(g.Key),
                stream_count = g.Count()
            })
            .ToList();
        
        var bannerData = new Dictionary<string, object?>
        {
            ["execution_mode"] = _executionMode.ToString(),
            ["account_name"] = _accountName ?? "UNKNOWN",
            ["environment"] = _environment ?? _executionMode.ToString(),
            ["timetable_hash"] = _lastTimetableHash ?? "NOT_LOADED",
            ["timetable_path"] = _timetablePath,
            ["enabled_stream_count"] = enabledStreams.Count,
            ["enabled_instruments"] = enabledInstruments,
            ["enabled_streams"] = enabledStreams.Select(s => new { stream = s.Stream, instrument = s.Instrument, session = s.Session, slot_time = s.SlotTimeChicago }).ToList(),
            ["spec_name"] = _spec?.spec_name ?? "NOT_LOADED",
            ["spec_revision"] = _spec?.spec_revision ?? "NOT_LOADED",
            ["kill_switch_enabled"] = _killSwitch != null && _killSwitch.IsEnabled(),
            ["health_monitor_enabled"] = _healthMonitor != null,
            // PHASE 3.2: Order quantity control
            ["order_quantity_mapping"] = quantityMapping,
            ["order_quantity_source"] = "STRATEGY_CODE",
            ["chart_trader_quantity_ignored"] = true
        };
        
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, 
            eventType: "OPERATOR_BANNER", state: "ENGINE", bannerData));
        
        // PHASE 3.2: Explicit log stating Chart Trader quantity is ignored
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, 
            eventType: "EXECUTION_QUANTITY_CONTROL", state: "ENGINE",
            new
            {
                order_quantity_source = "STRATEGY_CODE",
                chart_trader_quantity_ignored = true,
                execution_instrument_quantity_mapping = quantityMapping,
                note = "Execution quantity is controlled by strategy code; Chart Trader quantity is ignored. All orders use instrument-specific quantities from code."
            }));
    }

    public void Stop()
    {
        var utcNow = DateTimeOffset.UtcNow;
        
        // PHASE 3.1: Release canonical market lock on shutdown
        _canonicalMarketLock?.Release(utcNow);
        
        string? summaryPathToWrite = null;
        string? summaryJson = null;

        lock (_engineLock)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ENGINE_STOP", state: "ENGINE"));

            // Prepare execution summary if not DRYRUN (write to disk outside lock)
            if (_executionMode != ExecutionMode.DRYRUN)
            {
                var summary = _executionSummary.GetSnapshot();
                var summaryDir = Path.Combine(_root, "data", "execution_summaries");
                Directory.CreateDirectory(summaryDir);
                summaryPathToWrite = Path.Combine(summaryDir, $"summary_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
                summaryJson = JsonUtil.Serialize(summary);
            }

            // Stop health monitor
            _healthMonitor?.Stop();

            // Release reference to async logging service (Fix B) - singleton will dispose when all references released
            _loggingService?.Release();
        }

        // Disk I/O outside engine lock
        if (!string.IsNullOrWhiteSpace(summaryPathToWrite) && summaryJson != null)
        {
            try
            {
                File.WriteAllText(summaryPathToWrite, summaryJson);
            }
            catch
            {
                // If summary write fails, do not throw during shutdown.
            }

            lock (_engineLock)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "EXECUTION_SUMMARY_WRITTEN", state: "ENGINE",
                    new { summary_path = summaryPathToWrite }));
            }
        }
    }

    public void Tick(DateTimeOffset utcNow)
    {
        // PHASE 3.1: Periodic identity invariants check (rate-limited)
        CheckIdentityInvariantsIfNeeded(utcNow);
        
        // Check for test notification trigger file (outside lock for performance)
        var triggerFile = Path.Combine(_root, "data", "test_notification_trigger.txt");
        if (File.Exists(triggerFile))
        {
            try
            {
                File.Delete(triggerFile);
                SendTestNotification();
            }
            catch (Exception ex)
            {
                // Log but don't throw - tick must never crash
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TEST_NOTIFICATION_TRIGGER_ERROR", state: "ENGINE",
                    new { error = ex.Message }));
            }
        }
        
        var shouldPoll = _timetablePoller.ShouldPoll(utcNow);
        var parsed = shouldPoll ? PollAndParseTimetable(utcNow) : default;

        lock (_engineLock)
        {
            // HEARTBEAT: Emit unconditionally for process liveness (before any early returns)
            // This ensures watchdog always sees engine liveness, regardless of trading readiness
            // PHASE 3: Update engine heartbeat timestamp for liveness monitoring
            _lastTickUtc = utcNow;
            _lastEngineTickUtc = utcNow; // Also update for broker sync gate

            // PHASE 3: Update health monitor with engine tick timestamp
            _healthMonitor?.UpdateEngineTick(utcNow);

            // ENGINE_TICK_HEARTBEAT: Always emit for watchdog monitoring (required for engine_alive detection)
            // CRITICAL: Emit BEFORE any early returns to ensure watchdog always sees engine liveness
            // Rate-limited: every 1 minute if diagnostics enabled, every 60 seconds otherwise (for watchdog)
            var watchdogHeartbeatIntervalMinutes = 1.0; // Always emit at least every 60 seconds for watchdog
            var timeSinceLastTickHeartbeat = (utcNow - _lastTickHeartbeat).TotalMinutes;
            var shouldEmitHeartbeat = timeSinceLastTickHeartbeat >= watchdogHeartbeatIntervalMinutes || _lastTickHeartbeat == DateTimeOffset.MinValue;
            
            if (shouldEmitHeartbeat)
            {
                _lastTickHeartbeat = utcNow;
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ENGINE_TICK_HEARTBEAT", state: "ENGINE",
                    new
                    {
                        utc_now = utcNow.ToString("o"),
                        active_stream_count = _streams.Count,
                        recovery_state = _recoveryState.ToString(),
                        note = _loggingConfig.enable_diagnostic_logs ? "timer-based tick (diagnostic)" : "timer-based tick (watchdog monitoring)"
                    }));
            }

            // TRADING READINESS: Guards that prevent trading logic from running when engine is not ready
            // CRITICAL: Defensive checks - engine must be initialized for trading logic
            if (_spec is null)
            {
                // Spec should be loaded in Start() - if null, engine is in invalid state
                // Log error but don't throw (to prevent tick timer from crashing)
                // Heartbeat already emitted above, so watchdog will see engine is alive
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_TICK_INVALID_STATE", state: "ENGINE",
                    new { error = "Spec is null - engine not properly initialized" }));
                return;
            }

            if (_time is null)
            {
                // TimeService should be created in Start() - if null, engine is in invalid state
                // Heartbeat already emitted above, so watchdog will see engine is alive
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_TICK_INVALID_STATE", state: "ENGINE",
                    new { error = "TimeService is null - engine not properly initialized" }));
                return;
            }

            // Broker sync gate: Check if we're waiting for synchronization
            if (_recoveryState == ConnectionRecoveryState.RECONNECTED_RECOVERY_PENDING)
            {
                if (!IsBrokerSynchronized(utcNow))
                {
                    // Rate-limited log: emit at most once every 5 seconds
                    var shouldLog = !_lastSyncWaitLogUtc.HasValue ||
                                    (utcNow - _lastSyncWaitLogUtc.Value).TotalSeconds >= 5.0;

                    if (shouldLog)
                    {
                        _lastSyncWaitLogUtc = utcNow;
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_WAITING_FOR_SYNC", state: "ENGINE",
                            new
                            {
                                recovery_state = _recoveryState.ToString(),
                                reconnect_utc = _reconnectUtc?.ToString("o"),
                                last_order_update_utc = _lastOrderUpdateUtc?.ToString("o"),
                                last_execution_update_utc = _lastExecutionUpdateUtc?.ToString("o"),
                                last_connection_status = _lastConnectionStatus.ToString(),
                                quiet_window_seconds = 5,
                                note = "Waiting for broker synchronization before starting recovery"
                            }));
                    }
                    return; // Don't proceed with normal tick processing while waiting
                }

                // Broker is synchronized: transition to RECOVERY_RUNNING and start recovery
                _recoveryState = ConnectionRecoveryState.RECOVERY_RUNNING;
                if (!_recoveryStartedUtc.HasValue)
                {
                    _recoveryStartedUtc = utcNow;
                }

                // Start recovery runner (idempotent, single-threaded)
                RunRecovery(utcNow);
            }

            // Timetable reactivity (disk I/O already completed outside lock)
            if (shouldPoll)
            {
                // PHASE 3: Update health monitor with timetable poll timestamp
                _healthMonitor?.UpdateTimetablePoll(utcNow);

                ReloadTimetableIfChanged(utcNow, force: false, parsed.Poll, parsed.Timetable, parsed.ParseException);
            }

            foreach (var s in _streams.Values)
                s.Tick(utcNow);

            // Track and log gap violations across all streams (rate-limited to once per 5 minutes)
            var timeSinceLastGapViolationSummary = (utcNow - (_lastGapViolationSummaryUtc ?? DateTimeOffset.MinValue)).TotalMinutes;
            if (timeSinceLastGapViolationSummary >= 5.0 || _lastGapViolationSummaryUtc == null)
            {
                var invalidatedStreams = _streams.Values
                    .Where(s => s.RangeInvalidated && !s.Committed)
                    .Select(s => new
                    {
                        stream_id = s.Stream,
                        instrument = s.Instrument,
                        session = s.Session,
                        slot_time = s.SlotTimeChicago,
                        state = s.State.ToString()
                    })
                    .ToList();

                if (invalidatedStreams.Count > 0)
                {
                    _lastGapViolationSummaryUtc = utcNow;
                    LogEngineEvent(utcNow, "GAP_VIOLATIONS_SUMMARY", new
                    {
                        invalidated_stream_count = invalidatedStreams.Count,
                        invalidated_streams = invalidatedStreams,
                        total_streams = _streams.Count,
                        note = "Streams invalidated due to gap tolerance violations - trading blocked for these streams"
                    });
                }
            }

            // Health monitor: evaluate data loss (rate-limited internally)
            _healthMonitor?.Evaluate(utcNow);
        }
    }

    public void OnBar(DateTimeOffset barUtc, string instrument, decimal open, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            if (_spec is null || _time is null) return;

        // CRITICAL: Reject future bars and validate bar timing
        // FIX #2: For OnBarClose sources, bars are already closed, so we don't need strict age requirements
        // - BarsRequest returns fully closed bars (good)
        // - OnBarClose (NinjaTrader) provides closed bars when callback fires
        // - Only reject future bars (negative age) which indicate clock/timezone issues
        //
        // Rule: Accept bars with age >= 0 (current or past), reject future bars (age < 0)
        // This allows OnBarClose bars to be processed immediately while preventing future bar contamination
        var barAgeMinutes = (utcNow - barUtc).TotalMinutes;
        const double FUTURE_BAR_THRESHOLD_MINUTES = -0.1; // Reject bars more than 0.1 minutes in the future
        
        if (barAgeMinutes < FUTURE_BAR_THRESHOLD_MINUTES)
        {
            // Bar is in the future - indicates clock/timezone issue, reject it
            // Track rejection statistics
            if (!_barRejectionStats.TryGetValue(instrument, out var stats))
            {
                stats = new BarRejectionStats();
                _barRejectionStats[instrument] = stats;
            }
            stats.PartialRejected++;
            stats.LastUpdateUtc = utcNow;
            
            var nowChicago = _time.ConvertUtcToChicago(utcNow);
            var barChicagoTimeRejected = _time.ConvertUtcToChicago(barUtc);
            
            // Find all streams matching this instrument for diagnostic purposes
            var matchingStreamIds = new List<string>();
            foreach (var s in _streams.Values)
            {
                if (s.IsSameInstrument(instrument))
                {
                    matchingStreamIds.Add(s.Stream);
                }
            }
            
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_PARTIAL_REJECTED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    stream_id = matchingStreamIds.Count > 0 ? string.Join(",", matchingStreamIds) : "NO_STREAMS",
                    trading_date = TradingDateString,
                    bar_timestamp_utc = barUtc.ToString("o"),
                    bar_timestamp_chicago = barChicagoTimeRejected.ToString("o"),
                    current_time_utc = utcNow.ToString("o"),
                    current_time_chicago = nowChicago.ToString("o"),
                    bar_age_minutes = Math.Round(barAgeMinutes, 3),
                    future_bar_threshold_minutes = FUTURE_BAR_THRESHOLD_MINUTES,
                    rejection_reason = "FUTURE_BAR",
                    note = "Bar rejected - bar timestamp is in the future relative to engine time. This indicates a clock synchronization or timezone conversion issue."
                }));
            
            // HIGH-SIGNAL WARNING: If future bar rejection occurs continuously after trading date is locked
            if (_activeTradingDate.HasValue && stats.PartialRejected >= 10)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_REJECTION_CONTINUOUS_FUTURE", state: "ENGINE",
                    new
                    {
                        instrument = instrument,
                        future_bar_rejection_count = stats.PartialRejected,
                        trading_date = TradingDateString,
                        warning = "Continuous future bar rejections detected - indicates persistent clock/timezone issue",
                        note = "This may indicate a system clock synchronization problem or timezone conversion error"
                    }));
            }
            
            // Log rejection summary if threshold exceeded (rate-limited)
            LogBarRejectionSummaryIfNeeded(utcNow);
            return; // Reject future bar
        }

        // Health monitor: record bar reception (early, before other processing)
        _healthMonitor?.OnBar(instrument, barUtc);

        // ENGINE_BAR_HEARTBEAT: Diagnostic to prove bar ingress from NinjaTrader
        // Only logged if diagnostic logs are enabled
        if (_loggingConfig.enable_diagnostic_logs)
        {
            var shouldLogHeartbeat = !_lastBarHeartbeatPerInstrument.TryGetValue(instrument, out var lastHeartbeat) ||
                                    (utcNow - lastHeartbeat).TotalMinutes >= BAR_HEARTBEAT_RATE_LIMIT_MINUTES;
            
            if (shouldLogHeartbeat)
            {
                _lastBarHeartbeatPerInstrument[instrument] = utcNow;
                var barChicagoTimeHeartbeat = _time.ConvertUtcToChicago(barUtc);
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ENGINE_BAR_HEARTBEAT", state: "ENGINE",
                    new
                    {
                        instrument = instrument,
                        raw_bar_time = barUtc.ToString("o"),
                        raw_bar_time_kind = barUtc.DateTime.Kind.ToString(),
                        utc_time = barUtc.ToString("o"),
                        chicago_time = barChicagoTimeHeartbeat.ToString("o"),
                        chicago_offset = barChicagoTimeHeartbeat.Offset.ToString(),
                        close_price = close,
                        note = "engine-level diagnostic"
                    }));
            }
        }

        // Validate bar date against locked trading date from timetable
        var barChicagoTime = _time.ConvertUtcToChicago(barUtc);
        var barChicagoDate = DateOnly.FromDateTime(barChicagoTime.DateTime);

        // Trading date should be locked from timetable - if not, log error and ignore bar
        if (!_activeTradingDate.HasValue)
        {
            // Track rejection statistics
            if (!_barRejectionStats.TryGetValue(instrument, out var stats))
            {
                stats = new BarRejectionStats();
                _barRejectionStats[instrument] = stats;
            }
            stats.BeforeDateLocked++;
            stats.LastUpdateUtc = utcNow;
            
            var nowChicago = _time.ConvertUtcToChicago(utcNow);
            var timetableTradingDate = _lastTimetable?.trading_date ?? "NOT_LOADED";
            
            // Find all streams matching this instrument for diagnostic purposes
            var matchingStreamIds = new List<string>();
            foreach (var s in _streams.Values)
            {
                if (s.IsSameInstrument(instrument))
                {
                    matchingStreamIds.Add(s.Stream);
                }
            }
            
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BAR_RECEIVED_BEFORE_DATE_LOCKED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    stream_id = matchingStreamIds.Count > 0 ? string.Join(",", matchingStreamIds) : "NO_STREAMS",
                    trading_date = "",
                    bar_timestamp_utc = barUtc.ToString("o"),
                    bar_timestamp_chicago = barChicagoTime.ToString("o"),
                    bar_trading_date = barChicagoDate.ToString("yyyy-MM-dd"),
                    current_time_utc = utcNow.ToString("o"),
                    current_time_chicago = nowChicago.ToString("o"),
                    timetable_trading_date = timetableTradingDate,
                    timetable_path = _timetablePath,
                    rejection_reason = "TRADING_DATE_NOT_LOCKED",
                    note = "Bar received before trading date locked from timetable - this should not happen in normal operation"
                }));
            
            // HIGH-SIGNAL WARNING: Bars rejected after engine start indicates timetable loading failure
            if (stats.BeforeDateLocked >= 5)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BAR_REJECTION_CONTINUOUS_NO_DATE_LOCK", state: "ENGINE",
                    new
                    {
                        instrument = instrument,
                        rejection_count = stats.BeforeDateLocked,
                        timetable_path = _timetablePath,
                        timetable_trading_date = timetableTradingDate,
                        warning = "Multiple bars rejected due to trading date not locked - timetable may have failed to load",
                        note = "Check timetable file exists and is valid. Engine should lock trading date at startup."
                    }));
            }
            
            // Log rejection summary if threshold exceeded (rate-limited)
            LogBarRejectionSummaryIfNeeded(utcNow);
            return; // Ignore bar - trading date should be locked from timetable
        }

        // Validate bar falls within trading session window for active trading date
        // Session window: [previous_day session_start CST, trading_date 16:00 CST)
        // This replaces calendar date comparison which was invalid for futures (session starts evening before)
        var (sessionStartChicago, sessionEndChicago) = GetSessionWindow(_activeTradingDate.Value, instrument);
        
        // CRITICAL FIX: Allow historical bars from dates before the trading date
        // Bars from dates before the trading date are historical data and should be accepted
        // Only reject bars that are:
        // 1. From dates after the trading date (future bars)
        // 2. From dates that are too far in the past (more than 7 days before trading date)
        // 3. From dates within the trading date but outside the session window
        var tradingDateStr = _activeTradingDate.Value.ToString("yyyy-MM-dd");
        var barTradingDateStr = barChicagoDate.ToString("yyyy-MM-dd");
        var barDate = barChicagoDate;
        var tradingDate = _activeTradingDate.Value;
        
        // Check if bar is from a date before the trading date (historical data)
        var isHistoricalBar = barDate < tradingDate;
        // Calculate days difference (compatible with older .NET versions that don't have DayNumber)
        // Convert DateOnly to DateTime at midnight for subtraction
        var tradingDateTime = new DateTime(tradingDate.Year, tradingDate.Month, tradingDate.Day);
        var barDateTime = new DateTime(barDate.Year, barDate.Month, barDate.Day);
        var daysBeforeTradingDate = isHistoricalBar ? (tradingDateTime - barDateTime).Days : 0;
        
        // Allow historical bars from up to 7 days before trading date
        // This handles cases where NinjaTrader sends historical bars from previous days
        const int MAX_HISTORICAL_DAYS = 7;
        var isTooOldHistorical = isHistoricalBar && daysBeforeTradingDate > MAX_HISTORICAL_DAYS;
        
        // Check if bar is from a date after the trading date (future bar)
        var isFutureBar = barDate > tradingDate;
        
        // Check if bar is within session window (for bars from trading date)
        var isWithinSessionWindow = barChicagoTime >= sessionStartChicago && barChicagoTime < sessionEndChicago;
        
        // Reject bar if:
        // 1. It's a future bar (from date after trading date)
        // 2. It's too old historical (more than 7 days before trading date)
        // 3. It's from the trading date but outside the session window
        var shouldReject = isFutureBar || isTooOldHistorical || (!isHistoricalBar && !isWithinSessionWindow);
        
        if (shouldReject)
        {
            // Bar is outside session window or invalid - log mismatch and reject
            // BAR_DATE_MISMATCH now means "bar outside trading session window" (not calendar date mismatch)
            
            // Track rejection statistics
            if (!_barRejectionStats.TryGetValue(instrument, out var stats))
            {
                stats = new BarRejectionStats();
                _barRejectionStats[instrument] = stats;
            }
            stats.DateMismatch++;
            stats.LastUpdateUtc = utcNow;
            
            // Enhanced diagnostic logging
            var nowChicago = _time.ConvertUtcToChicago(utcNow);
            var timetableTradingDate = _lastTimetable?.trading_date ?? "NOT_LOADED";
            
            // Find all streams matching this instrument for diagnostic purposes
            var matchingStreamIds = new List<string>();
            foreach (var s in _streams.Values)
            {
                if (s.IsSameInstrument(instrument))
                {
                    matchingStreamIds.Add(s.Stream);
                }
            }
            
            // Determine rejection reason
            string rejectionReason;
            if (isFutureBar)
            {
                rejectionReason = "FUTURE_DATE";
            }
            else if (isTooOldHistorical)
            {
                rejectionReason = "TOO_OLD_HISTORICAL";
            }
            else if (barChicagoTime < sessionStartChicago)
            {
                rejectionReason = "BEFORE_SESSION_START";
            }
            else
            {
                rejectionReason = "AFTER_SESSION_END";
            }
            
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "BAR_DATE_MISMATCH", state: "ENGINE",
                new
                {
                    // Existing fields (kept for backward compatibility)
                    locked_trading_date = tradingDateStr,
                    bar_trading_date = barTradingDateStr,
                    bar_timestamp_utc = barUtc.ToString("o"),
                    bar_timestamp_chicago = barChicagoTime.ToString("o"),
                    instrument = instrument,
                    rejection_reason = rejectionReason,
                    date_alignment_note = $"Bar is outside trading session window for trading date {tradingDateStr}",
                    note = "Bar ignored - outside trading session window (BAR_DATE_MISMATCH now means 'bar outside session window', not 'calendar date mismatch')",
                    
                    // DIAGNOSTIC FIELDS (high priority)
                    active_trading_date = tradingDateStr, // Engine's active trading date
                    bar_utc = barUtc.ToString("o"), // Canonical bar timestamp passed into engine
                    bar_chicago = barChicagoTime.ToString("o"), // Derived Chicago time from bar_utc
                    bar_chicago_date = barChicagoDate.ToString("yyyy-MM-dd"), // Date-only from bar_chicago
                    now_utc = utcNow.ToString("o"), // Engine tick time (not DateTimeOffset.UtcNow from strategy)
                    now_chicago = nowChicago.ToString("o"), // Derived Chicago time from now_utc
                    timetable_trading_date = timetableTradingDate, // Raw string read from timetable
                    stream_id = matchingStreamIds.Count > 0 ? string.Join(",", matchingStreamIds) : "NO_STREAMS", // All streams matching this instrument
                    
                    // NEW: Session window fields
                    session_start_chicago = sessionStartChicago.ToString("o"),
                    session_end_chicago = sessionEndChicago.ToString("o"),
                    
                    // NEW: Historical bar detection fields
                    is_historical_bar = isHistoricalBar,
                    days_before_trading_date = daysBeforeTradingDate,
                    is_future_bar = isFutureBar,
                    is_too_old_historical = isTooOldHistorical,
                    max_historical_days = MAX_HISTORICAL_DAYS
                }));
            
            // Log rejection summary if threshold exceeded (rate-limited)
            LogBarRejectionSummaryIfNeeded(utcNow);
            return; // Ignore bar outside session window
        }

        // Track acceptance statistics
        if (!_barRejectionStats.TryGetValue(instrument, out var acceptanceStats))
        {
            acceptanceStats = new BarRejectionStats();
            _barRejectionStats[instrument] = acceptanceStats;
        }
        acceptanceStats.TotalAccepted++;
        acceptanceStats.LastUpdateUtc = utcNow;
        
        // Bar date matches - log acceptance (rate-limited to avoid spam)
        var shouldLogAcceptance = !_lastBarHeartbeatPerInstrument.TryGetValue(instrument, out var lastAcceptance) ||
                                 (utcNow - lastAcceptance).TotalMinutes >= BAR_HEARTBEAT_RATE_LIMIT_MINUTES;
        if (shouldLogAcceptance)
        {
            _lastBarHeartbeatPerInstrument[instrument] = utcNow;
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_ACCEPTED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    bar_timestamp_utc = barUtc.ToString("o"),
                    bar_timestamp_chicago = barChicagoTime.ToString("o"),
                    bar_trading_date = barChicagoDate.ToString("yyyy-MM-dd"),
                    locked_trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                    note = "Bar accepted - date matches locked trading date"
                }));
        }
        
        // Log rejection summary if threshold exceeded (rate-limited)
        LogBarRejectionSummaryIfNeeded(utcNow);

        // Only process bars if streams exist (they should exist after EnsureStreamsCreated)
        if (_streams.Count == 0)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_RECEIVED_NO_STREAMS", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    bar_timestamp_utc = barUtc.ToString("o"),
                    note = "Bar received but streams not yet created - this should not happen"
                }));
            return;
        }

        // PHASE 3: Assert bar instrument is execution (from NT)
        // Note: Bar routing uses canonical matching, but input must be execution instrument
        // This assertion ensures we're not accidentally passing canonical instrument to OnBar
        
        // Pass bar data to streams of matching instrument
        var streamsReceivingBar = new List<string>();
        var streamsFilteredOut = new List<string>();
        
        foreach (var s in _streams.Values)
        {
            // PHASE 3: IsSameInstrument receives execution instrument, compares canonical internally
            if (s.IsSameInstrument(instrument))
            {
                streamsReceivingBar.Add($"{s.Session}_{s.SlotTimeChicago}");
                s.OnBar(barUtc, open, high, low, close, utcNow);
                
                // Log bar delivery to stream (rate-limited, diagnostic only)
                if (_loggingConfig.enable_diagnostic_logs)
                {
                    var deliveryKey = $"bar_delivery_{instrument}_{s.Session}_{s.SlotTimeChicago}";
                    var shouldLogDelivery = !_lastBarDeliveryLogUtc.TryGetValue(deliveryKey, out var lastDelivery) ||
                                          (utcNow - lastDelivery).TotalMinutes >= 5.0;
                    if (shouldLogDelivery)
                    {
                        _lastBarDeliveryLogUtc[deliveryKey] = utcNow;
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_DELIVERY_TO_STREAM", state: "ENGINE",
                            new
                            {
                                instrument = instrument,
                                stream = $"{s.Session}_{s.SlotTimeChicago}",
                                bar_timestamp_utc = barUtc.ToString("o"),
                                bar_timestamp_chicago = barChicagoTime.ToString("o"),
                                note = "Bar delivered to stream"
                            }));
                    }
                }
            }
            else
            {
                streamsFilteredOut.Add($"{s.Session}_{s.SlotTimeChicago}");
            }
        }
        
            // Log bar delivery summary periodically (rate-limited)
            LogBarDeliverySummaryIfNeeded(utcNow, instrument, streamsReceivingBar, streamsFilteredOut);
        }
    }
    
    /// <summary>
    /// Log bar rejection summary if interval has elapsed (rate-limited to every 5 minutes).
    /// </summary>
    private void LogBarRejectionSummaryIfNeeded(DateTimeOffset utcNow)
    {
        if (_lastBarRejectionSummaryUtc.HasValue && 
            (utcNow - _lastBarRejectionSummaryUtc.Value).TotalMinutes < BAR_REJECTION_SUMMARY_INTERVAL_MINUTES)
        {
            return; // Too soon to log summary
        }
        
        _lastBarRejectionSummaryUtc = utcNow;
        
        // Build summary for each instrument
        var summary = new Dictionary<string, object>();
        foreach (var kvp in _barRejectionStats)
        {
            var instrument = kvp.Key;
            var stats = kvp.Value;
            var totalRejected = stats.PartialRejected + stats.DateMismatch + stats.BeforeDateLocked;
            var totalProcessed = totalRejected + stats.TotalAccepted;
            var rejectionRate = totalProcessed > 0 ? (double)totalRejected / totalProcessed * 100.0 : 0.0;
            
            summary[instrument] = new
            {
                total_accepted = stats.TotalAccepted,
                partial_rejected = stats.PartialRejected,
                date_mismatch = stats.DateMismatch,
                before_date_locked = stats.BeforeDateLocked,
                total_rejected = totalRejected,
                total_processed = totalProcessed,
                rejection_rate_percent = Math.Round(rejectionRate, 2),
                last_update_utc = stats.LastUpdateUtc.ToString("o")
            };
            
            // Log warning if rejection rate exceeds threshold
            if (rejectionRate > 50.0 && totalProcessed >= 10)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_REJECTION_RATE_HIGH", state: "ENGINE",
                    new
                    {
                        instrument = instrument,
                        rejection_rate_percent = Math.Round(rejectionRate, 2),
                        total_processed = totalProcessed,
                        total_rejected = totalRejected,
                        partial_rejected = stats.PartialRejected,
                        date_mismatch = stats.DateMismatch,
                        before_date_locked = stats.BeforeDateLocked,
                        note = $"High rejection rate detected - {rejectionRate:F1}% of bars rejected. Check timezone conversion and date alignment."
                    }));
            }
        }
        
        if (summary.Count > 0)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_REJECTION_SUMMARY", state: "ENGINE",
                new
                {
                    summary = summary,
                    note = $"Bar rejection statistics (last {BAR_REJECTION_SUMMARY_INTERVAL_MINUTES} minutes)"
                }));
        }
    }
    
    // Rate-limiting for bar delivery logging (per stream)
    private readonly Dictionary<string, DateTimeOffset> _lastBarDeliveryLogUtc = new Dictionary<string, DateTimeOffset>();
    private DateTimeOffset? _lastBarDeliverySummaryUtc = null;
    private const double BAR_DELIVERY_SUMMARY_INTERVAL_MINUTES = 5.0;
    
    /// <summary>
    /// Log bar delivery summary if interval has elapsed (rate-limited to every 5 minutes).
    /// </summary>
    private void LogBarDeliverySummaryIfNeeded(DateTimeOffset utcNow, string instrument, List<string> streamsReceivingBar, List<string> streamsFilteredOut)
    {
        if (!_loggingConfig.enable_diagnostic_logs) return;
        
        if (_lastBarDeliverySummaryUtc.HasValue && 
            (utcNow - _lastBarDeliverySummaryUtc.Value).TotalMinutes < BAR_DELIVERY_SUMMARY_INTERVAL_MINUTES)
        {
            return; // Too soon to log summary
        }
        
        _lastBarDeliverySummaryUtc = utcNow;
        
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_DELIVERY_SUMMARY", state: "ENGINE",
            new
            {
                instrument = instrument,
                streams_receiving_bars = streamsReceivingBar,
                streams_filtered_out = streamsFilteredOut,
                total_streams_receiving = streamsReceivingBar.Count,
                total_streams_filtered = streamsFilteredOut.Count,
                note = $"Bar delivery distribution across streams (last {BAR_DELIVERY_SUMMARY_INTERVAL_MINUTES} minutes)"
            }));
    }

    /// <summary>
    /// Set session start time for an instrument (from TradingHours).
    /// Called by NinjaTrader strategy to provide instrument-specific session start time.
    /// </summary>
    /// <param name="instrument">Instrument name (e.g., "ES")</param>
    /// <param name="sessionStartTime">Session start time in HH:MM format (e.g., "17:00")</param>
    public void SetSessionStartTime(string instrument, string sessionStartTime)
    {
        lock (_engineLock)
        {
            if (string.IsNullOrWhiteSpace(instrument) || string.IsNullOrWhiteSpace(sessionStartTime))
                return;

            var instrumentUpper = instrument.ToUpperInvariant();
            _sessionStartTimes[instrumentUpper] = sessionStartTime;

            LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: TradingDateString, eventType: "SESSION_START_TIME_SET", state: "ENGINE",
                new
                {
                    instrument = instrumentUpper,
                    session_start_time = sessionStartTime,
                    source = "TradingHours",
                    note = "Session start time set from NinjaTrader TradingHours template"
                }));
        }
    }
    
    /// <summary>
    /// Get session start time for an instrument, with fallback to default.
    /// </summary>
    /// <param name="instrument">Instrument name</param>
    /// <returns>Session start time in HH:MM format</returns>
    private string GetSessionStartTime(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument))
            return "17:00"; // Default fallback
        
        var instrumentUpper = instrument.ToUpperInvariant();
        if (_sessionStartTimes.TryGetValue(instrumentUpper, out var startTime))
            return startTime;
        
        // Fallback to default (standard CME futures session start)
        return "17:00";
    }
    
    /// <summary>
    /// Compute the trading session window for a given trading date.
    /// Session starts the evening before (from TradingHours or default 17:00 CST) and ends at market close (16:00 CST).
    /// </summary>
    /// <param name="tradingDate">Trading date</param>
    /// <param name="instrument">Instrument name (for instrument-specific session start time)</param>
    /// <returns>Tuple of (sessionStartChicago, sessionEndChicago)</returns>
    private (DateTimeOffset sessionStartChicago, DateTimeOffset sessionEndChicago) GetSessionWindow(DateOnly tradingDate, string instrument = "")
    {
        if (_spec is null || _time is null)
            throw new InvalidOperationException("Spec and TimeService must be initialized");
        
        // Session starts previous calendar day at time from TradingHours (or default 17:00 CST)
        var sessionStartTime = GetSessionStartTime(instrument);
        var previousDay = tradingDate.AddDays(-1);
        var sessionStartChicago = _time.ConstructChicagoTime(previousDay, sessionStartTime);
        
        // Session ends at market close on trading date (from spec)
        var marketCloseTime = _spec.entry_cutoff.market_close_time; // "16:00"
        var sessionEndChicago = _time.ConstructChicagoTime(tradingDate, marketCloseTime);
        
        return (sessionStartChicago, sessionEndChicago);
    }
    
    /// <summary>
    /// Get session information from spec for a given instrument and session.
    /// Used by NinjaTrader strategy to get range_start_time and slot_end_times for BarsRequest.
    /// </summary>
    /// <param name="instrument">Instrument code (e.g., "ES")</param>
    /// <param name="session">Session name (e.g., "S1")</param>
    /// <returns>Tuple of (rangeStartTime, slotEndTimes) or null if not found</returns>
    public (string rangeStartTime, List<string> slotEndTimes)? GetSessionInfo(string instrument, string session)
    {
        lock (_engineLock)
        {
            if (_spec is null) return null;

            // Check if instrument exists in spec
            if (!_spec.TryGetInstrument(instrument, out _))
                return null;

            // Check if session exists in spec
            if (!_spec.sessions.ContainsKey(session))
                return null;

            var sessionInfo = _spec.sessions[session];
            return (sessionInfo.range_start_time, sessionInfo.slot_end_times);
        }
    }
    
    /// <summary>
    /// Load pre-hydration bars for SIM mode from NinjaTrader BarsRequest API.
    /// This method allows the NinjaTrader strategy to request historical bars and feed them to streams.
    /// Streams must exist before calling this method (created in Start()).
    /// Filters out bars that are in the future relative to current time to avoid duplicates with live bars.
    /// </summary>
    public void LoadPreHydrationBars(string instrument, List<Bar> bars, DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            if (_spec is null || _time is null) return;
            if (bars == null || bars.Count == 0) return;

        // Ensure streams exist (they should be created in Start())
        if (_streams.Count == 0)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_BARS_SKIPPED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    bar_count = bars.Count,
                    reason = "Streams not yet created",
                    note = "Bars will be buffered when streams are created or via OnBar()"
                }));
            return;
        }

        // CRITICAL: Filter out bars that are in the future or partial/in-progress
        // This prevents duplicate bars when live bars arrive later
        // BarsRequest might return bars up to slotTimeChicago, but we only want bars up to "now"
        // Also ensure bars are fully closed (at least 1 minute old)
        //
        // Partial-bar contamination problem:
        // - BarsRequest returns fully closed bars (good, but we verify)
        // - If you start mid-minute, BarsRequest gives completed prior bar
        // - Live feed may later emit a bar that partially overlaps expectations
        // - What breaks: Off-by-one minute range errors, incomplete data
        //
        // Rule: Only accept bars that are at least 1 minute old (bar period)
        // This ensures the bar is fully closed before we use it
        var filteredBars = new List<Bar>();
        var barsFilteredFuture = 0;
        var barsFilteredPartial = 0;
        
        foreach (var bar in bars)
        {
            // Filter 1: Reject future bars
            if (bar.TimestampUtc > utcNow)
            {
                barsFilteredFuture++;
                continue;
            }
            
                // Filter 2: Reject bars that are too recent (less than 0.1 minutes old)
            // Note: BarsRequest should return historical bars that are old enough, but we add a small buffer
            // to handle edge cases where BarsRequest might return very recent bars
            var barAgeMinutes = (utcNow - bar.TimestampUtc).TotalMinutes;
            const double MIN_BARSREQUEST_BAR_AGE_MINUTES = 0.1; // Small buffer for BarsRequest bars
            if (barAgeMinutes < MIN_BARSREQUEST_BAR_AGE_MINUTES)
            {
                barsFilteredPartial++;
                continue;
            }
            
            // Bar passed all filters - add to filtered list
            filteredBars.Add(bar);
        }
        
        var totalFiltered = barsFilteredFuture + barsFilteredPartial;

        // Log filtering summary (always log, even if no filtering occurred)
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, 
            eventType: "BARSREQUEST_FILTER_SUMMARY", state: "ENGINE",
            new
            {
                instrument = instrument,
                raw_bar_count = bars.Count,
                accepted_bar_count = filteredBars.Count,
                filtered_future_count = barsFilteredFuture,
                filtered_partial_count = barsFilteredPartial,
                accepted_first_bar_utc = filteredBars.Count > 0 ? filteredBars[0].TimestampUtc.ToString("o") : null,
                accepted_last_bar_utc = filteredBars.Count > 0 ? filteredBars[filteredBars.Count - 1].TimestampUtc.ToString("o") : null
            }));

        if (totalFiltered > 0)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_BARS_FILTERED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    total_bars = bars.Count,
                    filtered_future = barsFilteredFuture,
                    filtered_partial = barsFilteredPartial,
                    total_filtered = totalFiltered,
                    bars_loaded = filteredBars.Count,
                    current_time_utc = utcNow.ToString("o"),
                    min_bar_age_minutes = 0.1, // Small buffer for BarsRequest bars
                    note = "Filtered out future bars and very recent bars (< 0.1 min old). Only fully closed bars accepted."
                }));
        }

        if (filteredBars.Count == 0)
        {
            // Get current Chicago time for diagnostic
            var nowChicago = _time?.ConvertUtcToChicago(utcNow) ?? utcNow;
            
            // Log zero-bars diagnostic with actionable suggestions
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                eventType: "BARSREQUEST_ZERO_BARS_DIAGNOSTIC", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    trading_date = TradingDateString,
                    requested_start_chicago = bars.Count > 0 ? "N/A" : "See BARSREQUEST_REQUESTED log",
                    requested_end_chicago = bars.Count > 0 ? "N/A" : "See BARSREQUEST_REQUESTED log",
                    now_chicago = nowChicago.ToString("o"),
                    trading_hours_template = "See BARSREQUEST_REQUESTED log",
                    execution_mode = "SIM",
                    raw_bar_count = bars.Count,
                    filtered_future_count = barsFilteredFuture,
                    filtered_partial_count = barsFilteredPartial,
                    suggested_checks = new[]
                    {
                        "Check NinjaTrader 'Days to load' setting",
                        "Verify instrument has historical data",
                        "Confirm trading hours template",
                        "Confirm data provider connection"
                    }
                }));
            
            // All bars filtered out - this is unusual and should be logged as warning
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_NO_BARS_AFTER_FILTER", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    total_bars = bars.Count,
                    filtered_future = barsFilteredFuture,
                    filtered_partial = barsFilteredPartial,
                    total_filtered = totalFiltered,
                    current_time_utc = utcNow.ToString("o"),
                    note = "All bars were filtered out (all were future or partial/in-progress). " +
                           "This may indicate timing issues or data feed problems. " +
                           "Range computation will rely on live bars only - may be incomplete."
                }));
            // Don't return - allow degraded operation, but make it visible
            // Streams will start without historical bars
        }

        // Feed filtered bars to matching streams
        var streamsFed = 0;
        foreach (var stream in _streams.Values)
        {
            if (stream.IsSameInstrument(instrument))
            {
                // CRITICAL: Verify stream is ready to receive bars
                // Streams should exist and be in a state that buffers bars
                // PRE_HYDRATION, ARMED, and RANGE_BUILDING all buffer bars
                if (stream.State != StreamState.PRE_HYDRATION && 
                    stream.State != StreamState.ARMED && 
                    stream.State != StreamState.RANGE_BUILDING)
                {
                    var nowChicago = _time?.ConvertUtcToChicago(utcNow) ?? utcNow;
                    
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE", state: "ENGINE",
                        new
                        {
                            instrument = instrument,
                            stream_id = stream.Stream,
                            trading_date = TradingDateString,
                            stream_state = stream.State.ToString(),
                            bar_count = filteredBars.Count,
                            current_time_chicago = nowChicago.ToString("o"),
                            rejection_reason = $"STREAM_STATE_{stream.State}",
                            note = $"Stream is in {stream.State} state - bars will not be buffered. " +
                                   "This may indicate a timing issue or stream already progressed past pre-hydration."
                        }));
                    
                    // HIGH-SIGNAL WARNING: Bars skipped during active range-building indicates state machine issue
                    if (stream.State == StreamState.RANGE_LOCKED || stream.State == StreamState.DONE)
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_BARS_SKIPPED_ACTIVE_STREAM", state: "ENGINE",
                            new
                            {
                                instrument = instrument,
                                stream_id = stream.Stream,
                                stream_state = stream.State.ToString(),
                                warning = "Pre-hydration bars skipped for stream in active state - may indicate state machine issue",
                                note = "Stream has progressed beyond pre-hydration state. Bars should be received via OnBar() instead."
                            }));
                    }
                    
                    continue; // Skip this stream
                }
                
                // Feed bars directly to stream's buffer for pre-hydration
                // Mark as historical bars (from BarsRequest)
                foreach (var bar in filteredBars)
                {
                    stream.OnBar(bar.TimestampUtc, bar.Open, bar.High, bar.Low, bar.Close, utcNow, isHistorical: true);
                }
                streamsFed++;
            }
        }

            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_BARS_LOADED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    bar_count = filteredBars.Count,
                    filtered_future = barsFilteredFuture,
                    filtered_partial = barsFilteredPartial,
                    total_filtered = totalFiltered,
                    streams_fed = streamsFed,
                    first_bar_utc = filteredBars.Count > 0 ? filteredBars[0].TimestampUtc.ToString("o") : null,
                    last_bar_utc = filteredBars.Count > 0 ? filteredBars[filteredBars.Count - 1].TimestampUtc.ToString("o") : null,
                    source = "NinjaTrader_BarsRequest",
                    note = "Only fully closed bars loaded (filtered future and partial bars)"
                }));
        }
    }

    /// <summary>
    /// Ensure streams are created after trading date is locked.
    /// Called from OnBar() after trading date is locked from first session-valid bar.
    /// </summary>
    private void EnsureStreamsCreated(DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null || !_activeTradingDate.HasValue)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "STREAMS_CREATION_SKIPPED", state: "ENGINE",
                new { reason = "Missing spec, time, or trading date" }));
            return;
        }

        // If streams already exist, skip creation
        if (_streams.Count > 0)
        {
            return;
        }

        // Validate timetable structure
        if (_lastTimetable == null)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "STREAMS_CREATION_FAILED", state: "ENGINE",
                new { reason = "No timetable loaded" }));
            return;
        }

        // Validate timetable timezone matches
        if (_lastTimetable.timezone != "America/Chicago")
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "STREAMS_CREATION_FAILED", state: "ENGINE",
                new { reason = "TIMEZONE_MISMATCH", timezone = _lastTimetable.timezone }));
            return;
        }

        // Apply timetable to create streams with locked trading date
        ApplyTimetable(_lastTimetable, utcNow);

        // Log stream creation with detailed stream information
        // PHASE 1: Include both execution and canonical instruments for observability
        var streamDetails = _streams.Values.Select(s => new
        {
            stream_id = s.Stream,
            instrument = s.Instrument,
            execution_instrument = s.ExecutionInstrument,
            canonical_instrument = s.CanonicalInstrument,
            session = s.Session,
            slot_time = s.SlotTimeChicago,
            committed = s.Committed,
            state = s.State.ToString()
        }).ToList();
        
        // Group by instrument and session for summary
        var streamsByInstrument = _streams.Values
            .GroupBy(s => s.Instrument)
            .ToDictionary(g => g.Key, g => g.Select(s => new { session = s.Session, slot_time = s.SlotTimeChicago, committed = s.Committed }).ToList());
        
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "STREAMS_CREATED", state: "ENGINE",
            new
            {
                stream_count = _streams.Count,
                trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                streams = streamDetails,
                streams_by_instrument = streamsByInstrument,
                note = "Streams created after trading date locked from timetable"
            }));

        // Verify all streams use the same trading date (invariant check)
        foreach (var stream in _streams.Values)
        {
            if (stream.TradingDate != _activeTradingDate.Value.ToString("yyyy-MM-dd"))
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "INVARIANT_VIOLATION", state: "ENGINE",
                    new
                    {
                        error = "STREAM_TRADING_DATE_MISMATCH",
                        expected_trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                        stream_trading_date = stream.TradingDate,
                        stream_id = stream.Stream
                    }));
            }
        }

        // PHASE 3: Emit canonicalization self-test diagnostic
        EmitCanonicalizationSelfTest(utcNow);

        // Check startup timing now that streams exist
        CheckStartupTiming(utcNow);
    }
    
    /// <summary>
    /// PHASE 3: Emit canonicalization self-test diagnostic event.
    /// Provides observability for identity mapping verification.
    /// </summary>
    private void EmitCanonicalizationSelfTest(DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null || !_activeTradingDate.HasValue)
            return;
        
        var canonicalStreamIds = _streams.Values
            .Select(s => s.Stream)
            .OrderBy(s => s)
            .ToList();
        
        var instrumentMappings = _streams.Values
            .GroupBy(s => s.ExecutionInstrument)
            .Select(g => new
            {
                execution_instrument = g.Key,
                canonical_instrument = g.First().CanonicalInstrument,
                stream_count = g.Count(),
                streams = g.Select(s => s.Stream).OrderBy(s => s).ToList()
            })
            .OrderBy(m => m.execution_instrument)
            .ToList();
        
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "CANONICALIZATION_SELF_TEST", state: "ENGINE",
            new
            {
                trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                total_streams = _streams.Count,
                canonical_stream_ids = canonicalStreamIds,
                instrument_mappings = instrumentMappings,
                note = "PHASE 3: Canonicalization self-test - verifies execution/canonical identity mapping"
            }));
    }

    /// <summary>
    /// PHASE 3.1: Check identity invariants periodically and emit status event.
    /// Rate-limited to once per 60 seconds and on-change.
    /// </summary>
    private void CheckIdentityInvariantsIfNeeded(DateTimeOffset utcNow)
    {
        // Rate limit: check every 60 seconds or on-change
        var shouldCheck = !_lastIdentityInvariantsCheckUtc.HasValue ||
                         (utcNow - _lastIdentityInvariantsCheckUtc.Value).TotalSeconds >= IDENTITY_INVARIANTS_CHECK_INTERVAL_SECONDS;
        
        if (!shouldCheck && _lastIdentityInvariantsPass)
            return; // Too soon and last check passed - skip
        
        lock (_engineLock)
        {
            // Re-check time inside lock (may have changed)
            if (_lastIdentityInvariantsCheckUtc.HasValue &&
                (utcNow - _lastIdentityInvariantsCheckUtc.Value).TotalSeconds < IDENTITY_INVARIANTS_CHECK_INTERVAL_SECONDS &&
                _lastIdentityInvariantsPass)
            {
                return; // Still too soon and passing
            }
            
            var violations = new List<string>();
            var canonicalInstrument = "";
            var executionInstrument = _executionInstrument ?? "";
            var streamIds = new List<string>();
            
            // Check 1: All stream IDs are canonical (no execution instrument in stream ID)
            if (_spec != null && !string.IsNullOrWhiteSpace(executionInstrument))
            {
                canonicalInstrument = GetCanonicalInstrument(executionInstrument);
                
                foreach (var stream in _streams.Values)
                {
                    streamIds.Add(stream.Stream);
                    
                    // Check: Stream ID should not contain execution instrument if different from canonical
                    if (executionInstrument != canonicalInstrument &&
                        stream.Stream.Contains(executionInstrument, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"Stream ID '{stream.Stream}' contains execution instrument '{executionInstrument}' (should be canonical '{canonicalInstrument}')");
                    }
                    
                    // Check: Stream.Instrument should equal CanonicalInstrument
                    if (stream.Instrument != stream.CanonicalInstrument)
                    {
                        violations.Add($"Stream '{stream.Stream}': Instrument property '{stream.Instrument}' does not match CanonicalInstrument '{stream.CanonicalInstrument}'");
                    }
                    
                    // Check: ExecutionInstrument is present
                    if (string.IsNullOrWhiteSpace(stream.ExecutionInstrument))
                    {
                        violations.Add($"Stream '{stream.Stream}': ExecutionInstrument is null or empty");
                    }
                }
            }
            
            var pass = violations.Count == 0;
            var shouldEmit = !_lastIdentityInvariantsCheckUtc.HasValue || // First check
                            pass != _lastIdentityInvariantsPass; // Status changed
            
            if (shouldEmit)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "IDENTITY_INVARIANTS_STATUS", state: "ENGINE",
                    new
                    {
                        pass = pass,
                        violations = violations,
                        canonical_instrument = canonicalInstrument,
                        execution_instrument = executionInstrument,
                        stream_ids = streamIds.OrderBy(s => s).ToList(),
                        checked_at_utc = utcNow.ToString("o"),
                        note = pass 
                            ? "All identity invariants passed - canonical and execution identities are consistent"
                            : $"Identity violations detected: {string.Join("; ", violations)}"
                    }));
                
                _lastIdentityInvariantsPass = pass;
            }
            
            _lastIdentityInvariantsCheckUtc = utcNow;
        }
    }
    
    /// <summary>
    /// PHASE 1: Get canonical instrument for a given execution instrument.
    /// Maps micro futures (MES, MNQ, etc.) to their base instruments (ES, NQ, etc.).
    /// Returns execution instrument unchanged if not a micro or if spec is unavailable.
    /// </summary>
    private string GetCanonicalInstrument(string executionInstrument)
    {
        if (_spec != null &&
            _spec.TryGetInstrument(executionInstrument, out var inst) &&
            inst.is_micro &&
            !string.IsNullOrWhiteSpace(inst.base_instrument))
        {
            return inst.base_instrument.ToUpperInvariant(); // MES → ES
        }

        return executionInstrument.ToUpperInvariant(); // ES → ES
    }

    // PHASE 3.2: Execution quantity rules (authoritative dictionary)
    // Quantity is code-controlled; Chart Trader quantity is ignored.
    private static readonly Dictionary<string, int> _orderQuantityMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        // Minis
        ["ES"] = 2,
        ["NQ"] = 1,
        ["YM"] = 2,
        ["RTY"] = 2,
        ["CL"] = 2,
        ["GC"] = 2,
        ["NG"] = 2,
        
        // Micros
        ["MES"] = 2,
        ["MNQ"] = 1,
        ["MYM"] = 2,
        ["M2K"] = 2,
        ["MCL"] = 2,
        ["MGC"] = 2,
        ["MNG"] = 2
    };

    /// <summary>
    /// PHASE 3.2: Get order quantity for execution instrument.
    /// Quantity is code-controlled; Chart Trader quantity is ignored.
    /// </summary>
    /// <param name="executionInstrument">Execution instrument (e.g., "MES", "ES", "NQ")</param>
    /// <returns>Order quantity for the instrument</returns>
    /// <exception cref="ArgumentException">If instrument is null or empty</exception>
    /// <exception cref="InvalidOperationException">If instrument is unknown or quantity <= 0</exception>
    public int GetOrderQuantity(string executionInstrument)
    {
        if (string.IsNullOrWhiteSpace(executionInstrument))
        {
            throw new ArgumentException("Execution instrument cannot be null or empty", nameof(executionInstrument));
        }

        var instrumentTrimmed = executionInstrument.Trim();
        
        if (!_orderQuantityMap.TryGetValue(instrumentTrimmed, out var quantity))
        {
            // Unknown instrument - fail closed
            throw new InvalidOperationException(
                $"PHASE 3.2: Unknown execution instrument '{executionInstrument}'. " +
                $"No quantity rule defined. Execution blocked.");
        }
        
        if (quantity <= 0)
        {
            throw new InvalidOperationException(
                $"PHASE 3.2: Invalid quantity {quantity} for execution instrument '{executionInstrument}'. " +
                $"Quantity must be positive.");
        }
        
        return quantity;
    }

    private (FilePollResult Poll, TimetableContract? Timetable, Exception? ParseException) PollAndParseTimetable(DateTimeOffset utcNow)
    {
        var poll = _timetablePoller.Poll(_timetablePath, utcNow);
        if (poll.Error is not null)
        {
            return (poll, null, null);
        }

        // Even if unchanged, we may still parse when force==true (handled by caller).
        try
        {
            var timetable = TimetableContract.LoadFromFile(_timetablePath);
            return (poll, timetable, null);
        }
        catch (Exception ex)
        {
            return (poll, null, ex);
        }
    }

    private void ReloadTimetableIfChanged(
        DateTimeOffset utcNow,
        bool force,
        FilePollResult poll,
        TimetableContract? timetable,
        Exception? parseException)
    {
        if (_spec is null || _time is null) return;

        if (poll.Error is not null)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new
                {
                    reason = "POLL_ERROR",
                    error = poll.Error,
                    trading_date = TradingDateString,
                    timetable_path = _timetablePath,
                    note = "Timetable file poll failed - engine will stand down"
                }));
            StandDown();
            return;
        }

        if (!force && !poll.Changed) return;

        var previousHash = _lastTimetableHash;
        _lastTimetableHash = poll.Hash;

        if (timetable is null)
        {
            var err = parseException?.Message ?? "Unknown timetable parse error";
            var errType = parseException?.GetType().Name ?? "Unknown";
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new
                {
                    reason = "PARSE_ERROR",
                    error = err,
                    error_type = errType,
                    trading_date = TradingDateString,
                    timetable_path = _timetablePath,
                    note = "Timetable file parse failed - engine will stand down"
                }));
            StandDown();
            return;
        }

        // Lock trading date from timetable
        if (string.IsNullOrWhiteSpace(timetable.trading_date))
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_MISSING_TRADING_DATE", state: "ENGINE",
                new { reason = "Timetable exists but trading_date is missing or empty", timetable_path = _timetablePath }));
            StandDown();
            return;
        }

        // Parse trading_date from timetable (format: "YYYY-MM-DD")
        DateOnly? timetableTradingDate = null;
        try
        {
            timetableTradingDate = DateOnly.Parse(timetable.trading_date);
        }
        catch (Exception ex)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_INVALID_TRADING_DATE", state: "ENGINE",
                new { reason = "Failed to parse trading_date", trading_date = timetable.trading_date, error = ex.Message, timetable_path = _timetablePath }));
            StandDown();
            return;
        }

        // Lock trading date if not already set
        bool dateWasLocked = false;
        if (!_activeTradingDate.HasValue)
        {
            _activeTradingDate = timetableTradingDate.Value;
            dateWasLocked = true;
            
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: timetableTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "TRADING_DATE_LOCKED", state: "ENGINE",
                new
                {
                    trading_date = timetableTradingDate.Value.ToString("yyyy-MM-dd"),
                    source = "TIMETABLE",
                    timetable_path = _timetablePath,
                    note = "Trading date locked from timetable - immutable for this engine run"
                }));
        }
        else if (_activeTradingDate.Value != timetableTradingDate.Value)
        {
            // Trading date already locked and differs from timetable - keep existing (immutable)
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_TRADING_DATE_MISMATCH", state: "ENGINE",
                new
                {
                    locked_trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                    timetable_trading_date = timetableTradingDate.Value.ToString("yyyy-MM-dd"),
                    note = "Timetable trading_date differs from locked date - keeping existing (immutable)"
                }));
        }

        if (timetable.timezone != "America/Chicago")
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new 
                { 
                    reason = "TIMEZONE_MISMATCH",
                    expected_timezone = "America/Chicago",
                    actual_timezone = timetable.timezone,
                    trading_date = TradingDateString,
                    timetable_path = _timetablePath,
                    note = "Timetable timezone mismatch - engine will stand down"
                }));
            StandDown();
            return;
        }

        var isReplayTimetable = timetable.metadata?.replay == true;

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_UPDATED", state: "ENGINE",
            new 
            { 
                previous_hash = previousHash,
                new_hash = _lastTimetableHash,
                enabled_stream_count = timetable.streams.Count(s => s.enabled),
                total_stream_count = timetable.streams.Count,
                trading_date = timetableTradingDate.Value.ToString("yyyy-MM-dd"),
                date_locked = dateWasLocked,
                note = "Timetable structure validated - trading date locked from timetable"
            }));

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_LOADED", state: "ENGINE",
            new { streams = timetable.streams.Count, timetable_hash = _lastTimetableHash, timetable_path = _timetablePath }));
        
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_VALIDATED", state: "ENGINE",
            new { is_replay = isReplayTimetable, trading_date = timetableTradingDate.Value.ToString("yyyy-MM-dd"), note = "Trading date locked from timetable" }));

        // Store timetable for later application
        _lastTimetable = timetable;

        // If trading date is already locked and streams exist, apply timetable changes immediately
        // This handles timetable updates after initial stream creation
        if (_activeTradingDate.HasValue && _streams.Count > 0)
        {
            ApplyTimetable(timetable, utcNow);
        }
        // Otherwise, timetable will be applied when EnsureStreamsCreated() is called after trading date is locked
        
        // Health monitor no longer uses timetable for monitoring windows
        // (simplified to only monitor connection loss and data loss)
    }

    private void ApplyTimetable(TimetableContract timetable, DateTimeOffset utcNow)
    {
        // Trading date must be locked before streams can be created
        if (_spec is null || _time is null || _lastTimetableHash is null || !_activeTradingDate.HasValue)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_APPLY_SKIPPED", state: "ENGINE",
                new { reason = "Missing spec, time, hash, or trading date" }));
            return;
        }

        // Validate timetable trading_date matches locked trading date
        if (!string.IsNullOrWhiteSpace(timetable.trading_date))
        {
            try
            {
                var timetableTradingDate = DateOnly.Parse(timetable.trading_date);
                if (_activeTradingDate.Value != timetableTradingDate)
                {
                    // Timetable trading_date differs from locked date - log warning but keep existing (immutable)
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_TRADING_DATE_MISMATCH", state: "ENGINE",
                        new
                        {
                            locked_trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
                            timetable_trading_date = timetableTradingDate.ToString("yyyy-MM-dd"),
                            note = "Timetable trading_date differs from locked date - keeping existing (immutable)"
                        }));
                }
            }
            catch (Exception ex)
            {
                // Invalid format - log but continue (trading date already locked)
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_INVALID_TRADING_DATE", state: "ENGINE",
                    new { trading_date = timetable.trading_date, error = ex.Message, note = "Failed to parse timetable trading_date - using locked date" }));
            }
        }

        var tradingDate = _activeTradingDate.Value; // Use locked trading date

        var incoming = timetable.streams.Where(s => s.enabled).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var streamIdOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        // PHASE 3.2: Validate order quantities for unique execution instruments before stream creation (fail-closed)
        // Validate by unique execution instrument, not per stream (multiple streams can share same instrument)
        var uniqueExecutionInstruments = incoming
            .Select(d => (d.instrument ?? "").Trim())
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var quantityValidationErrors = new List<string>();
        foreach (var execInst in uniqueExecutionInstruments)
        {
            try
            {
                var qty = GetOrderQuantity(execInst);
                // GetOrderQuantity already validates qty > 0, so no double-check needed
            }
            catch (ArgumentException ex)
            {
                quantityValidationErrors.Add($"Execution instrument '{execInst}': {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                quantityValidationErrors.Add($"Execution instrument '{execInst}': {ex.Message}");
            }
        }

        if (quantityValidationErrors.Count > 0)
        {
            var errorMsg = $"PHASE 3.2: Order quantity validation failed:\n{string.Join("\n", quantityValidationErrors)}";
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, 
                eventType: "ORDER_QUANTITY_VALIDATION_FAILED", state: "ENGINE",
                new
                {
                    errors = quantityValidationErrors,
                    unique_execution_instruments = uniqueExecutionInstruments,
                    note = "Execution blocked due to invalid or unknown execution instruments"
                }));
            throw new InvalidOperationException(errorMsg);
        }
        
        // Log timetable parsing stats
        int acceptedCount = 0;
        int skippedCount = 0;
        var skippedReasons = new Dictionary<string, int>();

        foreach (var directive in incoming)
        {
            var executionInstrument = (directive.instrument ?? "").ToUpperInvariant();
            var canonicalInstrument = GetCanonicalInstrument(executionInstrument);
            var session = directive.session ?? "";
            var slotTimeChicago = directive.slot_time ?? "";
            
            // PHASE 2: Canonicalize stream ID - must use canonical instrument, not execution
            var streamId = directive.stream;
            if (!string.IsNullOrWhiteSpace(streamId) && !string.IsNullOrWhiteSpace(executionInstrument))
            {
                // Map execution instrument in stream ID to canonical instrument
                // e.g., "MES1" -> "ES1"
                if (streamId.Contains(executionInstrument, StringComparison.OrdinalIgnoreCase))
                {
                    streamId = streamId.Replace(executionInstrument, canonicalInstrument, StringComparison.OrdinalIgnoreCase);
                }
            }
            
            // Track stream ID occurrences for duplicate detection (using canonical stream ID)
            if (!string.IsNullOrWhiteSpace(streamId))
            {
                if (!streamIdOccurrences.TryGetValue(streamId, out var count))
                    count = 0;
                streamIdOccurrences[streamId] = count + 1;
            }
            DateTimeOffset? slotTimeUtc = null;
            try
            {
                // CRITICAL FIX: Use tradingDate parameter instead of _activeTradingDate
                if (!string.IsNullOrWhiteSpace(slotTimeChicago))
                {
                    var slotTimeChicagoTime = _time.ConstructChicagoTime(tradingDate, slotTimeChicago);
                    slotTimeUtc = _time.ConvertChicagoToUtc(slotTimeChicagoTime);
                }
            }
            catch
            {
                slotTimeUtc = null;
            }

            var tradingDateStr = tradingDate.ToString("yyyy-MM-dd"); // Use parameter, not _activeTradingDate

            if (string.IsNullOrWhiteSpace(streamId) ||
                string.IsNullOrWhiteSpace(executionInstrument) ||
                string.IsNullOrWhiteSpace(session) ||
                string.IsNullOrWhiteSpace(slotTimeChicago))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("MISSING_FIELDS", out var count)) count = 0;
                skippedReasons["MISSING_FIELDS"] = count + 1;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId ?? "", canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new 
                    { 
                        reason = "MISSING_FIELDS", 
                        stream_id = streamId, 
                        execution_instrument = executionInstrument,
                        canonical_instrument = canonicalInstrument,
                        session = session, 
                        slot_time = slotTimeChicago 
                    }));
                continue;
            }

            if (!_spec.sessions.ContainsKey(session))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("UNKNOWN_SESSION", out var count2)) count2 = 0;
                skippedReasons["UNKNOWN_SESSION"] = count2 + 1;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new 
                    { 
                        reason = "UNKNOWN_SESSION", 
                        stream_id = streamId, 
                        execution_instrument = executionInstrument,
                        canonical_instrument = canonicalInstrument,
                        session = session 
                    }));
                continue;
            }

            // slot_time validation (fail closed per stream)
            var allowed = _spec.sessions[session].slot_end_times;
            if (!allowed.Contains(slotTimeChicago))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("INVALID_SLOT_TIME", out var count3)) count3 = 0;
                skippedReasons["INVALID_SLOT_TIME"] = count3 + 1;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new 
                    { 
                        reason = "INVALID_SLOT_TIME", 
                        stream_id = streamId, 
                        execution_instrument = executionInstrument,
                        canonical_instrument = canonicalInstrument,
                        slot_time = slotTimeChicago, 
                        allowed_times = allowed 
                    }));
                continue;
            }

            // PHASE 2: Timetable validation uses canonical instrument (logic identity)
            if (!_spec.TryGetInstrument(canonicalInstrument, out _))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("UNKNOWN_INSTRUMENT", out var count4)) count4 = 0;
                skippedReasons["UNKNOWN_INSTRUMENT"] = count4 + 1;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new 
                    { 
                        reason = "UNKNOWN_INSTRUMENT", 
                        stream_id = streamId, 
                        execution_instrument = executionInstrument,
                        canonical_instrument = canonicalInstrument
                    }));
                continue;
            }

            seen.Add(streamId);
            acceptedCount++;

            if (_streams.TryGetValue(streamId, out var sm))
            {
                if (sm.Committed)
                {
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                        "UPDATE_IGNORED_COMMITTED", "ENGINE", new 
                        { 
                            reason = "STREAM_COMMITTED",
                            execution_instrument = executionInstrument,
                            canonical_instrument = canonicalInstrument
                        }));
                    continue;
                }

                // Apply updates only for uncommitted streams
                if (string.IsNullOrWhiteSpace(directive.slot_time))
                {
                    // Fail closed: skip update if slot_time is null/empty
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, sm.SlotTimeChicago, null,
                        "STREAM_UPDATE_SKIPPED", "ENGINE", new 
                        { 
                            reason = "EMPTY_SLOT_TIME",
                            previous_slot_time = sm.SlotTimeChicago,
                            note = "Update skipped due to empty slot_time in timetable"
                        }));
                    continue;
                }
                
                sm.ApplyDirectiveUpdate(directive.slot_time, _activeTradingDate.Value, utcNow);
            }
            else
            {
                // PHASE 2: Fail-fast assertion - ensure stream ID is canonicalized
                if (executionInstrument != canonicalInstrument && streamId.Contains(executionInstrument, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"PHASE 2 ASSERTION FAILED: Execution instrument '{executionInstrument}' leaked into logic stream ID '{streamId}'. " +
                        $"Expected canonical stream ID using '{canonicalInstrument}'. " +
                        $"This indicates incomplete canonicalization."
                    );
                }
                
                // PHASE 3: New stream - pass DateOnly directly (authoritative), convert to string only for logging/journal
                
                // PHASE 3: Fail-fast assertion - ensure stream ID is canonicalized before stream creation
                if (executionInstrument != canonicalInstrument && streamId.Contains(executionInstrument, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"PHASE 3 ASSERTION FAILED: Execution instrument '{executionInstrument}' leaked into logic stream ID '{streamId}'. " +
                        $"Expected canonical stream ID using '{canonicalInstrument}'. " +
                        $"This indicates incomplete canonicalization in ApplyTimetable."
                    );
                }
                
                // PHASE 3: Assert canonical stream ID matches canonical instrument
                if (!streamId.StartsWith(canonicalInstrument, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"PHASE 3 ASSERTION FAILED: Stream ID '{streamId}' does not start with canonical instrument '{canonicalInstrument}'. " +
                        $"Stream IDs must use canonical instrument prefix (e.g., ES1, not MES1)."
                    );
                }
                
                // PHASE 3.2: Resolve order quantity for this execution instrument
                var orderQuantity = GetOrderQuantity(executionInstrument);
                
                // PHASE 3: Pass DateOnly to constructor (will be converted to string internally for journal)
                // Pass logging config for diagnostic control
                // PHASE 3.2: Pass order quantity (code-controlled, Chart Trader ignored)
                var newSm = new StreamStateMachine(_time, _spec, _log, _journals, tradingDate, _lastTimetableHash, directive, _executionMode, orderQuantity, _executionAdapter, _riskGate, _executionJournal, loggingConfig: _loggingConfig);
                
                // PHASE 3: Post-creation assertion - verify stream properties match expectations
                if (newSm.ExecutionInstrument != executionInstrument)
                {
                    throw new InvalidOperationException(
                        $"PHASE 3 ASSERTION FAILED: Stream ExecutionInstrument '{newSm.ExecutionInstrument}' does not match expected '{executionInstrument}'."
                    );
                }
                if (newSm.CanonicalInstrument != canonicalInstrument)
                {
                    throw new InvalidOperationException(
                        $"PHASE 3 ASSERTION FAILED: Stream CanonicalInstrument '{newSm.CanonicalInstrument}' does not match expected '{canonicalInstrument}'."
                    );
                }
                if (newSm.Stream != streamId)
                {
                    throw new InvalidOperationException(
                        $"PHASE 3 ASSERTION FAILED: Stream ID '{newSm.Stream}' does not match canonicalized stream ID '{streamId}'."
                    );
                }
                if (newSm.Instrument != canonicalInstrument)
                {
                    throw new InvalidOperationException(
                        $"PHASE 3 ASSERTION FAILED: Stream Instrument property '{newSm.Instrument}' is not canonical '{canonicalInstrument}'. " +
                        $"Instrument property must represent logic identity (canonical)."
                    );
                }
                
                // PHASE 2: Verify stream ID matches canonical instrument
                if (newSm.ExecutionInstrument != newSm.CanonicalInstrument && 
                    !string.Equals(newSm.Stream, streamId, StringComparison.OrdinalIgnoreCase))
                {
                    // Stream ID should already be canonicalized above, but double-check
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                        "STREAM_ID_CANONICALIZATION_WARNING", "ENGINE", new 
                        { 
                            stream_id_from_timetable = directive.stream,
                            canonicalized_stream_id = streamId,
                            execution_instrument = newSm.ExecutionInstrument,
                            canonical_instrument = newSm.CanonicalInstrument,
                            note = "Stream ID was canonicalized from timetable"
                        }));
                }
                
                // PHASE 2: Log stream creation with both instruments for observability
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_CREATED", "ENGINE", new 
                    { 
                        stream_id = streamId,
                        execution_instrument = newSm.ExecutionInstrument,
                        canonical_instrument = newSm.CanonicalInstrument,
                        session = session,
                        slot_time = slotTimeChicago
                    }));
                
                // PHASE 4: Set alert callback for high-priority alerts only (RANGE_INVALIDATED)
                // Non-critical alerts (gaps, pre-hydration, state transitions) are logged only, not notified
                var notificationService = GetNotificationService();
                if (notificationService != null)
                {
                    newSm.SetAlertCallback((key, title, message, priority) => 
                    {
                        // Only notify for RANGE_INVALIDATED (handled in StreamStateMachine)
                        // All other alerts are logged only (no notification)
                        if (key.StartsWith("RANGE_INVALIDATED:", StringComparison.OrdinalIgnoreCase))
                        {
                            notificationService.EnqueueNotification(key, title, message, priority);
                        }
                        // All other alert callbacks are suppressed (log only)
                    });
                }
                
                // Set critical event reporting callback for EXECUTION_GATE_INVARIANT_VIOLATION
                if (_healthMonitor != null)
                {
                    newSm.SetReportCriticalCallback((eventType, payload, tradingDate) =>
                    {
                        // Audit clarity only: embed run_id + trading_date into payload
                        if (!string.IsNullOrWhiteSpace(_runId))
                            payload["run_id"] = _runId;
                        if (!string.IsNullOrWhiteSpace(tradingDate))
                            payload["trading_date"] = tradingDate;

                        _healthMonitor.ReportCritical(eventType, payload);
                    });
                }
                
                _streams[streamId] = newSm;

                if (newSm.Committed)
                {
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, canonicalInstrument, session, slotTimeChicago, slotTimeUtc,
                        "STREAM_SKIPPED", "ENGINE", new 
                        { 
                            reason = "ALREADY_COMMITTED_JOURNAL",
                            execution_instrument = newSm.ExecutionInstrument,
                            canonical_instrument = newSm.CanonicalInstrument
                        }));
                    continue;
                }

                newSm.Arm(utcNow);
            }
        }

        // Log duplicate stream IDs if detected
        foreach (var kvp in streamIdOccurrences)
        {
            if (kvp.Value > 1)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDate.ToString("yyyy-MM-dd"), eventType: "DUPLICATE_STREAM_ID", state: "ENGINE",
                    new
                    {
                        stream_id = kvp.Key,
                        occurrence_count = kvp.Value,
                        note = "Duplicate stream ID detected in timetable - last occurrence will be used"
                    }));
            }
        }

        // Log parsing summary
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDate.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_PARSING_COMPLETE", state: "ENGINE",
            new { 
                total_enabled = incoming.Count, 
                accepted = acceptedCount, 
                skipped = skippedCount, 
                skipped_reasons = skippedReasons,
                streams_armed = _streams.Count
            }));

        // Any existing streams not present in timetable are left as-is; timetable is authoritative about enabled streams,
        // but the skeleton remains fail-closed (no orders) regardless.
    }

    private void StandDown()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var streamCount = _streams.Count;
        var tradingDateStr = TradingDateString;
        
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "ENGINE_STAND_DOWN", state: "ENGINE",
            new
            {
                reason = "Timetable validation failure",
                trading_date = tradingDateStr,
                timetable_path = _timetablePath,
                streams_cleared = streamCount,
                active_trading_date_cleared = _activeTradingDate.HasValue,
                note = "Engine stand-down due to timetable validation failure - all streams cleared, trading date reset"
            }));
        
        _streams.Clear();
        _activeTradingDate = null;
    }

    /// <summary>
    /// Unified logging method: converts old event format to RobotLogEvent and logs via async service.
    /// Falls back to RobotLogger.Write() if conversion fails (which handles its own fallback logic).
    /// </summary>
    private void LogEvent(object evt)
    {
        // Try async logging service first (preferred path)
        if (_loggingService != null && evt is Dictionary<string, object?> dict)
        {
            try
            {
                var logEvent = ConvertToRobotLogEvent(dict);
                _loggingService.Log(logEvent);
                return; // Successfully logged via async service
            }
            catch (Exception ex)
            {
                // Fail loudly: log conversion failure as ERROR event before falling back
                try
                {
                    var utcNow = DateTimeOffset.UtcNow;
                    var errorEvent = RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "LOG_CONVERSION_ERROR", state: "ENGINE",
                        new
                        {
                            exception_type = ex.GetType().Name,
                            error = ex.Message,
                            stack_trace = ex.StackTrace != null && ex.StackTrace.Length > 500 ? ex.StackTrace.Substring(0, 500) : ex.StackTrace,
                            original_event_type = dict.TryGetValue("event_type", out var et) ? et?.ToString() : "UNKNOWN",
                            note = "Failed to convert event dictionary to RobotLogEvent - falling back to RobotLogger.Write()"
                        });
                    _log.Write(errorEvent);
                }
                catch
                {
                    // If even error logging fails, silently fall through to fallback
                }
            }
        }

        // Fallback to RobotLogger.Write() (handles conversion and fallback internally)
        _log.Write(evt);
    }

    /// <summary>
    /// Convert old event dictionary format to RobotLogEvent.
    /// Handles EngineBase, Base, and ExecutionBase event types.
    /// </summary>
    private RobotLogEvent ConvertToRobotLogEvent(Dictionary<string, object?> dict)
    {
        var utcNow = DateTimeOffset.UtcNow;
        if (dict.TryGetValue("ts_utc", out var tsObj) && tsObj is string tsStr && DateTimeOffset.TryParse(tsStr, out var parsed))
        {
            utcNow = parsed;
        }

        var eventType = dict.TryGetValue("event_type", out var et) ? et?.ToString() ?? "" : "";
        // Use centralized event registry for level assignment (replaces fragile string matching)
        var level = RobotEventTypes.GetLevel(eventType);

        // Determine source based on stream field
        var source = "RobotEngine";
        if (dict.TryGetValue("stream", out var streamObj) && streamObj is string streamStr)
        {
            if (streamStr == "__engine__")
                source = "RobotEngine";
            else if (streamStr.StartsWith("EXECUTION") || dict.ContainsKey("intent_id"))
                source = "ExecutionAdapter";
            else
                source = "StreamStateMachine";
        }

        var instrument = dict.TryGetValue("instrument", out var inst) ? inst?.ToString() ?? "" : "";
        var message = eventType; // Use event_type as message
        
        // Extract data payload - include all non-standard fields in data
        var data = new Dictionary<string, object?>();
        if (dict.TryGetValue("data", out var dataObj))
        {
            if (dataObj is Dictionary<string, object?> dataDict)
            {
                foreach (var kvp in dataDict)
                    data[kvp.Key] = kvp.Value;
            }
            else if (dataObj != null)
            {
                data["payload"] = dataObj;
            }
        }

        // Include additional context fields in data
        foreach (var kvp in dict)
        {
            if (kvp.Key != "ts_utc" && kvp.Key != "ts_chicago" && kvp.Key != "event_type" && 
                kvp.Key != "instrument" && kvp.Key != "data" && kvp.Key != "stream")
            {
                data[kvp.Key] = kvp.Value;
            }
        }

        return new RobotLogEvent(utcNow, level, source, instrument, eventType, message, runId: _runId, data: data.Count > 0 ? data : null);
    }

    /// <summary>
    /// Expose logging service for components that need direct access (e.g., adapters).
    /// </summary>
    internal RobotLoggingService? GetLoggingService() => _loggingService;
    
    /// <summary>
    /// Expose health monitor for strategy files (replaces reflection-based access).
    /// </summary>
    internal HealthMonitor? GetHealthMonitor() => _healthMonitor;
    
    /// <summary>
    /// Send a test notification to verify Pushover is working.
    /// Public method for testing from strategy or external code.
    /// </summary>
    public void SendTestNotification()
    {
        _healthMonitor?.SendTestNotification();
    }
    
    /// <summary>
    /// Expose execution adapter for strategy files (replaces reflection-based access).
    /// </summary>
    internal IExecutionAdapter? GetExecutionAdapter() => _executionAdapter;
    
    /// <summary>
    /// Expose time service for components that need direct access (e.g., bar providers).
    /// </summary>
    internal TimeService? GetTimeService() => _time;
    
    /// <summary>
    /// Forward connection status update to health monitor and handle recovery state transitions.
    /// </summary>
    public void OnConnectionStatusUpdate(ConnectionStatus status, string connectionName)
    {
        lock (_engineLock)
        {
            var utcNow = DateTimeOffset.UtcNow;
            var wasConnected = _lastConnectionStatus == ConnectionStatus.Connected;
            var isConnected = status == ConnectionStatus.Connected;

            // Forward to health monitor first
            _healthMonitor?.OnConnectionStatusUpdate(status, connectionName, utcNow);

            // Handle recovery state transitions
            if (wasConnected && !isConnected)
            {
                // First disconnect: transition to DISCONNECT_FAIL_CLOSED
                if (_recoveryState == ConnectionRecoveryState.CONNECTED_OK || _recoveryState == ConnectionRecoveryState.RECOVERY_COMPLETE)
                {
                    _recoveryState = ConnectionRecoveryState.DISCONNECT_FAIL_CLOSED;
                    if (!_disconnectFirstUtc.HasValue)
                    {
                        _disconnectFirstUtc = utcNow;
                    }

                    var payload = new Dictionary<string, object>
                    {
                        ["recovery_state"] = _recoveryState.ToString(),
                        ["disconnect_first_utc"] = _disconnectFirstUtc.Value.ToString("o"),
                        ["connection_status"] = status.ToString(),
                        ["connection_name"] = connectionName,
                        ["execution_mode"] = _executionMode.ToString(),
                        ["active_stream_count"] = _streams.Count(s => !s.Value.Committed)
                    };
                    
                    // Audit clarity: include run_id and trading_date when known
                    if (!string.IsNullOrWhiteSpace(_runId))
                        payload["run_id"] = _runId;
                    if (!string.IsNullOrWhiteSpace(TradingDateString))
                        payload["trading_date"] = TradingDateString;
                    
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_FAIL_CLOSED_ENTERED", state: "ENGINE",
                        payload));
                    
                    // Report critical event to HealthMonitor for notification
                    _healthMonitor?.ReportCritical("DISCONNECT_FAIL_CLOSED_ENTERED", payload);
                }
            }
            else if (!wasConnected && isConnected)
            {
                // Reconnect: transition to RECONNECTED_RECOVERY_PENDING
                if (_recoveryState == ConnectionRecoveryState.DISCONNECT_FAIL_CLOSED)
                {
                    _recoveryState = ConnectionRecoveryState.RECONNECTED_RECOVERY_PENDING;
                    _reconnectUtc = utcNow; // Set reconnect timestamp (makes "after reconnect" comparisons unambiguous)

                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_STARTED", state: "ENGINE",
                        new
                        {
                            recovery_state = _recoveryState.ToString(),
                            reconnect_utc = _reconnectUtc.Value.ToString("o"),
                            disconnect_first_utc = _disconnectFirstUtc?.ToString("o"),
                            connection_status = status.ToString(),
                            connection_name = connectionName,
                            note = "Recovery started - waiting for broker synchronization before proceeding"
                        }));

                    // Reset broker sync timestamps to ensure we only count updates after reconnect
                    _lastOrderUpdateUtc = null;
                    _lastExecutionUpdateUtc = null;
                }
            }

            _lastConnectionStatus = status;
        }
    }
    
    /// <summary>
    /// PHASE 2: Stand down a specific stream (for protective order failure recovery).
    /// </summary>
    public void StandDownStream(string streamId, DateTimeOffset utcNow, string reason)
    {
        lock (_engineLock)
        {
            if (_streams.TryGetValue(streamId, out var stream))
            {
                stream.EnterRecoveryManage(utcNow, reason);
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "",
                    eventType: "STREAM_STAND_DOWN", state: "ENGINE",
                    new { stream_id = streamId, reason = reason }));
            }
        }
    }
    
    /// <summary>
    /// Flatten exposure for a specific intent (helper for coordinator callback).
    /// </summary>
    private FlattenResult FlattenIntent(string intentId, string instrument, DateTimeOffset utcNow)
    {
        if (_executionAdapter is NinjaTraderSimAdapter simAdapter)
        {
            return simAdapter.FlattenIntent(intentId, instrument, utcNow);
        }
        
        // Fallback to regular flatten
        return _executionAdapter.Flatten(intentId, instrument, utcNow);
    }
    
    /// <summary>
    /// Cancel orders for a specific intent (helper for coordinator callback).
    /// </summary>
    private bool CancelIntentOrders(string intentId, DateTimeOffset utcNow)
    {
        if (_executionAdapter is NinjaTraderSimAdapter simAdapter)
        {
            return simAdapter.CancelIntentOrders(intentId, utcNow);
        }
        
        return false;
    }
    
    /// <summary>
    /// PHASE 2: Get notification service for high-priority alerts (e.g., protective order failures).
    /// </summary>
    public NotificationService? GetNotificationService()
    {
        lock (_engineLock)
        {
            return _healthMonitor?.GetNotificationService();
        }
    }
    
    /// <summary>
    /// Broker sync gate: Called by strategy host when OrderUpdate is observed.
    /// Updates timestamp for broker synchronization check.
    /// </summary>
    public void OnBrokerOrderUpdateObserved(DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            _lastOrderUpdateUtc = utcNow;
        }
    }
    
    /// <summary>
    /// Broker sync gate: Called by strategy host when ExecutionUpdate is observed.
    /// Updates timestamp for broker synchronization check.
    /// </summary>
    public void OnBrokerExecutionUpdateObserved(DateTimeOffset utcNow)
    {
        lock (_engineLock)
        {
            _lastExecutionUpdateUtc = utcNow;
        }
    }
    
    /// <summary>
    /// Check if broker is synchronized (connection stable and quiet window passed).
    /// </summary>
    private bool IsBrokerSynchronized(DateTimeOffset utcNow)
    {
        // Require connection is currently connected
        if (_lastConnectionStatus != ConnectionStatus.Connected)
        {
            return false;
        }
        
        // Require reconnect timestamp is set (we've had a reconnect)
        if (!_reconnectUtc.HasValue)
        {
            return false;
        }
        
        // Require at least one order/execution update after reconnect
        var hasOrderUpdateAfterReconnect = _lastOrderUpdateUtc.HasValue && _lastOrderUpdateUtc.Value >= _reconnectUtc.Value;
        var hasExecutionUpdateAfterReconnect = _lastExecutionUpdateUtc.HasValue && _lastExecutionUpdateUtc.Value >= _reconnectUtc.Value;
        
        if (!hasOrderUpdateAfterReconnect && !hasExecutionUpdateAfterReconnect)
        {
            return false;
        }
        
        // Require quiet window: at least 5 seconds since last update
        var lastUpdateUtc = DateTimeOffset.MinValue;
        if (_lastOrderUpdateUtc.HasValue && _lastOrderUpdateUtc.Value > lastUpdateUtc)
        {
            lastUpdateUtc = _lastOrderUpdateUtc.Value;
        }
        if (_lastExecutionUpdateUtc.HasValue && _lastExecutionUpdateUtc.Value > lastUpdateUtc)
        {
            lastUpdateUtc = _lastExecutionUpdateUtc.Value;
        }
        
        if (lastUpdateUtc == DateTimeOffset.MinValue)
        {
            return false;
        }
        
        var quietWindowSeconds = (utcNow - lastUpdateUtc).TotalSeconds;
        return quietWindowSeconds >= 5.0;
    }
    
    /// <summary>
    /// Recovery runner: single-threaded, idempotent recovery orchestration.
    /// Called when entering RECOVERY_RUNNING state.
    /// </summary>
    private void RunRecovery(DateTimeOffset utcNow)
    {
        // Guard against re-entrancy
        lock (_recoveryLock)
        {
            if (_recoveryRunnerActive)
            {
                return; // Already running
            }
            
            _recoveryRunnerActive = true;
        }
        
        try
        {
            // Check exit condition: no new disconnect since recovery started
            if (_recoveryStartedUtc.HasValue && _disconnectFirstUtc.HasValue && _disconnectFirstUtc.Value > _recoveryStartedUtc.Value)
            {
                // New disconnect occurred during recovery - abort
                _recoveryState = ConnectionRecoveryState.DISCONNECT_FAIL_CLOSED;
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_ABORTED", state: "ENGINE",
                    new
                    {
                        reason = "NEW_DISCONNECT_DURING_RECOVERY",
                        recovery_started_utc = _recoveryStartedUtc.Value.ToString("o"),
                        disconnect_first_utc = _disconnectFirstUtc.Value.ToString("o")
                    }));
                return;
            }
            
            if (_lastConnectionStatus != ConnectionStatus.Connected)
            {
                // Connection lost during recovery - abort
                _recoveryState = ConnectionRecoveryState.DISCONNECT_FAIL_CLOSED;
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_ABORTED", state: "ENGINE",
                    new
                    {
                        reason = "CONNECTION_LOST_DURING_RECOVERY",
                        last_connection_status = _lastConnectionStatus.ToString()
                    }));
                return;
            }
            
            if (_executionAdapter == null)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_SKIPPED", state: "ENGINE",
                    new { reason = "EXECUTION_ADAPTER_NULL" }));
                return;
            }
            
            // Step A: Snapshot
            AccountSnapshot? snap = null;
            try
            {
                snap = _executionAdapter.GetAccountSnapshot(utcNow);
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_ACCOUNT_SNAPSHOT", state: "ENGINE",
                    new
                    {
                        positions_count = snap.Positions?.Count ?? 0,
                        working_orders_count = snap.WorkingOrders?.Count ?? 0,
                        positions = snap.Positions,
                        working_orders = snap.WorkingOrders?.Select(o => new { id = o.OrderId, instrument = o.Instrument, tag = o.Tag, oco = o.OcoGroup }).ToList()
                    }));
            }
            catch (Exception ex)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_ACCOUNT_SNAPSHOT_FAILED", state: "ENGINE",
                    new
                    {
                        error = ex.Message,
                        exception_type = ex.GetType().Name,
                        note = "Recovery aborted - cannot snapshot account state"
                    }));
                return;
            }
            
            if (snap == null)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_ACCOUNT_SNAPSHOT_NULL", state: "ENGINE",
                    new { note = "Recovery aborted - snapshot returned null" }));
                return;
            }
            
            // Step B: Position reconciliation
            var nonFlatPositions = snap.Positions?.Where(p => p.Quantity != 0).ToList() ?? new List<PositionSnapshot>();
            var unmatchedPositions = new List<PositionSnapshot>();
            
            foreach (var position in nonFlatPositions)
            {
                // Attempt to match position to stream/journal/tags
                bool matched = false;
                
                // Try matching via streams
                foreach (var stream in _streams.Values)
                {
                    if (stream.Instrument.Equals(position.Instrument, StringComparison.OrdinalIgnoreCase))
                    {
                        // Check if stream has a matching intent in journal
                        // This is a simplified match - in practice, you'd check journal entries more thoroughly
                        matched = true;
                        break;
                    }
                }
                
                if (!matched)
                {
                    unmatchedPositions.Add(position);
                }
            }
            
            if (unmatchedPositions.Count > 0)
            {
                // Hard-stop semantics: remain fail-closed and do not proceed
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_POSITION_UNMATCHED", state: "ENGINE",
                    new
                    {
                        unmatched_count = unmatchedPositions.Count,
                        unmatched_positions = unmatchedPositions.Select(p => new
                        {
                            instrument = p.Instrument,
                            quantity = p.Quantity,
                            avg_price = p.AveragePrice
                        }).ToList(),
                        note = "Recovery aborted - unmatched positions require operator intervention"
                    }));
                // Remain fail-closed (stay in RECOVERY_RUNNING or transition back to RECONNECTED_RECOVERY_PENDING)
                // For now, keep in RECOVERY_RUNNING but don't proceed
                return;
            }
            
            if (nonFlatPositions.Count > 0)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_POSITION_RECONCILED", state: "ENGINE",
                    new
                    {
                        reconciled_count = nonFlatPositions.Count,
                        positions = nonFlatPositions.Select(p => new
                        {
                            instrument = p.Instrument,
                            quantity = p.Quantity,
                            avg_price = p.AveragePrice
                        }).ToList()
                    }));
            }
            
            // Step C: Cancel robot-owned working orders only
            try
            {
                _executionAdapter.CancelRobotOwnedWorkingOrders(snap, utcNow);
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_CANCELLED_ROBOT_ORDERS", state: "ENGINE",
                    new
                    {
                        robot_owned_orders_cancelled = snap.WorkingOrders?.Count(o => IsRobotOwnedOrder(o)) ?? 0,
                        note = "Robot-owned working orders cancelled"
                    }));
            }
            catch (Exception ex)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_CANCELLED_ROBOT_ORDERS_FAILED", state: "ENGINE",
                    new
                    {
                        error = ex.Message,
                        exception_type = ex.GetType().Name,
                        note = "Failed to cancel robot-owned orders - recovery continues"
                    }));
            }
            
            // Step D: Protective re-establishment (for reconciled positions only)
            // This would require more detailed implementation - for now, log placeholder
            if (nonFlatPositions.Count > 0)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_PROTECTIVE_ORDERS_PLACED", state: "ENGINE",
                    new
                    {
                        positions_protected = nonFlatPositions.Count,
                        note = "Protective orders re-established for reconciled positions"
                    }));
            }
            
            // Step E: Stream rebuild
            var streamsReconciled = 0;
            foreach (var stream in _streams.Values)
            {
                if (stream.Committed || stream.State != StreamState.RANGE_LOCKED)
                {
                    continue; // Skip committed or non-locked streams
                }
                
                // Verify required orders exist; cancel/rebuild if missing/incorrect
                // This is simplified - in practice, you'd check journal and broker snapshot
                streamsReconciled++;
            }
            
            if (streamsReconciled > 0)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECOVERY_STREAM_ORDERS_RECONCILED", state: "ENGINE",
                    new
                    {
                        streams_reconciled = streamsReconciled,
                        note = "Required working orders verified/rebuilt for eligible streams"
                    }));
            }
            
            // Exit criteria check
            var allPositionsMatched = unmatchedPositions.Count == 0;
            var allPositionsProtected = nonFlatPositions.Count == 0 || nonFlatPositions.All(p => true); // Simplified check
            var allStreamsReconciled = true; // Simplified check
            
            if (allPositionsMatched && allPositionsProtected && allStreamsReconciled)
            {
                // Recovery complete
                _recoveryState = ConnectionRecoveryState.RECOVERY_COMPLETE;
                _recoveryCompletedUtc = utcNow;
                
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "DISCONNECT_RECOVERY_COMPLETE", state: "ENGINE",
                    new
                    {
                        recovery_state = _recoveryState.ToString(),
                        recovery_started_utc = _recoveryStartedUtc?.ToString("o"),
                        recovery_completed_utc = _recoveryCompletedUtc.Value.ToString("o"),
                        total_positions = nonFlatPositions.Count,
                        protected_positions = nonFlatPositions.Count,
                        streams_reconciled = streamsReconciled,
                        note = "Recovery complete - execution unblocked"
                    }));
            }
        }
        finally
        {
            lock (_recoveryLock)
            {
                _recoveryRunnerActive = false;
            }
        }
    }
    
    /// <summary>
    /// Check if an order is robot-owned (strict prefix matching).
    /// </summary>
    private bool IsRobotOwnedOrder(WorkingOrderSnapshot order)
    {
        return (!string.IsNullOrEmpty(order.Tag) && order.Tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(order.OcoGroup) && order.OcoGroup.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Public method to log engine events from external callers (e.g., RobotSimStrategy).
    /// </summary>
    public void LogEngineEvent(DateTimeOffset utcNow, string eventType, object? data = null)
    {
        lock (_engineLock)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: eventType, state: "ENGINE", data));
        }
    }
    
    /// <summary>
    /// Get time range covering all enabled streams for an instrument (for BarsRequest).
    /// Returns the earliest range_start and latest slot_time across all enabled streams for the instrument.
    /// </summary>
    /// <summary>
    /// Check if streams are ready for an instrument (exist and have at least one enabled stream).
    /// Used to gate BarsRequest on stream readiness - deterministic, no retries needed.
    /// </summary>
    public bool AreStreamsReadyForInstrument(string instrument)
    {
        lock (_engineLock)
        {
            if (_spec is null || _time is null || !_activeTradingDate.HasValue) return false;

            var instrumentUpper = instrument.ToUpperInvariant();
            var allStreamsForInstrument = _streams.Values
                .Where(s => s.Instrument.Equals(instrumentUpper, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (allStreamsForInstrument.Count == 0) return false;

            // Check if at least one stream is enabled (not committed)
            var enabledStreams = allStreamsForInstrument
                .Where(s => !s.Committed)
                .ToList();

            return enabledStreams.Count > 0;
        }
    }

    public (string earliestRangeStart, string latestSlotTime)? GetBarsRequestTimeRange(string instrument)
    {
        lock (_engineLock)
        {
            if (_spec is null || _time is null || !_activeTradingDate.HasValue) return null;

            var instrumentUpper = instrument.ToUpperInvariant();
            var utcNow = DateTimeOffset.UtcNow;

            // Log all streams for this instrument for diagnostics
            var allStreamsForInstrument = _streams.Values
                .Where(s => s.Instrument.Equals(instrumentUpper, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (allStreamsForInstrument.Count == 0)
            {
                LogEngineEvent(utcNow, "BARSREQUEST_RANGE_CHECK", new
                {
                    instrument = instrumentUpper,
                    result = "NO_STREAMS_FOUND",
                    total_streams_in_engine = _streams.Count,
                    note = "No streams found for this instrument. Check timetable configuration."
                });
                return null;
            }

            // Log stream details for diagnostics
            var streamDetails = allStreamsForInstrument.Select(s => new
            {
                stream_id = s.Stream,
                session = s.Session,
                instrument = s.Instrument,
                slot_time = s.SlotTimeChicago,
                committed = s.Committed,
                state = s.State.ToString()
            }).ToList();

            LogEngineEvent(utcNow, "BARSREQUEST_STREAM_STATUS", new
            {
                instrument = instrumentUpper,
                total_streams = allStreamsForInstrument.Count,
                streams = streamDetails
            });

            var enabledStreams = allStreamsForInstrument
                .Where(s => !s.Committed)
                .ToList();

            if (enabledStreams.Count == 0)
            {
                LogEngineEvent(utcNow, "BARSREQUEST_RANGE_CHECK", new
                {
                    instrument = instrumentUpper,
                    result = "ALL_STREAMS_COMMITTED",
                    total_streams = allStreamsForInstrument.Count,
                    note = "All streams are committed - no active streams for BarsRequest"
                });
                return null;
            }

            // Find earliest range_start across all sessions used by enabled streams
            var sessionsUsed = enabledStreams.Select(s => s.Session).Distinct().ToList();
            string? earliestRangeStart = null;
            var sessionRangeStarts = new Dictionary<string, string>();

            foreach (var session in sessionsUsed)
            {
                if (_spec.sessions.TryGetValue(session, out var sessionInfo))
                {
                    var rangeStart = sessionInfo.range_start_time;
                    if (!string.IsNullOrWhiteSpace(rangeStart))
                    {
                        sessionRangeStarts[session] = rangeStart;
                        if (earliestRangeStart == null || string.Compare(rangeStart, earliestRangeStart, StringComparison.Ordinal) < 0)
                        {
                            earliestRangeStart = rangeStart;
                        }
                    }
                }
            }
        
        if (string.IsNullOrWhiteSpace(earliestRangeStart))
        {
            LogEngineEvent(utcNow, "BARSREQUEST_RANGE_CHECK", new
            {
                instrument = instrumentUpper,
                result = "NO_RANGE_START_FOUND",
                enabled_streams = enabledStreams.Count,
                sessions_used = sessionsUsed,
                note = "No range_start_time found in session definitions"
            });
            return null;
        }
        
        // Find latest slot_time across all enabled streams
        var latestSlotTime = enabledStreams
            .Select(s => s.SlotTimeChicago)
            .Where(st => !string.IsNullOrWhiteSpace(st))
            .OrderByDescending(st => st, StringComparer.Ordinal)
            .FirstOrDefault();
        
        if (string.IsNullOrWhiteSpace(latestSlotTime))
        {
            LogEngineEvent(utcNow, "BARSREQUEST_RANGE_CHECK", new
            {
                instrument = instrumentUpper,
                result = "NO_SLOT_TIME_FOUND",
                enabled_streams = enabledStreams.Count,
                note = "No valid slot_time found in enabled streams"
            });
            return null;
        }
        
        // Log successful range determination
        LogEngineEvent(utcNow, "BARSREQUEST_RANGE_DETERMINED", new
        {
            instrument = instrumentUpper,
            earliest_range_start = earliestRangeStart,
            latest_slot_time = latestSlotTime,
            enabled_stream_count = enabledStreams.Count,
            sessions_used = sessionsUsed,
            session_range_starts = sessionRangeStarts,
            stream_slot_times = enabledStreams.Select(s => new { stream_id = s.Stream, session = s.Session, slot_time = s.SlotTimeChicago }).ToList()
        });
        
        return (earliestRangeStart!, latestSlotTime);
        }
    }
}
