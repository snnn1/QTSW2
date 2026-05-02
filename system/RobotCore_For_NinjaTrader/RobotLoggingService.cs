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

public sealed partial class RobotLoggingService : IDisposable
{
    // Singleton pattern: one service per project root
    private static readonly Dictionary<string, RobotLoggingService> _instances = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _instancesLock = new();
    private static int _instanceCounter = 0;

    private readonly ConcurrentQueue<RobotLogEvent> _queue = new();
    private readonly string _logDirectory;
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writersLock = new();
    private readonly Dictionary<string, StreamWriter> _healthWriters = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _healthWritersLock = new();
    private readonly string _healthDirectory;
    private CancellationTokenSource _cancellationTokenSource = new();
    private Task? _backgroundWorker;
    private bool _disposed = false;
    private int _referenceCount = 0; // Track how many engines are using this instance
    private readonly object _referenceLock = new();

    // Configuration constants (now configurable via LoggingConfig)
    private readonly int FLUSH_INTERVAL_MS;
    private readonly int MAX_QUEUE_SIZE;
    private readonly int MAX_BATCH_PER_FLUSH;
    private const int ERROR_LOG_INTERVAL_SECONDS = 10;

    // Log rotation and filtering configuration
    private readonly LoggingConfig _config;
    private readonly int _maxLogFileSizeBytes;
    private readonly string _minLogLevel;

    private DateTime _lastErrorLogTime = DateTime.MinValue;
    private long _droppedDebugCount = 0; // Changed to long for Interlocked operations
    private long _droppedInfoCount = 0;  // Changed to long for Interlocked operations
    private long _droppedDebugVolumeCapCount = 0; // DEBUG events dropped due to per-minute cap
    private int _writeFailureCount = 0;
    private DateTime _lastBackpressureEventUtc = DateTime.MinValue;
    private DateTime _lastWorkerErrorEventUtc = DateTime.MinValue;
    private const int BACKPRESSURE_EVENT_RATE_LIMIT_SECONDS = 60; // Emit backpressure event max once per minute
    private const int WORKER_ERROR_EVENT_RATE_LIMIT_SECONDS = 60; // Emit worker error event max once per minute

    // Human-friendly daily summary (written by background worker)
    private DateTime _lastDailySummaryWriteUtc = DateTime.MinValue;
    private const int DAILY_SUMMARY_WRITE_INTERVAL_SECONDS = 60;
    private readonly DailySummaryAggregator _dailySummary = new();
    
    // Daily rotation tracking
    private DateTime _lastRotationDateUtc = DateTime.MinValue;
    
    // Rate limiting: track last emission time and counts per event type
    private readonly Dictionary<string, DateTimeOffset> _lastEventEmission = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _eventCountsPerMinute = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastRateLimitResetUtc = DateTimeOffset.UtcNow;
    private readonly object _rateLimitLock = new();
    private DateTime _lastPipelineMetricUtc = DateTime.MinValue;
    private const int PIPELINE_METRIC_INTERVAL_SECONDS = 60; // 1 minute - lightweight, good operational visibility
    private int _debugCountThisMinute = 0;
    // Phase B: Logging metrics
    private long _eventsEnqueuedSinceLastMetric = 0;
    private long _eventsEnqueuedTotal = 0;
    private int _peakQueueDepth = 0;
    private double _avgQueueDepth = 0;
    private bool _avgQueueDepthInitialized = false;
    private long _lastFlushDurationMs = 0;
    private DateTimeOffset _lastDebugCapResetUtc = DateTimeOffset.UtcNow;

}
