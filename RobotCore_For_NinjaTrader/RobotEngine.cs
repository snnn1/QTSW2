using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Execution;
using QTSW2.Robot.Core.Notifications;

public sealed class RobotEngine
{
    private readonly string _root;
    private readonly RobotLogger _log; // Kept for backward compatibility during migration
    private readonly RobotLoggingService? _loggingService; // New async logging service (Fix B)
    private readonly JournalStore _journals;
    private readonly FilePoller _timetablePoller;

    private ParitySpec? _spec;
    private TimeService? _time;

    private string _specPath = "";
    private string _timetablePath = "";

    private string? _lastTimetableHash;
    private DateOnly? _activeTradingDate; // PHASE 3: Store as DateOnly (authoritative), convert to string only for logging/journal

    private readonly Dictionary<string, StreamStateMachine> _streams = new();
    private readonly ExecutionMode _executionMode;
    private IExecutionAdapter? _executionAdapter;
    private RiskGate? _riskGate;
    private readonly ExecutionJournal _executionJournal;
    private readonly KillSwitch _killSwitch;
    private readonly ExecutionSummary _executionSummary;
    private HealthMonitor? _healthMonitor; // Optional: health monitoring and alerts

    // Logging configuration
    private readonly LoggingConfig _loggingConfig;

    // ENGINE-level bar ingress diagnostic (rate-limiting per instrument)
    private readonly Dictionary<string, DateTimeOffset> _lastBarHeartbeatPerInstrument = new();
    private int BAR_HEARTBEAT_RATE_LIMIT_MINUTES => _loggingConfig.enable_diagnostic_logs ? 1 : 5; // More frequent if diagnostics enabled
    
    // ENGINE-level tick heartbeat diagnostic (rate-limited)
    private DateTimeOffset _lastTickHeartbeat = DateTimeOffset.MinValue;
    private int TICK_HEARTBEAT_RATE_LIMIT_MINUTES => _loggingConfig.diagnostic_rate_limits?.tick_heartbeat_minutes ?? (_loggingConfig.enable_diagnostic_logs ? 1 : 5);
    
    // Engine heartbeat tracking for liveness monitoring
    private DateTimeOffset _lastTickUtc = DateTimeOffset.MinValue;
    
    // Account/environment info for startup banner (set by strategy host)
    private string? _accountName;
    private string? _environment;

    /// <summary>
    /// Set account and environment info for startup banner.
    /// Called by strategy host before Start().
    /// </summary>
    public void SetAccountInfo(string? accountName, string? environment)
    {
        _accountName = accountName;
        _environment = environment;
    }

    
    /// <summary>
    /// Get last tick timestamp for liveness monitoring.
    /// </summary>
    public DateTimeOffset GetLastTickUtc() => _lastTickUtc;

    public RobotEngine(string projectRoot, TimeSpan timetablePollInterval, ExecutionMode executionMode = ExecutionMode.DRYRUN, string? customLogDir = null, string? customTimetablePath = null, string? instrument = null, bool useAsyncLogging = true)
    {
        _root = projectRoot;
        _executionMode = executionMode;
        
        // Load logging configuration
        _loggingConfig = LoggingConfig.LoadFromFile(projectRoot);
        
        // Initialize async logging service (Fix B) - singleton per project root to prevent file lock contention
        if (useAsyncLogging)
        {
            _loggingService = RobotLoggingService.GetOrCreate(projectRoot, customLogDir);
        }
        
        // Create logger with service reference so ENGINE logs route through singleton
        _log = new RobotLogger(projectRoot, customLogDir, instrument, _loggingService);
        
        _journals = new JournalStore(projectRoot);
        _timetablePoller = new FilePoller(timetablePollInterval);

        _specPath = Path.Combine(_root, "configs", "analyzer_robot_parity.json");
        _timetablePath = customTimetablePath ?? Path.Combine(_root, "data", "timetable", "timetable_current.json");

        // Initialize execution components that don't depend on spec
        _killSwitch = new KillSwitch(projectRoot, _log);
        _executionJournal = new ExecutionJournal(projectRoot, _log);
        _executionSummary = new ExecutionSummary();
        
        // Initialize health monitor (fail-closed: if config missing/invalid, monitoring disabled)
        try
        {
            var healthMonitorPath = Path.Combine(projectRoot, "configs", "robot", "health_monitor.json");
            if (File.Exists(healthMonitorPath))
            {
                var healthMonitorJson = File.ReadAllText(healthMonitorPath);
                var healthMonitorConfig = JsonUtil.Deserialize<HealthMonitorConfig>(healthMonitorJson);
                if (healthMonitorConfig != null)
                {
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
                    
                    if (healthMonitorConfig.enabled)
                    {
                        _healthMonitor = new HealthMonitor(projectRoot, healthMonitorConfig, _log);
                        // Set session awareness callback: check if any streams are in active trading state
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
            // Fail-closed: if config load fails, monitoring disabled (no alerts)
            // Log error for debugging (was previously silent)
            LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HEALTH_MONITOR_CONFIG_ERROR", state: "ENGINE",
                new { error = ex.Message, error_type = ex.GetType().Name }));
        }
    }

    public void Start()
    {
        var utcNow = DateTimeOffset.UtcNow;
        
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
                    notificationService.EnqueueNotification("LIVE_MODE_BLOCKED", 
                        "CRITICAL: LIVE Trading Blocked", 
                        $"Robot attempted to start in LIVE mode but it is not enabled. Execution blocked. Error: {errorMsg}", 
                        priority: 2); // Emergency priority
                }
            }
            
            throw new InvalidOperationException(errorMsg);
        }
        
        // Start async logging service if enabled (Fix B)
        _loggingService?.Start();
        
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
        }
        catch (Exception ex)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_INVALID", state: "ENGINE", new { error = ex.Message }));
            throw;
        }

        // Initialize execution components now that spec is loaded
        _riskGate = new RiskGate(_spec, _time, _log, _killSwitch);
        
        // Try to create adapter (will throw if LIVE mode)
        try
        {
            _executionAdapter = ExecutionAdapterFactory.Create(_executionMode, _root, _log, _executionJournal);
            
            // PHASE 2: Set engine callbacks for protective order failure recovery
            if (_executionAdapter is NinjaTraderSimAdapter simAdapter)
            {
                simAdapter.SetEngineCallbacks(
                    standDownStreamCallback: (streamId, utcNow, reason) => StandDownStream(streamId, utcNow, reason),
                    getNotificationServiceCallback: () => GetNotificationService());
            }
        }
        catch (InvalidOperationException)
        {
            // Re-throw LIVE mode errors (already handled above, but double-check)
            throw;
        }

        // Log execution mode and adapter
        var adapterType = _executionAdapter.GetType().Name;
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_MODE_SET", state: "ENGINE",
            new { mode = _executionMode.ToString(), adapter = adapterType }));

        // Load timetable immediately (fail closed if invalid)
        // Health monitor update happens automatically in ReloadTimetableIfChanged() → ApplyTimetable()
        ReloadTimetableIfChanged(utcNow, force: true);
        
        // Check if strategy started after range window for any stream
        CheckStartupTiming(utcNow);
        
        // PHASE 1: Emit startup operator banner with execution mode, account, environment, timetable info
        EmitStartupBanner(utcNow);
        
        // Initialize heartbeat timestamp
        _lastTickUtc = utcNow;
        
        // Start health monitor if enabled
        _healthMonitor?.Start();
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
        return _streams.Values.Any(s => !s.Committed && 
            (s.State == StreamState.ARMED || 
             s.State == StreamState.RANGE_BUILDING || 
             s.State == StreamState.RANGE_LOCKED));
    }
    
    /// <summary>
    /// PHASE 1: Emit prominent operator banner log with execution mode, account, environment, timetable info.
    /// </summary>
    private void EmitStartupBanner(DateTimeOffset utcNow)
    {
        var enabledStreams = _streams.Values.Where(s => !s.Committed).ToList();
        var enabledInstruments = enabledStreams.Select(s => s.Instrument).Distinct().ToList();
        
        var bannerData = new Dictionary<string, object?>
        {
            ["execution_mode"] = _executionMode.ToString(),
            ["account_name"] = _accountName ?? "UNKNOWN",
            ["environment"] = _environment ?? (_executionMode == ExecutionMode.DRYRUN ? "DRYRUN" : _executionMode == ExecutionMode.SIM ? "SIM" : "UNKNOWN"),
            ["timetable_hash"] = _lastTimetableHash ?? "NOT_LOADED",
            ["timetable_path"] = _timetablePath,
            ["enabled_stream_count"] = enabledStreams.Count,
            ["enabled_instruments"] = enabledInstruments,
            ["enabled_streams"] = enabledStreams.Select(s => new { stream = s.Stream, instrument = s.Instrument, session = s.Session, slot_time = s.SlotTimeChicago }).ToList(),
            ["spec_name"] = _spec?.spec_name ?? "NOT_LOADED",
            ["spec_revision"] = _spec?.spec_revision ?? "NOT_LOADED",
            ["kill_switch_enabled"] = _killSwitch.IsEnabled(),
            ["health_monitor_enabled"] = _healthMonitor != null
        };
        
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "", 
            eventType: "OPERATOR_BANNER", state: "ENGINE", bannerData));
    }

    public void Stop()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var tradingDateStr = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "ENGINE_STOP", state: "ENGINE"));
        
        // Write execution summary if not DRYRUN
        if (_executionMode != ExecutionMode.DRYRUN)
        {
            var summary = _executionSummary.GetSnapshot();
            var summaryDir = Path.Combine(_root, "data", "execution_summaries");
            Directory.CreateDirectory(summaryDir);
            var summaryPath = Path.Combine(summaryDir, $"summary_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
            var json = JsonUtil.Serialize(summary);
            File.WriteAllText(summaryPath, json);
            var summaryTradingDateStr = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: summaryTradingDateStr, eventType: "EXECUTION_SUMMARY_WRITTEN", state: "ENGINE",
                new { summary_path = summaryPath }));
        }
        
        // Stop health monitor
        _healthMonitor?.Stop();
        
        // Release reference to async logging service (Fix B) - singleton will dispose when all references released
        _loggingService?.Release();
    }

    public void Tick(DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null) return;

        // PHASE 3: Update engine heartbeat timestamp for liveness monitoring
        _lastTickUtc = utcNow;
        
        // PHASE 3: Update health monitor with engine tick timestamp
        _healthMonitor?.UpdateEngineTick(utcNow);

        // ENGINE_TICK_HEARTBEAT: Diagnostic to prove Tick is advancing even with zero bars
        // Only logged if diagnostic logs are enabled
        if (_loggingConfig.enable_diagnostic_logs)
        {
            var timeSinceLastTickHeartbeat = (utcNow - _lastTickHeartbeat).TotalMinutes;
            if (timeSinceLastTickHeartbeat >= TICK_HEARTBEAT_RATE_LIMIT_MINUTES || _lastTickHeartbeat == DateTimeOffset.MinValue)
            {
                _lastTickHeartbeat = utcNow;
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "ENGINE_TICK_HEARTBEAT", state: "ENGINE",
                    new
                    {
                        utc_now = utcNow.ToString("o"),
                        active_stream_count = _streams.Count,
                        note = "timer-based tick"
                    }));
            }
        }

        // Timetable reactivity
        if (_timetablePoller.ShouldPoll(utcNow))
        {
            // PHASE 3: Update health monitor with timetable poll timestamp
            _healthMonitor?.UpdateTimetablePoll(utcNow);
            
            ReloadTimetableIfChanged(utcNow, force: false);
        }

        foreach (var s in _streams.Values)
            s.Tick(utcNow);
        
        // Health monitor: evaluate data loss (rate-limited internally)
        _healthMonitor?.Evaluate(utcNow);
    }

    public void OnBar(DateTimeOffset barUtc, string instrument, decimal open, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null) return;

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
                var barChicagoTime = _time.ConvertUtcToChicago(barUtc);
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "ENGINE_BAR_HEARTBEAT", state: "ENGINE",
                    new
                    {
                        instrument = instrument,
                        raw_bar_time = barUtc.ToString("o"),
                        raw_bar_time_kind = barUtc.DateTime.Kind.ToString(),
                        utc_time = barUtc.ToString("o"),
                        chicago_time = barChicagoTime.ToString("o"),
                        chicago_offset = barChicagoTime.Offset.ToString(),
                        close_price = close,
                        note = "engine-level diagnostic"
                    }));
            }
        }

        // PHASE 3: Derive trading_date from bar timestamp (bar-derived date is authoritative)
        // Parse once, store as DateOnly
        var barChicagoDate = _time.GetChicagoDateToday(barUtc);

        // Check if trading_date needs to roll over to a new day
        if (_activeTradingDate != barChicagoDate)
        {
            // Trading date rollover: update engine trading_date and notify all streams
            var previousTradingDate = _activeTradingDate;
            var isInitialization = !_activeTradingDate.HasValue;
            _activeTradingDate = barChicagoDate;

            // Log trading day rollover (convert to string only for logging)
            var barTradingDateStr = barChicagoDate.ToString("yyyy-MM-dd");
            var previousTradingDateStr = previousTradingDate?.ToString("yyyy-MM-dd") ?? "UNSET";
            
            // Only log ENGINE-level rollover if it's an actual rollover (not initialization)
            // Initialization is handled silently to prevent log spam
            if (!isInitialization)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: barTradingDateStr, eventType: "TRADING_DAY_ROLLOVER", state: "ENGINE",
                    new
                    {
                        previous_trading_date = previousTradingDateStr,
                        new_trading_date = barTradingDateStr,
                        bar_timestamp_utc = barUtc.ToString("o"),
                        bar_timestamp_chicago = _time.ConvertUtcToChicago(barUtc).ToString("o")
                    }));
            }

            // Update all stream state machines to use the new trading_date (pass DateOnly directly)
            foreach (var stream in _streams.Values)
            {
                stream.UpdateTradingDate(barChicagoDate, utcNow);
            }
        }

        // Replay invariant check: engine trading_date must match bar-derived trading_date
        // This prevents the trading_date mismatch bug from silently reappearing
        if (_activeTradingDate.HasValue && _activeTradingDate.Value != barChicagoDate)
        {
            // Check if we're in replay mode (timetable has replay metadata)
            var isReplay = false;
            try
            {
                if (File.Exists(_timetablePath))
                {
                    var timetable = TimetableContract.LoadFromFile(_timetablePath);
                    isReplay = timetable.metadata?.replay == true;
                }
            }
            catch
            {
                // If we can't determine replay mode, skip invariant check (fail open for live mode)
            }

            if (isReplay)
            {
                // In replay mode, trading_date mismatch after rollover is a critical error
                var engineTradingDateStr = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
                var barTradingDateStr = barChicagoDate.ToString("yyyy-MM-dd");
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: engineTradingDateStr, eventType: "REPLAY_INVARIANT_VIOLATION", state: "ENGINE",
                    new
                    {
                        error = "TRADING_DATE_MISMATCH",
                        engine_trading_date = engineTradingDateStr,
                        bar_derived_trading_date = barTradingDateStr,
                        bar_timestamp_utc = barUtc.ToString("o"),
                        bar_timestamp_chicago = _time.ConvertUtcToChicago(barUtc).ToString("o"),
                        message = "Engine trading_date does not match bar-derived trading_date after rollover. Replay aborted."
                    }));
                StandDown();
                return;
            }
        }

        // Pass bar data to streams of matching instrument
        foreach (var s in _streams.Values)
        {
            if (s.IsSameInstrument(instrument))
                s.OnBar(barUtc, open, high, low, close, utcNow);
        }
    }

    private void ReloadTimetableIfChanged(DateTimeOffset utcNow, bool force)
    {
        if (_spec is null || _time is null) return;

        var poll = _timetablePoller.Poll(_timetablePath, utcNow);
        if (poll.Error is not null)
        {
            var tradingDateStr = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = poll.Error }));
            StandDown();
            return;
        }

        if (!force && !poll.Changed) return;

        var previousHash = _lastTimetableHash;
        _lastTimetableHash = poll.Hash;

        TimetableContract timetable;
        try
        {
            timetable = TimetableContract.LoadFromFile(_timetablePath);
        }
        catch (Exception ex)
        {
            var tradingDateStr = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = "PARSE_ERROR", error = ex.Message }));
            StandDown();
            return;
        }

        if (!TimeService.TryParseDateOnly(timetable.trading_date, out var tradingDate))
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = "BAD_TRADING_DATE", trading_date = timetable.trading_date }));
            StandDown();
            return;
        }

        if (timetable.timezone != "America/Chicago")
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: timetable.trading_date, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = "TIMEZONE_MISMATCH", timezone = timetable.timezone }));
            StandDown();
            return;
        }

        var chicagoToday = _time.GetChicagoDateToday(utcNow);
        
        // For replay timetables (metadata.replay = true), allow trading_date <= chicagoToday
        // For live timetables, require exact match (trading_date == chicagoToday)
        var isReplayTimetable = timetable.metadata?.replay == true;
        if (isReplayTimetable)
        {
            if (tradingDate > chicagoToday)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: timetable.trading_date, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                    new { reason = "REPLAY_TRADING_DATE_FUTURE", trading_date = timetable.trading_date, chicago_today = chicagoToday.ToString("yyyy-MM-dd") }));
                StandDown();
                return;
            }
        }
        else
        {
            // Live timetable: require exact date match (fail closed on stale timetable)
            if (tradingDate != chicagoToday)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: timetable.trading_date, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                    new { reason = "STALE_TRADING_DATE", trading_date = timetable.trading_date, chicago_today = chicagoToday.ToString("yyyy-MM-dd") }));
                StandDown();
                return;
            }
        }

        // PHASE 3: Store as DateOnly (already parsed above)
        _activeTradingDate = tradingDate;

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: timetable.trading_date, eventType: "TIMETABLE_UPDATED", state: "ENGINE",
            new 
            { 
                previous_hash = previousHash,
                new_hash = _lastTimetableHash,
                enabled_stream_count = timetable.streams.Count(s => s.enabled),
                total_stream_count = timetable.streams.Count
            }));

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: timetable.trading_date, eventType: "TIMETABLE_LOADED", state: "ENGINE",
            new { streams = timetable.streams.Count, timetable_hash = _lastTimetableHash, timetable_path = _timetablePath }));
        
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: timetable.trading_date, eventType: "TIMETABLE_VALIDATED", state: "ENGINE",
            new { trading_date = timetable.trading_date, is_replay = isReplayTimetable }));

        ApplyTimetable(timetable, tradingDate, utcNow);
        
        // Health monitor no longer uses timetable for monitoring windows
        // (simplified to only monitor connection loss and data loss)
    }

    private void ApplyTimetable(TimetableContract timetable, DateOnly tradingDate, DateTimeOffset utcNow)
    {
        // CRITICAL FIX: Use tradingDate parameter instead of _activeTradingDate to allow streams
        // to be created even after StandDown() clears _activeTradingDate.
        // _activeTradingDate is set just before this call, but if StandDown() was called earlier,
        // we still want to create streams from a valid timetable.
        if (_spec is null || _time is null || _lastTimetableHash is null) return;

        var incoming = timetable.streams.Where(s => s.enabled).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var streamIdOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        // Log timetable parsing stats
        int acceptedCount = 0;
        int skippedCount = 0;
        var skippedReasons = new Dictionary<string, int>();

        foreach (var directive in incoming)
        {
            var streamId = directive.stream;
            
            // Track stream ID occurrences for duplicate detection
            if (!string.IsNullOrWhiteSpace(streamId))
            {
                if (!streamIdOccurrences.TryGetValue(streamId, out var count))
                    count = 0;
                streamIdOccurrences[streamId] = count + 1;
            }
            
            var instrument = (directive.instrument ?? "").ToUpperInvariant();
            var session = directive.session ?? "";
            var slotTimeChicago = directive.slot_time ?? "";
            DateTimeOffset? slotTimeUtc = null;
            try
            {
                // CRITICAL FIX: Use tradingDate parameter instead of _activeTradingDate
                if (!string.IsNullOrWhiteSpace(slotTimeChicago))
                {
                    var slotTimeChicagoTime = _time.ConstructChicagoTime(tradingDate, slotTimeChicago);
                    slotTimeUtc = slotTimeChicagoTime.ToUniversalTime();
                }
            }
            catch
            {
                slotTimeUtc = null;
            }

            var tradingDateStr = tradingDate.ToString("yyyy-MM-dd"); // Use parameter, not _activeTradingDate

            if (string.IsNullOrWhiteSpace(streamId) ||
                string.IsNullOrWhiteSpace(instrument) ||
                string.IsNullOrWhiteSpace(session) ||
                string.IsNullOrWhiteSpace(slotTimeChicago))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("MISSING_FIELDS", out var count)) count = 0;
                skippedReasons["MISSING_FIELDS"] = count + 1;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId ?? "", instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "MISSING_FIELDS", stream_id = streamId, instrument = instrument, session = session, slot_time = slotTimeChicago }));
                continue;
            }

            if (!_spec.sessions.ContainsKey(session))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("UNKNOWN_SESSION", out var count2)) count2 = 0;
                skippedReasons["UNKNOWN_SESSION"] = count2 + 1;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "UNKNOWN_SESSION", stream_id = streamId, session = session }));
                continue;
            }

            // slot_time validation (fail closed per stream)
            var allowed = _spec.sessions[session].slot_end_times;
            if (!allowed.Contains(slotTimeChicago))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("INVALID_SLOT_TIME", out var count3)) count3 = 0;
                skippedReasons["INVALID_SLOT_TIME"] = count3 + 1;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "INVALID_SLOT_TIME", stream_id = streamId, slot_time = slotTimeChicago, allowed_times = allowed }));
                continue;
            }

            if (!_spec.TryGetInstrument(instrument, out _))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("UNKNOWN_INSTRUMENT", out var count4)) count4 = 0;
                skippedReasons["UNKNOWN_INSTRUMENT"] = count4 + 1;
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "UNKNOWN_INSTRUMENT", stream_id = streamId, instrument = instrument }));
                continue;
            }

            seen.Add(streamId);
            acceptedCount++;

            if (_streams.TryGetValue(streamId, out var sm))
            {
                if (sm.Committed)
                {
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                        "UPDATE_IGNORED_COMMITTED", "ENGINE", new { reason = "STREAM_COMMITTED" }));
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
                
                sm.ApplyDirectiveUpdate(directive.slot_time, tradingDate, utcNow);
            }
            else
            {
                // PHASE 3: New stream - pass DateOnly directly (authoritative), convert to string only for logging/journal
                
                // PHASE 3: Pass DateOnly to constructor (will be converted to string internally for journal)
                // Pass logging config for diagnostic control
                var newSm = new StreamStateMachine(_time, _spec, _log, _journals, tradingDate, _lastTimetableHash, directive, _executionMode, _executionAdapter, _riskGate, _executionJournal, loggingConfig: _loggingConfig);
                
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
                
                _streams[streamId] = newSm;

                if (newSm.Committed)
                {
                    LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                        "STREAM_SKIPPED", "ENGINE", new { reason = "ALREADY_COMMITTED_JOURNAL" }));
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
            catch
            {
                // Conversion failed - fall through to RobotLogger.Write() which has its own fallback logic
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

        var level = "INFO";
        var eventType = dict.TryGetValue("event_type", out var et) ? et?.ToString() ?? "" : "";
        if (eventType.Contains("ERROR") || eventType.Contains("FAIL") || eventType.Contains("INVALID") || eventType.Contains("VIOLATION"))
            level = "ERROR";
        else if (eventType.Contains("WARN") || eventType.Contains("BLOCKED"))
            level = "WARN";

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

        return new RobotLogEvent(utcNow, level, source, instrument, eventType, message, data: data.Count > 0 ? data : null);
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
    /// Expose execution adapter for strategy files (replaces reflection-based access).
    /// </summary>
    internal IExecutionAdapter? GetExecutionAdapter() => _executionAdapter;
    
    /// <summary>
    /// Expose time service for components that need direct access (e.g., bar providers).
    /// </summary>
    internal TimeService? GetTimeService() => _time;
    
    /// <summary>
    /// Forward connection status update to health monitor (replaces reflection-based access).
    /// </summary>
    public void OnConnectionStatusUpdate(ConnectionStatus status, string connectionName)
    {
        _healthMonitor?.OnConnectionStatusUpdate(status, connectionName, DateTimeOffset.UtcNow);
    }
    
    /// <summary>
    /// PHASE 2: Stand down a specific stream (for protective order failure recovery).
    /// </summary>
    public void StandDownStream(string streamId, DateTimeOffset utcNow, string reason)
    {
        if (_streams.TryGetValue(streamId, out var stream))
        {
            stream.EnterRecoveryManage(utcNow, reason);
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate?.ToString("yyyy-MM-dd") ?? "", 
                eventType: "STREAM_STAND_DOWN", state: "ENGINE",
                new { stream_id = streamId, reason = reason }));
        }
    }
    
    /// <summary>
    /// PHASE 2: Get notification service for high-priority alerts (e.g., protective order failures).
    /// </summary>
    public NotificationService? GetNotificationService()
    {
        return _healthMonitor?.GetNotificationService();
    }
}
