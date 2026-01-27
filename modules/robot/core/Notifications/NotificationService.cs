using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace QTSW2.Robot.Core.Notifications;

/// <summary>
/// Background notification service for sending push notifications.
/// Singleton pattern: one service per project root.
/// Non-blocking: enqueues notifications, background worker sends them.
/// </summary>
public sealed class NotificationService : IDisposable
{
    // Singleton pattern: one service per project root
    private static readonly Dictionary<string, NotificationService> _instances = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _instancesLock = new();
    
    private readonly ConcurrentQueue<NotificationRequest> _queue = new();
    private CancellationTokenSource _cancellationTokenSource = new();
    private Task? _backgroundWorker;
    private Task? _watchdogTask;
    private bool _disposed = false;
    
    // Worker liveness tracking
    private DateTimeOffset _workerStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDequeuedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHeartbeatAt = DateTimeOffset.MinValue;
    private int _workerRestartCount = 0;
    private readonly object _livenessLock = new();
    
    // Configuration constants
    private const int MAX_QUEUE_SIZE = 1000;
    private const int WORKER_SLEEP_MS = 100;
    private const int HEARTBEAT_INTERVAL_SECONDS = 60;
    private const int STALL_DETECTION_SECONDS = 120;
    private const int EMERGENCY_NOTIFY_MIN_INTERVAL_SECONDS = 300; // 5 minutes between emergency notifications of same type
    
    // Emergency notification rate limiting: track last notification time per event type (cross-run persistence)
    // This prevents spam when the same critical event happens across multiple engine runs
    private readonly Dictionary<string, DateTimeOffset> _lastEmergencyNotifyUtcByEventType = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _rateLimitLock = new();
    
    private readonly string _projectRoot;
    private readonly HealthMonitorConfig _config;
    
    private NotificationService(string projectRoot, HealthMonitorConfig config)
    {
        _projectRoot = projectRoot;
        _config = config;
    }
    
    /// <summary>
    /// Get or create the singleton instance for the given project root.
    /// </summary>
    public static NotificationService GetOrCreate(string projectRoot, HealthMonitorConfig config)
    {
        lock (_instancesLock)
        {
            if (!_instances.TryGetValue(projectRoot, out var instance) || instance._disposed)
            {
                instance = new NotificationService(projectRoot, config);
                _instances[projectRoot] = instance;
            }
            
            return instance;
        }
    }
    
    /// <summary>
    /// Check if an emergency notification (priority 2) should be rate limited.
    /// Returns true if notification should be sent, false if rate limited.
    /// Thread-safe, uses singleton state for cross-run persistence.
    /// </summary>
    public bool ShouldSendEmergencyNotification(string eventType)
    {
        if (_disposed) return false;
        
        lock (_rateLimitLock)
        {
            var utcNow = DateTimeOffset.UtcNow;
            
            if (_lastEmergencyNotifyUtcByEventType.TryGetValue(eventType, out var lastNotify))
            {
                var timeSinceLastNotify = (utcNow - lastNotify).TotalSeconds;
                if (timeSinceLastNotify < EMERGENCY_NOTIFY_MIN_INTERVAL_SECONDS)
                {
                    return false; // Rate limited
                }
            }
            
            // Update timestamp immediately to prevent other calls from sending
            _lastEmergencyNotifyUtcByEventType[eventType] = utcNow;
            return true; // Not rate limited
        }
    }
    
    /// <summary>
    /// Enqueue a notification request. Non-blocking, never throws.
    /// </summary>
    public void EnqueueNotification(string key, string title, string message, int priority)
    {
        if (_disposed) return;
        if (!_config.pushover_enabled) return;
        
        // Backpressure: if queue exceeds max size, drop INFO-level notifications; never drop ERROR-level
        if (_queue.Count >= MAX_QUEUE_SIZE)
        {
            // Priority 0 = normal, 1 = high, 2 = emergency
            // Only drop if priority is 0 (normal/INFO level)
            if (priority == 0)
                return;
        }
        
        _queue.Enqueue(new NotificationRequest
        {
            Key = key,
            Title = title,
            Message = message,
            Priority = priority
        });
    }
    
    /// <summary>
    /// Start the background worker thread.
    /// </summary>
    public void Start()
    {
        lock (_livenessLock)
        {
            if (_backgroundWorker != null && !_backgroundWorker.IsCompleted) return;
            
            if (_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = new CancellationTokenSource();
            }
            
            _workerStartedAt = DateTimeOffset.UtcNow;
            _lastDequeuedAt = DateTimeOffset.MinValue;
            _lastHeartbeatAt = DateTimeOffset.UtcNow;
            
            _backgroundWorker = Task.Run(async () => await WorkerLoop(_cancellationTokenSource.Token));
            
            WriteNotificationLog("INFO", 
                $"Notification worker started: " +
                $"StartedAt={_workerStartedAt:o}, " +
                $"RestartCount={_workerRestartCount}");
            
            // Start watchdog timer to detect stalled workers (only if not already running)
            if (_watchdogTask == null || _watchdogTask.IsCompleted)
            {
                _watchdogTask = Task.Run(async () => await WatchdogLoop());
            }
        }
    }
    
    /// <summary>
    /// Watchdog loop to detect and restart stalled workers.
    /// </summary>
    private async Task WatchdogLoop()
    {
        while (!_disposed)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None); // Check every 30 seconds
                
                // Check if worker is stalled (read values outside lock)
                bool shouldRestart = false;
                int queueLength = 0;
                DateTimeOffset lastDequeuedAt;
                int restartCount;
                TaskStatus workerStatus = TaskStatus.Created;
                
                lock (_livenessLock)
                {
                    if (_disposed) break;
                    
                    var now = DateTimeOffset.UtcNow;
                    queueLength = _queue.Count;
                    lastDequeuedAt = _lastDequeuedAt;
                    restartCount = _workerRestartCount;
                    
                    // Worker is stalled if:
                    // - Worker task exists AND
                    // - It is not completed AND
                    // - Queue length > 0 AND
                    // - now - _lastDequeuedAt > 120 seconds
                    if (_backgroundWorker != null && 
                        !_backgroundWorker.IsCompleted && 
                        queueLength > 0 && 
                        lastDequeuedAt != DateTimeOffset.MinValue &&
                        (now - lastDequeuedAt).TotalSeconds > STALL_DETECTION_SECONDS)
                    {
                        workerStatus = _backgroundWorker.Status;
                        shouldRestart = true;
                    }
                }
                
                // Perform restart outside of lock (to avoid await in lock)
                if (shouldRestart)
                {
                    WriteNotificationLog("WARN", 
                        $"Notification worker stalled detected: " +
                        $"QueueLength={queueLength}, " +
                        $"LastDequeuedAt={lastDequeuedAt:o}, " +
                        $"SecondsSinceLastDequeue={(DateTimeOffset.UtcNow - lastDequeuedAt).TotalSeconds:F1}, " +
                        $"RestartCount={restartCount}, " +
                        $"WorkerTaskStatus={workerStatus}");
                    
                    // Cancel current worker
                    _cancellationTokenSource.Cancel();
                    
                    // Wait briefly for cancellation to propagate
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
                    }
                    catch { }
                    
                    // Perform restart operations inside lock
                    lock (_livenessLock)
                    {
                        // Dispose old cancellation token source
                        _cancellationTokenSource.Dispose();
                        
                        // Create new cancellation token source
                        _cancellationTokenSource = new CancellationTokenSource();
                        
                        // Increment restart count
                        _workerRestartCount++;
                        
                        // Reset timestamps
                        _workerStartedAt = DateTimeOffset.UtcNow;
                        _lastDequeuedAt = DateTimeOffset.MinValue;
                        _lastHeartbeatAt = DateTimeOffset.UtcNow;
                        
                        // Start fresh worker
                        _backgroundWorker = Task.Run(async () => await WorkerLoop(_cancellationTokenSource.Token));
                    }
                    
                    WriteNotificationLog("INFO", 
                        $"Notification worker restarted: " +
                        $"StartedAt={DateTimeOffset.UtcNow:o}, " +
                        $"RestartCount={_workerRestartCount}, " +
                        $"QueueLength={queueLength}");
                }
            }
            catch (Exception ex)
            {
                WriteNotificationLog("ERROR", 
                    $"Watchdog loop exception: " +
                    $"Exception={FormatExceptionChain(ex)}");
                await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
        }
    }
    
    /// <summary>
    /// Stop the service: drain queue and stop.
    /// </summary>
    public void Stop()
    {
        lock (_livenessLock)
        {
            if (_backgroundWorker == null) return;
            
            WriteNotificationLog("INFO", 
                $"Notification worker stopping: " +
                $"QueueLength={_queue.Count}, " +
                $"RestartCount={_workerRestartCount}");
            
            _cancellationTokenSource.Cancel();
            
            try
            {
                _backgroundWorker.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Expected when cancellation token is triggered
            }
            
            _backgroundWorker = null;
            _watchdogTask = null;
        }
    }
    
    /// <summary>
    /// Format exception chain recursively for logging.
    /// </summary>
    private static string FormatExceptionChain(Exception ex, int maxDepth = 10)
    {
        var sb = new System.Text.StringBuilder();
        var current = ex;
        int depth = 0;
        
        while (current != null && depth < maxDepth)
        {
            if (depth > 0)
                sb.Append(" -> ");
            
            sb.Append($"Type={current.GetType().Name}, Message={current.Message}");
            
            if (current.StackTrace != null)
            {
                var stackTrace = current.StackTrace.Replace("\r", "").Replace("\n", " | ");
                var truncatedStackTrace = stackTrace.Length > 1000 ? stackTrace.Substring(0, 1000) + "..." : stackTrace;
                sb.Append($", StackTrace={truncatedStackTrace}");
            }
            
            current = current.InnerException;
            depth++;
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Write log entry to notification_errors.log.
    /// </summary>
    private void WriteNotificationLog(string level, string message)
    {
        try
        {
            var logPath = System.IO.Path.Combine(_projectRoot, "logs", "robot", "notification_errors.log");
            var logDir = System.IO.Path.GetDirectoryName(logPath);
            if (!System.IO.Directory.Exists(logDir))
                System.IO.Directory.CreateDirectory(logDir);
            
            var logLine = $"{DateTimeOffset.UtcNow:o} [{level}] {message}\n";
            System.IO.File.AppendAllText(logPath, logLine);
        }
        catch
        {
            // If logging fails, ignore it (don't crash the robot)
        }
    }
    
    private async Task WorkerLoop(CancellationToken cancellationToken)
    {
        Thread.CurrentThread.IsBackground = true;
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    
                    // Emit heartbeat every 60 seconds
                    lock (_livenessLock)
                    {
                        if ((now - _lastHeartbeatAt).TotalSeconds >= HEARTBEAT_INTERVAL_SECONDS)
                        {
                            var uptime = _lastDequeuedAt != DateTimeOffset.MinValue 
                                ? (now - _workerStartedAt).TotalSeconds 
                                : 0;
                            
                            WriteNotificationLog("INFO", 
                                $"Notification worker heartbeat: " +
                                $"WorkerRunning=true, " +
                                $"QueueLength={_queue.Count}, " +
                                $"LastDequeuedAt={(_lastDequeuedAt != DateTimeOffset.MinValue ? _lastDequeuedAt.ToString("o") : "never")}, " +
                                $"WorkerUptimeSeconds={uptime:F1}, " +
                                $"RestartCount={_workerRestartCount}");
                            
                            _lastHeartbeatAt = now;
                        }
                    }
                    
                    if (_queue.TryDequeue(out var request))
                    {
                        // Update last dequeued timestamp (regardless of success/failure)
                        lock (_livenessLock)
                        {
                            _lastDequeuedAt = DateTimeOffset.UtcNow;
                        }
                        
                        // Send notification via Pushover with timeout guard
                        PushoverClient.SendResult result;
                        try
                        {
                            // Implement timeout guard using Task.WhenAny (10 seconds max)
                            var sendTask = PushoverClient.SendAsync(
                                _config.pushover_user_key,
                                _config.pushover_app_token,
                                request.Title,
                                request.Message,
                                request.Priority
                            );
                            
                            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                            var completedTask = await Task.WhenAny(sendTask, timeoutTask);
                            
                            if (completedTask == timeoutTask)
                            {
                                // Timeout occurred
                                result = new PushoverClient.SendResult
                                {
                                    Success = false,
                                    Exception = new TimeoutException("Pushover send operation timed out after 10 seconds")
                                };
                            }
                            else
                            {
                                // Send completed (successfully or with error)
                                result = await sendTask;
                            }
                        }
                        catch (OperationCanceledException ex)
                        {
                            // Cancellation occurred
                            result = new PushoverClient.SendResult
                            {
                                Success = false,
                                Exception = ex
                            };
                        }
                        catch (Exception ex)
                        {
                            // Unexpected exception during send
                            result = new PushoverClient.SendResult
                            {
                                Success = false,
                                Exception = ex
                            };
                        }
                        
                        if (result.Success)
                        {
                            // Log success (INFO level)
                            WriteNotificationLog("INFO", 
                                $"Pushover notification sent successfully: " +
                                $"Key={request.Key}, " +
                                $"Title={request.Title}, " +
                                $"Priority={request.Priority}, " +
                                $"HttpStatusCode={result.HttpStatusCode}, " +
                                $"Endpoint={PushoverClient.PUSHOVER_ENDPOINT}");
                        }
                        else
                        {
                            // Log failure (ERROR level) with full diagnostic details
                            var errorParts = new System.Text.StringBuilder();
                            errorParts.Append($"Pushover notification failed: ");
                            errorParts.Append($"Key={request.Key}, ");
                            errorParts.Append($"Title={request.Title}, ");
                            errorParts.Append($"Priority={request.Priority}, ");
                            errorParts.Append($"Endpoint={PushoverClient.PUSHOVER_ENDPOINT}");
                            
                            if (result.HttpStatusCode.HasValue)
                            {
                                errorParts.Append($", HttpStatusCode={result.HttpStatusCode.Value}");
                            }
                            
                            if (!string.IsNullOrWhiteSpace(result.ResponseBody))
                            {
                                var sanitizedBody = result.ResponseBody.Replace("\r", "").Replace("\n", " ");
                                errorParts.Append($", ResponseBody={sanitizedBody}");
                            }
                            
                            if (result.Exception != null)
                            {
                                errorParts.Append($", Exception={FormatExceptionChain(result.Exception)}");
                            }
                            
                            WriteNotificationLog("ERROR", errorParts.ToString());
                        }
                        
                        // Continue processing queue regardless of success/failure
                    }
                    else
                    {
                        await Task.Delay(WORKER_SLEEP_MS, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation token is triggered
                    WriteNotificationLog("INFO", 
                        $"Notification worker cancelled: " +
                        $"RestartCount={_workerRestartCount}");
                    break;
                }
                catch (Exception ex)
                {
                    // Log unexpected exception in worker loop (but don't crash)
                    WriteNotificationLog("ERROR", 
                        $"Unexpected exception in notification worker loop: " +
                        $"Exception={FormatExceptionChain(ex)}");
                    
                    // Continue processing - don't exit loop
                    try
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Worker exited unexpectedly
            WriteNotificationLog("ERROR", 
                $"Notification worker exited unexpectedly: " +
                $"Exception={FormatExceptionChain(ex)}, " +
                $"RestartCount={_workerRestartCount}");
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cancellationTokenSource.Dispose();
    }
    
    private class NotificationRequest
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public int Priority { get; set; }
    }
}
