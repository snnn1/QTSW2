using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Threading;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Execution;

public enum StreamState
{
    PRE_HYDRATION,
    ARMED,
    RANGE_BUILDING,
    RANGE_LOCKED,
    DONE,
    SUSPENDED_DATA_INSUFFICIENT
}

/// <summary>
/// Bar source type for deduplication precedence.
/// Precedence order: LIVE > BARSREQUEST > CSV
/// </summary>
public enum BarSource
{
    /// <summary>
    /// Bar from live feed (OnBar) - highest precedence
    /// </summary>
    LIVE = 0,
    
    /// <summary>
    /// Bar from NinjaTrader BarsRequest API - medium precedence
    /// </summary>
    BARSREQUEST = 1,
    
    /// <summary>
    /// Bar from CSV file-based pre-hydration - lowest precedence
    /// </summary>
    CSV = 2
}

public sealed partial class StreamStateMachine
{
    
    public string Stream { get; }
    public string Instrument { get; }
    
    /// <summary>
    /// PHASE 1: Execution instrument (e.g., MES, MNQ) - used for order placement and fills.
    /// </summary>
    public string ExecutionInstrument { get; private set; } = "";
    
    /// <summary>
    /// PHASE 1: Canonical instrument (e.g., ES, NQ) - used for logic identity (future use).
    /// </summary>
    public string CanonicalInstrument { get; private set; } = "";
    
    public string Session { get; }
    public string SlotTimeChicago { get; private set; }

    public StreamState State { get; private set; } = StreamState.PRE_HYDRATION; // Initial state, will be set in constructor based on execution mode
    public bool Committed => _journal.Committed;
    public string? CommitReason => _journal.CommitReason;
    public bool ExecutionInterruptedByClose => _journal.ExecutionInterruptedByClose;
    public string? OriginalIntentId => _journal.OriginalIntentId;
    public string? ReentryIntentId => _journal.ReentryIntentId;
    public SlotStatus SlotStatus => _journal.SlotStatus;
    public bool IsActiveInterruptedBySessionClose =>
        _journal.SlotStatus == SlotStatus.ACTIVE && _journal.ExecutionInterruptedByClose && !_journal.Committed;
    public bool HasPlaybackCarryoverEvidence =>
        !_journal.Committed &&
        _journal.SlotStatus == SlotStatus.ACTIVE &&
        (_journal.ExecutionInterruptedByClose ||
         _journal.EntryDetected ||
         !string.IsNullOrWhiteSpace(_journal.OriginalIntentId) ||
         !string.IsNullOrWhiteSpace(_journal.ReentryIntentId) ||
         _journal.ReentrySubmitPending ||
         _journal.ReentrySubmitted ||
         _journal.ReentryFilled ||
         _journal.ProtectionSubmitted ||
         _journal.ProtectionAccepted);
    public bool HasTimetableRolloverRetentionEvidence =>
        !_journal.Committed ||
        _journal.SlotStatus == SlotStatus.ACTIVE ||
        _journal.ExecutionInterruptedByClose ||
        _journal.ReentrySubmitPending ||
        _journal.ReentrySubmitted ||
        _journal.ReentryFilled ||
        _journal.ProtectionSubmitted ||
        _journal.ProtectionAccepted;
    public bool HasActiveReentryLifecycleEvidence =>
        !_journal.Committed &&
        _journal.SlotStatus == SlotStatus.ACTIVE &&
        (!string.IsNullOrWhiteSpace(_journal.ReentryIntentId) ||
         _journal.ReentrySubmitPending ||
         _journal.ReentrySubmitted ||
         _journal.ReentryFilled ||
         _journal.ProtectionSubmitted ||
         _journal.ProtectionAccepted);
    public bool RangeInvalidated => _rangeInvalidated; // Expose range invalidation status for engine-level tracking
    public string TradingDate => _journal.TradingDate;

    // Range tracking (points)
    public decimal? RangeHigh { get; private set; }
    public decimal? RangeLow { get; private set; }
    public decimal? FreezeClose { get; private set; }
    public string FreezeCloseSource { get; private set; } = "UNSET";
    public bool BracketsIntended { get; private set; }

    public DateTimeOffset RangeStartUtc { get; private set; }
    public DateTimeOffset SlotTimeUtc { get; private set; }
    public DateTimeOffset MarketCloseUtc { get; private set; }

    /// <summary>
    /// Returns whether entry orders for this stream are still eligible (no cancellation needed when position flat).
    /// When false, entry orders should be cancelled when position is flat (invalid lifecycle state).
    /// Eligible: ACTIVE RANGE_LOCKED. Non-eligible: EXPIRED, COMMITTED, POST-FLATTEN, forced_flatten.
    /// </summary>
    public (bool Eligible, string? Reason) GetEntryOrderCancellationEligibility()
    {
        if (Committed) return (false, "committed");
        if (State != StreamState.RANGE_LOCKED) return (false, "invalid_state");
        if (_journal.SlotStatus != SlotStatus.ACTIVE) return (false, "slot_expired");
        if (_journal.ExecutionInterruptedByClose) return (false, "forced_flatten");
        return (true, null);
    }

    /// <summary>
    /// Returns a dictionary of stream status fields for logging (STREAM_STATUS_SUMMARY, STREAM_STATE_SNAPSHOT).
    /// </summary>
    public Dictionary<string, object> GetStatusForLogging(DateTimeOffset utcNow)
    {
        return new Dictionary<string, object>
        {
            ["stream_id"] = Stream,
            ["instrument"] = Instrument,
            ["execution_instrument"] = ExecutionInstrument,
            ["canonical_instrument"] = CanonicalInstrument,
            ["session"] = Session,
            ["slot_time_chicago"] = SlotTimeChicago,
            ["state"] = State.ToString(),
            ["committed"] = Committed,
            ["range_invalidated"] = RangeInvalidated,
            ["trading_date"] = TradingDate,
            ["range_high"] = RangeHigh ?? (object)"UNSET",
            ["range_low"] = RangeLow ?? (object)"UNSET",
            ["range_start_utc"] = RangeStartUtc.ToString("o"),
            ["slot_time_utc"] = SlotTimeUtc.ToString("o"),
            ["market_close_utc"] = MarketCloseUtc.ToString("o"),
            ["now_utc"] = utcNow.ToString("o")
        };
    }
    
    // Chicago time boundaries for range computation (authoritative for bar filtering)
    // CRITICAL: These are DateTimeOffset values in Chicago timezone, not strings
    private DateTimeOffset RangeStartChicagoTime { get; set; }
    private DateTimeOffset SlotTimeChicagoTime { get; set; }
    /// <summary>Market reopen (same calendar day as market close). Used for reentry time gate.</summary>
    private DateTimeOffset MarketReopenChicagoTime { get; set; }

    private readonly TimeService _time;
    private readonly ParitySpec _spec;
    private readonly RobotLogger _log;
    private readonly JournalStore _journals;
    /// <summary>Repository root for market data (e.g. data/raw).</summary>
    private readonly string _projectRoot;
    /// <summary>Root for robot state: journals, hydration/range JSONL, execution journal scans (may equal <see cref="_projectRoot"/> or an isolated playback tree).</summary>
    private readonly string _robotStateRoot;
    /// <summary>Isolated SIM playback: skip loading prior <see cref="StreamJournal"/> from disk so each run starts with a fresh in-memory slot (see <see cref="RobotEngine"/>).</summary>
    private readonly bool _ignoreExistingStreamJournals;
    private readonly RangeLockedEventPersister? _rangePersister;
    private readonly HydrationEventPersister? _hydrationPersister;
    private readonly RangeBuildingSnapshotPersister? _rangeBuildingSnapshotPersister;
    private StreamJournal _journal;
    private readonly decimal _tickSize;
    private readonly string _timetableHash;
    private readonly ExecutionMode _executionMode;
    private readonly decimal _baseTarget;
    private readonly IExecutionAdapter? _executionAdapter;
    private readonly RiskGate? _riskGate;
    private readonly ExecutionJournal? _executionJournal;
    private readonly RobotEngine? _engine; // Optional: engine reference for BarsRequest status checks

    // IEA alignment: block checks and fallbacks for forced flatten, slot expiry, reentry
    private readonly Func<string, bool>? _isInstrumentBlockedForReentry;
    private readonly Func<string, DateTimeOffset, FlattenResult>? _emergencyFlatten;
    private readonly Func<string, bool>? _isIeaQueueHealthyForInstrument;
    private DateTimeOffset? _lastActiveReentryForcedFlattenSkipLogUtc;
    private string? _lastActiveReentryForcedFlattenSkipLogKey;
    private string? _priorJournalTerminalMirrorCompletedKey;
    private const double ActiveReentryForcedFlattenSkipLogIntervalMinutes = 60.0;

    // Canonical event writer for replay reconstruction (forced flatten, slot expiry)
    private readonly ExecutionEventWriter? _eventWriter;

    private readonly int _orderQuantity; // PHASE 3.2: Fixed order quantity for all intents (code-controlled)
    private readonly int _maxQuantity; // Policy max_size
    private bool _stopBracketsSubmittedAtLock = false;
    
    /// <summary>Invariant-based recovery: typed action (None, ResubmitClean, CancelAndRebuild). Replaces coarse _reconciliationRequestedResubmit.</summary>
    private EntryOrderRecoveryState _entryOrderRecoveryState = EntryOrderRecoveryState.None();
    
    // PHASE 4: Alert callback for missing data incidents
    private Action<string, string, string, int>? _alertCallback;
    
    // Critical event reporting callback: (eventType, payload, tradingDate) => void
    private Action<string, Dictionary<string, object>, string?>? _reportCriticalCallback;

    // Diagnostic logging configuration
    private readonly bool _enableDiagnosticLogs;
    private readonly int _barDiagnosticRateLimitSeconds;
    private readonly int _slotGateDiagnosticRateLimitSeconds;

    // Bar buffer for retrospective range computation (live mode)
    private readonly List<Bar> _barBuffer = new();
    private readonly object _barBufferLock = new();
    private bool _rangeLocked = false; // Authoritative lock flag - range is immutable when true
    private DateTimeOffset? _rangeLockAttemptedAtUtc = null; // Track when lock was attempted (for rate-limiting)
    private int _rangeLockFailureCount = 0; // Track consecutive failures (for backoff)
    private bool _rangeLockCommitted = false; // Track if lock was successfully committed (for duplicate detection)
    private bool _breakoutLevelsMissing = false; // Gate flag: prevents entries if breakout levels failed
    
    // RANGE_BUILDING restore: cutoff for bar deduplication (bars with timestamp <= this are already in snapshot)
    private DateTimeOffset? _lastProcessedBarTimeUtc = null;
    private bool _restoredFromRangeBuildingSnapshot = false;
    
    // Bar source tracking for deduplication precedence
    // CRITICAL: Track bar sources to enforce precedence: LIVE > BARSREQUEST > CSV
    // This dictionary maps bar timestamp to its source, used for centralized deduplication
    private readonly Dictionary<DateTimeOffset, BarSource> _barSourceMap = new();
    
    // Bar source tracking for logging clarity
    // CRITICAL: Track bar sources to make debugging transparent
    // Without explicit logs, you won't remember:
    // - Which bars came from BarsRequest (historical)
    // - Which came from live feed
    // - Whether filtering occurred
    // - What deduplication happened
    private int _historicalBarCount = 0; // Bars from BarsRequest/pre-hydration
    private int _liveBarCount = 0; // Bars from live feed (OnBar)

    /// <summary>
    /// Running max high / min low for bars with barUtc &gt; SlotTimeUtc (strictly after slot).
    /// Authoritative source for pre-submit &quot;breakout already happened&quot;; journal flags are supplemental (e.g. restart).
    /// </summary>
    private bool _postSlotExcursionHasSamples;
    private decimal _postSlotMaxHighSinceSlot = decimal.MinValue;
    private decimal _postSlotMinLowSinceSlot = decimal.MaxValue;

    private int _filteredFutureBarCount = 0; // Bars filtered (future)
    private int _filteredPartialBarCount = 0; // Bars filtered (partial/in-progress)
    private int _dedupedBarCount = 0; // Bars deduplicated (replaced existing)
    private DateTimeOffset? _lastSlotGateDiagnostic = null; // Rate-limiting for SLOT_GATE_DIAGNOSTIC
    private DateTimeOffset? _lastBarDiagnosticTime = null; // Rate-limiting for BAR_RECEIVED_DIAGNOSTIC
    private bool _lastSlotGateState = false; // Track previous gate state for change detection
    private DateTimeOffset? _lastStuckRangeBuildingAlertUtc = null; // Rate-limiting for RANGE_BUILDING_STUCK_PAST_SLOT_TIME alert
    
    // Pre-hydration state
    private bool _preHydrationComplete = false; // Must be true before entering ARMED/RANGE_BUILDING
    private bool _hadZeroBarHydration = false; // Track if hydration resulted in zero bars (for terminal state classification)
    
    // Gap tolerance tracking (all times in Chicago, bar OPEN times)
    private double _largestSingleGapMinutes = 0.0; // Largest single gap seen
    private double _totalGapMinutes = 0.0; // Cumulative gap time
    private DateTimeOffset? _lastBarOpenChicago = null; // Last bar open time (Chicago) for gap calculation
    private bool _rangeInvalidated = false; // Permanently invalidated due to gap violation
    private bool _rangeInvalidatedNotified = false; // Track if RANGE_INVALIDATED notification already sent for this slot
    
    // Gap tolerance constants (only apply to DATA_FEED_FAILURE - LOW_LIQUIDITY gaps never invalidate)
    private const double MAX_SINGLE_GAP_MINUTES = 3.0;
    private const double MAX_TOTAL_GAP_MINUTES = 6.0;
    private const double MAX_GAP_LAST_10_MINUTES = 2.0;
    
    // Health logging state
    private DateTimeOffset? _lastHeartbeatUtc = null; // Throttle heartbeat logs
    private const int HEARTBEAT_INTERVAL_MINUTES = 7; // Heartbeat every 7 minutes
    private DateTimeOffset? _lastBarReceivedUtc = null; // Track data feed health
    private const int DATA_FEED_STALL_THRESHOLD_MINUTES = 3; // Warn if no bars for 3+ minutes
    private DateTimeOffset? _lastBarTimestampUtc = null; // Detect out-of-order bars
    private bool _slotEndSummaryLogged = false; // Ensure exactly one summary per slot
    
    // Logging rate limiting
    private DateTimeOffset? _lastRangeComputeFailedLogUtc = null; // Rate-limit RANGE_COMPUTE_FAILED (once per minute max)
    private DateTimeOffset? _lastBarFilteringLogUtc = null; // Rate-limit RANGE_COMPUTE_BAR_FILTERING (once per minute max)
    private DateTimeOffset? _stateEntryTimeUtc = null; // Track when current state was entered (for stuck detection)
    private DateTimeOffset? _lastStuckStateCheckUtc = null; // Rate-limit stuck state checks (once per 5 minutes max)
    private DateTimeOffset? _lastLiveBarAgeWarningUtc = null; // Rate-limit BAR_PARTIAL_WARNING_LIVE_FEED (once per stream per 5 minutes)
    private DateTimeOffset? _lastBarBufferedStateIndependentUtc = null; // Rate-limit BAR_BUFFERED_STATE_INDEPENDENT (once per stream per 5 minutes)
    private DateTimeOffset? _lastPreHydrationHandlerTraceUtc = null; // Rate-limit PRE_HYDRATION_HANDLER_TRACE (once per stream per 5 minutes)
    private DateTimeOffset? _lastArmedWaitingForBarsLogUtc = null; // Rate-limit ARMED_WAITING_FOR_BARS (once per stream per 5 minutes)
    private DateTimeOffset? _lastRangeLockedPreSlotWaitLogUtc = null; // Rate-limit RANGE_LOCKED_PRE_SLOT_WAIT (once per stream per 30 minutes)
    // Assertion flags (once per stream per day)
    private bool _rangeIntentAssertEmitted = false; // RANGE_INTENT_ASSERT emitted
    private bool _firstBarAcceptedAssertEmitted = false; // RANGE_FIRST_BAR_ACCEPTED emitted
    private bool _rangeLockAssertEmitted = false; // RANGE_LOCK_ASSERT emitted

    /// <summary>Once per (trading_date|stream|reason) SESSION_REENTRY_BLOCKED engine events (avoids tick spam).</summary>
    private readonly HashSet<string> _sessionReentryBlockedLogKeys = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastForcedFlattenPreEntryCancelUtc = null;

    // Dry-run entry tracking
    // SEMANTIC CHANGE: _entryDetected is now an observation flag (post-fact), not a decision flag (pre-submission)
    // After removing immediate entry path, _entryDetected is only set:
    // - On restart (from journal/execution journal)
    // - In LogNoTradeMarketClose() (mark as processed)
    // It is NOT set pre-submission - execution journal is authoritative for fill state
    // All _entryDetected checks are kept for defense-in-depth and restart recovery support
    private bool _entryDetected;
    private string? _intendedDirection; // "Long", "Short", or null
    private decimal? _intendedEntryPrice;
    private decimal? _brkLongRaw;
    private decimal? _brkShortRaw;
    private decimal? _brkLongRounded;
    private decimal? _brkShortRounded;
    
    // Execution tracking
    private decimal? _intendedStopPrice;
    private decimal? _intendedTargetPrice;
    private decimal? _intendedBeTrigger;

    /// <summary>Stop-entry at RANGE_LOCKED deferred until position authority becomes REAL (RECOVERY → REAL).</summary>
    private bool _deferredBracketTradePending;
    private DateTimeOffset? _deferredBracketTradeExpiryUtc;
    private bool _loggedTradeExecutedFromDeferred;

    public StreamStateMachine(
        TimeService time,
        ParitySpec spec,
        RobotLogger log,
        JournalStore journals,
        DateOnly tradingDate, // PHASE 3: Accept DateOnly directly (authoritative), no parsing needed
        string timetableHash,
        TimetableStream directive,
        ExecutionMode executionMode,
        int orderQuantity, // baseSize (PHASE 3.2: Fixed order quantity, mandatory, code-controlled)
        int maxQuantity, // NEW: maxSize (policy max_size)
        string projectRoot, // Repository root (market data paths)
        string robotStateRoot, // Root for journals + hydration/range persistence (same layout as project root)
        bool ignoreExistingStreamJournals = false,
        IExecutionAdapter? executionAdapter = null,
        RiskGate? riskGate = null,
        ExecutionJournal? executionJournal = null,
        LoggingConfig? loggingConfig = null, // Optional: logging configuration for diagnostic control
        RobotEngine? engine = null, // Optional: engine reference for BarsRequest status checks
        Func<string, bool>? isInstrumentBlockedForReentry = null,
        Func<string, DateTimeOffset, FlattenResult>? emergencyFlatten = null,
        Func<string, bool>? isIeaQueueHealthyForInstrument = null,
        ExecutionEventWriter? eventWriter = null,
        DateTimeOffset? streamInitializationEventUtc = null
    )
    {
        _time = time;
        _spec = spec;
        _log = log;
        _journals = journals;
        _projectRoot = projectRoot;
        _robotStateRoot = robotStateRoot;
        _ignoreExistingStreamJournals = ignoreExistingStreamJournals;
        _timetableHash = timetableHash;
        _executionMode = executionMode;
        _executionAdapter = executionAdapter;
        _riskGate = riskGate;
        _executionJournal = executionJournal;
        _engine = engine;
        _isInstrumentBlockedForReentry = isInstrumentBlockedForReentry;
        _emergencyFlatten = emergencyFlatten;
        _isIeaQueueHealthyForInstrument = isIeaQueueHealthyForInstrument;
        _eventWriter = eventWriter;

        // Align mid-session restart / hydration init timestamps with engine tick time in playback; wall clock when unknown.
        var initEventUtc = streamInitializationEventUtc ?? DateTimeOffset.UtcNow;

        // Initialize range event persister (singleton)
        _rangePersister = RangeLockedEventPersister.GetInstance(_robotStateRoot);
        
        // Initialize hydration event persister (singleton)
        _hydrationPersister = HydrationEventPersister.GetInstance(_robotStateRoot);
        
        // Initialize RANGE_BUILDING snapshot persister (singleton)
        _rangeBuildingSnapshotPersister = RangeBuildingSnapshotPersister.GetInstance(_robotStateRoot);
        
        // PHASE 3.2: Store and validate order quantity
        _orderQuantity = orderQuantity;
        if (_orderQuantity <= 0)
        {
            throw new ArgumentException(
                $"Order quantity must be positive, got {_orderQuantity}. " +
                $"Execution instrument: {ExecutionInstrument}",
                nameof(orderQuantity));
        }
        
        // Store max quantity
        _maxQuantity = maxQuantity;
        if (_maxQuantity <= 0)
        {
            throw new ArgumentException(
                $"Max quantity must be positive, got {_maxQuantity}. " +
                $"Execution instrument: {ExecutionInstrument}",
                nameof(maxQuantity));
        }
        
        if (_orderQuantity > _maxQuantity)
        {
            throw new ArgumentException(
                $"Order quantity ({_orderQuantity}) cannot exceed max quantity ({_maxQuantity}). " +
                $"Execution instrument: {ExecutionInstrument}");
        }

        // Load diagnostic logging configuration
        _enableDiagnosticLogs = loggingConfig?.enable_diagnostic_logs ?? false;
        _barDiagnosticRateLimitSeconds = loggingConfig?.diagnostic_rate_limits?.bar_diagnostic_seconds ?? (_enableDiagnosticLogs ? 30 : 300);
        _slotGateDiagnosticRateLimitSeconds = loggingConfig?.diagnostic_rate_limits?.slot_gate_diagnostic_seconds ?? (_enableDiagnosticLogs ? 30 : 60);

        TimetableStream.EnsureExecutionFields(directive);

        Stream = directive.stream;
        Session = directive.session;
        SlotTimeChicago = directive.slot_time;

        // PHASE 2: Store execution instrument (from timetable directive) - used for orders
        ExecutionInstrument = directive.instrument.ToUpperInvariant();
        
        // PHASE 2: Compute canonical instrument (maps micro futures to base instruments) - used for logic
        CanonicalInstrument = GetCanonicalInstrument(ExecutionInstrument, spec);
        
        // PHASE 2: Instrument property now represents LOGIC identity (canonical), not execution
        Instrument = CanonicalInstrument;

        // PHASE 3: Use DateOnly directly (no parsing needed)
        var dateOnly = tradingDate;
        var tradingDateStr = tradingDate.ToString("yyyy-MM-dd"); // Convert to string only for journal/logging

        // Initialize time boundaries using extracted method
        RecomputeTimeBoundaries(dateOnly, spec, time);

        if (!spec.TryGetInstrument(Instrument, out var inst))
            throw new InvalidOperationException($"Instrument not found in parity spec: {Instrument}");
        _tickSize = inst.tick_size;
        _baseTarget = inst.base_target;

        StreamJournal? existing = null;
        if (!_ignoreExistingStreamJournals)
            existing = journals.TryLoad(tradingDateStr, Stream);
        var isRestart = existing != null;
        var isMidSessionRestart = false;
        
        if (isRestart)
        {
            // Check if this is a mid-session restart (stream was already initialized today)
            var nowUtc = initEventUtc;
            var nowChicago = time.ConvertUtcToChicago(nowUtc);
            
            // Mid-session restart if:
            // 1. Journal exists (stream was initialized before)
            // 2. Journal is not committed (stream was active)
            // 3. Current time is after range start (session has begun)
            isMidSessionRestart = !existing.Committed && nowChicago >= RangeStartChicagoTime;
            
            if (isMidSessionRestart)
            {
                // RESTART BEHAVIOR POLICY: "Restart = Full Reconstruction"
                // When strategy restarts mid-session:
                // - BarsRequest loads historical bars from range_start to min(slot_time, now)
                // - Range is recomputed from all available bars (historical + live)
                // - This may differ from uninterrupted operation if restart occurs after slot_time
                // - Result: Deterministic reconstruction, but may differ from continuous run
                //
                // Alternative policy (not implemented): "Restart invalidates stream"
                // - Would mark stream as invalidated if restart occurs after slot_time
                // - Would prevent trading for that stream on that day
                //
                // Current choice: Full reconstruction allows recovery from crashes/restarts
                // Trade-off: Same day may produce different results depending on restart timing
                
                log.Write(RobotEvents.Base(time, nowUtc, tradingDateStr, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "MID_SESSION_RESTART_DETECTED", "STREAM_INIT",
                    new
                    {
                        trading_date = tradingDateStr,
                        previous_state = existing.LastState,
                        previous_update_utc = existing.LastUpdateUtc,
                        restart_time_chicago = nowChicago.ToString("o"),
                        restart_time_utc = nowUtc.ToString("o"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                        execution_instrument = ExecutionInstrument,  // PHASE 3: Execution identity
                        canonical_instrument = CanonicalInstrument,   // PHASE 3: Canonical identity
                        policy = "RESTART_FULL_RECONSTRUCTION",
                        note = "Mid-session restart detected - will reconstruct range from historical + live bars. Result may differ from uninterrupted operation."
                    }));
                
                // Notify HealthMonitor to send push notification (OnConnectionStatusUpdate never fires when process crashes)
                var prevUpdateUtc = DateTimeOffset.TryParse(existing.LastUpdateUtc, out var parsed) ? parsed : DateTimeOffset.MinValue;
                _engine?.OnMidSessionRestartDetected(Stream, tradingDateStr, existing.LastState ?? "", prevUpdateUtc, nowUtc);
                
                // Signal that BarsRequest should be called for restart
                // Strategy will check for restart state after Realtime transition
                // Log event for diagnostics
                log.Write(RobotEvents.Base(time, nowUtc, tradingDateStr, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "RESTART_BARSREQUEST_NEEDED", "STREAM_INIT",
                    new
                    {
                        canonical_instrument = CanonicalInstrument,
                        execution_instrument = ExecutionInstrument,
                        restart_time_chicago = nowChicago.ToString("o"),
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                        note = "Stream restarted - BarsRequest should be called to load historical bars up to current time"
                    }));
            }
        }
        
        _journal = existing ?? new StreamJournal
        {
            TradingDate = tradingDateStr,
            Stream = Stream,
            Committed = false,
            CommitReason = null,
            LastState = State.ToString(),
            LastUpdateUtc = DateTimeOffset.MinValue.ToString("o"),
            TimetableHashAtCommit = null,
            StopBracketsSubmittedAtLock = false,
            EntryDetected = false,
            SlotStatus = SlotStatus.ACTIVE,
            SlotInstanceKey = null, // Will be set below if new journal
            ExecutionInterruptedByClose = false,
            ReentrySubmitPending = false,
            ReentrySubmitPendingAtUtc = null,
            ReentrySubmitLastFailureUtc = null,
            ReentrySubmitFailureCount = 0,
            LastReentrySubmitError = null,
            ReentrySubmitted = false,
            ReentryFilled = false,
            ProtectionSubmitted = false,
            ProtectionAccepted = false
        };
        
        // SLOT PERSISTENCE: Generate SlotInstanceKey for new slots (set once, never overwritten)
        if (existing == null && string.IsNullOrWhiteSpace(_journal.SlotInstanceKey))
        {
            // New slot lifecycle - generate SlotInstanceKey
            _journal.SlotInstanceKey = $"{Stream}_{SlotTimeChicago}_{tradingDateStr}";
            
            // Calculate and store NextSlotTimeUtc
            _journal.NextSlotTimeUtc = CalculateNextSlotTimeUtc(tradingDate, SlotTimeChicago, time);
        }
        else if (existing != null && !string.IsNullOrWhiteSpace(existing.SlotInstanceKey))
        {
            // Restore SlotInstanceKey from existing journal (preserve lifecycle identity)
            _journal.SlotInstanceKey = existing.SlotInstanceKey;
            _journal.SlotStatus = existing.SlotStatus;
            _journal.ExecutionInterruptedByClose = existing.ExecutionInterruptedByClose;
            _journal.ForcedFlattenTimestamp = existing.ForcedFlattenTimestamp;
            _journal.OriginalIntentId = existing.OriginalIntentId;
            _journal.ReentryIntentId = existing.ReentryIntentId;
            _journal.ReentrySubmitPending = existing.ReentrySubmitPending;
            _journal.ReentrySubmitPendingAtUtc = existing.ReentrySubmitPendingAtUtc;
            _journal.ReentrySubmitLastFailureUtc = existing.ReentrySubmitLastFailureUtc;
            _journal.ReentrySubmitFailureCount = existing.ReentrySubmitFailureCount;
            _journal.LastReentrySubmitError = existing.LastReentrySubmitError;
            _journal.ReentrySubmitted = existing.ReentrySubmitted;
            _journal.ReentryFilled = existing.ReentryFilled;
            _journal.ProtectionSubmitted = existing.ProtectionSubmitted;
            _journal.ProtectionAccepted = existing.ProtectionAccepted;
            _journal.NextSlotTimeUtc = existing.NextSlotTimeUtc ?? CalculateNextSlotTimeUtc(tradingDate, SlotTimeChicago, time);
            _journal.PriorJournalKey = existing.PriorJournalKey;
        }

        if (_journal.Committed)
        {
            TryEnsureCarryForwardTerminalMirror(
                initEventUtc,
                _journal.CommitReason ?? "JOURNAL_ALREADY_COMMITTED",
                "TRADE_COMPLETED",
                _journal.SlotStatus);
        }
        
        // Initialize state entry time tracking
        _stateEntryTimeUtc = initEventUtc;
        
        // RESTART RECOVERY: Restore flags from persisted state
        if (isRestart && existing != null)
        {
            // Restore order submission flag from journal
            _stopBracketsSubmittedAtLock = existing.StopBracketsSubmittedAtLock;
            
            // Restore recovery action from journal (invariant-based model)
            if (!string.IsNullOrEmpty(existing.RecoveryAction) && existing.RecoveryAction != "None")
            {
                _entryOrderRecoveryState = new EntryOrderRecoveryState
                {
                    Action = Enum.TryParse<EntryOrderRecoveryAction>(existing.RecoveryAction, true, out var a) ? a : EntryOrderRecoveryAction.None,
                    Reason = existing.RecoveryActionReason ?? "",
                    IssuedUtc = DateTimeOffset.TryParse(existing.RecoveryActionIssuedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : default
                };
            }
            
            // Restore entry detection from execution journal (Option B)
            if (_executionJournal != null && !existing.EntryDetected)
            {
                // Scan execution journal for any filled entry
                _entryDetected = _executionJournal.HasEntryFillForStream(tradingDateStr, Stream);
            }
            else
            {
                // Use journal value if available, otherwise default to false
                _entryDetected = existing.EntryDetected;
            }
            
            // REQUIRED CHANGE #4: Restart recovery — make hydration/range events canonical
            // On startup, replay hydration_{day}.jsonl (or ranges_{day}.jsonl)
            // If a RANGE_LOCKED event exists for this stream+day, restore locked state
            if (isRestart && existing != null)
            {
                // Safe by design: skip restore when waiting for re-entry after forced flatten
                // (would overwrite _stopBracketsSubmittedAtLock and could confuse retry logic)
                if (!existing.ExecutionInterruptedByClose)
                {
                    RestoreRangeLockedFromHydrationLog(tradingDateStr, Stream);
                }
                
                // FAIL-CLOSED: If previous_state == RANGE_LOCKED but restore failed AND bars are insufficient, suspend stream
                if (existing.LastState == "RANGE_LOCKED" && !_rangeLocked)
                {
                    // Check if bars are insufficient using helper method
                    var barCount = GetBarBufferCount();
                    if (!HasSufficientRangeBars(barCount, out var expectedBarCount, out var minimumRequired))
                    {
                        // SUSPEND STREAM - Do not recompute
                        LogHealth("CRITICAL", "RANGE_LOCKED_RESTORE_FAILED_INSUFFICIENT_BARS",
                            "CRITICAL: Previous state was RANGE_LOCKED but restore failed and bars are insufficient - SUSPENDING STREAM",
                            new
                            {
                                previous_state = existing.LastState,
                                restore_succeeded = _rangeLocked,
                                bar_count = barCount,
                                expected_bar_count = expectedBarCount,
                                minimum_required = minimumRequired,
                                note = "Stream suspended - do not recompute range. Manual intervention required."
                            });
                        
                        // Transition to explicit suspended state
                        State = StreamState.SUSPENDED_DATA_INSUFFICIENT;
                        // Do NOT allow recomputation
                        // Stream remains in suspended state until manual intervention
                        // Exit constructor early - stream is suspended
                        return;
                    }
                }
                
                // ARCHITECTURAL DOCTRINE: Hydration logs are canonical. Journal is advisory only.
                // If hydration log restoration failed, range must be recomputed (explicit failure, not hidden fallback).
                // Removed journal fallback to enforce single canonical source of truth.
                
                // RANGE_BUILDING restore: if previous state was RANGE_BUILDING and RANGE_LOCKED restore did not apply
                if (existing.LastState == "RANGE_BUILDING" && !_rangeLocked)
                {
                    RestoreRangeBuildingFromSnapshot(tradingDateStr, Stream);
                }
            }
        }

        if (_journal.Committed)
        {
            State = StreamState.DONE;
            TryEnsureCarryForwardTerminalMirror(
                initEventUtc,
                _journal.CommitReason ?? "JOURNAL_ALREADY_COMMITTED",
                "TRADE_COMPLETED",
                _journal.SlotStatus);
        }

        // Log STREAM_INITIALIZED hydration event
        try
        {
            var utcNow = initEventUtc;
            var chicagoNow = time.ConvertUtcToChicago(utcNow);
            var initData = new Dictionary<string, object>
            {
                ["execution_mode"] = executionMode.ToString(),
                ["order_quantity"] = orderQuantity,
                ["max_quantity"] = maxQuantity,
                ["timetable_hash"] = timetableHash,
                ["is_restart"] = isRestart,
                ["is_mid_session_restart"] = isMidSessionRestart,
                ["range_start_chicago"] = RangeStartChicagoTime.ToString("o"),
                ["slot_time_chicago"] = SlotTimeChicagoTime.ToString("o"),
                ["range_start_utc"] = RangeStartUtc.ToString("o"),
                ["slot_time_utc"] = SlotTimeUtc.ToString("o"),
                ["tick_size"] = _tickSize,
                ["base_target"] = _baseTarget
            };
            
            if (isRestart && existing != null)
            {
                initData["previous_state"] = existing.LastState;
                initData["previous_update_utc"] = existing.LastUpdateUtc;
            }
            
            var hydrationEvent = new HydrationEvent(
                eventType: "STREAM_INITIALIZED",
                tradingDay: tradingDateStr,
                streamId: Stream,
                canonicalInstrument: CanonicalInstrument,
                executionInstrument: ExecutionInstrument,
                session: Session,
                slotTimeChicago: SlotTimeChicago,
                timestampUtc: utcNow,
                timestampChicago: chicagoNow,
                state: State.ToString(),
                data: initData
            );
            
            _hydrationPersister?.Persist(hydrationEvent);
        }
        catch (Exception)
        {
            // Fail-safe: hydration logging never throws
        }
    }

    /// <summary>
    /// PHASE 1: Get canonical instrument for a given execution instrument.
    /// Maps micro futures (MES, MNQ, etc.) to their base instruments (ES, NQ, etc.).
    /// Returns execution instrument unchanged if not a micro or if spec is unavailable.
    /// </summary>
    private static string GetCanonicalInstrument(string executionInstrument, ParitySpec spec)
    {
        if (spec != null &&
            spec.TryGetInstrument(executionInstrument, out var inst) &&
            inst.is_micro &&
            !string.IsNullOrWhiteSpace(inst.base_instrument))
        {
            return inst.base_instrument.ToUpperInvariant(); // MES → ES
        }

        return executionInstrument.ToUpperInvariant(); // ES → ES
    }

    /// <summary>
    /// PHASE 2: Check if incoming instrument matches this stream's canonical instrument.
    /// Routes bars by canonical instrument (MES bars route to ES streams).
    /// </summary>
    public bool IsSameInstrument(string incomingInstrument)
    {
        // PHASE 2: Canonicalize incoming instrument for comparison
        var incomingCanonical = GetCanonicalInstrument(incomingInstrument, _spec);
        return string.Equals(
            CanonicalInstrument,
            incomingCanonical,
            StringComparison.OrdinalIgnoreCase
        );
    }
    
    /// <summary>
    /// Record filtered bars for logging clarity.
    /// Called by RobotEngine when filtering bars during pre-hydration.
    /// </summary>
    public void RecordFilteredBars(int filteredFuture, int filteredPartial)
    {
        _filteredFutureBarCount += filteredFuture;
        _filteredPartialBarCount += filteredPartial;
    }
    
    /// <summary>
    /// PHASE 4: Set alert callback for triggering high-priority notifications.
    /// </summary>
    public void SetAlertCallback(Action<string, string, string, int>? callback)
    {
        _alertCallback = callback;
    }
    
    /// <summary>
    /// Set callback for reporting critical events to HealthMonitor.
    /// </summary>
    public void SetReportCriticalCallback(Action<string, Dictionary<string, object>, string?>? callback)
    {
        _reportCriticalCallback = callback;
    }

    public bool ApplyDirectiveUpdate(string newSlotTimeChicago, DateOnly tradingDate, DateTimeOffset utcNow)
    {
        // NQ2 FIX: Allow slot_time updates during PRE_HYDRATION and RANGE_BUILDING (before range lock).
        // Reject once RANGE_LOCKED or beyond - prevents mid-session changes after commitment.
        var allowUpdate = State == StreamState.PRE_HYDRATION || State == StreamState.RANGE_BUILDING;
        if (!allowUpdate && SlotTimeChicago != newSlotTimeChicago)
        {
            var warningMsg = $"WARNING: Attempted to update slot_time from '{SlotTimeChicago}' to '{newSlotTimeChicago}' " +
                           $"but stream is already in state '{State}'. Slot_time changes are only allowed during PRE_HYDRATION or RANGE_BUILDING. " +
                           $"Ignoring update to prevent mid-session slot_time changes.";
            
            LogHealth("WARN", "SLOT_TIME_UPDATE_REJECTED", warningMsg, new
            {
                stream = Stream,
                current_state = State.ToString(),
                current_slot_time = SlotTimeChicago,
                attempted_slot_time = newSlotTimeChicago,
                trading_date = tradingDate.ToString("yyyy-MM-dd"),
                note = "Slot_time update rejected - stream already initialized. This prevents timetable updates from affecting active streams."
            });
            
            // Report to HealthMonitor for visibility
            if (_reportCriticalCallback != null)
            {
                _reportCriticalCallback("SLOT_TIME_UPDATE_REJECTED", new Dictionary<string, object>
                {
                    { "stream", Stream },
                    { "current_slot_time", SlotTimeChicago },
                    { "attempted_slot_time", newSlotTimeChicago },
                    { "state", State.ToString() }
                }, TradingDate);
            }
            
            return false; // Reject the update
        }
        
        // Allowed only if not committed
        SlotTimeChicago = newSlotTimeChicago;
        
        // PHASE 1: Construct Chicago time directly (authoritative)
        SlotTimeChicagoTime = _time.ConstructChicagoTime(tradingDate, newSlotTimeChicago);
        
        // PHASE 2: Derive UTC from Chicago time (derived representation)
        SlotTimeUtc = _time.ConvertChicagoToUtc(SlotTimeChicagoTime);
        
        _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc, "UPDATE_APPLIED", State.ToString(),
            new { slot_time_chicago = SlotTimeChicago, slot_time_chicago_time = SlotTimeChicagoTime.ToString("o"), slot_time_utc = SlotTimeUtc.ToString("o") }));
        return true;
    }

    public void UpdateTradingDate(DateOnly newTradingDate, DateTimeOffset utcNow, bool allowCarryForwardActive = false)
    {
        // Update trading_date and recompute all UTC times based on new date
        // NOTE: This method should only be called during initialization or historical replay.
        // In live trading, trading date is locked from first bar and never changes.
        var newTradingDateStr = newTradingDate.ToString("yyyy-MM-dd");
        var previousTradingDateStr = _journal.TradingDate;

        // Only update if trading_date actually changed
        if (previousTradingDateStr == newTradingDateStr)
            return;

        // GUARD: If trading date is already set and differs, this is a mid-session change attempt
        // This should not happen in live trading - log warning and prevent change
        if (!string.IsNullOrWhiteSpace(previousTradingDateStr))
        {
            if (TimeService.TryParseDateOnly(previousTradingDateStr, out var prevDate) && 
                prevDate != newTradingDate)
            {
                // Check if this is initialization (empty journal) vs mid-session change
                // If state is beyond PRE_HYDRATION, this is a mid-session change attempt
                if (State != StreamState.PRE_HYDRATION && !allowCarryForwardActive)
                {
                    LogHealth("WARN", "TRADING_DATE_CHANGE_BLOCKED", $"Trading date change blocked - date is immutable after initialization",
                        new
                        {
                            previous_trading_date = previousTradingDateStr,
                            attempted_new_trading_date = newTradingDateStr,
                            current_state = State.ToString(),
                            note = "Trading date is locked and cannot be changed mid-session"
                        });
                    return; // Block mid-session trading date changes
                }
                else if (State != StreamState.PRE_HYDRATION && allowCarryForwardActive)
                {
                    LogHealth("INFO", "TIMETABLE_TRADING_DATE_CARRY_FORWARD_ALLOWED", "Timetable rollover allowed active stream date carry-forward",
                        new
                        {
                            previous_trading_date = previousTradingDateStr,
                            attempted_new_trading_date = newTradingDateStr,
                            current_state = State.ToString(),
                            note = "Allowed only for timetable rollover retention of a nonterminal stream lifecycle."
                        });
                }
            }
        }
        
        // GUARD: If previous trading date is empty/null, this is initialization, not a rollover
        // In this case, just update the journal and times without resetting state
        var isInitialization = string.IsNullOrWhiteSpace(previousTradingDateStr);
        
        // GUARD: If new date is before previous date, this is historical/replay data
        // Don't reset state for backward date progression (replay mode)
        var isBackwardDate = false;
        if (!isInitialization && !string.IsNullOrWhiteSpace(previousTradingDateStr))
        {
            if (TimeService.TryParseDateOnly(previousTradingDateStr, out var prevDate) && 
                newTradingDate < prevDate)
            {
                isBackwardDate = true;
            }
        }

        // Load or create journal for the new trading_date
        var existingJournal = _ignoreExistingStreamJournals ? null : _journals.TryLoad(newTradingDateStr, Stream);
        if (existingJournal != null && existingJournal.Committed)
        {
            // Journal already exists and is committed - use it and mark as DONE
            _journal = existingJournal;
            State = StreamState.DONE;
            TryEnsureCarryForwardTerminalMirror(
                utcNow,
                _journal.CommitReason ?? "JOURNAL_ALREADY_COMMITTED",
                "TRADE_COMPLETED",
                _journal.SlotStatus);
            
            // Only log rollover if it's forward progression (not backward/replay)
            if (!isBackwardDate)
            {
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "TRADING_DAY_ROLLOVER", State.ToString(),
                    new
                    {
                        previous_trading_date = previousTradingDateStr,
                        new_trading_date = newTradingDateStr,
                        note = "JOURNAL_ALREADY_COMMITTED_FOR_NEW_DATE"
                    }));
            }
            else
            {
                // Backward date with committed journal - silent update
                LogHealth("INFO", "TRADING_DATE_BACKWARD", $"Trading date moved backward (committed journal): {previousTradingDateStr} -> {newTradingDateStr}",
                    new { previous_trading_date = previousTradingDateStr, new_trading_date = newTradingDateStr, note = "Committed journal, state preserved" });
            }
            return;
        }

        // Recompute time boundaries for new trading date
        RecomputeTimeBoundaries(newTradingDate);

        // SLOT PERSISTENCE: Check if this is a post-entry active slot that should be carried forward
        var previousJournal = _ignoreExistingStreamJournals ? null : _journals.TryLoad(previousTradingDateStr, Stream);
        var isPostEntryActive = false;
        
        if (!isInitialization && !isBackwardDate && previousJournal != null)
        {
            // Check post-entry active condition:
            // 1. SlotStatus == ACTIVE
            // 2. ExecutionInterruptedByClose == true OR post-entry verified via ExecutionJournal
            // 3. now < NextSlotTimeUtc (slot not expired)
            var slotNotExpired = !previousJournal.NextSlotTimeUtc.HasValue || utcNow < previousJournal.NextSlotTimeUtc.Value;
            var isPostEntry = false;
            
            if (previousJournal.ExecutionInterruptedByClose)
            {
                isPostEntry = true; // Already marked as interrupted (forced flattened)
            }
            else if (!string.IsNullOrWhiteSpace(previousJournal.OriginalIntentId) && _executionJournal != null)
            {
                // Verify post-entry via ExecutionJournal: load ExecutionJournalEntry via OriginalIntentId, check EntryFilled==true
                isPostEntry = HasEntryFillForIntentId(previousJournal.OriginalIntentId, previousTradingDateStr);
            }
            
            isPostEntryActive = previousJournal.SlotStatus == SlotStatus.ACTIVE && 
                               isPostEntry && 
                               slotNotExpired;
        }

        // Replace journal with one for the new trading_date
        if (existingJournal != null)
        {
            // Use existing journal for new date
            _journal = existingJournal;
        }
        else if (isPostEntryActive && previousJournal != null)
        {
            // SLOT PERSISTENCE: Clone-forward post-entry active slot (carry-forward mechanism)
            // Create new journal entry for new TradingDate but preserve SlotInstanceKey and lifecycle fields
            _journal = new StreamJournal
            {
                TradingDate = newTradingDateStr, // Update for reporting
                Stream = Stream,
                Committed = false,
                CommitReason = null,
                LastState = State.ToString(),
                LastUpdateUtc = utcNow.ToString("o"),
                TimetableHashAtCommit = null,
                StopBracketsSubmittedAtLock = previousJournal.StopBracketsSubmittedAtLock,
                EntryDetected = previousJournal.EntryDetected,
                // Preserve slot lifecycle identity and state
                SlotInstanceKey = previousJournal.SlotInstanceKey, // NEVER overwrite - preserve lifecycle identity
                SlotStatus = previousJournal.SlotStatus,
                ExecutionInterruptedByClose = previousJournal.ExecutionInterruptedByClose,
                ForcedFlattenTimestamp = previousJournal.ForcedFlattenTimestamp,
                OriginalIntentId = previousJournal.OriginalIntentId,
                ReentryIntentId = previousJournal.ReentryIntentId,
                ReentrySubmitPending = previousJournal.ReentrySubmitPending,
                ReentrySubmitPendingAtUtc = previousJournal.ReentrySubmitPendingAtUtc,
                ReentrySubmitLastFailureUtc = previousJournal.ReentrySubmitLastFailureUtc,
                ReentrySubmitFailureCount = previousJournal.ReentrySubmitFailureCount,
                LastReentrySubmitError = previousJournal.LastReentrySubmitError,
                ReentrySubmitted = previousJournal.ReentrySubmitted,
                ReentryFilled = previousJournal.ReentryFilled,
                ProtectionSubmitted = previousJournal.ProtectionSubmitted,
                ProtectionAccepted = previousJournal.ProtectionAccepted,
                NextSlotTimeUtc = previousJournal.NextSlotTimeUtc,
                PriorJournalKey = $"{previousTradingDateStr}_{Stream}", // Reference to previous day's journal
                RecoveryAction = previousJournal.RecoveryAction,
                RecoveryActionReason = previousJournal.RecoveryActionReason,
                RecoveryActionIssuedUtc = previousJournal.RecoveryActionIssuedUtc
            };
        }
        else
        {
            // Create new journal entry for new trading_date (new slot lifecycle)
            _journal = new StreamJournal
            {
                TradingDate = newTradingDateStr,
                Stream = Stream,
                Committed = false,
                CommitReason = null,
                LastState = State.ToString(),
                LastUpdateUtc = utcNow.ToString("o"),
                TimetableHashAtCommit = null,
                StopBracketsSubmittedAtLock = false,
                EntryDetected = false,
                SlotStatus = SlotStatus.ACTIVE,
                SlotInstanceKey = null, // Will be set below if new journal
                ExecutionInterruptedByClose = false,
                ReentrySubmitted = false,
                ReentryFilled = false,
                ProtectionSubmitted = false,
                ProtectionAccepted = false
            };
            
            // Generate new SlotInstanceKey for new slot lifecycle
            if (string.IsNullOrWhiteSpace(_journal.SlotInstanceKey))
            {
                _journal.SlotInstanceKey = $"{Stream}_{SlotTimeChicago}_{newTradingDateStr}";
                _journal.NextSlotTimeUtc = CalculateNextSlotTimeUtc(newTradingDate, SlotTimeChicago, _time);
            }
        }
        _journals.Save(_journal);

        // Only reset state and clear buffers if this is an actual forward rollover AND NOT post-entry active
        if (!isInitialization && !isBackwardDate && !isPostEntryActive)
        {
            // Reset all daily state for new trading day
            ResetDailyState();
            
            // A) Strategy lifecycle: New trading day detected
            LogHealth("INFO", "TRADING_DAY_ROLLOVER", $"New trading day detected: {newTradingDateStr}",
                new { previous_trading_date = previousTradingDateStr, new_trading_date = newTradingDateStr });
            
            // Clear bar buffer on reset
            lock (_barBufferLock)
            {
                var previousCount = _barBuffer.Count;
                _barBuffer.Clear();
                _barSourceMap.Clear();
                
                // Log buffer clear
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "BAR_BUFFER_CLEARED", State.ToString(),
                    new
                    {
                        stream_id = Stream,
                        canonical_instrument = CanonicalInstrument,
                        instrument = Instrument,
                        clear_reason = "TRADING_DAY_ROLLOVER",
                        previous_buffer_count = previousCount,
                        range_start_chicago = RangeStartChicagoTime.ToString("o"),
                        slot_time_chicago = SlotTimeChicagoTime.ToString("o")
                    }));
            }
            _brkLongRaw = null;
            _brkShortRaw = null;
            _brkLongRounded = null;
            _brkShortRounded = null;

            // Reset state appropriately for new trading day
            // Since we cleared the bar buffer and reset _preHydrationComplete, we must reset to PRE_HYDRATION
            // to allow pre-hydration to re-run for the new trading day
            if (!_journal.Committed)
            {
                // Reset to PRE_HYDRATION so pre-hydration can re-run for new trading day
                State = StreamState.PRE_HYDRATION;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "TRADING_DAY_ROLLOVER", State.ToString(),
                    new
                    {
                        previous_trading_date = previousTradingDateStr,
                        new_trading_date = newTradingDateStr,
                        state_reset_to = "PRE_HYDRATION",
                        reason = "Bar buffer cleared and pre-hydration reset for new trading day"
                    }));
            }
            else
            {
                // Journal is committed - mark as DONE
                State = StreamState.DONE;
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "TRADING_DAY_ROLLOVER", State.ToString(),
                    new
                    {
                        previous_trading_date = previousTradingDateStr,
                        new_trading_date = newTradingDateStr,
                        state_reset_to = "DONE",
                        reason = "Journal already committed for new trading day"
                    }));
            }
        }
        else if (isInitialization)
        {
            // Initialization: Just update journal and times, don't reset state or clear buffers
            // This prevents rollover spam when streams are first created
            LogHealth("INFO", "TRADING_DATE_INITIALIZED", $"Trading date initialized: {newTradingDateStr}",
                new { new_trading_date = newTradingDateStr, note = "Initial setup, not a rollover" });
        }
        else if (isBackwardDate)
        {
            // Backward date (replay/historical): Update journal and times, but don't reset state
            // This prevents state resets when processing historical bars
            LogHealth("INFO", "TRADING_DATE_BACKWARD", $"Trading date moved backward (replay mode): {previousTradingDateStr} -> {newTradingDateStr}",
                new { previous_trading_date = previousTradingDateStr, new_trading_date = newTradingDateStr, note = "Historical data, state preserved" });
        }
    }

    public void Tick(DateTimeOffset utcNow)
    {
        // DIAGNOSTIC: Unconditional log to verify Tick() is being called at all

        if (_journal.Committed)
        {
            TryEnsureCarryForwardTerminalMirror(
                utcNow,
                _journal.CommitReason ?? "JOURNAL_ALREADY_COMMITTED",
                "TRADE_COMPLETED",
                _journal.SlotStatus);
            // Hard no re-arming
            State = StreamState.DONE;
            return;
        }
        
        // SLOT PERSISTENCE: Check slot expiry BEFORE other state transitions (authoritative termination)
        if (_journal.SlotStatus == SlotStatus.ACTIVE && _journal.NextSlotTimeUtc.HasValue && utcNow >= _journal.NextSlotTimeUtc.Value)
        {
            // Slot expired at next slot time - exit position and commit lifecycle terminal
            HandleSlotExpiry(utcNow);
            return;
        }

        TryProcessDeferredBracketTradeAuthority(utcNow);
        
        // SLOT PERSISTENCE: Forced-flatten reentry owns the slot until it either
        // re-enters safely, fails terminally, or expires at the next slot.
        if (_journal.SlotStatus == SlotStatus.ACTIVE && _journal.ExecutionInterruptedByClose)
        {
            if (!_journal.ReentrySubmitted)
            {
                CheckMarketOpenReentry(utcNow);
            }

            if (_journal.Committed || State == StreamState.DONE)
                return;

            if (_journal.ExecutionInterruptedByClose)
                return;
        }


        // OPTIONAL SAFETY ASSERTION: Detect stuck RANGE_BUILDING states
        // If state == RANGE_BUILDING and now > slot_time + X minutes → log critical
        // This guardrail would have caught the original bug automatically
        if (State == StreamState.RANGE_BUILDING && SlotTimeUtc != DateTimeOffset.MinValue)
        {
            var minutesPastSlotTime = (utcNow - SlotTimeUtc).TotalMinutes;
            const double STUCK_RANGE_BUILDING_THRESHOLD_MINUTES = 10.0; // Alert if stuck > 10 minutes past slot time
            
            // DIAGNOSTIC: Log safety assertion check (verifies assertion is running)
            // Rate-limited to once per 15 minutes to confirm assertion is active without spam
            var shouldLogAssertionCheck = !_lastStuckRangeBuildingAlertUtc.HasValue ||
                                         (utcNow - _lastStuckRangeBuildingAlertUtc.Value).TotalMinutes >= 15.0;
            
            if (shouldLogAssertionCheck && minutesPastSlotTime <= STUCK_RANGE_BUILDING_THRESHOLD_MINUTES)
            {
                // Log that assertion is checking but threshold not exceeded (diagnostic confirmation)
                var nowChicago = _time.ConvertUtcToChicago(utcNow);
                var slotTimeChicago = _time.ConvertUtcToChicago(SlotTimeUtc);
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "RANGE_BUILDING_SAFETY_ASSERTION_CHECK", State.ToString(),
                    new
                    {
                        minutes_past_slot_time = Math.Round(minutesPastSlotTime, 1),
                        threshold_minutes = STUCK_RANGE_BUILDING_THRESHOLD_MINUTES,
                        slot_time_chicago = slotTimeChicago.ToString("o"),
                        current_time_chicago = nowChicago.ToString("o"),
                        status = "OK",
                        note = $"DIAGNOSTIC: Safety assertion is checking. Stream is {Math.Round(minutesPastSlotTime, 1)} minutes past slot time (threshold: {STUCK_RANGE_BUILDING_THRESHOLD_MINUTES} min). Assertion is active and monitoring."
                    }));
            }
            
            if (minutesPastSlotTime > STUCK_RANGE_BUILDING_THRESHOLD_MINUTES)
            {
                // Rate-limit this critical alert to once per 5 minutes
                var shouldLogStuck = !_lastStuckRangeBuildingAlertUtc.HasValue ||
                                    (utcNow - _lastStuckRangeBuildingAlertUtc.Value).TotalMinutes >= 5.0;
                
                if (shouldLogStuck)
                {
                    _lastStuckRangeBuildingAlertUtc = utcNow;
                    var nowChicago = _time.ConvertUtcToChicago(utcNow);
                    var slotTimeChicago = _time.ConvertUtcToChicago(SlotTimeUtc);
                    
                    LogHealth("CRITICAL", "RANGE_BUILDING_STUCK_PAST_SLOT_TIME", 
                        $"Stream stuck in RANGE_BUILDING state for {minutesPastSlotTime:F1} minutes past slot time. " +
                        $"Slot time: {slotTimeChicago:HH:mm:ss} CT, Current: {nowChicago:HH:mm:ss} CT. " +
                        $"This may indicate Tick() is not running or range lock check is failing.",
                        new
                        {
                            minutes_past_slot_time = Math.Round(minutesPastSlotTime, 1),
                            slot_time_chicago = slotTimeChicago.ToString("o"),
                            current_time_chicago = nowChicago.ToString("o"),
                            slot_time_utc = SlotTimeUtc.ToString("o"),
                            current_time_utc = utcNow.ToString("o"),
                            threshold_minutes = STUCK_RANGE_BUILDING_THRESHOLD_MINUTES,
                            note = "Safety assertion: Would have caught original bug where Tick() stopped running"
                        });
                }
            }
        }

        switch (State)
        {
            case StreamState.PRE_HYDRATION:
                HandlePreHydrationState(utcNow);
                break;
                
            case StreamState.ARMED:
                HandleArmedState(utcNow);
                break;

            case StreamState.RANGE_BUILDING:
                HandleRangeBuildingState(utcNow);
                break;

            case StreamState.RANGE_LOCKED:
                HandleRangeLockedState(utcNow);
                break;

            case StreamState.DONE:
                // terminal
                break;
                
            case StreamState.SUSPENDED_DATA_INSUFFICIENT:
                // Stream suspended due to insufficient data - log periodic heartbeat
                // Do not process ticks - stream requires manual intervention
                var lastHeartbeatUtc = _lastHeartbeatUtc ?? DateTimeOffset.MinValue;
                if ((utcNow - lastHeartbeatUtc).TotalMinutes >= 5.0)
                {
                    _lastHeartbeatUtc = utcNow;
                    LogHealth("INFO", "SUSPENDED_STREAM_HEARTBEAT",
                        "Stream suspended due to insufficient data - manual intervention required",
                        new
                        {
                            bar_count = GetBarBufferCount(),
                            range_start_chicago = RangeStartChicagoTime.ToString("o"),
                            slot_time_chicago = SlotTimeChicagoTime.ToString("o"),
                            note = "Stream is suspended and will not process ticks until manually resumed"
                        });
                }
                break;
        }
    }

    /// <summary>
    /// Check if breakout was missed due to late start (only if starting after slot_time).
    /// Returns true if breakout occurred between slot_time and now, with breakout details.
    /// 
    /// CRITICAL BOUNDARY SEMANTICS:
    /// - Range build window: [range_start, slot_time) - slot_time is EXCLUSIVE
    /// - Missed-breakout scan window: [slot_time, now] - slot_time is INCLUSIVE
    /// - Breakout detection uses STRICT inequalities: bar.High > rangeHigh OR bar.Low < rangeLow
    /// - Bars are sorted chronologically before scanning (earliest breakout wins)
    /// 
    /// EDGE CASES HANDLED:
    /// - Breakout at slot_time + 1s: Detected (barChicagoTime >= slotChicago)
    /// - Touch at exactly slot_time: Ignored (range window is exclusive, so not in range)
    /// - Price equals range high/low: NOT a breakout (strict inequality required)
    /// </summary>
    private (bool MissedBreakout, DateTimeOffset? BreakoutTimeUtc, DateTimeOffset? BreakoutTimeChicago, decimal? BreakoutPrice, string? BreakoutDirection) CheckMissedBreakout(DateTimeOffset utcNow, decimal rangeHigh, decimal rangeLow)
    {
        var nowChicago = _time.ConvertUtcToChicago(utcNow);
        var slotChicago = SlotTimeChicagoTime;
        
        // Only check if starting after slot_time
        if (nowChicago <= slotChicago)
        {
            return (false, null, null, null, null);
        }
        
        // Scan bars from slot_time to now for missed breakout
        // CRITICAL: Get snapshot and explicitly sort by timestamp to guarantee chronological order
        // This ensures earliest breakout wins (prevents misclassifying direction)
        var barsSnapshot = GetBarBufferSnapshot();
        barsSnapshot.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        
        foreach (var bar in barsSnapshot)
        {
            var barChicagoTime = _time.ConvertUtcToChicago(bar.TimestampUtc);
            
            // Missed-breakout scan window: [slot_time, now] (inclusive on both ends)
            // Only consider bars in this window
            if (barChicagoTime < slotChicago)
                continue; // Skip bars before slot_time (these are in range build window)
            
            if (barChicagoTime > nowChicago)
                break; // Past current time, stop checking (no lookahead)
            
            // DIAGNOSTIC: Log boundary validation for edge case testing
            // This helps verify: breakout at slot_time+1s is detected, touch at slot_time is ignored
            if (_enableDiagnosticLogs)
            {
                var secondsFromSlot = (barChicagoTime - slotChicago).TotalSeconds;
                var isAtSlotBoundary = Math.Abs(secondsFromSlot) < 1.0; // Within 1 second of slot_time
                var isAfterSlot = barChicagoTime >= slotChicago;
                
                _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                    "MISSED_BREAKOUT_SCAN_BAR", State.ToString(),
                    new
                    {
                        bar_time_chicago = barChicagoTime.ToString("o"),
                        slot_time_chicago = slotChicago.ToString("o"),
                        seconds_from_slot = Math.Round(secondsFromSlot, 3),
                        is_at_slot_boundary = isAtSlotBoundary,
                        is_after_slot = isAfterSlot,
                        bar_high = bar.High,
                        bar_low = bar.Low,
                        range_high = rangeHigh,
                        range_low = rangeLow,
                        high_exceeds_range = bar.High > rangeHigh,
                        low_exceeds_range = bar.Low < rangeLow,
                        note = "Diagnostic: Bar in missed-breakout scan window. Validates boundary semantics and strict inequality checks."
                    }));
            }
            
            // CRITICAL: Use STRICT inequalities for breakout detection
            // bar.High > rangeHigh (not >=) - price must exceed range high
            // bar.Low < rangeLow (not <=) - price must exceed range low
            // Price equals range boundary is NOT a breakout
            if (bar.High > rangeHigh)
            {
                // LONG breakout: price exceeded range high
                return (true, bar.TimestampUtc, barChicagoTime, bar.High, "LONG");
            }
            else if (bar.Low < rangeLow)
            {
                // SHORT breakout: price exceeded range low
                return (true, bar.TimestampUtc, barChicagoTime, bar.Low, "SHORT");
            }
        }
        
        return (false, null, null, null, null);
    }

    /// <summary>
    /// Log critical error loudly: emits structured ERROR event to main JSONL log with exception details.
    /// Never throws - designed for use in catch blocks where we want to fail loudly but not crash.
    /// </summary>
    private void LogCriticalError(string eventType, Exception ex, DateTimeOffset utcNow, object? context = null)
    {
        try
        {
            if (_log == null) return; // Can't log if logger is null
            
            var exceptionType = ex.GetType().Name;
            var errorMessage = ex.Message ?? "Unknown error";
            var stackTrace = ex.StackTrace;
            
            // Truncate stack trace to safe size (max 1000 chars)
            var truncatedStackTrace = stackTrace != null && stackTrace.Length > 1000 
                ? stackTrace.Substring(0, 1000) + "... [truncated]" 
                : stackTrace;
            
            // Merge context with exception details
            var payload = new Dictionary<string, object?>
            {
                ["exception_type"] = exceptionType,
                ["error"] = errorMessage,
                ["stack_trace"] = truncatedStackTrace
            };
            
            // Add context fields if provided
            if (context != null)
            {
                if (context is Dictionary<string, object?> contextDict)
                {
                    foreach (var kvp in contextDict)
                        payload[kvp.Key] = kvp.Value;
                }
                else
                {
                    payload["context"] = context;
                }
            }
            
            // Emit ERROR event to main log
            _log.Write(RobotEvents.Base(
                _time ?? new TimeService("America/Chicago"), 
                utcNow, 
                TradingDate ?? "", 
                Stream ?? "", 
                Instrument ?? "", 
                Session ?? "", 
                SlotTimeChicago ?? "", 
                SlotTimeUtc,
                eventType, 
                State.ToString(),
                payload
            ));
        }
        catch
        {
            // Last resort: if even error logging fails, silently fail to prevent infinite loops
            // This should be extremely rare (only if JSON serialization or file write completely broken)
        }
    }
    

    /// <summary>
    /// Log health/anomaly event through RobotLogger (routes to health sink via RobotLoggingService).
    /// Health events are automatically routed to health sink if enable_health_sink is true.
    /// </summary>
    private void LogHealth(string level, string eventType, string message, object? data = null)
    {
        try
        {
            var utcNow = DateTimeOffset.UtcNow;
            
            // Build data dictionary with additional context
            var eventData = new Dictionary<string, object?>
            {
                ["message"] = message,
                ["state"] = State.ToString(),
                ["execution_mode"] = _executionMode.ToString()
            };
            
            // Include slot_instance_key if available for health sink path granularity
            if (_journal != null && !string.IsNullOrWhiteSpace(_journal.SlotInstanceKey))
            {
                eventData["slot_instance_key"] = _journal.SlotInstanceKey;
            }
            
            // Merge additional data if provided
            if (data != null)
            {
                if (data is Dictionary<string, object?> dataDict)
                {
                    foreach (var kvp in dataDict)
                    {
                        eventData[kvp.Key] = kvp.Value;
                    }
                }
                else
                {
                    eventData["payload"] = data;
                }
            }
            
            // Route through RobotLogger - will automatically go to health sink if level >= WARN or selected INFO
            _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
                eventType, message, eventData));
        }
        catch (Exception ex)
        {
            // Fail loudly: log health logging failure as ERROR event
            var utcNow = DateTimeOffset.UtcNow;
            LogCriticalError("LOG_HEALTH_ERROR", ex, utcNow, new
            {
                original_event_type = eventType,
                original_level = level,
                original_message = message,
                note = "Health logging failed - attempting to log to main log instead"
            });
        }
    }
    
    /// <summary>
    /// Log slot end summary explaining why nothing happened (or what happened).
    /// </summary>
    private void LogSlotEndSummary(DateTimeOffset utcNow, string rangeSource, bool rangeLocked, bool tradeExecuted, string reason)
    {
        if (_slotEndSummaryLogged) return; // Ensure exactly one summary per slot
        _slotEndSummaryLogged = true;
        
        var currentTimeChicago = _time.ConvertUtcToChicago(utcNow);
        int liveBarCount = GetBarBufferCount();
        
        // G) "Nothing happened" explanations: Slot end summary
        LogHealth("INFO", "SLOT_END_SUMMARY", $"Slot {SlotTimeChicago} summary — RangeStatus={rangeSource}, RangeLocked={rangeLocked}, TradeExecuted={tradeExecuted}, Reason={reason}",
            new
            {
                slot_time_chicago = SlotTimeChicago,
                range_status = rangeSource,
                range_locked = rangeLocked,
                trade_executed = tradeExecuted,
                reason = reason,
                range_high = RangeHigh,
                range_low = RangeLow,
                live_bar_count = liveBarCount,
                range_invalidated = _rangeInvalidated,
                largest_single_gap_minutes = _largestSingleGapMinutes,
                total_gap_minutes = _totalGapMinutes,
                execution_mode = _executionMode.ToString(),
                entry_detected = _entryDetected
            });
    }

}
