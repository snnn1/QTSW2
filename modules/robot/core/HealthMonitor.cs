using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Core.Notifications;

namespace QTSW2.Robot.Core;

/// <summary>
/// Health monitor: detects data stalls and robot stalls, sends Pushover notifications.
/// Monitoring windows are computed from enabled timetable streams.
/// </summary>
public sealed class HealthMonitor
{
    private readonly HealthMonitorConfig _config;
    private readonly RobotLogger _log;
    private readonly NotificationService? _notificationService;
    private TimeService? _time;
    
    // Tracking state
    private DateTimeOffset _lastEngineTickUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _monitorStartUtc = DateTimeOffset.MinValue; // Track when monitoring started
    private readonly Dictionary<string, DateTimeOffset> _lastBarUtcByInstrument = new(StringComparer.OrdinalIgnoreCase);
    
    // Incident state
    private readonly Dictionary<string, bool> _dataStallActive = new(StringComparer.OrdinalIgnoreCase);
    private bool _robotStallActive = false;
    
    // Rate limiting
    private readonly Dictionary<string, DateTimeOffset> _lastNotifyUtcByKey = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastEvaluateUtc = DateTimeOffset.MinValue;
    private const int EVALUATE_RATE_LIMIT_SECONDS = 1; // Max 1 evaluation per second
    
    // Monitoring windows (computed from timetable + spec)
    private List<(DateTimeOffset Start, DateTimeOffset End)> _monitoringWindows = new();
    private DateOnly? _tradingDate;
    
    // Background evaluation thread
    private CancellationTokenSource? _evaluationCancellationTokenSource;
    private Task? _evaluationTask;
    private const int EVALUATION_INTERVAL_SECONDS = 5; // Evaluate every 5 seconds
    
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
    /// Update monitoring windows from timetable and spec.
    /// Called when timetable is loaded or updated.
    /// </summary>
    public void UpdateTimetable(ParitySpec spec, TimetableContract timetable, TimeService time)
    {
        _time = time;
        
        if (!TimeService.TryParseDateOnly(timetable.trading_date, out var tradingDate))
            return;
        
        _tradingDate = tradingDate;
        _monitoringWindows = ComputeMonitoringWindows(spec, timetable, time, tradingDate);
        
        // Log computed windows once at timetable update
        var windowsList = _monitoringWindows.Select(w => new
        {
            start_chicago = w.Start.ToString("o"),
            end_chicago = w.End.ToString("o"),
            start_utc = w.Start.ToUniversalTime().ToString("o"),
            end_utc = w.End.ToUniversalTime().ToString("o")
        }).ToList();
        
        _log.Write(RobotEvents.EngineBase(
            DateTimeOffset.UtcNow,
            tradingDate: timetable.trading_date,
            eventType: "HEALTH_MONITOR_WINDOWS_COMPUTED",
            state: "ENGINE",
            extra: new
            {
                trading_date = timetable.trading_date,
                enabled_stream_count = timetable.streams.Count(s => s.enabled),
                monitoring_windows_count = _monitoringWindows.Count,
                monitoring_windows = windowsList,
                grace_period_seconds = _config.GracePeriodSeconds
            }));
    }
    
    /// <summary>
    /// Compute monitoring windows from enabled streams.
    /// Window = [RangeStartChicagoTime, SlotTimeChicagoTime + grace_period] for each enabled stream.
    /// Windows are merged if overlapping.
    /// </summary>
    private List<(DateTimeOffset Start, DateTimeOffset End)> ComputeMonitoringWindows(
        ParitySpec spec,
        TimetableContract timetable,
        TimeService time,
        DateOnly tradingDate)
    {
        var windows = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        
        foreach (var stream in timetable.streams.Where(s => s.enabled))
        {
            // Get session (S1 or S2)
            if (!spec.sessions.TryGetValue(stream.session, out var session))
                continue;
            
            // Get range start time from ParitySpec
            var rangeStartChicago = session.range_start_time;
            var slotTimeChicago = stream.slot_time;
            
            // Construct Chicago times
            var rangeStartChicagoTime = time.ConstructChicagoTime(tradingDate, rangeStartChicago);
            var slotTimeChicagoTime = time.ConstructChicagoTime(tradingDate, slotTimeChicago);
            
            // Add grace period to end time
            var windowEnd = slotTimeChicagoTime.AddSeconds(_config.grace_period_seconds);
            
            windows.Add((rangeStartChicagoTime, windowEnd));
        }
        
        // Merge overlapping windows
        if (windows.Count == 0)
            return windows;
        
        // Sort by start time
        windows.Sort((a, b) => a.Start.CompareTo(b.Start));
        
        // Merge overlapping windows
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var current = windows[0];
        
        for (int i = 1; i < windows.Count; i++)
        {
            if (windows[i].Start <= current.End)
            {
                // Overlapping: extend current window
                current = (current.Start, current.End > windows[i].End ? current.End : windows[i].End);
            }
            else
            {
                // Non-overlapping: save current and start new
                merged.Add(current);
                current = windows[i];
            }
        }
        merged.Add(current);
        
        return merged;
    }
    
    /// <summary>
    /// Check if monitoring is currently active (current time falls within any monitoring window).
    /// </summary>
    private bool IsMonitoringActiveNow(DateTimeOffset utcNow)
    {
        if (_monitoringWindows.Count == 0)
            return false;
        
        if (_time == null)
            return false;
        
        var nowChicago = _time.ConvertUtcToChicago(utcNow);
        
        foreach (var window in _monitoringWindows)
        {
            if (nowChicago >= window.Start && nowChicago < window.End)
                return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Record engine tick heartbeat.
    /// </summary>
    public void OnEngineTick(DateTimeOffset utcNow)
    {
        _lastEngineTickUtc = utcNow;
    }
    
    /// <summary>
    /// Record bar reception for an instrument.
    /// </summary>
    public void OnBar(string instrument, DateTimeOffset barUtc)
    {
        _lastBarUtcByInstrument[instrument] = barUtc;
    }
    
    /// <summary>
    /// Evaluate staleness and trigger alerts if needed.
    /// Rate-limited internally to max 1/sec.
    /// </summary>
    public void Evaluate(DateTimeOffset utcNow)
    {
        // Log entry to Evaluate()
        try
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                new { diagnostic = "evaluate_entry", enabled = _config.enabled, last_evaluate_utc = _lastEvaluateUtc.ToString("o") }));
        }
        catch { /* Ignore logging errors */ }
        
        if (!_config.enabled)
        {
            // Log that evaluation is skipped due to disabled config
            try
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                    new { diagnostic = "evaluate_skipped", reason = "config_disabled" }));
            }
            catch { /* Ignore logging errors */ }
            return;
        }
        
        // Rate limit evaluation
        var timeSinceLastEvaluate = (utcNow - _lastEvaluateUtc).TotalSeconds;
        if (timeSinceLastEvaluate < EVALUATE_RATE_LIMIT_SECONDS)
        {
            // Log rate limiting
            try
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                    new { diagnostic = "evaluate_rate_limited", time_since_last = timeSinceLastEvaluate, rate_limit = EVALUATE_RATE_LIMIT_SECONDS }));
            }
            catch { /* Ignore logging errors */ }
            return;
        }
        
        _lastEvaluateUtc = utcNow;
        
        // Log that Evaluate() is proceeding
        try
        {
            var elapsedSinceLastTick = (utcNow - _lastEngineTickUtc).TotalSeconds;
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                new
                {
                    diagnostic = "evaluate_proceeding",
                    elapsed_since_last_tick = elapsedSinceLastTick,
                    threshold = _config.robot_stall_seconds,
                    last_engine_tick_utc = _lastEngineTickUtc == DateTimeOffset.MinValue ? "NEVER_CALLED" : _lastEngineTickUtc.ToString("o"),
                    monitor_start_utc = _monitorStartUtc.ToString("o"),
                    time_since_monitor_start = _monitorStartUtc != DateTimeOffset.MinValue ? (utcNow - _monitorStartUtc).TotalSeconds : 0
                }));
        }
        catch { /* Ignore logging errors */ }
        
        // Check robot stall
        EvaluateRobotStall(utcNow);
        
        // Check data stalls (only during active monitoring windows)
        if (IsMonitoringActiveNow(utcNow))
        {
            EvaluateDataStalls(utcNow);
        }
    }
    
    private void EvaluateRobotStall(DateTimeOffset utcNow)
    {
        // Log entry to EvaluateRobotStall()
        try
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                new
                {
                    diagnostic = "evaluate_robot_stall_entry",
                    last_engine_tick_utc = _lastEngineTickUtc.ToString("o"),
                    is_min_value = _lastEngineTickUtc == DateTimeOffset.MinValue,
                    monitor_start_utc = _monitorStartUtc.ToString("o"),
                    time_since_monitor_start = _monitorStartUtc != DateTimeOffset.MinValue ? (utcNow - _monitorStartUtc).TotalSeconds : 0
                }));
        }
        catch { /* Ignore logging errors */ }
        
        // Determine baseline: use _lastEngineTickUtc if it's recent, otherwise use monitor_start
        // This handles the case where Tick() stops being called (strategy disabled)
        DateTimeOffset baselineUtc;
        bool usingMonitorStart = false;
        
        if (_lastEngineTickUtc == DateTimeOffset.MinValue)
        {
            // OnEngineTick() was never called - use monitor start time
            if (_monitorStartUtc == DateTimeOffset.MinValue)
            {
                // Shouldn't happen, but handle it
                try
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                        new { diagnostic = "robot_stall_check_skipped", reason = "monitor_not_started" }));
                }
                catch { /* Ignore logging errors */ }
                return;
            }
            baselineUtc = _monitorStartUtc;
            usingMonitorStart = true;
            
            // Log that we're using monitor start time
            try
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                    new
                    {
                        diagnostic = "using_monitor_start_as_baseline",
                        reason = "on_engine_tick_never_called",
                        monitor_start_utc = _monitorStartUtc.ToString("o"),
                        time_since_start = (utcNow - _monitorStartUtc).TotalSeconds
                    }));
            }
            catch { /* Ignore logging errors */ }
        }
        else
        {
            // OnEngineTick() was called - check if it's recent enough
            // If _lastEngineTickUtc is very recent (< 1 second), it means Tick() was just called
            // In that case, we should use monitor_start as baseline to detect if Tick() stops
            var timeSinceLastTick = (utcNow - _lastEngineTickUtc).TotalSeconds;
            
            // If last tick was very recent (< 1s), Tick() is still being called
            // Use monitor_start as baseline to detect when Tick() stops
            // If last tick was old (> 1s), Tick() has stopped, use _lastEngineTickUtc as baseline
            if (timeSinceLastTick < 1.0)
            {
                // Tick() was just called - use monitor_start to detect when it stops
                if (_monitorStartUtc != DateTimeOffset.MinValue)
                {
                    baselineUtc = _monitorStartUtc;
                    usingMonitorStart = true;
                    
                    // Log that we're using monitor start because Tick() is still active
                    try
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                            new
                            {
                                diagnostic = "using_monitor_start_as_baseline",
                                reason = "tick_still_active_using_monitor_start",
                                monitor_start_utc = _monitorStartUtc.ToString("o"),
                                last_tick_utc = _lastEngineTickUtc.ToString("o"),
                                time_since_last_tick = timeSinceLastTick,
                                time_since_start = (utcNow - _monitorStartUtc).TotalSeconds
                            }));
                    }
                    catch { /* Ignore logging errors */ }
                }
                else
                {
                    // Fallback to _lastEngineTickUtc if monitor_start not available
                    baselineUtc = _lastEngineTickUtc;
                }
            }
            else
            {
                // Tick() has stopped - use _lastEngineTickUtc as baseline
                baselineUtc = _lastEngineTickUtc;
            }
        }
        
        var elapsed = (utcNow - baselineUtc).TotalSeconds;
        
        // Log elapsed time calculation
        try
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                new
                {
                    diagnostic = "elapsed_calculated",
                    elapsed_seconds = elapsed,
                    threshold_seconds = _config.robot_stall_seconds,
                    exceeds_threshold = elapsed > _config.robot_stall_seconds,
                    robot_stall_active = _robotStallActive,
                    baseline_utc = baselineUtc.ToString("o"),
                    using_monitor_start = usingMonitorStart
                }));
        }
        catch { /* Ignore logging errors */ }
        
        // Log diagnostic every time we check (for debugging) - but only when elapsed > threshold to avoid spam
        if (elapsed > _config.robot_stall_seconds)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                new
                {
                    diagnostic = "robot_stall_check",
                    baseline_utc = baselineUtc.ToString("o"),
                    last_tick_utc = _lastEngineTickUtc == DateTimeOffset.MinValue ? "NEVER_CALLED" : _lastEngineTickUtc.ToString("o"),
                    elapsed_seconds = elapsed,
                    threshold_seconds = _config.robot_stall_seconds,
                    robot_stall_active = _robotStallActive,
                    using_monitor_start = usingMonitorStart
                }));
        }
        
        if (elapsed > _config.robot_stall_seconds)
        {
            // Log that threshold exceeded
            try
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                    new
                    {
                        diagnostic = "threshold_exceeded",
                        elapsed_seconds = elapsed,
                        threshold_seconds = _config.robot_stall_seconds,
                        robot_stall_active = _robotStallActive,
                        will_notify = !_robotStallActive
                    }));
            }
            catch { /* Ignore logging errors */ }
            
            if (!_robotStallActive)
            {
                // Log before marking as active
                try
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                        new { diagnostic = "marking_stall_active" }));
                }
                catch { /* Ignore logging errors */ }
                
                // First detection: mark active and notify
                _robotStallActive = true;
                
                var title = "Robot Stall Detected";
                var lastTickInfo = _lastEngineTickUtc == DateTimeOffset.MinValue 
                    ? "Never called (strategy may be disabled)" 
                    : $"{_lastEngineTickUtc:o} UTC";
                var message = $"Robot tick heartbeat stopped. Last tick: {lastTickInfo} ({elapsed:F0}s ago). Threshold: {_config.robot_stall_seconds}s";
                
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "ROBOT_STALL_DETECTED", state: "ENGINE",
                    new
                    {
                        baseline_utc = baselineUtc.ToString("o"),
                        last_tick_utc = _lastEngineTickUtc == DateTimeOffset.MinValue ? "NEVER_CALLED" : _lastEngineTickUtc.ToString("o"),
                        elapsed_seconds = elapsed,
                        threshold_seconds = _config.robot_stall_seconds,
                        using_monitor_start = usingMonitorStart
                    }));
                
                // Log before sending notification
                try
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                        new { diagnostic = "calling_send_notification", notification_key = "ROBOT_STALL" }));
                }
                catch { /* Ignore logging errors */ }
                
                SendNotification("ROBOT_STALL", title, message, priority: 2); // Emergency priority
            }
            else
            {
                // Log that stall is already active
                try
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                        new { diagnostic = "stall_already_active", skipping_notification = true }));
                }
                catch { /* Ignore logging errors */ }
            }
        }
        else
        {
            // Log that threshold not exceeded
            try
            {
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_DIAGNOSTIC", state: "ENGINE",
                    new
                    {
                        diagnostic = "threshold_not_exceeded",
                        elapsed_seconds = elapsed,
                        threshold_seconds = _config.robot_stall_seconds
                    }));
            }
            catch { /* Ignore logging errors */ }
        }
        else
        {
            if (_robotStallActive)
            {
                // Recovery: clear flag and log
                _robotStallActive = false;
                
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "ROBOT_STALL_RECOVERED", state: "ENGINE",
                    new
                    {
                        baseline_utc = baselineUtc.ToString("o"),
                        last_tick_utc = _lastEngineTickUtc == DateTimeOffset.MinValue ? "NEVER_CALLED" : _lastEngineTickUtc.ToString("o"),
                        elapsed_seconds = elapsed
                    }));
            }
        }
    }
    
    private void EvaluateDataStalls(DateTimeOffset utcNow)
    {
        // Check each instrument that has received bars
        var instrumentsToCheck = new List<string>(_lastBarUtcByInstrument.Keys);
        
        foreach (var instrument in instrumentsToCheck)
        {
            var lastBarUtc = _lastBarUtcByInstrument[instrument];
            var elapsed = (utcNow - lastBarUtc).TotalSeconds;
            
            if (elapsed > _config.data_stall_seconds)
            {
                if (!_dataStallActive.TryGetValue(instrument, out var isActive) || !isActive)
                {
                    // First detection: mark active and notify
                    _dataStallActive[instrument] = true;
                    
                    var title = $"Data Stall: {instrument}";
                    var message = $"No bars received for {instrument}. Last bar: {lastBarUtc:o} UTC ({elapsed:F0}s ago). Threshold: {_config.data_stall_seconds}s";
                    
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "DATA_STALL_DETECTED", state: "ENGINE",
                        new
                        {
                            instrument = instrument,
                            last_bar_utc = lastBarUtc.ToString("o"),
                            elapsed_seconds = elapsed,
                            threshold_seconds = _config.data_stall_seconds
                        }));
                    
                    SendNotification($"DATA_STALL:{instrument}", title, message, priority: 1); // High priority
                }
            }
            else
            {
                if (_dataStallActive.TryGetValue(instrument, out var isActive) && isActive)
                {
                    // Recovery: clear flag and log
                    _dataStallActive[instrument] = false;
                    
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "DATA_STALL_RECOVERED", state: "ENGINE",
                        new
                        {
                            instrument = instrument,
                            last_bar_utc = lastBarUtc.ToString("o"),
                            elapsed_seconds = elapsed
                        }));
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
        
        // Log notification enqueued (logging is already non-blocking via RobotLogger)
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "PUSHOVER_NOTIFY_ENQUEUED", state: "ENGINE",
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
        
        // Track when monitoring started (for detecting stalls if Tick() never called)
        var now = DateTimeOffset.UtcNow;
        if (_monitorStartUtc == DateTimeOffset.MinValue)
        {
            _monitorStartUtc = now;
        }
        
        // Start background evaluation thread (independent of Tick())
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
        _log.Write(RobotEvents.EngineBase(now, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_STARTED", state: "ENGINE",
            new
            {
                enabled = _config.enabled,
                data_stall_seconds = _config.data_stall_seconds,
                robot_stall_seconds = _config.robot_stall_seconds,
                grace_period_seconds = _config.grace_period_seconds,
                min_notify_interval_seconds = _config.min_notify_interval_seconds,
                pushover_configured = _config.pushover_enabled && !string.IsNullOrWhiteSpace(_config.pushover_user_key) && !string.IsNullOrWhiteSpace(_config.pushover_app_token),
                pushover_priority = _config.pushover_priority,
                evaluation_thread_started = true,
                monitor_start_utc = _monitorStartUtc.ToString("o"),
                last_engine_tick_utc = _lastEngineTickUtc == DateTimeOffset.MinValue ? "NEVER_CALLED" : _lastEngineTickUtc.ToString("o")
            }));
    }
    
    /// <summary>
    /// Background evaluation loop - runs independently of Tick().
    /// This allows detection of robot stalls even when strategy is disabled.
    /// </summary>
    private void EvaluationLoop(CancellationToken cancellationToken)
    {
        Thread.CurrentThread.IsBackground = true;
        Thread.CurrentThread.Name = "HealthMonitor-Evaluation";
        
        // Log thread start
        try
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_EVALUATION_THREAD_STARTED", state: "ENGINE",
                new { evaluation_interval_seconds = EVALUATION_INTERVAL_SECONDS }));
        }
        catch { /* Ignore logging errors */ }
        
        int iterationCount = 0;
        // Log that we're entering the loop
        try
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_EVALUATION_LOOP_ENTERED", state: "ENGINE",
                new { cancellation_requested = cancellationToken.IsCancellationRequested }));
        }
        catch { /* Ignore logging errors */ }
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                iterationCount++;
                var utcNow = DateTimeOffset.UtcNow;
                
                // Log first iteration immediately
                if (iterationCount == 1)
                {
                    try
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_EVALUATION_LOOP_FIRST_ITERATION", state: "ENGINE",
                            new { iteration = iterationCount, enabled = _config.enabled, last_engine_tick_utc = _lastEngineTickUtc.ToString("o") }));
                    }
                    catch { /* Ignore logging errors */ }
                }
                
                // Log every 6th iteration (every ~30 seconds) for debugging
                if (iterationCount % 6 == 0)
                {
                    try
                    {
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_EVALUATION_LOOP_ITERATION", state: "ENGINE",
                            new { iteration = iterationCount, last_engine_tick_utc = _lastEngineTickUtc.ToString("o"), enabled = _config.enabled }));
                    }
                    catch { /* Ignore logging errors */ }
                }
                
                Evaluate(utcNow); // Evaluate staleness and trigger alerts if needed
                
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
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_EVALUATION_ERROR", state: "ENGINE",
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
}
