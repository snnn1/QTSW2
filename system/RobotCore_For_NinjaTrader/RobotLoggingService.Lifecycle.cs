// SINGLE SOURCE OF TRUTH
// This file is the authoritative implementation of RobotLoggingService.
// It is compiled into Robot.Core.dll and should be referenced from that DLL.
// Do not duplicate this file elsewhere - if source is needed, reference Robot.Core.dll instead.
//
// Linked into: Robot.Core.csproj (modules/robot/core/)
// Referenced by: RobotCore_For_NinjaTrader (via Robot.Core.dll)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QTSW2.Robot.Core;

public sealed partial class RobotLoggingService
{
    public void FlushNowForSummary()
    {
        if (_disposed) return;
        FlushBatch(force: true);
    }

    /// <summary>
    /// Start the background worker thread.
    /// </summary>
    public void Start()
    {
        if (_backgroundWorker != null && !_backgroundWorker.IsCompleted) return;

        // If previous worker completed or was cancelled, create new token source
        if (_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
        }
        
        _backgroundWorker = Task.Run(() => WorkerLoop(_cancellationTokenSource.Token));
    }

    /// <summary>
    /// Stop the service: drain queue and flush, then stop.
    /// Bounded time limit to prevent NinjaTrader termination from hanging.
    /// </summary>
    public void Stop()
    {
        if (_backgroundWorker == null) return;

        // Signal cancellation
        _cancellationTokenSource.Cancel();

        bool workerCompleted = false;
        try
        {
            // Wait for worker to finish (with timeout)
            workerCompleted = _backgroundWorker.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // Expected when cancellation token is triggered
        }

        if (!workerCompleted)
        {
            // Worker didn't complete in time - log warning but continue shutdown
            LogErrorToFile("WARNING: Worker thread did not complete within timeout during shutdown");
        }

        // Final flush: drain remaining queue (bounded)
        var remainingCount = _queue.Count;
        if (remainingCount > 0)
        {
            // Limit final flush to prevent hanging on very large queues
            var maxFinalFlush = Math.Min(remainingCount, 10000);
            for (int i = 0; i < maxFinalFlush && _queue.TryDequeue(out var evt); i++)
            {
                // Process remaining events in small batches
                if (i % 1000 == 0)
                {
                    FlushBatch(force: false);
                }
            }
            FlushBatch(force: true);
            
            if (_queue.Count > 0)
            {
                LogErrorToFile($"WARNING: {_queue.Count} events were dropped during shutdown due to timeout");
            }
        }
        else
        {
            FlushBatch(force: true);
        }

        // Close all writers
        lock (_writersLock)
        {
            foreach (var writer in _writers.Values)
            {
                try
                {
                    writer.Flush();
                    writer.Dispose();
                }
                catch
                {
                    // Ignore errors during shutdown
                }
            }
            _writers.Clear();
        }
        
        // Close all health writers
        lock (_healthWritersLock)
        {
            foreach (var writer in _healthWriters.Values)
            {
                try
                {
                    writer.Flush();
                    writer.Dispose();
                }
                catch
                {
                    // Ignore errors during shutdown
                }
            }
            _healthWriters.Clear();
        }

        _backgroundWorker = null;
    }

}
