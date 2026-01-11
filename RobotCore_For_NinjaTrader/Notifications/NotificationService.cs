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
    private bool _disposed = false;
    
    // Configuration constants
    private const int MAX_QUEUE_SIZE = 1000;
    private const int WORKER_SLEEP_MS = 100;
    
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
    /// Enqueue a notification request. Non-blocking, never throws.
    /// </summary>
    public void EnqueueNotification(string key, string title, string message, int priority)
    {
        if (_disposed) return;
        if (!_config.PushoverEnabled) return;
        
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
        if (_backgroundWorker != null && !_backgroundWorker.IsCompleted) return;
        
        if (_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
        }
        
        _backgroundWorker = Task.Run(async () => await WorkerLoop(_cancellationTokenSource.Token));
    }
    
    /// <summary>
    /// Stop the service: drain queue and stop.
    /// </summary>
    public void Stop()
    {
        if (_backgroundWorker == null) return;
        
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
    }
    
    private async Task WorkerLoop(CancellationToken cancellationToken)
    {
        Thread.CurrentThread.IsBackground = true;
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_queue.TryDequeue(out var request))
                {
                    // Send notification via Pushover (await directly since we're already in background task)
                    try
                    {
                        var success = await PushoverClient.SendAsync(
                            _config.PushoverUserKey,
                            _config.PushoverAppToken,
                            request.Title,
                            request.Message,
                            request.Priority
                        );
                        
                        // Note: Logging of send success/failure is handled by HealthMonitor
                    }
                    catch
                    {
                        // Swallow exceptions - notification failures should not crash the robot
                    }
                }
                else
                {
                    await Task.Delay(WORKER_SLEEP_MS, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
                break;
            }
            catch
            {
                // Swallow exceptions - notification failures should not crash the robot
                await Task.Delay(1000, cancellationToken);
            }
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
