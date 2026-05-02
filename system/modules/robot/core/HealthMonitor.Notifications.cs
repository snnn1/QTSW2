using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Core.Notifications;

namespace QTSW2.Robot.Core;

public sealed partial class HealthMonitor
{
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
}
