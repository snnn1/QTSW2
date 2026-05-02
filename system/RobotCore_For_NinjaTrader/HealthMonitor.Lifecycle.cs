using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Core.Notifications;

namespace QTSW2.Robot.Core;

public sealed partial class HealthMonitor
{
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
