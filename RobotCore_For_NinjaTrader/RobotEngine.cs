using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Execution;

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
    private IBarProvider? _barProvider; // Optional: for historical bar hydration (SIM/DRYRUN modes)
    private HealthMonitor? _healthMonitor; // Optional: health monitoring and alerts

    // ENGINE-level bar ingress diagnostic (rate-limiting per instrument)
    private readonly Dictionary<string, DateTimeOffset> _lastBarHeartbeatPerInstrument = new();
    private const int BAR_HEARTBEAT_RATE_LIMIT_MINUTES = 1; // Log once per instrument per minute
    
    // ENGINE-level tick heartbeat diagnostic (rate-limited)
    private DateTimeOffset _lastTickHeartbeat = DateTimeOffset.MinValue;
    private const int TICK_HEARTBEAT_RATE_LIMIT_MINUTES = 1; // Log once per minute

    /// <summary>
    /// Set the bar provider for historical bar hydration (used when starting late).
    /// </summary>
    public void SetBarProvider(IBarProvider? barProvider)
    {
        _barProvider = barProvider;
    }

    public RobotEngine(string projectRoot, TimeSpan timetablePollInterval, ExecutionMode executionMode = ExecutionMode.DRYRUN, string? customLogDir = null, string? customTimetablePath = null, string? instrument = null, bool useAsyncLogging = true)
    {
        _root = projectRoot;
        _executionMode = executionMode;
        
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
                if (healthMonitorConfig != null && healthMonitorConfig.Enabled)
                {
                    _healthMonitor = new HealthMonitor(projectRoot, healthMonitorConfig, _log);
                }
            }
        }
        catch
        {
            // Fail-closed: if config load fails, monitoring disabled (no alerts)
            // Don't log error to avoid spam - monitoring is optional
        }
    }

    public void Start()
    {
        var utcNow = DateTimeOffset.UtcNow;
        
        // Start async logging service if enabled (Fix B)
        _loggingService?.Start();
        
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENGINE_START", state: "ENGINE"));

        try
        {
            _spec = ParitySpec.LoadFromFile(_specPath);
            // Debug log: confirm spec_name was loaded
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_NAME_LOADED", state: "ENGINE",
                new { spec_name = _spec.spec_name }));
            _time = new TimeService(_spec.timezone);
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_LOADED", state: "ENGINE",
                new { spec_name = _spec.spec_name, spec_revision = _spec.spec_revision, timezone = _spec.timezone }));
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "SPEC_INVALID", state: "ENGINE", new { error = ex.Message }));
            throw;
        }

        // Initialize execution components now that spec is loaded
        _riskGate = new RiskGate(_spec, _time, _log, _killSwitch);
        _executionAdapter = ExecutionAdapterFactory.Create(_executionMode, _root, _log, _executionJournal);

        // Log execution mode and adapter
        var adapterType = _executionAdapter.GetType().Name;
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_MODE_SET", state: "ENGINE",
            new { mode = _executionMode.ToString(), adapter = adapterType }));

        // Load timetable immediately (fail closed if invalid)
        // Health monitor update happens automatically in ReloadTimetableIfChanged() → ApplyTimetable()
        ReloadTimetableIfChanged(utcNow, force: true);
        
        // Start health monitor if enabled
        _healthMonitor?.Start();
    }

    public void Stop()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var stopTradingDateStr = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: stopTradingDateStr, eventType: "ENGINE_STOP", state: "ENGINE"));
        
        // Write execution summary if not DRYRUN
        if (_executionMode != ExecutionMode.DRYRUN)
        {
            var summary = _executionSummary.GetSnapshot();
            var summaryDir = Path.Combine(_root, "data", "execution_summaries");
            Directory.CreateDirectory(summaryDir);
            var summaryPath = Path.Combine(summaryDir, $"summary_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
            var json = JsonUtil.Serialize(summary);
            File.WriteAllText(summaryPath, json);
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: stopTradingDateStr, eventType: "EXECUTION_SUMMARY_WRITTEN", state: "ENGINE",
                new { summary_path = summaryPath }));
        }
        
        // Stop health monitor
        _healthMonitor?.Stop();
        
        // Stop health monitor
        _healthMonitor?.Stop();
        
        // Release reference to async logging service (Fix B) - singleton will dispose when all references released
        _loggingService?.Release();
    }

    public void Tick(DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null) return;

        // Health monitor: record engine tick
        _healthMonitor?.OnEngineTick(utcNow);

        // ENGINE_TICK_HEARTBEAT: Diagnostic to prove Tick is advancing even with zero bars
        // Rate-limited to once per minute (DEBUG level, never affects execution)
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

        // Timetable reactivity
        if (_timetablePoller.ShouldPoll(utcNow))
        {
            ReloadTimetableIfChanged(utcNow, force: false);
        }

        foreach (var s in _streams.Values)
            s.Tick(utcNow);
        
        // Health monitor: evaluate staleness (rate-limited internally)
        _healthMonitor?.Evaluate(utcNow);
    }

    public void OnBar(DateTimeOffset barUtc, string instrument, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
    {
        if (_spec is null || _time is null) return;

        // Health monitor: record bar reception (early, before other processing)
        _healthMonitor?.OnBar(instrument, barUtc);

        // ENGINE_BAR_HEARTBEAT: Diagnostic to prove bar ingress from NinjaTrader
        // This fires regardless of stream state or existence - pure observability
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

        // PHASE 3: Derive trading_date from bar timestamp (bar-derived date is authoritative)
        // Parse once, store as DateOnly
        var barChicagoDate = _time.GetChicagoDateToday(barUtc);

        // Check if trading_date needs to roll over to a new day
        if (_activeTradingDate != barChicagoDate)
        {
            // Trading date rollover: update engine trading_date and notify all streams
            var previousTradingDate = _activeTradingDate;
            _activeTradingDate = barChicagoDate;

            // Log trading day rollover (convert to string only for logging)
            var barTradingDateStr = barChicagoDate.ToString("yyyy-MM-dd");
            var previousTradingDateStr = previousTradingDate?.ToString("yyyy-MM-dd") ?? "UNSET";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: barTradingDateStr, eventType: "TRADING_DAY_ROLLOVER", state: "ENGINE",
                new
                {
                    previous_trading_date = previousTradingDateStr,
                    new_trading_date = barTradingDateStr,
                    bar_timestamp_utc = barUtc.ToString("o"),
                    bar_timestamp_chicago = _time.ConvertUtcToChicago(barUtc).ToString("o")
                }));

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
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: engineTradingDateStr, eventType: "REPLAY_INVARIANT_VIOLATION", state: "ENGINE",
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
                s.OnBar(barUtc, high, low, close, utcNow);
        }
    }

    private void ReloadTimetableIfChanged(DateTimeOffset utcNow, bool force)
    {
        if (_spec is null || _time is null) return;

        var poll = _timetablePoller.Poll(_timetablePath, utcNow);
        if (poll.Error is not null)
        {
            var tradingDateStr = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = poll.Error }));
            StandDown();
            return;
        }

        if (!force && !poll.Changed) return;

        _lastTimetableHash = poll.Hash;
        var updateTradingDateStr = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: updateTradingDateStr, eventType: "TIMETABLE_UPDATED", state: "ENGINE",
            new { timetable_hash = _lastTimetableHash }));

        TimetableContract timetable;
        try
        {
            timetable = TimetableContract.LoadFromFile(_timetablePath);
        }
        catch (Exception ex)
        {
            var tradingDateStr = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = "PARSE_ERROR", error = ex.Message }));
            StandDown();
            return;
        }

        if (!TimeService.TryParseDateOnly(timetable.trading_date, out var tradingDate))
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "TIMETABLE_INVALID", state: "ENGINE",
                new { reason = "BAD_TRADING_DATE", trading_date = timetable.trading_date }));
            StandDown();
            return;
        }

        if (timetable.timezone != "America/Chicago")
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.trading_date, eventType: "TIMETABLE_INVALID", state: "ENGINE",
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
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.trading_date, eventType: "TIMETABLE_INVALID", state: "ENGINE",
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
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.trading_date, eventType: "TIMETABLE_INVALID", state: "ENGINE",
                    new { reason = "STALE_TRADING_DATE", trading_date = timetable.trading_date, chicago_today = chicagoToday.ToString("yyyy-MM-dd") }));
                StandDown();
                return;
            }
        }

        // PHASE 3: Store as DateOnly (already parsed above)
        _activeTradingDate = tradingDate;

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.trading_date, eventType: "TIMETABLE_LOADED", state: "ENGINE",
            new { streams = timetable.streams.Count, timetable_hash = _lastTimetableHash, timetable_path = _timetablePath }));
        
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: timetable.trading_date, eventType: "TIMETABLE_VALIDATED", state: "ENGINE",
            new { trading_date = timetable.trading_date, is_replay = isReplayTimetable }));

        ApplyTimetable(timetable, tradingDate, utcNow);
        
        // Update health monitor with new timetable (for monitoring window computation)
        if (_healthMonitor != null && _spec != null && _time != null)
        {
            _healthMonitor.UpdateTimetable(_spec, timetable, _time);
        }
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
        
        // Log timetable parsing stats
        int acceptedCount = 0;
        int skippedCount = 0;
        var skippedReasons = new Dictionary<string, int>();

        foreach (var directive in incoming)
        {
            var streamId = directive.stream;
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
                _log.Write(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId ?? "", instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "MISSING_FIELDS", stream_id = streamId, instrument = instrument, session = session, slot_time = slotTimeChicago }));
                continue;
            }

            if (!_spec.sessions.ContainsKey(session))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("UNKNOWN_SESSION", out var count2)) count2 = 0;
                skippedReasons["UNKNOWN_SESSION"] = count2 + 1;
                _log.Write(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
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
                _log.Write(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "INVALID_SLOT_TIME", stream_id = streamId, slot_time = slotTimeChicago, allowed_times = allowed }));
                continue;
            }

            if (!_spec.TryGetInstrument(instrument, out _))
            {
                skippedCount++;
                if (!skippedReasons.TryGetValue("UNKNOWN_INSTRUMENT", out var count4)) count4 = 0;
                skippedReasons["UNKNOWN_INSTRUMENT"] = count4 + 1;
                _log.Write(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_SKIPPED", "ENGINE", new { reason = "UNKNOWN_INSTRUMENT", stream_id = streamId, instrument = instrument }));
                continue;
            }

            seen.Add(streamId);
            acceptedCount++;

            if (_streams.TryGetValue(streamId, out var sm))
            {
                if (sm.Committed)
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                        "UPDATE_IGNORED_COMMITTED", "ENGINE", new { reason = "STREAM_COMMITTED" }));
                    continue;
                }

                // Apply updates only for uncommitted streams
                if (string.IsNullOrWhiteSpace(directive.slot_time))
                {
                    // Fail closed: skip update if slot_time is null/empty
                    continue;
                }
                
                // DIAGNOSTIC: Log trading date being used for stream update
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_UPDATE_DIAGNOSTIC", "ENGINE", new 
                    { 
                        trading_date_str = tradingDateStr,
                        trading_date_parsed = tradingDate.ToString("yyyy-MM-dd"),
                        timetable_trading_date = timetable.trading_date,
                        previous_slot_time_utc = sm.SlotTimeUtc.ToString("o"),
                        new_slot_time_utc = slotTimeUtc?.ToString("o"),
                        note = "Logging trading date used when updating existing stream"
                    }));
                
                sm.ApplyDirectiveUpdate(directive.slot_time, tradingDate, utcNow);
            }
            else
            {
                // PHASE 3: New stream - pass DateOnly directly (authoritative), convert to string only for logging/journal
                // Note: IBarProvider is null for live/SIM modes (bars come via OnBar). For DRYRUN mode with historical replay, 
                // IBarProvider would be passed here if available, but currently RobotEngine doesn't maintain one.
                
                // DIAGNOSTIC: Log trading date being used for stream creation
                LogEvent(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                    "STREAM_CREATION_DIAGNOSTIC", "ENGINE", new 
                    { 
                        trading_date_str = tradingDateStr,
                        trading_date_parsed = tradingDate.ToString("yyyy-MM-dd"),
                        timetable_trading_date = timetable.trading_date,
                        slot_time_chicago = slotTimeChicago,
                        slot_time_utc = slotTimeUtc?.ToString("o"),
                        note = "Logging trading date used when creating new stream"
                    }));
                
                // PHASE 3: Pass DateOnly to constructor (will be converted to string internally for journal)
                // Pass bar provider for historical hydration support (SIM/DRYRUN modes)
                var newSm = new StreamStateMachine(_time, _spec, _log, _journals, tradingDate, _lastTimetableHash, directive, _executionMode, _executionAdapter, _riskGate, _executionJournal, barProvider: _barProvider);
                _streams[streamId] = newSm;

                if (newSm.Committed)
                {
                    _log.Write(RobotEvents.Base(_time, utcNow, tradingDateStr, streamId, instrument, session, slotTimeChicago, slotTimeUtc,
                        "STREAM_SKIPPED", "ENGINE", new { reason = "ALREADY_COMMITTED_JOURNAL" }));
                    continue;
                }

                newSm.Arm(utcNow);
            }
        }

        // Log parsing summary
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: tradingDate.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_PARSING_COMPLETE", state: "ENGINE",
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
}

