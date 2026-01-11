using System;
using System.Collections.Generic;
using System.Linq;
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
    
    public HealthMonitor(string projectRoot, HealthMonitorConfig config, RobotLogger log)
    {
        _config = config;
        _log = log;
        
        if (config.PushoverEnabled && !string.IsNullOrWhiteSpace(config.PushoverUserKey) && !string.IsNullOrWhiteSpace(config.PushoverAppToken))
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
            var windowEnd = slotTimeChicagoTime.AddSeconds(_config.GracePeriodSeconds);
            
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
        if (!_config.Enabled)
            return;
        
        // Rate limit evaluation
        var timeSinceLastEvaluate = (utcNow - _lastEvaluateUtc).TotalSeconds;
        if (timeSinceLastEvaluate < EVALUATE_RATE_LIMIT_SECONDS)
            return;
        
        _lastEvaluateUtc = utcNow;
        
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
        if (_lastEngineTickUtc == DateTimeOffset.MinValue)
            return; // Not initialized yet
        
        var elapsed = (utcNow - _lastEngineTickUtc).TotalSeconds;
        
        if (elapsed > _config.RobotStallSeconds)
        {
            if (!_robotStallActive)
            {
                // First detection: mark active and notify
                _robotStallActive = true;
                
                var title = "Robot Stall Detected";
                var message = $"Robot tick heartbeat stopped. Last tick: {_lastEngineTickUtc:o} UTC ({elapsed:F0}s ago). Threshold: {_config.RobotStallSeconds}s";
                
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "ROBOT_STALL_DETECTED", state: "ENGINE",
                    new
                    {
                        last_tick_utc = _lastEngineTickUtc.ToString("o"),
                        elapsed_seconds = elapsed,
                        threshold_seconds = _config.RobotStallSeconds
                    }));
                
                SendNotification("ROBOT_STALL", title, message, priority: 2); // Emergency priority
            }
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
                        last_tick_utc = _lastEngineTickUtc.ToString("o"),
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
            
            if (elapsed > _config.DataStallSeconds)
            {
                if (!_dataStallActive.TryGetValue(instrument, out var isActive) || !isActive)
                {
                    // First detection: mark active and notify
                    _dataStallActive[instrument] = true;
                    
                    var title = $"Data Stall: {instrument}";
                    var message = $"No bars received for {instrument}. Last bar: {lastBarUtc:o} UTC ({elapsed:F0}s ago). Threshold: {_config.DataStallSeconds}s";
                    
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "DATA_STALL_DETECTED", state: "ENGINE",
                        new
                        {
                            instrument = instrument,
                            last_bar_utc = lastBarUtc.ToString("o"),
                            elapsed_seconds = elapsed,
                            threshold_seconds = _config.DataStallSeconds
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
            if (timeSinceLastNotify < _config.MinNotifyIntervalSeconds)
                return; // Rate limited
        }
        
        _lastNotifyUtcByKey[key] = DateTimeOffset.UtcNow;
        
        // Enqueue notification (non-blocking)
        _notificationService.EnqueueNotification(key, title, message, priority);
        
        // Log notification enqueued (logging is already non-blocking via RobotLogger)
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "PUSHOVER_NOTIFY_SENT", state: "ENGINE",
            new
            {
                notification_key = key,
                title = title,
                priority = priority,
                pushover_configured = true,
                note = "Notification enqueued (actual send handled by background worker)"
            }));
    }
    
    /// <summary>
    /// Start the notification service background worker.
    /// </summary>
    public void Start()
    {
        _notificationService?.Start();
        
        // Log health monitor started
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: _tradingDate?.ToString("yyyy-MM-dd") ?? "", eventType: "HEALTH_MONITOR_STARTED", state: "ENGINE",
            new
            {
                enabled = _config.Enabled,
                data_stall_seconds = _config.DataStallSeconds,
                robot_stall_seconds = _config.RobotStallSeconds,
                grace_period_seconds = _config.GracePeriodSeconds,
                min_notify_interval_seconds = _config.MinNotifyIntervalSeconds,
                pushover_configured = _config.PushoverEnabled && !string.IsNullOrWhiteSpace(_config.PushoverUserKey) && !string.IsNullOrWhiteSpace(_config.PushoverAppToken),
                pushover_priority = _config.PushoverPriority
            }));
    }
    
    /// <summary>
    /// Stop the notification service.
    /// </summary>
    public void Stop()
    {
        _notificationService?.Stop();
    }
}
