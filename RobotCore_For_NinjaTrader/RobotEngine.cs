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
    public string GetTradingDate() => TradingDateString;

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

        // Load timetable and lock trading date from it (fail closed if invalid)
        // Trading date is locked immediately from timetable, then streams are created
        ReloadTimetableIfChanged(utcNow, force: true);
        
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
    /// Called after trading date is locked from first session-valid bar.
    /// </summary>
    private void EmitStartupBanner(DateTimeOffset utcNow)
    {
        var enabledStreams = _streams.Values.Where(s => !s.Committed).ToList();
        var enabledInstruments = enabledStreams.Select(s => s.Instrument).Distinct().ToList();
        
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
            ["kill_switch_enabled"] = _killSwitch.IsEnabled(),
            ["health_monitor_enabled"] = _healthMonitor != null
        };
        
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, 
            eventType: "OPERATOR_BANNER", state: "ENGINE", bannerData));
    }

    public void Stop()
    {
        var utcNow = DateTimeOffset.UtcNow;
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ENGINE_STOP", state: "ENGINE"));
        
        // Write execution summary if not DRYRUN
        if (_executionMode != ExecutionMode.DRYRUN)
        {
            var summary = _executionSummary.GetSnapshot();
            var summaryDir = Path.Combine(_root, "data", "execution_summaries");
            Directory.CreateDirectory(summaryDir);
            var summaryPath = Path.Combine(summaryDir, $"summary_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
            var json = JsonUtil.Serialize(summary);
            File.WriteAllText(summaryPath, json);
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "EXECUTION_SUMMARY_WRITTEN", state: "ENGINE",
                new { summary_path = summaryPath }));
        }
        
        // Stop health monitor
        _healthMonitor?.Stop();
        
        // Release reference to async logging service (Fix B) - singleton will dispose when all references released
        _loggingService?.Release();
    }

    public void Tick(DateTimeOffset utcNow)
    {
        // CRITICAL: Defensive checks - engine must be initialized
        if (_spec is null)
        {
            // Spec should be loaded in Start() - if null, engine is in invalid state
            // Log error but don't throw (to prevent tick timer from crashing)
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_TICK_INVALID_STATE", state: "ENGINE",
                new { error = "Spec is null - engine not properly initialized" }));
            return;
        }
        
        if (_time is null)
        {
            // TimeService should be created in Start() - if null, engine is in invalid state
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_TICK_INVALID_STATE", state: "ENGINE",
                new { error = "TimeService is null - engine not properly initialized" }));
            return;
        }

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
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ENGINE_TICK_HEARTBEAT", state: "ENGINE",
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

        // CRITICAL: Reject partial bars - only accept fully closed bars
        // Partial-bar contamination problem:
        // - BarsRequest returns fully closed bars (good)
        // - Live bars should be closed (OnBarClose), but defensive check needed
        // - If you start mid-minute, BarsRequest gives completed prior bar
        // - Live feed may later emit a bar that partially overlaps expectations
        // - What breaks: Off-by-one minute range errors, incomplete data
        //
        // Rule: Only accept bars that are at least 1 minute old (bar period)
        // This ensures the bar is fully closed before we use it
        var barAgeMinutes = (utcNow - barUtc).TotalMinutes;
        const double MIN_BAR_AGE_MINUTES = 1.0; // Bar period (1 minute bars)
        
        if (barAgeMinutes < MIN_BAR_AGE_MINUTES)
        {
            // Bar is too recent - likely partial/in-progress, reject it
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "BAR_PARTIAL_REJECTED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    bar_timestamp_utc = barUtc.ToString("o"),
                    current_time_utc = utcNow.ToString("o"),
                    bar_age_minutes = barAgeMinutes,
                    min_bar_age_minutes = MIN_BAR_AGE_MINUTES,
                    note = "Bar rejected - too recent, likely partial/in-progress bar. Only fully closed bars accepted."
                }));
            return; // Reject partial bar
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
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "BAR_RECEIVED_BEFORE_DATE_LOCKED", state: "ENGINE",
                new
                {
                    bar_timestamp_utc = barUtc.ToString("o"),
                    bar_timestamp_chicago = barChicagoTime.ToString("o"),
                    bar_trading_date = barChicagoDate.ToString("yyyy-MM-dd"),
                    instrument = instrument,
                    note = "Bar received before trading date locked from timetable - this should not happen in normal operation"
                }));
            return; // Ignore bar - trading date should be locked from timetable
        }

        // Validate bar date matches locked trading date
        if (_activeTradingDate.Value != barChicagoDate)
        {
            // Trading date mismatch - log warning and ignore bar
            // Trading date is immutable once locked, so bars from different dates are ignored
            var tradingDateStr = _activeTradingDate.Value.ToString("yyyy-MM-dd");
            var barTradingDateStr = barChicagoDate.ToString("yyyy-MM-dd");
            
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "BAR_DATE_MISMATCH", state: "ENGINE",
                new
                {
                    locked_trading_date = tradingDateStr,
                    bar_trading_date = barTradingDateStr,
                    bar_timestamp_utc = barUtc.ToString("o"),
                    bar_timestamp_chicago = barChicagoTime.ToString("o"),
                    instrument = instrument,
                    note = "Bar ignored - trading date is locked from timetable and immutable"
                }));
            return; // Ignore bar from different trading date
        }

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

        // Pass bar data to streams of matching instrument
        foreach (var s in _streams.Values)
        {
            if (s.IsSameInstrument(instrument))
                s.OnBar(barUtc, open, high, low, close, utcNow);
        }
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
    
    /// <summary>
    /// Load pre-hydration bars for SIM mode from NinjaTrader BarsRequest API.
    /// This method allows the NinjaTrader strategy to request historical bars and feed them to streams.
    /// Streams must exist before calling this method (created in Start()).
    /// Filters out bars that are in the future relative to current time to avoid duplicates with live bars.
    /// </summary>
    public void LoadPreHydrationBars(string instrument, List<Bar> bars, DateTimeOffset utcNow)
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
        const double MIN_BAR_AGE_MINUTES = 1.0; // Bar period (1 minute bars)
        
        foreach (var bar in bars)
        {
            // Filter 1: Reject future bars
            if (bar.TimestampUtc > utcNow)
            {
                barsFilteredFuture++;
                continue;
            }
            
            // Filter 2: Reject partial/in-progress bars (must be at least 1 minute old)
            var barAgeMinutes = (utcNow - bar.TimestampUtc).TotalMinutes;
            if (barAgeMinutes < MIN_BAR_AGE_MINUTES)
            {
                barsFilteredPartial++;
                continue;
            }
            
            // Bar passed all filters - add to filtered list
            filteredBars.Add(bar);
        }
        
        var totalFiltered = barsFilteredFuture + barsFilteredPartial;

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
                    min_bar_age_minutes = MIN_BAR_AGE_MINUTES,
                    note = "Filtered out future bars and partial/in-progress bars. Only fully closed bars accepted."
                }));
        }

        if (filteredBars.Count == 0)
        {
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
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE", state: "ENGINE",
                        new
                        {
                            instrument = instrument,
                            stream_id = stream.Stream,
                            stream_state = stream.State.ToString(),
                            bar_count = filteredBars.Count,
                            note = $"Stream is in {stream.State} state - bars will not be buffered. " +
                                   "This may indicate a timing issue or stream already progressed past pre-hydration."
                        }));
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
                    first_bar_utc = filteredBars[0].TimestampUtc.ToString("o"),
                    last_bar_utc = filteredBars[filteredBars.Count - 1].TimestampUtc.ToString("o"),
                    source = "NinjaTrader_BarsRequest",
                    note = "Only fully closed bars loaded (filtered future and partial bars)"
                }));
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

        // Log stream creation
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: _activeTradingDate.Value.ToString("yyyy-MM-dd"), eventType: "STREAMS_CREATED", state: "ENGINE",
            new
            {
                stream_count = _streams.Count,
                trading_date = _activeTradingDate.Value.ToString("yyyy-MM-dd"),
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

        // Check startup timing now that streams exist
        CheckStartupTiming(utcNow);
    }

    private void ReloadTimetableIfChanged(DateTimeOffset utcNow, bool force)
    {
        if (_spec is null || _time is null) return;

        var poll = _timetablePoller.Poll(_timetablePath, utcNow);
        if (poll.Error is not null)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_INVALID", state: "ENGINE",
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
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = "PARSE_ERROR", error = ex.Message }));
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
                new { reason = "TIMEZONE_MISMATCH", timezone = timetable.timezone }));
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
                
                sm.ApplyDirectiveUpdate(directive.slot_time, _activeTradingDate.Value, utcNow);
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

    /// <summary>
    /// Public method to log engine events from external callers (e.g., RobotSimStrategy).
    /// </summary>
    public void LogEngineEvent(DateTimeOffset utcNow, string eventType, object? data = null)
    {
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: eventType, state: "ENGINE", data));
    }
    
    /// <summary>
    /// Get time range covering all enabled streams for an instrument (for BarsRequest).
    /// Returns the earliest range_start and latest slot_time across all enabled streams for the instrument.
    /// </summary>
    public (string earliestRangeStart, string latestSlotTime)? GetBarsRequestTimeRange(string instrument)
    {
        // Diagnostic: Log why we're returning null
        if (_spec is null)
        {
            LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: TradingDateString, 
                eventType: "BARSREQUEST_RANGE_NULL", state: "ENGINE",
                new { instrument, reason = "spec_is_null" }));
            return null;
        }
        
        if (_time is null)
        {
            LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: TradingDateString, 
                eventType: "BARSREQUEST_RANGE_NULL", state: "ENGINE",
                new { instrument, reason = "time_service_is_null" }));
            return null;
        }
        
        if (!_activeTradingDate.HasValue)
        {
            LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: TradingDateString, 
                eventType: "BARSREQUEST_RANGE_NULL", state: "ENGINE",
                new { instrument, reason = "trading_date_not_locked", total_streams = _streams.Count }));
            return null;
        }
        
        var instrumentUpper = instrument.ToUpperInvariant();
        var enabledStreams = _streams.Values
            .Where(s => !s.Committed && s.Instrument.Equals(instrumentUpper, StringComparison.OrdinalIgnoreCase))
            .ToList();
        
        if (enabledStreams.Count == 0)
        {
            var allInstruments = _streams.Values.Where(s => !s.Committed).Select(s => s.Instrument).Distinct().ToList();
            LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: TradingDateString, 
                eventType: "BARSREQUEST_RANGE_NULL", state: "ENGINE",
                new { 
                    instrument, 
                    reason = "no_enabled_streams_for_instrument",
                    total_streams = _streams.Count,
                    enabled_instruments = allInstruments
                }));
            return null;
        }
        
        // Find earliest range_start across all sessions used by enabled streams
        var sessionsUsed = enabledStreams.Select(s => s.Session).Distinct().ToList();
        string? earliestRangeStart = null;
        
        foreach (var session in sessionsUsed)
        {
            if (_spec.sessions.TryGetValue(session, out var sessionInfo))
            {
                var rangeStart = sessionInfo.range_start_time;
                if (!string.IsNullOrWhiteSpace(rangeStart))
                {
                    if (earliestRangeStart == null || string.Compare(rangeStart, earliestRangeStart, StringComparison.Ordinal) < 0)
                    {
                        earliestRangeStart = rangeStart;
                    }
                }
            }
        }
        
        if (string.IsNullOrWhiteSpace(earliestRangeStart))
        {
            LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: TradingDateString, 
                eventType: "BARSREQUEST_RANGE_NULL", state: "ENGINE",
                new { 
                    instrument, 
                    reason = "no_valid_range_start_time",
                    enabled_stream_count = enabledStreams.Count,
                    sessions_used = enabledStreams.Select(s => s.Session).Distinct().ToList()
                }));
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
            LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: TradingDateString, 
                eventType: "BARSREQUEST_RANGE_NULL", state: "ENGINE",
                new { 
                    instrument, 
                    reason = "no_valid_slot_time",
                    enabled_stream_count = enabledStreams.Count,
                    streams_with_slot_times = enabledStreams.Where(s => !string.IsNullOrWhiteSpace(s.SlotTimeChicago)).Select(s => new { stream = s.Stream, slot_time = s.SlotTimeChicago }).ToList()
                }));
            return null;
        }
        
        return (earliestRangeStart, latestSlotTime);
    }
}
